using System.Runtime;
using OODD.Timer;

namespace OODD.Timer.Tests;

/// <summary>
/// 进程级调优开关的行为测试。
/// </summary>
/// <remarks>
/// 这里刻意不断言"精度提升了多少"——那取决于机器与负载，属于基准程序的职责。
/// 本文件守的是三条结构性不变量：调用不得抛、计数进出对称、重复释放不会把状态改坏。
/// 第一条曾真实地失守过：<c>DllImport</c> 的入口点写成大写开头的 <c>TimeBeginPeriod</c>
/// 时，winmm 找不到导出名，调用即抛 <see cref="EntryPointNotFoundException"/>，
/// 而当时没有任何测试走过这条路径。
/// </remarks>
public class RuntimeTuningTests
{
    [Fact]
    public void RequestSystemTimerResolution_不抛异常且计数进出对称()
    {
        int before = RuntimeTuning.SystemTimerResolutionRequestCount;

        IDisposable scope = RuntimeTuning.RequestSystemTimerResolution(1);
        try
        {
            // 计数要么 +1（请求被系统接受），要么不变（被拒绝，返回空操作对象）。
            // 无论如何都不能抛，也不能凭空 +2。
            Assert.InRange(RuntimeTuning.SystemTimerResolutionRequestCount, before, before + 1);
        }
        finally
        {
            scope.Dispose();
        }

        Assert.Equal(before, RuntimeTuning.SystemTimerResolutionRequestCount);
    }

    [Fact]
    public void RequestSystemTimerResolution_嵌套释放后计数归零()
    {
        IDisposable first = RuntimeTuning.RequestSystemTimerResolution(1);
        IDisposable second = RuntimeTuning.RequestSystemTimerResolution(1);

        // 释放顺序故意与申请顺序相反以外的一种：先释放 first。
        first.Dispose();
        first.Dispose(); // 幂等：同一作用域重复释放不得重复递减
        second.Dispose();

        Assert.Equal(0, RuntimeTuning.SystemTimerResolutionRequestCount);
    }

    [Fact]
    public void RequestSystemTimerResolution_只作用于Windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            using IDisposable scope = RuntimeTuning.RequestSystemTimerResolution(1);
            Assert.Equal(0, RuntimeTuning.SystemTimerResolutionRequestCount);
        }
    }

    [Fact]
    public void RequestGcLatencyMode_嵌套时最后一个释放才恢复()
    {
        GCLatencyMode original = GCSettings.LatencyMode;
        IDisposable outer = RuntimeTuning.RequestGcLatencyMode(GCLatencyMode.SustainedLowLatency);
        try
        {
            Assert.Equal(1, RuntimeTuning.GcLatencyRequestCount);

            // 服务器 GC 等场景下运行时拒绝切换模式，库会记录"未生效"但保持计数正确。
            // 只有真的切成功了，才对模式本身下断言。
            if (GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency)
            {
                IDisposable inner = RuntimeTuning.RequestGcLatencyMode(GCLatencyMode.Batch);
                try
                {
                    Assert.Equal(2, RuntimeTuning.GcLatencyRequestCount);

                    // 已有引用在内层不会改动模式，否则 B 会把 A 依赖的模式换掉。
                    Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
                }
                finally
                {
                    inner.Dispose();
                }

                Assert.Equal(1, RuntimeTuning.GcLatencyRequestCount);
                Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
            }
        }
        finally
        {
            outer.Dispose();
        }

        Assert.Equal(0, RuntimeTuning.GcLatencyRequestCount);
        Assert.Equal(original, GCSettings.LatencyMode);
    }

    [Fact]
    public void RequestGcLatencyMode_重复释放不产生负计数()
    {
        IDisposable scope = RuntimeTuning.RequestGcLatencyMode();
        scope.Dispose();
        scope.Dispose();

        Assert.Equal(0, RuntimeTuning.GcLatencyRequestCount);
    }
}
