using System.Runtime.InteropServices;
using System.Runtime;

namespace OODD.Timer;

/// <summary>
/// 本进程的平台能力快照。用 <see cref="HighResolutionWait.GetPlatformInfo"/> 获取。
/// </summary>
/// <param name="OsDescription">运行时自述的操作系统（<see cref="RuntimeInformation.OSDescription"/>）。</param>
/// <param name="Architecture">进程指令集架构。</param>
/// <param name="Is64BitProcess">是否 64 位进程。</param>
/// <param name="ProcessorCount">可用逻辑核数。</param>
/// <param name="ClockFrequency">单调时钟频率（Hz）。Windows/Linux/macOS 通常为 10_000_000。</param>
/// <param name="NanosecondsPerTick">一个时钟 tick 等于多少纳秒——这就是本库理论上无法跨越的分辨率地板。</param>
/// <param name="HighResolutionClock">单调时钟是否为硬件高分辨率时钟（应为 true；false 表示退化成了 wall-clock）。</param>
/// <param name="KernelHighResolutionWaits">内核（非自旋）等待是否具备亚毫秒能力。</param>
/// <param name="KernelWaitImplementation">内核等待的实际实现说明。</param>
/// <param name="GcLatencyMode">当前 GC 延迟模式。</param>
/// <param name="IsServerGc">是否启用了服务器 GC（服务器 GC 的停顿更长、更难压到 µs 级）。</param>
public sealed record TimerPlatformInfo(
    string OsDescription,
    Architecture Architecture,
    bool Is64BitProcess,
    int ProcessorCount,
    long ClockFrequency,
    double NanosecondsPerTick,
    bool HighResolutionClock,
    bool KernelHighResolutionWaits,
    string KernelWaitImplementation,
    string GcLatencyMode,
    bool IsServerGc)
{
    /// <summary>采集当前进程的能力。开销极低（只读若干静态量），可以在启动时调用并写进日志。</summary>
    internal static TimerPlatformInfo Capture() =>
        new(
            Platform.OsDescription,
            Platform.OsArchitecture,
            Environment.Is64BitProcess,
            Environment.ProcessorCount,
            HiResClock.Frequency,
            HiResClock.NanosecondsPerTick,
            HiResClock.IsHighResolution,
            KernelWait.SupportsHighResolution,
            KernelWait.Implementation,
            GCSettings.LatencyMode.ToString(),
            GCSettings.IsServerGC);

    /// <summary>
    /// 基于本机能力给出的等待策略建议。
    /// </summary>
    /// <remarks>
    /// 判据只有一条：内核等待是否亚毫秒。
    /// 具备时 <see cref="WaitMode.Hybrid"/> 能同时拿到精度和低 CPU；
    /// 不具备时混合模式的自旋窗口会被迫放大到系统节拍量级（本机兜底路径实测 15.6ms），
    /// 此时不如显式选择 <see cref="WaitMode.Spin"/>（精度更好）或 <see cref="WaitMode.Kernel"/>（更省 CPU 但精度差）。
    /// </remarks>
    public WaitMode RecommendedMode => KernelHighResolutionWaits ? WaitMode.Hybrid : WaitMode.Spin;

    /// <summary>一行式摘要，适合写进启动日志。</summary>
    public override string ToString() =>
        $"{OsDescription} ({Architecture}, {(Is64BitProcess ? "x64" : "x86")}, {ProcessorCount} CPUs) | " +
        $"clock={ClockFrequency}Hz, tick={NanosecondsPerTick:F1}ns, hiResClock={HighResolutionClock} | " +
        $"kernelWaits={KernelWaitImplementation} (subMs={KernelHighResolutionWaits}) | " +
        $"gc={GcLatencyMode}{(IsServerGc ? "/server" : string.Empty)}";
}

/// <summary>
/// 运行时调优开关：Windows 系统时钟节拍、GC 延迟模式。
/// </summary>
/// <remarks>
/// 这两项都是<b>进程级</b>设置，会同时影响同进程里所有代码，因此默认全部关闭，由你显式调用。
/// <see cref="RequestGcLatencyMode"/> 改的是尾延迟（p99/max）——本库定时器的 p50 已经落在
/// 时钟粒度以内，尖峰来自 GC 停顿和内核抢占。<see cref="RequestSystemTimerResolution"/> 改的是
/// <b>走系统节拍的那些路径</b>的 p50（实测 15.56ms → 1.99ms），对本库的高分辨率等待路径没有影响。
/// </remarks>
public static class RuntimeTuning
{
    /// <summary>
    /// 引用计数式地进入 <see cref="GCLatencyMode.SustainedLowLatency"/>。
    /// </summary>
    /// <remarks>
    /// 多个定时器/多处调用共用一个计数器，最后一个释放时才恢复原模式，
    /// 因此不会出现"A 的 Dispose 把 B 正在依赖的模式改回去"的问题。
    /// <para>
    /// 本机实测参考（1ms 节拍 × 3000 拍、纯自旋、后台持续分配）：默认模式下 p95 迟到 262µs、
    /// max 7.0ms、18 拍被合并；进入该模式后 p95 131µs、max 0.51ms、0 拍被合并。
    /// 代价是整个进程的托管内存占用上升。
    /// </para>
    /// </remarks>
    /// <param name="mode">要请求的模式，默认 <see cref="GCLatencyMode.SustainedLowLatency"/>。</param>
    /// <returns>释放时恢复原模式的 <see cref="IDisposable"/>；平台不允许设置时返回一个空操作对象。</returns>
    public static IDisposable RequestGcLatencyMode(
#if NET40
        GCLatencyMode mode)
#else
        GCLatencyMode mode = GCLatencyMode.SustainedLowLatency)
#endif
    {
#if NET40
        // .NET Framework 4.0 的 GCLatencyMode 里没有 SustainedLowLatency（4.5 才加入），
        // 用 LowLatency 代替语义又完全不同（它禁止 gen2 回收、可能 OOM），所以这里明确拒绝而不是静默降级。
        _ = mode;
        throw new PlatformNotSupportedException(
            "net40 的 GC 没有 SustainedLowLatency 模式；请改用 net45+ / netstandard2.0 目标，或关闭 PreferSustainedLowLatencyGc。");
#else
        lock (s_gcGate)
        {
            if (s_gcRefCount == 0)
            {
                s_savedGcMode = GCSettings.LatencyMode;
                try
                {
                    GCSettings.LatencyMode = mode;
                    s_gcModeApplied = true;
                }
                catch (InvalidOperationException)
                {
                    // 处于 NoGCRegion 等不允许修改的状态：记录为"未生效"，仍然返回可释放对象，
                    // 让调用方的 using 语义保持一致。
                    s_gcModeApplied = false;
                }
            }
            else if (!s_gcModeApplied)
            {
                try
                {
                    GCSettings.LatencyMode = mode;
                    s_gcModeApplied = true;
                }
                catch (InvalidOperationException)
                {
                    s_gcModeApplied = false;
                }
            }

            s_gcRefCount++;
            return new GcScope();
        }
#endif
    }

    private static readonly object s_gcGate = new();
    private static readonly object s_windowsGate = new();
    private static int s_gcRefCount;
    private static GCLatencyMode s_savedGcMode = GCLatencyMode.Interactive;
    private static bool s_gcModeApplied;
    private static int s_windowsPeriodRefCount;
    private static uint s_windowsPeriodMs;

    /// <summary><see cref="RequestGcLatencyMode"/> 当前的引用计数（诊断用）。</summary>
    public static int GcLatencyRequestCount => Volatile.Read(ref s_gcRefCount);

    /// <summary>Windows 时钟节拍请求的引用计数（诊断用）。</summary>
    public static int SystemTimerResolutionRequestCount => Volatile.Read(ref s_windowsPeriodRefCount);

    /// <summary>
    /// Windows 专用：把系统定时器节拍请求到 <paramref name="resolutionMs"/> 毫秒。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是传统 <c>winmm!timeBeginPeriod</c> 的做法：把系统时钟节拍从 15.6ms 请求到 1ms。
    /// 本机（Windows 11 10.0.26200）实测<b>有效</b>，收益很大，但天花板仍在毫秒量级：
    /// <list type="bullet">
    /// <item><description><c>Thread.Sleep(1)</c> 实际睡时长：p50 15.56ms → <b>1.99ms</b>（min 9.87 → 1.03，max 17.27 → 2.67；各 600 次采样）。</description></item>
    /// <item><description>1ms 周期的 <c>System.Threading.Timer</c> 每拍迟到：p50 14.55ms → <b>0.76ms</b>（max 16.48 → 3.17；1500 拍）。</description></item>
    /// <item><description><see cref="IDisposable.Dispose"/> 让引用计数归零后回到 p50 15.55ms，完全可逆。</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// 本库的默认路径<b>不依赖它</b>：<see cref="KernelWait"/> 用的是
    /// <c>CreateWaitableTimerExW(CREATE_WAITABLE_TIMER_HIGH_RESOLUTION)</c>，触发点不受系统节拍量化，
    /// 配合尾部自旋可以落到 µs 甚至亚 µs。而本方法改的是<b>整个进程（乃至整机）</b>的定时器分辨率，
    /// 会让其它线程的唤醒也更频繁（更耗电），所以默认不调用。值得显式调用的两种情形：
    /// <list type="bullet">
    /// <item><description>平台不支持高分辨率等待句柄、本库退回托管 sleep（此时它能把手动自旋窗口缩小一个量级，见 <see cref="AdaptiveSpinWindow"/>）。</description></item>
    /// <item><description>同进程里还有别的代码在吃粗节拍——<c>PeriodicTimer</c>、第三方库里的 <c>Thread.Sleep</c>。</description></item>
    /// </list>
    /// 非 Windows 平台调用返回空操作对象。若系统拒绝请求（winmm 未返回 <c>TIMERR_NOERROR</c>），
    /// 同样返回空操作对象且<b>不计数</b>：用 <see cref="SystemTimerResolutionRequestCount"/> 是否为 0 判断请求有没有被接受。
    /// </para>
    /// </remarks>
    /// <param name="resolutionMs">请求的节拍，单位毫秒，通常是 1。</param>
    /// <returns>释放时（引用计数归零）恢复原节拍的 <see cref="IDisposable"/>。</returns>
    public static IDisposable RequestSystemTimerResolution(uint resolutionMs = 1)
    {
        if (!Platform.IsWindows)
        {
            return NoOpScope.Instance;
        }

        lock (s_windowsGate)
        {
            if (s_windowsPeriodRefCount == 0)
            {
                if (WindowsNative.TimeBeginPeriod(resolutionMs) != TimerrNoError)
                {
                    // 请求被系统拒绝（TIMERR_NOCANDO 等）。此时不能开始计数——否则最后一个
                    // Dispose 会去调 TimeEndPeriod，撤销一次从未成立的请求。
                    return NoOpScope.Instance;
                }

                s_windowsPeriodMs = resolutionMs;
            }

            s_windowsPeriodRefCount++;
            return new WindowsTimerScope();
        }
    }

    /// <summary><c>TIMERR_NOERROR</c>：winmm 计时器请求被接受。</summary>
    private const uint TimerrNoError = 0;

    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class GcScope : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            lock (s_gcGate)
            {
                if (_done)
                {
                    return;
                }

                _done = true;
                s_gcRefCount--;
                if (s_gcRefCount <= 0)
                {
                    s_gcRefCount = 0;
                    if (s_gcModeApplied)
                    {
                        try
                        {
                            GCSettings.LatencyMode = s_savedGcMode;
                        }
                        catch (InvalidOperationException)
                        {
                            // 无法恢复时保持现状，不影响正确性。
                        }

                        s_gcModeApplied = false;
                    }
                }
            }
        }
    }

    private sealed class WindowsTimerScope : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            lock (s_windowsGate)
            {
                if (_done)
                {
                    return;
                }

                _done = true;
                s_windowsPeriodRefCount--;
                if (s_windowsPeriodRefCount <= 0)
                {
                    s_windowsPeriodRefCount = 0;
                    // 计数已归零，无论 winmm 返回什么都不再重试：TimeEndPeriod 只在"从未 Begin"
                    // 或参数不匹配时失败，这两种情况都没有需要恢复的状态。
                    _ = WindowsNative.TimeEndPeriod(s_windowsPeriodMs);
                }
            }
        }
    }

    private static class WindowsNative
    {
        // winmm 的导出名是小写开头的 timeBeginPeriod / timeEndPeriod。
        // DllImport 的入口点查找区分大小写，直接写成 TimeBeginPeriod 会在调用时抛
        // EntryPointNotFoundException（本机踩过，靠文档示例跑出来的）。
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = false)]
        public static extern uint TimeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = false)]
        public static extern uint TimeEndPeriod(uint milliseconds);
    }
}
