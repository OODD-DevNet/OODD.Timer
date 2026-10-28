using OODD.Timer;

namespace OODD.Timer.Tests;

/// <summary>
/// 生命周期与语义测试：状态机、Change/Reset、异常处理、等待接口、释放。
/// </summary>
public class TimerLifecycleTests
{
    private static HighResolutionTimer NewTimer(TimeSpan period, WaitMode mode = WaitMode.Spin) =>
        new(new HighResolutionTimerOptions { Period = period, Mode = mode, AutoStart = false });

    [Fact]
    public void Constructor_ValidatesOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new HighResolutionTimer((HighResolutionTimerOptions)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HighResolutionTimer(
            new HighResolutionTimerOptions { Period = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HighResolutionTimer(
            new HighResolutionTimerOptions { Period = TimeSpan.FromMilliseconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HighResolutionTimer(
            new HighResolutionTimerOptions { DueTime = TimeSpan.FromMilliseconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HighResolutionTimer(
            new HighResolutionTimerOptions { SpinWindowNanoseconds = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HighResolutionTimer(
            new HighResolutionTimerOptions { MaxSpinWindowNanoseconds = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HighResolutionTimer(
            new HighResolutionTimerOptions { CoarseChunkMilliseconds = 0 }));
    }

    [Fact]
    public void ConvenienceCtor_AutoStarts()
    {
        using var timer = new HighResolutionTimer(TimeSpan.FromMilliseconds(5));
        Assert.Equal(TimerState.Running, timer.State);
        Assert.True(timer.IsRunning);
        Assert.True(timer.PeriodTimestamps > 0);
        Assert.Contains("period=", timer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void StartStop_TransitionsAndReturnValues()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(2));
        Assert.Equal(TimerState.Stopped, timer.State);
        Assert.False(timer.Stop()); // 未启动时 Stop 仍然安全

        timer.Start();
        Assert.Equal(TimerState.Running, timer.State);
        Assert.True(timer.Stop());
        Assert.Equal(TimerState.Stopped, timer.State);
        Assert.False(timer.Stop());

        // 反复 Start/Stop 不能在调度堆里留下重复记录（否则会出现"一次 Stop 之后还在跑"）。
        for (int i = 0; i < 20; i++)
        {
            timer.Start();
            _ = timer.Stop();
        }

        timer.Start();
        Assert.True(timer.WaitOne(TimeSpan.FromSeconds(5)));
        long first = timer.Sequence;
        Assert.True(first >= 1);
        timer.Stop();
        long after = timer.Sequence;
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(60), WaitMode.Kernel);
        Assert.Equal(after, timer.Sequence); // 停表后不再推进
    }

    [Fact]
    public void Stop_WhileRunning_KeepsSequenceMonotonic()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(1));
        timer.Start();
        Assert.True(WaitFor(timer, 50, TimeSpan.FromSeconds(5)));
        timer.Stop();
        long snapshot = timer.Sequence;
        Assert.True(snapshot >= 50);
        timer.Start();
        // Start 不重置序号，只重新锚定相位。
        Assert.True(timer.Sequence >= snapshot - 1);
    }

    [Fact]
    public void AutoResetFalse_FiresOnceAndStops()
    {
        using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(5),
            AutoReset = false,
            Mode = WaitMode.Spin,
            AutoStart = false,
        });

        int count = 0;
        timer.Elapsed += (_, _) => Interlocked.Increment(ref count);
        timer.Start();

        long deadline = HiResClock.Add(HiResClock.Now(), HiResClock.ToTicks(TimeSpan.FromSeconds(3)));
        while (Volatile.Read(ref count) == 0 && !HiResClock.IsDue(deadline))
        {
            HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(1), WaitMode.Kernel);
        }

        Assert.Equal(1, Volatile.Read(ref count));
        Assert.Equal(TimerState.Stopped, timer.State);
        Assert.False(timer.AutoReset);
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(100), WaitMode.Kernel);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void Change_AdjustsPeriodAndOneShotSemantics()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(10));

        // 未在运行时 Change 只更新配置，返回 false。
        Assert.False(timer.Change(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));

        timer.Start();
        Assert.True(timer.Change(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
        Assert.True(WaitFor(timer, 30, TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromMilliseconds(1), timer.Period);

        // period <= 0 → 改单次：触发一拍后序号不再增长。
        Assert.True(timer.Change(TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan));
        long before = timer.Sequence;
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(80), WaitMode.Kernel);
        Assert.True(timer.Sequence > before);
        long oneShot = timer.Sequence;
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(80), WaitMode.Kernel);
        Assert.Equal(oneShot, timer.Sequence);
    }

    [Fact]
    public void Change_RejectsNegativeDueTime()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.Change(TimeSpan.FromMilliseconds(-1), TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void PeriodSetter_ReanchorsAndValidates()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(5));
        timer.Start();
        timer.Period = TimeSpan.FromMilliseconds(1);
        Assert.Equal(TimeSpan.FromMilliseconds(1), timer.Period);
        Assert.Equal(HiResClock.ToTicksFromMilliseconds(1), timer.PeriodTimestamps);
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.Period = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.Period = TimeSpan.FromMilliseconds(-5));
    }

    [Fact]
    public void DueTime_ValidatesAndAppliesOnNextStart()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.DueTime = TimeSpan.FromMilliseconds(-1));
        timer.DueTime = TimeSpan.FromMilliseconds(200);
        Assert.Equal(TimeSpan.FromMilliseconds(200), timer.DueTime);
        timer.Start();
        Assert.Equal(0, timer.Sequence); // 首次延迟 200ms，刚起跑时还没触发
        Assert.Equal(0, timer.LastDueTimestamp); // 未触发过
        timer.DueTime = null;
    }

    [Fact]
    public void ModeSetter_RestartsRunningTimer()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(2), WaitMode.Spin);
        timer.Start();
        Assert.True(WaitFor(timer, 10, TimeSpan.FromSeconds(5)));
        timer.Mode = WaitMode.Hybrid;
        Assert.Equal(WaitMode.Hybrid, timer.Mode);
        Assert.Equal(TimerState.Running, timer.State);
        Assert.True(timer.IsRunning);
        Assert.True(WaitFor(timer, timer.Sequence + 10, TimeSpan.FromSeconds(5)));

        // 未运行时只改配置，不擅自启动。
        timer.Stop();
        timer.Mode = WaitMode.Kernel;
        Assert.Equal(TimerState.Stopped, timer.State);
    }

    [Fact]
    public void Reset_ClearsStatisticsAndRestarts()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(1));
        timer.Start();
        Assert.True(WaitFor(timer, 30, TimeSpan.FromSeconds(5)));
        Assert.True(timer.GetStatistics().TickCount > 0);
        timer.Reset();
        Assert.Equal(0, timer.GetStatistics().TickCount);
        Assert.Equal(TimerState.Running, timer.State);
        Assert.True(timer.WaitOne(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void WaitOne_TimeoutAndStopBehavior()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(50));
        Assert.False(timer.WaitOne(TimeSpan.FromMilliseconds(80))); // 未启动：必然超时
        Assert.False(timer.WaitOne(80));

        timer.Start();
        Assert.True(timer.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.True(timer.WaitOne(5_000));
        timer.Stop();
        Assert.False(timer.WaitOne(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void WaitOne_RejectsInvalidTimeout()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.WaitOne(-2));
    }

    [Fact]
    public async Task WaitForNextTickAsync_ReturnsTruePerTick()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(2));
        timer.Start();
        long start = HiResClock.Now();
        for (int i = 0; i < 5; i++)
        {
            Assert.True(await timer.WaitForNextTickAsync());
        }

        // 逐拍量"这一拍等了多久"不成立：续体被线程池拖住时，恢复后下一拍可能已在几十微秒内到达。
        // 有效的判据是"5 拍合计至少经过了 4 个周期"，这照样能抓住"立即返回"的假实现。
        double elapsedMs = HiResClock.ToMilliseconds(HiResClock.Now() - start);
        Assert.True(elapsedMs >= 8, $"5 拍只用了 {elapsedMs:F3}ms（2ms 周期，至少应 8ms）");
    }

    [Fact]
    public async Task WaitForNextTickAsync_ReturnsFalseAfterDispose()
    {
        // 周期取 30s：确保释放之前不可能有任何一拍抢先完成等待，返回值只能来自"被释放"这条路径。
        var timer = NewTimer(TimeSpan.FromSeconds(30));
        timer.Start();
        Task<bool> pending = timer.WaitForNextTickAsync();
        timer.Dispose();
        Assert.False(await pending);

        // 释放后新的等待立即得到 false，而不是永久挂起。
        Assert.False(await timer.WaitForNextTickAsync());
    }

    [Fact]
    public async Task WaitForNextTickAsync_ThrowsOnCancellation()
    {
        using var timer = NewTimer(TimeSpan.FromSeconds(30));
        timer.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await timer.WaitForNextTickAsync(cts.Token));

        // 取消后定时器仍可用。
        Assert.True(timer.Change(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(2)));
        Assert.True(await timer.WaitForNextTickAsync());
    }

    [Fact]
    public void CallbackExceptions_AreSuppressedAndReported()
    {
        var errors = new List<Exception>();
        int ticks = 0;
        using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(1),
            Mode = WaitMode.Spin,
            AutoStart = false,
            Callback = () =>
            {
                if (Interlocked.Increment(ref ticks) <= 3)
                {
                    throw new InvalidOperationException("boom");
                }
            },
        });

        timer.Error += (_, e) =>
        {
            Assert.True(e.Sequence >= 1);
            Assert.Same(timer, e.Timer);
            lock (errors)
            {
                errors.Add(e.Exception);
            }
        };

        timer.Start();
        Assert.True(WaitFor(timer, 30, TimeSpan.FromSeconds(5))); // 异常没有打断后续节拍
        timer.Stop();

        lock (errors)
        {
            Assert.Equal(3, errors.Count);
            Assert.All(errors, e => Assert.IsType<InvalidOperationException>(e));
        }

        Assert.Equal(3, timer.GetStatistics().ErrorCount);
    }

    [Fact]
    public void Dispose_IsIdempotentAndBlocksReuse()
    {
        var timer = NewTimer(TimeSpan.FromMilliseconds(1));
        timer.Start();
        timer.Dispose();
        timer.Dispose(); // 幂等

        Assert.Equal(TimerState.Disposed, timer.State);
        Assert.False(timer.IsRunning);
        Assert.Throws<ObjectDisposedException>(() => timer.Start());
        Assert.False(timer.WaitOne(TimeSpan.FromMilliseconds(10)));
        Assert.False(timer.WaitOne(10));
        Assert.False(timer.Change(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
        Assert.False(timer.Stop());

        // 统计接口在释放后仍可读取，不抛异常。
        TimerStatistics stats = timer.GetStatistics();
        Assert.False(string.IsNullOrWhiteSpace(stats.ToString()));
    }

    [Fact]
    public void Dispose_ReleasesOwnedScheduler()
    {
        var timer = NewTimer(TimeSpan.FromMilliseconds(1));
        HighResolutionScheduler scheduler = timer.Scheduler;
        Assert.False(scheduler.IsDisposed);
        timer.Start();
        Assert.Equal(1, scheduler.TimerCount);
        timer.Dispose();
        Assert.True(scheduler.IsDisposed);
        Assert.Equal(0, scheduler.TimerCount);
    }

    [Fact]
    public void SharedScheduler_SurvivesTimerDispose_AndRejectsReuseAfterItsOwnDispose()
    {
        var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { Name = "Tests.Shared" });
        using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(1),
            Mode = WaitMode.Spin,
            Scheduler = scheduler,
            AutoStart = false,
        });

        timer.Start();
        Assert.True(WaitFor(timer, 20, TimeSpan.FromSeconds(5)));
        timer.Dispose();

        // 定时器释放不会连带释放它借用的调度器。
        Assert.False(scheduler.IsDisposed);

        scheduler.Dispose();
        Assert.True(scheduler.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(1),
            Scheduler = scheduler,
            AutoStart = true,
        }));
    }

    [Fact]
    public void ElapsedEventArgs_CarryConsistentTiming()
    {
        using var timer = NewTimer(TimeSpan.FromMicroseconds(800));
        var captured = new List<TimerElapsedEventArgs>();
        timer.Elapsed += (_, e) =>
        {
            lock (captured)
            {
                captured.Add(e);
            }
        };

        timer.Start();
        Assert.True(WaitFor(timer, 40, TimeSpan.FromSeconds(5)));
        timer.Stop();

        lock (captured)
        {
            Assert.True(captured.Count >= 40);
            for (int i = 0; i < captured.Count; i++)
            {
                TimerElapsedEventArgs e = captured[i];
                Assert.Equal(i + 1, (int)e.Sequence);
                Assert.True(e.FiredTimestamp >= e.DueTimestamp - HiResClock.NanosecondsPerTick * 2,
                    $"第 {i + 1} 拍早于应到时刻");
                Assert.Equal(e.MissedTicks > 0, e.Overrun);
                Assert.Equal(e.LatenessNanoseconds, HiResClock.ToNanoseconds(e.FiredTimestamp - e.DueTimestamp), 1);
                Assert.True(e.Lateness.TotalMilliseconds is >= 0 and < 1000);
                Assert.Contains("missed=", e.ToString(), StringComparison.Ordinal);
                if (i > 0)
                {
                    Assert.True(e.IntervalNanoseconds > 0);
                }
                else
                {
                    Assert.True(double.IsNaN(e.IntervalNanoseconds), "首拍没有上一拍可参照");
                }
            }
        }
    }

    private static bool WaitFor(HighResolutionTimer timer, long expectedSequence, TimeSpan timeout)
    {
        long deadline = HiResClock.Add(HiResClock.Now(), HiResClock.ToTicks(timeout));
        while (timer.Sequence < expectedSequence && !HiResClock.IsDue(deadline))
        {
            HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(1), WaitMode.Kernel);
        }

        return timer.Sequence >= expectedSequence;
    }
}
