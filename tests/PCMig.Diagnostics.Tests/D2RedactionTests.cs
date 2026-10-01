using System;
using System.Text;
using PCMig.Diagnostics.Abstractions;
using Xunit;

namespace PCMig.Diagnostics.Tests;

/// <summary>
/// D2 契约：隐私从**入口**开始（不是导出时再洗）。
/// 这些用例使用**人工合成 canary**（不读取任何真实凭据/历史日志）。
/// </summary>
public sealed class D2RedactionTests
{
    private const string CanarySecret = "Sup3rSecret!P@ssw0rd";
    private const string CanaryPath = @"\\fileserver01\机密共享\财务\2026\工资表.xlsx";
    private const string CanaryDrivePath = @"C:\Users\zhangsan\Documents\private-notes.txt";

    [Fact]
    public void TokenIsStableWithinPolicyAndKeyedPerSession()
    {
        var policy = RedactionPolicy.CreateForSession("k1");
        var other = RedactionPolicy.CreateForSession("k2");

        var a = policy.Token(CanaryPath);
        var b = policy.Token(CanaryPath);
        Assert.Equal(a, b);                       // 同会话同输入 ⇒ 同令牌（可关联）
        Assert.NotEqual(a, other.Token(CanaryPath)); // 换密钥 ⇒ 不可跨会话关联
        Assert.DoesNotContain("机密", a, StringComparison.Ordinal);
        Assert.DoesNotContain("fileserver01", a, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(22, a.Length);
    }

    [Fact]
    public void PathRefNeverCarriesPlaintext()
    {
        var policy = RedactionPolicy.CreateForSession();
        var pathRef = policy.CreatePathRef(CanaryDrivePath, PathRole.Source, "src");

        Assert.Equal("Drive", pathRef.RootKind);
        Assert.Equal("src", pathRef.RootAlias);
        Assert.Equal("document", pathRef.ExtensionClass);
        Assert.Equal("session", pathRef.Scope);
        Assert.Equal(policy.KeyId, pathRef.KeyId);
        Assert.NotEqual(CanaryDrivePath, pathRef.PathToken);

        // 全字段扫描：任何明文片段都不允许出现。
        var dump = string.Join("|", pathRef.Role, pathRef.RootKind, pathRef.RootAlias, pathRef.PathToken,
            pathRef.KeyId, pathRef.Scope, pathRef.ExtensionClass, pathRef.DepthBucket);
        Assert.DoesNotContain("zhangsan", dump, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-notes", dump, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void UncPathsAreClassifiedButNotExposed()
    {
        var policy = RedactionPolicy.CreateForSession();
        var pathRef = policy.CreatePathRef(CanaryPath, PathRole.Target, "dst");

        Assert.Equal("Unc", pathRef.RootKind);
        Assert.Equal("document", pathRef.ExtensionClass);
        Assert.DoesNotContain("fileserver01", pathRef.PathToken, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("a.txt", "document")]
    [InlineData("a.PDF", "document")]
    [InlineData("a.zip", "archive")]
    [InlineData("a.mp4", "video")]
    [InlineData("a.dll", "data")]
    [InlineData("a.unknownext", "other")]
    [InlineData("noext", "none")]
    public void ExtensionClassBucketsAreCoarse(string? path, string expected)
        => Assert.Equal(expected, RedactionPolicy.ExtensionClassOf(path));

    [Theory]
    [InlineData(@"C:\a", 2)]
    [InlineData(@"C:\a\b\c", 4)]
    [InlineData(@"C:\a\b\c\d\e", 6)]
    [InlineData(@"C:\a\b\c\d\e\f\g\h", 8)]
    public void DepthIsBucketed(string path, int expected)
        => Assert.Equal(expected, RedactionPolicy.DepthBucketOf(path));

    [Theory]
    [InlineData(@"\\srv\share", "Unc")]
    [InlineData(@"C:\x", "Drive")]
    [InlineData(@"\\?\C:\x", "DriveExtended")]
    [InlineData(@"\\?\UNC\srv\share", "UncExtended")]
    [InlineData("relative\\path", "Unknown")]
    public void RootKindClassification(string path, string expected)
        => Assert.Equal(expected, RedactionPolicy.RootKindOf(path));

    [Fact]
    public void SanitizeTextMasksPathsAndSecretAssignments()
    {
        var policy = RedactionPolicy.CreateForSession();

        var masked = policy.SanitizeText($"打开 {CanaryDrivePath} 失败");
        Assert.NotNull(masked);
        Assert.DoesNotContain("zhangsan", masked!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[path]", masked!, StringComparison.Ordinal);

        var uncMasked = policy.SanitizeText($"无法访问 {CanaryPath}");
        Assert.DoesNotContain("机密", uncMasked!, StringComparison.Ordinal);

        var secretMasked = policy.SanitizeText($"连接失败 password={CanarySecret} user=zhangsan");
        Assert.DoesNotContain(CanarySecret, secretMasked!, StringComparison.Ordinal);
        Assert.Contains("[redacted]", secretMasked!, StringComparison.Ordinal);

        var colonForm = policy.SanitizeText($"token: {CanarySecret};");
        Assert.DoesNotContain(CanarySecret, colonForm!, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeTextTruncatesAndIsIdempotentEnough()
    {
        var policy = RedactionPolicy.CreateForSession();
        var longText = new string('x', 5_000);

        var sanitized = policy.SanitizeText(longText, maxLength: 256);
        Assert.NotNull(sanitized);
        Assert.Equal(256, sanitized!.Length);

        // 时间戳里的 "12:30" 不能被误判成盘符路径。
        var time = policy.SanitizeText("在 12:30 开始");
        Assert.Equal("在 12:30 开始", time);
    }

    [Fact]
    public void CanaryNeverAppearsInCanonicalJsonLine()
    {
        var policy = RedactionPolicy.CreateForSession();
        var pathRef = policy.CreatePathRef(CanaryPath, PathRole.Source, "src");

        var evt = TestEvents.Minimal(Abstractions.Events.UiEvents.ActionFaulted) with
        {
            Message = policy.SanitizeText($"访问 {CanaryPath} 失败：password={CanarySecret}"),
            Path = pathRef,
        };

        var line = Abstractions.Serialization.DiagnosticEventJson.ToJsonLine(evt);
        Assert.DoesNotContain(CanarySecret, line, StringComparison.Ordinal);
        Assert.DoesNotContain("机密共享", line, StringComparison.Ordinal);
        Assert.DoesNotContain("工资表", line, StringComparison.Ordinal);
        Assert.DoesNotContain("fileserver01", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[path]", line, StringComparison.Ordinal);
    }

    [Fact]
    public void PathRefUnavailableVariantsAreExplicitNotSilent()
    {
        var policy = RedactionPolicy.CreateForSession();
        Assert.Equal("[unavailable]", policy.CreatePathRef(null, PathRole.Source).PathToken);
        Assert.Equal("[unavailable]", policy.CreatePathRef("   ", PathRole.Target).PathToken);
    }

    [Fact]
    public void TokenNeverFallsBackToPlaintextOnFailure()
    {
        // 极端输入（含控制字符/超长）也必须只给出令牌或占位符，绝不回退原文。
        var policy = RedactionPolicy.CreateForSession();
        var weird = "a\0b\u0001c" + new string('z', 10_000);

        var token = policy.Token(weird);
        Assert.DoesNotContain("zzz", token, StringComparison.Ordinal);
        Assert.True(token.Length <= 32);
    }
}