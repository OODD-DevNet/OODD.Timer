using System.Runtime.CompilerServices;

namespace OODD.Timer;

/// <summary>
/// 亚毫秒级等待原语：<see cref="Sleep(TimeSpan, WaitMode)"/>、<see cref="SleepAsync"/>、
/// 内核唤醒延迟标定 <see cref="MeasureKernelWakeLatency"/>。
/// </summary>
/// <remarks>
/// <para>
/// 本类是 <see cref="HighResolutionTimer"/> 的底层机制，也可以单独使用——
/// 当你需要"睡到某个绝对时刻"而不是"周期性回调"时，直接用这里的方法。
/// </para>
/// <para>
/// 所有等待都以"绝对到期时间戳"（<see cref="HiResClock.Now"/> 同一时钟）为目标。
/// 请以 <c>deadline = HiResClock.Now() + HiResClock.ToTicks(period)</c> 起手，
/// 然后 <c>deadline = HiResClock.Add(deadline, periodTicks)</c> 逐拍推进——
/// 这样误差不会被累加进去（详见 <see cref="HighResolutionTimer"/> 的漂移说明）。
/// </para>
/// </remarks>
public static class HighResolutionWait
{
    /// <summary>未显式指定窗口时的默认自旋窗口起点（纳秒）。</summary>
    public const double DefaultSpinWindowNanoseconds = 1_000_000;

    /// <summary>自旋窗口的下限（纳秒）：小于这个值没有意义，因为一次读时钟就要几十纳秒。</summary>
    public const double MinSpinWindowNanoseconds = 2_000;

    /// <summary>自旋窗口的上限（纳秒）：超过它说明平台内核等待太粗，宁可放弃粗等待阶段全程自旋。</summary>
    public const double MaxSpinWindowNanoseconds = 32_000_000;

    /// <summary>全进程共享的自适应窗口（供 <see cref="Sleep(TimeSpan, WaitMode)"/> 等静态方法使用）。</summary>
    private static readonly AdaptiveSpinWindow s_sharedWindow = new(
        DefaultSpinWindowNanoseconds, MinSpinWindowNanoseconds, MaxSpinWindowNanoseconds);

    /// <summary>当前共享自适应窗口的实际长度（纳秒）。<see cref="WaitMode.Hybrid"/> 标定后会稳定在这个值附近。</summary>
    public static double SharedSpinWindowNanoseconds => s_sharedWindow.ValueNanoseconds;

    /// <summary>把共享自适应窗口复位到初始值（在平台条件发生变化后调用，例如切入了电源计划高性能模式）。</summary>
    public static void ResetSharedSpinWindow() => s_sharedWindow.Reset();

    /// <summary>
    /// 让当前线程精确睡眠 <paramref name="duration"/>。
    /// </summary>
    /// <param name="duration">睡眠时长；&lt;= <see cref="TimeSpan.Zero"/> 时立即返回。</param>
    /// <param name="mode">等待策略，默认 <see cref="WaitMode.Hybrid"/>。</param>
    /// <remarks>
    /// 在 <see cref="WaitMode.Spin"/> / <see cref="WaitMode.Hybrid"/> 下，这段时间当前线程是忙的，
    /// 不要在线程池线程上调用（会占住一个工作线程）。长时间睡眠请改用
    /// <see cref="WaitMode.Kernel"/> 或 <see cref="HighResolutionTimer"/>。
    /// </remarks>
    public static void Sleep(TimeSpan duration, WaitMode mode = WaitMode.Hybrid)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        Until(HiResClock.Now() + HiResClock.ToTicks(duration), mode);
    }

    /// <summary>让当前线程精确睡眠到绝对到期时间戳。</summary>
    /// <param name="deadlineTimestamp">绝对到期时间戳（<see cref="HiResClock.Now"/> 同一时钟）。</param>
    /// <param name="mode">等待策略。</param>
    /// <returns><see langword="true"/> 表示已经到达到期点。</returns>
    public static bool Until(long deadlineTimestamp, WaitMode mode = WaitMode.Hybrid)
    {
        WaitSettings settings = mode switch
        {
            WaitMode.Spin => WaitSettings.Spin,
            WaitMode.Kernel => WaitSettings.KernelOnly,
            _ => WaitSettings.HybridAdaptive(s_sharedWindow),
        };

        return WaitEngine.WaitUntil(deadlineTimestamp, settings);
    }

    /// <summary>全程自旋到绝对到期时间戳——精度最高，等待期间占满一个逻辑核。</summary>
    /// <param name="deadlineTimestamp">绝对到期时间戳。</param>
    /// <remarks>等价于 <c>Until(deadlineTimestamp, WaitMode.Spin)</c>，热路径少一层分支。</remarks>
    [MethodImpl(InlineHint.AggressiveInlining)]
    public static void SpinUntil(long deadlineTimestamp) => WaitEngine.SpinTo(deadlineTimestamp, wake: null, CancellationToken.None);

    /// <summary>
    /// 异步精确睡眠：到期后完成返回的 <see cref="Task"/>。
    /// </summary>
    /// <remarks>
    /// 等待期间占用的是共享调度线程而不是线程池线程。<b>完成通知要经过一次线程池派发</b>，
    /// 因此回调被真正执行的时刻会比到期时间晚一个线程池排队延迟（通常几微秒到几百微秒，
    /// 繁忙时更久）。若你需要"在到期那一刻的亚微秒精度上做决定"，
    /// 请用 <see cref="HighResolutionTimer"/> 的内联回调，或 <see cref="HighResolutionTimer.WaitForNextTickAsync"/>。
    /// </remarks>
    /// <param name="duration">睡眠时长。</param>
    /// <param name="mode">等待策略。</param>
    /// <param name="cancellationToken">取消令牌；取消时任务以 <see cref="OperationCanceledException"/> 结束。</param>
    public static Task SleepAsync(TimeSpan duration, WaitMode mode = WaitMode.Hybrid, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return TaskCompat.FromCanceled(cancellationToken);
        }

        if (duration <= TimeSpan.Zero)
        {
            return TaskCompat.CompletedTask;
        }

        return HighResolutionScheduler.Shared.SleepAsync(duration, mode, cancellationToken);
    }

    /// <summary>读取本进程的平台能力快照，用于诊断"在这台机器上到底能精度到多少"。</summary>
    /// <returns>描述时钟频率、内核等待实现与推荐模式的信息对象。</returns>
    public static TimerPlatformInfo GetPlatformInfo() => TimerPlatformInfo.Capture();

    /// <summary>
    /// 实测当前平台的"内核唤醒延迟"分布，并据此推荐 <see cref="WaitMode.Hybrid"/> 的自旋窗口。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 自适应窗口本来会在运行中自动标定这个值；但标定期内（前若干拍）的定时器可能偏晚触发，
    /// 所以如果你在延迟敏感的系统里上线，建议先跑一次本方法，
    /// 把 <see cref="KernelWakeLatency.RecommendedSpinWindowNanoseconds"/> 写进
    /// <see cref="HighResolutionTimerOptions.SpinWindowNanoseconds"/> 并把
    /// <see cref="HighResolutionTimerOptions.AdaptiveSpinWindow"/> 设为 <see langword="false"/>。
    /// </para>
    /// <para>必须在目标部署机器上跑，不要在开发机上跑完就把数字抄到生产配置里。</para>
    /// </remarks>
    /// <param name="sampleCount">采样次数，默认 300。</param>
    /// <param name="targetInterval">每次等待的目标时长，默认 1ms。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>唤醒延迟分布统计与推荐的自旋窗口。</returns>
    public static KernelWakeLatency MeasureKernelWakeLatency(int sampleCount = 300, TimeSpan? targetInterval = null, CancellationToken cancellationToken = default)
    {
        Guard.NotLessThan(sampleCount, 16, nameof(sampleCount));
        TimeSpan target = targetInterval ?? TimeSpan.FromMilliseconds(1);
        long targetTicks = HiResClock.ToTicks(target);

        var lateness = new long[sampleCount];
        var thread = new Thread(() =>
        {
            for (int i = 0; i < lateness.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                long deadline = HiResClock.Add(HiResClock.Now(), targetTicks);
                KernelWait.Sleep(targetTicks);
                lateness[i] = HiResClock.Now() - deadline;
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "OODD.Timer.LatencyProbe",
        };

        thread.Start();
        thread.Join();

        return new KernelWakeLatency(lateness, target);
    }
}

/// <summary>
/// 等待策略参数（内部传递用，避免为每个定时器分配一个设置对象）。
/// </summary>
internal readonly struct WaitSettings
{
    /// <summary>等待策略。</summary>
    public required WaitMode Mode { get; init; }

    /// <summary>自适应自旋窗口；为 null 时改用 <see cref="FixedWindowTicks"/>。</summary>
    public AdaptiveSpinWindow? AdaptiveWindow { get; init; }

    /// <summary>固定自旋窗口（tick）。仅在 <see cref="AdaptiveWindow"/> 为 null 时生效。</summary>
    public long FixedWindowTicks { get; init; }

    /// <summary>"紧自旋"阶段的长度（tick）：剩余时间小于它就只读时钟，不做任何让出。</summary>
    public long FineSpinTicks { get; init; }

    /// <summary>粗等待阶段单次内核等待的最长时长（tick），决定打断信号的最早响应延迟。</summary>
    public long ChunkTicks { get; init; }

    /// <summary>可以提前结束等待的信号；置位后等待立即返回。</summary>
    public ManualResetEventSlim? Wake { get; init; }

    /// <summary>取消令牌。</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>纯自旋设置。</summary>
    public static WaitSettings Spin { get; } = new()
    {
        Mode = WaitMode.Spin,
        FineSpinTicks = 0,
        ChunkTicks = 0,
    };

    /// <summary>纯内核等待设置（零 CPU，精度受平台唤醒延迟限制）。</summary>
    public static WaitSettings KernelOnly { get; } = new()
    {
        Mode = WaitMode.Kernel,
        ChunkTicks = HiResClock.ToTicks(2_000_000),
        FineSpinTicks = 0,
    };

    /// <summary>混合等待 + 自适应自旋窗口。</summary>
    /// <param name="window">该路径专属的自适应窗口实例。</param>
    /// <param name="chunkTicks">粗等待分段长度；&lt;= 0 用默认 2ms。</param>
    /// <param name="fineTicks">紧自旋阈值；&lt;= 0 用默认 2µs。</param>
    public static WaitSettings HybridAdaptive(AdaptiveSpinWindow window, long chunkTicks = 0, long fineTicks = 0) => new()
    {
        Mode = WaitMode.Hybrid,
        AdaptiveWindow = window,
        ChunkTicks = chunkTicks > 0 ? chunkTicks : HiResClock.ToTicks(2_000_000),
        FineSpinTicks = fineTicks > 0 ? fineTicks : HiResClock.ToTicks(2_000),
    };

    /// <summary>混合等待 + 固定自旋窗口（关掉自适应时用）。</summary>
    /// <param name="windowTicks">固定窗口长度（tick）。</param>
    /// <param name="chunkTicks">粗等待分段长度；&lt;= 0 用默认 2ms。</param>
    /// <param name="fineTicks">紧自旋阈值；&lt;= 0 用默认 2µs。</param>
    public static WaitSettings HybridFixed(long windowTicks, long chunkTicks = 0, long fineTicks = 0) => new()
    {
        Mode = WaitMode.Hybrid,
        AdaptiveWindow = null,
        FixedWindowTicks = windowTicks > 0 ? windowTicks : HiResClock.ToTicks(HighResolutionWait.MinSpinWindowNanoseconds),
        ChunkTicks = chunkTicks > 0 ? chunkTicks : HiResClock.ToTicks(2_000_000),
        FineSpinTicks = fineTicks > 0 ? fineTicks : HiResClock.ToTicks(2_000),
    };

    /// <summary>纯内核等待 + 自定义分段长度。</summary>
    /// <param name="chunkTicks">分段长度（tick）。</param>
    public static WaitSettings Kernel(long chunkTicks) => KernelOnly with { ChunkTicks = chunkTicks > 0 ? chunkTicks : HiResClock.ToTicks(2_000_000) };
}

/// <summary>
/// 自适应自旋窗口：观测内核唤醒延迟，动态决定"最后一段时间必须自旋多长"。
/// </summary>
/// <remarks>
/// 这是 <see cref="WaitMode.Hybrid"/> 能做到"既省 CPU 又不迟到"的关键。逻辑如下：
/// <list type="number">
/// <item>粗等待阶段计划"在到期前 <c>window</c> 醒来"，然后自旋到点。</item>
/// <item>若内核实际比计划晚了 <c>L</c>（L 就是内核唤醒延迟），而 <c>L &gt; window</c>，
/// 那么本次就会在到期之后才醒来——迟到 <c>L - window</c>。</item>
/// <item>所以窗口必须 ≥ 内核唤醒延迟的 p99。本类观测每次唤醒延迟：
/// 变大时立即跟上（1.25 倍余量），明显偏大时以每次 1/64 的速度缓慢收缩。</item>
/// </list>
/// 只增长不收缩会退化成长期自旋，因此收缩同样重要；观测样本越多，窗口越贴合真实分布。
/// </remarks>
internal sealed class AdaptiveSpinWindow
{
    /// <summary>全进程共享实例，供 <see cref="HighResolutionWait"/> 的静态方法使用。</summary>
    public static AdaptiveSpinWindow Shared { get; } = new(
        HighResolutionWait.DefaultSpinWindowNanoseconds,
        HighResolutionWait.MinSpinWindowNanoseconds,
        HighResolutionWait.MaxSpinWindowNanoseconds);

    private readonly long _floorTicks;
    private readonly long _ceilingTicks;
    private long _valueTicks;
    private long _observedMaxTicks;

    /// <summary>
    /// 创建窗口。
    /// </summary>
    /// <param name="initialNanoseconds">初始窗口长度（纳秒）。</param>
    /// <param name="floorNanoseconds">窗口下限。</param>
    /// <param name="ceilingNanoseconds">窗口上限；触顶时精度会开始让位于 CPU。</param>
    public AdaptiveSpinWindow(double initialNanoseconds, double floorNanoseconds, double ceilingNanoseconds)
    {
        _floorTicks = HiResClock.ToTicks(Math.Max(floorNanoseconds, 1));
        _ceilingTicks = Math.Max(_floorTicks, HiResClock.ToTicks(Math.Max(ceilingNanoseconds, 1)));
        _valueTicks = MathCompat.Clamp(HiResClock.ToTicks(Math.Max(initialNanoseconds, 1)), _floorTicks, _ceilingTicks);
    }

    /// <summary>当前窗口长度（tick）。</summary>
    public long ValueTicks => Volatile.Read(ref _valueTicks);

    /// <summary>当前窗口长度（纳秒）。</summary>
    public double ValueNanoseconds => HiResClock.ToNanoseconds(ValueTicks);

    /// <summary>历史上观测到的最大内核唤醒延迟（纳秒）。</summary>
    public double ObservedMaxWakeLatencyNanoseconds => HiResClock.ToNanoseconds(Volatile.Read(ref _observedMaxTicks));

    /// <summary>观测次数（用于判断标定是否已经稳定）。</summary>
    public long ObservationCount => Volatile.Read(ref _observationCount);

    private long _observationCount;

    /// <summary>把窗口复位到"下限与初始值的较大者"，并清零历史最大值。</summary>
    public void Reset()
    {
        Volatile.Write(ref _observedMaxTicks, 0);
        Interlocked.Exchange(ref _observationCount, 0);
        Interlocked.Exchange(ref _valueTicks, Math.Max(_floorTicks, HiResClock.ToTicks(HighResolutionWait.DefaultSpinWindowNanoseconds)));
    }

    /// <summary>
    /// 记录一次内核唤醒延迟。
    /// </summary>
    /// <param name="wakeLatenessTicks">"实际醒来时刻 - 计划醒来时刻"，单位 tick；早醒传 0 或负数。</param>
    public void Observe(long wakeLatenessTicks)
    {
        Interlocked.Increment(ref _observationCount);
        if (wakeLatenessTicks > 0)
        {
            long observedMax = Volatile.Read(ref _observedMaxTicks);
            while (wakeLatenessTicks > observedMax
                   && Interlocked.CompareExchange(ref _observedMaxTicks, wakeLatenessTicks, observedMax) != observedMax)
            {
                observedMax = Volatile.Read(ref _observedMaxTicks);
            }
        }

        long current = ValueTicks;

        // 需要的窗口 = 延迟 + 25% 余量 + 下限，保证"计划醒来点"总在真实延迟之外。
        long needed = wakeLatenessTicks + (wakeLatenessTicks >> 2) + _floorTicks;
        if (needed > current)
        {
            long target = Math.Min(needed, _ceilingTicks);
            _ = Interlocked.CompareExchange(ref _valueTicks, target, current);
            return;
        }

        // 明显偏大才收缩（保留 4 倍余量），每次只减 1/64，避免和增长互相抖。
        if (current > _floorTicks && needed * 4 < current)
        {
            long decay = Math.Max(1, current >> 6);
            long shrunk = Math.Max(_floorTicks, current - decay);
            _ = Interlocked.CompareExchange(ref _valueTicks, shrunk, current);
        }
    }

    /// <summary>诊断用的一行文本。</summary>
    public override string ToString() =>
        $"spinWindow={ValueNanoseconds:F0}ns observations={ObservationCount} maxWakeLatency={ObservedMaxWakeLatencyNanoseconds:F0}ns";
}

/// <summary>
/// 等待引擎：把"睡到绝对时刻"拆成粗等待 + 中速自旋 + 紧自旋三段，逐段收紧精度。
/// </summary>
internal static class WaitEngine
{
    /// <summary>
    /// 等待直到 <paramref name="deadlineTimestamp"/>。
    /// </summary>
    /// <param name="deadlineTimestamp">绝对到期时间戳。</param>
    /// <param name="settings">等待策略。</param>
    /// <returns><see langword="true"/> 表示已到点；<see langword="false"/> 表示被 <see cref="WaitSettings.Wake"/>
    /// 或 <see cref="WaitSettings.CancellationToken"/> 提前打断。</returns>
    public static bool WaitUntil(long deadlineTimestamp, in WaitSettings settings)
    {
        switch (settings.Mode)
        {
            case WaitMode.Spin:
                return SpinTo(deadlineTimestamp, settings.Wake, settings.CancellationToken);

            case WaitMode.Kernel:
                return KernelWait.WaitUntil(deadlineTimestamp, settings.ChunkTicks, settings.Wake);

            default:
                return HybridTo(deadlineTimestamp, settings);
        }
    }

    /// <summary>
    /// 混合等待：内核睡到"到期前一个自旋窗口"，然后自旋到点。
    /// </summary>
    private static bool HybridTo(long deadlineTimestamp, in WaitSettings settings)
    {
        AdaptiveSpinWindow? window = settings.AdaptiveWindow;
        long currentWindow = window?.ValueTicks ?? settings.FixedWindowTicks;
        if (currentWindow <= 0)
        {
            currentWindow = HiResClock.ToTicks(HighResolutionWait.MinSpinWindowNanoseconds);
        }

        while (true)
        {
            long intendedWakePoint = deadlineTimestamp - currentWindow;
            long remaining = intendedWakePoint - HiResClock.Now();
            if (remaining <= 0)
            {
                // 已经在窗口内（或窗口比整个剩余时间还长）：直接进入自旋阶段。
                break;
            }

            if (settings.Wake?.IsSet == true || settings.CancellationToken.IsCancellationRequested)
            {
                return false;
            }

            long chunk = settings.ChunkTicks > 0 && remaining > settings.ChunkTicks ? settings.ChunkTicks : remaining;

            long start = HiResClock.Now();
            KernelWait.Sleep(chunk);
            long lateness = HiResClock.Now() - (start + chunk);
            window?.Observe(lateness);

            long nextWindow = window?.ValueTicks ?? currentWindow;
            if (nextWindow == currentWindow)
            {
                continue;
            }

            currentWindow = nextWindow;
        }

        return SpinTo(deadlineTimestamp, settings.Wake, settings.CancellationToken, settings.FineSpinTicks);
    }

    /// <summary>
    /// 自旋到点。剩余时间较长时用 <see cref="Thread.SpinWait(int)"/>（省功耗、对超线程友好），
    /// 最后 <paramref name="fineSpinTicks"/> 之内只做"读时钟 + 比较"，把触发抖动压到时钟粒度以内。
    /// </summary>
    /// <param name="deadlineTimestamp">绝对到期时间戳。</param>
    /// <param name="wake">提前打断信号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="fineSpinTicks">进入"紧自旋"的阈值；0 表示从一开始就紧自旋。</param>
    /// <returns><see langword="true"/> 表示已到点。</returns>
    public static bool SpinTo(long deadlineTimestamp, ManualResetEventSlim? wake, CancellationToken cancellationToken, long fineSpinTicks = 0)
    {
        if (wake is null && !cancellationToken.CanBeCanceled)
        {
            // 最常见也最紧的路径：不带打断检查，循环体只有一次时钟读。
            while (deadlineTimestamp - HiResClock.Now() > 0)
            {
                if (fineSpinTicks > 0 && deadlineTimestamp - HiResClock.Now() > fineSpinTicks)
                {
                    Thread.SpinWait(24);
                }
            }

            return true;
        }

        while (true)
        {
            long remaining = deadlineTimestamp - HiResClock.Now();
            if (remaining <= 0)
            {
                return true;
            }

            if (wake?.IsSet == true || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (fineSpinTicks > 0 && remaining > fineSpinTicks)
            {
                Thread.SpinWait(24);
            }
        }
    }
}

/// <summary>
/// <see cref="HighResolutionWait.MeasureKernelWakeLatency"/> 的返回结果。
/// </summary>
/// <param name="SampleCount">有效样本数。</param>
/// <param name="TargetInterval">每次等待的目标时长。</param>
/// <param name="MeanLateness">平均唤醒延迟。</param>
/// <param name="P50Lateness">中位数唤醒延迟。</param>
/// <param name="P99Lateness">p99 唤醒延迟。</param>
/// <param name="MaxLateness">最大唤醒延迟。</param>
/// <param name="RecommendedSpinWindowNanoseconds">推荐写进
/// <see cref="HighResolutionTimerOptions.SpinWindowNanoseconds"/> 的自旋窗口。</param>
public sealed record KernelWakeLatency(
    int SampleCount,
    TimeSpan TargetInterval,
    double MeanLateness,
    double P50Lateness,
    double P99Lateness,
    double MaxLateness,
    double RecommendedSpinWindowNanoseconds)
{
    /// <summary>
    /// 由原始样本（相对目标到期点的迟到量，单位 tick）构造统计。
    /// </summary>
    internal KernelWakeLatency(long[] latenessTicks, TimeSpan targetInterval)
        : this(
            latenessTicks.Length,
            targetInterval,
            Mean(latenessTicks),
            Percentile(latenessTicks, 0.50),
            Percentile(latenessTicks, 0.99),
            Percentile(latenessTicks, 1.00),
            Math.Max(HighResolutionWait.MinSpinWindowNanoseconds, Percentile(latenessTicks, 1.00) * 1.25 + 50_000))
    {
    }

    private static double Mean(long[] samples)
    {
        double sum = 0;
        foreach (long sample in samples)
        {
            sum += HiResClock.ToNanoseconds(sample);
        }

        return samples.Length == 0 ? 0 : sum / samples.Length;
    }

    private static double Percentile(long[] samples, double percentile)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        long[] sorted = (long[])samples.Clone();
        Array.Sort(sorted);
        int index = (int)Math.Round(percentile * (sorted.Length - 1), MidpointRounding.AwayFromZero);
        return HiResClock.ToNanoseconds(sorted[MathCompat.Clamp(index, 0, sorted.Length - 1)]);
    }

    /// <summary>人类可读的一行摘要，适合直接写进启动日志。</summary>
    public override string ToString() =>
        $"kernel wake latency over {SampleCount} samples: p50={P50Lateness:F0}ns p99={P99Lateness:F0}ns max={MaxLateness:F0}ns " +
        $"-> recommended spin window={RecommendedSpinWindowNanoseconds:F0}ns";
}
