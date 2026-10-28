using OODD.Timer;
using System.Diagnostics;

namespace OODD.Timer.Tests;

/// <summary>
/// 时钟换算与比较语义的测试。这些是纯计算，断言可以写得很严。
/// </summary>
public class HiResClockTests
{
    [Fact]
    public void Frequency_IsHighResolutionAndReasonable()
    {
        Assert.True(HiResClock.IsHighResolution, $"单调时钟退化为 wall-clock：{HiResClock.DescribePlatform()}");
        Assert.True(HiResClock.Frequency >= 1_000_000, $"时钟频率过低：{HiResClock.Frequency}Hz");
        Assert.Equal(Stopwatch.Frequency, HiResClock.Frequency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(999)]
    [InlineData(1_000)]
    [InlineData(12_345_678)]
    [InlineData(1_000_000_000)]
    public void NanosecondRoundTrip_WithinOneTick(long nanoseconds)
    {
        long ticks = HiResClock.ToTicks((double)nanoseconds);
        double back = HiResClock.ToNanoseconds(ticks);

        // 换算误差不会超过半个 tick（10MHz 时即 50ns）。
        Assert.True(Math.Abs(back - nanoseconds) <= HiResClock.NanosecondsPerTick,
            $"{nanoseconds}ns -> {ticks}tick -> {back}ns");
    }

    [Fact]
    public void ToWholeNanoseconds_MatchesToNanosecondsForExactFrequency()
    {
        for (long ticks = 0; ticks < 10_000; ticks += 997)
        {
            long integer = HiResClock.ToWholeNanoseconds(ticks);
            double exact = HiResClock.ToNanoseconds(ticks);
            Assert.True(Math.Abs(integer - exact) < 1.0, $"ticks={ticks}: {integer} vs {exact}");
        }
    }

    [Fact]
    public void ToWholeNanoseconds_ClampsNonPositive()
    {
        Assert.Equal(0, HiResClock.ToWholeNanoseconds(0));
        Assert.Equal(0, HiResClock.ToWholeNanoseconds(-1));
        Assert.Equal(0, HiResClock.ToWholeNanoseconds(long.MinValue));
    }

    [Fact]
    public void TimeSpanRoundTrip_MicrosecondToSeconds()
    {
        foreach (TimeSpan interval in new[]
                 {
                     TimeSpan.FromMicroseconds(1),
                     TimeSpan.FromMicroseconds(37),
                     TimeSpan.FromMilliseconds(1),
                     TimeSpan.FromMilliseconds(15.6),
                     TimeSpan.FromSeconds(1),
                     TimeSpan.FromSeconds(137.5),
                 })
        {
            long ticks = HiResClock.ToTicks(interval);
            TimeSpan back = HiResClock.ToTimeSpan(ticks);
            Assert.True((back - interval).Duration() <= TimeSpan.FromTicks(10),
                $"{interval} -> {ticks}tick -> {back}");
        }
    }

    [Fact]
    public void ToTicks_NegativeAndZeroInputsAreZero()
    {
        Assert.Equal(0, HiResClock.ToTicks(TimeSpan.Zero));
        Assert.Equal(0, HiResClock.ToTicks(TimeSpan.FromMilliseconds(-1)));
        Assert.Equal(0, HiResClock.ToTicks(-0.5));
        Assert.Equal(0, HiResClock.ToTicksFromMicroseconds(-5));
        Assert.Equal(0, HiResClock.ToTicksFromMilliseconds(-5));
    }

    [Fact]
    public void ToTicks_SaturatesInsteadOfOverflowing()
    {
        Assert.Equal(long.MaxValue, HiResClock.ToTicks(TimeSpan.MaxValue));
        Assert.Equal(long.MaxValue, HiResClock.ToTicks(double.MaxValue));
    }

    [Fact]
    public void Add_SaturatesOnOverflowAndHandlesNegative()
    {
        long t = HiResClock.Now();
        Assert.Equal(t + 100, HiResClock.Add(t, 100));
        Assert.Equal(t - 100, HiResClock.Add(t, -100));
        Assert.Equal(long.MaxValue, HiResClock.Add(long.MaxValue, 1));
        Assert.Equal(long.MinValue, HiResClock.Add(long.MinValue, -1));
    }

    [Fact]
    public void IsDue_UsesWrapSafeSubtraction()
    {
        long past = HiResClock.Add(HiResClock.Now(), -HiResClock.Frequency);
        long future = HiResClock.Add(HiResClock.Now(), HiResClock.Frequency);
        Assert.True(HiResClock.IsDue(past));
        Assert.False(HiResClock.IsDue(future));

        // 即使时间戳本身接近边界，差值比较仍然正确（不会因为相加而回绕）。
        Assert.False(HiResClock.IsDue(long.MaxValue));
    }

    [Fact]
    public void Remaining_IsZeroOnceDeadlinePassed()
    {
        Assert.Equal(0, HiResClock.Remaining(HiResClock.Add(HiResClock.Now(), -1000)));
        Assert.True(HiResClock.Remaining(HiResClock.Deadline(TimeSpan.FromSeconds(30))) > 0);
    }

    [Fact]
    public void Now_IsMonotonic()
    {
        long previous = HiResClock.Now();
        for (int i = 0; i < 100_000; i++)
        {
            long current = HiResClock.Now();
            Assert.True(current >= previous, "单调时钟出现回退");
            previous = current;
        }
    }

    [Fact]
    public void ElapsedNanoseconds_MeasuresRealDelay()
    {
        long start = HiResClock.Now();
        Thread.SpinWait(2_000_000);
        long end = HiResClock.Now();

        double twoArg = HiResClock.ElapsedNanoseconds(start, end);
        Assert.Equal(HiResClock.ToNanoseconds(end - start), twoArg);

        // 单参重载等价于"用当前时刻做终点"，因此只会不小于已确定的区间长度。
        Assert.True(HiResClock.ElapsedNanoseconds(start) >= twoArg);
        Assert.True(HiResClock.ElapsedSeconds(start) > 0);
        Assert.True(HiResClock.Elapsed(start) > TimeSpan.Zero);
        Assert.InRange(twoArg, 1.0, double.MaxValue / 2);
    }

    [Fact]
    public void DescribePlatform_ContainsFrequency()
    {
        Assert.Contains(HiResClock.Frequency.ToString(), HiResClock.DescribePlatform(), StringComparison.Ordinal);
    }
}
