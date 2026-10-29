namespace OODD.Timer;

/// <summary>
/// 常用入口的静态快捷方法。
/// </summary>
public static class HighResolutionTimers
{
    /// <summary>创建一个正在运行的周期定时器（零分配回调）。</summary>
    /// <param name="period">周期。</param>
    /// <param name="callback">每拍调用，在调度线程上执行。</param>
    /// <param name="mode">等待策略。</param>
    /// <returns>已启动的定时器；请自行 <see cref="HighResolutionTimer.Dispose"/>。</returns>
    public static HighResolutionTimer Periodic(TimeSpan period, Action callback, WaitMode mode = WaitMode.Hybrid)
    {
        Guard.NotNull(callback, nameof(callback));
        return new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = period,
            Mode = mode,
            Callback = callback,
        });
    }

    /// <summary>创建一个单次定时器。</summary>
    /// <param name="delay">延迟时长。</param>
    /// <param name="callback">到期回调。</param>
    /// <param name="mode">等待策略。</param>
    /// <returns>已启动的一次性定时器；触发后自动停表，仍需 <see cref="HighResolutionTimer.Dispose"/> 释放线程。</returns>
    public static HighResolutionTimer Once(TimeSpan delay, Action callback, WaitMode mode = WaitMode.Hybrid)
    {
        Guard.NotNull(callback, nameof(callback));
        return new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = delay,
            DueTime = delay,
            AutoReset = false,
            Mode = mode,
            Callback = callback,
        });
    }

    /// <summary>
    /// 在指定的 wall-clock 时刻触发一次的定时器（绝对时刻对齐触发）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 内部把 <paramref name="when"/> 换算成"距今还有多长"的相对延迟，再交给单调时钟驱动的
    /// <see cref="Once"/>。之所以走单调时钟：定时器本身的推进不依赖 wall-clock
    /// （wall-clock 在 Windows 上粒度 10~16ms、还会被 NTP/夏令时/手动改表拉动）。
    /// </para>
    /// <para>
    /// 因此这里的精度受限于<b>换算那一瞬间</b>的 <see cref="DateTimeOffset"/> 与 monotonic 时钟的对照误差
    /// （约 wall-clock 粒度量级，毫秒级）；换算完成后那一拍的触发精度仍是本库的高分辨率水平。
    /// 若 <paramref name="when"/> 已经过去，立即按 <see cref="TimeSpan.Zero"/> 延迟安排（尽快触发）。
    /// </para>
    /// </remarks>
    /// <param name="when">期望触发的绝对时刻（UTC 或任意带时区偏移的 <see cref="DateTimeOffset"/>）。</param>
    /// <param name="callback">到期回调。</param>
    /// <param name="mode">等待策略。</param>
    /// <returns>已启动的一次性定时器；触发后自动停表，仍需 <see cref="HighResolutionTimer.Dispose"/> 释放线程。</returns>
    public static HighResolutionTimer At(DateTimeOffset when, Action callback, WaitMode mode = WaitMode.Hybrid)
    {
        Guard.NotNull(callback, nameof(callback));
        TimeSpan delay = when - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            // 过去/恰好此刻：钳到一个最小正延迟（1 tick），尽快触发；
            // 不能直接用 0——单次定时器的 Period 必须严格大于零。
            delay = TimeSpan.FromTicks(1);
        }

        return Once(delay, callback, mode);
    }

    /// <summary>
    /// 异步周期循环：<c>while (await timer.WaitForNextTickAsync(ct)) await body(ct)</c> 的封装，
    /// 相当于高分辨率版 <see cref="System.Threading.PeriodicTimer"/>。
    /// </summary>
    /// <param name="period">周期。</param>
    /// <param name="body">每拍执行的异步体。</param>
    /// <param name="mode">等待策略。</param>
    /// <param name="scheduler">共享调度器；null 表示本循环独占一个调度线程。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static Task RunPeriodicAsync(TimeSpan period, Func<CancellationToken, Task> body, WaitMode mode = WaitMode.Hybrid, HighResolutionScheduler? scheduler = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(body, nameof(body));
#if NET40
        PeriodicLoop loop = new(period, body, mode, scheduler, cancellationToken);
        loop.Start();
        return loop.Task;
#else
        return RunPeriodicCoreAsync(period, body, mode, scheduler, cancellationToken);
#endif
    }

#if NET40
    // PeriodicLoop 自己持有并释放定时器（每一条结束路径都会 Dispose），不对外暴露可释放语义。
#pragma warning disable CA1001
    /// <summary>
    /// .NET 4.0 没有 async/await 时的手工版周期循环：每拍拿到 <see cref="HighResolutionTimer.WaitForNextTickAsync"/>
    /// 的任务后再跑一次 <c>body</c>，任何一环失败都先释放定时器再把异常/取消传出去。
    /// </summary>
    private sealed class PeriodicLoop
    {
        private readonly Func<CancellationToken, Task> _body;
        private readonly CancellationToken _cancellationToken;
        private readonly HighResolutionTimer _timer;
        private readonly TaskCompletionSource<object?> _completion = new();

        public PeriodicLoop(TimeSpan period, Func<CancellationToken, Task> body, WaitMode mode, HighResolutionScheduler? scheduler, CancellationToken cancellationToken)
        {
            _body = body;
            _cancellationToken = cancellationToken;
            _timer = new HighResolutionTimer(new HighResolutionTimerOptions
            {
                Period = period,
                Mode = mode,
                Scheduler = scheduler,
                AutoStart = false,
            });
        }

        public Task Task => _completion.Task;

        public void Start()
        {
            _timer.Start();
            WaitTick();
        }

        private void WaitTick() => _ = _timer.WaitForNextTickAsync(_cancellationToken).ContinueWith(
            OnTick,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        private void OnTick(Task<bool> tick)
        {
            if (tick.IsCanceled || tick.IsFaulted)
            {
                Finish(tick);
                return;
            }

            if (!tick.Result)
            {
                // 定时器被释放：与 async 版一致，循环正常结束。
                _timer.Dispose();
                _ = _completion.TrySetResult(null);
                return;
            }

            Task work;
            try
            {
                work = _body(_cancellationToken);
            }
            catch (Exception exception)
            {
                _timer.Dispose();
                _ = _completion.TrySetException(exception);
                return;
            }

            _ = work.ContinueWith(OnBody, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void OnBody(Task work)
        {
            if (work.IsCanceled || work.IsFaulted)
            {
                Finish(work);
                return;
            }

            WaitTick();
        }

        private void Finish(Task completed)
        {
            _timer.Dispose();
            TaskCompat.Forward(_completion, completed);
        }
    }
#pragma warning restore CA1001
#else
    private static async Task RunPeriodicCoreAsync(TimeSpan period, Func<CancellationToken, Task> body, WaitMode mode, HighResolutionScheduler? scheduler, CancellationToken cancellationToken)
    {
        using HighResolutionTimer timer = new(new HighResolutionTimerOptions
        {
            Period = period,
            Mode = mode,
            Scheduler = scheduler,
            AutoStart = false,
        });

        timer.Start();
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await body(cancellationToken).ConfigureAwait(false);
        }
    }
#endif

#if NET8_0_OR_GREATER
    /// <summary>
    /// 高分辨率 <see cref="TimeProvider"/>，可直接注册进依赖注入容器替换默认的
    /// <see cref="TimeProvider.System"/>。
    /// </summary>
    public static TimeProvider TimeProvider => HighResolutionTimeProvider.Instance;
#endif

    /// <summary>精确睡眠（阻塞当前线程）。等价于 <see cref="HighResolutionWait.Sleep(TimeSpan, WaitMode)"/>。</summary>
    /// <param name="duration">睡眠时长。</param>
    /// <param name="mode">等待策略。</param>
    public static void Sleep(TimeSpan duration, WaitMode mode = WaitMode.Hybrid) => HighResolutionWait.Sleep(duration, mode);
}
