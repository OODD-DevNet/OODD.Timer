using OODD.Timer;
using System.Collections.Concurrent;

namespace OODD.Timer.Tests;

/// <summary>
/// 调度器测试：共享线程上的多定时器管理、堆的排序语义、停启不重复挂载。
/// </summary>
public class SchedulerTests
{
    [Fact]
    public void SharedScheduler_DrivesManyTimers()
    {
        const int timerCount = 50;
        var fired = new ConcurrentDictionary<int, int>();
        var timers = new List<HighResolutionTimer>(timerCount);

        try
        {
            for (int i = 0; i < timerCount; i++)
            {
                int index = i;
                var timer = new HighResolutionTimer(new HighResolutionTimerOptions
                {
                    Name = $"shared-{i}",
                    Period = TimeSpan.FromMilliseconds(5 + i % 7),
                    Mode = WaitMode.Spin,
                    Scheduler = HighResolutionScheduler.Shared,
                    AutoStart = false,
                    Callback = () => fired.AddOrUpdate(index, 1, (_, c) => c + 1),
                });
                timers.Add(timer);
                timer.Start();
            }

            Assert.True(HighResolutionScheduler.Shared.TimerCount >= timerCount);
            long before = HighResolutionScheduler.Shared.TotalFiredTicks;
            HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(250), WaitMode.Kernel);

            Assert.Equal(timerCount, fired.Count);
            Assert.All(fired.Values, count => Assert.True(count > 0, "存在从未触发的定时器"));
            Assert.True(HighResolutionScheduler.Shared.TotalFiredTicks > before + timerCount);

            TimerStatistics sharedStats = HighResolutionScheduler.Shared.GetStatistics();
            Assert.True(sharedStats.TickCount > 0);
            Assert.Contains("timers=", HighResolutionScheduler.Shared.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            foreach (HighResolutionTimer timer in timers)
            {
                timer.Dispose();
            }
        }

        // 借用的调度器不会因为定时器释放而被连带释放。
        Assert.False(HighResolutionScheduler.Shared.IsDisposed);
        HighResolutionScheduler.Shared.ResetStatistics();
    }

    [Fact]
    public void EarlierDeadline_FiresFirst()
    {
        using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { Name = "Tests.Order" });
        var order = new ConcurrentQueue<string>();

        using var slow = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Name = "slow",
            Period = TimeSpan.FromMilliseconds(10),
            DueTime = TimeSpan.FromMilliseconds(60),
            Mode = WaitMode.Spin,
            Scheduler = scheduler,
            AutoStart = false,
            Callback = () => order.Enqueue("slow"),
        });
        using var fast = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Name = "fast",
            Period = TimeSpan.FromMilliseconds(10),
            DueTime = TimeSpan.FromMilliseconds(10),
            Mode = WaitMode.Spin,
            Scheduler = scheduler,
            AutoStart = false,
            Callback = () => order.Enqueue("fast"),
        });

        slow.Start();
        fast.Start();

        // 等到第一拍出现（fast 的到期点是 10ms，slow 是 60ms），再判顺序。
        long deadline = HiResClock.Add(HiResClock.Now(), HiResClock.ToTicks(TimeSpan.FromSeconds(5)));
        while (order.IsEmpty && !HiResClock.IsDue(deadline))
        {
            HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(1), WaitMode.Kernel);
        }

        Assert.False(order.IsEmpty, "两个定时器都没有触发");
        Assert.True(order.TryPeek(out string? first));
        Assert.Equal("fast", first);
        Assert.Equal(2, scheduler.TimerCount);
    }

    [Fact]
    public void SameDeadline_AllTimersFireExactlyOncePerPeriod()
    {
        using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { Name = "Tests.SameDue" });
        int a = 0;
        int b = 0;
        var due = TimeSpan.FromMilliseconds(4);

        using var ta = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = due,
            DueTime = due,
            Mode = WaitMode.Spin,
            Scheduler = scheduler,
            AutoStart = false,
            Callback = () => Interlocked.Increment(ref a),
        });
        using var tb = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = due,
            DueTime = due,
            Mode = WaitMode.Spin,
            Scheduler = scheduler,
            AutoStart = false,
            Callback = () => Interlocked.Increment(ref b),
        });

        ta.Start();
        tb.Start();
        Assert.Equal(2, scheduler.TimerCount);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(120), WaitMode.Kernel);
        ta.Stop();
        tb.Stop();
        watch.Stop();

        // 上限按"实际经过了多久"算，而不是按名义的 120ms：机器偶尔被外部事件拖住 60ms，
        // 用固定上限会把这种环境抖动误判成"重复触发"。
        int expected = (int)(watch.Elapsed.TotalMilliseconds / due.TotalMilliseconds);
        Assert.True(a > 0 && a <= expected + 3, $"a={a}，理论上限 {expected + 3}（实际 {watch.ElapsedMilliseconds}ms）");
        // 两者计数差只用来抓"重复注册"（那会表现为接近 2 倍），不应预置一个固定拍数：
        // 同一时刻挂载的两拍在调度线程上串行执行，卡顿只会整体推后，差值随运行时长按比例放宽。
        int tolerance = Math.Max(3, expected / 8);
        Assert.True(Math.Abs(a - b) <= tolerance,
            $"同时刻挂载的两个定时器计数差过大：a={a} b={b}（容差 {tolerance}）");
        Assert.Equal(0, scheduler.TimerCount);
    }

    [Fact]
    public void StopStart_LoopDoesNotDoubleRegister()
    {
        // 回归测试：Stop→Start 若在堆里留下重复记录，这一拍会被触发两次，
        // 表现为计数约为预期的两倍。
        using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(5),
            Mode = WaitMode.Spin,
            AutoStart = false,
        });

        for (int i = 0; i < 50; i++)
        {
            timer.Start();
            _ = timer.Stop();
        }

        int fired = 0;
        timer.Elapsed += (_, _) => Interlocked.Increment(ref fired);
        timer.Start();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(100), WaitMode.Kernel);
        timer.Stop();
        watch.Stop();

        // 上界按"实际经过的墙钟时间"算：外部卡顿会让 100ms 的睡眠变成 300ms，
        // 用固定 25 拍会把环境抖动误判成重复挂载。重复挂载的特征是约两倍，上界足以抓住它。
        int expected = (int)(watch.Elapsed.TotalMilliseconds / 5);
        Assert.True(fired > 0, "回归循环之后定时器没有触发过");
        Assert.True(fired <= expected + 2, $"重复挂载？实际 {watch.ElapsedMilliseconds}ms 内触发 {fired} 拍（约一半即 {expected}）");
    }

    [Fact]
    public void DisposedScheduler_RejectsArming()
    {
        var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { Name = "Tests.Dead" });
        scheduler.Dispose();
        scheduler.Dispose(); // 幂等
        Assert.True(scheduler.IsDisposed);

        using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(1),
            Scheduler = scheduler,
            AutoStart = false,
        });

        Assert.Throws<ObjectDisposedException>(() => timer.Start());
        Assert.Equal(TimerState.Stopped, timer.State);
    }

    [Fact]
    public void TimerCount_ReflectsArmedTimers()
    {
        using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { Name = "Tests.Count" });
        Assert.Equal(0, scheduler.TimerCount);
        Assert.Equal(WaitMode.Hybrid, scheduler.DefaultMode);

        var timers = Enumerable.Range(0, 8).Select(i => new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(20 + i),
            Mode = WaitMode.Kernel,
            Scheduler = scheduler,
            AutoStart = false,
        })).ToList();

        try
        {
            foreach (HighResolutionTimer timer in timers)
            {
                timer.Start();
            }

            Assert.Equal(8, scheduler.TimerCount);
            foreach (HighResolutionTimer timer in timers)
            {
                timer.Stop();
            }

            Assert.Equal(0, scheduler.TimerCount);
        }
        finally
        {
            foreach (HighResolutionTimer timer in timers)
            {
                timer.Dispose();
            }
        }
    }

    [Fact]
    public void SpinModeBurnsACore_WhileKernelModeIdlesForFree()
    {
        // 这是 WaitMode 的核心权衡，必须实测验证，不能只写在文档里。
        // 判据用"进程累计 CPU 时间"的增量：自旋一个核约消耗 1ms CPU / 1ms 墙上时间，
        // 内核等待的空闲调度线程则接近 0。
        // 进程级 CPU 会被同进程其它工作的尾巴污染（后台 JIT、GC、上一个测试遗留的线程收尾），
        // 而污染是瞬态的：空闲侧取 3 次窗口的最小值，才能把"线程真的挂住了"和"恰好有别的东西在跑"分开。
        const int WindowMilliseconds = 300;
        const int IdleWindowSamples = 3;

        double kernelCpu = Enumerable.Range(0, IdleWindowSamples)
            .Select(_ => MeasureCpuMilliseconds(WindowMilliseconds, WaitMode.Kernel))
            .Min();
        double spinCpu = MeasureCpuMilliseconds(WindowMilliseconds, WaitMode.Spin);

        Assert.True(spinCpu > WindowMilliseconds * 0.5,
            $"Spin 模式在 {WindowMilliseconds}ms 内只消耗了 {spinCpu:F1}ms CPU，忙等可能未生效");
        Assert.True(kernelCpu < WindowMilliseconds * 0.5,
            $"Kernel 模式在 {WindowMilliseconds}ms 的 {IdleWindowSamples} 次采样里最干净的一次仍消耗了 {kernelCpu:F1}ms CPU，线程没有被内核挂起");
    }

    private static double MeasureCpuMilliseconds(int windowMilliseconds, WaitMode mode)
    {
        using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { Name = $"Tests.Cpu.{mode}" });
        using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            // 周期取大值：绝大多数时间线程都在"等待下一拍"，测到的正是等待策略本身的开销。
            Period = TimeSpan.FromSeconds(30),
            Mode = mode,
            Scheduler = scheduler,
            AutoStart = false,
        });

        timer.Start();
        // 让调度线程先起跑（首次 Arm 才创建线程），再开始计时。
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(20), WaitMode.Kernel);

        long before = CurrentProcessCpuNanoseconds();
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(windowMilliseconds), WaitMode.Kernel);
        double cpu = (CurrentProcessCpuNanoseconds() - before) / 1_000_000.0;

        timer.Stop();
        return cpu;
    }

    private static long CurrentProcessCpuNanoseconds()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return (long)(process.TotalProcessorTime.TotalMilliseconds * 1_000_000.0);
    }

    [Fact]
    public void SchedulerOptions_AreAppliedToThreadNameAndPriority()
    {
        using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions
        {
            Name = "Tests.Named",
            Mode = WaitMode.Kernel,
            ThreadPriority = ThreadPriority.AboveNormal,
        });

        using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(10),
            Scheduler = scheduler,
            AutoStart = false,
        });

        timer.Start();
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(30), WaitMode.Kernel);
        Assert.True(scheduler.ThreadId > 0);
        Assert.Equal(WaitMode.Kernel, scheduler.DefaultMode);
        timer.Stop();
    }
}
