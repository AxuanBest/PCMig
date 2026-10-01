// ============================================================================
//  测试专用替身（★ 仅供 tests\PCMig.Core.Tests 编译期使用，不是产品代码 ★）
// ============================================================================
//  为什么存在：
//    A.5 的行为级回归测试需要**真正 new 出** WinUI 侧的 Presentation 对象
//    （DirNode / FileRow / DirectoryTreeViewModel / MigrationSessionViewModel），
//    而这些类型所在的项目（src\PCMig.WinUI）是 WinUI 3 应用（TFM
//    net8.0-windows10.0.19041.0 + UseWinUI=true）。测试项目既不能 project-reference
//    它（会与正在构建它的进程抢文件锁），也不应该为了跑单测而拖入 Windows App SDK
//    运行时。因此采用「源码链入」：把**不依赖 XAML 运行时**的 Presentation 源码
//    作为 Compile 项链进本测试项目（见 PCMig.Core.Tests.csproj）。
//
//    链入文件里唯一的 WinUI 类型依赖就是 Microsoft.UI.Dispatching.DispatcherQueue
//    （只有 DirectoryTreeViewModel.cs 与 MigrationSessionViewModel.cs 用到了它，
//     且只用到 GetForCurrentThread / HasThreadAccess / TryEnqueue 三个成员）。
//
//  为什么这个替身是**保真**的而不是"顺手糊一个"：
//    产品代码自己就显式支持「取不到 DispatcherQueue」这条分支 ——
//      · ctor:      _queue = queue ?? DispatcherQueue.GetForCurrentThread();
//      · OnUiAsync: if (_queue is null) { action(); return Task.CompletedTask; }
//      · Post:      if (_queue is null || _queue.HasThreadAccess) { action(); return; }
//    真实 WinUI 里，在**没有 dispatcher 的线程**上 GetForCurrentThread() 就返回 null；
//    这里让替身恒返回 null，等于让测试永远走"无 UI 队列 ⇒ 同步直通"这条**产品源码里真实存在**
//    的分支，语义与"在非 UI 线程上构造 VM"完全一致，没有伪造任何 UI 行为。
//
//  ★ 风险与约束（违反会静默失真）：
//    1) 本文件声明的 namespace 是 Microsoft.UI.Dispatching —— 一旦本测试项目将来引用
//       WindowsAppSDK / WinUI（PackageReference 或 ProjectReference），就会出现
//       「类型重复定义」编译错误。届时**必须删除本文件**并改用真实类型（那时也就
//       不再需要链入式测试了）。
//    2) 不得在本文件里模拟 UI 行为（不排队、不切线程、不假装 HasThreadAccess=false），
//       否则测试断言的就不再是产品源码的真实分支。
// ============================================================================

// ReSharper disable once CheckNamespace
namespace Microsoft.UI.Dispatching;

/// <summary>与 WinUI 同名同签名的回调委托（仅为让链入源码可编译）。</summary>
public delegate void DispatcherQueueHandler();

/// <summary>
/// <see cref="DispatcherQueue"/> 的编译期替身：<see cref="GetForCurrentThread"/> 恒返回 null，
/// 即产品源码里"当前线程没有 UI 队列"的真实分支（详见文件头说明）。
/// </summary>
public sealed class DispatcherQueue
{
    /// <summary>恒为 null：测试进程里没有 WinUI dispatcher，产品代码会走同步直通分支。</summary>
    public static DispatcherQueue? GetForCurrentThread() => null;

    /// <summary>占位实现（恒 true，因本替身永不产生后台队列）。</summary>
    public bool HasThreadAccess => true;

    /// <summary>占位实现：立即执行（本替身不会产生真正的队列语义）。</summary>
    public bool TryEnqueue(DispatcherQueueHandler callback)
    {
        callback();
        return true;
    }
}