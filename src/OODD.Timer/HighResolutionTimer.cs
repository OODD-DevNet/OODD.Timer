namespace OODD.Timer;

/// <summary>
/// 高精度定时器：用单调时钟 + 自旋/内核混合等待替代系统时钟节拍，把触发误差从 10~15ms 压到微秒甚至亚微秒量级。
/// </summary>
/// <remarks>
/// <para>
/// <b>和 <see cref="System.Threading.Timer"/> 的根本区别：</b>后者把到期时间交给操作系统定时器队列，
/// 触发时刻只能落在系统时钟节拍的整数倍上（Windows 默认 15.6ms；本机实测 1ms 周期的
/// <c>System.Threading.Timer</c> 每拍迟到 p50 = 14.55ms）。就算把系统节拍请求到 1ms
/// （<see cref="RuntimeTuning.RequestSystemTimerResolution"/>，实测 p50 降到 0.76ms），
/// 误差仍是毫秒量级，而且它的计时是自我节律的——每一拍的超时会累加到下一拍上。
/// 本类不依赖那个节拍：它自己持有一个线程，用 <see cref="HiResClock"/>（QPC / clock_gettime，粒度约 100ns）
/// 逼近一个绝对到期点，最后一段用自旋收尾，因此触发点落在时钟粒度以内。
/// </para>
/// <para>
/// <b>无漂移调度：</b>第 N 拍的到期点始终是"起点 + N × 周期"（整数 tick 运算），
/// 而不是"上一次触发时刻 + 周期"。所以单次迟到不会累加成长期偏移。
/// 若一次回调超时导致连续几拍错过，本库只触发一次，并把错过的拍计入
/// <see cref="TimerElapsedEventArgs.MissedTicks"/>，绝不"追赶式"连续补触发（那会雪崩）。
/// </para>
/// <para>
/// <b>线程模型：</b>默认每个定时器独占一个调度线程（<see cref="HighResolutionTimerOptions.Scheduler"/> 为 null）。
/// 几十个定时器请挂到同一个 <see cref="HighResolutionScheduler"/> 共享自旋开销。
/// 回调就在调度线程上执行——不要在回调里做阻塞 IO，也不要等待同一个调度器上的另一个定时器。
/// 也可以用拉模式（<see cref="WaitForTrigger()"/> / <see cref="WaitForTriggerAsync"/>）在自己的线程上等下一拍，
/// 调度线程只负责记账；拉模式的投递延迟比回调多一层线程唤醒，取舍见 README「拉模式」。
/// </para>
/// <para>
/// <b>怎么选 <see cref="WaitMode"/>：</b>
/// 周期 &lt; 1ms 用 <see cref="WaitMode.Spin"/>；1ms~100ms 用 <see cref="WaitMode.Hybrid"/>；
/// 秒级以上或移动端后台用 <see cref="WaitMode.Kernel"/>。
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // 每 200µs 一拍，亚微秒级触发
/// using var timer = new HighResolutionTimer(TimeSpan.FromMicroseconds(200), WaitMode.Spin);
/// timer.Elapsed += static (sender, e) =>
/// {
///     if (e.MissedTicks != 0)
///     {
///         // 这一拍被让出去了，记录告警
///     }
///
///     Sample();
/// };
/// </code>
/// </example>
public sealed class HighResolutionTimer : IDisposable
{
    private static int s_timerCounter;

    /// <summary>拉模式单次等待的上限：同时决定"停表后多久解除阻塞"。</summary>
    private const int PullEventWaitMilliseconds = 50;

    /// <summary>
    /// 拉模式过了到期点后的自旋余量：调度线程本来就该在到期这一刻触发，多转一小会儿就能直接看到序列号变化，
    /// 省掉一次"事件唤醒"（本机实测那一次唤醒 p50 约几十 µs，尾部可到几百 µs）。
    /// </summary>
    private static readonly long PullPostDueSpinTicks = HiResClock.ToTicksFromMicroseconds(200);

    private readonly object _gate = new();
    private readonly HighResolutionScheduler _scheduler;
    private readonly bool _ownsScheduler;
    private readonly LatencyCollector _collector = new();
    private readonly ManualResetEventSlim _tickEvent = new(false);
    private readonly List<TaskCompletionSource<bool>> _waiters = new(2);
    private readonly HighResolutionTimerOptions _options;
    private readonly AdaptiveSpinWindow? _window;
#if NET40
    // net40 的 GC 没有 SustainedLowLatency（见 RuntimeTuning.RequestGcLatencyMode），不会持有作用域对象
#else
    private readonly IDisposable? _gcScope;
#endif
    private int _state;                                  // 对应 TimerState
    private long _lastDueTimestamp;
    private double _lastLatenessNanoseconds;
    private bool _autoResetFlag;
    private long _pulledSequence;                        // 拉模式最近一次交付给消费者的序列号
    private long _pulledDueTimestamp;                    // 已交付那一拍的应到时刻（消费者自己的光标）
    private long _pullSpinWindowTicks;                   // 拉模式实测到的内核唤醒迟到（只增不减，含 25% 余量）
    private long _pullWindowFloorTicks;                  // 调用方给的自旋窗口下限

    /// <summary>用默认配置（1ms 周期、<see cref="WaitMode.Hybrid"/>、自动启动）创建定时器。</summary>
    public HighResolutionTimer() : this(new HighResolutionTimerOptions())
    {
    }

    /// <summary>
    /// 指定周期创建定时器，自动启动，独占一个调度线程。
    /// </summary>
    /// <param name="period">周期，必须严格大于 <see cref="TimeSpan.Zero"/>。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="period"/> 不为正。</exception>
    public HighResolutionTimer(TimeSpan period)
        : this(new HighResolutionTimerOptions { Period = period })
    {
    }

    /// <summary>
    /// 指定周期与等待策略创建定时器。
    /// </summary>
    /// <param name="period">周期。</param>
    /// <param name="mode">等待策略；<see cref="WaitMode.Spin"/> 精度最高但会占满一个逻辑核。</param>
    public HighResolutionTimer(TimeSpan period, WaitMode mode)
        : this(new HighResolutionTimerOptions { Period = period, Mode = mode })
    {
    }

    /// <summary>
    /// 指定周期并挂到已有调度器上（多个定时器共享一个线程）。
    /// </summary>
    /// <param name="period">周期。</param>
    /// <param name="scheduler">共享调度器，例如 <see cref="HighResolutionScheduler.Shared"/>。</param>
    /// <exception cref="ArgumentNullException"><paramref name="scheduler"/> 为 null。</exception>
    public HighResolutionTimer(TimeSpan period, HighResolutionScheduler scheduler)
        : this(new HighResolutionTimerOptions
        {
            Period = period,
            Scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler)),
        })
    {
    }

    /// <summary>
    /// 用完整配置创建定时器。
    /// </summary>
    /// <param name="options">配置；内部会克隆一份，之后修改传入对象不会影响本定时器。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">配置非法。</exception>
    public HighResolutionTimer(HighResolutionTimerOptions options)
    {
        Guard.NotNull(options, nameof(options));
        options.Validate();
        _options = options.Clone();
        Name = _options.Name ?? $"OODD.Timer.{Interlocked.Increment(ref s_timerCounter):D4}";
        PeriodTicks = Math.Max(1, HiResClock.ToTicks(_options.Period));
        _autoResetFlag = _options.AutoReset;

        if (_options.Mode == WaitMode.Hybrid && _options.AdaptiveSpinWindow)
        {
            _window = new AdaptiveSpinWindow(
                _options.SpinWindowNanoseconds,
                HighResolutionWait.MinSpinWindowNanoseconds,
                _options.MaxSpinWindowNanoseconds);
        }

        Settings = BuildSettings();

        if (_options.Scheduler is { } shared)
        {
            _scheduler = shared;
            _ownsScheduler = false;
        }
        else
        {
            _scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions
            {
                Name = Name,
                Mode = _options.Mode,
                ThreadPriority = _options.ThreadPriority,
            });
            _ownsScheduler = true;
        }

        if (_options.PreferSustainedLowLatencyGc)
        {
#if NET40
            throw new PlatformNotSupportedException(
                "net40 不支持 PreferSustainedLowLatencyGc：GC 没有 SustainedLowLatency 模式。");
#else
            _gcScope = RuntimeTuning.RequestGcLatencyMode();
#endif
        }

        if (_options.AutoStart)
        {
            Start();
        }
    }

    /// <summary>定时器名字（独占调度线程时同时作为线程名）。</summary>
    public string Name { get; }

    /// <summary>当前状态。</summary>
    public TimerState State => (TimerState)Volatile.Read(ref _state);

    /// <summary>是否正在调度中（等价于 <see cref="State"/> 为 <see cref="TimerState.Running"/>）。</summary>
    public bool IsRunning => Volatile.Read(ref _state) == (int)TimerState.Running;

    /// <summary>驱动本定时器的调度器（独占或共享）。</summary>
    public HighResolutionScheduler Scheduler => _scheduler;

    /// <summary>
    /// 当前周期。
    /// </summary>
    /// <remarks>
    /// 赋值会<strong>重新锚定相位</strong>：运行中赋值等价于
    /// <c>Change(新周期, 新周期)</c>，从此刻起重新起算，不保留旧相位的剩余时间。
    /// 在微秒级节拍上，这种"改了就从下一拍干净地按新周期走"的行为比"接着旧相位"更可预期。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">赋值不为正。</exception>
    public TimeSpan Period
    {
        get => _options.Period;
        set
        {
            Guard.NotLessThanOrEqual(value, TimeSpan.Zero, nameof(value));
            _ = Change(value, value);
        }
    }

    /// <summary>
    /// 首次触发前的延迟；null 表示等于 <see cref="Period"/>。仅影响下一次 <see cref="Start"/>。
    /// </summary>
    public TimeSpan? DueTime
    {
        get => _options.DueTime;
        set
        {
            if (value is { } v && v < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "首次延迟不能为负。");
            }

            lock (_gate)
            {
                _options.DueTime = value;
            }
        }
    }

    /// <summary>周期长度（单调时钟 tick）。调度循环用整数 tick 推下一拍，避免浮点累积误差。</summary>
    public long PeriodTimestamps => Volatile.Read(ref PeriodTicks);

    /// <summary>
    /// <see langword="true"/>（默认）周期触发；<see langword="false"/> 单次触发后自动停表。运行中修改在下一拍生效。
    /// </summary>
    public bool AutoReset
    {
        get => Volatile.Read(ref _autoResetFlag);
        set
        {
            lock (_gate)
            {
                _options.AutoReset = value;
                Volatile.Write(ref _autoResetFlag, value);
            }
        }
    }

    /// <summary>等待策略。赋值会让运行中的定时器重新挂载（同时重新锚定相位）。</summary>
    public WaitMode Mode
    {
        get => _options.Mode;
        set
        {
            bool restart = IsRunning;
            lock (_gate)
            {
                _options.Mode = value;
                Settings = BuildSettings();
            }

            if (restart)
            {
                Stop();
                Start();
            }
        }
    }

    /// <summary>当前自适应自旋窗口（纳秒）；未启用自适应时为 0。</summary>
    public double SpinWindowNanoseconds => _window?.ValueNanoseconds ?? 0;

    /// <summary>历史观测到的最大内核唤醒延迟（纳秒）；未启用自适应时为 0。</summary>
    public double MaxKernelWakeLatencyNanoseconds => _window?.ObservedMaxWakeLatencyNanoseconds ?? 0;

    /// <summary>启动以来已触发的拍数（从 1 开始）。</summary>
    public long Sequence => Volatile.Read(ref SequenceField);

    /// <summary>最近一拍的迟到量（纳秒）。零分配读取路径，适合高频回调里做告警判断。</summary>
    public double LastLatenessNanoseconds => Volatile.Read(ref _lastLatenessNanoseconds);

    /// <summary>最近一拍的应到时间戳。</summary>
    public long LastDueTimestamp => Volatile.Read(ref _lastDueTimestamp);

    /// <summary>
    /// 每次到期触发。回调在调度线程上执行，<b>必须极短</b>（微秒级）；稍重的活儿请自己丢线程池。
    /// </summary>
    /// <remarks>
    /// 有订阅者时每拍会构造一个 <see cref="TimerElapsedEventArgs"/>（约 80 字节）。
    /// 高频（&gt;100kHz）请改用 <see cref="HighResolutionTimerOptions.Callback"/> 零分配路径。
    /// </remarks>
    public event EventHandler<TimerElapsedEventArgs>? Elapsed;

    /// <summary>回调抛出异常时触发（<see cref="HighResolutionTimerOptions.SuppressCallbackExceptions"/> 为 true 时才可能观察到）。</summary>
    public event EventHandler<TimerErrorEventArgs>? Error;

    /// <summary>
    /// 开始调度：按 <see cref="DueTime"/>（默认等于周期）安排第一拍。
    /// </summary>
    /// <remarks>已经在跑时先停后启，等于重新锚定相位。</remarks>
    /// <exception cref="ObjectDisposedException">已释放。</exception>
    public void Start()
    {
        Guard.NotDisposed(Volatile.Read(ref _state) == (int)TimerState.Disposed, this);

        long dueTimeTicks;
        lock (_gate)
        {
            dueTimeTicks = HiResClock.ToTicks(_options.DueTime ?? _options.Period);
            Volatile.Write(ref _autoResetFlag, _options.AutoReset);
            _tickEvent.Reset();
            _collector.MarkStarted();
        }

        long firstDue = HiResClock.Add(HiResClock.Now(), dueTimeTicks);

        // 重新锚定相位：已经在跑时先摘掉旧记录。构造函数默认 AutoStart，用户再显式 Start() 一次很常见；
        // 不先摘除就会在堆里留下两份记录——Debug 下断言失败，Release 下双倍触发。
        _ = _scheduler.Disarm(this);
        _scheduler.Arm(this, Settings, firstDue);
        Volatile.Write(ref _state, (int)TimerState.Running);
    }

    /// <summary>
    /// 停止调度。已经在执行中的回调不会被取消（与 <see cref="System.Threading.Timer"/> 语义一致）。
    /// </summary>
    /// <returns><see langword="true"/> 表示确实从"运行中"被停下。</returns>
    public bool Stop()
    {
        if (Volatile.Read(ref _state) != (int)TimerState.Running)
        {
            return _scheduler.Disarm(this);
        }

        bool wasArmed = _scheduler.Disarm(this);
        Volatile.Write(ref _state, (int)TimerState.Stopped);
        return wasArmed;
    }

    /// <summary>
    /// 原地修改周期与首次延迟（不重启调度线程）。语义对齐 <see cref="System.Threading.Timer.Change(int,int)"/>，
    /// 但到期点同样重新锚定为"现在 + <paramref name="dueTime"/>".
    /// </summary>
    /// <param name="dueTime">下一拍的延迟；<see cref="TimeSpan.Zero"/> 表示立即触发一次。</param>
    /// <param name="period">新周期；&lt;= 0 表示改为单次触发。</param>
    /// <returns><see langword="true"/> 表示定时器当前在调度中并已按新值调整；false 表示它未在运行（只更新了配置）。</returns>
    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        Guard.NotLessThan(dueTime, TimeSpan.Zero, nameof(dueTime));

        long periodTicks = period > TimeSpan.Zero ? Math.Max(1, HiResClock.ToTicks(period)) : 0;
        long newDue = HiResClock.Add(HiResClock.Now(), HiResClock.ToTicks(dueTime));

        lock (_gate)
        {
            _options.Period = period > TimeSpan.Zero ? period : _options.Period;
            _options.DueTime = dueTime;

            // 允许置 0：0 表示"这一拍之后不再重复"，对齐 BCL 里 Change(due, -1) 的语义。
            PeriodTicks = periodTicks;
        }

        return _scheduler.UpdateSchedule(this, PeriodTicks, newDue);
    }

    /// <summary>
    /// 复位：停表、清零统计，然后按原配置重新起算。常用于外部事件到达时把节拍拉回整周期相位。
    /// </summary>
    public void Reset()
    {
        _ = Stop();
        ResetStatistics();
        Start();
    }

    /// <summary>
    /// 同步等待下一拍。适合基准测试和"每拍干活"的循环。
    /// </summary>
    /// <param name="timeout">最长等待时长；null 表示无限等待。</param>
    /// <returns><see langword="true"/> 表示等到了下一拍；<see langword="false"/> 表示超时或定时器已释放。</returns>
    /// <remarks>同一时刻只应有一个线程调用本方法（内部是一个置位事件，会被并发消费者瓜分）。</remarks>
    public bool WaitOne(TimeSpan? timeout = null)
    {
        try
        {
            if (!_tickEvent.IsSet && Volatile.Read(ref _state) == (int)TimerState.Disposed)
            {
                return false;
            }

            if (timeout is null)
            {
                // ManualResetEventSlim.Wait() 无参重载返回 void：它只在事件置位后才返回。
                _tickEvent.Wait();
                _tickEvent.Reset();
                return true;
            }

            bool signaled = _tickEvent.Wait(timeout.Value);
            if (signaled)
            {
                _tickEvent.Reset();
            }

            return signaled;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 用毫秒数设置周期（可小数，如 <c>16.67</c>）。运行中也生效，等价于 <see cref="Period"/> 的赋值。
    /// </summary>
    /// <remarks>
    /// 这是给"拉模式"准备的便捷入口，形状对齐 <c>Haukcode.HighResolutionTimer</c> 一类库的
    /// <c>SetPeriod(double)</c>。赋值会重新锚定相位：从此刻起按新周期干净地走。
    /// </remarks>
    /// <param name="periodMilliseconds">周期（毫秒），必须是有限正数。</param>
    /// <exception cref="ArgumentOutOfRangeException">不是有限正数。</exception>
    public void SetPeriod(double periodMilliseconds)
    {
        if (!MathCompat.IsFinite(periodMilliseconds) || periodMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(periodMilliseconds),
                periodMilliseconds,
                "周期必须是有限正数（毫秒）。");
        }

        Period = TimeSpan.FromMilliseconds(periodMilliseconds);
    }

    /// <summary>
    /// 拉模式：阻塞当前线程直到下一拍到达，然后返回。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 语义等价于 <see cref="WaitForNextTickAsync"/> 的同步版：适合"等一拍、干一段活、再等一拍"的循环。
    /// 全程零 CPU 等待一次内核事件，代价是那一拍要经过一次线程唤醒才进入你的代码：
    /// 本机（Windows 11 / 16 核 / 1ms 节拍）三次实测投递迟到 p50 = 11~42µs、p99 = 158~187µs
    /// （机器变忙时还能再高一个量级）。要把它压到 µs 级，用 <see cref="WaitForTrigger(TimeSpan)"/>
    /// 给一段自旋窗口。
    /// </para>
    /// <para>
    /// 要把抖动压到最低，直接用 <see cref="Elapsed"/> / <see cref="HighResolutionTimerOptions.Callback"/> 推送模式：
    /// 回调就在调度线程上、恰好在该触发的时刻执行，没有跨线程投递，本机实测 p50 ≈ 0.2µs。
    /// </para>
    /// <para>
    /// 同一时刻只允许一个线程调用本方法。循环每轮复核状态：别的线程 <see cref="Stop"/> 后约 50ms 内
    /// 就会抛 <see cref="InvalidOperationException"/>，不会永久挂住。
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">定时器已释放。</exception>
    /// <exception cref="InvalidOperationException">定时器未在运行（含等待期间被停表）。</exception>
    public void WaitForTrigger() => PullTick(TimeSpan.Zero);

    /// <summary>
    /// 拉模式（µs 级投递）：至少提前 <paramref name="spinWindow"/> 醒来，在调用线程里自旋到下一拍。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是把调度线程那套"内核睡到窗口 + 尾部自旋"搬到消费者线程：粗等待用高分辨率内核等待
    /// （µs 级粒度，不受系统节拍量化），最后一段只轮询一个 volatile 序列号，没有内核唤醒。
    /// </para>
    /// <para>
    /// <b><paramref name="spinWindow"/> 是下限，不是精确值：</b>内核睡醒本身有几百 µs 的迟到
    /// （本机 p50 ≈ 0.58ms、p99 ≈ 0.8~1.2ms，见 <see cref="HighResolutionWait.MeasureKernelWakeLatency"/>）。
    /// 如果按你给的窗口睡、醒来却已越过到期点，这一拍照样迟到。所以窗口会自动增长到覆盖实测的唤醒延迟
    /// （只增不减，可长达整个周期）——<b>精度优先</b>。本机实测 1ms 节拍给 200µs 窗口，稳态约占 90% 一个逻辑核：
    /// 因为睡醒迟到（p50 ≈ 577µs）本身就比 200µs 窗口长，最后剩下的时间只能靠自旋。
    /// 想省 CPU 就别给窗口（<see cref="WaitForTrigger()"/>），或者直接用推送模式。
    /// </para>
    /// <para>
    /// <see cref="TimeSpan.Zero"/> 等价于 <see cref="WaitForTrigger()"/>（全程不自旋）。
    /// 首拍到达前、单次定时器、以及已经落后到"下一拍已过点"的情形会自动退回有界事件等待。
    /// 同一时刻只允许一个消费者。
    /// </para>
    /// </remarks>
    /// <param name="spinWindow">尾部自旋窗口的<b>下限</b>；建议从
    /// <c>HighResolutionWait.MeasureKernelWakeLatency().RecommendedSpinWindow</c> 的量级
    /// （本机每次标定在 1.3~3.5ms 之间浮动）起调。</param>
    /// <exception cref="ObjectDisposedException">定时器已释放。</exception>
    /// <exception cref="InvalidOperationException">定时器未在运行（含等待期间被停表）。</exception>
    public void WaitForTrigger(TimeSpan spinWindow) => PullTick(spinWindow);

    /// <summary>
    /// 拉模式的异步版：等到下一拍或取消。正常返回即代表等到了；取消抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    /// <remarks>
    /// 完成通知要经过一次线程池派发，所以"用户代码真正跑起来"比到期时刻晚一个线程池排队延迟
    /// （通常几十 µs 到毫秒级）。要在这个时刻上做决定，请用 <see cref="Elapsed"/> 内联回调，
    /// 或同步的 <see cref="WaitForTrigger(TimeSpan)"/>。
    /// </remarks>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ObjectDisposedException">定时器已释放。</exception>
    /// <exception cref="InvalidOperationException">定时器未在运行。</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 被取消。</exception>
    public Task WaitForTriggerAsync(CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        Task<bool> wait = WaitForNextTickAsync(cancellationToken);
#if NET40
        return TaskCompat.Then(wait, tick =>
        {
            if (!tick)
            {
                // 等待期间被 Dispose：WaitForNextTickAsync 以 false 结束。
                Guard.NotDisposed(Volatile.Read(ref _state) == (int)TimerState.Disposed, this);
            }
        });
#else
        return WaitForTriggerCoreAsync(wait);
#endif
    }

#if !NET40
    private async Task WaitForTriggerCoreAsync(Task<bool> wait)
    {
        if (!await wait.ConfigureAwait(false))
        {
            // 等待期间被 Dispose：WaitForNextTickAsync 以 false 结束。
            Guard.NotDisposed(Volatile.Read(ref _state) == (int)TimerState.Disposed, this);
        }
    }
#endif

    /// <summary>拉模式的公共实现：<see cref="SequenceField"/> 变化是唯一交付判据，返回即"观察到一次新的触发"。</summary>
    private void PullTick(TimeSpan spinWindow)
    {
        long floorTicks = spinWindow > TimeSpan.Zero ? Math.Max(1, HiResClock.ToTicks(spinWindow)) : 0;
        PreparePullWindow(floorTicks);
        long observed = Volatile.Read(ref _pulledSequence);

        while (true)
        {
            // 只先复核"已释放"；"已停止"要等检查完序列号再说：单次定时器触发后调度线程会立刻
            // 把状态置成 Stopped，若先查状态，等待者会在"这一拍其实已经交付"的情况下误抛。
            Guard.NotDisposed(Volatile.Read(ref _state) == (int)TimerState.Disposed, this);

            long current = Volatile.Read(ref SequenceField);
            if (current != observed)
            {
                // 这一拍的 FireCore 可能已经置位了同步事件；吞掉它，避免下一次调用被"过时的置位"立即满足。
                ResetTickEvent();
                Volatile.Write(ref _pulledSequence, current);

                // 记住这一拍的应到时刻：下一拍的目标要基于"我交付到哪儿"推算，
                // 而不是读定时器的 _lastDueTimestamp——后者在 FireCore 里先于序列号发布，
                // 读到它就可能已经跨到下一拍，从而把这一拍整个跳过（实测表现为恰好迟到 1 个周期）。
                Volatile.Write(ref _pulledDueTimestamp, Volatile.Read(ref _lastDueTimestamp));
                return;
            }

            EnsureRunning();

            if (floorTicks > 0)
            {
                long periodTicks = Volatile.Read(ref PeriodTicks);
                long lastDue = Volatile.Read(ref _pulledDueTimestamp);
                if (lastDue == 0)
                {
                    // 还没交付过：以定时器的最近一次触发为起点；连一次都没触发过则等第一拍。
                    lastDue = Volatile.Read(ref _lastDueTimestamp);
                }

                if (periodTicks > 0 && lastDue > 0)
                {
                    // 等待目标 = 下一拍应到时刻（无漂移网格：上一拍应到时刻 + 周期）。
                    long now = HiResClock.Now();
                    long target = HiResClock.Add(lastDue, periodTicks);

                    long recheck = HiResClock.Add(now, HiResClock.ToTicksFromMilliseconds(PullEventWaitMilliseconds));
                    if (target - recheck > 0)
                    {
                        // 长周期也保持"停表后约 50ms 内复核状态"的承诺。
                        target = recheck;
                    }

                    long remaining = target - now;
                    if (remaining > 0)
                    {
                        long window = Math.Max(floorTicks, Volatile.Read(ref _pullSpinWindowTicks));
                        if (remaining > window)
                        {
                            // 粗等待：睡到"目标前一个窗口"。内核睡醒迟到的部分会吃掉窗口，
                            // 迟到多少就记下来，下次把窗口放大到能盖住它。
                            long planned = target - window;
                            long wanted = planned - now;
                            long chunk = Math.Min(wanted, HiResClock.ToTicksFromMilliseconds(PullEventWaitMilliseconds));
                            long wakeDeadline = HiResClock.Add(now, chunk);
                            _ = KernelWait.WaitUntil(wakeDeadline, 0, null);

                            long overshoot = HiResClock.Now() - wakeDeadline;
                            if (chunk == wanted && overshoot > 0)
                            {
                                GrowPullWindow(overshoot + (overshoot >> 2) + floorTicks);
                            }

                            continue;
                        }

                        SpinUntilTargetOrSequence(observed, target);
                        continue;
                    }

                    // 已过点：调度线程的触发就在这一刻附近，先自旋一小段等序列号，再退回事件等待。
                    SpinUntilTargetOrSequence(observed, HiResClock.Add(HiResClock.Now(), PullPostDueSpinTicks));
                    continue;
                }
            }

            // 首拍尚未到达、单次定时器、或已经落后于到期点：有界事件等待兜底（零 CPU，但要多付一次唤醒）。
            try
            {
                _ = _tickEvent.Wait(PullEventWaitMilliseconds);
            }
            catch (ObjectDisposedException)
            {
                EnsureRunning();
                throw;
            }
        }
    }

    /// <summary>
    /// 首次进入拉模式、或窗口下限被改掉时，用进程内已标定的内核唤醒迟到来给窗口打底，
    /// 避免"第一拍就睡过头"。
    /// </summary>
    private void PreparePullWindow(long floorTicks)
    {
        if (floorTicks <= 0)
        {
            return;
        }

        if (_pullWindowFloorTicks != floorTicks)
        {
            _pullWindowFloorTicks = floorTicks;
            Volatile.Write(ref _pullSpinWindowTicks, 0);
        }

        if (Volatile.Read(ref _pullSpinWindowTicks) != 0)
        {
            return;
        }

        double observedMax = AdaptiveSpinWindow.Shared.ObservedMaxWakeLatencyNanoseconds;
        if (observedMax > 0)
        {
            long estimate = HiResClock.ToTicks(observedMax * 1.25) + floorTicks;
            _ = Interlocked.CompareExchange(ref _pullSpinWindowTicks, CapPullWindow(estimate), 0);
        }
    }

    /// <summary>放大拉模式自旋窗口（只增不减：窗口不够只会表现为迟到，够大只会多烧 CPU）。</summary>
    private void GrowPullWindow(long neededTicks)
    {
        long current = Volatile.Read(ref _pullSpinWindowTicks);
        long target = CapPullWindow(neededTicks);
        if (target <= current)
        {
            return;
        }

        _ = Interlocked.CompareExchange(ref _pullSpinWindowTicks, target, current);
    }

    private static long CapPullWindow(long ticks) =>
        Math.Min(ticks, HiResClock.ToTicks(HighResolutionWait.MaxSpinWindowNanoseconds));

    /// <summary>自旋到"目标时刻"或"序列号变化"（交付由外层循环完成），两个出口都是有界的。</summary>
    private void SpinUntilTargetOrSequence(long observed, long targetTimestamp)
    {
        while (Volatile.Read(ref SequenceField) == observed)
        {
            if (HiResClock.Now() - targetTimestamp >= 0 || Volatile.Read(ref _state) != (int)TimerState.Running)
            {
                return;
            }

            Thread.SpinWait(20);
        }
    }

    /// <summary>每轮等待前复核状态：已释放抛 <see cref="ObjectDisposedException"/>，未运行抛 <see cref="InvalidOperationException"/>。</summary>
    private void EnsureRunning()
    {
        Guard.NotDisposed(Volatile.Read(ref _state) == (int)TimerState.Disposed, this);
        if (Volatile.Read(ref _state) != (int)TimerState.Running)
        {
            throw new InvalidOperationException($"定时器 {Name} 未在运行，请先调用 {nameof(Start)}()。");
        }
    }

    private void ResetTickEvent()
    {
        try
        {
            _tickEvent.Reset();
        }
        catch (ObjectDisposedException)
        {
            EnsureRunning();
        }
    }

    /// <summary>同步等待下一拍，带毫秒超时。</summary>
    /// <param name="millisecondsTimeout">超时毫秒数；-1（<see cref="Timeout.Infinite"/>）表示无限。</param>
    /// <returns>是否等到了。</returns>
    /// <exception cref="ArgumentOutOfRangeException">小于 -1。</exception>
    public bool WaitOne(int millisecondsTimeout)
    {
        Guard.NotLessThan(millisecondsTimeout, System.Threading.Timeout.Infinite, nameof(millisecondsTimeout));

        try
        {
            if (!_tickEvent.IsSet && Volatile.Read(ref _state) == (int)TimerState.Disposed)
            {
                return false;
            }

            bool signaled = _tickEvent.Wait(millisecondsTimeout);
            if (signaled)
            {
                _tickEvent.Reset();
            }

            return signaled;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 异步等待下一拍。语义对齐 <see cref="System.Threading.PeriodicTimer"/>：
    /// 返回 <see langword="false"/> 表示定时器已被释放。
    /// </summary>
    /// <remarks>
    /// 完成通知经过一次线程池派发，因此"用户代码真正跑起来"会比到期时刻晚一个线程池排队延迟。
    /// 要在那个时刻的亚微秒精度上做决定，请用 <see cref="Elapsed"/> 内联回调。
    /// </remarks>
    /// <param name="cancellationToken">取消令牌；取消时任务以 <see cref="OperationCanceledException"/> 结束。</param>
    public Task<bool> WaitForNextTickAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool> source = TaskCompat.CreateBooleanSource();
        lock (_waiters)
        {
            if (Volatile.Read(ref _state) == (int)TimerState.Disposed)
            {
                return TaskCompat.FromResult(false);
            }

            _waiters.Add(source);
        }

        if (!cancellationToken.CanBeCanceled)
        {
            // 没有令牌时省掉一次 CancellationTokenSource 注册分配。
            return source.Task;
        }

        return WaitForNextTickCoreAsync(source, cancellationToken);
    }

#if NET40
    // .NET 4.0 没有 async/await（编译器需要的 AsyncTaskMethodBuilder 从 4.5 才进入 mscorlib），
    // 这里用 TCS + 取消注册手工表达同一语义：取消时把自己从等待队列摘掉，再让任务以“已取消”结束。
    private Task<bool> WaitForNextTickCoreAsync(TaskCompletionSource<bool> source, CancellationToken cancellationToken)
    {
        CancellationTokenRegistration registration = CancellationCompat.Register(
            state =>
            {
                TaskCompletionSource<bool> waiter = (TaskCompletionSource<bool>)state!;
                lock (_waiters)
                {
                    _ = _waiters.Remove(waiter);
                }

                _ = waiter.TrySetCanceled();
            },
            source,
            cancellationToken);

        // 任务结束时注销注册，避免令牌源长期持有回调。
        _ = source.Task.ContinueWith(
            _ => registration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return source.Task;
    }
#else
    private async Task<bool> WaitForNextTickCoreAsync(TaskCompletionSource<bool> source, CancellationToken cancellationToken)
    {
        try
        {
            using (CancellationCompat.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), source, cancellationToken))
            {
                return await source.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_waiters)
            {
                _ = _waiters.Remove(source);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }
#endif

    /// <summary>取当前精度统计快照（含迟到量对数直方图）。</summary>
    public TimerStatistics GetStatistics()
    {
        lock (_gate)
        {
            return _collector.Snapshot(
                Name,
                _options.Mode,
                HiResClock.ToNanoseconds(PeriodTicks),
                SpinWindowNanoseconds,
                MaxKernelWakeLatencyNanoseconds);
        }
    }

    /// <summary>清零统计与直方图（不影响调度状态）。</summary>
    public void ResetStatistics()
    {
        lock (_gate)
        {
            _collector.Reset();
        }
    }

    /// <summary>停止调度并释放资源；正在异步等待的调用会得到 <see langword="false"/>。</summary>
    public void Dispose()
    {
        int previous = Interlocked.Exchange(ref _state, (int)TimerState.Disposed);
        if (previous == (int)TimerState.Disposed)
        {
            return;
        }

        _ = _scheduler.Disarm(this);
        CompleteWaiters(completed: false);
#if !NET40
        _gcScope?.Dispose();
#endif

        if (_ownsScheduler)
        {
            _scheduler.Dispose();
        }

        _tickEvent.Dispose();
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name}: period={MathCompat.TotalMicroseconds(Period):F1}us mode={Mode} state={State}";

    // ============================ 与调度线程的契约（internal） ============================

    /// <summary>本拍应到的绝对时间戳。调度线程在调度器锁内读写。</summary>
    internal long DueTimestamp;

    /// <summary>周期（tick）。0 表示单次触发。</summary>
    internal long PeriodTicks;

    /// <summary>是否应归属到调度堆上（在飞的回调也算"归属中"，由版本号决定谁来重新入堆）。</summary>
    internal bool IsArmed;

    /// <summary>入堆顺序，用于同一到期点的稳定排序。</summary>
    internal long Ordinal;

    /// <summary>Arm/Disarm 版本号。重新入堆前必须核对，防止"停→起"造成堆内双份记录。</summary>
    internal long ArmVersion;

    /// <summary>在堆中的下标；-1 表示不在堆内。由 <see cref="TimerHeap"/> 维护。</summary>
    internal int HeapIndex = -1;

    /// <summary>触发计数（内部存储；对外见 <see cref="Sequence"/>）。</summary>
    internal long SequenceField;

    /// <summary>上一拍实际触发时间戳。</summary>
    internal long LastFiredTimestamp;

    /// <summary>本定时器的等待策略快照，调度线程等待"到本定时器到期点"时使用。</summary>
    internal WaitSettings Settings { get; private set; }

    /// <summary>由 <see cref="HighResolutionScheduler.Arm"/> 在调度器锁内调用：置入调度字段。</summary>
    internal void ArmCore(in WaitSettings settings, long ordinal, long dueTimestamp)
    {
        Settings = settings;
        Ordinal = ordinal;
        DueTimestamp = dueTimestamp;
        IsArmed = true;
        Interlocked.Increment(ref ArmVersion);
        LastFiredTimestamp = 0;
    }

    /// <summary>由调度线程在触发一拍时调用：记统计、发事件、通知异步等待者。</summary>
    internal void FireCore(long dueTimestamp, long firedTimestamp, double latenessNanos, int missedTicks)
    {
        Volatile.Write(ref _lastDueTimestamp, dueTimestamp);
        Volatile.Write(ref _lastLatenessNanoseconds, latenessNanos);
        long sequence = Interlocked.Increment(ref SequenceField);
        long previousFired = Interlocked.Exchange(ref LastFiredTimestamp, firedTimestamp);
        double intervalNanos = previousFired > 0 ? HiResClock.ToNanoseconds(firedTimestamp - previousFired) : double.NaN;

        lock (_gate)
        {
            _collector.Record(latenessNanos, intervalNanos, missedTicks, firedTimestamp);
        }

        CompleteWaiters(completed: true);

        Action? callback = _options.Callback;
        EventHandler<TimerElapsedEventArgs>? handlers = Elapsed;
        if (callback is null && handlers is null)
        {
            // 没有任何消费者：仍然置位同步等待事件，供 WaitOne 的循环模式使用。
            _tickEvent.Set();
            return;
        }

        _tickEvent.Set();

        if (callback is not null)
        {
            InvokeCallback(callback, sequence);
        }

        if (handlers is not null)
        {
            TimerElapsedEventArgs args = new(this, dueTimestamp, firedTimestamp, PeriodTicks, sequence, missedTicks, previousFired);
            InvokeHandlers(handlers, args, sequence);
        }
    }

    /// <summary>单次定时器触发后由调度线程置停。</summary>
    internal void MarkStoppedAfterOneShot()
    {
        IsArmed = false;
        Volatile.Write(ref _state, (int)TimerState.Stopped);
    }

    private void InvokeCallback(Action callback, long sequence)
    {
        try
        {
            callback();
        }
        catch (Exception exception) when (_options.SuppressCallbackExceptions)
        {
            RecordErrorAndRaise(exception, sequence);
        }
    }

    private void InvokeHandlers(EventHandler<TimerElapsedEventArgs> handlers, TimerElapsedEventArgs args, long sequence)
    {
        try
        {
            handlers(this, args);
        }
        catch (Exception exception) when (_options.SuppressCallbackExceptions)
        {
            RecordErrorAndRaise(exception, sequence);
        }
    }

    private void RecordErrorAndRaise(Exception exception, long sequence)
    {
        lock (_gate)
        {
            _collector.RecordError();
        }

        Error?.Invoke(this, new TimerErrorEventArgs(this, sequence, exception));
    }

    private WaitSettings BuildSettings()
    {
        long chunkTicks = HiResClock.ToTicksFromMilliseconds(_options.CoarseChunkMilliseconds);
        return _options.Mode switch
        {
            WaitMode.Spin => WaitSettings.Spin,
            WaitMode.Kernel => WaitSettings.Kernel(chunkTicks),
            _ => _window is not null
                ? WaitSettings.HybridAdaptive(_window, chunkTicks)
                : WaitSettings.HybridFixed(HiResClock.ToTicks(Math.Max(_options.SpinWindowNanoseconds, HighResolutionWait.MinSpinWindowNanoseconds)), chunkTicks),
        };
    }

    private void CompleteWaiters(bool completed)
    {
        if (_waiters.Count == 0)
        {
            return;
        }

        TaskCompletionSource<bool>[] snapshot;
        lock (_waiters)
        {
            if (_waiters.Count == 0)
            {
                return;
            }

            snapshot = _waiters.ToArray();
            _waiters.Clear();
        }

#if NET40
        // .NET 4.0 的 TCS 没有 RunContinuationsAsynchronously：把完成动作丢到线程池，
        // 避免消费者的续体在调度线程上内联执行（那会推迟同一调度器上的其它定时器）。
        _ = ThreadPool.QueueUserWorkItem(
            state =>
            {
                TaskCompletionSource<bool>[] waiters = (TaskCompletionSource<bool>[])state!;
                foreach (TaskCompletionSource<bool> waiter in waiters)
                {
                    _ = waiter.TrySetResult(completed);
                }
            },
            snapshot);
#else
        foreach (TaskCompletionSource<bool> source in snapshot)
        {
            _ = source.TrySetResult(completed);
        }
#endif
    }
}
