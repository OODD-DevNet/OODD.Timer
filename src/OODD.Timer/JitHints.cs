using System.Runtime.CompilerServices;

namespace OODD.Timer;

/// <summary>
/// JIT 提示常量。
/// </summary>
/// <remarks>
/// <see cref="MethodImplOptions.AggressiveOptimization"/> 的值是 512，这个成员在 netcoreapp3.0 才出现，
/// 老 TFM 的枚举里没有它，所以这里用整数常量绕过编译期差异。
/// 未知位会被运行时忽略（.NET Framework 没有分层编译，忽略即正确），因此可以无条件套用。
/// </remarks>
internal static class JitHints
{
    /// <summary>首次调用即编译成完全优化的代码，跳过 tier-0 / OSR 提升。</summary>
    public const MethodImplOptions AggressiveOptimization = (MethodImplOptions)512;
}
