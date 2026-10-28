using System.Globalization;
using System.Numerics;
using System.Text;

namespace OODD.Timer;

/// <summary>
/// 一个定时器（或一个调度器）从启动/上次复位以来的精度统计快照。
/// </summary>
/// <remarks>
/// <para>
/// 这是只读的值类型，由 <see cref="HighResolutionTimer.GetStatistics"/> 拷贝生成，
/// 之后不会随定时器继续变化。要复位计数用 <see cref="HighResolutionTimer.ResetStatistics"/>。
/// </para>
/// <para>
/// <b>延迟直方图用的是对数分桶：</b>第 <c>i</c> 桶覆盖 <c>[2^(i-1), 2^i) ns</c>，
/// 所以 <see cref="GetLatenessPercentile"/> 返回的是该桶的上界（偏保守，
/// 例如落在 1µs~2µs 桶内的样本会被报成 2000ns）。这样做的原因是在
/// 100ns ~ 1s 跨 7 个数量级的数据上，等宽分桶要么浪费内存要么丢掉尾部信息。
/// </para>
/// </remarks>
public readonly struct TimerStatistics
{
    /// <summary>直方图桶数（覆盖到 2^63 ns，足够容纳任何现实延迟）。</summary>
    internal const int BucketCount = 64;

    /// <summary>定时器名字。</summary>
    public string? Name { get; init; }

    /// <summary>等待策略。</summary>
    public WaitMode Mode { get; init; }

    /// <summary>统计窗口内的周期（纳秒）。</summary>
    public double PeriodNanoseconds { get; init; }

    /// <summary>实际触发次数（回调进入次数）。</summary>
    public long TickCount { get; init; }

    /// <summary>累计漏拍数（被合并跳过的拍）。</summary>
    public long MissedTicks { get; init; }

    /// <summary>回调抛出异常的累计次数。</summary>
    public long ErrorCount { get; init; }

    /// <summary>第一拍触发时间戳（0 表示还没触发过）。</summary>
    public long FirstFiredTimestamp { get; init; }

    /// <summary>最近一拍触发时间戳。</summary>
    public long LastFiredTimestamp { get; init; }

    /// <summary>统计起点时间戳。</summary>
    public long StartedTimestamp { get; init; }

    /// <summary>迟到量均值（纳秒）。</summary>
    public double MeanLatenessNanoseconds { get; init; }

    /// <summary>迟到量最小值（纳秒）。理想情况是 0~100ns（一次时钟读的开销）。</summary>
    public double MinLatenessNanoseconds { get; init; }

    /// <summary>迟到量最大值（纳秒）。它通常来自 GC 停顿或内核抢占。</summary>
    public double MaxLatenessNanoseconds { get; init; }

    /// <summary>相邻两拍实际间隔均值（纳秒）；未跑满两拍时为 0。</summary>
    public double MeanIntervalNanoseconds { get; init; }

    /// <summary>相邻两拍实际间隔最小值（纳秒）。</summary>
    public double MinIntervalNanoseconds { get; init; }

    /// <summary>相邻两拍实际间隔最大值（纳秒）。漏拍会体现为一个约等于 N×周期的尖峰。</summary>
    public double MaxIntervalNanoseconds { get; init; }

    /// <summary>当前自适应自旋窗口（纳秒）；未启用自适应时为 0。</summary>
    public double SpinWindowNanoseconds { get; init; }

    /// <summary>历史上观测到的最大内核唤醒延迟（纳秒）。</summary>
    public double MaxKernelWakeLatencyNanoseconds { get; init; }

    /// <summary>迟到量对数分桶直方图（内部使用，索引 i 覆盖 [2^(i-1), 2^i) ns）。</summary>
    internal long[] LatenessBuckets { get; init; }

    /// <summary>统计窗口长度（秒）。</summary>
    public double UptimeSeconds => StartedTimestamp == 0 ? 0 : HiResClock.ElapsedSeconds(StartedTimestamp);

    /// <summary>实测触发频率（次/秒）；与 <see cref="PeriodNanoseconds"/> 的倒数对比即可判断是否漏拍。</summary>
    public double FireRatePerSecond => UptimeSeconds > 0 ? TickCount / UptimeSeconds : 0;

    /// <summary>
    /// 抖动率：<c>(p99 迟到 - p50 迟到) / 周期</c>。0 表示完全精确，&gt; 1 表示周期已不可靠。
    /// </summary>
    public double JitterRatio
    {
        get
        {
            if (PeriodNanoseconds <= 0 || TickCount < 2)
            {
                return 0;
            }

            return (GetLatenessPercentile(0.99) - GetLatenessPercentile(0.50)) / PeriodNanoseconds;
        }
    }

    /// <summary>
    /// 取迟到量的分位数（纳秒，对数分桶上界，见类型说明）。
    /// </summary>
    /// <param name="percentile">0~1 之间，例如 0.99 表示 p99。</param>
    public double GetLatenessPercentile(double percentile)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percentile);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 1);

        if (LatenessBuckets is null or { Length: 0 } || TickCount == 0)
        {
            return 0;
        }

        long target = (long)Math.Ceiling(percentile * TickCount);
        if (target < 1)
        {
            target = 1;
        }

        long cumulative = 0;
        for (int i = 0; i < LatenessBuckets.Length; i++)
        {
            cumulative += Volatile.Read(ref LatenessBuckets[i]);
            if (cumulative >= target)
            {
                return i <= 0 ? 1d : Math.Min(Math.Pow(2, i), double.MaxValue);
            }
        }

        return MaxLatenessNanoseconds;
    }

    /// <summary>打印迟到量直方图（每行一个数量级区间），用于诊断和基准输出。</summary>
    public string ToLatenessHistogramText()
    {
        StringBuilder builder = new();
        builder.Append(Name ?? "timer").Append(": ticks=").Append(TickCount)
            .Append(" missed=").Append(MissedTicks).Append(" errors=").Append(ErrorCount)
            .Append(" period=").Append((PeriodNanoseconds / 1000).ToString("F1", CultureInfo.InvariantCulture)).Append("us")
            .AppendLine();
        if (LatenessBuckets is null)
        {
            return builder.ToString();
        }

        long cumulative = 0;
        for (int i = 0; i < LatenessBuckets.Length; i++)
        {
            long count = Volatile.Read(ref LatenessBuckets[i]);
            if (count == 0)
            {
                continue;
            }

            cumulative += count;
            double lower = i <= 0 ? 0 : Math.Pow(2, i - 1);
            double upper = i >= 62 ? double.MaxValue : Math.Pow(2, i);
            builder.Append("  lateness ")
                .Append(FormatNanos(lower)).Append('-').Append(FormatNanos(upper)).Append('\t')
                .Append(count).Append('\t')
                .Append((cumulative * 100.0 / Math.Max(1, TickCount)).ToString("F1", CultureInfo.InvariantCulture)).Append('%')
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string FormatNanos(double nanos) => nanos switch
    {
        < 1_000 => $"{nanos:F0}ns",
        < 1_000_000 => $"{nanos / 1_000:F1}us",
        < 1_000_000_000 => $"{nanos / 1_000_000:F2}ms",
        _ => $"{nanos / 1_000_000_000:F2}s",
    };

    /// <summary>一行摘要，适合写进启动日志。</summary>
    public override string ToString()
    {
        if (TickCount == 0)
        {
            return $"{Name ?? "timer"}: 尚未触发（uptime={UptimeSeconds:F3}s, mode={Mode}）";
        }

        return $"{Name ?? "timer"}: ticks={TickCount} rate={FireRatePerSecond:F1}/s missed={MissedTicks} errors={ErrorCount} "
            + $"lateness p50={GetLatenessPercentile(0.50):F0}ns p99={GetLatenessPercentile(0.99):F0}ns max={MaxLatenessNanoseconds:F0}ns "
            + $"period={PeriodNanoseconds / 1000:F1}us";
    }
}

/// <summary>
/// 延迟量采集器。只有调度线程写入，因此用无锁累加 + 快照拷贝，不给热路径加锁。
/// </summary>
internal sealed class LatencyCollector
{
    private readonly long[] _buckets = new long[TimerStatistics.BucketCount];

    private double _sumLateness;
    private double _sumInterval;
    private double _minLateness = double.MaxValue;
    private double _maxLateness;
    private double _minInterval = double.MaxValue;
    private double _maxInterval;
    private long _tickCount;
    private long _missedTicks;
    private long _errorCount;
    private long _startedTimestamp;
    private long _firstFired;
    private long _lastFired;

    /// <summary>复位所有计数（保留桶数组长度，清零内容）。</summary>
    public void Reset()
    {
        Array.Clear(_buckets);
        _sumLateness = _sumInterval = 0;
        _minLateness = _minInterval = double.MaxValue;
        _maxLateness = _maxInterval = 0;
        _tickCount = _missedTicks = _errorCount = 0;
        _startedTimestamp = HiResClock.Now();
        _firstFired = _lastFired = 0;
    }

    /// <summary>记录一拍。</summary>
    /// <param name="latenessNanos">迟到量（纳秒），负数按 0 处理。</param>
    /// <param name="intervalNanos">与上一拍的间隔（纳秒）；第一拍传 <see cref="double.NaN"/>。</param>
    /// <param name="missedTicks">本拍合并掉的漏拍数。</param>
    /// <param name="firedTimestamp">本拍触发时间戳。</param>
    public void Record(double latenessNanos, double intervalNanos, int missedTicks, long firedTimestamp)
    {
        if (latenessNanos < 0)
        {
            latenessNanos = 0;
        }

        _tickCount++;
        _missedTicks += missedTicks;
        _sumLateness += latenessNanos;
        if (latenessNanos < _minLateness)
        {
            _minLateness = latenessNanos;
        }

        if (latenessNanos > _maxLateness)
        {
            _maxLateness = latenessNanos;
        }

        _buckets[BucketIndex(latenessNanos)]++;
        if (!double.IsNaN(intervalNanos))
        {
            _sumInterval += intervalNanos;
            if (intervalNanos < _minInterval)
            {
                _minInterval = intervalNanos;
            }

            if (intervalNanos > _maxInterval)
            {
                _maxInterval = intervalNanos;
            }
        }

        if (_firstFired == 0)
        {
            _firstFired = firedTimestamp;
        }

        _lastFired = firedTimestamp;
    }

    /// <summary>记录一次回调异常。</summary>
    public void RecordError() => _errorCount++;

    /// <summary>标记启动时刻。</summary>
    public void MarkStarted() => _startedTimestamp = HiResClock.Now();

    /// <summary>生成只读快照。桶数组会被拷贝，因此返回后与定时器不再关联。</summary>
    public TimerStatistics Snapshot(string? name, WaitMode mode, double periodNanoseconds, double spinWindowNanoseconds, double maxKernelWakeLatencyNanoseconds)
    {
        long[] copy = (long[])_buckets.Clone();
        long ticks = _tickCount;
        return new TimerStatistics
        {
            Name = name,
            Mode = mode,
            PeriodNanoseconds = periodNanoseconds,
            TickCount = ticks,
            MissedTicks = _missedTicks,
            ErrorCount = _errorCount,
            StartedTimestamp = _startedTimestamp,
            FirstFiredTimestamp = _firstFired,
            LastFiredTimestamp = _lastFired,
            MeanLatenessNanoseconds = ticks == 0 ? 0 : _sumLateness / ticks,
            MinLatenessNanoseconds = ticks == 0 ? 0 : _minLateness,
            MaxLatenessNanoseconds = _maxLateness,
            MeanIntervalNanoseconds = ticks < 2 ? 0 : _sumInterval / ticks,
            MinIntervalNanoseconds = ticks < 2 ? 0 : _minInterval,
            MaxIntervalNanoseconds = _maxInterval,
            SpinWindowNanoseconds = spinWindowNanoseconds,
            MaxKernelWakeLatencyNanoseconds = maxKernelWakeLatencyNanoseconds,
            LatenessBuckets = copy,
        };
    }

    /// <summary>对数分桶：i 号桶覆盖 [2^(i-1), 2^i) 纳秒。</summary>
    private static int BucketIndex(double nanos)
    {
        if (nanos <= 1)
        {
            return 0;
        }

        if (nanos >= long.MaxValue)
        {
            return TimerStatistics.BucketCount - 1;
        }

        ulong integral = (ulong)nanos;
        return 64 - BitOperations.LeadingZeroCount(integral);
    }
}
