using System.Runtime.InteropServices;

namespace OODD.Timer;

/// <summary>
/// 内核级（非自旋）阻塞等待的跨平台实现，是 <see cref="WaitMode.Kernel"/> 与
/// <see cref="WaitMode.Hybrid"/> "粗等待阶段"的底座。
/// </summary>
/// <remarks>
/// <para>
/// 各平台落点（选路依据来自本机实测，见 README「为什么必须绕开系统节拍」）：
/// <list type="table">
/// <listheader><description>数字为本机 Windows 11 10.0.26200 / 16 核的测量结果。</description></listheader>
/// <item><term>Windows</term>
/// <description><c>CreateWaitableTimerExW(CREATE_WAITABLE_TIMER_HIGH_RESOLUTION)</c> + <c>WaitForSingleObject(INFINITE)</c>。
/// 该标志（Windows 10 1803+）让定时器以微秒级粒度触发：200µs 目标实测 p50=342µs、p99=820µs。
/// 作为对照，默认节拍下 <c>Thread.Sleep(1)</c> 实测 p50=15.56ms（min 9.87 / max 17.27）。
/// 把系统节拍请求到 1ms（<see cref="RuntimeTuning.RequestSystemTimerResolution"/>）能把它压到
/// p50=1.99ms，但仍停在毫秒量级，而且是进程级副作用——所以本库默认不动系统节拍。</description></item>
/// <item><term>Linux / macOS / iOS / Android / FreeBSD</term>
/// <description>直接走 <see cref="Thread.Sleep(TimeSpan)"/>：CoreCLR 在这些平台用 <c>nanosleep</c> 实现，
/// 请求本身支持亚毫秒（典型数十~数百µs，取决于内核 HZ 与调度器；请在目标机用
/// <see cref="HighResolutionWait.MeasureKernelWakeLatency"/> 复核）。</description></item>
/// <item><term>兜底</term>
/// <description>高分辨率句柄创建失败（老系统、受限环境）时退回托管 sleep，
/// <see cref="AdaptiveSpinWindow"/> 会自动把自旋窗口放大到能覆盖粗节拍：功能不受影响，只是 CPU 占用升高。
/// 这种降级情形下 <see cref="RuntimeTuning.RequestSystemTimerResolution"/> 是值得买的一次调优：
/// 本机实测它把兜底 sleep 的粒度从 15.56ms 压到 1.99ms，自适应窗口也随之缩小。</description></item>
/// </list>
/// </para>
/// <para>
/// 每个首次使用本类的线程持有一个等待句柄（线程本地，随 <see cref="SafeHandle"/> 由 GC 释放）。
/// 定时器工作线程是长期线程，这个成本可以忽略；不要在线程池线程上高频调用本类。
/// </para>
/// </remarks>
internal static class KernelWait
{
    /// <summary>本进程是否具备亚毫秒级内核等待能力（探测结果，运行中可能被降级）。</summary>
    public static bool SupportsHighResolution => Volatile.Read(ref s_supportsHighResolution);

    /// <summary>实际选择的等待实现，供诊断输出使用。</summary>
    public static string Implementation => Volatile.Read(ref s_implementation);

    private static bool s_supportsHighResolution = true;
    private static string s_implementation = "未探测";

    /// <summary>Windows 上高分辨率句柄不可用后，本进程不再尝试创建（0=可用，1=不可用）。</summary>
    private static int s_windowsHandleUnavailable;

    private const int CreateWaitableTimerHighResolution = 0x00000002;
    private const int TimerAllAccess = 0x001F0003;
    private const uint InfiniteTimeout = 0xFFFFFFFF;

    /// <summary><c>WAIT_OBJECT_0</c>：句柄进入已信号态，等待正常结束。</summary>
    private const uint WaitSignaled = 0x00000000;

    static KernelWait()
    {
        if (OperatingSystem.IsWindows())
        {
            s_implementation = "Windows 高分辨率等待句柄 (CreateWaitableTimerExW)";
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsAndroid()
                 || OperatingSystem.IsFreeBSD() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsWatchOS())
        {
            s_implementation = "POSIX nanosleep（经由 CoreCLR Thread.Sleep）";
        }
        else
        {
            s_supportsHighResolution = false;
            s_implementation = "托管 Thread.Sleep 兜底";
        }
    }

    /// <summary>
    /// 阻塞当前线程 <paramref name="durationTicks"/> 个 tick。单次调用，不分段、不可打断，
    /// 因此精度就是本平台的内核等待精度。
    /// </summary>
    /// <param name="durationTicks">等待时长；&lt;= 0 时立即返回。</param>
    public static void Sleep(long durationTicks)
    {
        if (durationTicks <= 0)
        {
            return;
        }

        if (OperatingSystem.IsWindows() && Volatile.Read(ref s_windowsHandleUnavailable) == 0 && SleepWindows(durationTicks))
        {
            return;
        }

        SleepManaged(durationTicks);
    }

    /// <summary>
    /// 阻塞当前线程直到绝对到期时间戳，期间可以被 <paramref name="wake"/> 提前打断。
    /// </summary>
    /// <remarks>
    /// 长等待被切成不超过 <paramref name="chunkTicks"/> 的分段，段间重新计算剩余时间并检查打断信号。
    /// 分段不影响最终精度：最后一段总是"恰好等于剩余时间"。它只决定"最早一次响应打断的延迟"，
    /// 而这段逻辑只在"距离到期还很远"的阶段生效，那里本来就不追求微秒。
    /// </remarks>
    /// <param name="deadlineTimestamp">绝对到期时间戳（与 <see cref="HiResClock.Now"/> 同一时钟）。</param>
    /// <param name="chunkTicks">单段最长等待时长；&lt;= 0 表示不分段。</param>
    /// <param name="wake">可选的提前唤醒信号。</param>
    /// <returns><see langword="true"/> 表示已到期；<see langword="false"/> 表示被 <paramref name="wake"/> 打断。</returns>
    public static bool WaitUntil(long deadlineTimestamp, long chunkTicks, ManualResetEventSlim? wake)
    {
        while (true)
        {
            if (wake?.IsSet == true)
            {
                return false;
            }

            long remaining = deadlineTimestamp - HiResClock.Now();
            if (remaining <= 0)
            {
                return true;
            }

            if (chunkTicks > 0 && remaining > chunkTicks)
            {
                remaining = chunkTicks;
            }

            Sleep(remaining);
        }
    }

    /// <summary>托管兜底路径：所有平台都能走通，精度取决于平台节拍。</summary>
    private static void SleepManaged(long durationTicks)
    {
        double milliseconds = HiResClock.ToMilliseconds(durationTicks);
        if (milliseconds >= 1)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(milliseconds));
        }
        else
        {
            // 亚毫秒请求在托管路径上无法精确表达；让出一次时间片，
            // 这段由调用方的自旋窗口兜住（自适应窗口会因为这次"早醒"而重新标定）。
            Thread.Sleep(0);
        }
    }

    /// <summary>
    /// Windows 高分辨率等待路径。
    /// </summary>
    /// <param name="durationTicks">等待时长（tick）。</param>
    /// <returns><see langword="false"/> 表示本线程用不了高分辨率句柄，调用方应改走 <see cref="SleepManaged"/>。</returns>
    private static bool SleepWindows(long durationTicks)
    {
        WaitHandleTimer? timer = GetOrCreateWindowsHandle();
        if (timer is null)
        {
            MarkUnavailable("Windows 高分辨率等待句柄不可用，退回托管 Thread.Sleep");
            return false;
        }

        // 相对到期时间，单位 100ns；负数表示"从现在起这么久"。
        long hundredsOfNanoseconds = (long)(durationTicks * (HiResClock.NanosecondsPerTick / 100.0));
        if (hundredsOfNanoseconds <= 0)
        {
            hundredsOfNanoseconds = 1;
        }

        long dueTime = -hundredsOfNanoseconds;
        if (!SetWaitableTimer(timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            MarkUnavailable("SetWaitableTimer 失败，退回托管 Thread.Sleep");
            return false;
        }

        // 用 INFINITE 而不是毫秒超时：毫秒级 timeout 参数会被系统节拍量化，
        // 精确性完全来自定时器对象本身的 100ns 粒度。
        uint waitResult = WaitForSingleObject(timer, InfiniteTimeout);
        if (waitResult != WaitSignaled)
        {
            // 只有 WAIT_FAILED 会走到这里（INFINITE 不会超时）。此时本拍一点都没等，
            // 返回 false 让调用方改走托管兜底，同时把进程内的高分辨率路径降级。
            MarkUnavailable($"WaitForSingleObject 返回 0x{waitResult:X8}，退回托管 Thread.Sleep");
            return false;
        }

        return true;
    }

    private static void MarkUnavailable(string reason)
    {
        Interlocked.Exchange(ref s_windowsHandleUnavailable, 1);
        Volatile.Write(ref s_supportsHighResolution, false);
        Volatile.Write(ref s_implementation, reason);
    }

    /// <summary>线程本地的高分辨率等待句柄。</summary>
    [ThreadStatic]
    private static WaitHandleTimer? t_windowsHandle;

    private static WaitHandleTimer? GetOrCreateWindowsHandle()
    {
        if (t_windowsHandle is not null)
        {
            return t_windowsHandle.IsInvalid ? null : t_windowsHandle;
        }

        WaitHandleTimer? created = null;
        try
        {
            created = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        }
        catch (EntryPointNotFoundException)
        {
            created = null;
        }
        catch (DllNotFoundException)
        {
            created = null;
        }

        if (created is null || created.IsInvalid)
        {
            // Windows 10 1803 以前不认这个标志位；退回普通等待句柄——仍然可用，只是精度回到系统节拍。
            try
            {
                created = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
            }
            catch (DllNotFoundException)
            {
                created = null;
            }
        }

        t_windowsHandle = created;
        return created is null || created.IsInvalid ? null : created;
    }

    /// <summary><c>kernel32!CreateWaitableTimerExW</c> 返回句柄的 <see cref="SafeHandle"/> 包装。</summary>
    private sealed class WaitHandleTimer : SafeHandle
    {
        public WaitHandleTimer() : base(IntPtr.Zero, ownsHandle: true)
        {
        }

        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern WaitHandleTimer? CreateWaitableTimerExW(IntPtr timerAttributes, string? name, int createFlags, int desiredAccess);

    // pDueTime 是指向 LARGE_INTEGER 的**指针**：按值传 long 会让内核把数值当地址解引用，
    // 直接 AccessViolation（这里踩过一次，务必保持 ref）。
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeHandle timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr completionContext, bool resumeTimer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeHandle handle, uint millisecondsTimeout);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
