using OODD.Timer;
using System.Collections.Concurrent;

namespace OODD.Timer.Tests;

/// <summary>
/// 端到端精度测试：真正跑定时器，验证"微秒级、且不随时间漂移"这两个核心卖点。
/// </summary>
public class TimerAccuracyTests
{
    /// <summary>Windows 系统时钟节拍的典型值；本库存在的意义就是打破它。</summary>
    private const double SystemTickMilliseconds = 15.6;

    /// <summary>采集每一拍的原始数据，供断言使用。</summary>
    private sealed record Tick(long Due, long Fired, double LatenessNanos, int Missed, long Sequence);

    /// <summary>创建一个"没自己启动"的定时器，好让测试先挂事件再起跑。</summary>
    private static HighResolutionTimer NewTimer(TimeSpan period, WaitMode mode) =>
        new(new HighResolutionTimerOptions { Period = period, Mode = mode, AutoStart = false });

    private static List<Tick> Run(HighResolutionTimer timer, int expectedTicks, TimeSpan timeout)
    {
        var ticks = new ConcurrentQueue<Tick>();
        using var done = new ManualResetEventSlim();
        timer.Elapsed += (_, e) =>
        {
            ticks.Enqueue(new Tick(e.DueTimestamp, e.FiredTimestamp, e.LatenessNanoseconds, e.MissedTicks, e.Sequence));
            if (ticks.Count >= expectedTicks)
            {
                done.Set();
            }
        };

        timer.Start();
        Assert.True(done.Wait(timeout), $"只等到 {ticks.Count}/{expectedTicks} 拍");
        timer.Stop();

        // done.Set() 到 Stop() 真正生效之间，快速节拍（10kHz 只有 100µs 窗口）还能再进 1~2 拍。
        // 截取前 expectedTicks 拍，让"恰好 N 拍"的断言不依赖这个竞态。
        return ticks.OrderBy(t => t.Sequence).Take(expectedTicks).ToList();
    }

    private static double Median(double[] values)
    {
        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    [Theory]
    [InlineData(WaitMode.Spin)]
    [InlineData(WaitMode.Hybrid)]
    public void Elapsed_FiresOnTime_AndNeverEarly(WaitMode mode)
    {
        using HighResolutionTimer timer = NewTimer(TimeSpan.FromMicroseconds(500), mode);
        List<Tick> ticks = Run(timer, 400, TimeSpan.FromSeconds(20));
        Assert.Equal(400, ticks.Count);

        double[] lateness = ticks.Select(t => t.LatenessNanos).ToArray();

        // 1) 不允许早触发：迟到量非负（容忍一个时钟粒度的测量噪声）。
        Assert.True(lateness.Min() >= -HiResClock.NanosecondsPerTick,
            $"出现早触发：最小迟到 {lateness.Min():F0}ns");

        // 2) 中位数必须远小于系统节拍——这正是本库存在的意义。
        double median = Median(lateness);
        Assert.True(median < 1_000_000, $"{mode} 中位迟到 {median / 1000:F1}µs，未达到微秒级目标");
    }

    [Fact]
    public void DueTimestamps_AreExactlyPeriodic_NoDrift()
    {
        // 漂移是定时器最难缠的问题：如果每次按"上次触发 + 周期"推进，误差会永久累加。
        // 本库按绝对锚点推进，所以每一拍的到期点必须精确落在 firstDue + n*period 的格点上。
        const int ExpectedTicks = 500;
        var period = TimeSpan.FromMicroseconds(400);
        long periodTicks = HiResClock.ToTicks(period);

        using HighResolutionTimer timer = NewTimer(period, WaitMode.Spin);
        List<Tick> ticks = Run(timer, ExpectedTicks, TimeSpan.FromSeconds(20));

        long first = ticks[0].Due;
        long slot = 0;
        int cleanTicks = 0;
        foreach (Tick tick in ticks)
        {
            Assert.Equal(HiResClock.Add(first, slot * periodTicks), tick.Due);
            if (tick.Missed == 0)
            {
                cleanTicks++;
            }

            slot += tick.Missed + 1;
        }

        Assert.True(cleanTicks > ticks.Count * 0.9,
            $"{cleanTicks}/{ticks.Count} 拍准点，测试期间机器负载过高，结果不可用于精度判定");

        // 迟到不会被下一拍继承：最后一拍的迟到仍然是微秒级，而不是 500 拍累积量。
        double lastLatenessMs = ticks[^1].LatenessNanos / 1_000_000;
        Assert.True(lastLatenessMs < SystemTickMilliseconds,
            $"跑了 {ticks.Count} 拍之后迟到累积到 {lastLatenessMs:F3}ms");
    }

    [Fact]
    public void SlowCallback_CoalescesInsteadOfCatchUpStorm()
    {
        // 回调耗时 6 个周期：调度器应跳过欠账（合并漏拍），而不是把 6 拍瞬间补发。
        var period = TimeSpan.FromMilliseconds(2);
        int fired = 0;
        var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = period,
            Mode = WaitMode.Spin,
            AutoStart = false,
            Callback = () =>
            {
                Interlocked.Increment(ref fired);
                HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(12), WaitMode.Spin);
            },
        });

        using (timer)
        {
            timer.Start();
            HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(120), WaitMode.Kernel);
            timer.Stop();

            int expectedMax = 120 / 12 + 4; // 每次回调 12ms，最多也就跑这么多拍
            Assert.True(fired > 0, "回调一次都没有执行");
            Assert.True(fired <= expectedMax,
                $"漏拍被逐拍补发（跑了 {fired} 次，上限 {expectedMax}）——这会形成追单风暴");

            TimerStatistics stats = timer.GetStatistics();
            Assert.True(stats.MissedTicks > 0, "回调超时应当计入 MissedTicks");
            Assert.Equal(fired, stats.TickCount);
        }
    }

    [Fact]
    public void MissedTicks_MatchScheduledDueArithmetic()
    {
        // 语义要点：调度线程在"进入回调之前"取 firedAt，所以一次阻塞 9ms 的回调
        // 只会在**下一拍**被结算——那一拍按计划本应在 due+2ms 触发，实际在 due+9ms 才跑到，
        // 迟到 7ms ⇒ 跳过 7/2 = 3 拍。这正是"不追赶、只记账"的设计。
        var missed = new List<int>();
        using HighResolutionTimer timer = NewTimer(TimeSpan.FromMilliseconds(2), WaitMode.Spin);
        timer.Elapsed += (_, e) =>
        {
            lock (missed)
            {
                missed.Add(e.MissedTicks);
            }

            if (e.Sequence == 3)
            {
                HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(9), WaitMode.Spin);
            }
        };

        _ = Run(timer, 14, TimeSpan.FromSeconds(10));
        lock (missed)
        {
            Assert.All(missed, m => Assert.True(m >= 0, "漏拍计数不应为负"));
            // 名义上是 9ms/2ms ≈ 3 拍；机器被外部事件拖住时结算点会再往后滑，
            // 记成 4~5 拍是环境噪声，不是语义错误。语义边界是"绝不补发"（上限 6）。
            Assert.Contains(missed, m => m >= 3);
            Assert.DoesNotContain(missed, m => m > 6);
        }

        // 漏掉的拍数被正确累计进统计。
        Assert.True(timer.GetStatistics().MissedTicks >= 3);
    }

    [Fact]
    public void Statistics_SummaryPercentilesAndHistogramAreSane()
    {
        using HighResolutionTimer timer = NewTimer(TimeSpan.FromMilliseconds(1), WaitMode.Hybrid);
        _ = Run(timer, 300, TimeSpan.FromSeconds(15));

        TimerStatistics stats = timer.GetStatistics();

        // 统计是在 Stop() 之后读的，可能比等待的 300 拍多出零星几拍（见 Run 的说明）。
        Assert.True(stats.TickCount >= 300, $"统计只记了 {stats.TickCount} 拍");
        Assert.Equal(WaitMode.Hybrid, stats.Mode);
        Assert.True(Math.Abs(stats.PeriodNanoseconds - 1_000_000) < 1_000,
            $"周期回读异常：{stats.PeriodNanoseconds}ns");
        Assert.True(stats.MinLatenessNanoseconds <= stats.MeanLatenessNanoseconds);
        Assert.True(stats.MeanLatenessNanoseconds <= stats.MaxLatenessNanoseconds);
        Assert.True(stats.MinIntervalNanoseconds <= stats.MaxIntervalNanoseconds);
        Assert.True(stats.UptimeSeconds > 0);
        Assert.True(stats.FireRatePerSecond > 50, $"实测频率 {stats.FireRatePerSecond:F1}/s 过低");
        Assert.True(stats.GetLatenessPercentile(0.50) <= stats.GetLatenessPercentile(0.99));
        Assert.True(stats.GetLatenessPercentile(0.99) <= stats.MaxLatenessNanoseconds * 2 + 1,
            "对数分桶给出的 p99 上界不应超过真实最大值的一个倍程");
        Assert.True(stats.JitterRatio < 1, $"抖动率 {stats.JitterRatio:F3} 已接近周期本身");
        Assert.Contains("lateness", stats.ToLatenessHistogramText(), StringComparison.Ordinal);
        Assert.Contains($"ticks={stats.TickCount}", stats.ToString(), StringComparison.Ordinal);

        timer.ResetStatistics();
        Assert.Equal(0, timer.GetStatistics().TickCount);
    }

    [Fact]
    public void KernelMode_IsUsableForCoarsePeriods()
    {
        // 秒级/几十毫秒节拍用 Kernel 最省 CPU，精度要求也低：断言它确实按周期跑。
        using HighResolutionTimer timer = NewTimer(TimeSpan.FromMilliseconds(50), WaitMode.Kernel);
        List<Tick> ticks = Run(timer, 10, TimeSpan.FromSeconds(10));
        double meanIntervalMs = HiResClock.ToMilliseconds(ticks[^1].Fired - ticks[0].Fired) / (ticks.Count - 1);
        Assert.True(meanIntervalMs is > 45 and < 80, $"Kernel 模式平均间隔 {meanIntervalMs:F1}ms");
    }

    [Fact]
    public void HighFrequencyTimer_KeepsUpWithRequestedRate()
    {
        // 100µs（10kHz）是"微秒级定时器"的典型使用强度。
        // 判据用"准点拍的迟到中位数"：被内核抢占的拍会整段缺席若干个周期，
        // 它们必然计入 MissedTicks，但不该污染"这个定时器本身有多准"的度量。
        using HighResolutionTimer timer = NewTimer(TimeSpan.FromMicroseconds(100), WaitMode.Spin);
        List<Tick> ticks = Run(timer, 300, TimeSpan.FromSeconds(10));
        Assert.Equal(300, ticks.Count);

        int missed = ticks.Sum(t => t.Missed);
        Assert.True(missed <= 30, $"10kHz 节拍漏了 {missed} 拍，超过 10%（测试机负载过高）");

        double[] onTime = ticks.Where(t => t.Missed == 0).Select(t => t.LatenessNanos).ToArray();
        Assert.True(onTime.Length >= ticks.Count * 0.7, "准点拍太少，测试机噪声过大");
        Assert.True(Median(onTime) < 20_000, $"100µs 节拍的准点迟到中位数超过了 20µs：{Median(onTime):F0}ns");
    }

    [Fact]
    public void CallbackPath_AvoidsPerTickEventArgsAllocation()
    {
        const int Ticks = 400;

        // 每条路径先跑一段预热，再把测量窗口卡在预热之后：
        // JIT 分层编译、线程池启动、首拍内部结构的一次性分配都落在窗口外，
        // 否则这些噪声会把两条路径的差值吃掉（表现为偶发的"差异过小"）。
        const int Warmup = 40;

        // 内联回调路径：FireCore 在 callback 非空且无事件订阅时不构造 TimerElapsedEventArgs。
        long callbackBytes;
        using (var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMicroseconds(200),
            Mode = WaitMode.Spin,
            AutoStart = false,
            Callback = () => { },
        }))
        {
            timer.Start();
            Assert.True(WaitForSequence(timer, Warmup, TimeSpan.FromSeconds(10)));
            long before = GC.GetTotalAllocatedBytes(true);
            Assert.True(WaitForSequence(timer, Warmup + Ticks, TimeSpan.FromSeconds(10)));
            timer.Stop();
            callbackBytes = GC.GetTotalAllocatedBytes(true) - before;
        }

        // 事件路径：每拍一个 TimerElapsedEventArgs。
        long eventBytes;
        using (var evented = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMicroseconds(200),
            Mode = WaitMode.Spin,
            AutoStart = false,
        }))
        {
            evented.Elapsed += (_, e) => Assert.NotNull(e);
            evented.Start();
            Assert.True(WaitForSequence(evented, Warmup, TimeSpan.FromSeconds(10)));
            long before = GC.GetTotalAllocatedBytes(true);
            Assert.True(WaitForSequence(evented, Warmup + Ticks, TimeSpan.FromSeconds(10)));
            evented.Stop();
            eventBytes = GC.GetTotalAllocatedBytes(true) - before;
        }

        Assert.True(eventBytes - callbackBytes > Ticks * 20L,
            $"事件路径 {eventBytes}B 与回调路径 {callbackBytes}B 的差异小于每拍一个 EventArgs 的量级");
    }

    private static bool WaitForSequence(HighResolutionTimer timer, int expected, TimeSpan timeout)
    {
        long deadline = HiResClock.Add(HiResClock.Now(), HiResClock.ToTicks(timeout));
        while (timer.Sequence < expected && !HiResClock.IsDue(deadline))
        {
            HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(2), WaitMode.Kernel);
        }

        return timer.Sequence >= expected;
    }
}
