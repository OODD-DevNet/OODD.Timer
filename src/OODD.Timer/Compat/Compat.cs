using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OODD.Timer;

/// <summary>
/// 参数与对象状态校验。
/// </summary>
/// <remarks>
/// BCL 的 <c>ArgumentNullException.ThrowIfNull</c>（.NET 6）、<c>ArgumentOutOfRangeException.ThrowIf*</c>（.NET 7）
/// 与 <c>ObjectDisposedException.ThrowIf</c>（.NET 7）在 net40 / netstandard2.0 上都不存在，
/// 这里给出所有 TFM 共用的等价入口，异常类型与参数名保持一致。
/// </remarks>
internal static class Guard
{
    public static void NotNull(object? value, string paramName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    /// <summary>等价于 <c>ObjectDisposedException.ThrowIf(condition, instance)</c>。</summary>
    public static void NotDisposed(bool condition, object instance)
    {
#if NET8_0_OR_GREATER
        ObjectDisposedException.ThrowIf(condition, instance);
#else
        if (condition)
        {
            throw new ObjectDisposedException(instance.GetType().FullName);
        }
#endif
    }

    public static void NotLessThan(TimeSpan value, TimeSpan minimum, string paramName)
    {
        if (value < minimum)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"值不能小于 {minimum}。");
        }
    }

    public static void NotLessThanOrEqual(TimeSpan value, TimeSpan maximum, string paramName)
    {
        if (value <= maximum)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"值必须大于 {maximum}。");
        }
    }

    public static void NotLessThan(int value, int minimum, string paramName)
    {
        if (value < minimum)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"值不能小于 {minimum}。");
        }
    }

    public static void NotNegative(double value, string paramName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "值不能为负。");
        }
    }

    public static void NotGreaterThan(double value, double maximum, string paramName)
    {
        if (value > maximum)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"值不能大于 {maximum}。");
        }
    }
}

/// <summary>
/// 平台判定与能力描述。net40 用 <see cref="Environment.OSVersion"/>，其余 TFM 用 <see cref="RuntimeInformation"/>。
/// </summary>
internal static class Platform
{
#if NET40
    public static bool IsWindows { get; } = DetectWindows();

    private static bool DetectWindows()
    {
        PlatformID platform = Environment.OSVersion.Platform;
        return platform == PlatformID.Win32NT || platform == PlatformID.Win32Windows
            || platform == PlatformID.Win32S || platform == PlatformID.WinCE;
    }

    /// <summary>CoreCLR 之外（Mono）的 Unix 运行时同样把 <see cref="Thread.Sleep(TimeSpan)"/> 映射到 nanosleep。</summary>
    public static bool IsPosixNanosleep => !IsWindows;

    public static string OsDescription => Environment.OSVersion.VersionString;

    public static Architecture OsArchitecture => IntPtr.Size == 8 ? Architecture.X64 : Architecture.X86;
#else
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static bool IsPosixNanosleep =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        || RuntimeInformation.IsOSPlatform(OSPlatform.Create("FREEBSD"))
        || RuntimeInformation.IsOSPlatform(OSPlatform.Create("ANDROID"))
        || RuntimeInformation.IsOSPlatform(OSPlatform.Create("IOS"))
        || RuntimeInformation.IsOSPlatform(OSPlatform.Create("TVOS"))
        || RuntimeInformation.IsOSPlatform(OSPlatform.Create("WATCHOS"));

    public static string OsDescription => RuntimeInformation.OSDescription;

    public static Architecture OsArchitecture => RuntimeInformation.OSArchitecture;
#endif
}

/// <summary>
/// 与 TFM 无关的数学/时间换算小工具（<c>Math.Clamp</c> 等 API 在旧 TFM 上不存在）。
/// </summary>
internal static class MathCompat
{
    public static long Clamp(long value, long minimum, long maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    public static int Clamp(int value, int minimum, int maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary><c>TimeSpan.TotalMicroseconds</c> 从 .NET 7 才有；1 tick = 100ns，故除以 10。</summary>
    public static double TotalMicroseconds(TimeSpan value) => value.Ticks / 10.0;

    /// <summary>等价于 <c>64 - BitOperations.LeadingZeroCount(value)</c>，即 <c>floor(log2(value)) + 1</c>（value ≥ 1）。</summary>
    public static int Log2PlusOne(ulong value)
    {
#if NET8_0_OR_GREATER
        return 64 - System.Numerics.BitOperations.LeadingZeroCount(value);
#else
        int bits = 0;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }

        return bits;
#endif
    }
}

/// <summary>
/// 内联提示常量：<c>MethodImplOptions.AggressiveInlining</c> 的数值在 .NET 4.5 才命名，
/// 但其底层位值（0x100）一直有效，直接传常量即可在所有 TFM 上表达同一意图。
/// </summary>
internal static class InlineHint
{
    public const MethodImplOptions AggressiveInlining = (MethodImplOptions)0x100;
}

/// <summary>
/// 取消令牌注册的跨 TFM 入口：.NET 6+ 用零分配的 <c>UnsafeRegister</c>，
/// 其余 TFM 退回 <c>Register(..., useSynchronizationContext: false)</c>。
/// </summary>
internal static class CancellationCompat
{
    public static CancellationTokenRegistration Register(Action<object?> callback, object? state, CancellationToken cancellationToken)
    {
#if NET8_0_OR_GREATER
        return cancellationToken.UnsafeRegister(callback, state);
#else
        return cancellationToken.Register(callback, state, useSynchronizationContext: false);
#endif
    }
}

/// <summary>
/// 任务相关的兼容层。<see cref="Task"/> 与 <see cref="TaskCompletionSource{TResult}"/> 在 .NET 4.0 就有，
/// 缺的只是 <c>Task.CompletedTask</c> / <c>Task.FromCanceled</c>（4.6 才有）与
/// <c>TaskCreationOptions.RunContinuationsAsynchronously</c>（4.6 才有）。
/// </summary>
internal static class TaskCompat
{
#if NET40
    private static readonly Task s_completed = CreateCompletedTask();

    private static Task<object?> CreateCompletedTask()
    {
        TaskCompletionSource<object?> source = new();
        source.SetResult(null);
        return source.Task;
    }
#endif

    public static Task CompletedTask
    {
        get
        {
#if NET40
            return s_completed;
#else
            return Task.CompletedTask;
#endif
        }
    }

    public static Task FromCanceled(CancellationToken cancellationToken)
    {
#if NET40
        TaskCompletionSource<object?> source = new();
        source.SetCanceled();
        return source.Task;
#else
        return Task.FromCanceled(cancellationToken);
#endif
    }

    /// <summary>等价于 <c>Task.FromResult</c>（.NET 4.5 才加入）。</summary>
    public static Task<T> FromResult<T>(T value)
    {
#if NET40
        TaskCompletionSource<T> source = new();
        source.SetResult(value);
        return source.Task;
#else
        return Task.FromResult(value);
#endif
    }

    /// <summary>
    /// 创建“续体不在完成线程上内联执行”的 <see cref="TaskCompletionSource{TResult}"/>。
    /// </summary>
    /// <remarks>
    /// .NET 4.6+ 直接传 <c>RunContinuationsAsynchronously</c>；4.0 没有这个选项，
    /// 改由完成方（<c>HighResolutionTimer.CompleteWaiters</c>）把完成动作挪到线程池，效果等价。
    /// </remarks>
    public static TaskCompletionSource<bool> CreateBooleanSource()
    {
#if NET40
        return new TaskCompletionSource<bool>();
#else
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#endif
    }

#if NET40
    /// <summary>任务成功完成后执行 <paramref name="onCompleted"/>，并返回原样传播其完成状态的任务。</summary>
    public static Task Then<T>(Task<T> source, Action<T> onCompleted)
    {
        TaskCompletionSource<object?> completion = new();
        _ = source.ContinueWith(
            task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    Forward(completion, task);
                    return;
                }

                try
                {
                    onCompleted(task.Result);
                }
                catch (Exception exception)
                {
                    _ = completion.TrySetException(exception);
                    return;
                }

                _ = completion.TrySetResult(null);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return completion.Task;
    }

    /// <summary>与 <c>try/finally</c> 等价：无论上游以何种状态结束都执行 <paramref name="onCompleted"/>。</summary>
    public static Task Finally(Task source, Action onCompleted)
    {
        TaskCompletionSource<object?> completion = new();
        _ = source.ContinueWith(
            task =>
            {
                try
                {
                    onCompleted();
                }
                catch (Exception exception)
                {
                    _ = completion.TrySetException(exception);
                    return;
                }

                Forward(completion, task);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return completion.Task;
    }

    /// <summary>把 <paramref name="completed"/> 的完成状态（结果/取消/异常）原样搬到 <paramref name="completion"/> 上。</summary>
    public static void Forward(TaskCompletionSource<object?> completion, Task completed)
    {
        if (completed.IsCanceled)
        {
            _ = completion.TrySetCanceled();
            return;
        }

        if (completed.IsFaulted)
        {
            _ = completion.TrySetException(completed.Exception!.InnerExceptions);
            return;
        }

        _ = completion.TrySetResult(null);
    }
#endif
}

#if NET40
/// <summary>
/// .NET 4.0 没有 <see cref="System.Threading.Volatile"/>（4.5 才加入）。
/// 当前命名空间内的类型优先于 using 导入的同名类型，因此库内所有 <c>Volatile.Read/Write</c>
/// 调用在 4.0 上会绑定到这里，无需改动调用点。
/// </summary>
/// <remarks>
/// 用 <see cref="Thread.VolatileRead"/> / <see cref="Thread.VolatileWrite"/> 实现：它们都是全屏障版本，
/// 语义上强于 <c>Volatile.Read/Write</c>（获取/释放语义），只多一条屏障指令。
/// </remarks>
internal static class Volatile
{
    public static int Read(ref int location) => Thread.VolatileRead(ref location);

    public static long Read(ref long location) => Thread.VolatileRead(ref location);

    public static double Read(ref double location) => Thread.VolatileRead(ref location);

    public static bool Read(ref bool location)
    {
        // Thread.VolatileRead 没有 bool 重载，用全屏障表达同样的获取语义。
        Thread.MemoryBarrier();
        return location;
    }

    public static string Read(ref string location)
    {
        Thread.MemoryBarrier();
        return location;
    }

    public static void Write(ref int location, int value) => Thread.VolatileWrite(ref location, value);

    public static void Write(ref long location, long value) => Thread.VolatileWrite(ref location, value);

    public static void Write(ref double location, double value) => Thread.VolatileWrite(ref location, value);

    public static void Write(ref bool location, bool value)
    {
        Thread.MemoryBarrier();
        location = value;
        Thread.MemoryBarrier();
    }

    public static void Write(ref string location, string value)
    {
        Thread.MemoryBarrier();
        location = value;
        Thread.MemoryBarrier();
    }
}
#endif
