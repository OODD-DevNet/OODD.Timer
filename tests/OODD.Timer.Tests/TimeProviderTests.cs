using OODD.Timer;

namespace OODD.Timer.Tests;

/// <summary>
/// <see cref="TimeProvider"/> 集成与静态快捷入口的测试。
/// </summary>
public class TimeProviderTests
{
    [Fact]
    public void TimestampComesFromHiResClock()
    {
        TimeProvider provider = HighResolutionTimeProvider.Instance;
        long before = HiResClock.Now();
        long timestamp = provider.GetTimestamp();
        long after = HiResClock.Now();

        Assert.True(timestamp >= before && timestamp <= after, $"时间戳 {timestamp} 不在 [{before}, {after}] 内");
        Assert.Equal(HiResClock.Frequency, provider.TimestampFrequency);
        Assert.Contains("HighResolutionTimeProvider", provider.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GetElapsedTime_UsesSameArithmeticAsClock()
    {
        TimeProvider provider = HighResolutionTimeProvider.Instance;
        long start = provider.GetTimestamp();
        long end = HiResClock.Add(start, HiResClock.ToTicksFromMilliseconds(250));
        Assert.Equal(250, provider.GetElapsedTime(start, end).TotalMilliseconds, 1);

        // 基类的 GetElapsedTime 是不可重写的具体方法，且不像 HiResClock.ToTimeSpan 那样把负数夹到 0。
        Assert.True(provider.GetElapsedTime(end, start) <= TimeSpan.Zero);
        Assert.True(provider.GetElapsedTime(start) >= TimeSpan.Zero);
    }

    [Fact]
    public async Task ProviderCreateTimer_ServesAsPreciseOneShotDelay()
    {
        // TimeProvider 在本运行时既没有可重写的 Delay，也没有 Delay 扩展方法（实测确认），
        // 因此"精确延迟"通过 provider.CreateTimer(due, -1) 的一次性定时器获得：
        // 它走的是本库的调度线程，精度与 HighResolutionTimer 相同。
        TimeProvider provider = HighResolutionTimeProvider.Instance;
        long start = HiResClock.Now();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using ITimer timer = provider.CreateTimer(_ => tcs.TrySetResult(), null,
            dueTime: TimeSpan.FromMilliseconds(40), period: Timeout.InfiniteTimeSpan);

        await tcs.Task;
        double ms = HiResClock.ToMilliseconds(HiResClock.Now() - start);
        Assert.True(ms >= 40, $"提前完成：{ms:F3}ms");
        Assert.True(ms < 40 + 20, $"过晚完成：{ms:F3}ms");

        // 直接可用的精确异步睡眠（HighResolutionWait.SleepAsync 走同一套调度线程）。
        start = HiResClock.Now();
        await HighResolutionWait.SleepAsync(TimeSpan.FromMilliseconds(20), WaitMode.Hybrid);
        Assert.True(HiResClock.ToMilliseconds(HiResClock.Now() - start) >= 20);
    }

    [Fact]
    public async Task CreateTimer_RepeatsUntilDisposed()
    {
        var provider = new HighResolutionTimeProvider();
        int count = 0;
        using ITimer timer = provider.CreateTimer(
            _ => Interlocked.Increment(ref count),
            state: null,
            dueTime: TimeSpan.FromMilliseconds(2),
            period: TimeSpan.FromMilliseconds(2));

        await WaitUntil(() => Volatile.Read(ref count) >= 10, TimeSpan.FromSeconds(5));
        Assert.True(count >= 10);

        timer.Dispose();
        int snapshot = count;
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(80), WaitMode.Kernel);
        Assert.Equal(snapshot, count);
    }

    [Fact]
    public async Task CreateTimer_InfiniteDueTimeIsParkedUntilChanged()
    {
        var provider = new HighResolutionTimeProvider();
        int count = 0;
        await using var timer = provider.CreateTimer(
            _ => Interlocked.Increment(ref count),
            state: null,
            dueTime: Timeout.InfiniteTimeSpan,
            period: TimeSpan.FromMilliseconds(2));

        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(60), WaitMode.Kernel);
        Assert.Equal(0, count); // -1 表示"挂起"，TimeProvider 约定

        Assert.True(timer.Change(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(2)));
        await WaitUntil(() => Volatile.Read(ref count) >= 3, TimeSpan.FromSeconds(5));

        Assert.True(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
        int parked = Volatile.Read(ref count);
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(60), WaitMode.Kernel);
        Assert.Equal(parked, Volatile.Read(ref count)); // 再次挂起后不再推进
    }

    [Fact]
    public async Task CreateTimer_OneShotPeriodDoesNotRepeat()
    {
        var provider = new HighResolutionTimeProvider();
        int count = 0;
        using ITimer timer = provider.CreateTimer(
            _ => Interlocked.Increment(ref count),
            null,
            dueTime: TimeSpan.FromMilliseconds(3),
            period: Timeout.InfiniteTimeSpan);

        await WaitUntil(() => Volatile.Read(ref count) >= 1, TimeSpan.FromSeconds(5));
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(80), WaitMode.Kernel);
        Assert.Equal(1, Volatile.Read(ref count));
        Assert.True(timer.Change(TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(-1)));
        await WaitUntil(() => Volatile.Read(ref count) >= 2, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CreateTimer_DisposeIsIdempotentAndChangeAfterDisposeFails()
    {
        var provider = new HighResolutionTimeProvider();
        ITimer timer = provider.CreateTimer(_ => { }, null, TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(2));
        timer.Dispose();
        timer.Dispose();
        Assert.False(timer.Change(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
        await timer.DisposeAsync();
    }

    [Fact]
    public async Task Periodic_And_Once_RunAsNamed()
    {
        int ticks = 0;
        using HighResolutionTimer periodic = HighResolutionTimers.Periodic(
            TimeSpan.FromMilliseconds(2), () => Interlocked.Increment(ref ticks), WaitMode.Spin);
        await WaitUntil(() => Volatile.Read(ref ticks) >= 20, TimeSpan.FromSeconds(5));
        Assert.True(periodic.IsRunning);
        Assert.True(ticks >= 20);

        int onceFired = 0;
        using HighResolutionTimer once = HighResolutionTimers.Once(
            TimeSpan.FromMilliseconds(5), () => Interlocked.Increment(ref onceFired));
        Assert.False(once.AutoReset);
        await WaitUntil(() => Volatile.Read(ref onceFired) == 1, TimeSpan.FromSeconds(5));
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(50), WaitMode.Kernel);
        Assert.Equal(1, Volatile.Read(ref onceFired));
    }

    [Fact]
    public async Task RunPeriodicAsync_StopsWhenCancelled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        int rounds = 0;
        long start = HiResClock.Now();

        // 与 PeriodicTimer.WaitForNextTickAsync(ct) 一致：取消时以 OperationCanceledException 结束。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HighResolutionTimers.RunPeriodicAsync(
            TimeSpan.FromMilliseconds(5),
            async _ =>
            {
                Interlocked.Increment(ref rounds);
                await Task.Yield();
            },
            cancellationToken: cts.Token));

        Assert.True(rounds >= 10, $"只跑了 {rounds} 轮");
        Assert.True(HiResClock.ToMilliseconds(HiResClock.Now() - start) >= 140);
    }

    [Fact]
    public async Task RunPeriodicAsync_ValidationAndNormalCompletion()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => HighResolutionTimers.RunPeriodicAsync(TimeSpan.FromMilliseconds(10), null!));

        // 未取消时，body 里主动结束循环（用一个已完成的 TCS 让 while 条件先退出）。
        using var cts = new CancellationTokenSource();
        int rounds = 0;
        Task run = HighResolutionTimers.RunPeriodicAsync(
            TimeSpan.FromMilliseconds(2),
            _ =>
            {
                if (Interlocked.Increment(ref rounds) >= 3)
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            },
            cancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(rounds >= 3);
    }

    [Fact]
    public void HighResolutionTimers_SleepAndTimeProviderShortcut()
    {
        long start = HiResClock.Now();
        HighResolutionTimers.Sleep(TimeSpan.FromMilliseconds(5));
        Assert.True(HiResClock.ToMilliseconds(HiResClock.Now() - start) >= 5);
        Assert.Same(HighResolutionTimeProvider.Instance, HighResolutionTimers.TimeProvider);
    }

    [Fact]
    public void ProviderWithCustomScheduler_IsUsedByCreatedTimers()
    {
        using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { Name = "Tests.Provider" });
        var provider = new HighResolutionTimeProvider(scheduler);
        using ITimer timer = provider.CreateTimer(_ => { }, null, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(40), WaitMode.Kernel);
        Assert.Equal(1, scheduler.TimerCount);
        timer.Dispose();
        Assert.Equal(0, scheduler.TimerCount);
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
