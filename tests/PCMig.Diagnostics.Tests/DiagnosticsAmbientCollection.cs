using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// 装了诊断的测试会改写 **进程级** 的 <c>CoreDiagnostics</c> sink（装配根的真实形态就是全局的）。
/// 因此**所有**会安装 sink 的测试类必须归入同一个不可并行化的集合：
/// 否则两个类同时在跑时，一方 Dispose 会把另一方的 sink 还原成 NoOp，
/// 表现为"事件莫名消失"的假失败（实测踩过：D3a 与 D3b 并发运行）。
///
/// 注意：这不是"把测试变慢来绕过问题"——它对应生产事实：一个进程只有一个装配根。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosticsAmbientCollection
{
    public const string Name = "diagnostics-ambient-sink";
}