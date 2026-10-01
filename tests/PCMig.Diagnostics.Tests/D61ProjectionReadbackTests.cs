using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PCMig.Diagnostics;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D6.1 §13（L2 真实控件 readback）收口测试。对应验收清单 §20.20：
///   修改真实控件状态必须能被 readback 检出 mismatch；
///   页面未挂载 ⇒ NotObserved/PageUnavailable（**不是** mismatch）。
/// </summary>
public sealed class D61ProjectionReadbackTests
{
    // ────────────────────────── 判定策略（纯函数：行为可验证） ──────────────────────────

    [Fact]
    public void FlippingTheRealControlStateIsDetectedAsMismatch()
    {
        // 期望"该可用"，实际控件被禁用 ⇒ 必须检出 mismatch（这正是旧实现永远做不到的）。
        var decision = ProjectionReadbackPolicy.Evaluate(
            pageMounted: true, actualEnabled: false, actualVisibility: "Visible",
            expectedEnabled: true, expectedVisibility: null);

        Assert.True(decision.PageMounted);
        Assert.True(decision.Observed);
        Assert.False(decision.Matches);
        Assert.Equal(ProjectionReadbackPolicy.ReasonEnabledMismatch, decision.ReasonCode);

        // 反向：期望"不该可用"，实际可用 ⇒ 同样必须检出。
        var reverse = ProjectionReadbackPolicy.Evaluate(
            pageMounted: true, actualEnabled: true, actualVisibility: "Visible",
            expectedEnabled: false, expectedVisibility: null);
        Assert.False(reverse.Matches);

        // 可见性不一致也必须检出。
        var visibility = ProjectionReadbackPolicy.Evaluate(
            pageMounted: true, actualEnabled: true, actualVisibility: "Visible",
            expectedEnabled: null, expectedVisibility: "Collapsed");
        Assert.False(visibility.Matches);
        Assert.Equal(ProjectionReadbackPolicy.ReasonVisibilityMismatch, visibility.ReasonCode);

        // 一致时才是 match。
        var ok = ProjectionReadbackPolicy.Evaluate(
            pageMounted: true, actualEnabled: true, actualVisibility: "Collapsed",
            expectedEnabled: true, expectedVisibility: "Collapsed");
        Assert.True(ok.Matches);
        Assert.Equal(ProjectionReadbackPolicy.ReasonMatch, ok.ReasonCode);
    }

    [Fact]
    public void UnmountedPageIsNotObservedRatherThanMismatch()
    {
        var decision = ProjectionReadbackPolicy.Evaluate(
            pageMounted: false, actualEnabled: false, actualVisibility: "Unavailable",
            expectedEnabled: true, expectedVisibility: "Visible");

        Assert.False(decision.PageMounted);
        Assert.False(decision.Observed);        // ★ 未观察到，不是"不一致" ★
        Assert.False(decision.Matches);         // 但绝不能被当成"匹配"
        Assert.Equal(ProjectionReadbackPolicy.ReasonPageUnavailable, decision.ReasonCode);
    }

    /// <summary>回读事件必须在**正常采集模式**下可见（Verbose 会让 L2 只有开 Deep 才被发现）。</summary>
    [Fact]
    public void ProjectionReadbackIsCollectedInNormalMode()
    {
        Assert.Equal(DeliveryClass.Operational, UiEvents.ProjectionReadback.Delivery);
    }

    /// <summary>载荷必须能如实表达"未观察到"与期望值（离线包要能重现判定过程）。</summary>
    [Fact]
    public void ReadbackPayloadCarriesExpectedActualAndReason()
    {
        var payload = new UiProjectionReadbackPayload(
            IsEnabled: false, Visibility: "Visible", ObservationVersion: 7, ProjectionVersion: 7,
            Generation: 7, MatchesSource: false)
        {
            PageMounted = true,
            Observed = true,
            ExpectedEnabled = true,
            ExpectedVisibility = null,
            ReasonCode = ProjectionReadbackPolicy.ReasonEnabledMismatch,
        };

        var json = WritePayloadJson(payload);
        Assert.Contains("\"matchesSource\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"expectedEnabled\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"reasonCode\":\"enabled-mismatch\"", json, StringComparison.Ordinal);
        Assert.Contains("\"pageMounted\":true", json, StringComparison.Ordinal);
    }

    // ────────────────────────── 接线契约（WinUI 不能在本项目加载） ──────────────────────────

    [Fact]
    public void PageReadsTheRealControlInsteadOfAssertingItsOwnCorrectness()
    {
        var page = ReadWinUi("Views", "Step1ConnectPage.xaml.cs");

        // 必须把**真实控件**交给观察器（而不是把 VM 的值当成"实际值"）。
        Assert.Contains("ProjectionObserver.ReadControl(", page, StringComparison.Ordinal);
        Assert.Contains("EmptySharesHint,", page, StringComparison.Ordinal);
        Assert.Contains("ConnectButton,", page, StringComparison.Ordinal);

        // 期望值来自投影承诺，代际来自自增计数器。
        Assert.Contains("sourceObservationVersion: ++_projectionGeneration", page, StringComparison.Ordinal);
        Assert.Contains("expectedVisibility:", page, StringComparison.Ordinal);
        Assert.Contains("expectedEnabled:", page, StringComparison.Ordinal);

        // ★ 旧缺陷形状必须彻底消失 ★ 不允许再出现"自己告诉自己匹配"的调用。
        Assert.DoesNotContain("matchesSource: true", page, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectionObserver.ReadEnabled(", page, StringComparison.Ordinal);

        // 回读必须发生在**真实推送之后**（同一方法内先写 Visibility 再读）。
        var pushIndex = page.IndexOf("EmptySharesHint.Visibility =", StringComparison.Ordinal);
        var readIndex = page.IndexOf("ProjectionObserver.ReadControl(", StringComparison.Ordinal);
        Assert.True(pushIndex > 0 && readIndex > pushIndex, "必须先真实推送、再回读");
    }

    private static string WritePayloadJson(IDiagnosticPayload payload)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            payload.WriteJson(writer);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string ReadWinUi(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln")))
            {
                var path = Path.Combine(new[] { dir.FullName, "src", "PCMig.WinUI" }.Concat(parts).ToArray());
                Assert.True(File.Exists(path), "缺少文件：" + path);
                return File.ReadAllText(path);
            }
        }
        throw new InvalidOperationException("找不到仓库根");
    }
}