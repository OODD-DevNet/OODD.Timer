namespace OODD.Timer;

/// <summary>
/// 定时器状态机。
/// </summary>
public enum TimerState
{
    /// <summary>已创建但未启动（或已被 <see cref="HighResolutionTimer.Stop"/> 停下）。</summary>
    Stopped = 0,

    /// <summary>已挂到调度线程上，正在等待/触发。</summary>
    Running = 1,

    /// <summary>已释放。任何再次启动的尝试都会抛 <see cref="ObjectDisposedException"/>。</summary>
    Disposed = 2,
}

/// <summary>
/// <see cref="HighResolutionTimer"/> 的配置项。
/// </summary>
/// <remarks>
/// 所有属性都可以在创建定时器之后修改（除 <see cref="Scheduler"/> 与 <see cref="Name"/>），
/// 修改只影响之后新建的定时器；已经运行的定时器请用 <see cref="HighResolutionTimer"/> 上的对应属性调整。
/// </remarks>
public sealed class HighResolutionTimerOptions
{
    /// <summary>
    /// 定时器名字，出现在调度线程名与诊断输出里。默认 null（自动生成 <c>OODD.Timer.&lt;序号&gt;</c>）。
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// 周期（<see cref="AutoReset"/> 为 true 时的两次触发间隔；为 false 时的单次延迟）。
    /// </summary>
    /// <remarks>
    /// 必须严格大于 <see cref="TimeSpan.Zero"/>。理论下限是单调时钟粒度（Windows/Linux 约 100ns），
    /// 实际下限取决于回调耗时——回调比周期长时本库会合并漏拍并在
    /// <see cref="TimerElapsedEventArgs.MissedTicks"/> 里报告。
    /// <para>
    /// 精度并不取决于这个值有多小，而取决于 <see cref="Mode"/>：
    /// 1µs 周期配 <see cref="WaitMode.Kernel"/> 依然会迟到几百微秒。
    /// </para>
    /// </remarks>
    public TimeSpan Period { get; set; } = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// 首次触发前的延迟。默认 null，表示等于 <see cref="Period"/>。
    /// </summary>
    public TimeSpan? DueTime { get; set; }

    /// <summary>
    /// <see langword="true"/>（默认）表示周期性重复触发；<see langword="false"/> 表示只触发一次的单次定时器。
    /// </summary>
    public bool AutoReset { get; set; } = true;

    /// <summary>等待策略，默认 <see cref="WaitMode.Hybrid"/>。</summary>
    public WaitMode Mode { get; set; } = WaitMode.Hybrid;

    /// <summary>
    /// 是否让自旋窗口自适应标定内核唤醒延迟。默认 <see langword="true"/>。
    /// </summary>
    /// <remarks>
    /// 关掉它就完全按 <see cref="SpinWindowNanoseconds"/> 固定自旋窗口。
    /// 只有在目标机器已经用 <see cref="HighResolutionWait.MeasureKernelWakeLatency"/>
    /// 标定过、且你要求"第一拍就精确"时才建议关掉。
    /// </remarks>
    public bool AdaptiveSpinWindow { get; set; } = true;

    /// <summary>
    /// 固定自旋窗口（纳秒），仅在 <see cref="AdaptiveSpinWindow"/> 为 <see langword="false"/> 时生效。
    /// 默认 <see cref="HighResolutionWait.DefaultSpinWindowNanoseconds"/>（1ms）。
    /// </summary>
    /// <remarks>
    /// 取值含义：窗口太短 → 内核醒得晚，定时器迟到；窗口太长 → 白烧 CPU。
    /// 正确取值 = 该平台内核唤醒延迟的 p99 再留 25% 余量。
    /// </remarks>
    public double SpinWindowNanoseconds { get; set; } = HighResolutionWait.DefaultSpinWindowNanoseconds;

    /// <summary>
    /// 自适应窗口的上限（纳秒）。默认 <see cref="HighResolutionWait.MaxSpinWindowNanoseconds"/>（32ms）。
    /// </summary>
    /// <remarks>
    /// 触顶意味着这台机器的内核等待太粗，此时混合模式已经等价于纯自旋。
    /// 宁可让它触顶也不要调小——调小会直接表现为定时器迟到。
    /// </remarks>
    public double MaxSpinWindowNanoseconds { get; set; } = HighResolutionWait.MaxSpinWindowNanoseconds;

    /// <summary>
    /// 粗等待阶段单段最长毫秒数，决定 <see cref="HighResolutionTimer.Stop"/> /
    /// <see cref="HighResolutionTimer.Dispose"/> 与"新任务插队"的最早响应延迟。默认 2ms。
    /// </summary>
    public double CoarseChunkMilliseconds { get; set; } = 2;

    /// <summary>
    /// 工作线程优先级。默认 <see cref="ThreadPriority.Highest"/>。
    /// </summary>
    /// <remarks>
    /// 本机实测（1ms 节拍 × 3000 拍、纯自旋、无分配压力）：
    /// <see cref="ThreadPriority.Highest"/> 的最大迟到尖峰 1.97ms，
    /// <see cref="ThreadPriority.Normal"/> 是 9.90ms；p50/p95/p99 三者没有差别。
    /// 也就是说优先级改的是<b>最坏情况</b>，不是典型情况。
    /// 这也不是"实时优先级"——Windows 的 ThreadPriority 只映射到线程优先级类别，
    /// 真正的 µs 级硬保证需要操作系统的实时调度（见 docs/进阶：把误差压到极限.md）。
    /// </remarks>
    public ThreadPriority ThreadPriority { get; set; } = ThreadPriority.Highest;

    /// <summary>
    /// 是否把独占的调度线程提升到 Windows 的 <c>THREAD_PRIORITY_TIME_CRITICAL</c>（15）。
    /// 默认 <see langword="false"/>；仅在未指定 <see cref="Scheduler"/> 时生效。
    /// </summary>
    /// <remarks>
    /// <para>
    /// .NET 的 <see cref="ThreadPriority"/> 最高只到 <see cref="ThreadPriority.Highest"/>（10），
    /// 而 Windows 的 15 级能让调度线程抢占普通线程的时间片。
    /// 本机实测（1ms 节拍 × 20000 拍）：开启后 p99 从 20µs 降到 2µs、p99.9 从 100µs 降到 50µs。
    /// </para>
    /// <para>
    /// 高优先级自旋可能使低优先级线程饥饿，等待它们的锁可能发生优先级反转；不独占核心，也不免除 GC。
    /// </para>
    /// <para>非 Windows 平台忽略此设置。</para>
    /// </remarks>
    public bool UseRealTimePriority { get; set; }

    /// <summary>绑定的逻辑处理器编号，null 不绑核；指定 Scheduler 时须在调度器选项上配置，否则抛异常。</summary>
    public int? ProcessorIndex { get; set; }

    /// <summary>Windows 处理器组，默认 0；Linux 必须为 0。需同时指定 ProcessorIndex。</summary>
    public ushort ProcessorGroup { get; set; }

    /// <summary>
    /// 共享调度器。默认 null，表示为该定时器独占创建一个调度线程。
    /// </summary>
    /// <remarks>
    /// 十几个以上定时器时请传入同一个 <see cref="HighResolutionScheduler"/>
    /// （例如 <see cref="HighResolutionScheduler.Shared"/>）：所有定时器共用一个线程的自旋开销，
    /// 代价是同一次回调耗时会推迟同一调度器上更晚到期的定时器。
    /// </remarks>
    public HighResolutionScheduler? Scheduler { get; set; }

    /// <summary>
    /// 构造时是否立即启动。默认 <see langword="true"/>（和 <see cref="System.Threading.Timer"/> 一致，
    /// 便于 <c>using</c> 一行搞定）。设为 false 后可自己调 <see cref="HighResolutionTimer.Start"/>。
    /// </summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>
    /// 无 Elapsed 订阅和异步等待者时的零分配回调；同时订阅 Elapsed 仍会分配事件参数，同进程 GC 仍可暂停本线程。
    /// </summary>
    public Action? Callback { get; set; }

    /// <summary>
    /// 回调抛出的异常是否吞掉并计入统计。默认 <see langword="true"/>：
    /// 异常会记录到 <see cref="HighResolutionTimer.Error"/> 事件与统计里，不会让调度线程崩掉。
    /// 设为 false 时异常直接冒泡，工作线程会终止（仅适合开发期暴露问题）。
    /// </summary>
    public bool SuppressCallbackExceptions { get; set; } = true;

    /// <summary>
    /// 是否在定时器运行期间请求 GC 进入 <see cref="System.Runtime.GCLatencyMode.SustainedLowLatency"/>。
    /// 默认 <see langword="false"/>。
    /// </summary>
    /// <remarks>
    /// 本机实测（1ms 节拍 × 3000 拍、纯自旋、正常优先级、后台持续产生 gen0 垃圾）：
    /// 默认 <c>Interactive</c> 模式下 p95 迟到 262µs、max 7.0ms、18 拍被合并；
    /// 开启本开关后 p95 131µs、max 0.51ms、0 拍被合并。
    /// 没有分配压力时两种配置的分布基本一致——所以先消除回调里的分配，再考虑这个开关。
    /// 开启会推迟阻塞式 GC，代价是整个进程的托管内存占用上升；
    /// 这是进程级设置，多个定时器共享一个引用计数，全部停掉后自动恢复。
    /// </remarks>
    public bool PreferSustainedLowLatencyGc { get; set; }

    /// <summary>
    /// 每拍在到期点上叠加的随机抖动上限（纳秒）。默认 0，即严格的无漂移格点。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用途是<b>防惊群</b>：很多设备/进程在同一个整秒或同一个周期点唤醒会互相抢占，
    /// 给每拍的到期点加一个 <c>[0, JitterNanoseconds]</c> 的随机偏移可以把唤醒时刻散开。
    /// </para>
    /// <para>
    /// 代价：开启后<b>主动放弃</b>“到期点恒等于首拍 + n×周期”这条无漂移不变量——这正是抖动要做的事。
    /// 抖动只会让到期点更晚（非负），因此 <see cref="TimerElapsedEventArgs.LatenessNanoseconds"/>
    /// 仍然只反映“相对本拍到期点的触发精度”，不会为负。相邻两拍的应到间隔落在
    /// <c>[周期, 周期 + JitterNanoseconds]</c>。默认 0 时行为与不设抖动完全一致。
    /// </para>
    /// <para>必须小于一个周期，否则抖动可能盖过周期本身、让语义退化。</para>
    /// </remarks>
    public double JitterNanoseconds { get; set; }

    /// <summary>
    /// 是否把回调/事件投递到线程池执行，让调度线程立即回到等待下一拍。默认 <see langword="false"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是精度换吞吐的取舍，请想清楚再用。</b>关掉时（默认）回调就在调度线程、恰好在到期点执行，
    /// 是精度最高的路径。开启后回调挪到线程池线程，其执行时刻多了线程池排队延迟（几十µs到ms级），
    /// <see cref="HighResolutionTimer"/> 的到期记账和 <see cref="TimerElapsedEventArgs.LatenessNanoseconds"/>
    /// 仍在调度线程上照常统计，但“你的代码真正跑起来”不再精确对齐到期点。
    /// </para>
    /// <para>
    /// 适合“回调本身就慢，不想让一个慢回调把同一调度器上的兄弟定时器一起拖迟”的场景。
    /// 反压策略固定为<b>单条在飞</b>：上一段卸载的回调还没跑完时，新到期的一拍不再排队，
    /// 而是被丢弃并计入 <see cref="HighResolutionTimer.OffloadedCallbackDrops"/>（绝不无界堆积）。
    /// </para>
    /// </remarks>
    public bool OffloadCallback { get; set; }

    /// <summary>校验配置，非法时抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
    internal void Validate()
    {
        Guard.NotLessThanOrEqual(Period, TimeSpan.Zero, nameof(Period));
        ThreadBoost.ValidateProcessorAffinity(ProcessorIndex, ProcessorGroup);
        if (Scheduler is not null && ProcessorIndex.HasValue)
        {
            throw new ArgumentException("Configure processor affinity on the supplied scheduler, not on an individual timer.", nameof(ProcessorIndex));
        }

        if (DueTime is { } due && due < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DueTime), "首次延迟不能为负。");
        }

        if (SpinWindowNanoseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SpinWindowNanoseconds), "自旋窗口不能为负。");
        }

        if (MaxSpinWindowNanoseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSpinWindowNanoseconds), "自旋窗口上限必须大于 0。");
        }

        if (CoarseChunkMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CoarseChunkMilliseconds), "粗等待分段长度必须大于 0。");
        }

        Guard.NotNegative(JitterNanoseconds, nameof(JitterNanoseconds));
        if (!MathCompat.IsFinite(JitterNanoseconds))
        {
            throw new ArgumentOutOfRangeException(nameof(JitterNanoseconds), JitterNanoseconds, "抖动必须是有限非负数。");
        }

        if (JitterNanoseconds >= Period.Ticks * 100.0)
        {
            throw new ArgumentOutOfRangeException(nameof(JitterNanoseconds), JitterNanoseconds, "抖动上限必须小于一个周期。");
        }
    }

    /// <summary>把当前配置克隆一份（防止调用方复用同一个 options 对象时被相互影响）。</summary>
    internal HighResolutionTimerOptions Clone() => new()
    {
        Name = Name,
        Period = Period,
        DueTime = DueTime,
        AutoReset = AutoReset,
        Mode = Mode,
        AdaptiveSpinWindow = AdaptiveSpinWindow,
        SpinWindowNanoseconds = SpinWindowNanoseconds,
        MaxSpinWindowNanoseconds = MaxSpinWindowNanoseconds,
        CoarseChunkMilliseconds = CoarseChunkMilliseconds,
        ThreadPriority = ThreadPriority,
        UseRealTimePriority = UseRealTimePriority,
        ProcessorIndex = ProcessorIndex,
        ProcessorGroup = ProcessorGroup,
        Scheduler = Scheduler,
        AutoStart = AutoStart,
        Callback = Callback,
        SuppressCallbackExceptions = SuppressCallbackExceptions,
        PreferSustainedLowLatencyGc = PreferSustainedLowLatencyGc,
        JitterNanoseconds = JitterNanoseconds,
        OffloadCallback = OffloadCallback,
    };
}

/// <summary>
/// <see cref="HighResolutionTimer.Elapsed"/> 事件参数：一拍的完整时间与误差信息。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这些信息：</b>高精度定时器真正难的地方不是"触发得准"，而是"知道自己准不准"。
/// 每拍都携带应到时刻与实际触发时刻，你才能在不接外部仪器的情况下画出自己系统上的延迟分布。
/// </para>
/// <para>本对象每次触发新建一个（约 80 字节）。高频场景请改用
/// <see cref="HighResolutionTimerOptions.Callback"/> 零分配路径。</para>
/// </remarks>
public sealed class TimerElapsedEventArgs : EventArgs
{
    internal TimerElapsedEventArgs(HighResolutionTimer timer, long dueTimestamp, long firedTimestamp, long periodTicks, long sequence, int missedTicks, long previousFiredTimestamp)
    {
        Timer = timer;
        DueTimestamp = dueTimestamp;
        FiredTimestamp = firedTimestamp;
        PeriodTimestamps = periodTicks;
        Sequence = sequence;
        MissedTicks = missedTicks;
        long delta = firedTimestamp - dueTimestamp;
        LatenessNanoseconds = HiResClock.ToNanoseconds(delta < 0 ? 0 : delta);
        IntervalNanoseconds = previousFiredTimestamp > 0
            ? HiResClock.ToNanoseconds(firedTimestamp - previousFiredTimestamp)
            : double.NaN;
    }

    /// <summary>触发本次事件的定时器。</summary>
    public HighResolutionTimer Timer { get; }

    /// <summary>本拍"应该"触发的绝对时间戳（单调时钟）。</summary>
    public long DueTimestamp { get; }

    /// <summary>本拍实际进入回调的时间戳（单调时钟）。</summary>
    public long FiredTimestamp { get; }

    /// <summary>周期长度（tick）。</summary>
    public long PeriodTimestamps { get; }

    /// <summary>本拍是启动以来的第几拍，从 1 开始。用于对齐相位、做无损统计。</summary>
    public long Sequence { get; }

    /// <summary>
    /// 因回调超时或线程被抢占而"跳过"的拍数。0 表示没有漏拍。
    /// </summary>
    /// <remarks>
    /// 漏拍不会被补触发（否则会造成雪崩式追赶），只会体现在这个计数里，
    /// 同时计入 <see cref="TimerStatistics.MissedTicks"/>。
    /// </remarks>
    public int MissedTicks { get; }

    /// <summary>本拍迟到了多少纳秒（= 触发时刻 - 应到时刻，负数按 0 计）。</summary>
    public double LatenessNanoseconds { get; }

    /// <summary>本拍迟到了多久。</summary>
    public TimeSpan Lateness => TimeSpan.FromTicks((long)(LatenessNanoseconds / 100.0));

    /// <summary>
    /// 与上一拍实际触发时刻的间隔（纳秒）；第一拍为 <see cref="double.NaN"/>。
    /// </summary>
    public double IntervalNanoseconds { get; }

    /// <summary>是否发生了漏拍。</summary>
    public bool Overrun => MissedTicks > 0;

    /// <summary>调试输出。</summary>
    public override string ToString() =>
        $"#{Sequence} due={DueTimestamp} fired={FiredTimestamp} late={LatenessNanoseconds:F0}ns missed={MissedTicks}";
}

/// <summary>
/// 回调抛出异常时 <see cref="HighResolutionTimer.Error"/> 事件携带的信息。
/// </summary>
/// <param name="timer">发生异常的定时器。</param>
/// <param name="sequence">异常发生在第几拍（从 1 开始）。</param>
/// <param name="exception">回调抛出的原始异常，不会被包装。</param>
public sealed class TimerErrorEventArgs(HighResolutionTimer timer, long sequence, Exception exception) : EventArgs
{
    /// <summary>发生异常的定时器。</summary>
    public HighResolutionTimer Timer { get; } = timer;

    /// <summary>异常发生在第几拍。</summary>
    public long Sequence { get; } = sequence;

    /// <summary>回调抛出的原始异常。</summary>
    public Exception Exception { get; } = exception;
}
