using OODD.Timer;

namespace OODD.Timer.Tests;

/// <summary>
/// 拉模式（<c>SetPeriod(double)</c> / <c>WaitForTrigger</c> / <c>WaitForTriggerAsync</c>）的行为测试：
/// 契约、异常、单拍交付、窗口路径与取消。
/// </summary>
public class PullModeTests
{
    private static HighResolutionTimer NewTimer(TimeSpan period, bool autoStart = false) =>
        new(new HighResolutionTimerOptions
        {
            Period = period,
            Mode = WaitMode.Hybrid,
            AutoStart = autoStart,
        });

    /// <summary>把可能挂住的同步等待放进有界任务里，失败时给出明确信息而不是拖死整个测试进程。</summary>
    private static void RunBounded(Action action, int seconds = 10)
    {
        Task task = Task.Run(action);
        Assert.True(task.Wait(TimeSpan.FromSeconds(seconds)), "拉模式等待在预期时间内没有返回。");
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void SetPeriod_AcceptsFractionalMilliseconds()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(10));

        timer.SetPeriod(0.25);

        Assert.Equal(TimeSpan.FromMilliseconds(0.25), timer.Period);
        Assert.Equal(HiResClock.ToTicks(TimeSpan.FromMilliseconds(0.25)), timer.PeriodTimestamps);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void SetPeriod_RejectsNonPositiveOrNonFinite(double value)
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(10));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => timer.SetPeriod(value));
    }

    [Fact]
    public void WaitForTrigger_DeliversOneTickPerCall()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(5));
        timer.Start();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        RunBounded(() =>
        {
            for (int i = 0; i < 3; i++)
            {
                timer.WaitForTrigger();
            }
        });
        watch.Stop();

        // 3 拍 × 5ms = 15ms；给足机器抖动余量，只排除"立刻返回"和"永远不返回"。
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5));
        Assert.True(timer.Sequence >= 3, $"应至少触发 3 拍，实际 {timer.Sequence}。");
    }

    [Fact]
    public void WaitForTrigger_WithSpinWindow_DeliversAtTheDuePoint()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(2));
        timer.Start();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        RunBounded(() =>
        {
            for (int i = 0; i < 5; i++)
            {
                timer.WaitForTrigger(TimeSpan.FromMicroseconds(200));

                // 交付不能早于应到时刻；也不该落后到"整拍被跳过"的量级。
                long lateTicks = HiResClock.Now() - timer.LastDueTimestamp;
                Assert.InRange(lateTicks, 0, HiResClock.ToTicks(TimeSpan.FromMilliseconds(50)));
            }
        });
        watch.Stop();

        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(8), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void WaitForTrigger_ThrowsWhenTimerIsNotRunning()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(10));

        _ = Assert.Throws<InvalidOperationException>(() => timer.WaitForTrigger());
        _ = Assert.Throws<InvalidOperationException>(() => timer.WaitForTrigger(TimeSpan.FromMicroseconds(200)));

        timer.Start();
        timer.Stop();
        _ = Assert.Throws<InvalidOperationException>(() => timer.WaitForTrigger());
    }

    [Fact]
    public void WaitForTrigger_ThrowsWhenDisposed()
    {
        var timer = NewTimer(TimeSpan.FromMilliseconds(10), autoStart: true);
        timer.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => timer.WaitForTrigger());
        _ = Assert.Throws<ObjectDisposedException>(() => timer.WaitForTrigger(TimeSpan.FromMicroseconds(200)));
    }

    [Fact]
    public void WaitForTrigger_DeliversTheSingleTickOfAOneShotTimer()
    {
        using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(5),
            AutoReset = false,
            Mode = WaitMode.Hybrid,
            AutoStart = false,
        });
        timer.Start();

        // 单次触发后调度线程会立刻把状态置成 Stopped；等待者必须先把这一拍交出去，而不是误抛。
        RunBounded(() => timer.WaitForTrigger());

        Assert.Equal(1, timer.Sequence);
        Assert.Equal(TimerState.Stopped, timer.State);
    }

    [Fact]
    public async Task WaitForTriggerAsync_CompletesPerTick()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(2));
        timer.Start();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 3; i++)
        {
            await timer.WaitForTriggerAsync().ConfigureAwait(true);
        }
        watch.Stop();

        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(4), TimeSpan.FromSeconds(5));
        Assert.True(timer.Sequence >= 3, $"应至少触发 3 拍，实际 {timer.Sequence}。");
    }

    [Fact]
    public async Task WaitForTriggerAsync_HonorsCancellation()
    {
        using var timer = NewTimer(TimeSpan.FromSeconds(1));
        timer.Start();

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => timer.WaitForTriggerAsync(cts.Token)).ConfigureAwait(true);
    }

    [Fact]
    public async Task WaitForTriggerAsync_ThrowsWhenTimerIsNotRunning()
    {
        using var timer = NewTimer(TimeSpan.FromMilliseconds(10));

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => timer.WaitForTriggerAsync()).ConfigureAwait(true);
    }
}
