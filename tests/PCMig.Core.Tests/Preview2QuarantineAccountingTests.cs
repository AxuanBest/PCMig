using System;
using System.IO;
using PCMig.Core.Transfer;
using PCMig.Core.Util;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// ★ v0.5.4 Preview.2 真机修复回归（2026-10-16，用户任务令 §七 Quarantine 组）★
///
/// 真机现场（JOB-20261016-150319-e738 / object-000010，RootFiles 通道，目标 = D:\ 根）：
///   第 1 趟：targetFiles 9 / targetBytes 1,990,998,416，robocopy 退出码 9（DumpStack.log 拒读）
///   「强制覆盖」把这 9 个已复制文件隔离成 &lt;原名&gt;.pcmig-old-20261016073229（同目录改名，未删除）
///   第 2 趟：targetFiles **18** / targetBytes **3,981,996,832** —— 恰好 ×2
///   robocopy 日志逐条列出 9 个「*额外文件 ... .pcmig-old-20261016073229」
///
/// ⇒ 隔离旧副本被当成"本轮目标文件"又数了一遍。本组把这个双倍指纹钉死。
/// 纪律：**不删除 quarantine 文件**、不改 quarantine/恢复账本语义，
///       只在"统计与验证口径"上把 PCMig 自己产生的隔离物排除干净。
/// </summary>
public class Preview2QuarantineAccountingTests : IDisposable
{
    private readonly string _root;

    public Preview2QuarantineAccountingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pcmig-q2-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteFile(string relPath, int bytes)
    {
        var full = Path.Combine(_root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[bytes]);
        return full;
    }

    /// <summary>真机 9 个隔离旧副本的命名形态（真实时间戳后缀）。</summary>
    private static string OldCopy(string originalName) => originalName + RepairQuarantine.Suffix + "20261016073229";

    // ─────────────────────────────────────────────────────────────────────
    // §七-Quarantine-①② 隔离旧副本不进入 TargetFiles / TargetBytes
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 真机双倍指纹的最小复现：目标目录里 2 个真实文件 + 它们的 2 个 .pcmig-old-* 旧副本。
    /// 正确口径必须只统计 2 个真实文件（100 B），而不是 4 个（200 B）。
    /// </summary>
    [Fact]
    public void PT2_Q01_DirStat_ExcludesQuarantineOldCopies()
    {
        WriteFile("真实-甲.dat", 60);
        WriteFile("真实-乙.dat", 40);
        WriteFile(OldCopy("真实-甲.dat"), 60);
        WriteFile(OldCopy("真实-乙.dat"), 40);

        var withSkip = DirStat.Measure(_root, long.MaxValue, skipQuarantineOldCopies: true);
        var withoutSkip = DirStat.Measure(_root, long.MaxValue, skipQuarantineOldCopies: false);

        Assert.Equal(2, withSkip.Files);
        Assert.Equal(100, withSkip.Bytes);
        // 对照：不排除时确实会数成 4 个 —— 这正是真机 9→18 的机制，证明排除参数是有效的。
        Assert.Equal(4, withoutSkip.Files);
        Assert.Equal(200, withoutSkip.Bytes);
    }

    /// <summary>
    /// 真机 object-000010 的 RootFiles 形态逐字复现：目标 = 根目录，9 个业务文件 + 9 个隔离旧副本。
    /// 断言"本轮目标文件数 = 9"而不是 18。
    /// </summary>
    [Fact]
    public void PT2_Q02_RootFilesStyleTarget_NotDoubledByQuarantine()
    {
        string[] business =
        {
            "092623-7281-01.dmp", "KAC800 TT服务扫描报告-11-4 .pptx", "office2021.exe",
            "PDS系统.lnk", "UMOUTLOOK.reg", "UmOutlookAddin1.dll",
            "Windows (C) - 快捷方式.lnk", "临时公用桌面快捷方式.lnk", "项目管理体系.lnk",
        };
        foreach (var n in business) WriteFile(n, 10);
        // 「强制覆盖」把第 1 趟的产物同目录改名隔离（文件仍在，未删除）。
        foreach (var n in business) WriteFile(OldCopy(n), 10);

        // 与 TransferOrchestrator.MeasureSettledTarget 的 RootFiles 分支同一判据。
        long files = 0, bytes = 0;
        foreach (var f in Directory.EnumerateFiles(_root))
        {
            if (RepairQuarantine.IsQuarantinedOldCopy(Path.GetFileName(f))) continue;
            files++; bytes += new FileInfo(f).Length;
        }

        Assert.Equal(9, files);
        Assert.Equal(90, bytes);
        // 物理上确实还是 18 个文件 —— 我们只改统计口径，**没有删除任何隔离旧副本**。
        Assert.Equal(18, Directory.GetFiles(_root).Length);
    }

    // ─────────────────────────────────────────────────────────────────────
    // §七-Quarantine-③ 第二趟不会双倍统计
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 跨趟稳定性：第 1 趟结束后目标有 9 个真实文件；第 2 趟前「强制覆盖」又隔离出 9 个旧副本、
    /// robocopy 重新复制 9 个。最终口径必须**仍然是 9 文件 / 相同字节**，绝不 ×2。
    /// </summary>
    [Fact]
    public void PT2_Q03_SecondPass_StaysStableAfterReCopy()
    {
        const int size = 1_000;
        for (int i = 1; i <= 9; i++) WriteFile($"数据-{i:00}.bin", size);

        var afterPass1 = DirStat.Measure(_root, long.MaxValue, skipQuarantineOldCopies: true);
        Assert.Equal(9, afterPass1.Files);
        Assert.Equal(9 * size, afterPass1.Bytes);

        // 第 2 趟：旧副本产生 + 真实文件被重新复制（内容长度不变）。
        for (int i = 1; i <= 9; i++)
        {
            WriteFile(OldCopy($"数据-{i:00}.bin"), size);
            WriteFile($"数据-{i:00}.bin", size);
        }

        var afterPass2 = DirStat.Measure(_root, long.MaxValue, skipQuarantineOldCopies: true);

        Assert.Equal(9, afterPass2.Files);
        Assert.Equal(9 * size, afterPass2.Bytes);
        Assert.NotEqual(afterPass1.Files * 2, afterPass2.Files);
    }

    /// <summary>嵌套子目录里的隔离旧副本也必须被排除（DirStat 是全递归的）。</summary>
    [Fact]
    public void PT2_Q04_NestedQuarantineCopies_Excluded()
    {
        WriteFile(Path.Combine("甲", "数据.dat"), 50);
        WriteFile(Path.Combine("甲", "乙", "数据2.dat"), 30);
        WriteFile(Path.Combine("甲", OldCopy("数据.dat")), 50);
        WriteFile(Path.Combine("甲", "乙", OldCopy("数据2.dat")), 30);

        var stat = DirStat.Measure(_root, long.MaxValue, skipQuarantineOldCopies: true);

        Assert.Equal(2, stat.Files);
        Assert.Equal(80, stat.Bytes);
    }

    // ─────────────────────────────────────────────────────────────────────
    // §七-Quarantine-④ Verify 不把 quarantine 当用户多余文件
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 结构判定本身：只有 PCMig 自己那套"业务名 + .pcmig-old- + 14 位时间戳"才算隔离旧副本。
    /// 用户的真实文件绝不能因为名字里恰好含相似片段被误排除（否则会漏报缺文件）。
    /// </summary>
    [Theory]
    [InlineData("报告.docx.pcmig-old-20261016073229", true)]
    [InlineData("a.pcmig-old-20261016073229-1", true)]     // 冲突时的序号尾
    [InlineData("a.pcmig-old-20261016073229", true)]
    [InlineData(".pcmig-old-20261016073229", false)]      // 后缀前没有业务名 ⇒ 不是我们的产物
    [InlineData("a.pcmig-old-2026101607322", false)]      // 时间戳不足 14 位
    [InlineData("a.pcmig-old-20261016073X29", false)]     // 非数字
    [InlineData("a.pcmig-old-20261016073229-", false)]    // 尾部空段
    [InlineData("普通文件.docx", false)]
    [InlineData("报告 pcmig-old 备份.docx", false)]        // 没有点号前缀，不构成后缀
    public void PT2_Q05_QuarantineStructure_RecognizedExactly(string name, bool expected)
    {
        Assert.Equal(expected, RepairQuarantine.IsQuarantinedOldCopy(name));
    }

    /// <summary>
    /// 真机 verify-report.json 的 extraOnTarget 曾出现 5 项以 .pcmig-old-20261014070527 结尾的条目
    /// （更早一轮 b5dc Job）。隔离旧副本是 PCMig 自己的中间产物，**不是用户目标侧多余数据**，
    /// 验证口径不得把它报成"目标多余"。
    /// </summary>
    [Fact]
    public void PT2_Q06_QuarantineCopies_AreNotUserExtraFiles()
    {
        string[] extras =
        {
            "旧报告.docx.pcmig-old-20261014070527",
            "旧表格.xlsx.pcmig-old-20261014070527",
            "旧照片.jpg.pcmig-old-20261014070527",
        };
        foreach (var e in extras) WriteFile(e, 10);
        WriteFile("用户自己多放的文件.txt", 10);

        var userExtras = 0;
        foreach (var f in Directory.EnumerateFiles(_root))
            if (!RepairQuarantine.IsQuarantinedOldCopy(Path.GetFileName(f))) userExtras++;

        Assert.Equal(1, userExtras);   // 只有那 1 个真正的用户多余文件
    }

    // ─────────────────────────────────────────────────────────────────────
    // 反例：口径必须真的生效（防"排除了但没人用"）
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 反例验证：若把排除关掉（skipQuarantineOldCopies: false），统计立刻翻倍 ⇒ 证明本组断言
    /// 依赖的正是那个开关，而不是"恰好没有旧副本"。这是"反例必须失败"的自我检查。
    /// </summary>
    [Fact]
    public void PT2_Q07_Control_DisablingSkipReproducesTheRealDeviceBug()
    {
        for (int i = 1; i <= 9; i++) { WriteFile($"f{i}.bin", 10); WriteFile(OldCopy($"f{i}.bin"), 10); }

        var buggy = DirStat.Measure(_root, long.MaxValue, skipQuarantineOldCopies: false);
        var fixedStat = DirStat.Measure(_root, long.MaxValue, skipQuarantineOldCopies: true);

        Assert.Equal(18, buggy.Files);      // 真机的 18
        Assert.Equal(9, fixedStat.Files);   // 应有的 9
    }
}
