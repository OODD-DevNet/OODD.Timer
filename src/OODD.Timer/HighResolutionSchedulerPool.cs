namespace OODD.Timer;

/// <summary>
/// 调度器池：持有一组 <see cref="HighResolutionScheduler"/>，把多个定时器分摊到多个线程上。
/// </summary>
/// <remarks>
/// <para>
/// <b>它解决的矛盾：</b>单个 <see cref="HighResolutionScheduler"/> 用一个线程驱动任意多个定时器，
/// 省掉了"每定时器一线程"的自旋开销，代价是同一次回调耗时会推迟该线程上更晚到期的兄弟定时器
/// （见 <see cref="HighResolutionScheduler"/> 说明）。回调不极短、又很多时，
/// 一个线程就成了串行瓶颈——这时把定时器散到 N 个调度器上，就能用 N 个核并行触发。
/// </para>
/// <para>
/// <b>怎么摊：</b><see cref="Next"/> 轮询（round-robin）适合负载均匀的定时器；
/// <see cref="LeastLoaded"/> 按当前挂载数选最闲的一个，适合数量/周期参差的场景。
/// 两者都不看未来耗时，只是经验性的均衡——真要精确隔离，请为关键定时器单独建一个调度器。
/// </para>
/// <para>
/// <b>与"每定时器独占线程"的区别：</b>池只建 N 个线程（N 由你定），
/// 而 N 个定时器独占模式建 N 个；当定时器数量 ≫ N 时池省下线程与自旋，
/// 当定时器数量 ≤ N 时两者等价，池省下的只是"每线程一份的堆管理"。
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using var pool = new HighResolutionSchedulerPool(Environment.ProcessorCount / 2);
/// foreach (var sensor in sensors)
/// {
///     var timer = pool.CreateTimer(new HighResolutionTimerOptions { Period = TimeSpan.FromMicroseconds(200), Callback = sensor.Read });
/// }
/// </code>
/// </example>
public sealed class HighResolutionSchedulerPool : IDisposable
#if NET8_0_OR_GREATER
    , IAsyncDisposable
#endif
{
    private readonly HighResolutionScheduler[] _schedulers;
    private int _nextIndex;
    private int _disposed;

    /// <summary>
    /// 创建调度器池。线程懒创建——每个 <see cref="HighResolutionScheduler"/> 在第一个定时器挂载时才建线程。
    /// </summary>
    /// <param name="count">池中调度器个数，必须 ≥ 1。通常取参与并发的逻辑核数。</param>
    /// <param name="optionsFactory">
    /// 可选：按索引（0..<paramref name="count"/>）定制每个调度器的配置，用于绑核或设实时优先级
    /// （例如把第 i 个调度器绑到第 i 个核）。返回 null 表示该调度器用默认配置。
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> 小于 1。</exception>
    public HighResolutionSchedulerPool(int count, Func<int, HighResolutionSchedulerOptions?>? optionsFactory = null)
    {
        Guard.NotLessThan(count, 1, nameof(count));
        _schedulers = new HighResolutionScheduler[count];
        for (int i = 0; i < count; i++)
        {
            _schedulers[i] = new HighResolutionScheduler(optionsFactory?.Invoke(i));
        }
    }

    /// <summary>创建 <paramref name="count"/> 个用相同配置的调度器（<paramref name="options"/> 会被逐个克隆）。</summary>
    /// <param name="count">调度器个数，必须 ≥ 1。</param>
    /// <param name="options">共享的基准配置；null 用默认值。</param>
    public HighResolutionSchedulerPool(int count, HighResolutionSchedulerOptions? options)
        : this(count, options is null ? null : _ => options)
    {
    }

    /// <summary>池中调度器个数。</summary>
    public int Count => _schedulers.Length;

    /// <summary>按索引取调度器（用于精细控制，例如给某个关键定时器指定线程）。</summary>
    /// <param name="index">0..<see cref="Count"/>。</param>
    public HighResolutionScheduler this[int index] => _schedulers[index];

    /// <summary>轮询返回下一个调度器。</summary>
    /// <exception cref="ObjectDisposedException">池已释放。</exception>
    public HighResolutionScheduler Next()
    {
        GuardPool();
        int slot = (int)((uint)Interlocked.Increment(ref _nextIndex) % (uint)_schedulers.Length);
        return _schedulers[slot];
    }

    /// <summary>返回当前挂载定时器最少的调度器（平票取索引最小的）。</summary>
    /// <exception cref="ObjectDisposedException">池已释放。</exception>
    public HighResolutionScheduler LeastLoaded()
    {
        GuardPool();
        HighResolutionScheduler best = _schedulers[0];
        int bestCount = best.TimerCount;
        for (int i = 1; i < _schedulers.Length; i++)
        {
            int count = _schedulers[i].TimerCount;
            if (count < bestCount)
            {
                best = _schedulers[i];
                bestCount = count;
            }
        }

        return best;
    }

    /// <summary>
    /// 在池中某个调度器（<see cref="Next"/> 轮询）上创建一个定时器。
    /// </summary>
    /// <param name="options">定时器配置；其 <see cref="HighResolutionTimerOptions.Scheduler"/> 会被池分配的调度器覆盖，
    /// <see cref="HighResolutionTimerOptions.ProcessorIndex"/> 必须留空（亲和性属于调度器层）。内部会克隆一份。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ObjectDisposedException">池已释放。</exception>
    public HighResolutionTimer CreateTimer(HighResolutionTimerOptions options)
    {
        Guard.NotNull(options, nameof(options));
        GuardPool();
        HighResolutionTimerOptions clone = options.Clone();
        clone.Scheduler = Next();
        return new HighResolutionTimer(clone);
    }

    /// <summary>
    /// 在池中某个调度器上创建一个已启动的周期定时器。
    /// </summary>
    /// <param name="period">周期。</param>
    /// <param name="callback">每拍调用，在所选调度线程上执行。</param>
    /// <param name="mode">等待策略。</param>
    /// <exception cref="ArgumentNullException"><paramref name="callback"/> 为 null。</exception>
    /// <exception cref="ObjectDisposedException">池已释放。</exception>
    public HighResolutionTimer CreateTimer(TimeSpan period, Action callback, WaitMode mode = WaitMode.Hybrid)
    {
        Guard.NotNull(callback, nameof(callback));
        GuardPool();
        return new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = period,
            Mode = mode,
            Callback = callback,
            Scheduler = Next(),
        });
    }

    /// <inheritdoc />
    public override string ToString()
    {
        int total = 0;
        foreach (HighResolutionScheduler scheduler in _schedulers)
        {
            total += scheduler.TimerCount;
        }

        return $"HighResolutionSchedulerPool({(Volatile.Read(ref _disposed) != 0 ? "disposed" : "live")}): schedulers={_schedulers.Length} timers={total}";
    }

    private void GuardPool()
    {
        Guard.NotDisposed(Volatile.Read(ref _disposed) != 0, this);
    }

    /// <summary>释放池中所有调度器（逐个同步停止线程，上限各 2 秒）。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (HighResolutionScheduler scheduler in _schedulers)
        {
            scheduler.Dispose();
        }
    }

#if NET8_0_OR_GREATER
    /// <summary>异步释放：并发请求所有调度线程停止，再 await 它们全部干净退出，不阻塞调用线程。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task[] pending = new Task[_schedulers.Length];
        for (int i = 0; i < _schedulers.Length; i++)
        {
            pending[i] = _schedulers[i].DisposeAsyncCore().AsTask();
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
    }
#endif
}
