using System.Text.Json;
using OODD.Timer;

namespace OODD.Timer.Tests;

/// <summary>
/// v1.2 新增能力（P1/P2）的功能与验证测试：await using、指标导出、抖动、卸载回调、
/// wall-clock At()、调度器池、虚拟时钟。
/// </summary>
public class FeatureTests
{
    // ============================ ToCsv / ToJson ============================

    [Fact]
    public async Task ToCsv_HasHeaderAndMatchingRowFieldCounts()
    {
        int ticks = 0;
        using HighResolutionTimer timer = HighResolutionTimers.Periodic(
            TimeSpan.FromMilliseconds(2), () => Interlocked.Increment(ref ticks), WaitMode.Spin);
        await WaitUntil(() => Volatile.Read(ref ticks) >= 5, TimeSpan.FromSeconds(5));

        string csv = timer.GetStatistics().ToCsv();
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        string[] header = lines[0].Split(',');
        string[] row = lines[1].Split(',');
        Assert.True(header.Length >= 20, $"列数太少：{header.Length}");
        Assert.Equal(header.Length, row.Length);
        Assert.Equal("name", header[0], StringComparer.Ordinal);
        Assert.True(Array.IndexOf(header, "lateness_p99_ns") >= 0, "缺少 lateness_p99_ns 列");
        Assert.True(Array.IndexOf(header, "max_kernel_wake_ns") >= 0, "缺少 max_kernel_wake_ns 列");
        Assert.Contains("ticks=", timer.GetStatistics().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToJson_IsValidAndCarriesPercentileFields()
    {
        int ticks = 0;
        using HighResolutionTimer timer = HighResolutionTimers.Periodic(
            TimeSpan.FromMilliseconds(2), () => Interlocked.Increment(ref ticks), WaitMode.Spin);
        await WaitUntil(() => Volatile.Read(ref ticks) >= 5, TimeSpan.FromSeconds(5));

        string json = timer.GetStatistics().ToJson();
        using JsonDocument doc = JsonDocument.Parse(json); // 非法 JSON 会在这里抛
        JsonElement root = doc.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("ticks", out JsonElement ticksElement));
        Assert.True(ticksElement.GetInt64() >= 5);
        Assert.True(root.TryGetProperty("lateness_p99_ns", out _));
        Assert.True(root.TryGetProperty("mode", out _));
        Assert.True(root.GetProperty("period_ns").GetDouble() > 0);
    }

    [Fact]
    public void ToJson_EscapesNameWithQuotesAndControlChars()
    {
        // 名字里塞引号/换行，导出必须是合法 JSON（转义生效）。
        using HighResolutionTimer timer = new(new HighResolutionTimerOptions
        {
            Name = "he\"llo\nworld",
            Period = TimeSpan.FromMilliseconds(1),
            AutoStart = false,
        });

        string json = timer.GetStatistics().ToJson();
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("he\"llo\nworld", doc.RootElement.GetProperty("name").GetString());

        string csv = timer.GetStatistics().ToCsv();
        // CSV 里字段被双引号包裹且内嵌引号翻倍。
        Assert.Contains("\"he\"\"llo", csv, StringComparison.Ordinal);
    }

    // ============================ 抖动 jitter ============================

    [Fact]
    public void Jitter_ValidationRejectsOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(10),
            JitterNanoseconds = -1,
        }));

        // 抖动 >= 周期必须拒绝（20ms 周期 = 20_000_000 ns）。
        Assert.Throws<ArgumentOutOfRangeException>(() => new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(20),
            JitterNanoseconds = 20_000_000,
        }));

        Assert.Throws<ArgumentOutOfRangeException>(() => new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(10),
            JitterNanoseconds = double.NaN,
        }));
    }

    [Fact]
    public async Task Jitter_PushesDuePointsLaterByAtMostTheConfiguredAmount()
    {
        const long periodTicks = 20L * TimeSpan.TicksPerMillisecond;   // 20ms
        const long jitterMs = 10;
        var dueStamps = new List<long>();
        var missedFlags = new List<int>();

        using HighResolutionTimer timer = new(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(20),
            Mode = WaitMode.Spin,
            JitterNanoseconds = jitterMs * 1_000_000d,
            AutoStart = false,
        });

        timer.Elapsed += (_, e) =>
        {
            lock (dueStamps)
            {
                dueStamps.Add(e.DueTimestamp);
                missedFlags.Add(e.MissedTicks);
            }
        };

        timer.Start();
        await WaitUntil(() => { lock (dueStamps) { return dueStamps.Count >= 6; } }, TimeSpan.FromSeconds(5));
        timer.Stop();

        long[] snaps;
        int[] misses;
        lock (dueStamps)
        {
            snaps = dueStamps.ToArray();
            misses = missedFlags.ToArray();
        }

        int gapsChecked = 0;
        for (int i = 1; i < snaps.Length; i++)
        {
            if (misses[i] != 0)
            {
                continue; // 漏拍的间隔跨越了多拍，不参与 [周期, 周期+抖动] 断言
            }

            long gap = snaps[i] - snaps[i - 1];
            Assert.True(gap >= periodTicks, $"到期点提前：{HiResClock.ToMilliseconds(gap):F2}ms < 周期");
            Assert.True(gap <= periodTicks + HiResClock.ToTicksFromMilliseconds(jitterMs),
                $"抖动超过上限：{HiResClock.ToMilliseconds(gap):F2}ms");
            gapsChecked++;
        }

        Assert.True(gapsChecked >= 2, $"可核对的相邻拍太少：{gapsChecked}");
    }

    // ============================ 卸载回调 OffloadCallback ============================

    [Fact]
    public async Task OffloadCallback_RunsOffSchedulerAndDropsOverlappingTicks()
    {
        var firstFired = new ManualResetEventSlim(false);
        int fired = 0;

        using HighResolutionTimer timer = new(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(2),
            Mode = WaitMode.Spin,
            OffloadCallback = true,
            AutoStart = false,
            // 回调故意阻塞远大于周期：单条在飞时，后续到期拍应被丢弃计数而非无限排队。
            Callback = () =>
            {
                Interlocked.Increment(ref fired);
                firstFired.Set();
                HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(40), WaitMode.Kernel);
            },
        });

        timer.Start();
        Assert.True(firstFired.Wait(TimeSpan.FromSeconds(5)), "卸载的回调没有执行");
        await Task.Delay(120);
        timer.Stop();

        Assert.True(timer.OffloadedCallbackDrops >= 1,
            $"40ms 阻塞回调 + 2ms 周期本应触发丢弃，实际 drops={timer.OffloadedCallbackDrops}");
        // 调度线程没有被 40ms 回调拖住：卸载生效时定时器仍能照常记账多拍。
        Assert.True(timer.GetStatistics().TickCount >= 10,
            $"调度线程似乎被阻塞：TickCount={timer.GetStatistics().TickCount}");
        Assert.True(Volatile.Read(ref fired) >= 1);
    }

    // ============================ At（wall-clock 绝对时刻）============================

    [Fact]
    public async Task At_FiresOnceNearTheRequestedInstant()
    {
        var fired = new ManualResetEventSlim(false);
        DateTimeOffset target = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(40);
        long startTimestamp = HiResClock.Now();

        using HighResolutionTimer timer = HighResolutionTimers.At(target, () => fired.Set());
        Assert.False(timer.AutoReset);

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), "At() 定时器未触发");
        double elapsedMs = HiResClock.ToMilliseconds(HiResClock.Now() - startTimestamp);
        Assert.True(elapsedMs >= 40 - 5, $"提前触发：{elapsedMs:F2}ms");
        Assert.True(elapsedMs < 200, $"过晚触发：{elapsedMs:F2}ms");

        // 一次性定时器触发后进入 Stopped，不再重复。
        await Task.Delay(60);
        Assert.False(timer.IsRunning);
    }

    [Fact]
    public void At_PastTimeFiresPromptly_AndValidatesCallback()
    {
        var fired = new ManualResetEventSlim(false);
        using HighResolutionTimer timer = HighResolutionTimers.At(
            DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5), () => fired.Set());

        Assert.True(fired.Wait(TimeSpan.FromSeconds(2)), "过去时刻的 At() 应尽快触发");
        Assert.Throws<ArgumentNullException>(() => HighResolutionTimers.At(DateTimeOffset.UtcNow, null!));
    }

    // ============================ 调度器池 ============================

    [Fact]
    public void Pool_RoundRobinLeastLoadedAndLifecycle()
    {
        using var pool = new HighResolutionSchedulerPool(3, new HighResolutionSchedulerOptions { Mode = WaitMode.Kernel });
        Assert.Equal(3, pool.Count);

        Assert.NotNull(pool.Next());
        Assert.NotNull(pool.LeastLoaded());
        Assert.True(ReferenceEquals(pool[0], pool[0]));

        // 空池时 LeastLoaded 取第一个（平票取索引最小）。
        Assert.Same(pool[0], pool.LeastLoaded());

        var timers = new List<HighResolutionTimer>();
        for (int i = 0; i < 6; i++)
        {
            timers.Add(pool.CreateTimer(TimeSpan.FromMilliseconds(10), () => { }, WaitMode.Kernel));
        }

        int totalOnPool = 0;
        for (int i = 0; i < pool.Count; i++)
        {
            totalOnPool += pool[i].TimerCount;
        }

        Assert.Equal(6, totalOnPool);

        foreach (HighResolutionTimer timer in timers)
        {
            timer.Dispose();
        }

        pool.Dispose();
        pool.Dispose(); // 幂等
        Assert.Throws<ObjectDisposedException>(() => pool.CreateTimer(TimeSpan.FromMilliseconds(10), () => { }));
    }

    [Fact]
    public void Pool_RejectsCountBelowOne_AndOptionsCarryScheduler()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HighResolutionSchedulerPool(0));

        using var pool = new HighResolutionSchedulerPool(2);
        HighResolutionTimerOptions options = new() { Period = TimeSpan.FromMilliseconds(5), Callback = () => { } };
        using HighResolutionTimer timer = pool.CreateTimer(options);
        // options 被克隆且 Scheduler 覆盖为池内调度器，传入对象不应被改到已释放。
        Assert.True(timer.IsRunning);
        Assert.Throws<ArgumentNullException>(() => pool.CreateTimer((HighResolutionTimerOptions)null!));
    }

    // ============================ await using (IAsyncDisposable) ============================

    [Fact]
    public async Task AwaitUsing_TimerAndSchedulerDisposeAsyncCleanly()
    {
        await using (HighResolutionTimer timer = new(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(2),
            Mode = WaitMode.Spin,
        }))
        {
            await Task.Delay(30);
            Assert.True(timer.IsRunning);
            Assert.True(timer.GetStatistics().TickCount >= 1);
        }

        // 独占调度器：DisposeAsync 应 await 到那个线程干净退出，不阻塞调用线程。
        await using (HighResolutionScheduler scheduler = new(new HighResolutionSchedulerOptions { Name = "Tests.AwaitUsing" }))
        {
            await using HighResolutionTimer shared = new(new HighResolutionTimerOptions
            {
                Period = TimeSpan.FromMilliseconds(2),
                Scheduler = scheduler,
            });

            await Task.Delay(10);
            Assert.Equal(1, scheduler.TimerCount); // 共享定时器释放前挂在调度器上
        }
    }

    [Fact]
    public async Task AwaitUsing_PoolAndSharedSchedulerAreNotDisposedByTimer()
    {
        await using HighResolutionScheduler scheduler = new(new HighResolutionSchedulerOptions { Name = "Tests.PoolShared" });
        await using (HighResolutionTimer timer = new(new HighResolutionTimerOptions { Period = TimeSpan.FromMilliseconds(2), Scheduler = scheduler }))
        {
            await Task.Delay(10);
        }

        // 共享调度器不属于定时器，定时器释放后调度器仍在用。
        Assert.False(scheduler.IsDisposed);
        Assert.Equal(0, scheduler.TimerCount);

        await using HighResolutionSchedulerPool pool = new(2);
        await using HighResolutionTimer pooled = pool.CreateTimer(TimeSpan.FromMilliseconds(2), () => { });
    }

    // ============================ VirtualTimeProvider ============================

    [Fact]
    public void VirtualClock_AdvanceFiresDueTimerAndNotBefore()
    {
        var clock = new VirtualTimeProvider();
        bool fired = false;
        using ITimer timer = clock.CreateTimer(_ => fired = true, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(fired);
        Assert.Equal(29, clock.Elapsed.TotalSeconds, 3);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(fired);
        Assert.Equal(TimeSpan.FromSeconds(30), clock.Elapsed);
        Assert.Equal(TimeSpan.TicksPerSecond * 30, clock.GetTimestamp());
    }

    [Fact]
    public void VirtualClock_PeriodicReschedulesWithNoDrift()
    {
        var clock = new VirtualTimeProvider();
        int count = 0;
        using ITimer timer = clock.CreateTimer(_ => count++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(5, count); // 恰好落在 1..5 秒的格点，无累积漂移

        // 时间戳与 UTC 一起前进。
        Assert.Equal(TimeSpan.TicksPerSecond * 5, clock.GetTimestamp());
    }

    [Fact]
    public void VirtualClock_ZeroAdvanceFiresImmediatelyDueTimers()
    {
        var clock = new VirtualTimeProvider();
        int count = 0;
        using ITimer timer = clock.CreateTimer(_ => count++, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        clock.Advance(TimeSpan.Zero);
        Assert.Equal(1, count);
    }

    [Fact]
    public void VirtualClock_ParkedUntilChangedAndDisposeIsIdempotent()
    {
        var clock = new VirtualTimeProvider();
        int count = 0;
        ITimer timer = clock.CreateTimer(_ => count++, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, count);

        Assert.True(timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(3, count);

        Assert.True(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)); // 再次挂起
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(3, count);

        timer.Dispose();
        timer.Dispose();
        Assert.False(timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.Equal(0, clock.TimerCount);
    }

    [Fact]
    public void VirtualClock_NegativeAndBackwardsAdvanceThrow()
    {
        var clock = new VirtualTimeProvider();
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.AdvanceTo(clock.GetTimestamp() - 1));
    }

    [Fact]
    public void VirtualClock_CallbackCanCreateAndDisposeTimersReentrantly()
    {
        var clock = new VirtualTimeProvider();
        int nested = 0;
        ITimer? inner = null;
        using ITimer outer = clock.CreateTimer(_ =>
        {
            // 回调里重入登记：到期后一次性再排一个 1 秒后的定时器。
            inner = clock.CreateTimer(__ => Interlocked.Increment(ref nested), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(1)); // 触发 outer，登记 inner
        clock.Advance(TimeSpan.FromSeconds(1)); // 触发 inner
        Assert.Equal(1, nested);
        inner?.Dispose();
    }

    [Fact]
    public void VirtualClock_GetUtcNowHonorsStartParameter()
    {
        DateTimeOffset start = new(2030, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new VirtualTimeProvider(start);
        Assert.Equal(start, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(90));
        Assert.Equal(start + TimeSpan.FromMinutes(90), clock.GetUtcNow());
        Assert.Equal(TimeZoneInfo.Utc, clock.LocalTimeZone);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        long deadline = HiResClock.Add(HiResClock.Now(), HiResClock.ToTicks(timeout));
        while (!condition() && !HiResClock.IsDue(deadline))
        {
            await Task.Delay(2).ConfigureAwait(false);
        }

        Assert.True(condition(), "等待条件超时");
    }
}
