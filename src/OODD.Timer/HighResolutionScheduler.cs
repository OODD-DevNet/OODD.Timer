namespace OODD.Timer;

/// <summary>
/// 高精度调度线程：一个线程按到期时间驱动任意多个 <see cref="HighResolutionTimer"/>。
/// </summary>
/// <remarks>
/// <para>
/// <b>它解决"定时器数量 vs 核心数量"的矛盾。</b>
/// 每个 <see cref="HighResolutionTimer"/> 默认独占一个调度线程——那个线程要为它自旋，
/// 10 个自旋型定时器就是 10 个核。把它们挂到同一个 <see cref="HighResolutionScheduler"/> 上，
/// 自旋开销就变成"每个到期点最多一次"：调度线程总是等向全局最早的那个到期点，
/// 到点后把已到期项逐个触发。
/// </para>
/// <para>
/// <b>代价：</b>同一个调度器上，一次回调的耗时会推迟所有更晚到期的定时器（并计入它们的
/// <see cref="TimerElapsedEventArgs.MissedTicks"/>）。所以回调要么极短（几十微秒内），
/// 要么自己把耗时的活儿丢进线程池。
/// </para>
/// <para>
/// 每个定时器保留自己的 <see cref="WaitMode"/>：调度线程等待"堆顶定时器"的到期点时用的就是
/// 那个定时器自己的策略——反正它的到期点全局最早，它对精度的要求就是此刻应该付出的代价。
/// </para>
/// <para>
/// 调度线程在第一个定时器挂载时才创建；堆空时阻塞在事件对象上，不消耗 CPU。
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using var scheduler = new HighResolutionScheduler();
/// var a = new HighResolutionTimer(TimeSpan.FromMicroseconds(500), scheduler);
/// var b = new HighResolutionTimer(TimeSpan.FromMilliseconds(1), scheduler);
/// </code>
/// </example>
public sealed class HighResolutionScheduler : IDisposable
{
    /// <summary>
    /// 全进程共享的调度器（线程懒创建，永不主动释放）。
    /// </summary>
    /// <remarks>
    /// 需要把多个定时器合并到一个线程时，把它传给
    /// <see cref="HighResolutionTimerOptions.Scheduler"/> 或
    /// <see cref="HighResolutionTimer(TimeSpan, HighResolutionScheduler)"/>。
    /// <see cref="HighResolutionWait.SleepAsync"/> 也借它在到期时完成通知。
    /// </remarks>
    public static HighResolutionScheduler Shared { get; } = new(new HighResolutionSchedulerOptions { Name = "OODD.Timer.Shared" });

    private static int s_schedulerCounter;

    private readonly object _gate = new();
    private readonly TimerHeap _heap = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Thread _thread;
    private readonly HighResolutionSchedulerOptions _options;
    private readonly LatencyCollector _collector = new();
    private readonly int _threadId;
    private long _nextOrdinal;
    private int _state;              // 0=线程未启动 1=运行中 2=已请求停止 3=线程已退出
    private long _totalFiredTicks;
    private readonly object _startupGate = new();
    private bool _affinityInitialized;
    private Exception? _startupError;

    /// <summary>
    /// 创建调度器。此时还没有创建线程，第一个定时器挂载时才创建。
    /// </summary>
    /// <param name="options">调度器配置；null 使用默认值（名称 <c>OODD.Timer.Scheduler.&lt;序号&gt;</c>）。</param>
    /// <exception cref="ArgumentOutOfRangeException">配置非法（窗口上限 ≤ 0 等）。</exception>
    public HighResolutionScheduler(HighResolutionSchedulerOptions? options = null)
    {
        _options = (options ?? new HighResolutionSchedulerOptions()).Clone();
        ThreadBoost.ValidateProcessorAffinity(_options.ProcessorIndex, _options.ProcessorGroup);
        Name = _options.Name ?? $"OODD.Timer.Scheduler.{Interlocked.Increment(ref s_schedulerCounter)}";

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = Name,
            Priority = _options.ThreadPriority,
        };

        // ManagedThreadId 在线程创建后立即可读，不需要线程已启动；
        // 它用于识别"在回调里释放自己所在的调度器"，此时不能 Join 自己。
        _threadId = _thread.ManagedThreadId;
    }

    /// <summary>调度器名字（同时是线程名）。</summary>
    public string Name { get; }

    /// <summary>当前在调度中的定时器数量。</summary>
    public int TimerCount
    {
        get
        {
            lock (_gate)
            {
                return _heap.Count;
            }
        }
    }

    /// <summary>本调度器累计触发次数。</summary>
    public long TotalFiredTicks => Volatile.Read(ref _totalFiredTicks);

    /// <summary>是否已释放或正在释放。</summary>
    public bool IsDisposed => Volatile.Read(ref _state) >= 2;

    /// <summary>本调度线程的托管线程 ID；不是原生线程 ID，不能传给操作系统的亲和性 API。</summary>
    public int ThreadId => _threadId;

    /// <summary>挂载到本调度器的定时器未显式指定策略时的默认等待模式。</summary>
    public WaitMode DefaultMode => _options.Mode;

    /// <summary>调度器级精度统计（把所有定时器的迟到量汇总到一个直方图）。</summary>
    public TimerStatistics GetStatistics()
    {
        lock (_gate)
        {
            return _collector.Snapshot(Name, _options.Mode, 0, 0, 0);
        }
    }

    /// <summary>复位调度器级统计。</summary>
    public void ResetStatistics()
    {
        lock (_gate)
        {
            _collector.Reset();
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name}: timers={TimerCount} fired={TotalFiredTicks} state={Volatile.Read(ref _state)}";

    /// <summary>
    /// 停止调度线程并释放资源；正在调度的定时器随之失效。
    /// </summary>
    /// <remarks>
    /// 会等待调度线程退出（上限 2 秒）。在回调里释放自己所在的调度器不会死锁：
    /// 当前线程就是调度线程时跳过 Join。
    /// </remarks>
    public void Dispose()
    {
        int previous = Interlocked.Exchange(ref _state, 2);
        if (previous >= 2)
        {
            return;
        }

        lock (_gate)
        {
            _wake.Set();
        }

#if NET40
        if (Thread.CurrentThread.ManagedThreadId != _threadId && previous != 0)
#else
        if (Environment.CurrentManagedThreadId != _threadId && previous != 0)
#endif
        {
            _ = _thread.Join(TimeSpan.FromSeconds(2));
        }

        Interlocked.Exchange(ref _state, 3);
    }

    /// <summary>
    /// 由 <see cref="HighResolutionTimer.Start"/> 调用：置入到期时间、加入堆、唤醒调度线程。
    /// </summary>
    /// <param name="timer">要挂载的定时器。</param>
    /// <param name="settings">该定时器的等待策略。</param>
    /// <param name="dueTimestamp">第一拍的绝对到期时间戳。</param>
    /// <exception cref="ObjectDisposedException">调度器已释放。</exception>
    internal void Arm(HighResolutionTimer timer, in WaitSettings settings, long dueTimestamp)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _state) >= 2)
            {
                throw new ObjectDisposedException(Name, "调度器已释放，无法再挂载定时器。");
            }

            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
            {
                try
                {
                    _thread.Start();
                }
                catch
                {
                    Interlocked.Exchange(ref _state, 3);
                    throw;
                }

                if (_options.ProcessorIndex.HasValue)
                {
                    lock (_startupGate)
                    {
                        while (!_affinityInitialized)
                        {
                            Monitor.Wait(_startupGate);
                        }

                        if (_startupError is not null)
                        {
                            throw new InvalidOperationException("Failed to bind the scheduler thread to the requested processor.", _startupError);
                        }
                    }
                }
            }

            Guard.NotDisposed(Volatile.Read(ref _state) >= 2, this);

            timer.ArmCore(settings, ++_nextOrdinal, dueTimestamp);
            _heap.Add(timer);
            _wake.Set();
        }
    }

    /// <summary>
    /// 把定时器从调度中摘除。
    /// </summary>
    /// <param name="timer">要摘除的定时器。</param>
    /// <returns><see langword="true"/> 表示它原本确实在调度中。</returns>
    /// <remarks>
    /// 若该定时器的回调正在执行（"在飞"状态），本次调用不会取消那一拍——
    /// 这与 <see cref="System.Threading.Timer.Change(int,int)"/> 的语义一致：停表不保证没有已排出的回调。
    /// 但 <see cref="TimerElapsedEventArgs.MissedTicks"/> 之后的下一拍一定不会再发生，
    /// 因为版本号已在此锁内递增。
    /// </remarks>
    internal bool Disarm(HighResolutionTimer timer)
    {
        lock (_gate)
        {
            bool wasArmed = timer.IsArmed;
            timer.IsArmed = false;
            Interlocked.Increment(ref timer.ArmVersion);
            _heap.Remove(timer);

            // 唤醒调度线程重算堆顶：它可能正睡在这个（刚被摘除的）定时器的到期点上。
            _wake.Set();
            return wasArmed;
        }
    }

    /// <summary>
    /// 原地修改周期与到期点（改配置时不"停→起"，避免停启之间的空窗与双倍入堆风险）。
    /// </summary>
    /// <param name="timer">目标定时器。</param>
    /// <param name="periodTicks">新周期（tick）。</param>
    /// <param name="dueTimestamp">新到期时间戳。</param>
    /// <returns><see langword="true"/> 表示定时器当前在调度中，已按新值调整。</returns>
    internal bool UpdateSchedule(HighResolutionTimer timer, long periodTicks, long dueTimestamp)
    {
        lock (_gate)
        {
            if (!timer.IsArmed)
            {
                return false;
            }

            timer.PeriodTicks = periodTicks;
            timer.DueTimestamp = dueTimestamp;
            _heap.UpdateKey(timer);
            _wake.Set();
            return true;
        }
    }

    /// <summary>
    /// 在 <paramref name="duration"/> 之后完成的异步单次等待，借用本调度器的线程而不是占用线程池线程。
    /// </summary>
    internal Task SleepAsync(TimeSpan duration, WaitMode mode, CancellationToken cancellationToken)
    {
        HighResolutionTimer timer = new(new HighResolutionTimerOptions
        {
            Period = duration,
            DueTime = duration,
            AutoReset = false,
            Mode = mode,
            Scheduler = this,
            AutoStart = false,
            Name = null,
        });

        timer.Start();
        return CompleteAndDisposeAsync(timer.WaitForNextTickAsync(cancellationToken), timer);
    }

    private static Task CompleteAndDisposeAsync(Task<bool> wait, HighResolutionTimer timer)
    {
#if NET40
        // 等价于下面对应的 async 版本（4.0 没有 async/await）：等完（无论成败）都要 Dispose。
        return TaskCompat.Finally(wait, timer.Dispose);
#else
        return CompleteAndDisposeCoreAsync(wait, timer);
#endif
    }

#if !NET40
    private static async Task CompleteAndDisposeCoreAsync(Task<bool> wait, HighResolutionTimer timer)
    {
        try
        {
            _ = await wait.ConfigureAwait(false);
        }
        finally
        {
            timer.Dispose();
        }
    }
#endif

    /// <summary>调度线程主循环。</summary>
    [System.Runtime.CompilerServices.MethodImpl(JitHints.AggressiveOptimization)]
    private void Run()
    {
        if (_options.ProcessorIndex is int processorIndex)
        {
            // Arm holds the heap lock while awaiting this handshake; initialization must not take that lock.
            lock (_startupGate)
            {
                try
                {
                    ThreadBoost.ApplyProcessorAffinity(processorIndex, _options.ProcessorGroup);
                }
                catch (Exception exception)
                {
                    _startupError = exception;
                    Interlocked.Exchange(ref _state, 3);
                }
                finally
                {
                    _affinityInitialized = true;
                    Monitor.PulseAll(_startupGate);
                }
            }

            if (_startupError is not null)
            {
                return;
            }
        }

        if (_options.UseRealTimePriority)
        {
            _ = ThreadBoost.TryEnableTimeCritical();
        }

        WarmUp();
        _collector.MarkStarted();

        while (Volatile.Read(ref _state) < 2)
        {
            HighResolutionTimer? due = null;
            long dueVersion = 0;
            long nextDeadline = 0;
            WaitSettings nextSettings = default;
            bool hasNext = false;

            lock (_gate)
            {
                if (_heap.Count > 0)
                {
                    long now = HiResClock.Now();
                    HighResolutionTimer top = _heap.Peek();
                    if (top.DueTimestamp - now <= 0)
                    {
                        due = _heap.ExtractMin();
                        dueVersion = due.ArmVersion;
                    }
                    else
                    {
                        hasNext = true;
                        nextDeadline = top.DueTimestamp;
                        nextSettings = top.Settings;
                    }
                }
                else
                {
                    // 空闲。Set/Reset 都只在本锁内发生，而任何要改堆的手必须先拿到同一把锁，
                    // 因此"判空 → 复位 → 出锁等待"不会丢唤醒。
                    _wake.Reset();
                }
            }

            if (due is not null)
            {
                FireOne(due, dueVersion);
                continue;
            }

            if (!hasNext)
            {
                _wake.Wait(Timeout.Infinite);
                continue;
            }

            WaitSettings settings = nextSettings with { Wake = _wake };
            if (WaitEngine.WaitUntil(nextDeadline, settings))
            {
                continue;
            }

            // 被打断（插队 / 停表 / 释放）：回到循环顶重新计算堆顶。
            lock (_gate)
            {
                _wake.Reset();
            }
        }

        Interlocked.Exchange(ref _state, 3);
    }

    /// <summary>
    /// 预热：把等待路径的 JIT、时钟读、本线程的内核等待句柄都先跑一遍。
    /// 否则第一拍会被分层编译和句柄创建拖出几百微秒，看起来像"第一拍特别不准"。
    /// </summary>
    private static void WarmUp()
    {
        WaitSettings spin = WaitSettings.Spin;
        for (int i = 0; i < 3; i++)
        {
            _ = WaitEngine.WaitUntil(HiResClock.Add(HiResClock.Now(), 10), spin);
        }

        _ = WaitEngine.WaitUntil(HiResClock.Add(HiResClock.Now(), HiResClock.ToTicksFromMilliseconds(1)), WaitSettings.HybridAdaptive(AdaptiveSpinWindow.Shared));
        _ = WaitEngine.WaitUntil(HiResClock.Now(), WaitSettings.KernelOnly);
    }

    /// <summary>触发一个已到期定时器，并按"无漂移"规则算出下一拍到期点。</summary>
    [System.Runtime.CompilerServices.MethodImpl(JitHints.AggressiveOptimization)]
    private void FireOne(HighResolutionTimer timer, long dueVersion)
    {
        long dueTimestamp = timer.DueTimestamp;
        long firedAt = HiResClock.Now();

        // PeriodTicks 为 0 意味着 Change(due, 0) 把它改成了单次触发。
        bool repeat = timer.AutoReset && timer.PeriodTicks > 0;
        long nextDue = 0;
        int missed = 0;

        if (repeat)
        {
            long periodTicks = timer.PeriodTicks;
            long late = firedAt - dueTimestamp;
            int skippedTimes;
            if (late > 0)
            {
                // 用除法一次算出跳过几拍，绝不循环追赶：回调超时越久，循环追赶越慢，容易雪崩。
                long skipped = late / periodTicks;
                skippedTimes = skipped > int.MaxValue ? int.MaxValue : (int)skipped;
            }
            else
            {
                skippedTimes = 0;
            }

            missed = skippedTimes;
            nextDue = HiResClock.Add(dueTimestamp, ((long)missed + 1) * periodTicks);
            if (nextDue - firedAt <= 0)
            {
                nextDue = HiResClock.Add(firedAt, periodTicks);
            }
        }

        double latenessNanos = HiResClock.ToNanoseconds(firedAt - dueTimestamp);
        Interlocked.Add(ref _totalFiredTicks, 1);
        _collector.Record(latenessNanos, double.NaN, missed, firedAt);

        // 回调跑完之后再入堆：这样回调耗时会被算进"漏了几拍"，而堆里不会同时存在两份同一定时器。
        timer.FireCore(dueTimestamp, firedAt, latenessNanos, missed);

        if (repeat)
        {
            lock (_gate)
            {
                // 版本号一致才挂回：期间若发生过 Stop/Start（Disarm/Arm 都会递增版本），
                // 堆里的归属就交给那一次调用负责，这里绝不重复入堆。
                if (timer.IsArmed && timer.ArmVersion == dueVersion && Volatile.Read(ref _state) < 2)
                {
                    timer.DueTimestamp = nextDue;
                    _heap.Add(timer);
                }
            }
        }
        else
        {
            timer.MarkStoppedAfterOneShot();
        }
    }
}

/// <summary>
/// 调度器配置。
/// </summary>
public sealed class HighResolutionSchedulerOptions
{
    /// <summary>调度器名 / 线程名。默认 null（自动编号）。</summary>
    public string? Name { get; set; }

    /// <summary>挂载定时器未显式指定策略时的默认等待模式。默认 <see cref="WaitMode.Hybrid"/>。</summary>
    public WaitMode Mode { get; set; } = WaitMode.Hybrid;

    /// <summary>调度线程优先级。默认 <see cref="ThreadPriority.Highest"/>。</summary>
    public ThreadPriority ThreadPriority { get; set; } = ThreadPriority.Highest;

    /// <summary>
    /// 是否把调度线程提升到 Windows 的 <c>THREAD_PRIORITY_TIME_CRITICAL</c>（15）。默认 <see langword="false"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ThreadPriority"/> 最高只能表达 <see cref="ThreadPriority.Highest"/>（基础优先级 10），
    /// 而 Windows 的 15 级能让线程抢占普通线程的时间片——本机实测 p99 从 20µs 降到 2µs、
    /// p99.9 从 100µs 降到 50µs。
    /// </para>
    /// <para>
    /// 高优先级自旋可能使低优先级线程饥饿，等待它们的锁可能发生优先级反转；不独占核心，也不免除 GC。
    /// </para>
    /// <para>非 Windows 平台忽略此设置（Linux 需要 <c>SCHED_FIFO</c> + root/CAP_SYS_NICE）。</para>
    /// </remarks>
    public bool UseRealTimePriority { get; set; }

    /// <summary>绑定的逻辑处理器编号，null 不绑核；Windows 为组内编号，Linux 为 CPU ID；失败时 Start 抛异常。</summary>
    public int? ProcessorIndex { get; set; }

    /// <summary>Windows 处理器组，默认 0；Linux 必须为 0。需同时指定 ProcessorIndex；不代表核心独占或免 GC。</summary>
    public ushort ProcessorGroup { get; set; }

    internal HighResolutionSchedulerOptions Clone() => new()
    {
        Name = Name,
        Mode = Mode,
        ThreadPriority = ThreadPriority,
        UseRealTimePriority = UseRealTimePriority,
        ProcessorIndex = ProcessorIndex,
        ProcessorGroup = ProcessorGroup,
    };
}
