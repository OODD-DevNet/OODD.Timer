using System.Diagnostics;
using System.Globalization;
using OODD.Timer;

namespace OODD.Timer.Benchmark;

/// <summary>
/// 精度基准：把常见的等待/定时方式和 OODD.Timer 放在同一口径下对比。
/// </summary>
/// <remarks>
/// <para><b>统一口径：</b>每次触发都相对"理想到期点 = 起跑锚点 + n × 周期"记录累计落后量，
/// 再拆成两个指标输出：<b>每拍迟到</b>（相邻落点间隔减去周期，即抖动）与<b>累积漂移</b>（首尾之差）。
/// 只测"距上一次触发的间隔"会把漂移掩盖掉，那正是普通定时器的致命处；
/// 只报累计落后又分不清"每次都偏一点"和"偏一次但不再累加"，所以两者都给。</para>
/// <para>用法：<c>dotnet run -c Release -- [周期ms] [拍数]</c>，默认 1ms × 1000 拍。</para>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        double periodMs = args.Length > 0
            ? double.Parse(args[0], CultureInfo.InvariantCulture)
            : 1.0;
        int ticks = args.Length > 1
            ? int.Parse(args[1], CultureInfo.InvariantCulture)
            : 1_000;

        if (periodMs <= 0 || ticks < 20)
        {
            Console.Error.WriteLine("用法: dotnet run -c Release -- <周期ms> <拍数(>=20)>");
            return 2;
        }

        var period = TimeSpan.FromMilliseconds(periodMs);
        Console.WriteLine($"# OODD.Timer benchmark  (period={Name(period)}, ticks={ticks})");
        Console.WriteLine($"# {HiResClock.DescribePlatform()}");
        Console.WriteLine($"# {HighResolutionWait.GetPlatformInfo()}");
        Console.WriteLine();

        KernelWakeLatency wake = HighResolutionWait.MeasureKernelWakeLatency(
            Math.Clamp(ticks, 16, 400), period);
        Console.WriteLine($"内核唤醒延迟实测（{wake.SampleCount} 次，目标 {period.TotalMilliseconds}ms）：");
        Console.WriteLine(wake);
        Console.WriteLine();

        var rows = new List<Row>();
        CultureInfo culture = CultureInfo.InvariantCulture;

        // ---------- 同一时刻只跑一个被测者，避免互相污染 ----------
        // Thread.Sleep 的最小可用请求是 1ms，亚毫秒周期只能按 1ms 睡（这正是它做不到高精度的一部分）。
        rows.Add(MeasureSelfPaced($"Thread.Sleep({Math.Max(1, (int)Math.Round(period.TotalMilliseconds))}ms)", period, ticks));
        rows.Add(MeasureHighResolutionWait("HighResolutionWait.Sleep(Kernel)", WaitMode.Kernel, period, ticks));
        rows.Add(MeasureHighResolutionWait("HighResolutionWait.Sleep(Hybrid)", WaitMode.Hybrid, period, ticks));
        rows.Add(MeasureHighResolutionWait("HighResolutionWait.Sleep(Spin)", WaitMode.Spin, period, ticks));
        // System.Threading.Timer 的周期参数按整数毫秒截断，PeriodicTimer 的构造函数
        // 直接对 <1ms 的周期抛 ArgumentOutOfRangeException——亚毫秒节拍它们根本无法表达。
        if (period.TotalMilliseconds >= 1)
        {
            rows.Add(MeasureBclTimer(period, ticks));
            rows.Add(await MeasurePeriodicTimer(period, ticks).ConfigureAwait(false));
        }
        else
        {
            Console.WriteLine($"# 周期 {Name(period)} < 1ms：System.Threading.Timer 与 PeriodicTimer 都无法表达该周期"
                + "（前者按整数毫秒截断，后者构造函数抛 ArgumentOutOfRangeException），这两行从表中略过。");
        }

        rows.Add(MeasureOoddTimer("OODD.Timer (Kernel)", period, ticks, WaitMode.Kernel));
        Probe("OODD.Timer (Kernel) 之后");
        rows.Add(MeasureOoddTimer("OODD.Timer (Hybrid)", period, ticks, WaitMode.Hybrid));
        rows.Add(MeasureOoddTimer("OODD.Timer (Spin)", period, ticks, WaitMode.Spin));

        Console.WriteLine("## Markdown 表格（可直接贴进 README）");
        Console.WriteLine();
        Console.WriteLine("| 实现 | 每拍迟到 p50 | 每拍迟到 p95 | 每拍迟到 p99 | 每拍迟到 max | 累积漂移 | 漂移/拍 | 期间 CPU |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (Row row in rows)
        {
            Console.WriteLine(string.Create(culture,
                $"| {row.Name} | {row.PerTickP50:F1}µs | {row.PerTickP95:F1}µs | {row.PerTickP99:F1}µs | {row.PerTickMax:F1}µs "
                + $"| {row.DriftMilliseconds:F2}ms | {row.DriftPerTickMicroseconds:F2}µs | {row.CpuMilliseconds:F0}ms |"));
        }

        Console.WriteLine();
        Console.WriteLine("## 说明");
        Console.WriteLine($"- 单调时钟粒度 = {HiResClock.NanosecondsPerTick:F1}ns（{HiResClock.Frequency}Hz）。"
            + "低于这个量级的差异无法区分，只说明\"已到点\"。");
        Console.WriteLine("- 每拍迟到 = 相邻两次落点的间隔减去周期，只看抖动，不随运行时长累加。");
        Console.WriteLine("- 累积漂移 = 末拍相对\"锚点 + n×周期\"的落后；为 0 表示按绝对格点推进（无漂移调度），"
            + "非 0 表示误差会随运行时间累加。");
        Console.WriteLine("- 无漂移调度器在过载时不会累加误差，而是合并空拍（表现为某一拍的\"每拍迟到\"= k×周期）；"
            + "看 ticks/missed 可以分辨这两种情况。");
        Console.WriteLine("- 这些数字只描述跑这条命令的这台机器。换机器/换负载必须重测。");

        Console.WriteLine();
        Console.WriteLine("## 直方图（OODD Hybrid）");
        using (var diag = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = period,
            Mode = WaitMode.Hybrid,
            AutoStart = false,
        }))
        {
            diag.Start();
            WaitForTicks(diag, ticks);
            diag.Stop();
            Console.WriteLine(diag.GetStatistics().ToLatenessHistogramText());
            Console.WriteLine(diag.GetStatistics());
        }

        // 收尾复查：内核等待路径一旦被降级（高分辨率句柄创建失败、等待返回错误），
        // Kernel 那几行的数字就会突然变成系统节拍的量级。没有这行对照，
        // 只看表格很难分辨"本来就这么粗"和"中途被降级"。
        Console.WriteLine();
        Probe("全部测量结束");
        KernelWakeLatency wakeAfter = HighResolutionWait.MeasureKernelWakeLatency(
            Math.Clamp(ticks, 16, 400), period);
        Console.WriteLine($"# 收尾复测内核唤醒：{wakeAfter}");

        return 0;
    }

    /// <summary>打印当前内核等待路径的状态；用于发现"跑到一半被降级"。</summary>
    private static void Probe(string stage) =>
        Console.WriteLine($"# [{stage}] {HighResolutionWait.GetPlatformInfo()}");

    /// <summary>一次测量的结果。前四项是"每拍迟到"的分位数，后面是累积口径与成本。</summary>
    private sealed record Row(
        string Name,
        double PerTickP50,
        double PerTickP95,
        double PerTickP99,
        double PerTickMax,
        double DriftMilliseconds,
        double DriftPerTickMicroseconds,
        double CpuMilliseconds);

    /// <summary>
    /// 把"相对理想格点的累计落后"拆成两种口径：
    /// <b>每拍迟到</b>（相邻两次落点的间隔减去周期，反映抖动）与<b>累积漂移</b>（首尾之差，反映是否累加）。
    /// 朴素定时器两者差不多，但无漂移调度器会表现为"每拍几乎为零、累积也几乎为零"，
    /// 而自 paced 的 sleep 循环则是"每拍都超一点、越跑越偏"。
    /// </summary>
    /// <param name="name">行名。</param>
    /// <param name="cumulativeLatenessMicroseconds">每拍的累计迟到（µs），按事件顺序。</param>
    /// <param name="ticks">拍数。</param>
    /// <param name="cpuMs">测量期间进程 CPU 时间（ms）。</param>
    private static Row Build(string name, double[] cumulativeLatenessMicroseconds, int ticks, double cpuMs)
    {
        double[] perTick = new double[cumulativeLatenessMicroseconds.Length];
        for (int i = 0; i < perTick.Length; i++)
        {
            perTick[i] = i == 0
                ? cumulativeLatenessMicroseconds[i]
                : cumulativeLatenessMicroseconds[i] - cumulativeLatenessMicroseconds[i - 1];
        }

        double[] sorted = (double[])perTick.Clone();
        Array.Sort(sorted);
        double first = cumulativeLatenessMicroseconds[0];
        double last = cumulativeLatenessMicroseconds[^1];
        double drift = last - first;
        return new Row(
            name,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            sorted[^1],
            drift / 1000.0,
            ticks > 1 ? drift / (ticks - 1) : 0,
            cpuMs);
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        int index = (int)Math.Round(p * (sorted.Length - 1), MidpointRounding.AwayFromZero);
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static double CpuMs(long beforeNs, long afterNs) => (afterNs - beforeNs) / 1_000_000.0;

    private static long ProcessCpuNanoseconds()
    {
        using Process process = Process.GetCurrentProcess();
        return (long)(process.TotalProcessorTime.TotalMilliseconds * 1_000_000.0);
    }

    /// <summary>最常见也最"朴素"的用法：每次睡一个固定时长，不做任何补偿。</summary>
    private static Row MeasureSelfPaced(string name, TimeSpan period, int ticks)
    {
        long periodTicks = HiResClock.ToTicks(period);
        var lateness = new double[ticks];
        WarmUp();

        long cpuBefore = ProcessCpuNanoseconds();
        long anchor = HiResClock.Now();
        int sleepMs = Math.Max(1, (int)Math.Round(period.TotalMilliseconds));
        for (int i = 0; i < ticks; i++)
        {
            Thread.Sleep(sleepMs);
            lateness[i] = HiResClock.ToMicroseconds(HiResClock.Now() - (anchor + (i + 1) * periodTicks));
        }

        return Build(name, lateness, ticks, CpuMs(cpuBefore, ProcessCpuNanoseconds()));
    }

    private static Row MeasureHighResolutionWait(string name, WaitMode mode, TimeSpan period, int ticks)
    {
        long periodTicks = HiResClock.ToTicks(period);
        var lateness = new double[ticks];
        WarmUp();

        long cpuBefore = ProcessCpuNanoseconds();
        long anchor = HiResClock.Now();
        for (int i = 0; i < ticks; i++)
        {
            HighResolutionWait.Sleep(period, mode);
            lateness[i] = HiResClock.ToMicroseconds(HiResClock.Now() - (anchor + (i + 1) * periodTicks));
        }

        return Build(name, lateness, ticks, CpuMs(cpuBefore, ProcessCpuNanoseconds()));
    }

    private static Row MeasureBclTimer(TimeSpan period, int ticks)
    {
        var lateness = new List<double>(ticks);
        using var done = new ManualResetEventSlim();
        long periodTicks = HiResClock.ToTicks(period);
        long anchor = 0;

        using var timer = new System.Threading.Timer(_ =>
        {
            long now = HiResClock.Now();
            lock (lateness)
            {
                if (lateness.Count == 0)
                {
                    anchor = now;
                }

                lateness.Add(HiResClock.ToMicroseconds(now - (anchor + lateness.Count * periodTicks)));
                if (lateness.Count >= ticks)
                {
                    done.Set();
                }
            }
        }, null, Timeout.Infinite, Timeout.Infinite);

        WarmUp();
        long cpuBefore = ProcessCpuNanoseconds();
        _ = timer.Change(period, period);
        _ = done.Wait(TimeSpan.FromSeconds(Math.Max(30, ticks * period.TotalSeconds * 5)));
        _ = timer.Change(Timeout.Infinite, Timeout.Infinite);
        double cpu = CpuMs(cpuBefore, ProcessCpuNanoseconds());

        lock (lateness)
        {
            return Build($"System.Threading.Timer ({Name(period)})", lateness.ToArray(), ticks, cpu);
        }
    }

    private static async Task<Row> MeasurePeriodicTimer(TimeSpan period, int ticks)
    {
        using var timer = new PeriodicTimer(period);
        var lateness = new List<double>(ticks);
        long periodTicks = HiResClock.ToTicks(period);
        WarmUp();

        long cpuBefore = ProcessCpuNanoseconds();
        long anchor = HiResClock.Now();
        int n = 0;
        while (n < ticks && await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            n++;
            lateness.Add(HiResClock.ToMicroseconds(HiResClock.Now() - (anchor + n * periodTicks)));
        }

        double cpu = CpuMs(cpuBefore, ProcessCpuNanoseconds());
        return Build($"PeriodicTimer ({Name(period)})", lateness.ToArray(), ticks, cpu);
    }

    private static Row MeasureOoddTimer(string name, TimeSpan period, int ticks, WaitMode mode)
    {
        var lateness = new List<double>(ticks);
        using var done = new ManualResetEventSlim();
        long periodTicks = HiResClock.ToTicks(period);
        long anchor = 0;

        using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = period,
            Mode = mode,
            AutoStart = false,
        });

        timer.Elapsed += (_, e) =>
        {
            lock (lateness)
            {
                if (lateness.Count == 0)
                {
                    anchor = e.DueTimestamp;
                }

                // 相对"理想格点"（锚定第一拍的应到时刻）测量，和上面所有被测者同一口径。
                long ideal = anchor + lateness.Count * periodTicks;
                lateness.Add(HiResClock.ToMicroseconds(e.FiredTimestamp - ideal));
                if (lateness.Count >= ticks)
                {
                    done.Set();
                }
            }
        };

        WarmUp();
        long cpuBefore = ProcessCpuNanoseconds();
        timer.Start();
        _ = done.Wait(TimeSpan.FromSeconds(Math.Max(30, ticks * period.TotalSeconds * 5)));
        timer.Stop();
        double cpu = CpuMs(cpuBefore, ProcessCpuNanoseconds());

        // 交叉验证：表格里那列是"相对理想格点"的迟到（含任何恒定偏置），
        // 库自己统计的是"相对本拍 due"的迟到。两者的差就是每次唤醒的固定开销。
        // 取统计要在 lock (lateness) 之外做，避免与回调线程形成锁序交叉。
        string selfStatistics = timer.GetStatistics().ToString();

        lock (lateness)
        {
            Console.WriteLine($"# [{name}] 库内统计：{selfStatistics}");
            return Build(name, lateness.ToArray(), ticks, cpu);
        }
    }

    private static string Name(TimeSpan period) =>
        period.TotalMilliseconds >= 1
            ? $"{period.TotalMilliseconds:F1}ms"
            : $"{period.TotalMicroseconds:F0}us";

    /// <summary>把即将用到的路径先跑热，避免把 JIT 成本算进第一拍。</summary>
    private static void WarmUp()
    {
        for (int i = 0; i < 50; i++)
        {
            HighResolutionWait.SpinUntil(HiResClock.Add(HiResClock.Now(), 10));
        }

        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(1), WaitMode.Hybrid);
    }

    private static void WaitForTicks(HighResolutionTimer timer, int ticks)
    {
        long deadline = HiResClock.Add(HiResClock.Now(), HiResClock.ToTicks(TimeSpan.FromSeconds(60)));
        while (timer.Sequence < ticks && !HiResClock.IsDue(deadline))
        {
            HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(5), WaitMode.Kernel);
        }
    }
}
