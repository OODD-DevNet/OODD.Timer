namespace OODD.Timer;

#if NET8_0_OR_GREATER

/// <summary>
/// 把高精度时钟/定时器接到 .NET 的 <see cref="TimeProvider"/> 抽象上。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这很有用：</b>业务代码通常不直接 <c>new Timer</c>，而是注入 <see cref="TimeProvider"/>
/// 然后调 <see cref="TimeProvider.CreateTimer"/>。默认实现 <see cref="TimeProvider.System"/> 走的就是
/// <see cref="System.Threading.Timer"/>，也就是那个 15.6ms 节拍。
/// 把容器里的 <see cref="TimeProvider"/> 换成本类，<b>不用改一行业务代码</b>，
/// 现有基于 TimeProvider 的超时、重试、轮询逻辑就全部变成亚毫秒精度。
/// </para>
/// <para>
/// 注意：日期与时区能力（<see cref="TimeProvider.GetUtcNow"/>、<see cref="TimeProvider.LocalTimeZone"/>）
/// 仍然来自系统 wall-clock——本类不伪造日期，只有时间戳与定时器走高分辨率路径。
/// </para>
/// <para>
/// 本类只在 net8.0+ 目标里存在：<see cref="TimeProvider"/> 是 .NET 8 才加入 BCL 的类型，
/// net40 / netstandard2.0 上没有任何可对齐的抽象。
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Microsoft.Extensions.DependencyInjection
/// services.AddSingleton&lt;TimeProvider&gt;(HighResolutionTimeProvider.Instance);
///
/// // 业务代码里照常使用，无需感知 OODD.Timer
/// await timeProvider.Delay(TimeSpan.FromMicroseconds(200), cancellationToken);
/// </code>
/// </example>
public sealed class HighResolutionTimeProvider : TimeProvider
{
    /// <summary>共享实例（无状态，可安全并发使用）。</summary>
    public static HighResolutionTimeProvider Instance { get; } = new();

    /// <summary>调度器；默认复用 <see cref="HighResolutionScheduler.Shared"/>，让所有基于本 Provider 的定时器共用一个线程。</summary>
    private readonly HighResolutionScheduler _scheduler;

    /// <summary>
    /// 创建时间提供者。
    /// </summary>
    /// <param name="scheduler">指定调度器（可为多个定时器共享一个线程）。传 null 用
    /// <see cref="HighResolutionScheduler.Shared"/>。</param>
    public HighResolutionTimeProvider(HighResolutionScheduler? scheduler = null)
    {
        _scheduler = scheduler ?? HighResolutionScheduler.Shared;
    }

    /// <summary>单调时钟频率（Hz），与 <see cref="HiResClock"/> 一致。</summary>
    public override long TimestampFrequency => HiResClock.Frequency;

    /// <summary>取高分辨率单调时间戳。</summary>
    public override long GetTimestamp() => HiResClock.Now();

    // 说明：TimeProvider.GetElapsedTime(long) 与 (long, long) 在 BCL 里是**非虚方法**，不能重写。
    // 它们的实现只依赖 TimestampFrequency 与 GetTimestamp，而这两个已经指向高分辨率单调时钟，
    // 所以基类算出来的差值本身就是纳秒级的，无需干预。
    // TimeProvider.Delay/SleepAsync 同理是扩展方法（TimeProviderExtensions），内部走 CreateTimer，
    // 因此下面 CreateTimer 一旦被重写，provider.Delay(...) 自动获得亚毫秒精度。

    /// <summary>
    /// 创建亚毫秒精度的 <see cref="ITimer"/>，替换默认的 <see cref="System.Threading.Timer"/>。
    /// </summary>
    /// <param name="callback">到期回调，在调度线程上执行。</param>
    /// <param name="state">传给回调的状态对象。</param>
    /// <param name="dueTime">首次延迟；<see cref="Timeout.InfiniteTimeSpan"/> 表示先不启动。</param>
    /// <param name="period">周期；&lt;= 0 表示只触发一次。</param>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Guard.NotNull(callback, nameof(callback));
        return new HighResolutionProviderTimer(this, callback, state, dueTime, period);
    }

    /// <summary>诊断输出。</summary>
    public override string ToString() => $"HighResolutionTimeProvider({HiResClock.Frequency}Hz, scheduler={_scheduler.Name})";

    /// <summary>把 TimeProvider 的回调包装成 <see cref="HighResolutionTimer"/>。</summary>
    private sealed class HighResolutionProviderTimer(HighResolutionTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private readonly object _gate = new();
        private HighResolutionTimer? _timer = CreateTimerCore(owner, callback, state, dueTime, period);
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _timer?.Dispose();
                _timer = CreateTimerCore(owner, callback, state, dueTime, period);

                // 与 System.Threading.Timer 对齐：改成"挂起"（dueTime=-1）同样算改成功，
                // 不能因为内部定时器对象为 null 就报 false。
                return true;
            }
        }

        // 注意：本类继承自 TimeProvider，而 TimeProvider 有一个静态属性 System，
        // 它会在简单名查找时遮蔽命名空间 System，所以这里必须用不限定的 Timeout。
        public bool Change(int dueTime, int period) => Change(
            dueTime == Timeout.Infinite ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(dueTime),
            period == Timeout.Infinite ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(period));

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _timer?.Dispose();
                _timer = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }

        private static HighResolutionTimer? CreateTimerCore(HighResolutionTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                // "先不启动"：TimeProvider 约定 dueTime=-1 表示挂起，等 Change 再放行。
                return null;
            }

            bool repeating = period > TimeSpan.Zero;
            return new HighResolutionTimer(new HighResolutionTimerOptions
            {
                Period = repeating ? period : EnsurePositive(dueTime),
                DueTime = dueTime,
                AutoReset = repeating,
                AutoStart = true,
                Scheduler = owner._scheduler,
                Callback = () => callback(state),
            });
        }

        private static TimeSpan EnsurePositive(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.FromMilliseconds(1);
    }
}

#endif
