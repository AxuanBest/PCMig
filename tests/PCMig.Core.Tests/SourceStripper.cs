using System;
using Xunit;

namespace PCMig.Core.Tests;

/// <summary>
/// 源码剥注释的唯一实现 —— 全测试项目共用，**不要再各自复制一份**。
///
/// 【为什么必须用状态机，不能用正则】
/// 历史实现（分散在 10+ 个测试文件里，至少 4 种不同质量）都是两条正则：
/// `块注释正则 -> 行注释正则`。它有一个会把"检查"变成"什么都没检查"的缺陷：
/// C# 文档注释里出现字面量 `/*` 极其常见 —— 例如
/// 「禁止在 WinUI 里定义任何 *Orchestrator/*Runner 类型」这句注释里就有 `/*Runner`。
/// 正则一旦把它当成块注释开头，就会一路吃到下一个 `*/`，把中间的**全部可执行代码**
/// 静默删掉。实测事故：`Presentation\MigrationSessionViewModel.cs` 原文 240,758 B，
/// 被这种实现吞到只剩 90,929 B（丢 62.2%，被吞区间从第 143 行的 `/*Runner`
/// 一直吃到第 2544 行）。当时 `WinUiDpiContractTests.Shell_StaysFreeOfSecondEngine`
/// 之所以"通过"，正是因为被吞掉的那 107 KB 里含有它要找的禁词 —— 断言看着在守，其实全瞎。
///
/// 状态机天然免疫：`/*` 只有在**代码态**才是块注释开头；在行注释态里它只是普通字符。
///
/// 【第二道防线：块注释跨度上限】
/// "剥干净"和"剥过头"在结果上无法区分（剥完仍是一段合法 C#）。所以这里对单块注释的
/// 跨度设上限 <see cref="MaxBlockCommentChars"/>：给人看的注释不会横跨几万字符，
/// 一旦超过就说明匹配失控，直接失败而不是"通过"。
///
/// 识别范围：`//` 行注释、`/* */` 块注释、XAML `<!-- -->`、普通/逐字/原始字符串、字符字面量。
/// 字符串与字符字面量内部一律原样保留（其中的 `//`、`/*` 都不是注释）。
/// </summary>
internal static class SourceStripper
{
    /// <summary>单块注释允许的最大跨度（字符）；超过即视为匹配失控。</summary>
    internal const int MaxBlockCommentChars = 4000;

    /// <summary>
    /// 剥掉 C# / XAML 注释，只保留可执行内容。用于"不得出现某类型名"这类断言。
    /// 剥注释后**代码结构必须不变** —— 这是调用方可以依赖的契约。
    /// </summary>
    internal static string StripComments(string text)
    {
        Assert.False(string.IsNullOrEmpty(text), "StripComments 收到空文本 —— 调用方多半是读错了文件路径。");

        var sb = new System.Text.StringBuilder(text.Length);
        var n = text.Length;
        var i = 0;

        while (i < n)
        {
            var c = text[i];
            var next = i + 1 < n ? text[i + 1] : '\0';

            // ── 行注释：// 到行尾 ──
            if (c == '/' && next == '/')
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }

            // ── 块注释：/* 到 */ ──
            if (c == '/' && next == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                Assert.True(end >= 0,
                    "块注释没有闭合（起点偏移 " + i + "）—— 剥注释结果不可信，请先修 SourceStripper。");
                var span = end - i;
                Assert.True(span <= MaxBlockCommentChars,
                    "块注释跨度 " + span + " 字符（起点偏移 " + i + "）超过上限 " + MaxBlockCommentChars +
                    "。这几乎一定是把文档注释里的字面量 `/*` 当成了块注释开头，随后吞掉了整段代码 —— " +
                    "继续跑下去，后面所有「找不到 XXX」的断言都不可信。");
                i = end + 2;
                sb.Append(' ');
                continue;
            }

            // ── XML 注释：<!-- 到 -->（XAML）──
            if (c == '<' && next == '!' && i + 3 < n && text[i + 2] == '-' && text[i + 3] == '-')
            {
                var end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                Assert.True(end >= 0,
                    "XML 注释没有闭合（起点偏移 " + i + "）—— 剥注释结果不可信，请先修 SourceStripper。");
                i = end + 3;
                sb.Append(' ');
                continue;
            }

            // ── 原始字符串字面量：""" … """（引号数可变）──
            if (c == '"')
            {
                var quotes = 0;
                while (i + quotes < n && text[i + quotes] == '"') quotes++;
                if (quotes >= 3)
                {
                    var fence = new string('"', quotes);
                    var end = text.IndexOf(fence, i + quotes, StringComparison.Ordinal);
                    Assert.True(end >= 0,
                        "原始字符串没有闭合（起点偏移 " + i + "）—— 剥注释结果不可信，请先修 SourceStripper。");
                    i = end + quotes;
                    sb.Append(fence).Append(fence);
                    continue;
                }
            }

            // ── 普通 / 逐字字符串、字符字面量：整体原样保留 ──
            if (c == '"' || c == '\'')
            {
                var quote = c;
                var verbatim = i > 0 && text[i - 1] == '@';
                sb.Append(c);
                i++;
                while (i < n)
                {
                    var d = text[i];
                    sb.Append(d);
                    if (verbatim)
                    {
                        // 逐字字符串："" 表示一个字面引号，反斜杠无转义含义
                        if (d == quote)
                        {
                            if (i + 1 < n && text[i + 1] == quote)
                            {
                                sb.Append(text[i + 1]);
                                i += 2;
                                continue;
                            }
                            i++;
                            break;
                        }
                    }
                    else
                    {
                        if (d == '\\' && i + 1 < n)
                        {
                            sb.Append(text[i + 1]);
                            i += 2;
                            continue;
                        }
                        if (d == quote)
                        {
                            i++;
                            break;
                        }
                    }
                    i++;
                }
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}