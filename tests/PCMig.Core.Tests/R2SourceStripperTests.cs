using System;
using System.IO;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// <see cref="SourceStripper"/> 自身的回归 —— 它现在被多个契约测试当作"看代码的眼睛"，
/// 它一旦剥错，那些测试的"通过"就全是假的。所以必须直接测它，而不是只靠间接效果。
///
/// 【这个文件存在的理由】
/// 历史上剥注释是每个测试文件各写一份正则，质量参差且无一处被直接验证。
/// `WinUiDpiContractTests.Shell_StaysFreeOfSecondEngine` 因此长期处在"靠吞掉 107 KB 源码而通过"
/// 的状态：它要找的 `RobocopyRunner` / `PCMig.Gui` / 引擎类定义全在被吞区间里，
/// 断言每天绿着，却什么都没看。本文件把那类事故固化成会失败的用例。
/// </summary>
public class R2SourceStripperTests
{
    [Fact]
    public void RS01_DocCommentContainingBlockCommentLiteral_DoesNotSwallowFollowingCode()
    {
        // 这就是真实事故的形态：文档注释里出现字面量 /*Runner，其后是必须被看见的代码。
        const string source = """
// 禁止在 WinUI 里定义任何 *Orchestrator/*Runner 类型
internal sealed class ForbiddenRunner
{
    public int Value => 1;
}
""";

        var stripped = SourceStripper.StripComments(source);

        // 关键：注释被去掉，但后面的**代码必须还在**。
        Assert.Contains("ForbiddenRunner", stripped, StringComparison.Ordinal);
        Assert.Contains("public int Value => 1;", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("Orchestrator", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void RS02_LineCommentContainingBlockCommentLiteral_DoesNotOpenBlockComment()
    {
        // 行注释态里出现的 /* 只是普通字符，不得开启块注释。
        const string source = """
var a = 1; // 这里提到 /* 但它在行注释里
var b = 2;
var c = 3;
""";

        var stripped = SourceStripper.StripComments(source);

        Assert.Contains("var a = 1;", stripped, StringComparison.Ordinal);
        Assert.Contains("var b = 2;", stripped, StringComparison.Ordinal);
        Assert.Contains("var c = 3;", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void RS03_StringLiteralContainingCommentMarkers_IsPreservedIntact()
    {
        // 字符串里的 // 与 /* */ 都不是注释，必须原样保留 ——
        // 否则"某禁词是否出现在字符串里"这类判断会被扭曲。
        const string source = """
var url = "http://example.test/a/*b*/c";
var path = @"C:\x\/*y*\z";
var ch = '"';
""";

        var stripped = SourceStripper.StripComments(source);

        Assert.Contains(@"""http://example.test/a/*b*/c""", stripped, StringComparison.Ordinal);
        Assert.Contains(@"C:\x\/*y*\z", stripped, StringComparison.Ordinal);
        Assert.Contains("var ch = '", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void RS04_NormalCommentsAreRemoved()
    {
        const string source = """
// 整行注释
var keep = 1; // 尾随注释
/* 块注释
   跨多行 */
var alsoKeep = 2;
""";

        var stripped = SourceStripper.StripComments(source);

        Assert.Contains("var keep = 1;", stripped, StringComparison.Ordinal);
        Assert.Contains("var alsoKeep = 2;", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("整行注释", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("尾随注释", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("跨多行", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void RS05_XamlCommentsAreRemoved_ButAttributesSurvive()
    {
        const string source = """
<!-- 这里是 XAML 注释，里面提到 /* 与 // -->
<Grid x:Name="Keep" Width="12" />
""";

        var stripped = SourceStripper.StripComments(source);

        Assert.Contains(@"x:Name=""Keep""", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("XAML 注释", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void RS06_RunawayBlockComment_IsRejectedInsteadOfSilentlySwallowingCode()
    {
        // 失控的块注释（跨度远超上限）必须**报错**，而不是安静地把整段代码吃掉 ——
        // 因为"安静吃掉"正是历史上这个缺陷能以"通过"的形式存在的原因。
        var big = new string('x', SourceStripper.MaxBlockCommentChars + 1000);
        var source = "var before = 1;\n/* " + big + " */\nvar after = 2;";

        var ex = Assert.ThrowsAny<Exception>(() => SourceStripper.StripComments(source));
        Assert.Contains("块注释跨度", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RS07_RealWinUiSources_AllStripWithoutRunaway()
    {
        // 用**真实源码**跑一遍：任何一个 WinUI 文件都不允许触发失控判定。
        // 这条是"全仓普查"，防止将来有人在注释里写出新的失控形态。
        var winUi = Path.Combine(FindRepoRoot(), "src", "PCMig.WinUI");
        var files = Directory.GetFiles(winUi, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(winUi, "*.xaml", SearchOption.AllDirectories))
            .Where(f => !f.Contains(@"\bin\", StringComparison.Ordinal)
                        && !f.Contains(@"\obj\", StringComparison.Ordinal))
            .ToArray();

        Assert.True(files.Length >= 20, "WinUI 源码文件数异常偏少：" + files.Length + "（扫描可能已失效）");

        foreach (var f in files)
        {
            var raw = File.ReadAllText(f);
            // 不抛异常即通过；失败信息里带上文件名，便于定位。
            try
            {
                var stripped = SourceStripper.StripComments(raw);
                Assert.False(string.IsNullOrWhiteSpace(stripped),
                    Path.GetFileName(f) + " 剥注释后为空 —— 说明整份文件都被吃掉了。");
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException(
                    Path.GetFileName(f) + " 剥注释失败：" + ex.Message);
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("找不到仓库根（PCMig.sln）。BaseDirectory=" + AppContext.BaseDirectory);
    }
}