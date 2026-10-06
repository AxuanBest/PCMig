using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PCMig.Diagnostics.Abstractions;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §16：**Event Catalog 生产覆盖矩阵**（机器生成，不可能腐烂）。
///
/// 为什么要有它：目录里有 140 个事件**不等于** 140 个事件都接进了产品。
/// 本测试每次运行都重新扫描生产源码，得出每个事件的真实归属，并**写出矩阵文档**；
/// 同时用"必须项清单"卡住 Stage B 之前不可缺少的观察点。
///
/// 分类口径（客观、可复算）：
///   · <c>Produced</c>      生产代码里有发布点（非消费方、非目录定义自身）；
///   · <c>Deep-only</c>     已产出，但投递类是 Verbose（只在 Deep 模式下采集）；
///   · <c>Reserved</c>      目录里定义了、当前没有生产发布点；
///   · <c>Retired</c>       在 catalog 的 RetiredEventIds 里。
/// </summary>
public sealed class CoverageMatrixTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        throw new InvalidOperationException("找不到仓库根：" + AppContext.BaseDirectory);
    }

    /// <summary>生产源码（排除：目录定义自身、诊断消费方、测试、生成物）。</summary>
    private static IEnumerable<string> ProductionSources()
    {
        var root = RepoRoot();
        var src = Path.Combine(root, "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        // 目录定义本身不算"生产发布点"
                        && !p.Contains($"{Path.DirectorySeparatorChar}Catalog{Path.DirectorySeparatorChar}")
                        // 规则/反馈契约是**消费方**：它们引用事件名用于匹配，不代表有人发布
                        && !p.Contains($"{Path.DirectorySeparatorChar}Analysis{Path.DirectorySeparatorChar}")
                        // 事件目录容器与描述符类型本身
                        && !p.EndsWith("EventCatalog.cs", StringComparison.OrdinalIgnoreCase)
                        && !p.EndsWith("EventDescriptor.cs", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>事件 → 是否在生产代码里被发布（用"事件常量"的引用判断，并给出证据文件）。</summary>
    private static Dictionary<string, string?> BuildProducerIndex()
    {
        var sources = ProductionSources()
            .Select(p => (Path: p, Text: File.ReadAllText(p)))
            .ToArray();

        var index = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var descriptor in EventCatalog.All)
        {
            // 目录里的常量名形如 <Family>Events.<Member>；用 "Events.<Member>" 精确定位，
            // 再要求同一文件里出现发布动作（TryPublish/PublishCore/PublishUiAction/PublishInternal），
            // 避免把"只是引用了名字"的文件当成生产者。
            var member = descriptor.Name.Split('.')[^1];
            var needle = "Events." + member;
            string? evidence = null;

            foreach (var (path, text) in sources)
            {
                if (!text.Contains(needle, StringComparison.Ordinal)) continue;
                if (!text.Contains("TryPublish", StringComparison.Ordinal)
                    && !text.Contains("PublishCore", StringComparison.Ordinal)
                    && !text.Contains("PublishUiAction", StringComparison.Ordinal)
                    && !text.Contains("PublishInternal", StringComparison.Ordinal)) continue;

                evidence = Path.GetRelativePath(RepoRoot(), path);
                break;
            }

            index[descriptor.Name] = evidence;
        }

        return index;
    }

    [Fact]
    public void CoverageMatrixIsGeneratedAndWrittenToDocs()
    {
        var index = BuildProducerIndex();
        var retired = new HashSet<string>(EventCatalog.RetiredEventIds.Select(id => id.ToString()));
        var rows = new List<(string Family, string Name, string Delivery, string Status, string Evidence)>();

        foreach (var descriptor in EventCatalog.All)
        {
            var family = descriptor.Category.ToString();
            var evidence = index.GetValueOrDefault(descriptor.Name);
            var status = retired.Contains(descriptor.EventId.ToString()) ? "Retired"
                : evidence is null ? "Reserved"
                : descriptor.Delivery == DeliveryClass.Verbose ? "Deep-only"
                : "Produced";

            rows.Add((family, descriptor.Name, descriptor.Delivery.ToString(), status, evidence ?? "-"));
        }

        var produced = rows.Count(r => r.Status == "Produced");
        var deepOnly = rows.Count(r => r.Status == "Deep-only");
        var reserved = rows.Count(r => r.Status == "Reserved");

        // ---- 写出矩阵（机器生成 ⇒ 不会与源码脱节）----
        var sb = new StringBuilder();
        sb.AppendLine("# PCMig 诊断事件覆盖矩阵（**自动生成，勿手工编辑**）");
        sb.AppendLine();
        sb.AppendLine("> 由 `tests/PCMig.Diagnostics.Tests/CoverageMatrixTests.cs` 每次运行重新生成：");
        sb.AppendLine("> 扫描生产源码里的事件发布点（排除目录定义自身、规则/反馈消费方、测试）。");
        sb.AppendLine("> 生成时间：不写入文档（保证仓库可复现）。");
        sb.AppendLine();
        sb.AppendLine($"| 事件总数 | Produced | Deep-only | Reserved | Retired |");
        sb.AppendLine($"|---|---|---|---|---|");
        sb.AppendLine($"| {rows.Count} | {produced} | {deepOnly} | {reserved} | {rows.Count(r => r.Status == "Retired")} |");
        sb.AppendLine();
        sb.AppendLine("**口径**：`Produced` = 常规模式即可采集；`Deep-only` = 投递类为 Verbose（仅 Deep 模式采集）；");
        sb.AppendLine("`Reserved` = 目录已定义但当前**没有生产发布点**（= 尚未接入，不得当作已覆盖）；`Retired` = 已停用。");
        sb.AppendLine();
        sb.AppendLine("| 族 | 事件 | 投递类 | 状态 | 生产发布点 |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var row in rows.OrderBy(r => r.Family).ThenBy(r => r.Name))
            sb.AppendLine($"| {row.Family} | `{row.Name}` | {row.Delivery} | {row.Status} | {row.Evidence} |");
        sb.AppendLine();
        sb.AppendLine("## 尚无生产发布点（Reserved）");
        sb.AppendLine();
        foreach (var row in rows.Where(r => r.Status == "Reserved").OrderBy(r => r.Family).ThenBy(r => r.Name))
            sb.AppendLine($"- `{row.Name}`（{row.Family}）");
        sb.AppendLine();

        var docPath = Path.Combine(RepoRoot(), "docs", "诊断系统实施-事件覆盖矩阵.md");
        File.WriteAllText(docPath, sb.ToString(), new UTF8Encoding(false));

        // ---- must-have 清单：Stage B 之前不可缺少的观察点 ----
        var mustHave = new[]
        {
            // 连接/网络/预检
            "NET.ProbeStarted", "NET.DnsResolved", "NET.DnsFailed", "NET.TcpProbeAttempt", "NET.TcpProbeFailed",
            "NET.SmbSessionConnectStarted", "NET.SmbSessionConnected", "NET.SmbSessionReused", "NET.SmbConnectFailed",
            "NET.ShareResolved", "NET.ShareEnumerationFailed",
            "PFL.PreflightStarted", "PFL.PreflightCompleted", "PFL.CheckCompleted", "PFL.TargetVolumeObserved",
            // 扫描/计划
            "FS.ScanStarted", "FS.ScanCompleted", "FS.ScanIncomplete",
            "PLN.PlanRequested", "PLN.PlanCreated", "PLN.PlanEmpty",
            // 传输/robocopy/暂停恢复停止
            "TRN.JobRunStarted", "TRN.JobRunCompleted", "TRN.ObjectStarted", "TRN.ObjectCompleted",
            "TRN.ObjectInterrupted", "TRN.RetryScheduled", "TRN.RetryStarted",
            "TRN.PauseRequestWriteResult", "TRN.PauseRequestCleared", "TRN.PauseObserved", "TRN.Resumed",
            "TRN.StopObserved", "TRN.SpaceAbortRequested",
            "RBC.ProcessStarted", "RBC.ProcessExited", "RBC.ErrorLinesAggregated",
            // 持久化
            "PST.WriteSucceeded", "PST.WriteSkipped", "PST.WriteFailed",
            // 验证/修复
            "VRF.VerifyStarted", "VRF.PlanValidated", "VRF.SourceStatSucceeded", "VRF.TargetStatSucceeded",
            "VRF.SampleSelected", "VRF.HashSucceeded", "VRF.HashFailed", "VRF.Mismatch",
            "VRF.StatsCompletenessObserved", "VRF.Completed",
            "RPR.RepairRequested", "RPR.RepairTargetsCollected", "RPR.RepairNoTargets",
            "RPR.PurgeAttempted", "RPR.PurgeFailed", "RPR.RepairCompleted",
            // 自身诊断 / UI 动作链
            "DIA.SessionStarted", "DIA.SessionCleanShutdown",
            "UI.UserActionObserved", "UI.ActionCompleted", "UI.ProjectionReadback",
        };

        var matrix = rows.ToDictionary(r => r.Name, r => r.Status, StringComparer.Ordinal);
        var missing = mustHave.Where(name => !matrix.TryGetValue(name, out var status) || status == "Reserved").ToArray();

        Assert.True(missing.Length == 0,
            "以下 **must-have** 观察点在 Stage B 之前必须接入，但当前没有生产发布点：" + string.Join(", ", missing) +
            $"（完整矩阵见 {Path.GetRelativePath(RepoRoot(), docPath)}）");

        // 覆盖底线：至少一半事件已接入（防止"目录很热闹、实际没接几个"）。
        Assert.True(produced + deepOnly >= rows.Count / 2,
            $"生产覆盖过低：Produced {produced} + Deep-only {deepOnly} / 共 {rows.Count}");
    }
}