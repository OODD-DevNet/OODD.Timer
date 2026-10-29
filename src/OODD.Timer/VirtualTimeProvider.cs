namespace OODD.Timer;

#if NET8_0_OR_GREATER

/// <summary>
/// 手动推进的虚拟时钟：<see cref="TimeProvider"/> 的确定性测试实现。
/// </summary>
/// <remarks>
/// <para>
/// <b>它解决什么：</b>被测代码若注入 <see cref="TimeProvider"/> 并用 <see cref="TimeProvider.CreateTimer"/>、
/// <see cref="TimeProvider.GetTimestamp"/>、<see cref="TimeProvider.GetUtcNow"/> 来计时，
/// 用它就能把"等 30 秒超时"这种测试压缩成一次 <see cref="Advance"/> 调用——时间不再真实流逝，
/// 而是由测试代码一格一格拨动，到期回调在 <see cref="Advance"/> 里同步、按序触发。
/// </para>
/// <para>
/// <b>与 <see cref="HighResolutionTimeProvider"/> 的关键区别（别误解）：</b>本类<b>不驱动</b>真实的
/// <see cref="HighResolutionTimer"/>，也不会让任何东西变快——它是纯粹的、与真实线程无关的仿真时钟，
/// 只服务于"消费 TimeProvider 抽象"的代码。真实的高分辨率定时器请用
/// <see cref="HighResolutionTimeProvider"/>。两者是不同用途：一个做确定性单测，一个做真实低延迟触发。
/// </para>
/// <para>
/// <b>线程模型：</b>所有状态由同一把锁串行化，<see cref="Advance"/> 期间到期回调
/// 在<b>调用 <see cref="Advance"/> 的线程上同步执行</b>（触发时不持锁，所以回调里可以安全地
/// <see cref="TimeProvider.CreateTimer"/>、改期或释放）。不要在回调里再调 <see cref="Advance"/>（会重入）。
/// 单线程测试里天然确定。
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var clock = new VirtualTimeProvider();
/// bool fired = false;
/// using var timer = clock.CreateTimer(_ => fired = true, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
/// clock.Advance(TimeSpan.FromSeconds(29));
/// Debug.Assert(!fired);
/// clock.Advance(TimeSpan.FromSeconds(1));
/// Debug.Assert(fired);
/// </code>
/// </example>
public sealed class VirtualTimeProvider : TimeProvider
{
    /// <summary>时间戳频率：与 <see cref="TimeSpan.TicksPerSecond"/> 一致，1 个时间戳单位 = 100ns，换算最直白。</summary>
    private const long Frequency = TimeSpan.TicksPerSecond;

    private readonly object _gate = new();
    private readonly List<VirtualTimer> _timers = new();
    private long _timestamp;
    private DateTimeOffset _utcNow;

    /// <summary>
    /// 创建虚拟时钟。
    /// </summary>
    /// <param name="startUtc">虚拟的当前 UTC 时刻。<see cref="GetTimestamp"/> 从 0 起算，
    /// <see cref="GetUtcNow"/> 从该值随 <see cref="Advance"/> 一起前进。默认 2020-01-01T00:00:00Z。</param>
    public VirtualTimeProvider(DateTimeOffset? startUtc = null)
    {
        _utcNow = startUtc ?? new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public override long TimestampFrequency => Frequency;

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    /// <summary>虚拟时钟固定以 UTC 为本地时区，保证跨机器结果一致。</summary>
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <summary>当前虚拟已流逝时间（自创建起累积的推进量）。</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromTicks(_timestamp);
            }
        }
    }

    /// <summary>当前登记的虚拟定时器数量（含已挂起、未释放的）。</summary>
    public int TimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    /// <summary>
    /// 把虚拟时间向前推进 <paramref name="by"/>，期间按到期先后同步触发所有落在此窗口内的定时器回调。
    /// </summary>
    /// <param name="by">推进量；负数抛 <see cref="ArgumentOutOfRangeException"/>，传 <see cref="TimeSpan.Zero"/>
    /// 会把"恰好在此刻到期"的定时器立即触发一轮。</param>
    /// <remarks>
    /// 回调里新登记或改期的定时器若到期点仍落在本次目标之内，会在同一次 <see cref="Advance"/> 里被接着触发。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="by"/> 为负。</exception>
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(by), by, "虚拟时间不能倒退。");
        }

        long target;
        lock (_gate)
        {
            target = SaturatingAdd(_timestamp, by.Ticks);
        }

        RunUntil(target);
    }

    /// <summary>把虚拟时间直接拨到绝对时间戳 <paramref name="targetTimestamp"/>（不得早于当前）。</summary>
    /// <param name="targetTimestamp">来自 <see cref="GetTimestamp"/> 的量纲（<see cref="Frequency"/> Hz）。</param>
    /// <exception cref="ArgumentOutOfRangeException">目标早于当前虚拟时间。</exception>
    public void AdvanceTo(long targetTimestamp)
    {
        lock (_gate)
        {
            if (targetTimestamp < _timestamp)
            {
                throw new ArgumentOutOfRangeException(nameof(targetTimestamp), targetTimestamp, "虚拟时间不能倒退。");
            }
        }

        RunUntil(targetTimestamp);
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Guard.NotNull(callback, nameof(callback));
        VirtualTimer timer = new(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.ScheduleLocked(dueTime, period, _timestamp);
        }

        return timer;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        lock (_gate)
        {
            return $"VirtualTimeProvider(utc={_utcNow:O}, elapsed={TimeSpan.FromTicks(_timestamp)}, timers={_timers.Count})";
        }
    }

    /// <summary>推进到 <paramref name="target"/>，沿途按到期先后触发；触发时释放锁，允许回调重入登记。</summary>
    private void RunUntil(long target)
    {
        while (true)
        {
            VirtualTimer due;
            TimerCallback callback;
            object? state;

            lock (_gate)
            {
                VirtualTimer? earliest = null;
                for (int i = 0; i < _timers.Count; i++)
                {
                    VirtualTimer candidate = _timers[i];
                    if (candidate._dueAt >= 0 && (earliest is null || candidate._dueAt < earliest._dueAt))
                    {
                        earliest = candidate;
                    }
                }

                if (earliest is null || earliest._dueAt > target)
                {
                    MoveClockLocked(target);
                    return;
                }

                MoveClockLocked(earliest._dueAt);

                // 触发前算好下一拍（或摘下单次项）：把本拍与后续改期解耦，回调重入时状态一致。
                due = earliest;
                callback = due._callback;
                state = due._state;
                due.PrepareAfterFireLocked(_timestamp);
            }

            callback(state);
        }
    }

    /// <summary>把虚拟时钟推进到 <paramref name="to"/>（仅前进；调用方已持锁）。</summary>
    private void MoveClockLocked(long to)
    {
        if (to > _timestamp)
        {
            _utcNow += TimeSpan.FromTicks(to - _timestamp);
            _timestamp = to;
        }
    }

    private static long SaturatingAdd(long a, long b)
    {
        long result = a + b;
        if (((a ^ result) & (b ^ result)) < 0)
        {
            return b >= 0 ? long.MaxValue : long.MinValue;
        }

        return result;
    }

    /// <summary>
    /// 虚拟时钟上的一个定时器：到期点存在所属 <see cref="VirtualTimeProvider"/> 的登记表里，由
    /// <see cref="VirtualTimeProvider.Advance"/> 驱动；同时充当 <see cref="ITimer"/> 句柄。
    /// </summary>
    /// <remarks>
    /// 类型本身是 <see langword="private"/>，故下列 <see langword="internal"/> 字段不会外泄；
    /// 之所以用 internal 而非 private：C# 里外层类型不能直接访问嵌套类型的 private 成员，
    /// 而 <see cref="RunUntil"/> 需要在锁内扫描/读取到期点。
    /// </remarks>
    private sealed class VirtualTimer : ITimer
    {
        private readonly VirtualTimeProvider _owner;
        internal readonly TimerCallback _callback;
        internal readonly object? _state;
        internal long _dueAt = -1;      // 下一次到期的虚拟时间戳；-1 表示未排期（挂起/单次已触发/已释放）
        private long _periodTicks;      // <= 0 表示单次
        private bool _disposed;

        public VirtualTimer(VirtualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        /// <summary>按 TimeProvider 语义排期；调用方须持 <see cref="VirtualTimeProvider._gate"/>。</summary>
        public void ScheduleLocked(TimeSpan dueTime, TimeSpan period, long now)
        {
            if (_disposed)
            {
                return;
            }

            _periodTicks = ToPeriodTicks(period);

            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                // 挂起：保留周期设定，但不排期。
                _dueAt = -1;
                return;
            }

            long dueTicks = dueTime < TimeSpan.Zero ? 0 : dueTime.Ticks;
            _dueAt = SaturatingAdd(now, dueTicks);
        }

        /// <summary>本拍已交付，准备下一拍：单次或已释放则摘除，周期则从本拍到点推进一个周期（无累积漂移）。</summary>
        public void PrepareAfterFireLocked(long now)
        {
            if (_disposed)
            {
                return;
            }

            if (_periodTicks <= 0)
            {
                _dueAt = -1;
                return;
            }

            long scheduled = SaturatingAdd(_dueAt, _periodTicks);
            if (scheduled <= now)
            {
                // 周期远小于本次推进：跳到当前时刻之后的第一拍，绝不回到过去造成同一时刻反复触发。
                scheduled = SaturatingAdd(now, _periodTicks);
            }

            _dueAt = scheduled;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                ScheduleLocked(dueTime, period, _owner._timestamp);
                return true;
            }
        }

        public bool Change(int dueTime, int period) => Change(
            dueTime == Timeout.Infinite ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(dueTime),
            period == Timeout.Infinite ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(period));

        public void Dispose() => DisposeCore();

        public ValueTask DisposeAsync()
        {
            DisposeCore();
            return default;
        }

        private void DisposeCore()
        {
            lock (_owner._gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _dueAt = -1;
                _ = _owner._timers.Remove(this);
            }
        }

        private static long ToPeriodTicks(TimeSpan period) =>
            period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? -1 : period.Ticks;
    }
}

#endif
