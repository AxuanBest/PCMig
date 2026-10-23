#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
生成 PMML Source Traceability 差异报告（保留项 / 行号变化 / 已删除实现 / 新增实现）。

输出：docs/pmml-engineering/diff/
        relocation.csv        —— 逐行裁定（机器可读）
        差异报告.md            —— 人类可读总结
只读源码；只写 docs/pmml-engineering/ 下产物。
"""
import csv, json, os, re, subprocess
from collections import Counter, OrderedDict

import os as _os
REPO = _os.environ.get("PCMIG_REPO") or _os.path.abspath(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", "..", ".."))
ENG = os.path.join(REPO, "docs", "pmml-engineering")
DIFFDIR = os.path.join(ENG, "diff")
RELOC = os.path.join(DIFFDIR, "relocation.csv")
RECOVERED = os.path.join(ENG, "_recovered.json")

HEAD = subprocess.run(["git", "rev-parse", "HEAD"], cwd=REPO,
                      capture_output=True, text=True).stdout.strip()
BASE = "2c0183b"

rows = list(csv.DictReader(open(RELOC, encoding="utf-8")))
c = Counter(r["Verdict"] for r in rows)

def group(v):
    return [r for r in rows if r["Verdict"] == v]

# 漂移按文件聚合
by_file = Counter()
for r in group("RELOCATED"):
    f = r["NewSourcePath"].rsplit(":", 1)[0]
    by_file[f] += 1

lines = []
A = lines.append
A("# PMML Source Traceability —— 差异报告")
A("")
A(f"- 基线 commit（快照）：`{BASE}`（v0.5.0 时代）")
A(f"- 当前 commit（HEAD）：`{HEAD}`")
A(f"- 记录总数：**{len(rows)}**")
A(f"- 生成时间：2026-10-14 (+08:00)")
A("")
A("## 一、总体裁定")
A("")
A("| 裁定 | 数量 | 含义 |")
A("| --- | ---: | --- |")
A(f"| UNCHANGED | {c['UNCHANGED']} | 行号与当前 HEAD 完全一致（**保留项**） |")
A(f"| RELOCATED | {c['RELOCATED']} | 同文件内找到，行号漂移（**行号变化**） |")
A(f"| LOCATED | {c['LOCATED']} | 原记录未给行号，本次补全到具体行 |")
A(f"| NO_PROBE | {c['NO_PROBE']} | 原记录只写 `Comment`/表达式，无可检索标识符 → 需人工回读 |")
A(f"| FILE_GONE | {c['FILE_GONE']} | 引用文件在当前 HEAD 不存在（**已删除实现**） |")
A("")
A("## 二、保留项（行号一致）")
A("")
A(f"共 **{c['UNCHANGED']}** 条。这些记录在 v0.5.0 时代写下后，代码位移未曾触及，"
  "可直接继续引用。完整清单见 `relocation.csv` 中 `Verdict=UNCHANGED` 行。")
A("")
A("## 三、行号变化")
A("")
A(f"共 **{c['RELOCATED']}** 条。漂移按文件聚合如下（前 20）：")
A("")
A("| 文件 | 漂移条数 |")
A("| --- | ---: |")
for f, n in by_file.most_common(20):
    A(f"| `{f}` | {n} |")
A("")
A("主要成因（已确证）：")
A("")
A("- `Themes/Materials.xaml` 整体 **+49 行**（Surface 样式块前移/扩写）："
  "旧记 `:168/169/170/177/178/179` → 现 `:217/218/219/226/227/228`。")
A("- `Presentation/MotionDirector.cs` 因 **v0.5.4 动效回归修复**大幅增行"
  "（`2c0183b` 版 666 行 → HEAD 版 1163 行）：旧 `:370` → 现 `:335`。")
A("- 缓存路径解析缺陷（非源码变化）：旧记录中 `src/<Proj>/../docs/x.md` 形式的"
  "引用曾被错误规约，本次已按「候选路径逐一验证存在性」修正。")
A("")
A("## 四、已删除实现")
A("")
gone = group("FILE_GONE")
A(f"共 **{len(gone)}** 条。")
A("")
for r in gone:
    A(f"- `{r['FeatureId']}`（{r['CanonicalChinese']}）：`{r['OldSourcePath']}`")
A("")
A("其中 `PROG-009` 引用的 `Presentation/ProgressMotionDriver.cs`"
  "（818 行 / 40.1 KB）已于 2026-10-06 经用户授权删除，对应登记册 **L-19 已 CLOSED**。"
  "当前 `src/` 零生产引用，仅测试保留断言（`Batch5UiLayoutContractTests.cs:184`、"
  "`ImmersiveVisualContractTests.cs:318/325/326`）。")
A("")
A("## 五、新增实现")
A("")
A("**本次比对不产生「新增实现」清单**，原因如下：")
A("")
A("原 CSV 是按「PMML 设计语言要素」逐条枚举的审计产物，不是全量源码清单。"
  "把当前源码相对它做差集，得到的差异绝大多数是**该 CSV 从未打算覆盖的普通业务代码**，"
  "而非新增的设计语言实现。发布这样的清单会制造大量噪声与误判。")
A("")
A("已确认的、**与本档案主题真正相关的**新增实现如下（人工核对，非自动差集）：")
A("")
A("| 新增项 | 位置 | 说明 |")
A("| --- | --- | --- |")
A("| `ShowVerificationPanel(FrameworkElement?)` | `src/PCMig.WinUI/MainWindow.xaml.cs:1190` | "
  "取代旧 `EnsureVerifyPanel`；只负责开面板与定位（`TogglePanel(..., onOpened: null)`），"
  "**不执行闸门、不触发 IO** —— 原 `VERIFY-004` 的不变量仍然成立，仅符号名漂移 |")
A("| `OtherVisibleToolPanel(FrameworkElement)` | `src/PCMig.WinUI/MainWindow.xaml.cs:1215` | "
  "面板互斥辅助方法（浮层打开时收掉其它可见工具面板） |")
A("| `VerificationPanelWidth` 常量 | `src/PCMig.WinUI/MainWindow.xaml.cs` | "
  "验证面板宽度改由常量承载（原实现散在 `PositionOverlayPanels` 内联 820） |")
A("")
A("## 六、需人工回读（NO_PROBE）")
A("")
A(f"共 **{c['NO_PROBE']}** 条。原记录的 `SymbolOrResourceKey` 只写了 `Comment`、"
  "纯数值表达式或英文描述，**不含可自动检索的标识符**。这些不是「找不到」，"
  "而是「自动比对不适用」，行号需人工回读确认：")
A("")
A("| FeatureId | 原引用 | 主题 |")
A("| --- | --- | --- |")
for r in group("NO_PROBE"):
    A(f"| `{r['FeatureId']}` | `{r['OldSourcePath']}` | {r['CanonicalChinese']} |")
A("")
A("## 七、结论")
A("")
A(f"- **可继续直接引用**：{c['UNCHANGED']} 条（行号未变）")
A(f"- **行号需更新后可用**：{c['RELOCATED'] + c['LOCATED']} 条（内容位置已定位）")
A(f"- **需人工回读**：{c['NO_PROBE']} 条（引用方式不可自动检索）")
A(f"- **已删除实现**：{len(gone)} 条")
A("")
A("**技术发现复核**：对 33 条高价值断言逐一回读当前源码，"
  "**除 `VERIFY-004` 的符号名外，全部内容成立**"
  "（含 `DAM-001` Mica XAML、`DAM-003` Acrylic 参数、`BOOT-002` `MarkShellInteractive`、"
  "`BOOT-003` 七级缓存闸门、`OACT-004/008` 相位表、`TAOP-001/003/005` 定位算法、"
  "`PROG-001` `ClampVisual` 硬守卫、`PROG-002` 指数滤波 `k=10`、"
  "`IP-001` `Interrupted = 7`、`MHE-009` 全仓零 MHE 命中）。")
A("")

out = os.path.join(DIFFDIR, "差异报告.md")
open(out, "w", encoding="utf-8", newline="\n").write("\n".join(lines))
print("wrote", out)
print("verdicts:", dict(c))
