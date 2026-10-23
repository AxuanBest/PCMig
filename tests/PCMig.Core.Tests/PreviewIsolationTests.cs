using System;
using System.IO;
using System.Text;
using PCMig.Core.Util;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ 2026-10-15（v0.5.4 Preview.2 安装器数据隔离整改）★
/// <see cref="PreviewIsolation"/> 的**正负双向**合同测试。
///
/// 【它要钉住的核心事实】
/// 隔离的判据是**产物身份**（程序集 <c>AssemblyInformationalVersion</c> 是否含 <c>-preview</c>），
/// 而不是"目录里有没有这个文件"。因此：
///   · 正式产物：**永远**不读配置 ⇒ 生产路径逐位不变（哪怕目录里被人误放了配置文件）；
///   · 预览产物：配置合法 ⇒ 用配置的数据根；缺失/损坏/指向正式根/不可写 ⇒ **Fail Closed**，
///     绝不允许静默回退到 <c>%ProgramData%\PCMig</c>（那正是本整改要根除的污染）。
///
/// 【为什么这些用例值钱】
/// 此前的做法（安装器写 <c>HKCU\Environment</c>）无法用测试拦住 —— 它污染的是账户，
/// 测试进程自己看不见。改成"随产物分发的配置 + 身份判定"后，每一档行为都可在进程内断言。
/// </summary>
public class PreviewIsolationTests
{
    private const bool PreviewIdentity = true;
    private const bool StableIdentity = false;

    private static string ValidJson(string root, int schema = 1)
        => $"{{\"schemaVersion\":{schema},\"dataRoot\":\"{root.Replace("\\", "\\\\")}\"}}";

    private static string TempRoot()
        => Path.Combine(Path.GetTempPath(), "pcmig-prevtest-" + Guid.NewGuid().ToString("N"));

    // ── ① 正式身份：绝不介入 ────────────────────────────────────────────────────

    [Fact]
    public void PVI01_StableIdentity_IgnoresConfigEntirely()
    {
        // 正式产物即使目录里真有配置，也不得生效（否则误放一个文件就能改掉正式版数据根）。
        var state = PreviewIsolation.EvaluateForTest(ValidJson(TempRoot()), StableIdentity);

        Assert.False(state.IsActive);
        Assert.Null(state.DataRoot);
        Assert.Null(state.Failure);   // 正式版不是"失败"，只是不参与
    }

    [Fact]
    public void PVI02_StableIdentity_NullConfig_IsNotFailure()
    {
        var state = PreviewIsolation.EvaluateForTest(null, StableIdentity);

        Assert.False(state.IsActive);
        Assert.Null(state.Failure);   // 正式版没有配置文件是完全正常的
    }

    [Fact]
    public void PVI03_StableIdentity_MalformedConfig_DoesNotBreakStable()
    {
        // 正式版下即使配置是垃圾也不得 Fail —— 否则误放一个坏文件就让正式版启动不了。
        var state = PreviewIsolation.EvaluateForTest("{ 这不是 JSON", StableIdentity);

        Assert.False(state.IsActive);
        Assert.Null(state.Failure);
    }

    // ── ② 预览身份 + 合法配置 ⇒ 激活且数据根独立 ────────────────────────────────

    [Fact]
    public void PVI04_PreviewIdentity_ValidConfig_ActivatesWithGivenRoot()
    {
        var root = TempRoot();
        try
        {
            var state = PreviewIsolation.EvaluateForTest(ValidJson(root), PreviewIdentity, root);

            Assert.True(state.IsActive);
            Assert.Null(state.Failure);
            Assert.Equal(Path.GetFullPath(root), state.DataRoot);
        }
        finally { TryCleanup(root); }
    }

    [Fact]
    public void PVI05_ActiveRoot_MustNotBeUnderStableRoots()
    {
        var root = TempRoot();
        try
        {
            var state = PreviewIsolation.EvaluateForTest(ValidJson(root), PreviewIdentity, root);
            Assert.True(state.IsActive);

            var stableCommon = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCMig");
            var stableLocal = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMig");

            Assert.False(IsSameOrUnder(state.DataRoot!, stableCommon), "预览数据根不得落在 %ProgramData%\\PCMig");
            Assert.False(IsSameOrUnder(state.DataRoot!, stableLocal), "预览数据根不得落在 %LocalAppData%\\PCMig");
        }
        finally { TryCleanup(root); }
    }

    [Fact]
    public void PVI06_SubDirectory_ResolvesUnderDataRoot()
    {
        var root = TempRoot();
        try
        {
            var state = PreviewIsolation.EvaluateForTest(ValidJson(root), PreviewIdentity, root);
            Assert.True(state.IsActive);

            foreach (var name in new[] { "Jobs", "Logs", "Diagnostics", "Settings", "Exports" })
            {
                var expected = Path.Combine(Path.GetFullPath(root), name);
                Assert.Equal(expected, Path.Combine(state.DataRoot!, name));
            }
        }
        finally { TryCleanup(root); }
    }

    // ── ③ 预览身份 + 配置不可用 ⇒ Fail Closed（本整改的核心）────────────────────

    [Fact]
    public void PVI07_PreviewIdentity_MissingConfig_FailsClosed()
    {
        var state = PreviewIsolation.EvaluateForTest(null, PreviewIdentity);

        Assert.False(state.IsActive);
        Assert.NotNull(state.Failure);
        Assert.Contains(PreviewIsolation.FileName, state.Failure!);
        // 必须明确"不会回退"，而不是含糊报错
        Assert.Contains("回退", state.Failure!);
    }

    [Fact]
    public void PVI08_PreviewIdentity_EmptyConfig_FailsClosed()
    {
        var state = PreviewIsolation.EvaluateForTest("   ", PreviewIdentity);
        Assert.False(state.IsActive);
        Assert.NotNull(state.Failure);
    }

    [Fact]
    public void PVI09_PreviewIdentity_MalformedJson_FailsClosed()
    {
        var state = PreviewIsolation.EvaluateForTest("{ 不是合法 JSON ", PreviewIdentity);

        Assert.False(state.IsActive);
        Assert.NotNull(state.Failure);
        Assert.Contains("格式非法", state.Failure!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void PVI10_PreviewIdentity_WrongSchemaVersion_FailsClosed(int schema)
    {
        var root = TempRoot();
        try
        {
            var state = PreviewIsolation.EvaluateForTest(ValidJson(root, schema), PreviewIdentity, root);

            Assert.False(state.IsActive);
            Assert.NotNull(state.Failure);
            Assert.Contains("schemaVersion", state.Failure!);
        }
        finally { TryCleanup(root); }
    }

    [Fact]
    public void PVI11_PreviewIdentity_MissingDataRoot_FailsClosed()
    {
        var state = PreviewIsolation.EvaluateForTest("{\"schemaVersion\":1}", PreviewIdentity);

        Assert.False(state.IsActive);
        Assert.NotNull(state.Failure);
        Assert.Contains("dataRoot", state.Failure!);
    }

    [Theory]
    [InlineData("relative\\path\\here")]
    [InlineData("..\\..\\escape")]
    public void PVI12_PreviewIdentity_RelativeDataRoot_FailsClosed(string badRoot)
    {
        var state = PreviewIsolation.EvaluateForTest(ValidJson(badRoot), PreviewIdentity, null);

        Assert.False(state.IsActive);
        Assert.NotNull(state.Failure);
        Assert.Contains("绝对路径", state.Failure!);
    }

    // ── ④ 指向正式数据根 ⇒ 必须拒绝（这是本整改存在的全部理由）─────────────────

    [Fact]
    public void PVI13_PreviewIdentity_RootPointingAtProgramDataPCMig_FailsClosed()
    {
        var stable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCMig");
        var state = PreviewIsolation.EvaluateForTest(ValidJson(stable), PreviewIdentity, null);

        Assert.False(state.IsActive);
        Assert.NotNull(state.Failure);
        Assert.Contains("正式数据根", state.Failure!);
    }

    [Fact]
    public void PVI14_PreviewIdentity_RootUnderProgramDataPCMig_FailsClosed()
    {
        // 不只是"等于"，**子目录**也算 —— 否则 config 写 %ProgramData%\PCMig\Preview 就绕过去了
        var stableSub = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCMig", "Preview2Data");
        var state = PreviewIsolation.EvaluateForTest(ValidJson(stableSub), PreviewIdentity, null);

        Assert.False(state.IsActive);
        Assert.NotNull(state.Failure);
    }

    [Fact]
    public void PVI15_PreviewIdentity_RootPointingAtLocalAppDataPCMig_FailsClosed()
    {
        var stable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMig");
        var state = PreviewIsolation.EvaluateForTest(ValidJson(stable), PreviewIdentity, null);

        Assert.False(state.IsActive);
        Assert.NotNull(state.Failure);
        Assert.Contains("正式数据根", state.Failure!);
    }

    [Fact]
    public void PVI16_PreviewIdentity_RootInsideInstallDirectory_FailsClosed()
    {
        // 数据根不得落在程序目录下：升级/卸载会连带删除用户迁移成果。
        var insideApp = Path.Combine(AppContext.BaseDirectory, "Data");
        var state = PreviewIsolation.EvaluateForTest(ValidJson(insideApp), PreviewIdentity, null);

        Assert.False(state.IsActive);
        Assert.NotNull(state.Failure);
        Assert.Contains("程序目录", state.Failure!);
    }

    // ── ⑤ 数据根不可用 ⇒ Fail Closed（而非静默回落）─────────────────────────────

    [Fact]
    public void PVI17_PreviewIdentity_UnusableRoot_FailsClosed()
    {
        // 用"已存在同名文件"制造必然的创建失败（跨权限模型稳定，不依赖 ACL）。
        var filePath = Path.Combine(Path.GetTempPath(), "pcmig-prevtest-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(filePath, "not a directory", new UTF8Encoding(false));
        try
        {
            var state = PreviewIsolation.EvaluateForTest(ValidJson(filePath), PreviewIdentity, null);

            Assert.False(state.IsActive);
            Assert.NotNull(state.Failure);
            Assert.Contains("不可用", state.Failure!);
        }
        finally { try { File.Delete(filePath); } catch { } }
    }

    [Fact]
    public void PVI18_PreviewIdentity_ValidRootIsActuallyWritable()
    {
        // 正向确认：合法根会被真正建成目录（不能只"看起来合法"）
        var root = TempRoot();
        try
        {
            var state = PreviewIsolation.EvaluateForTest(ValidJson(root), PreviewIdentity, root);

            Assert.True(state.IsActive);
            Assert.True(Directory.Exists(root), "激活预览隔离时应已确认数据根可写");
        }
        finally { TryCleanup(root); }
    }

    // ── ⑥ 与期望根不一致 ⇒ 激活但给出提示（不静默）─────────────────────────────

    [Fact]
    public void PVI19_PreviewIdentity_RootDiffersFromExpected_ActivatesWithNote()
    {
        var root = TempRoot();
        var other = TempRoot();
        try
        {
            var state = PreviewIsolation.EvaluateForTest(ValidJson(root), PreviewIdentity, other);

            Assert.True(state.IsActive);              // 仍然激活（用户可能有意换盘）
            Assert.NotNull(state.Note);               // 但必须留痕，不得静默
            Assert.Contains("预期根", state.Note!);
        }
        finally { TryCleanup(root); TryCleanup(other); }
    }

    [Fact]
    public void PVI20_PreviewIdentity_RootMatchesExpected_NoNote()
    {
        var root = TempRoot();
        try
        {
            var state = PreviewIsolation.EvaluateForTest(ValidJson(root), PreviewIdentity, root);

            Assert.True(state.IsActive);
            Assert.Null(state.Note);
        }
        finally { TryCleanup(root); }
    }

    // ── ⑦ 环境变量不得反超预览隔离 ──────────────────────────────────────────────
    //   否则账户级变量又能改写预览行为 ⇒ 等于没隔离。这里断言的是**求值结果**里的
    //   数据根与进程环境变量无关（Evaluation 根本不读环境变量）。

    [Fact]
    public void PVI21_EvaluationIgnoresPcmigEnvironmentVariables()
    {
        var root = TempRoot();
        var decoy = TempRoot();
        var savedJobs = Environment.GetEnvironmentVariable("PCMIG_JOBS");
        var savedLogs = Environment.GetEnvironmentVariable("PCMIG_LOG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("PCMIG_JOBS", decoy);
            Environment.SetEnvironmentVariable("PCMIG_LOG_DIR", decoy);

            var state = PreviewIsolation.EvaluateForTest(ValidJson(root), PreviewIdentity, root);

            Assert.True(state.IsActive);
            Assert.Equal(Path.GetFullPath(root), state.DataRoot);
            Assert.NotEqual(Path.GetFullPath(decoy), state.DataRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PCMIG_JOBS", savedJobs);
            Environment.SetEnvironmentVariable("PCMIG_LOG_DIR", savedLogs);
            TryCleanup(root);
            TryCleanup(decoy);
        }
    }

    [Fact]
    public void PVI22_ExpectedPreviewRoot_IsUnderLocalAppDataAndDistinctFromStable()
    {
        var expected = PreviewIsolation.ExpectedPreviewRoot();

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(local, expected, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("PCMig-Preview2", expected, StringComparison.OrdinalIgnoreCase);
        // 必须与正式版的 %LocalAppData%\PCMig 不同
        Assert.NotEqual(
            Path.Combine(local, "PCMig").TrimEnd('\\'),
            expected.TrimEnd('\\'),
            StringComparer.OrdinalIgnoreCase);
    }

    // ── 工具 ────────────────────────────────────────────────────────────────────

    private static bool IsSameOrUnder(string candidate, string ancestor)
    {
        var c = Path.GetFullPath(candidate).TrimEnd('\\') + '\\';
        var a = Path.GetFullPath(ancestor).TrimEnd('\\') + '\\';
        return c.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryCleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 清理失败不影响结论 */ }
    }
}
