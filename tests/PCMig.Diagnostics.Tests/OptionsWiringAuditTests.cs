using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using PCMig.Diagnostics;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §19：**配置项接线审计**（机器生成 + 防腐烂校验）。
///
/// 纪律：
///   · `Active`    —— 运行时真的读取它（有消费者）；必须能在源码里找到读取点；
///   · `Reserved`  —— 暂时未生效，**不得**在 UI/文档里声称生效；下面有一份显式清单，
///                    且校验"它确实没有消费者"（否则应升级为 Active）；
///   · `Deprecated`—— 删除或明确标记（例如原 `FailOpenToNoOp` 已删除）。
///
/// 产出：`docs\诊断系统实施-配置项接线审计.md`（每次运行重新生成）。
/// </summary>
public sealed class OptionsWiringAuditTests
{
    /// <summary>显式声明的"暂未生效"配置项（每条都必须写明理由，且必须**真的没有**消费者）。</summary>
    private static readonly Dictionary<string, string> Reserved = new(StringComparer.Ordinal)
    {
        // 当前为空：D6.1 §19 审计后，原先 3 个"能配却没用上"的项已分别接入（2 个）或删除（1 个）。
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        throw new InvalidOperationException("找不到仓库根");
    }

    private static string RuntimeSourceText()
    {
        var root = Path.Combine(RepoRoot(), "src", "PCMig.Diagnostics");
        return string.Join("\n", Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.EndsWith("DiagnosticRuntimeOptions.cs", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));
    }

    [Fact]
    public void EveryOptionIsActiveReservedOrDeprecated()
    {
        var properties = typeof(DiagnosticRuntimeOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(properties);

        var runtimeText = RuntimeSourceText();
        var rows = new List<(string Name, string Status, int Uses)>();

        foreach (var name in properties)
        {
            // 消费者计数：`_options.X` / `options.X` / `.X`（在运行时源码里）
            var uses = System.Text.RegularExpressions.Regex.Matches(runtimeText, @"\." + System.Text.RegularExpressions.Regex.Escape(name) + @"\b").Count;
            var status = Reserved.ContainsKey(name) ? "Reserved" : uses > 0 ? "Active" : "Unclassified";
            rows.Add((name, status, uses));
        }

        // ★ 防腐烂 ① ★ 声明为 Reserved 的项必须**确实没有**消费者（否则应改成 Active）。
        foreach (var (name, reason) in Reserved)
        {
            var row = rows.SingleOrDefault(r => r.Name == name);
            Assert.True(row.Name is not null, $"Reserved 清单里的 {name} 已不在配置项里，请同步删除该条目");
            Assert.True(row.Uses == 0, $"{name} 现在已有消费者 ⇒ 应升级为 Active 并从 Reserved 清单移除（原理由：{reason}）");
        }

        // ★ 防腐烂 ② ★ 不允许出现"没人认领"的项：要么 Active、要么在 Reserved 清单里写明理由。
        var unclassified = rows.Where(r => r.Status == "Unclassified").Select(r => r.Name).ToArray();
        Assert.True(unclassified.Length == 0,
            "以下配置项既没有消费者、也没在 Reserved 清单里说明理由（禁止'能配却没用上'）：" + string.Join(", ", unclassified));

        // ---- 写出审计文档 ----
        var sb = new StringBuilder();
        sb.AppendLine("# PCMig 诊断运行时 · 配置项接线审计（**自动生成，勿手工编辑**）");
        sb.AppendLine();
        sb.AppendLine("> 由 `tests/PCMig.Diagnostics.Tests/OptionsWiringAuditTests.cs` 每次运行重新生成：");
        sb.AppendLine("> 逐项统计运行时源码里的真实消费者。**Active 才代表该配置真的生效**。");
        sb.AppendLine("> 生成时间：不写入文档（保证仓库可复现）。");
        sb.AppendLine();
        sb.AppendLine($"| 配置项 | 状态 | 运行时读取点 |");
        sb.AppendLine($"|---|---|---|");
        foreach (var row in rows)
            sb.AppendLine($"| `{row.Name}` | {row.Status} | {row.Uses} |");
        sb.AppendLine();
        sb.AppendLine("## 口径");
        sb.AppendLine();
        sb.AppendLine("- Active：运行时真的读取它（例如 _options.X），配置生效；");
        sb.AppendLine("- Reserved：暂未生效，不得在界面/文档里声称生效（当前清单为空）；");
        sb.AppendLine("- Deprecated：已删除或明确标记；原 FailOpenToNoOp 已删除，");
        sb.AppendLine("  因为 fail-open 在本实现里是无条件的（装配失败即降级为内存分支），留开关会让人误以为可以关掉它。");
        sb.AppendLine();
        sb.AppendLine("## D6.1 §19 处理记录");
        sb.AppendLine();
        sb.AppendLine("| 配置项 | 处理 | 说明 |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine("| `WriterBatchMaxBytes` | 接入 | 未刷字节达阈值即做一次普通 flush（与时间闸门取先到者）|");
        sb.AppendLine("| `CheckpointIntervalMs` | 接入 | 检查点按配置间隔到期才写（旧实现每次 Tick 都写）|");
        sb.AppendLine("| `FailOpenToNoOp` | 删除 | 无消费者且语义为假（fail-open 无条件）|");
        sb.AppendLine();

        var docPath = Path.Combine(RepoRoot(), "docs", "诊断系统实施-配置项接线审计.md");
        File.WriteAllText(docPath, sb.ToString(), new UTF8Encoding(false));

        // 该文档必须存在且非空（防止"审计只在内存里"）。
        Assert.True(new FileInfo(docPath).Length > 0, "配置项审计文档未生成");
    }
}