namespace OODD.Timer;

/// <summary>
/// 定时器线程在"距离到期还有一段时间"时所采用的等待策略。
/// </summary>
/// <remarks>
/// 三种策略构成一条"精度 ↔ CPU 占用"的取舍曲线，请根据节拍长短选择：
/// <list type="table">
/// <listheader><description>本机（Windows 11 / 16 核）实测，节拍 1ms × 10000 拍，完整数据见 docs/benchmarks.md。</description></listheader>
/// <item><term><see cref="Spin"/></term><description>每拍迟到 p50 低于 100ns 分辨率、p99 = 43µs；等待期间独占一个逻辑核 100%。</description></item>
/// <item><term><see cref="Hybrid"/></term><description>每拍迟到 p50 低于 100ns、p99 = 45µs；CPU 占用 ≈ 自旋窗口 / 节拍。</description></item>
/// <item><term><see cref="Kernel"/></term><description>每拍迟到 p50 = 37µs（绝对格点，不累加）；裸唤醒延迟 p50 ≈ 0.58ms。CPU ≈ 单核 2%。</description></item>
/// </list>
/// </remarks>
public enum WaitMode
{
    /// <summary>
    /// 纯自旋：线程全程忙等，每次迭代读一次单调时钟。
    /// </summary>
    /// <remarks>
    /// 精度最高（触发点落在时钟粒度以内，本机实测 p50 = 0 tick = 0ns），
    /// 代价是等待期间该逻辑核 100% 占用、无法降频、笔记本耗电。
    /// 适合节拍 &lt; 1ms 或在专用/隔离核上运行的场景。
    /// </remarks>
    Spin = 0,

    /// <summary>
    /// 混合等待（默认）：先用内核等待睡到"到期前一个自旋窗口"，最后在窗口内自旋到点。
    /// </summary>
    /// <remarks>
    /// 自旋窗口由 <see cref="HighResolutionTimerOptions.AdaptiveSpinWindow"/> 自动标定：
    /// 每次内核唤醒都会测量"比预定唤醒点晚了多少"，晚到就把窗口放大到足以覆盖，
    /// 从而保证永远不在到期之后才醒来。窗口稳定后，精度约等于一次自旋迭代的开销（&lt;1µs），
    /// CPU 占用约为 <c>窗口 / 节拍</c>。
    /// <para>
    /// 注意：若平台只提供粗粒度内核等待（Windows 默认节拍 15.6ms），标定出的窗口会达到 20ms 量级，
    /// 此时短节拍的 CPU 占用很高——这正是 <see cref="Spin"/> 与 <see cref="Hybrid"/> 等价的情形。
    /// 库会优先使用内核高分辨率等待（Windows 的 <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>、
    /// POSIX 的 <c>nanosleep</c>）把窗口压到 1ms 量级。
    /// </para>
    /// </remarks>
    Hybrid = 1,

    /// <summary>
    /// 纯内核等待：完全不自旋，靠操作系统的定时对象唤醒线程。
    /// </summary>
    /// <remarks>
    /// CPU 占用接近 0，精度受内核唤醒延迟与调度器抖动限制（本机实测 p50 ≈ 0.34~0.57ms）。
    /// 适合秒级/十分钟级节拍，或对功耗敏感的移动端后台任务。
    /// </remarks>
    Kernel = 2,
}
