using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ 2026-10-15 偶发失败定点修复 ★ **`A5-jobdir-isolation` 集合的正式定义**。
///
/// 【此前的问题】13 个测试类标注了 `[Collection("A5-jobdir-isolation")]`，但仓库里
/// **没有任何 `[CollectionDefinition("A5-jobdir-isolation")]`** —— xUnit 对未声明的集合名
/// 仍会按集合串行化（行为正确），但"这个集合到底保证什么"只散落在各测试类的注释里，
/// 新成员靠口耳相传。本文件把契约写下来（对比 `tests\PCMig.Diagnostics.Tests\DiagnosticsAmbientCollection.cs`
/// 的同类做法）。
///
/// 【本集合保证两件事】
///
/// **① 作业目录隔离（历史原因）**：集合内多个测试类会在构造/释放时改写**进程级**环境变量
/// `PCMIG_JOBS`（例如 <c>P0SourceLampTests</c> 指向 <c>%TEMP%\pcmig-p0lamp-&lt;guid&gt;</c>）。
/// 不串行 ⇒ 别的类可能把自己的作业目录换掉，或让本类的 Create/Open/枚举落在"刚被别的测试删掉的目录"上，
/// 瞬时 IO 异常会被 <c>AdoptExistingJobAsync</c> 的 catch 吞成 <c>return false</c>（重负载下偶发）。
///
/// **② 源证据时钟隔离（2026-10-15 新增）**：<c>ConnectionViewModel.Clock</c> 与
/// <c>ConnectionViewModel.ShareCountProbe</c> 是为验证"证据有效期（默认 60 秒）"而开的**测试缝**，
/// 它们是 <c>internal static</c> 字段（见 <c>src\PCMig.WinUI\Presentation\ConnectionViewModel.cs:73</c>）
/// ⇒ **整个测试进程共享一份**。集合内存在改写者时，读取者会读到被改写的"现在"：
/// <code>
/// PCMig.Core.Tests.SourceConnectionStateTests.P05_FileLockMustNotReachTheLamp_AndRecoveringIsNotGreen
/// Assert.Equal() Failure: Values differ
/// Expected: Connected
/// Actual:   SuspectOrUnknown
/// </code>
/// 该值来自 <c>SourceEvidenceLedger.Derive</c> 的 <c>if (expired &gt; 0) return SuspectOrUnknown;</c>：
/// 证据 <c>AtUtc</c> 写入正常，但读取时 <c>Clock()</c> 已被推进 61 秒 ⇒ <c>age &gt; Ttl</c> ⇒ 过期。
/// 注意污染**只有这一个方向**有效：写入时时钟快会让 <c>age</c> 变负、仍算新鲜，不产生失败。
///
/// 【当前成员（三者必须同集合）】
///   · <c>P0SourceEvidenceFreshnessTests</c> —— 时钟改写者（零 IO，挂哪个集合都安全）；
///   · <c>SourceConnectionStateTests</c> —— 读取者，P05 失败者；
///   · <c>P0SourceLampTests</c> —— 读取者，且必须在本集合以继续享有 ① 的 `PCMIG_JOBS` 隔离。
///
/// 【今后纪律】新增测试类只要满足下列任一条件，就**必须**标注
/// <c>[Collection(JobDirIsolationCollection.Name)]</c>：
///   ① 读写进程级环境变量 <c>PCMIG_JOBS</c>，或触碰真实 <c>%ProgramData%\PCMig\Jobs</c>；
///   ② 改写 <c>ConnectionViewModel.Clock</c> / <c>ShareCountProbe</c>；
///   ③ 构造 <c>ConnectionViewModel</c> 并断言源状态分档（即依赖 <c>Clock()</c> 的"现在"）。
/// 忘记标注 ⇒ 偶发失败复现，而它概率性、极难在单次 CI 运行中被抓住。
///
/// 【为什么不把 Clock 改成实例字段】<c>Clock</c> 被静态读取的地方遍布
/// <c>UpdateSourceState</c> / <c>EvidenceExpiredTargets</c> / <c>SweepEvidenceFreshness</c> /
/// <c>ProbeIfDueAsync</c>；把隔离责任放在**测试集合**上是范围最小、不动产品的做法。
/// 本轮任务令禁止借机重构，故采用集合隔离。
/// </summary>
[CollectionDefinition(JobDirIsolationCollection.Name, DisableParallelization = true)]
public sealed class JobDirIsolationCollection
{
    /// <summary>集合名。历史成员一直手写字符串字面量 <c>"A5-jobdir-isolation"</c>，此常量与之一致。</summary>
    public const string Name = "A5-jobdir-isolation";
}
