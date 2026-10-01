using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// 三套可切换主题的资源键静态契约。
/// 仅比较每套主题声明的 x:Key 集合，不加载 WPF 运行时，也不改变视觉或业务行为。
/// </summary>
public class ThemeResourceKeyParityTests
{
    private static readonly string[] ThemeFiles =
    {
        @"src\PCMig.Gui\Theme\Glass.xaml",
        @"src\PCMig.Gui\Theme\Glass.Dark.xaml",
        @"src\PCMig.Gui\Theme\Classic.xaml",
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCMig.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到仓库根（未发现 PCMig.sln）。起始：" + AppContext.BaseDirectory);
    }

    private static HashSet<string> ReadKeys(string relativePath)
    {
        var fullPath = Path.Combine(FindRepoRoot(), relativePath);
        Assert.True(File.Exists(fullPath), "找不到主题文件：" + fullPath);
        var text = File.ReadAllText(fullPath);
        return Regex.Matches(text, @"x:Key\s*=\s*""([^""]+)""")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void Themes_DefineIdenticalResourceKeySets()
    {
        var keySets = ThemeFiles.ToDictionary(path => path, ReadKeys, StringComparer.Ordinal);
        var baselinePath = ThemeFiles[0];
        var baseline = keySets[baselinePath];
        var differences = new List<string>();

        foreach (var path in ThemeFiles.Skip(1))
        {
            var missing = baseline.Except(keySets[path], StringComparer.Ordinal).OrderBy(key => key).ToArray();
            var extra = keySets[path].Except(baseline, StringComparer.Ordinal).OrderBy(key => key).ToArray();
            if (missing.Length == 0 && extra.Length == 0) continue;

            var themeName = Path.GetFileName(path);
            if (missing.Length > 0) differences.Add(themeName + " 缺少：" + string.Join(", ", missing));
            if (extra.Length > 0) differences.Add(themeName + " 多出：" + string.Join(", ", extra));
        }

        Assert.True(differences.Count == 0,
            "三套主题 x:Key 集合不一致：\n  " + string.Join("\n  ", differences));
    }
}
