// ============================================================================
//  UiFlushPumpFactory —— 生产薄泵的唯一装配点（★ 本文件**故意不链入测试** ★）
// ============================================================================
//  为什么需要这一层（"分层"的最后一环，缺了它测试项目就编译不过）：
//    MigrationSessionViewModel.cs 被**链入** tests\PCMig.Core.Tests 编译，而该测试项目的
//    DispatcherQueue 替身（TestOnlyDispatcherQueueShim.cs）**没有 DispatcherQueueTimer**。
//    因此链入的 VM 只允许引用"声明里不含 WinUI 类型"的东西：
//      · 纯接口 IUiFlushPump（住在可链入的 UiBatchBuffer.cs）；
//      · 诊断入口 UiFlushTrace（住在可链入的 UiFlushTrace.cs，只用 System.IO）。
//    真实定时器的建立**只**发生在 UiFlushPump.cs（该类持 DispatcherQueueTimer），
//    而 VM 侧通过 `UiFlushPumpFactoryForTest` 测试缝 / 生产外壳注入来取得泵实例。
//
//  ⚠ 对测试的影响（必须知道，否则会写出"假通过"的测试）：
//    链入 VM 时本文件不在编译单元里 ⇒ VM 在生产分支上**故意抛异常**并把话说清楚
//    （见 MigrationSessionViewModel.CreateProductionFlushPump 的注释）。
//    这不是缺陷，而是刻意的边界：测试里 `_queue` 恒为 null（替身 GetForCurrentThread()
//    返回 null）⇒ ReadonlyUiThrottleDisabled = true ⇒ StartFlushTimer 直接 return，
//    永不触碰生产分支。D2 的 timer 生命周期契约由 `UiFlushPumpFactoryForTest` 注入假泵覆盖。
//
//  本文件当前只承载"说明 + 未来的装配扩展点"；真实建立代码在 UiFlushPump.cs。
// ============================================================================

namespace PCMig.WinUI.Presentation;

/// <summary>
/// 生产薄泵装配点的占位（真实建立见 <see cref="DispatcherQueueUiFlushPump"/>）。
/// 保留此类型是为了让"哪些东西不能链入测试"这件事在代码里有一个显式的、可被 grep 的锚点。
/// </summary>
internal static class UiFlushPumpFactory
{
    /// <summary>本文件**不得**被加入 tests\PCMig.Core.Tests.csproj 的链入清单。</summary>
    internal const string LinkContract =
        "UiFlushPump.cs / UiFlushPumpFactory.cs 依赖 Microsoft.UI.Dispatching.DispatcherQueueTimer，禁止链入测试项目。";
}