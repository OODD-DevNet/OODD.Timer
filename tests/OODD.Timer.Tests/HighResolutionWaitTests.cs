
using OODD.Timer;

namespace OODD.Timer.Tests;

/// <summary>
/// 等待引擎的精度测试。断言的口径是"远好于系统时钟节拍"，而不是绝对数值——
/// 具体数字随机器负载变化，因此在 CI/笔记本上会偏松。
/// </summary>
public class HighResolutionWaitTests
{
    /// <summary>Windows 系统时钟节拍的典型值；本库存在的意义就是打破它。</summary>
    private const double SystemTickMilliseconds = 10.0;

    private static double Median(List<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    [Theory]
    [InlineData(WaitMode.Spin)]
    [InlineData(WaitMode.Hybrid)]
    [InlineData(WaitMode.Kernel)]
    public void Sleep_NeverReturnsEarly(WaitMode mode)
    {
        for (int i = 0; i < 50; i++)
        {
            long start = HiResClock.Now();
            HighResolutionWait.Sleep(TimeSpan.FromMicroseconds(800), mode);
            long lateness = HiResClock.Now() - start - HiResClock.ToTicks(800_000);

            // 允许一个时钟粒度（10MHz 时 100ns）的负误差，来自起止两次读钟的量化。
            Assert.True(lateness >= -2, $"{mode} 提前返回了 {HiResClock.ToNanoseconds(lateness):F0}ns");
        }
    }

    [Theory]
    [InlineData(WaitMode.Spin)]
    [InlineData(WaitMode.Hybrid)]
    public void Sleep_MedianLatenessIsFarBelowSystemTick(WaitMode mode)
    {
        HighResolutionWait.ResetSharedSpinWindow();
        var samples = new List<double>(60);
        for (int i = 0; i < 60; i++)
        {
            long start = HiResClock.Now();
            HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(1), mode);
            samples.Add(HiResClock.ToMilliseconds(HiResClock.Now() - start - HiResClock.ToTicksFromMilliseconds(1)));
        }

        double median = Median(samples);
        Assert.True(median < SystemTickMilliseconds,
            $"{mode} 中位数迟到 {median:F3}ms，已达到系统节拍量级（{HiResClock.DescribePlatform()}）");
    }

    [Fact]
    public void Spin_IsAtLeastAsAccurateAsKernel()
    {
        HighResolutionWait.ResetSharedSpinWindow();
        double spin = MeasureMedianLateness(WaitMode.Spin);
        double kernel = MeasureMedianLateness(WaitMode.Kernel);
        Assert.True(spin <= kernel, $"spin p50={spin:F3}ms 并不优于 kernel p50={kernel:F3}ms");
    }

    private static double MeasureMedianLateness(WaitMode mode)
    {
        var samples = new List<double>(40);
        for (int i = 0; i < 40; i++)
        {
            long start = HiResClock.Now();
            HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(2), mode);
            samples.Add(HiResClock.ToMilliseconds(HiResClock.Now() - start - HiResClock.ToTicksFromMilliseconds(2)));
        }

        return Median(samples);
    }

    [Fact]
    public void Until_PastDeadlineReturnsImmediately()
    {
        long start = HiResClock.Now();
        Assert.True(HiResClock.IsDue(start - 1_000_000));
        Assert.True(HighResolutionWait.Until(start - 1_000_000, WaitMode.Hybrid));
        Assert.True(HiResClock.ToMicroseconds(HiResClock.Now() - start) < 1_000, "已过期的deadline应当立即返回");
    }

    [Fact]
    public void Until_ReachesDeadlineAndReportsLate()
    {
        long deadline = HiResClock.Add(HiResClock.Now(), HiResClock.ToTicksFromMilliseconds(1));
        Assert.True(HighResolutionWait.Until(deadline, WaitMode.Spin));
        Assert.True(HiResClock.IsDue(deadline), "Until 返回 true 时必然已经到期");
    }

    [Fact]
    public void SpinUntil_SmallDurationsLandInsideOneSystemTick()
    {
        var samples = new List<double>(200);
        for (int i = 0; i < 200; i++)
        {
            long deadline = HiResClock.Add(HiResClock.Now(), 300); // 300 tick = 30µs
            long start = HiResClock.Now();
            HighResolutionWait.SpinUntil(deadline);
            samples.Add(HiResClock.ToMicroseconds(HiResClock.Now() - start));
        }

        double median = Median(samples);
        Assert.True(median is >= 20 and < 2_000, $"30µs 的自旋实测中位数 {median:F1}µs");
    }

    [Fact]
    public async Task SleepAsync_CompletesAroundDeadline()
    {
        long start = HiResClock.Now();
        await HighResolutionWait.SleepAsync(TimeSpan.FromMilliseconds(20), WaitMode.Hybrid);
        double ms = HiResClock.ToMilliseconds(HiResClock.Now() - start);
        Assert.True(ms >= 20, $"提前完成：{ms:F3}ms");

        // 允许线程池派发带来的额外延迟，但不能退化成系统节拍量级。
        Assert.True(ms < 20 + 40, $"SleepAsync 完成过晚：{ms:F3}ms");
    }

    [Fact]
    public async Task SleepAsync_ZeroDurationIsSynchronous()
    {
        await HighResolutionWait.SleepAsync(TimeSpan.Zero);
        await HighResolutionWait.SleepAsync(TimeSpan.FromMilliseconds(-1));
    }

    [Fact]
    public async Task SleepAsync_PreCanceledTokenThrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // TaskCanceledException 是 OperationCanceledException 的子类（Task.FromCanceled 产生），
        // 所以这里必须用 ThrowsAny 而不是精确类型匹配。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await HighResolutionWait.SleepAsync(TimeSpan.FromMilliseconds(500), WaitMode.Hybrid, cts.Token));
    }

    [Fact]
    public void SharedSpinWindow_GrowsWithKernelLatencyAndResets()
    {
        HighResolutionWait.ResetSharedSpinWindow();
        double before = HighResolutionWait.SharedSpinWindowNanoseconds;
        Assert.Equal(HighResolutionWait.DefaultSpinWindowNanoseconds, before, HighResolutionWait.MinSpinWindowNanoseconds);

        // 若干次带内核唤醒的等待会推动自适应窗口标定的上限不会越过设定天花板。
        for (int i = 0; i < 40; i++)
        {
            HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(1), WaitMode.Hybrid);
        }

        double after = HighResolutionWait.SharedSpinWindowNanoseconds;
        Assert.True(after >= HighResolutionWait.MinSpinWindowNanoseconds);
        Assert.True(after <= HighResolutionWait.MaxSpinWindowNanoseconds);
        HighResolutionWait.ResetSharedSpinWindow();
        Assert.Equal(HighResolutionWait.DefaultSpinWindowNanoseconds,
            HighResolutionWait.SharedSpinWindowNanoseconds, HighResolutionWait.MinSpinWindowNanoseconds);
    }

    [Fact]
    public void GetPlatformInfo_DescribesThisMachine()
    {
        TimerPlatformInfo info = HighResolutionWait.GetPlatformInfo();
        Assert.False(string.IsNullOrWhiteSpace(info.OsDescription));
        Assert.Equal(HiResClock.Frequency, info.ClockFrequency);
        Assert.True(info.HighResolutionClock);
        Assert.False(string.IsNullOrWhiteSpace(info.KernelWaitImplementation));
        Assert.Contains("kernelWaits=", info.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MeasureKernelWakeLatency_ReturnsOrderedPercentiles()
    {
        KernelWakeLatency latency = HighResolutionWait.MeasureKernelWakeLatency(64, TimeSpan.FromMilliseconds(1));
        Assert.Equal(64, latency.SampleCount);
        Assert.True(latency.P50Lateness <= latency.P99Lateness);
        Assert.True(latency.P99Lateness <= latency.MaxLateness);
        Assert.True(latency.RecommendedSpinWindowNanoseconds >= HighResolutionWait.MinSpinWindowNanoseconds);
        Assert.Contains("p50", latency.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MeasureKernelWakeLatency_RejectsTooFewSamples()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HighResolutionWait.MeasureKernelWakeLatency(4));
    }

    [Fact]
    public async Task KernelMode_ReleasesTheThreadInsteadOfSpinning()
    {
        // 纯内核等待期间不应把 CPU 烧满：用一个粗节拍做对照，
        // 只断言"能睡够"，避免把调度噪声写进断言。
        long start = HiResClock.Now();
        await Task.Run(() => HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(200), WaitMode.Kernel));
        Assert.True(HiResClock.ToMilliseconds(HiResClock.Now() - start) >= 200);
    }
}
