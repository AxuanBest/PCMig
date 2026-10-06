# PCMig 工作区治理 · 第一轮 Workspace Inventory

> 执行日期：**2026-10-01** ｜ 任务来源：用户下达的 **PCMig Workspace Cleanup & Governance**
> 本轮性质：**纯只读盘点**。全文所有数字均为实测；**本轮零删除、零移动、零重命名、零改名**。
> 证据与中间产物目录：`<工作区根>\archive\pcmig-inventory-20261001\`
> 基线：HEAD `c9aef30`（分支 `feature/winui-v0.5.0`）
> 产品代码、`tools\*` 脚本、`AGENTS.md`、任何 `.ps1`、任何历史交接文档 **本轮一字未动**。

---

## 0. 本轮做了什么 / 没做什么

**做了**：基线快照 → 全仓目录树与体积 → docs 引用图（83 篇逐篇硬/软引用）→ 空目录与临时文件扫描 → 2280 文件哈希去重 → 工程与 solution 引用关系核对 → 文档分类（46 篇由 3 个子调查员并行分类，39 篇交接文档机检归类）→ Contract Test / `release.ps1` / `csproj` / `src` 注释级硬路径抓取 → 工作区根越界观察。

**没做（本轮禁止）**：没有删过任何文件、没有移动过任何文件、没有改过任何文件名、没有跑过构建或测试、没有执行任何 git 写命令（全程只用 `git status` / `ls-files` / `show` / `rev-parse` 只读命令）。

**证据文件**（`archive\pcmig-inventory-20261001\`）：

| 文件 | 内容 |
|---|---|
| `00-head.txt` | HEAD 与分支 |
| `01-status-porcelain.txt` / `02-status-with-ignored.txt` | 工作树状态（含 ignored） |
| `03-tracked-files.txt` / `12-tracked-readable.txt` | 100 个 tracked 文件全清单 |
| `10-status-readable.txt` / `11-status-readable-ignored.txt` | 去引号转义后的可读状态 |
| `20-docs-metadata.csv` | 83 篇 docs md 的大小/日期/tracked/首次提交 |
| `21-docs-refgraph.csv` / `22-refgraph-condensed.csv` | 83 篇逐篇「被谁引用」全表（本报告引用列的数据源） |
| `30-docs-digest.txt` | 每篇前 7 行非空内容摘要 |
| `40-hashes.csv` | 2280 文件的 SHA256（重复组分析数据源） |
| `build-inventory.ps1` / `build-digest.ps1` | 两个一次性只读脚本（不写仓库） |

---

## 1. Current Workspace Map

### 1.1 权威坐标（不得混淆）

| 用途 | 路径 |
|---|---|
| 权威工作区（唯一事实来源） | `<仓库根>` |
| 交付区（对外交付物只落这里） | `<交付区>` |
| 源码镜像备份 | `<镜像备份根>\PCMig` |
| 工作副本（发版脚本自动建） | `<发版工作副本>` |
| 发版脚本 | `PCMig\tools\release.ps1` |
| 旧移动硬盘镜像 | `<旧镜像根>` —— **只作覆盖目标，永不作事实依据/代码来源** |

### 1.2 工作区根 `<工作区根>`（项目外，越界观察）

`<工作区根>\AGENTS.md`（2791 B，2026-09-21）与 `INDEX.md`（4963 B）已经立过规矩：

> 根目录只允许 `PCMig\`、`AGENTS.md`、`INDEX.md` 与 `labs\` / `archive\` / `projects\` / `dsh-data` 四个分类目录；一次性脚本一律写进 `archive\scripts\`。

`INDEX.md` 记录了 2026-09-18 的一次完整归位（173 个散落文件 + 12 个目录 → `archive\scripts\{ps1,py,js}`、`archive\conversations`、`archive\docs-snapshots`、`archive\images`、`archive\misc`、`labs\`、`projects\`、`dsh-data\`）。

**实测现状：根目录又脏了。**

- 根下**散落文件 174 个 / 22,792,055 B**：`.ps1` 93、`.py` 31、`.txt` 21、`.js` 14、`.png` 5、`.md` 3、`.jsonl` 2、`.obj` 2、`.gz` 1、`.mjs` 1、`.zstd` 1。
- 根下还有 25 个目录，其中 **12 个与归档目录逐字节重复**（见下表）。
- 逐文件 SHA256 比对结果：**174 个散落文件里 171 个在 `archive\` 下已有同哈希副本**；唯一 3 个是 `AGENTS.md`、`INDEX.md`、`thin-search.tar.gz`。

| 根条目 | 归档对应 | 比对结果 |
|---|---|---|
| `TreeTest`(38) | `labs\TreeTest` | 同内容 38 / 0 差异 |
| `BalanceTest`(38) | `labs\BalanceTest` | 同内容 38 / 0 差异 |
| `RepairTest`(38) | `labs\RepairTest` | 同内容 38 / 0 差异 |
| `ErrorReasonTest`(38) | `labs\ErrorReasonTest` | 同内容 38 / 0 差异 |
| `repair-lab`(14) | `labs\repair-lab` | 同内容 14 / 0 差异 |
| `robo-lab`(3) | `labs\robo-lab` | 同内容 3 / 0 差异 |
| `thin-search-src`(8) | `projects\thin-search-src`(9) | 同内容 8 / 归档多 1 |
| `dlss5-payload`(35) | `projects\dlss5-payload` | 同内容 35 / 0 差异 |
| `_tools`(2) | `dsh-data\_tools` | 同内容 2 / 0 差异 |
| `_prev`(8) | `dsh-data\_prev` | 同内容 8 / 0 差异 |
| `UserData`(817) | `dsh-data\UserData` | 同内容 817 / 0 差异 |
| `pinkllo-src`(0) | `projects\pinkllo-src` | 空 ↔ 空 |

⇒ 推断：2026-09-18 的归位**是「复制」而不是「移动」**（原地副本仍在），或原地被恢复过。**本轮不作任何处理**；这是**项目外**区域，且 `AGENTS.md` 第 39 行明文「`dlss5-payload`、`pinkllo-src`、`thin-search-src` 等为历史/附带的独立工程，不受本文件约束」。

`UserData`(817 文件) 很可能是 **DSH 活数据**（`.dsh` / `.openviking`），**绝对不能按「重复副本」处理**——列为 H 待确认。

### 1.3 PCMig 顶层结构（体积实测，排除 `.git`）

| 顶层 | 文件 | 体积 | 目录 | 用途判定 |
|---|---|---|---|---|
| `src\` | 2981 | 555.5 MB | 251 | 源码（6 工程，见 1.4） |
| `tests\` | 416 | 19.4 MB | 63 | 测试（2 工程） |
| `docs\` | 92 | 7.5 MB | 3 | 文档（本轮主目标） |
| `archive\` | 1931 | 342.7 MB | 562 | 历史归档 + 证据（**保护区内**） |
| `dist\` | 76 | **4791.1 MB** | 9 | 构建产物 + 48 个历史安装包 |
| `lab\` | 24 | 2.9 MB | 0 | 虚拟机/实验脚本 |
| `tools\` | 25 | 0.2 MB | 0 | 工具链 |
| `.vs\` | 19 | 2.2 MB | — | VS 缓存（F） |
| `installer\` | 1 | 2143 B | 0 | `pcmig.iss` |
| `matrix\` | 1 | 1898 B | 0 | `migration-matrix.yaml` |
| 根文件 | 4 | — | 0 | `PCMig.sln` 4369 B、`AGENTS.md` 7295 B、`.gitignore` 421 B、`README.md` 4463 B |

**根目录结论：干净**——根下不存在临时 `.ps1`/`.log`/`.txt`/`.zip`/`.bak`/`.tmp` 残留（实测 0 命中）。

**`dist\` 关键判据（不要误删 4.8 GB）**：
- `dist\{cli,gui,app}`：`tools\release.ps1:132` 会 `Remove-Item … -Recurse -Force` 后重新 publish ⇒ **F 可重建**。
- `dist\PCMigSetup-*.exe` **48 个（v0.1.0 → v0.4.9，每个约 95 MB）**：`tools\release.ps1:180` 注释写明「**历史安装包一律保留：交付区按「各版本并列存放」使用（用户明确要求），全量存档在 dist\**」⇒ **用户明确要求保留，属保护区**。

### 1.4 解决方案与工程引用图（★ 本轮最重要的结构性发现）

工作树共 **8 个 csproj**，但 `PCMig.sln` 只挂了 **6 个**：

| 工程 | 在 sln（工作树） | 在 sln（HEAD） | 引用 |
|---|---|---|---|
| `src\PCMig.Core\PCMig.Core.csproj` | ✅ | ✅ | → Diagnostics.Abstractions |
| `src\PCMig.Cli\PCMig.Cli.csproj` | ✅ | ✅ | → Core |
| `src\PCMig.Gui\PCMig.Gui.csproj` | ✅ | ✅ | → Core |
| `src\PCMig.Diagnostics.Abstractions\…csproj` | ✅ | ❌ | **不得有 ProjectReference/PackageReference（契约层硬约束，写在 csproj 注释里）** |
| `tests\PCMig.Core.Tests\…csproj` | ✅ | ✅ | → Core |
| `tests\PCMig.Diagnostics.Tests\…csproj` | ✅ | ❌ | → Diagnostics.Abstractions + Diagnostics + Core |
| **`src\PCMig.Diagnostics\PCMig.Diagnostics.csproj`** | ❌ | ❌ | → Diagnostics.Abstractions |
| **`src\PCMig.WinUI\PCMig.WinUI.csproj`** | ❌ | ❌ | → Core + Diagnostics |

⇒ **`PCMig.WinUI` 与 `PCMig.Diagnostics` 不在 solution 里**（两者都是未跟踪的新工程）。这解释了交接文档为什么把「sln 0 error/1 warning」与「WinUI 0 error/3 warning」分开报。**这是治理缺口（见 §8 P-G1），本轮只记录，不改。**

### 1.5 git 拓扑（实测）

- 仓库根 = `PCMig` 自身（`.git` 在 `PCMig\` 内），分支 `feature/winui-v0.5.0`，HEAD `c9aef30`。
- **tracked 仅 100 个文件**；工作树 `M 46 / ?? 144 / D 0 / A 0 / R 0` = 190；ignored 43。
- tracked 分布：`src` 56、`docs` 19、`tests` 9、`lab` 6、`tools` 4、`installer` 1、`matrix` 1、根 4。
- untracked 分布：`docs` 72、`src` 40、`tools` 18、`tests` 13、`archive` 1。
- **仓库内容有 3/4 不在版本控制内**。⇒ 任何 `git clean -fd` / `git checkout .` 会不可逆摧毁未提交成果（含全部 D6.1 诊断代码、全部 A5/WinUI 未跟踪源码、12 个契约测试文件本身、71 篇未跟踪文档）。死律 9 已禁此类命令，本报告再次点名：**治理过程绝不能顺手「清理」git 工作树**。

---

## 2. docs Inventory

### 2.1 总览

- `docs\` 递归 **92 文件**：**83 篇 `.md` 全部在顶层**（子目录只有 `screenshots\`、`ui\`），另有 **4 个 `.txt` + 5 个 `.png`**。
- **83 篇 md 里只有 12 篇被 git 跟踪，71 篇未跟踪**（契约测试、PMML 之外的全部设计与交接文档都不在版本控制内）。
- 机器生成、**禁止手工编辑**（每次跑测试会重算并覆盖）：
  - `docs\诊断系统实施-事件覆盖矩阵.md`（`tests\PCMig.Diagnostics.Tests\CoverageMatrixTests.cs:136`）
  - `docs\诊断系统实施-配置项接线审计.md`（`tests\PCMig.Diagnostics.Tests\OptionsWiringAuditTests.cs:21,114`）
- 引用列说明：`硬/软` = 「全名带 `.md` 命中的文件数」/「仅 basename 命中数」；完整「被谁引用」逐条清单见 `archive\pcmig-inventory-20261001\22-refgraph-condensed.csv`。**只有代码/测试/脚本/AGENTS 的引用才是「移动即断」风险**，纯文档互引只产生「需要同批修链接」的成本。

### 2.2 B — Governance / Mandatory Read（14 篇，全部保持原位）

| 文件 | 当前用途 | 类 | 引用(硬/软) | 推荐动作 | 迁移风险 |
|---|---|---|---|---|---|
| `发版铁律.md` | 死条律全文 · 项目最高约束 | B | 18/0 | 保持原位 | **AGENTS 引用** + 18 处文档引用 |
| `发布流程.md` | 发版操作版流程 | B | 8/3 | 保持原位 | **README 引用**（`README.md:118`） |
| `稳定性守则.md` | 功能冻结期改动准绳 | B | 6/2 | 保持原位 | `发版铁律.md` 声明共同构成强制约束 |
| `稳定性验收标准.md` | 3 次复现+3 次回归纪律 | B | 3/3 | 保持原位 | 同上 |
| `PCMig-Visual-Motion-Language.md` | PMML v1.0 规范全文（PMML-R1–R15） | B | 8/0 | **保持原位** | **Contract Test** `PmmlContractTests.cs:47,148,155` + **AGENTS 引用** |
| `PMML-UI修改硬性规范.md` | 改 UI 前必读/改完必过 | B | 8/0 | **保持原位** | **Contract Test** `PmmlContractTests.cs:48,166` + **`AGENTS.md:130-131` 断言 AGENTS 正文必须含此文件名** |
| `PMML-Implementation-Audit.md` | 代码→PMML 术语→参数映射 | B | 6/0 | **保持原位** | **Contract Test** `PmmlContractTests.cs:49,208,216` |
| `PMML-Legacy-Deviations.md` | Legacy 差异与 Known Gap 登记 | B | 6/0 | **保持原位** | **Contract Test** `PmmlContractTests.cs:50` |
| `更新日志.md` | 全版本变更日志（闸门 1/2） | A/B | 32/48 | **保持原位** | **`release.ps1:55-64` 正则强校验** + **被 3 个 csproj 内嵌**（见 2.8） |
| `测试报告-公司环境.md` | 公司实测 + 发版验证总账 | A/B | 16/0 | 保持原位 | **`AGENTS.md:34` 死律 5 的追加目标** + `release.ps1:200` |
| `首日实测检查表.md` | 随包首日实测勾选表 | A | 9/2 | 保持原位（内容陈旧，见 P-G7） | **`release.ps1:145` 复制进包** + `installer\pcmig.iss:49` 快捷方式 + 铁律第 18 条随包清单 |
| `First-Day-Company-Test-Checklist.md` | COMPANY_ONLY 15 项（C-01–C-15）公司实测主清单 | A | 10/0 | 保持原位 | 仅 docs 内互引 |
| `使用说明.txt` | 面向用户的逐版说明（闸门 3） | A | 图外 | **保持原位** | **`release.ps1:66-70` 校验、`:143` 打包** + `installer\pcmig.iss:47` |
| `更新日志.txt` | 记事本可读随包日志（由 md 生成） | F | 图外 | 保持原位 | **`release.ps1:124-129` 生成并校验 BOM**；死律 4 交付四件套 |

### 2.3 A — Active / Current（现行架构与设计依据，10 篇）

| 文件 | 当前用途 | 类 | 引用(硬/软) | 推荐动作 | 迁移风险 |
|---|---|---|---|---|---|
| `方案-诊断中心与自诊断架构.md` | 诊断系统架构方案（D1–D6 边界/授权范围） | A | 4/0 | 保持原位 | 页眉仍是 "DRAFT/未获实施授权"，与已实施事实不符（P-G9 同类文档债） |
| `诊断系统实施-D6.1-进度.md` | D6.1 每轮刷新的进度板 | A | 2/0 | 保持原位 | 无（状态快照，可重建） |
| `A5-节流与竞态修复设计.md` | 节流/竞态（P1-5/P2-6）修复设计依据 | A | 8/0 | **保持原位** | `src\PCMig.WinUI\Presentation\MigrationSessionViewModel.cs:2662`、`UiFlushPump.cs:45` 注释引用 |
| `方案-20260928-业务接线与可信度修复.md` | 阶段 A/B/C/D 业务接线与可信度方案 | A | 4/0 | 保持原位 | 阶段 B「待单独授权」、阶段 D「不得删除或缩减」的授权边界在此 |
| `技术发现-20260927-关闭崩溃转储级定位.md` | P0 关闭崩溃根因（转储指纹 7/7） | A | 4/0 | **保持原位** | `src\PCMig.WinUI\MainWindow.xaml.cs:234-243` 源码注释硬引用 |
| `技术备忘-20260927-视频验收阻塞重评估与内置H264编码器.md` | 推翻「本机无编码器⇒无法产出 mp4/gif」旧结论 | A | 0/0 | 保持原位 | 无（零引用但结论已被采用） |
| `工作交接-20261001-D6.1诊断收口与全量验收.md` | **最新交接 = 当前状态唯一入口** | A | 0/0 | 保持原位 | 无（被 5+ 篇反向引用） |
| `工作交接说明_v0.4.6.md` | v0.1.0→v0.4.6 全 46 版本与坐标口径 | D→A | 1/0 | 保持原位 | git tracked |
| `技术发现-20260928-UniformScaleHost-Spike.md` | 整应用等比缩放方案验证（1424×892 设计面） | C→A | 0/0 | 保持原位 | 已是生产默认（`MainWindow.xaml.cs:254-259`），零引用 |
| `docs\ui\v0.5-reference\*.png`（4 张，**tracked**） | v0.5 UI 参考图 | A | — | **保持原位** | 无（但在 git 里，移动会产生 rename 噪音） |

### 2.4 C — Evidence（证据，12 篇）

| 文件 | 当前用途 | 类 | 引用(硬/软) | 推荐动作 | 迁移风险 |
|---|---|---|---|---|---|
| `诊断系统实施-阶段证据.md` | D1–D6 只增不改的实施证据流水账（含更正节） | C | 5/0 | **保持原位（不可移）** | **Contract Test** `D5UiWiringContractTests.cs:265` + `WinUiStep1ContractTests.cs:91` |
| `诊断系统实施-自检报告-20261001.md` | D1–D6 独立自检/审计结论（探针目录） | C | 3/0 | 可移入 evidence 层 | 无代码引用，需同批修 3 处链接 |
| `Coverage-Matrix-V046.md` | v0.4.6 场景×环境等级覆盖矩阵 + 证据索引 | C | 9/0 | 可移入 evidence 层 | 无代码引用，需同批修 9 处链接 |
| `Test-Execution-Report-V046.md` | v0.4.6 逐场景测试执行报告 | C | 8/2 | 可移入 evidence 层 | 无 |
| `Test-Execution-Report-V046-AddendumA.md` | 附录 A：B3 故障注入批次 | C | 2/0 | 可移入 evidence 层 | 无 |
| `Private-Test-Lab-Final-Report.md` | L0–L2 执行结果最终报告 | C | 6/0 | 可移入 evidence 层 | 无 |
| `Step1视觉验收结论-V12-20260926.md` | Step1 V12 整屏视觉验收结论 | C | 2/0 | 可移入 evidence 层 | 无 |
| `FinalPolish-完成度审计-20260928.md` | 67 节完成度逐条审计（判据口径：亲见代码为准） | C | 2/0 | 保持原位 | 无 |
| `FinalPolish-标注项独立复核-20260927.md` | 与交付报告结论**相反**的独立复核 | C | 0/0 | **保持原位（不可删）** | 无（零引用但属反向证据） |
| `技术发现-20260927-关闭应用必崩.md` | P0 初证与引入时间线 | C | 1/0 | 保持原位 | 无 |
| `技术发现-20260927-布局不变量实测与源码注释不符.md` | 布局不变量算术校验与注释纠错 | C | 1/0 | 保持原位 | 无 |
| `技术发现-20260927-最小窗口尺寸边框未计入.md` | 最小窗口边框未计入缺陷实测 | C | 1/0 | 保持原位 | 无 |

### 2.5 D — Historical / Archive（历史，含交接 39 篇）

**39 篇 `工作交接-*.md`**（全部未跟踪、全部零代码引用，唯一例外标出）。除特别注明外，**推荐动作 = 归档到 `docs\handover\`（或保持原位，见 §4 决策点 D-2）**，迁移风险 = 无（仅需同批修文档互引）。

| 文件 | 硬引用 | 备注 |
|---|---|---|
| 工作交接-20260919-会话纪要与企业环境实测指引.md | 2 | |
| 工作交接-20260923-v048真实迁移修复与发版.md | 0 | 记录「改发版脚本须用户授权」 |
| 工作交接-20260923-v049响应式布局与发版.md | 0 | 同上 |
| 工作交接-20260925-Step1视觉对齐-D1-DPI-D2收口.md | 0 | |
| 工作交接-20260925-VisualPass2.5与Canonical视口.md | 0 | |
| 工作交接-20260925-WinUI-Step1视觉Pass2.md | 2 | ⚠ 被 `tests\PCMig.Core.Tests\WinUiDpiContractTests.cs:213` 注释引用 |
| 工作交接-20260926-Step1视觉保真-V0到V23与A21-A23.md | 2 | |
| 工作交接-20260926-Step1阶段1-2字体几何与V8禁用态.md | 1 | |
| 工作交接-20260926-Step1阶段10-A22A23重测与V12结论.md | 0 | |
| 工作交接-20260926-Step1阶段11-V11字号系统落地.md | 0 | |
| 工作交接-20260926-Step1阶段3-4-V10底栏与材质环境光.md | 0 | |
| 工作交接-20260926-Step1阶段5-V4b边缘高光与彩色再调.md | 0 | |
| 工作交接-20260926-Step1阶段6-V5投影三路尝试与回退.md | 0 | |
| 工作交接-20260926-Step1阶段7-V11字体层次与底栏节奏.md | 0 | |
| 工作交接-20260926-Step1阶段8-V9插画与空状态字号.md | 0 | |
| 工作交接-20260926-Step1阶段9-环境层重构与液态透明调查.md | 0 | |
| 工作交接-20260927-FinalPolish后半程实现与DPI验收卡点.md | 3 | |
| 工作交接-20260927-FinalPolish首批实现与验收阻塞.md | 3 | |
| 工作交接-20260927-GIF编码器缺陷修复与Dialog范围核查.md | 1 | |
| 工作交接-20260927-P0关闭崩溃修复与视觉层修复.md | 0 | |
| 工作交接-20260927-PanelExit补全与三项独立复核.md | 1 | |
| 工作交接-20260927-Sidebar选中过渡与Dialog动效与Motion可播放证据.md | 1 | |
| 工作交接-20260927-人工标注项视觉验收包与本轮证伪.md | 0 | |
| 工作交接-20260927-四页Shell与DesktopAcrylic材质体系与开发者调节器.md | 2 | |
| 工作交接-20260927-真实动效证据与标注原文更正.md | 1 | |
| 工作交接-20260928-A41-Sidebar光照修订.md | 0 | |
| 工作交接-20260928-A49-DAEL与Utility材质定案.md | 0 | |
| 工作交接-20260928-FinalPolish收尾-Motion修复与裁切证伪.md | 4 | |
| 工作交接-20260928-Responsive与Motion全阶段.md | 1 | |
| 工作交接-20260928-Round12-七项标注修复.md | 1 | |
| 工作交接-20260928-U62-FluidZoomTransition.md | 2 | |
| 工作交接-20260928-U64-HoverLiftPressedSink.md | 2 | |
| 工作交接-20260928-U65-UniformScaleDefault.md | 1 | |
| 工作交接-20260928-WinUI视觉精修与材质事故恢复.md | 1 | |
| 工作交接-20260928-会话总交接-U62到U65与UI冻结.md | 0 | UI 冻结决策出处 |
| 工作交接-20260930-A6已有任务下拉与四页巡检与暂停恢复.md | 2 | |
| 工作交接-20260930-A7两项UI修正与PMMLv1.0冻结.md | 0 | **PMML v1.0 FROZEN 的决策出处** |
| 工作交接-20261001-D6.1诊断收口与全量验收.md | 0 | **当前接手入口（见 2.3）** |
| 工作交接-L3企业模拟实验室-20260919.md | 4 | 被 `测试报告-公司环境.md` 等引用 |

**其余历史类**：

| 文件 | 当前用途 | 类 | 引用(硬/软) | 推荐动作 | 迁移风险 |
|---|---|---|---|---|---|
| `Corporate-Simulation-Blueprint.md` | L3 企业模拟实验室设计蓝图 | D | 8/0 | 归档（docs\archive\） | 无（需同批修 8 处链接） |
| `Private-Test-Lab-Blueprint.md` | v0.4.6 L0–L2 设计蓝图 | D | 7/0 | 归档 | 无 |
| `阶段A-交付说明-20260929.md` | 阶段 A 业务接线交付说明（A12 入口） | D | 2/0 | 保持原位 | 无 |
| `v0.5.0-WinUI-PoC-报告-20260924.md` | WinUI 独立工程 PoC 报告（工程边界决策） | D | 1/0 | 保持原位 | git tracked |
| `FinalPolish-用户指令原文-20260927.md` | 用户 67 节 FinalPolish 指令原文 | D | 3/0 | **保持原位（不可删）** | 无（用户原文归档） |
| `FinalPolish-本轮交付报告-20260928.md` | 本轮交付 + 自我更正说明 | D | 3/0 | 归档 | 无 |
| `FinalPolish-Responsive扩展说明.md` | 响应式扩展实现清单 | D | 1/0 | 归档 | 无 |
| `工作交接说明_v0.4.1.md` | v0.4.1 双机开发史（含前 106 轮会话状态） | E | 3/0 | 归档 | git tracked |
| `工作交接说明_v0.4.5.md` | v0.4.5 三机开发史 | E | 1/0 | 归档 | git tracked |
| `公司域环境验证清单.md` | 域兼容验证项 + 对 IT 申请话术 | E | 2/0 | **先并入后归档**（见 P-G6） | git tracked |
| `现场验证清单-v0.3.8.txt` | v0.3.8 现场验证清单 | E | — | 归档 | git tracked（非 md，未纳入引用图） |

### 2.6 F — Generated / Rebuildable

| 文件 | 说明 | 推荐动作 |
|---|---|---|
| `docs\诊断系统实施-事件覆盖矩阵.md` | 由 `CoverageMatrixTests` 每次运行重写（页眉自带「自动生成，勿手工编辑」） | **保持原位**（Contract Test 硬路径） |
| `docs\诊断系统实施-配置项接线审计.md` | 由 `OptionsWiringAuditTests` 每次运行重写 | **保持原位**（Contract Test 硬路径） |
| `docs\更新日志.txt` | 由 `release.ps1:124` 从 `更新日志.md` 生成（须 UTF-8 BOM + CRLF） | 保持原位 |
| `docs\screenshots\main-ui.png` | 295,729 B，2026-09-13，gitignored（`.gitignore` 有 `docs/screenshots/*.png`） | 保持原位或归档 |
| `dist\{app,cli,gui,publish-*}` | `release.ps1:132` 每次重建 | 可清理（第二阶段，见 §6） |
| `**/bin/`、`**/obj/`、`.vs\`、`src\PCMig.WinUI\bin\...\a5-model-dump.log`(55.5 MB) 等 | gitignored 构建/日志产物 | 可清理 |

### 2.7 非 md 文件（4 个 txt + 5 个 png，本轮未纳入 83 篇引用图）

| 文件 | 大小 | tracked | 判定 |
|---|---|---|---|
| `docs\使用说明.txt` | 39,623 B | ✅ | A（闸门 3，`release.ps1` 校验 + 打包 + 安装器快捷方式） |
| `docs\更新日志.txt` | 99,861 B | ✅ | F（生成物，闸门 4） |
| `docs\现场验证清单-v0.3.8.txt` | 2,693 B | ✅ | E（v0.3.8 期现场清单，已被首日/公司清单取代） |
| `docs\诊断系统实施-基线指纹-D1.txt` | 19,550 B | ❌ | **C（D1 基线指纹证据）**，未出现在任何 docs 索引里 → P-G10 |
| `docs\screenshots\main-ui.png` | 295,729 B | ❌(ignored) | F |
| `docs\ui\v0.5-reference\step{1-4}-*.png` | 1.33–1.46 MB ×4 | ✅ | A（v0.5 UI 参考图） |

### 2.8 「移动即断」硬路径总表（**任何 move 前必查**）

| 依赖方 | 位置 | 依赖的 docs 文件 | 断裂后果 |
|---|---|---|---|
| Contract Test | `tests\PCMig.Core.Tests\PmmlContractTests.cs:47-50` | `PCMig-Visual-Motion-Language.md`、`PMML-UI修改硬性规范.md`、`PMML-Implementation-Audit.md`、`PMML-Legacy-Deviations.md` | 测试直接红（4 个用例 MissingFile） |
| Contract Test | `PmmlContractTests.cs:130-131` | 断言 **`AGENTS.md` 正文含** `PCMig-Visual-Motion-Language.md`、`PMML-UI修改硬性规范.md` | 改名必须同步改 `AGENTS.md`，否则红 |
| Contract Test | `tests\PCMig.Diagnostics.Tests\CoverageMatrixTests.cs:136` | `诊断系统实施-事件覆盖矩阵.md`（读写） | 红 + 生成物丢失 |
| Contract Test | `tests\PCMig.Diagnostics.Tests\OptionsWiringAuditTests.cs:21,114` | `诊断系统实施-配置项接线审计.md`（写） | 红 |
| Contract Test | `tests\PCMig.Diagnostics.Tests\D5UiWiringContractTests.cs:265`、`tests\PCMig.Core.Tests\WinUiStep1ContractTests.cs:91` | `诊断系统实施-阶段证据.md` | 红 |
| 发版脚本 | `tools\release.ps1:55-64` | `docs\更新日志.md`（须含 `## v<Version>`） | 闸门 1 中止 |
| 发版脚本 | `tools\release.ps1:66-70`、`:143` | `docs\使用说明.txt` | 闸门 3 中止 |
| 发版脚本 | `tools\release.ps1:124-129`、`:144` | `docs\更新日志.txt`（BOM 校验） | 闸门 4 中止 |
| 发版脚本 | `tools\release.ps1:145` | `docs\首日实测检查表.md`（存在则复制进 app） | 静默不随包（**不报错**） |
| 发版脚本 | `tools\release.ps1:83` | 全仓 `*.ps1,py,md,txt,cs,xaml,iss,json,yaml` 口令残留扫描 | 新增 md 也会被扫（本报告已过） |
| 发版脚本 | `tools\release.ps1:100,110` | `README.md` 版本号正则补丁 | 版本号不更新 |
| 发版脚本 | `tools\release.ps1:200` 提示 | `docs\测试报告-公司环境.md` | 仅提示，不报错 |
| csproj 编译期 | `src\PCMig.WinUI\PCMig.WinUI.csproj:30` | `..\..\docs\更新日志.md` → `PCMig.WinUI.Assets.CHANGELOG.md` | **编译失败** |
| csproj 编译期 | `src\PCMig.Cli\PCMig.Cli.csproj:29-30` | 同上 → `PCMig.Cli.CHANGELOG.md` | **编译失败** |
| csproj 编译期 | `src\PCMig.Gui\PCMig.Gui.csproj:32-33` | 同上 → `Assets\CHANGELOG.md` | **编译失败** |
| 安装器 | `installer\pcmig.iss:47-49` | `使用说明.txt`、`更新日志.txt`、`首日实测检查表.md` | 快捷方式指向空文件 |
| 源码注释 | `src\PCMig.WinUI\MainWindow.xaml.cs:243` | `技术发现-20260927-关闭崩溃转储级定位.md` | 仅注释悬空（不致红） |
| 源码注释 | `MigrationSessionViewModel.cs:2662`、`UiFlushPump.cs:45` | `A5-节流与竞态修复设计.md` | 仅注释悬空 |
| 测试注释 | `WinUiDpiContractTests.cs:213` | `工作交接-20260925-WinUI-Step1视觉Pass2.md` | 仅注释悬空 |
| AGENTS | `AGENTS.md:4,28,31,33,34,39,60,66,69` | `发版铁律.md`、`更新日志.md`、`使用说明.txt`、`测试报告-公司环境.md`、`PCMig-Visual-Motion-Language.md`、`PMML-UI修改硬性规范.md` | **入口规则失效**（改名前必须同步改 AGENTS） |
| README | `README.md:39,111,116,118` | `docs/` 目录说明、`更新日志.md`、`使用说明.txt`、`发布流程.md` | 文档失真 |

---

## 3. Cleanup Candidates

> **本轮不执行任何一项。** 下列分类是「建议」，全部待用户批准后进第二阶段。

### 3.1 Keep（绝对保留，不进候选）

- `archive\` **全部**（342.7 MB / 1931 文件）：含 4 个 `backup-*` 源码全量副本、2 个 `baseline-A5-*`、`evidence\`（752 文件 / 414 目录）、`screenshots\`（237 文件 / 92.1 MB）、`packages\`（58.8 MB）、`pmm-l-audit\`。
  ⇒ **理由（哈希实测）**：`archive\backup-*` 内含与当前**未跟踪**源码逐字节相同的副本（`DirectoryTreeViewModel.cs` ×6、`MigrationSessionViewModel.cs` ×5、`tests\PCMig.Core.Tests\*.cs` ×3–6、`Step2SelectDataPage.xaml` ×7）。**在工作树未提交之前，这些备份可能是未提交工作的唯一第二副本。**
- `dist\PCMigSetup-*.exe` ×48：用户明确要求「各版本并列存放，全量存档在 dist\」。
- 全部 Contract Test / `release.ps1` / `csproj` / `AGENTS.md` / `README.md` / `installer` 依赖的文档（§2.8 全部）。
- 所有权证据：`docs\诊断系统实施-*`、`docs\ui\v0.5-reference\*.png`、`archive\evidence\**`、`archive\screenshots\**`、`archive\pmm-l-audit\**`。
- 用户原文归档：`FinalPolish-用户指令原文-20260927.md`。
- 反向证据：`FinalPolish-标注项独立复核-20260927.md`。
- 12 个未跟踪的契约测试文件与 `tests\PCMig.Diagnostics.Tests\`（40 文件）——**本身不在版本控制内，删即永失**。
- 未跟踪的 40 项 `src\` 新代码（D6.1 诊断三工程 + WinUI Presentation/Themes/Views/Converters + Gui Theme\V5）。

### 3.2 Merge / Index（不删，只建索引或合并）

| 对象 | 动作 |
|---|---|
| 83 篇 docs | 建 `docs\INDEX.md`（唯一入口）+ 分层目录（§4） |
| `docs\公司域环境验证清单.md` | **先把 2 块独有内容并入** `First-Day-Company-Test-Checklist.md`，再归档（P-G6） |
| `worktree` 内 `docs\使用说明.txt` 与 `docs\更新日志.txt` | 保持「md 为唯一内容源、txt 为生成物」关系，勿反向手改 |
| 根散落 174 文件 | 与 `archive\` 同哈希，**第二轮只需「删除重复副本」这一种动作**，无需再归档（归档件已在） |

### 3.3 Move / Archive（可移，需先修引用）

| 对象 | 目标 | 前置条件 |
|---|---|---|
| 39 篇 `工作交接-*.md` | `docs\handover\` | 决策点 D-2；`WinUiDpiContractTests.cs:213` 注释同步 |
| `Corporate-Simulation-Blueprint.md`、`Private-Test-Lab-Blueprint.md` | `docs\archive\` | 同批修 15 处文档链接 |
| `Coverage-Matrix-V046.md`、`Test-Execution-Report-V046*.md`、`Private-Test-Lab-Final-Report.md`、`Step1视觉验收结论-V12-20260926.md`、`诊断系统实施-自检报告-20261001.md` | `archive\evidence\<批次>\` | 同批修链接（自检报告 3 处、Coverage 9 处、V046 报告 8 硬+2 软） |
| `工作交接说明_v0.4.1/_v0.4.5.md`、`FinalPolish-Responsive扩展说明.md`、`FinalPolish-本轮交付报告-20260928.md`、`现场验证清单-v0.3.8.txt` | `docs\archive\` 或 `archive\docs-snapshots\` | 同批修链接 |
| `docs\screenshots\main-ui.png` | `archive\screenshots\` | 无（ignored） |

### 3.4 Safe Delete（**候选，第二轮才执行，逐项需批准**）

| 对象 | 体积 | 依据 |
|---|---|---|
| `dist\{app,cli,gui,publish-cli,publish-gui}` | 约 4.7 GB − 安装包 | `release.ps1:132` 每次重建（F） |
| `**\bin\`、`**\obj\`（含 `src\PCMig.WinUI\bin\Release\...\a5-model-dump.log` 55.5 MB、`poc-backdrop.log` 2.4 MB） | ≥ 60 MB | gitignored，可重建（F） |
| `.vs\` | 2.2 MB | VS 缓存 |
| `archive\scripts\diagnostics-audit-20261001\{bin,obj}\` | 约 0.5 MB | **仅 bin/obj**；同级 4 个源文件（`AuditProbe.csproj`/`Program.cs`/`ComponentAudit.cs`/`PackageAudit.cs`）**必须保留**（独立探针源码） |
| 工作区根 174 散落文件（171 个与 archive 同哈希）+ 12 个重复目录 | 22.8 MB + UserData 等 | **项目外**，且 `UserData`(817) 疑为 DSH 活数据 ⇒ 单列 H，不在此列 |
| 空目录：`archive\evidence\diagnostics-*\**` 下约 170 个 | 0 | **不是垃圾**（证据内部结构 `{events,incidents,metrics,snapshots,flight}`），**保留** |

### 3.5 Unknown / H（禁止删除，待确认）

| 对象 | 待确认什么 |
|---|---|
| 工作区根 `UserData\`(817 文件) 与 `dsh-data\UserData\` 同哈希 | 是否为 DSH 活数据（若活，则两份都不能删） |
| 工作区根 `.dsh-tool-vision`、`.dsh-vision-toolkit`、`.preset-square`、`dsh-session-recovery`、`tools\` | 是否 DSH 运行时目录（`INDEX.md` 未收录） |
| `src\PCMig.Core\Jobs\`（gitignored） | 运行时任务目录还是遗留 |
| `lab\*.txt` / `lab\*.png`（ignored，含 `vm-screen.png` 1.84 MB） | 实验残留还是证据 |
| `docs\诊断系统实施-基线指纹-D1.txt` | 归属与是否需并入证据目录（P-G10） |

---

## 4. Proposed Target Structure

### 4.1 docs 分层（**只动「历史/证据」，B/A 层原地不动**）

```
docs\
├── INDEX.md                          ← 【新建】唯一文档入口（第一层必读链）
├── 发版铁律.md 发布流程.md 更新日志.md 更新日志.txt 使用说明.txt
├── 稳定性守则.md 稳定性验收标准.md 测试报告-公司环境.md
├── 首日实测检查表.md First-Day-Company-Test-Checklist.md
├── PCMig-Visual-Motion-Language.md PMML-UI修改硬性规范.md
├── PMML-Implementation-Audit.md PMML-Legacy-Deviations.md
│   └─（以上 B/A 层 20 篇：原地不动，理由见 §2.8）
├── architecture\                     ← 【新建】当前架构与现行设计依据
│   ├── 方案-诊断中心与自诊断架构.md
│   ├── A5-节流与竞态修复设计.md 方案-20260928-业务接线与可信度修复.md
│   ├── 技术发现-20260927-关闭崩溃转储级定位.md
│   └── 技术备忘-20260927-视频验收阻塞重评估与内置H264编码器.md
├── diagnostics\                      ← 【新建】诊断子系统（D6.2 起点）
│   ├── README.md（索引：把下 6 篇串起来）
│   ├── 诊断系统实施-D6.1-进度.md
│   ├── 诊断系统实施-事件覆盖矩阵.md      ⚠ 机器生成 + Contract Test 路径 ⇒ 见 D-1
│   ├── 诊断系统实施-配置项接线审计.md    ⚠ 同上
│   ├── 诊断系统实施-阶段证据.md          ⚠ Contract Test 路径 ⇒ 见 D-1
│   └── 诊断系统实施-自检报告-20261001.md
├── qa\                               ← 【新建】测试与验收
│   ├── Coverage-Matrix-V046.md  Test-Execution-Report-V046.md
│   ├── Test-Execution-Report-V046-AddendumA.md
│   ├── Private-Test-Lab-Blueprint.md  Private-Test-Lab-Final-Report.md
│   ├── Corporate-Simulation-Blueprint.md  Step1视觉验收结论-V12-20260926.md
│   └── FinalPolish-*.md（5 篇）
├── handover\                         ← 【新建】历史交接（39 篇 + 3 篇说明）
│   └── 工作交接-*.md …
├── archive\                          ← 【新建】已取代但保留
│   ├── 公司域环境验证清单.md（先并入 10 项后）
│   └── 现场验证清单-v0.3.8.txt
├── ui\v0.5-reference\                ← 原地不动（tracked）
├── screenshots\                      ← 原地不动
└── 工作区治理\                        ← 本轮报告所在
```

**不要做**：不要为「整齐」把 B 层 20 篇挪进子目录——它们是 `AGENTS.md` / `README.md` / Contract Test / `release.ps1` / `csproj` 的**裸路径依赖**（§2.8），移动收益低、断裂面大。

### 4.2 迁移映射模板（第二阶段逐条填、逐条验）

第二阶段每移一个文件，必须先产出这样一行，**引用未查清前不得移动**：

```
old path  →  new path  →  references(文件:行)  →  required update  →  验证方式
docs\A5-节流与竞态修复设计.md → docs\architecture\A5-节流与竞态修复设计.md
  →  src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs:2662
      src/PCMig.WinUI/Presentation/UiFlushPump.cs:45
      docs/方案-20260928-业务接线与可信度修复.md:544 …
  →  同步改注释 2 处 + 文档 6 处
  →  grep 全仓旧名 = 0 命中 + dotnet test 576/0 不变
```

### 4.3 建议的唯一入口 `docs\INDEX.md`（新建，草案骨架）

```markdown
# PCMig 文档索引（唯一入口）
> 第一层（必读，任何会话开工）：见下「必读链」
> 铁律全文：docs\发版铁律.md ｜ 项目指令：..\AGENTS.md

## 必读链
1. <工作区根>\AGENTS.md            （工作区坐标 + 权限不豁免）
2. PCMig\AGENTS.md                                （11 条死律 + PMML 硬规则）
3. docs\发版铁律.md                               （最高约束）
4. docs\工作交接-<最新>.md                        （当前状态唯一入口）

## 按任务分流（第二层）
| 我要做什么 | 读这条链 |
|---|---|
| 改/审 UI | PCMig-Visual-Motion-Language.md → PMML-UI修改硬性规范.md（查值 PMML-Implementation-Audit.md；登记 PMML-Legacy-Deviations.md） |
| 发版 | 发布流程.md → 稳定性守则.md + 稳定性验收标准.md → 落笔 更新日志.md + 使用说明.txt |
| 公司实测 | First-Day-Company-Test-Checklist.md → 首日实测检查表.md → 结果追加 测试报告-公司环境.md |
| Diagnostics | architecture\方案-诊断中心与自诊断架构.md → diagnostics\README.md |
| 查历史决策 | 更新日志.md + 测试报告-公司环境.md（AGENTS.md 四.4 明定） |

## 文档生命周期规则
- architecture\ = 现行设计依据（改代码要读它）
- diagnostics\ = 诊断子系统（事件覆盖矩阵/接线审计为机器生成，**禁止手工编辑**）
- qa\ = 证据（只增不改；反向证据不得删）
- handover\ = 历史交接（只增不覆；永久保留）
- archive\ = 已被取代，保留可追溯
```

---

## 5. Mandatory Read Chain

**第一层（任何新会话，开工前 15 分钟内必须读完）**

1. `<工作区根>\AGENTS.md` —— 工作区坐标、三条最易搞错的路径、权限放宽不构成豁免。
2. `PCMig\AGENTS.md` —— 项目强制开工指令：十一条死律 + 三点五权限条款 + 三点六 PMML 入口硬规则。
3. `PCMig\docs\发版铁律.md` —— **最高约束**，死条律全文（冲突时以它为准）。
4. `PCMig\docs\工作交接-20261001-D6.1诊断收口与全量验收.md` —— **当前状态的唯一入口**（做了什么 / 没做什么 / 未决问题 P-1…P-15 / 接手首小时清单 / 回退路径）。

**第二层（按当前要做的事，只读相关一条链）**

| 任务类型 | 读这条链 |
|---|---|
| **UI / 视觉 / 动效** | `docs\PCMig-Visual-Motion-Language.md` → `docs\PMML-UI修改硬性规范.md` →（查值）`docs\PMML-Implementation-Audit.md` →（登记差异）`docs\PMML-Legacy-Deviations.md`；改动前查 `docs\工作交接-20260930-A7两项UI修正与PMMLv1.0冻结.md`（冻结决策出处） |
| **Diagnostics（D6.2 起）** | `docs\方案-诊断中心与自诊断架构.md` → `docs\诊断系统实施-阶段证据.md`（只增不改）→ `docs\诊断系统实施-事件覆盖矩阵.md`（机器生成，看「余 23 项 Reserved」）→ `docs\诊断系统实施-自检报告-20261001.md` |
| **发版** | `docs\发布流程.md`（操作版）→ `docs\稳定性守则.md` + `docs\稳定性验收标准.md` → 落笔 `docs\更新日志.md` + `docs\使用说明.txt` →（发版后）`docs\测试报告-公司环境.md` |
| **测试 / QA** | `docs\First-Day-Company-Test-Checklist.md` + `docs\首日实测检查表.md`；场景覆盖 `docs\Coverage-Matrix-V046.md`；执行记录 `docs\Test-Execution-Report-V046.md` |
| **查历史决策** | `docs\更新日志.md` + `docs\测试报告-公司环境.md`（`PCMig\AGENTS.md` 四.4 明定：不凭猜测） |
| **接手 / 交接** | `docs\handover\` 最新一篇 + `docs\更新日志.md` 版本对照表 |

**明确不进第一层**：`docs\更新日志.txt`（生成物，读 md）、`docs\公司域环境验证清单.md`（已被取代）、39 篇历史交接（按需）。

---

## 6. Destructive Operations Plan

### 6.1 本轮执行情况

**执行 0 项。** 本轮未删除、未移动、未重命名、未覆盖任何文件；未执行任何 git 写命令；未改动 `tools\*`、`AGENTS.md`、产品代码、`README.md`、`installer\pcmig.iss`。
本轮**唯一写盘**：本报告（新建 `docs\工作区治理\第一轮-Workspace-Inventory-20261001.md`，新文件、不覆盖任何历史文档）+ `archive\pcmig-inventory-20261001\` 下的证据文件。

### 6.2 第二阶段待批清单（**分批授权，不打包**）

| 批次 | 内容 | 体积 | 可回退性 | 需要的授权 |
|---|---|---|---|---|
| C-1 | 删除 `dist\{app,cli,gui,publish-cli,publish-gui}` | ~4.7 GB | 完全（`release.ps1` 重建） | 用户批准「清构建产物」 |
| C-2 | 清理 `bin`/`obj`/`.vs` 与 `a5-model-dump.log` 等 | ≥ 62 MB | 完全（重新构建） | 同上 |
| C-3 | 删除 `archive\scripts\diagnostics-audit-20261001\{bin,obj}` | ~0.5 MB | 完全（`dotnet build` 重建） | 同上（源文件保留） |
| M-1 | 建 `docs\INDEX.md` | 新建 | 直接删 | 用户批准入口方案 |
| M-2 | 历史交接 39+3 篇 → `docs\handover\` | 移动 | `git mv`/反移 | **决策点 D-2** |
| M-3 | 证据 6 篇 → `archive\evidence\<批次>\` | 移动 | 反移 | 用户批准 + 同批修链接 |
| M-4 | 架构 4 篇 → `docs\architecture\`；诊断 5 篇 → `docs\diagnostics\`；QA 8 篇 → `docs\qa\` | 移动 | 反移 | 用户批准 + 同批修链接 |
| M-5 | `公司域环境验证清单.md` 内容并入后归档 | 编辑+移动 | `git` 可回退（tracked） | 用户批准（**内容合并需先确认 2 块独有内容**） |
| W-1 | 工作区根 174 散落文件 + 12 重复目录（**项目外**） | 22.8 MB | 反移 | **用户单独授权**；`UserData` 排除 |
| N-1 | 空目录 170 个 | — | — | **建议不动**（证据内部结构） |

### 6.3 执行纪律（第二阶段沿用）

- 禁 `git reset --hard` / `git checkout .` / `git restore .` / `git clean -fd`（死律 9）；撤销只用 `git stash` / `git revert`。
- 任何移动前先落「old→new→references→required update」映射行，移动后 `grep` 旧名必须 0 命中。
- 每完成一批**立即跑一次 `dotnet test`**，确认 **576 通过 / 0 失败** 不退化。
- 不碰交付区 `<交付区>`、不碰 `<发版工作副本>`、不碰 `<旧镜像根>`、不碰 `tools\release.ps1`。

---

## 7. Current Progress Safety Check

| 必须不破坏的对象 | 现状 | 本方案会碰到它吗 | 结论 |
|---|---|---|---|
| **PMML v1.0 FROZEN** | 4 篇 PMML 文档在 `docs\` 顶层；`PmmlContractTests` 4 个硬路径 + `AGENTS.md:130-131` 字符串断言 | **不碰**（§4 明确 B 层原地不动） | ✅ 安全 |
| **Diagnostics D6.1** | 117 新文件（`src\PCMig.Diagnostics*`、`src\PCMig.WinUI\Diagnostics`、`tests\PCMig.Diagnostics.Tests`）+ 5 篇 `诊断系统实施-*` | 代码**完全不碰**；3 篇文档若移位须同步改 3 处测试路径 ⇒ 已列为 **D-1 决策点**（建议优先「原地不动」） | ✅ 安全（前提：D-1 选「不动」或同步改测试） |
| **576 通过 / 0 失败基线** | Core 290 + Diagnostics 286 | 移动**仅文档**，且无一被测试以路径依赖引用（除 D-1 三项） | ✅ 安全 |
| **D6.2 后续工作** | 未开始；起点 = `方案-诊断中心与自诊断架构.md` + 23 项 Reserved | 只影响「文档在哪」，不影响内容 | ✅ 安全 |
| **Stage B 规划** | 授权边界写在 `方案-20260928-业务接线与可信度修复.md`（阶段 B 待单独授权、阶段 D 不得缩减） | 该文档**建议保持原位** | ✅ 安全 |
| **备份与正式证据** | `archive\*` 342.7 MB、`dist\PCMigSetup-*.exe` ×48、`archive\evidence\*` | **全部列入 Keep**；`archive\scripts\...\bin|obj` 只清构建产物 | ✅ 安全 |
| **交付区 / 非权威副本** | `<交付区>`、`<发版工作副本>`、`<旧镜像根>` | 本方案**完全不涉及** | ✅ 安全 |
| **工作树未提交成果** | 190 条改动（M 46 / ?? 144），无一条被提交 | 第二阶段严禁任何 git 清理命令 | ⚠️ 需持续盯防 |

---

## 8. 本轮新发现（治理视角，均未修复）

| 编号 | 严重度 | 位置 | 事实 | 建议 |
|---|---|---|---|---|
| **P-G1** | 高 | `PCMig.sln` | sln 只挂 6/8 工程；`src\PCMig.WinUI` 与 `src\PCMig.Diagnostics` **不在任何 solution 里**（HEAD 版只有 4 个工程） | 单独一轮「sln 补齐」；需用户批准（涉及 `PCMig.sln` 已跟踪文件） |
| **P-G2** | 高 | 全仓 | tracked 仅 100 / 未跟踪 144；83 篇 docs 仅 12 篇入库；12 个契约测试文件本身未入库 | 提交策略需用户拍板（**不要自作主张 commit**） |
| **P-G3** | 高 | `archive\backup-*` | 含未跟踪源码的唯一第二副本（哈希实测） | 永久保留，不得随「去重」清理 |
| **P-G4** | 中 | 工作区根 | 174 散落文件（171 与 archive 同哈希）+ 12 重复目录，违反根 `AGENTS.md` 根目录纪律（2026-09-18 归位疑似「复制」而非「移动」） | 项目外，单独授权处理 |
| **P-G5** | 低 | `archive\evidence\diagnostics-*\**` | 约 170 个空目录，全部是证据内部结构 | **不要当垃圾清** |
| **P-G6** | 中 | `docs\公司域环境验证清单.md` | 已被 `First-Day-Company-Test-Checklist.md` 实质取代，但含 2 块独有内容（IT 申请话术 + 安全团队评审要点） | 先并入再归档 |
| **P-G7** | 中 | `docs\首日实测检查表.md` | 内容仍是 v0.2 期口径（错误 82/112），但 `release.ps1:145` 随包 + `installer:49` 快捷方式引用 ⇒ **不可移不可删** | 原地更新或维持 |
| **P-G8** | 中 | `tools\release.ps1:159` | 交付复制清单缺 `首日实测检查表.md`（已知脚本缺口，历史交接 20260923 两篇有记录） | **改脚本须用户明确授权** |
| **P-G9** | 低 | 多处页眉 | `PMML-Visual-Motion-Language.md` 页眉仍 `FREEZE CANDIDATE`、`方案-诊断中心与自诊断架构.md` 页眉仍 `DRAFT/未获实施授权`，与事实不符 | 文档债，建议在第二阶段顺带修页眉（不改语义） |
| **P-G10** | 低 | `docs\诊断系统实施-基线指纹-D1.txt` | D1 基线指纹证据（19,550 B，未跟踪）未出现在任何索引/引用图中 | 纳入 `docs\INDEX.md` 或证据目录 |
| **P-G11** | 低 | `archive\pcmig-inventory-20261001\` | 本轮一次性脚本放在工作区 `archive\<batch>\`，根 `AGENTS.md` 要求「一次性脚本一律写进 `archive\scripts\`」 | 第二阶段顺带归位（或保持，因属本轮证据包） |
| **P-G12** | 低 | 工作区根其它目录 | `dsh-session-recovery`、`.dsh-tool-vision`、`.dsh-vision-toolkit`、`.preset-square`、`tools\` 未见于 `INDEX.md` | H 待确认 |

---

## 9. 需要用户裁决的决策点

**D-1（重要）**：`docs\诊断系统实施-阶段证据.md`、`诊断系统实施-事件覆盖矩阵.md`、`诊断系统实施-配置项接线审计.md` 是 Contract Test 的**硬路径依赖**。
选 A：**原地不动**（推荐，零风险）；选 B：移入 `docs\diagnostics\` 并同批修改 3 处测试路径（多一次测试红/绿验证成本）。

**D-2**：39 篇历史交接是否移入 `docs\handover\`？
选 A：**不动**（最小改动，`docs\INDEX.md` 用索引串起来）；选 B：移入子目录（目录清爽，需修若干文档互引 + 1 处测试注释）。

**D-3**：B 层 20 篇（铁律/PMML/发版/稳定性/更新日志/使用说明/测试报告/首日清单）是否**完全不动**？（本方案默认不动，因为它被 `AGENTS.md`/`README.md`/Contract Test/`release.ps1`/3 个 `csproj` 裸路径依赖。）

**D-4**：是否批准第二阶段批次顺序 **C-1 → C-2 → C-3 → M-1 → （按 D-1/D-2 结论）M-2/M-3/M-4 → M-5**？还是只批准 C 类（清构建产物）先行？

**D-5**：工作区根 174 散落文件 + 12 个重复目录（项目外区域）是否纳入本轮治理范围？（默认**不纳入**，需单独授权）

**D-6**：`P-G1`（sln 缺 WinUI/Diagnostics）与 `P-G2`（71 篇 docs 未入库）是否本轮处理，还是留给 D6.2 之后单独一轮？

---

## 10. 一句话结论

**PCMig 的问题不是「文件太多」，而是「文件没入库」**：100 个 tracked 文件 vs 144 个未跟踪改动、83 篇文档只有 12 篇受版本控制、12 个契约测试与整个 D6.1 诊断代码都在版本控制之外——**所以第二阶段的正确方向是「建入口 + 分层 + 清可重建产物」，而不是删文件；任何 `git clean` 式操作都是灾难。**
