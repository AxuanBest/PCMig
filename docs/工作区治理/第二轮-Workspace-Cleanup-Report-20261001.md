# 第二轮 — Workspace Cleanup & Governance 报告（2026-10-01）

> 执行依据：用户 2026-10-01 Phase 2 正式执行指令（§一 裁决 D-1…D-6；§二–§二十九 执行顺序与禁止事项）。
> 权威工作区：`E:\Project\deepseek work\PCMig`。本轮作用域**仅此目录**。
> 执行窗口：2026-10-01 13:35 → 13:50（+08:00）。

---

## 〇 验收对账（对用户 §二十四「最终我希望看到」）

| 验收目标 | 实测结果 |
|---|---|
| 当前源码和测试 Git 可恢复 | ✅ `2c0183b`（336 文件 / +70,992 行） |
| PMML Git 可恢复 | ✅ 四篇 PMML + 相关文档在 `2c0183b` |
| Diagnostics D6.1 Git 可恢复 | ✅ `src\PCMig.Diagnostics*` + `tests\PCMig.Diagnostics.Tests` + 全部文档在 `2c0183b` |
| docs 有唯一入口 | ✅ `docs\INDEX.md`（13,561 B，11 章，46 处路径引用 BAD_REFS=0） |
| 顶层不再被 39 篇历史交接淹没 | ✅ 顶层 `.md` **83 → 29** |
| 历史全部仍在 | ✅ `docs\handover\history\` 42 篇、`docs\qa\history\` 7 篇、`docs\archive\` 8 项 |
| Evidence 没有损失 | ✅ `archive\evidence\*` 一字未动 |
| Backup 没有损失 | ✅ `archive\backup-*` 一字未动，另新增治理前封存 |
| Solution 代表当前完整产品工程 | ✅ `PCMig.sln` 6 → **8 工程** |
| Build 0 error | ✅ `0 error / 4 warning`（既有 warning，无新增） |
| Tests 576+ / 0 | ✅ **Core 290 + Diagnostics 286 = 576 / 0** |
| Build garbage 清掉 | ✅ 清出 **1,174,088,631 B ≈ 1.09 GiB** |
| 历史安装包全部保留 | ✅ `dist\PCMigSetup-*.exe` **49 个全在**（4,692,358,997 B） |
| Harness / DSH 完全没碰 | ✅ 见 §十二 |
| 当前交接唯一、准确 | ✅ `docs\工作交接-20261001-Workspace治理完成与D6.2起点.md` |

---

## 一 Before / After 目录地图

**治理前**（第一轮盘点口径，`c9aef30`）：

```
PCMig\
  AGENTS.md  README.md  .gitignore  PCMig.sln
  src\        2981 files  555.5 MB
  tests\       416 files   19.4 MB
  docs\         92 files    7.5 MB   ← 顶层 83 篇 .md，其中 39 篇是历史交接
  tools\        25 files    0.2 MB
  lab\          24 files    2.9 MB
  installer\     1 file
  matrix\        1 file
  archive\    1931 files  342.7 MB
  dist\         76 files 4791.1 MB   ← 5 个 publish 目录 + 49 个历史安装包
  .vs\          19 files    2.2 MB
```

**治理后**（2026-10-01 13:50 实测）：

```
PCMig\
  AGENTS.md  README.md  .gitignore  PCMig.sln(5517 B)
  src\         213 files    4.0 MB    ← bin/obj 已清
  tests\        61 files    0.7 MB    ← bin/obj 已清
  docs\         95 files    7.6 MB    ← 顶层 29 篇 .md + 3 个 .txt
  tools\        25 files    0.2 MB    ← 未动
  lab\          24 files    2.9 MB    ← 未动
  installer\     1 file
  matrix\        1 file
  archive\    2242 files  347.1 MB    ← +治理前封存(341)；−探针 bin/obj(30)
  dist\         50 files 4475.0 MB    ← 仅 49 个安装包 + gui-extracted.manifest
  .vs\        （已删除）
  .git\         44 files    7.6 MB
  ──────── 合计（不含 .git） 2716 files / 4837.5 MB
```

> 说明：`bin`/`obj` 在 G0 基线构建（`--no-incremental`，全 8 工程含 WinUI 自包含）与 S1 构建中被重新生成并显著膨胀，故"回收字节"以删除前逐目录实测为准（§五），与第一轮盘点的旧快照不可直接相减。

---

## 二 Git Before / After

| 项目 | Before | After |
|---|---|---|
| HEAD | `c9aef30`（v0.5.0 WinUI technical baseline） | `c5e668c`（chore: govern workspace and documentation layout） |
| 分支 | `feature/winui-v0.5.0` | 同左 |
| `git status --porcelain` | **190** 条 | **1** 条（`?? archive/`，有意不纳管） |
| 提交 | 未提交 | 3 个新 commit（§九） |

---

## 三 Tracked / Untracked Before / After

| 项目 | Before | After |
|---|---|---|
| tracked 文件数 | **100** | **391**（+ 本轮新增交接与报告 → 393） |
| porcelain 未跟踪条目 | 144 | 1（`archive/`） |
| 未跟踪文件总数（`--untracked-files=all`） | ~1900（折叠显示为 144 条目录条目） | **2238**，**全部位于 `archive\` 下**，非 archive 未跟踪 = **0** |
| ignored | 43 | 43（`**/bin/`、`**/obj/`、`dist/`、`.vs/`、`lab/*.txt|png|iso` 等，未改） |

tracked 100 → 390 由 checkpoint `2c0183b` 完成（`M 46 / A 290`，explicit allowlist，**UNKNOWN = ∅**，无 `git add -A`）；governance `c5e668c` 增加 1 个新文件（`docs\INDEX.md`）。

---

## 四 移动的文件（全部 rename，历史正文一字未改）

| # | 目标 | 数量 | 内容 |
|---|---|---|---|
| 1 | `docs\handover\history\` | 41 | 38 篇 `工作交接-*.md` + 3 篇 `工作交接说明_v0.4.{1,5,6}.md`（41 条 rename 实测 **全部 R100**） |
| 2 | `docs\qa\history\` | 7 | `Coverage-Matrix-V046.md`、`Test-Execution-Report-V046.md`、`Test-Execution-Report-V046-AddendumA.md`、`Private-Test-Lab-Blueprint.md`、`Private-Test-Lab-Final-Report.md`、`Corporate-Simulation-Blueprint.md`、`Step1视觉验收结论-V12-20260926.md` |
| 3 | `docs\archive\` | 7 | `公司域环境验证清单.md`、`FinalPolish-{完成度审计,标注项独立复核,用户指令原文,本轮交付报告,Responsive扩展说明}`、`阶段A-交付说明-20260929.md`、`现场验证清单-v0.3.8.txt` |
| 4 | `docs\handover\history\`（最终交接上线后） | 1 | `工作交接-20261001-D6.1诊断收口与全量验收.md`（旧 Current Handover → 历史） |

**链接修正**共 15 处 / 9 文件（M2 批次）+ 12 处相对引用（M3 批次）+ 1 处（`docs\archive\FinalPolish-完成度审计-20260928.md:4`）+ INDEX 6 处登记。全部仅改路径与引用，**未改任何历史正文**；逐文件 BOM 状态保持不变。

**允许保留的历史字面值**：历史交接正文中的"当时路径记录"、以及治理前快照 `docs\工作区治理\第一轮-Workspace-Inventory-20261001.md` 内的旧路径，按用户 §七 明示保留。

---

## 五 删除的可重建产物（C-1 / C-2 / C-3）

合计回收 **1,174,088,631 B ≈ 1.09 GiB**。

| 类别 | 路径 | 字节 |
|---|---|---|
| C-1 | `dist\app` | 107,857,785 |
| C-1 | `dist\cli` | 35,642,769 |
| C-1 | `dist\gui` | 72,285,802 |
| C-1 | `dist\publish-cli` | 35,451,259 |
| C-1 | `dist\publish-gui` | 80,216,816 |
| C-2 | `src\PCMig.WinUI\bin` | 528,528,851 |
| C-2 | `src\PCMig.Gui\bin` | 171,009,489 |
| C-2 | `src\PCMig.Cli\bin` | 76,808,500 |
| C-2 | `src\PCMig.Gui\obj` | 14,361,931 |
| C-2 | `src\PCMig.WinUI\obj` | 12,129,089 |
| C-2 | `tests\PCMig.Core.Tests\bin` | 11,606,067 |
| C-2 | `src\PCMig.Cli\obj` | 11,019,857 |
| C-2 | `tests\PCMig.Diagnostics.Tests\bin` | 6,504,175 |
| C-2 | `src\PCMig.Diagnostics\bin` | 1,388,704 |
| C-2 | `src\PCMig.Core\obj` | 1,186,821 |
| C-2 | `src\PCMig.Core\bin` | 1,145,854 |
| C-2 | `src\PCMig.Diagnostics\obj` | 1,114,936 |
| C-2 | `tests\PCMig.Core.Tests\obj` | 834,073 |
| C-2 | `tests\PCMig.Diagnostics.Tests\obj` | 663,849 |
| C-2 | `src\PCMig.Diagnostics.Abstractions\obj` | 544,698 |
| C-2 | `src\PCMig.Diagnostics.Abstractions\bin` | 332,998 |
| C-2 | `.vs\` | 2,306,773 |
| C-3 | `archive\scripts\diagnostics-audit-20261001\bin` | 900,146 |
| C-3 | `archive\scripts\diagnostics-audit-20261001\obj` | 247,389 |

删除脚本带三重守卫：① 绝对路径必须位于仓库内；② 非 C-3 项不得位于 `archive\`；③ 目录名必须命中白名单。逐项删除后复验"不存在"，`GUARDS_OK=True`。C-2 同时清除了其中明确点名的构建/调试日志（`a5-model-dump.log`、`poc-backdrop.log` 等）。

**清理由 `release.ps1` 可完整重建**：`release.ps1:132-148` 会 `Remove-Item dist\cli, dist\gui, dist\app -Recurse -Force` 后重新 publish；`bin`/`obj` 为 MSBuild 常规产物。按用户 §十七，清理后**未再 Build**（最终 Build/Test 证据已在清理前成立）。

---

## 六 受保护文件（本轮未动且不得删除）

- `archive\backup-*`（`backup-PCMig验收-20260930-122742`、`backup-新建文件夹4-20260930-123255`、`-A5final`、`-v2`）、`archive\evidence\*`、`archive\screenshots\*`、`archive\pmm-l-audit*`、`archive\baseline-*`、`archive\packages\*`、`archive\a5-rollback\`、`archive\scripts\`（除 C-3 的探针 bin/obj）。
- `archive\evidence\diagnostics-*\…` 下约 170 个**空目录** —— 证据结构，**不是垃圾**，保持。
- `dist\PCMigSetup-*.exe` **49 个历史安装包全部保留**（4,692,358,997 B，v0.1.0→v0.4.9；缺 0.4.2/0.4.3/0.4.4 属历史事实）；`dist\gui-extracted.manifest`（847 B）保留。
- `docs\工作区治理\第一轮-Workspace-Inventory-20261001.md` 及其快照口径。
- `docs\archive\FinalPolish-用户指令原文-20260927.md`（保留只读属性）、`FinalPolish-标注项独立复核-20260927.md`（与交付报告结论相反，不可删）。
- 机器生成文档：`docs\诊断系统实施-事件覆盖矩阵.md`、`docs\诊断系统实施-配置项接线审计.md`（**禁手改**）。
- **本轮新增的治理封存**：`archive\backup-pre-governance-20261001-133659\` —— 341 文件 / 5,814,770 B，含 `manifest.csv`、`SHA256.txt`、`git-status-before.txt`、`git-diff-before.patch`、`untracked-project-files-before.txt`；**逐文件 SHA256 336/336 MATCH（0 MISMATCH / 0 MISSING）**。

---

## 七 docs 顶层 Before / After

| 项目 | Before | After |
|---|---|---|
| 顶层 `.md` | **83** | **29** |
| 顶层 `.txt` | 4（`使用说明.txt`、`更新日志.txt`、`现场验证清单-v0.3.8.txt`、`诊断系统实施-基线指纹-D1.txt`） | 3（`现场验证清单-v0.3.8.txt` 已归档） |
| 子目录 | `screenshots\`、`ui\` | `handover\`（42）、`qa\`（7）、`archive\`（8）、`screenshots\`（1）、`ui\`（4）、`工作区治理\`（2） |

顶层保留的 29 篇全部属于：启动三件套 + 发版/发布/稳定性 + 更新日志/使用说明/测试报告 + 首日与公司两份清单 + PMML 四篇 + 现行架构方案（诊断中心、业务接线、A5 节流）+ 技术发现/备忘 + 诊断实施证据与进度 + 自检报告 + `INDEX.md` + 当前交接。**凡被 `AGENTS.md`/`README.md`/Contract Test/`release.ps1`/csproj/installer/源码注释裸路径引用的文档一律原位未动**（用户裁决 D-3）。

---

## 八 Solution Before / After

| 项目 | Before | After |
|---|---|---|
| 工程数 | **6**（Core / Cli / Gui / Diagnostics.Abstractions / Core.Tests / Diagnostics.Tests） | **8**（+ `src\PCMig.Diagnostics`、`src\PCMig.WinUI`） |
| `dotnet build PCMig.sln` 是否等于构建全产品 | ❌ | ✅ |
| csproj 业务属性改动 | — | **无**（用户 S1 明令禁止） |
| WinUI 配置映射 | — | `Debug|x64` / `Release|x64`（因 `<Platforms>x64</Platforms>`） |
| sln 形态 | 78 行 / 5,517 B，UTF-8 **无 BOM**、CRLF、**以空行开头** | 保持不变 |

---

## 九 Commit Hashes

| Hash | 时间（+08:00） | Message | 规模 |
|---|---|---|---|
| `2c0183beebcd6b06d3c1c2e903204a739f1bc104` | 2026-10-01 13:39:12 | `checkpoint: preserve v0.5.0 PMML frozen and Diagnostics D6.1` | 336 files, +70,992 / −1,327 |
| `7ba2bc19bbd42229be798b3ce43087cde459bc31` | 2026-10-01 13:45:11 | `chore: complete PCMig solution membership` | `PCMig.sln` +15 / −1 |
| `c5e668cda016801427b329fd3e45ec21205fd952` | 2026-10-01 13:45:12 | `chore: govern workspace and documentation layout` | 69 条：A 1 / M 12 / R 56，+299 / −41 |

`git diff --cached` 复核：主要呈现 **rename 56 / 索引登记 / 文档 metadata / solution membership**，无大规模业务代码变化（最大单文件改动是 `docs\INDEX.md` 新增 220 行）。**密钥扫描：staged diff 886 行宽匹配 14 处，全部是"关于口令的说明文字"，无任何真实凭据**；无私钥块、无 IP 明文。

---

## 十 Build / Tests / Warnings

| 命令 | 结果 |
|---|---|
| `dotnet build PCMig.sln -c Release --no-incremental`（治理前基线） | 0 error / **1 warning**（xUnit2031） |
| `dotnet build src\PCMig.WinUI\PCMig.WinUI.csproj -c Release`（治理前基线） | 0 error / **3 warning**（WMC1506 ×3） |
| `dotnet build PCMig.sln -c Release --no-incremental`（S1 后 · 最终） | 退出 0；**0 error / 4 warning** = xUnit2031 ×1（`tests\PCMig.Core.Tests\A5PresentationRegressionTests.cs:1634`）+ WMC1506 ×3（`src\PCMig.WinUI\Views\PCMigSurface.xaml:53,54,56`）——**全部既有，无新增** |
| `dotnet test PCMig.sln -c Release`（S1 后 · 最终） | 退出 0；`PCMig.Core.Tests` **290 通过 / 0 失败**；`PCMig.Diagnostics.Tests` **286 通过 / 0 失败**；合计 **576 / 0** |
| PMML Contract Tests | PASS（含在 Core.Tests 内） |
| Diagnostics Contract Tests | PASS（含在 Diagnostics.Tests 内） |

日志：`archive\pcmig-governance-20261001\g0-baseline.log`、`s1-build.log`、`s1-test.log`。

---

## 十一 遗留债（本轮登记，未修）

1. **P-G7 / Release Governance**：`docs\首日实测检查表.md` 仍是旧口径（v0.2 期内容）。按用户 §十一"本轮只登记、不改"。
2. **P-G8 / Release Governance**：`tools\release.ps1` 交付复制清单问题。**本轮未改 `release.ps1` 一个字节**。
3. **`AGENTS.md` 未更新（决策保留）**：`:10` 仍写 v0.4.8（发布线已 0.4.9），且未引用 `docs\INDEX.md`。属最高规则文件，未经明示授权不改。
4. **Harness / DSH 清理未进行**（见 §十二）。
5. **Diagnostics 事件口径遗留**（承接 D6.1）：23 个事件仍 `Reserved`；`DIA.SerializationFailed` 只有计数器没有事件；`FS.FileReadFailure` 口径缺失；Preflight 用中文检查名派生 code；1 条偶发未复现测试。
6. **未做的真实环境验证**：Deep Trace 端到端 / 高 DPI / 面板动效逐帧、性能实测（现数字全是候选值）、Stage B、三 VM、210 万文件级迁移。
7. **`archive\` 有意不纳管**：2238 个未跟踪文件全部位于 `archive\`，属用户 §四明示决定。

---

## 十二 Harness / DSH 排除范围（用户裁决 D-5，本轮完全未碰）

`E:\Project\deepseek work\` 下的：

- 174 个散落文件 / 22,792,055 B（`.ps1` 93、`.py` 31、`.txt` 21、`.js` 14 等；其中 171/174 与 `archive\` 下同哈希）；
- 12 个与归档目录逐字节相同的重复目录（`TreeTest`、`BalanceTest`、`RepairTest`、`ErrorReasonTest`、`repair-lab`、`robo-lab`、`dlss5-payload`、`_tools`、`_prev`、`UserData`(817 文件)、`thin-search-src`、`pinkllo-src`）；
- `UserData`、`.dsh*`、`.openviking`、`dsh-session-recovery`、`.preset-square` 等疑似 DSH / Harness 数据；
- 以及 `E:\Project\PCMig`（交付区）、`D:\PCMig`（工作副本）、`I:\K\deepseek work`（旧镜像）。

即使 SHA256 完全相同也**未删除任何一项**。理由：刚从第三方 DSH 切换到官方 DeepSeek Harness，Mnemon / MCP / Harness 数据是否仍活跃尚未完成识别。后续单开 **Harness / DSH Workspace Cleanup** 处理。

---

## 十三 证据索引

| 内容 | 路径 |
|---|---|
| 治理前增量封存（341 文件，SHA256 336/336 MATCH） | `PCMig\archive\backup-pre-governance-20261001-133659\` |
| 治理脚本与日志 | `E:\Project\deepseek work\archive\pcmig-governance-20261001\`（`g0-backup.ps1`、`g0-baseline.ps1/.log`、`git-checkpoint-allowlist.txt`、`stage-paths.txt`、`modified-tracked.txt`、`status-before.txt`、`untracked-all.txt`、`s1-build.log`、`s1-test.log`、`g2-staged-secret-scan.txt`） |
| 治理前 git 快照 | 同上封存内 `git-status-before.txt`、`git-diff-before.patch`、`untracked-project-files-before.txt` |
| 第一轮盘点证据 | `E:\Project\deepseek work\archive\pcmig-inventory-20261001\` |
| 第一轮盘点报告 | `docs\工作区治理\第一轮-Workspace-Inventory-20261001.md` |
| 当前交接 | `docs\工作交接-20261001-Workspace治理完成与D6.2起点.md` |

---

*本报告由 Workspace Cleanup & Governance 第二轮生成（2026-10-01）。所有数字均为本机实测；未发版、未改发版脚本、未改 frozen 范围代码、未执行任何破坏性 git 命令。*
