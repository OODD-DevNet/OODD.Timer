using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace OODD.Timer;

/// <summary>
/// 高精度单调时钟。对 <see cref="Stopwatch"/> 时间戳做换算与比较，是全库唯一的时间基准。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 <see cref="DateTime"/> / <see cref="DateTimeOffset"/>：</b>
/// 这两者来自系统 wall-clock，Windows 上典型推进粒度是 10~16ms（且会被 NTP 校正、夏令时、手动改表影响），
/// 用它测出来的"定时器误差"本身就有 10ms 量级的噪声，无法用于亚毫秒测量。
/// </para>
/// <para>
/// <b>本类依赖的底层时钟：</b>
/// <list type="bullet">
/// <item>Windows：<c>QueryPerformanceCounter()</c>，<see cref="Frequency"/> 通常为 10_000_000（100ns 一格）。</item>
/// <item>Linux：<c>clock_gettime(CLOCK_MONOTONIC)</c>，.NET 归一化后 <see cref="Frequency"/> 同样上报 10_000_000。</item>
/// <item>macOS / iOS：<c>mach_absolute_time</c>，.NET 归一化为 10_000_000。</item>
/// <item>Android：与 Linux 相同。</item>
/// </list>
/// 也就是说时间戳的单位是"tick"，tick 与纳秒的换算比例由 <see cref="Frequency"/> 决定，
/// 代码中一律通过本类换算，绝不硬编码 100ns。
/// </para>
/// <para>
/// <b>比较规则：</b>判断是否到期一律用"差值符号"而不是"大小比较"，
/// 即 <c>deadline - Now() &lt;= 0</c>。这样在时间戳跨越 <see cref="long"/> 半程之前都不会因回绕而误判
/// （10MHz 下相当于 292 年，实际不可达）。本类提供的 <see cref="IsDue"/> 已按该规则实现。
/// </para>
/// <para>
/// <b>精度说明：</b><see cref="ToNanoseconds(long)"/> 用 <see cref="double"/> 表达，
/// 只有当 tick 数小于 2^53 时才是逐位精确的；本库只换算"时间差"，且差值远小于该上限，
/// 因此不存在精度问题。需要整数纳秒时用 <see cref="ToWholeNanoseconds(long)"/>。
/// </para>
/// </remarks>
public static class HiResClock
{
    /// <summary>一秒等于多少纳秒。</summary>
    public const long NanosecondsPerSecond = 1_000_000_000L;

    /// <summary>一秒等于多少 tick（时钟频率）。Windows/Linux/macOS 上一般是 10_000_000。</summary>
    public static long Frequency => Stopwatch.Frequency;

    /// <summary>底层是否提供了高分辨率时钟。为 <see langword="false"/> 时本库会退化成 wall-clock，精度无法保证。</summary>
    public static bool IsHighResolution => Stopwatch.IsHighResolution;

    /// <summary>一个 tick 等于多少纳秒（浮点，仅用于换算小的时间差）。</summary>
    public static double NanosecondsPerTick { get; } = (double)NanosecondsPerSecond / Stopwatch.Frequency;

    /// <summary>一纳秒等于多少 tick。</summary>
    public static double TicksPerNanosecond { get; } = (double)Stopwatch.Frequency / NanosecondsPerSecond;

    /// <summary>
    /// 取当前单调时钟时间戳。<b>单次调用在 x64 上约 15~25ns</b>，可以放在最紧的自旋循环里。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Now() => Stopwatch.GetTimestamp();

    /// <summary>把 <see cref="TimeSpan"/> 换算成 tick 数，超出可表达范围时饱和到 <see cref="long.MaxValue"/>。</summary>
    /// <param name="interval">要换算的时长；负数按 0 处理。</param>
    public static long ToTicks(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return 0;
        }

        // TimeSpan.TotalSeconds 在极大值时会损失精度，这里先分段：整秒 + 余下部分。
        double seconds = interval.TotalSeconds;
        if (seconds >= double.MaxValue / Frequency)
        {
            return long.MaxValue;
        }

        double ticks = seconds * Frequency;
        return ticks >= long.MaxValue ? long.MaxValue : (long)(ticks + 0.5);
    }

    /// <summary>把纳秒换算成 tick 数（负数按 0 处理）。</summary>
    /// <param name="nanoseconds">纳秒数。允许小数，四舍五入到最近的 tick。</param>
    public static long ToTicks(double nanoseconds)
    {
        if (nanoseconds <= 0)
        {
            return 0;
        }

        double ticks = nanoseconds * TicksPerNanosecond;
        return ticks >= long.MaxValue ? long.MaxValue : (long)(ticks + 0.5);
    }

    /// <summary>把微秒换算成 tick 数（负数按 0 处理）。</summary>
    /// <param name="microseconds">微秒数。</param>
    public static long ToTicksFromMicroseconds(double microseconds) => ToTicks(microseconds * 1_000);

    /// <summary>把毫秒换算成 tick 数（负数按 0 处理）。</summary>
    /// <param name="milliseconds">毫秒数。</param>
    public static long ToTicksFromMilliseconds(double milliseconds) => ToTicks(milliseconds * 1_000_000);

    /// <summary>把 tick 差值换算成 <see cref="TimeSpan"/>。</summary>
    /// <param name="ticks">tick 数（通常是两个时间戳之差）。</param>
    public static TimeSpan ToTimeSpan(long ticks)
    {
        if (ticks <= 0)
        {
            return TimeSpan.Zero;
        }

        // 用 double 先算秒，避免 ticks * 1e7 在极端值下溢出。
        double seconds = ticks / (double)Frequency;
        if (seconds >= TimeSpan.MaxValue.TotalSeconds)
        {
            return TimeSpan.MaxValue;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>把 tick 差值换算成纳秒。</summary>
    /// <param name="ticks">tick 数（通常是两个时间戳之差）。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToNanoseconds(long ticks) => ticks * NanosecondsPerTick;

    /// <summary>把 tick 差值换算成整数纳秒（负数截断为 0）。</summary>
    /// <param name="ticks">tick 数（通常是两个时间戳之差）。</param>
    public static long ToWholeNanoseconds(long ticks)
    {
        if (ticks <= 0)
        {
            return 0;
        }

        // 1e9 与 Frequency 通常为整数倍关系（10MHz -> 100），此时走纯整数路径，无浮点误差、无溢出风险。
        if (NanosecondsPerSecond % Frequency == 0)
        {
            long scale = NanosecondsPerSecond / Frequency;
            return ticks > long.MaxValue / scale ? long.MaxValue : ticks * scale;
        }

        double nanos = ticks * NanosecondsPerTick;
        return nanos >= long.MaxValue ? long.MaxValue : (long)nanos;
    }

    /// <summary>把 tick 差值换算成微秒。</summary>
    /// <param name="ticks">tick 数。</param>
    public static double ToMicroseconds(long ticks) => ticks * NanosecondsPerTick / 1_000.0;

    /// <summary>把 tick 差值换算成毫秒。</summary>
    /// <param name="ticks">tick 数。</param>
    public static double ToMilliseconds(long ticks) => ticks * NanosecondsPerTick / 1_000_000.0;

    /// <summary>把 tick 差值换算成秒。</summary>
    /// <param name="ticks">tick 数。</param>
    public static double ToSeconds(long ticks) => ticks / (double)Frequency;

    /// <summary>从 <paramref name="startingTimestamp"/> 起已经过去了多少纳秒。</summary>
    /// <param name="startingTimestamp">起点时间戳，一般来自 <see cref="Now"/>。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ElapsedNanoseconds(long startingTimestamp) => ToNanoseconds(Now() - startingTimestamp);

    /// <summary>两个时间戳之间相差多少纳秒。</summary>
    /// <param name="startingTimestamp">起点时间戳。</param>
    /// <param name="endingTimestamp">终点时间戳。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ElapsedNanoseconds(long startingTimestamp, long endingTimestamp) => ToNanoseconds(endingTimestamp - startingTimestamp);

    /// <summary>从 <paramref name="startingTimestamp"/> 起已经过去了多少秒。</summary>
    /// <param name="startingTimestamp">起点时间戳。</param>
    public static double ElapsedSeconds(long startingTimestamp) => ToSeconds(Now() - startingTimestamp);

    /// <summary>从 <paramref name="startingTimestamp"/> 起已经过去了多长时间。</summary>
    /// <param name="startingTimestamp">起点时间戳。</param>
    public static TimeSpan Elapsed(long startingTimestamp) => ToTimeSpan(Now() - startingTimestamp);

    /// <summary>
    /// 计算"从现在起 <paramref name="delay"/> 之后"的到期时间戳。
    /// 需要"绝对到期时间 + 逐拍推进"（避免累积漂移）时，请对返回值继续用 <see cref="Add"/> 递增。
    /// </summary>
    /// <param name="delay">相对延迟。</param>
    public static long Deadline(TimeSpan delay) => Add(Now(), ToTicks(delay));

    /// <summary>按 tick 推进一个到期时间戳，超出可表达范围时饱和而不是回绕。</summary>
    /// <param name="timestamp">当前时间戳或到期时间戳。</param>
    /// <param name="ticks">要推进的 tick 数（可为负）。</param>
    public static long Add(long timestamp, long ticks)
    {
        // 用无符号溢出检测，饱和而不是回绕，保证调度循环里不会出现"到期时间穿越回过去"。
        long result = timestamp + ticks;
        bool overflowed = ((timestamp ^ result) & (ticks ^ result)) < 0;
        if (!overflowed)
        {
            return result;
        }

        return ticks >= 0 ? long.MaxValue : long.MinValue;
    }

    /// <summary>按回绕安全的规则判断 <paramref name="deadlineTimestamp"/> 是否已经到期。</summary>
    /// <param name="deadlineTimestamp">目标到期时间戳。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDue(long deadlineTimestamp) => deadlineTimestamp - Now() <= 0;

    /// <summary>距离到期还差多少 tick；已到期返回 0。</summary>
    /// <param name="deadlineTimestamp">目标到期时间戳。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Remaining(long deadlineTimestamp)
    {
        long remaining = deadlineTimestamp - Now();
        return remaining > 0 ? remaining : 0;
    }

    /// <summary>返回用于日志/诊断的一行式平台能力描述。</summary>
    public static string DescribePlatform() =>
        $"clock={Frequency}Hz (high-resolution={IsHighResolution}, 1 tick={NanosecondsPerTick:F1}ns), " +
        $"os={Environment.OSVersion}, 64bit={Environment.Is64BitProcess}, cpus={Environment.ProcessorCount}";
}
