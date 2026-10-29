// 这些语言特性（init / required / readonly struct / 记录类型）本身不依赖新版运行时，
// 但编译器要求 BCL 里存在对应的“标记类型”。.NET Framework 4.0 的 mscorlib 没有它们，
// netstandard2.0 也缺其中一部分，因此由本程序集补齐——这是社区 polyfill 的标准做法。
// .NET 8+ 的 BCL 自带这些类型，绝不能重复定义（否则 CS0433）。

#if !NET8_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    /// <summary>使 <c>init</c> 访问器可用（C# 9）。</summary>
    internal static class IsExternalInit
    {
    }

    /// <summary>使 <c>required</c> 修饰符可用（C# 11）。</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute
    {
    }

    /// <summary><c>required</c> 成员上由编译器附加的特性标记。</summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;

        public string FeatureName { get; }

        public bool IsOptional { get; set; }
    }
}
#endif

#if NET40
namespace System.Runtime.CompilerServices
{
    /// <summary>使 <c>readonly struct</c> 与 <c>in</c> 参数可用（C# 7.2）。</summary>
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class IsReadOnlyAttribute : Attribute
    {
    }
}

namespace System.Runtime.InteropServices
{
    /// <summary>
    /// .NET 4.0 没有 <c>Architecture</c> 枚举（它随 <c>RuntimeInformation</c> 在 4.7.1 才引入）。
    /// 这里按 BCL 的相同取值补齐，使 <see cref="OODD.Timer.TimerPlatformInfo"/> 的公开形状在所有 TFM 上一致。
    /// </summary>
    public enum Architecture
    {
        /// <summary>32 位 x86。</summary>
        X86 = 0,

        /// <summary>AMD64 / x86-64。</summary>
        X64 = 1,

        /// <summary>32 位 ARM。</summary>
        Arm = 2,

        /// <summary>64 位 ARM。</summary>
        Arm64 = 3,

        /// <summary>WebAssembly。</summary>
        Wasm = 4,

        /// <summary>IBM Z 系列。</summary>
        S390x = 5,

        /// <summary>龙芯 LoongArch 64 位。</summary>
        LoongArch64 = 6,

        /// <summary>ARMv6。</summary>
        Armv6 = 7,

        /// <summary>ppc64le。</summary>
        Ppc64le = 8,
    }
}
#endif
