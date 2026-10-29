using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using OODD.Timer;

namespace OODD.Timer.CompatSmoke;

/// <summary>
/// 跨 TFM 冒烟程序：在 .NET Framework 4.0 / 4.8 与 .NET 8 上分别跑一遍库的主要路径，
/// 确认各自拿到的资产（net40 / netstandard2.0 / net8.0）确实能工作。
/// 只用三个 TFM 都提供的 BCL 表面：不用 async/await，不用 Task.CompletedTask，不用 ValueTuple。
/// </summary>
internal static class Program
{
    private static int s_failures;

    private static readonly Task<int> s_completedTask = CreateCompletedTask();

    private static Task<int> CreateCompletedTask()
    {
        TaskCompletionSource<int> source = new();
        source.SetResult(0);
        return source.Task;
    }

    private static void Check(string name, bool condition, string detail)
    {
        Console.WriteLine((condition ? "PASS  " : "FAIL  ") + name + " | " + detail);
        if (!condition)
        {
            s_failures++;
        }
    }

    private static int Main()
    {
        Assembly library = typeof(HiResClock).Assembly;
        var framework = (TargetFrameworkAttribute)Attribute.GetCustomAttribute(library, typeof(TargetFrameworkAttribute));
        Console.WriteLine("== asset: " + (framework == null ? "unknown" : framework.FrameworkName) + " ==");
        Console.WriteLine(HiResClock.DescribePlatform());
        Console.WriteLine(HighResolutionWait.GetPlatformInfo());
        Console.WriteLine();

        CheckClock();
        CheckSleep();
        CheckPeriodicCallback();
        CheckPullModes();
        CheckAsyncWait();
        CheckCancellation();
        CheckRunPeriodicAsync();
        CheckOneShotHelpers();
        CheckStatistics();
        CheckKernelLatency();
        CheckWindowsTimerResolution();
        CheckWindowsProcessorAffinity();

        Console.WriteLine();
        Console.WriteLine(s_failures == 0 ? "ALL PASSED" : (s_failures + " FAILED"));
        return s_failures == 0 ? 0 : 1;
    }

    private static void CheckClock()
    {
        bool hires = Stopwatch.IsHighResolution;
        long start = HiResClock.Now();
        long after = HiResClock.Now();
        Check("时钟单调推进", after >= start && HiResClock.Frequency > 0,
            "freq=" + HiResClock.Frequency + "Hz tick=" + HiResClock.NanosecondsPerTick.ToString("F1") + "ns hires=" + hires);
    }

    private static void CheckSleep()
    {
        long start = HiResClock.Now();
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(5));
        double elapsedMs = HiResClock.ToMilliseconds(HiResClock.Now() - start);
        Check("HighResolutionWait.Sleep(5ms)", elapsedMs >= 4.5 && elapsedMs < 250, elapsedMs.ToString("F2") + "ms");
    }

    private static void CheckPeriodicCallback()
    {
        int fired = 0;
        var reached = new ManualResetEventSlim(false);
        var options = new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(2),
            Mode = WaitMode.Hybrid,
            Callback = () =>
            {
                if (Interlocked.Increment(ref fired) >= 20)
                {
                    reached.Set();
                }
            },
        };

        using (var timer = new HighResolutionTimer(options))
        {
            bool signaled = reached.Wait(5000);
            TimerStatistics stats = timer.GetStatistics();
            Check("周期回调 20 拍", signaled && fired >= 20,
                "fired=" + fired + " ticks=" + stats.TickCount + " missed=" + stats.MissedTicks);
            Check("平均迟到 < 5ms", stats.MeanLatenessNanoseconds < 5_000_000,
                "mean=" + stats.MeanLatenessNanoseconds.ToString("F0") + "ns p99=" + stats.GetLatenessPercentile(0.99).ToString("F0") + "ns");
        }
    }

    private static void CheckPullModes()
    {
        using (var timer = new HighResolutionTimer(TimeSpan.FromMilliseconds(2), WaitMode.Hybrid))
        {
            timer.WaitForTrigger();
            timer.WaitForTrigger(TimeSpan.FromMilliseconds(1));
            Check("同步拉模式 WaitForTrigger", timer.Sequence >= 2, "seq=" + timer.Sequence);
        }
    }

    private static void CheckAsyncWait()
    {
        using (var timer = new HighResolutionTimer(TimeSpan.FromMilliseconds(2), WaitMode.Hybrid))
        {
            Task<bool> wait = timer.WaitForNextTickAsync();
            bool completed = wait.Wait(5000);
            Check("WaitForNextTickAsync 正常完成", completed && wait.Result, "task=" + wait.Status);
        }
    }

    private static void CheckCancellation()
    {
        using (var timer = new HighResolutionTimer(TimeSpan.FromMilliseconds(500), WaitMode.Hybrid))
        {
            using (var already = new CancellationTokenSource())
            {
                already.Cancel();
                try
                {
                    timer.WaitForNextTickAsync(already.Token).Wait(2000);
                    Check("预先取消的令牌", false, "没有抛异常");
                }
                catch (AggregateException exception)
                {
                    Check("预先取消的令牌", exception.InnerException is OperationCanceledException,
                        exception.InnerException == null ? "inner=null" : "inner=" + exception.InnerException.GetType().Name);
                }
            }

            using (var pending = new CancellationTokenSource())
            {
                // net40 的 CancellationTokenSource 没有 CancelAfter（4.5 才加），用 Timer 兜。
                System.Threading.Timer canceller = new(_ => pending.Cancel(), null, 100, Timeout.Infinite);
                try
                {
                    timer.WaitForNextTickAsync(pending.Token).Wait(5000);
                    Check("等待中取消", false, "没有抛异常");
                }
                catch (AggregateException exception)
                {
                    Check("等待中取消", exception.InnerException is OperationCanceledException,
                        exception.InnerException == null ? "inner=null" : "inner=" + exception.InnerException.GetType().Name);
                }
                finally
                {
                    canceller.Dispose();
                }
            }
        }
    }

    private static void CheckRunPeriodicAsync()
    {
        int cycles = 0;
        using (var cancellation = new CancellationTokenSource())
        {
            Task loop = HighResolutionTimers.RunPeriodicAsync(
                TimeSpan.FromMilliseconds(2),
                token =>
                {
                    if (Interlocked.Increment(ref cycles) >= 5)
                    {
                        cancellation.Cancel();
                    }

                    return s_completedTask;
                },
                WaitMode.Hybrid,
                null,
                cancellation.Token);

            try
            {
                loop.Wait(5000);
                Check("RunPeriodicAsync 取消退出", false, "任务正常结束而不是取消");
            }
            catch (AggregateException exception)
            {
                Check("RunPeriodicAsync 取消退出", exception.InnerException is OperationCanceledException && cycles >= 5,
                    "cycles=" + cycles + " inner=" + (exception.InnerException == null ? "null" : exception.InnerException.GetType().Name));
            }
        }
    }

    private static void CheckOneShotHelpers()
    {
        var reached = new ManualResetEventSlim(false);
        using (HighResolutionTimer once = HighResolutionTimers.Once(TimeSpan.FromMilliseconds(5), reached.Set))
        {
            bool signaled = reached.Wait(5000);
            once.WaitOne(2000);
            Check("HighResolutionTimers.Once", signaled, "signaled=" + signaled + " state=" + once.State);
        }

        int ticks = 0;
        using (HighResolutionTimer periodic = HighResolutionTimers.Periodic(TimeSpan.FromMilliseconds(2), () => Interlocked.Increment(ref ticks)))
        {
            periodic.WaitOne(TimeSpan.FromMilliseconds(200));
        }

        Check("HighResolutionTimers.Periodic + WaitOne", ticks >= 1, "ticks=" + ticks);
    }

    private static void CheckStatistics()
    {
        using (var timer = new HighResolutionTimer(TimeSpan.FromMilliseconds(1), WaitMode.Spin))
        {
            timer.WaitForTrigger();
            TimerStatistics stats = timer.GetStatistics();
            string histogram = stats.ToLatenessHistogramText();
            bool produced = histogram.Length > 0 && stats.PercentileInRange();
            Check("统计与直方图", produced, "text len=" + histogram.Length + " p50=" + stats.GetLatenessPercentile(0.5).ToString("F0") + "ns");
        }
    }

    private static bool PercentileInRange(this TimerStatistics stats)
    {
        double p50 = stats.GetLatenessPercentile(0.5);
        double p99 = stats.GetLatenessPercentile(0.99);
        return p50 >= 0 && p99 >= p50;
    }

    private static void CheckKernelLatency()
    {
        KernelWakeLatency latency = HighResolutionWait.MeasureKernelWakeLatency(32);
        Check("MeasureKernelWakeLatency", latency.SampleCount == 32 && latency.P99Lateness >= 0, latency.ToString());
    }

    private static void CheckWindowsProcessorAffinity()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            Console.WriteLine("SKIP  Windows processor affinity | Windows-only check; not tested on this platform.");
            return;
        }

        try
        {
            NativeGroupAffinity callerAffinity;
            if (!GetThreadGroupAffinity(GetCurrentThread(), out callerAffinity))
            {
                Check("Windows processor affinity", false, "GetThreadGroupAffinity error=" + Marshal.GetLastWin32Error());
                return;
            }

            int processorIndex = -1;
            for (int index = IntPtr.Size * 8 - 1; index >= 0; index--)
            {
                if ((callerAffinity.Mask.ToUInt64() & (1UL << index)) != 0)
                {
                    processorIndex = index;
                    break;
                }
            }

            if (processorIndex < 0)
            {
                Check("Windows processor affinity", false, "The caller has no allowed processor.");
                return;
            }

            int samples = 0;
            int failed = 0;
            using (var timer = new HighResolutionTimer(new HighResolutionTimerOptions
            {
                ProcessorIndex = processorIndex,
                ProcessorGroup = callerAffinity.Group,
                Period = TimeSpan.FromMilliseconds(1),
                Mode = WaitMode.Kernel,
                ThreadPriority = ThreadPriority.Normal,
                AutoStart = false,
                Callback = () =>
                {
                    if (Interlocked.CompareExchange(ref samples, 0, 0) >= 100)
                    {
                        return;
                    }

                    try
                    {
                        NativeProcessorNumber processor;
                        GetCurrentProcessorNumberEx(out processor);
                        NativeGroupAffinity actualAffinity;
                        if (processor.Group != callerAffinity.Group || processor.Number != processorIndex
                            || !GetThreadGroupAffinity(GetCurrentThread(), out actualAffinity)
                            || actualAffinity.Group != callerAffinity.Group
                            || actualAffinity.Mask.ToUInt64() != (1UL << processorIndex))
                        {
                            Interlocked.Exchange(ref failed, 1);
                        }
                    }
                    catch (Exception)
                    {
                        Interlocked.Exchange(ref failed, 1);
                    }

                    Interlocked.Increment(ref samples);
                },
            }))
            {
                timer.Start();
                Stopwatch watch = Stopwatch.StartNew();
                while (Interlocked.CompareExchange(ref samples, 0, 0) < 100 && watch.ElapsedMilliseconds < 10000)
                {
                    Thread.Sleep(1);
                }

                timer.Stop();
            }

            NativeGroupAffinity after;
            bool callerUnchanged = GetThreadGroupAffinity(GetCurrentThread(), out after)
                && after.Group == callerAffinity.Group && after.Mask.Equals(callerAffinity.Mask);
            Check("Windows processor affinity", samples == 100 && failed == 0 && callerUnchanged,
                "group=" + callerAffinity.Group + " cpu=" + processorIndex + " samples=" + samples
                + " mismatch=" + failed + " callerUnchanged=" + callerUnchanged);
        }
        catch (Exception exception)
        {
            Check("Windows processor affinity", false, exception.ToString());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGroupAffinity
    {
        public UIntPtr Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProcessorNumber
    {
        public ushort Group;
        public byte Number;
        public byte Reserved;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadGroupAffinity(IntPtr thread, out NativeGroupAffinity affinity);

    [DllImport("kernel32.dll")]
    private static extern void GetCurrentProcessorNumberEx(out NativeProcessorNumber processor);

    private static void CheckWindowsTimerResolution()
    {
        bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
        using (RuntimeTuning.RequestSystemTimerResolution(1))
        {
            int during = RuntimeTuning.SystemTimerResolutionRequestCount;
            Check("RequestSystemTimerResolution 生效", !windows || during == 1, "windows=" + windows + " count=" + during);
        }

        Check("RequestSystemTimerResolution 归零", RuntimeTuning.SystemTimerResolutionRequestCount == 0,
            "count=" + RuntimeTuning.SystemTimerResolutionRequestCount);
    }
}
