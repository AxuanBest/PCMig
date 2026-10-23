# PCMig 文档索引

本文件是仓库文档的唯一导航入口。文档按用途分组，内容均为产品与工程事实。

> 用户手册见 [`使用说明.txt`](使用说明.txt)｜版本历史见 [`更新日志.md`](更新日志.md)｜发布规则见 [`RELEASE.md`](RELEASE.md)

---

## 1. 用户文档

| 文档 | 内容 |
|---|---|
| [`使用说明.txt`](使用说明.txt) | 面向使用者的操作手册（随发布物交付） |
| [`更新日志.md`](更新日志.md) | 从 v0.1.0 起全部版本变更，含版本号与发布日期对照表 |
| [`更新日志.txt`](更新日志.txt) | 由 `tools/md2txt.py` 从 Markdown 生成，随包交付 |
| [`首日实测检查表.md`](首日实测检查表.md) | 首次在现场使用时的逐项检查表 |
| [`First-Day-Company-Test-Checklist.md`](First-Day-Company-Test-Checklist.md) | 同一检查表的对照版（C-01…C-15） |
| [`历史版本索引.md`](历史版本索引.md) | 逐版本证据索引：Release / 安装包 / 源码快照三者分别记录 |

---

## 2. 架构与设计

| 文档 | 内容 |
|---|---|
| [`方案-诊断中心与自诊断架构.md`](方案-诊断中心与自诊断架构.md) | Diagnostics 子系统的整体架构方案 |
| [`方案-20260928-业务接线与可信度修复.md`](方案-20260928-业务接线与可信度修复.md) | 业务接线与可信度修复方案 |
| [`A5-节流与竞态修复设计.md`](A5-节流与竞态修复设计.md) | UI 节流与竞态条件的修复设计 |
| [`验证报告与结果页返修.md`](验证报告与结果页返修.md) | 验证语义收口：结论与证据分层、四态状态、L1/L2 口径、验证报告 HTML 章节与本地打开方式（v0.5.3 之后的候选改动） |

核心引擎结构见仓库根 [`README.md`](../README.md) 第 6–9 节：前端形态、双通道 Robocopy、暂停 / 恢复 / 断点续传、Diagnostics 可信度体系。

---

## 3. 诊断系统（Diagnostics）

| 文档 | 内容 |
|---|---|
| [`诊断系统实施-事件覆盖矩阵.md`](诊断系统实施-事件覆盖矩阵.md) | 扫描生产源码的事件发布点，统计 Produced / Deep-only / Reserved / Retired（**由测试生成，不要手改**） |
| [`诊断系统实施-配置项接线审计.md`](诊断系统实施-配置项接线审计.md) | 逐项统计运行时源码里的真实消费者，只有 `Active` 才代表配置真的生效（**由测试生成，不要手改**） |
| [`诊断系统实施-Reserved事件分级.md`](诊断系统实施-Reserved事件分级.md) | 预留事件的分级与接线计划 |
| [`诊断系统实施-阶段证据.md`](诊断系统实施-阶段证据.md) | 分阶段实施证据 |
| [`诊断系统实施-自检报告-20261001.md`](诊断系统实施-自检报告-20261001.md) | 独立自检报告 |
| [`诊断系统实施-D6.2真实验证报告.md`](诊断系统实施-D6.2真实验证报告.md) | D6.2 真实验证报告 |
| [`诊断系统实施-D6.3可信度收口报告.md`](诊断系统实施-D6.3可信度收口报告.md) | D6.3 可信度收口报告 |
| [`诊断系统实施-D6.3-Final-Closure-Report.md`](诊断系统实施-D6.3-Final-Closure-Report.md) | D6.3 最终收口报告（R-1…R-7 全部关闭） |

**已知缺口**：Deep Trace 产物（`DIA.RingTriggered` / `DIA.RingSealed`）尚未真正写入证据包（自 D6.3 起会诚实标注 `included=false`）；预留事件仍有 22 个未接线。

---

## 4. 测试与质量

| 文档 | 内容 |
|---|---|
| [`测试报告-公司环境.md`](测试报告-公司环境.md) | 真实环境测试记录；每轮的根因 / 修法 / 验证方式追加于此 |
| [`稳定性守则.md`](稳定性守则.md) | 稳定性约束 |
| [`稳定性验收标准.md`](稳定性验收标准.md) | 稳定性验收判据 |
| [`qa/`](qa/) | QA 过程记录与验证证据（现行 + `qa/history/` 历史） |

测试工程：`tests/PCMig.Core.Tests`（核心 + 计划/修复/验证契约）、`tests/PCMig.Diagnostics.Tests`（诊断系统）、`tests/PCMig.Cli.Tests`（命令行参数契约：子命令 `--help` 不得触发业务、未知参数必须非零退出、`--jobs=<dir>` 与空格写法等价）。

构建与测试命令见 [`RELEASE.md`](RELEASE.md) 第 3–4 节。当前基线（2026-10-14 实测，含动效可用性修复）：**Core 1007 / 1007、Diagnostics 392 / 392、CLI 契约 50 / 50 全通过，WinUI Release 构建 0 错误**。（2026-10-13 候选轮基线为 Core 979 / 979、Diagnostics 392 / 392、CLI 50 / 50。）

实验室与工具链：`lab/three-vm/`（三 VM 测试框架）、`tools/stability-test.ps1`（逐文件 SHA256 三方核对）、`tools/uishot.ps1`（界面截图）、`tools/pcmiglab-vm.ps1` 与 `tools/l3-*.ps1`（实验室编排）。

---

## 5. 界面与视觉（PMML）

| 文档 | 内容 |
|---|---|
| [`PCMig-Visual-Motion-Language.md`](PCMig-Visual-Motion-Language.md) | 视觉与动效语言规范（v1.0 FROZEN） |
| [`PMML-UI修改硬性规范.md`](PMML-UI修改硬性规范.md) | UI 改动的硬性规范与合规声明义务 |
| [`PMML-Implementation-Audit.md`](PMML-Implementation-Audit.md) | PMML 实现审计 |
| [`PMML-Legacy-Deviations.md`](PMML-Legacy-Deviations.md) | 与旧实现的偏差记录 |
| [`ui/v0.5-reference/`](ui/v0.5-reference/) | v0.5 界面参考图 |

> 任何影响 UI 视觉 / 布局 / 材质 / 动画 / `ControlTemplate` 的改动，开工前必读前两篇规范，收工后按 PMML Compliance Gate 逐项声明；纯文案 / 逻辑改动且视觉零影响时写 `PMML Visual Impact: None`。
>
> **硬约束**：装饰层（光波 / 粒子 / 涟漪 / 光晕）永远不得影响进度真值、字节数、回执与任务状态。
>
> **动效可用性判定（2026-10-14 修复，勿回退）**：全局动效闸门是 `src/PCMig.WinUI/Presentation/MotionDirector.cs` 的 `SystemAnimationsEnabled`。它必须**只判定一次并缓存**，且不得把「窗口尚未激活」或「远程会话（RDP）中 `UISettings.AnimationsEnabled` 读到 false」当成用户不要动效——这两条都按「允许」处理、不下结论；非 UI 线程与读取异常同样按「允许」处理且不缓存。`PCMIG_FORCE_MOTION=1` 为强制开启覆写。`App.xaml.cs` 在窗口激活后调用 `MotionDirector.MarkShellInteractive()`。契约测试：`tests/PCMig.Core.Tests/P1MotionAvailabilityTests.cs`（5 条）。违反此口径会导致**整个动效层在远程会话下静默消失**（2026-10-14 曾发生并已修复）。

---

## 6. 发布

| 文档 | 内容 |
|---|---|
| [`RELEASE.md`](RELEASE.md) | **发布规则总纲**：版本声明点、构建与测试基线、五道闸门、交付四件套、逐文件 SHA256（7 组）、发版后验证、tag 策略与已发布版本不可变性 |
| [`发布流程.md`](发布流程.md) | 发版流程说明 |

发布只能通过 `tools/release.ps1 -Version X.Y.Z` 执行，禁止手工 `dotnet publish` 或直接调用 Inno Setup。

> **本地实验预览版（不属正式发布链）**：v0.5.4 的 Preview.1 至 Preview.7 均以本地预览方式产出，**未 push / 未打 tag / 未发 Release / 未运行 elease.ps1**；版本号只落在构建快照，未写回仓库。七轮 Preview 的产出时间、验证结论与已知缺陷见 [预览版演进记录.md](预览版演进记录.md)。正式版本为 v0.5.4（2026-10-24）。
>
> ⚠ 正式发布流程会读 `src/PCMig.WinUI/PCMig.WinUI.csproj` 的 `<Version>` 节点：出正式版前必须先改回正式版本号（见 `RELEASE.md` 第 2 节）。

---

## 7. 界面改版与缺陷定位过程记录

> **公开范围说明**：`docs/qa/` 下的原始 QA 报告、逐趟账本与回归日志属于内部研发证据，其中部分文件含企业真实业务数据（资产规模、对象编号）与内网地址，**不随公开仓库分发**，按公司安全规定保留在内部工作区。下表保留这些资料的**索引与主题说明**（纯文本，不提供仓库内链接），便于在内部按名检索；技术结论摘要见 [`预览版演进记录.md`](预览版演进记录.md)。

v0.5.0 之前一段界面改造与缺陷定位记录，作为技术参考保留：

| 文档 | 内容 |
|---|---|
| `qa/v0.5.4-Preview1-Progress-Truth-RootCause-and-Fix.md` | v0.5.4 Preview.1 **进度可信度**专项：三异常（进度冻结 / 恢复跳 50.7% / completedBytes 越界）根因、精确源码位置、修复前后语义、剩余风险；证据状态 VERIFIED（原始日志 `PCMig(3) (1).zip` 逐字核验） |
| `qa/逐趟账本-20261014（原始资料私有留存，不随 Public 分发）` | `JOB-20261014-143502-b5dc` 逐轮账本（5 趟 attempt 1/2/4/8/16、回执字段语义、baseBytes 影响、UI 百分比、越界反推），含证据等级标注；原始 CSV 私有留存 |
| `qa/progress-truth-regression-results.txt` | 进度真值修复回归结果：Core 1013 / Diagnostics 392 / Cli 50 全通过（两次独立运行 + 真实退出码），含未覆盖项（PENDING REALWORLD / PENDING CAPTURE） |
| `qa/v0.5.4-Preview1-Isolated-Preview-Candidate.md` | 本地隔离预览候选说明（路径 / SHA256 / 真实构建版本 / 隔离性声明 / 启动状态 BLOCKED 理由） |
| `qa/progress-truth-fix.patch` | 本轮源码改动 patch（`ProgressTruthSnapshot.cs` + `TransferOrchestrator.cs`，+63/−6） |
| `qa/v0.5.4-Preview1-RealDevice-Fix-Report.md` | v0.5.4 Preview.1 **真机验收两轮**问题修复报告（Issue 1–6 + IP-006；含 UIA 几何实测、三轮失败尝试记录、DPI 覆盖推算、六项待决策） |
| `qa/v0.5.4-Preview2-Stop-Resume-Truth-RootCause-and-Fix.md` | v0.5.4 Preview.2 **Stop/Resume 进度真值**专项：B 类（续传基线错位 / attemptEpoch 重开 / 跳 50.7%）、A 类（大型对象在途状态诚实化）、C 类（越界防回归）根因与最小修复；含原始日志逐字证据、行为探针二进制级验证 |
| `qa/逐趟账本-Preview2（原始资料私有留存，不随 Public 分发）` | Preview.2 逐趟账本：同一对象五趟（attempt 1/2/4/8/16）+ verify 交叉校验 + robocopy 末块读数 + 结论行；全部来自原始证据，无虚构事件；原始 CSV 私有留存 |
| `qa/progress-truth-preview2-regression-results.txt` | Preview.2 回归结果：Core 1030 / Diagnostics 392 / Cli 50 全通过（各两次独立运行 + 真实退出码），含反例验证与闸门 A–H 明细 |
| `qa/v0.5.4-Preview2-ChangedFiles-and-Patch.md` | Preview.2 改动清单：6 个源文件 + 1 个新测试逐条原因、有意未改动项的保留理由、上一轮遗留改动清单（PRESERVED） |
| `qa/v0.5.4-Preview2-Isolated-Preview-Candidate.md` | Preview.2 本地隔离预览候选说明（路径 / SHA256 / 真实构建版本 0.5.4-preview.2 / 已实际启动 PID / 验收建议） |
| `qa/progress-truth-preview2-fix.patch` | Preview.2 源码改动 patch（6 个文件的工作树 diff） |
| `qa/v0.5.4-Preview2-NextReview-Round3-Fix-Report.md` | Preview.2 NextReview **Round3 返修**报告：Step3 分支顺序缺陷（缺源分支恒不可达 / 终态被"进行中"吞掉）根因与修复、R10 断言误报收窄、`preview-build.ps1` 基线闸门缺陷；含反例验证与 UIA 版本身份实测 |
| `qa/v0.5.4-Preview2-NextReview-Round3-ChangedFiles.md` | Round3 改动清单：4 个文件逐条原因（含 SHA256）、有意未改动项的保留理由、上一轮遗留改动（PRESERVED） |
| `qa/progress-truth-preview2-round3-regression-results.txt` | Round3 回归结果：Core 1070 / Diagnostics 392 / Cli 50（各两次独立运行 + 真实退出码）、Round3 定向 23 项、反例验证 4/4 FAIL |
| qa/v0.5.4-Preview2-Installer-Data-Isolation-Fix-Report.md | v0.5.4 Preview.2 **安装器数据隔离整改**：Preview.2 实验安装器写 HKCU\Environment（账户级）连带影响正式版与 Preview.1 运行数据；改为随产物分发 preview-isolation.json（进程级 + Fail Closed），并新增 N9–N13/P9–P13 正负双向门禁 |
| qa/preview2-installer-data-isolation.patch | 上述整改源码补丁：tools 四个脚本 + Core/DI 侧隔离配置接入（PreviewIsolation 解析与五个数据根） |
| `qa/v0.5.4-Preview2-NextReview-artifact-metadata-round3.txt` | Round3 产物元数据实测：托管程序集指纹、apphost 存根哈希不具区分度的说明、UIA 窗口标题与三处版本徽章实测 |
| `qa/pages-readiness-step3-selector-round3.patch` | Round3 源码改动 patch（`Step3StateSelector.cs` 新增 + `PageReadiness.cs` + 测试 + `preview-build.ps1`） |
| [`v0.5.0-WinUI-PoC-报告-20260924.md`](v0.5.0-WinUI-PoC-报告-20260924.md) | WinUI 3 概念验证报告 |
| [`UI-CLOSURE-REPORT-20261005.md`](UI-CLOSURE-REPORT-20261005.md) | 界面收口报告 |
| [`UI-CLOSURE-PHASE-ABCD-20261005.md`](UI-CLOSURE-PHASE-ABCD-20261005.md) | 分阶段收口记录 |
| [`UI-CLOSURE-ROUND2-REPORT-20261005.md`](UI-CLOSURE-ROUND2-REPORT-20261005.md) | 第二轮收口报告 |
| [`UI-CLOSURE-ISSUES-20261005.md`](UI-CLOSURE-ISSUES-20261005.md) | 收口期间的问题清单 |
| [`技术发现-20260927-关闭应用必崩.md`](技术发现-20260927-关闭应用必崩.md) | 缺陷定位：关闭应用崩溃 |
| [`技术发现-20260927-关闭崩溃转储级定位.md`](技术发现-20260927-关闭崩溃转储级定位.md) | 同一缺陷的转储级定位 |
| [`技术发现-20260927-布局不变量实测与源码注释不符.md`](技术发现-20260927-布局不变量实测与源码注释不符.md) | 布局不变量实测结论 |
| [`技术发现-20260927-最小窗口尺寸边框未计入.md`](技术发现-20260927-最小窗口尺寸边框未计入.md) | 窗口尺寸缺陷定位 |
| [`技术发现-20260928-UniformScaleHost-Spike.md`](技术发现-20260928-UniformScaleHost-Spike.md) | 等比缩放宿主的技术验证 |
| [`技术备忘-20260927-视频验收阻塞重评估与内置H264编码器.md`](技术备忘-20260927-视频验收阻塞重评估与内置H264编码器.md) | 视频验收路径的技术备忘 |

---

## 8. 历史归档

| 目录 | 内容 |
|---|---|
| [`archive/`](archive/) | 已被取代或已交付的历史文档（含 `FinalPolish-*`、`阶段A-交付说明-20260929.md`、`现场验证清单-v0.3.8.txt` 等） |

历史安装包与逐版本证据见 [`历史版本索引.md`](历史版本索引.md)；当前正式版本 v0.5.4，与 v0.5.3 / v0.5.1 / v0.5.0 的安装包一同通过 GitHub Releases 提供（Latest = v0.5.4）；Legacy v0.1.x – v0.4.x 安装包待通过独立归档 Release 集中发布。安装包**一律不写入 Git 历史**。

---

## 9. 文档与源码的硬路径依赖（移动 / 改名之前必查）

以下位置**以裸路径**依赖 `docs/` 下的具体文件，移动或改名即断链：

| 依赖方 | 被依赖文档 |
|---|---|
| `tests/PCMig.Core.Tests/PmmlContractTests.cs` | 4 篇 PMML（见第 5 节） |
| `tests/PCMig.Diagnostics.Tests/D5UiWiringContractTests.cs` | `诊断系统实施-阶段证据.md` |
| `tests/PCMig.Diagnostics.Tests/CoverageMatrixTests.cs` | `诊断系统实施-事件覆盖矩阵.md` |
| `tests/PCMig.Diagnostics.Tests/OptionsWiringAuditTests.cs` | `诊断系统实施-配置项接线审计.md` |
| `src/PCMig.WinUI/PCMig.WinUI.csproj`、`src/PCMig.Cli/PCMig.Cli.csproj`、`src/PCMig.Gui/PCMig.Gui.csproj` | `docs/更新日志.md`（`EmbeddedResource`，编译期内嵌） |
| `src/PCMig.WinUI/MainWindow.xaml.cs`（注释） | `技术发现-20260927-关闭崩溃转储级定位.md` |
| `src/PCMig.WinUI/Presentation/MigrationSessionViewModel.cs`、`UiFlushPump.cs`（注释） | `A5-节流与竞态修复设计.md` |
| `tools/release.ps1` | `更新日志.md`、`使用说明.txt`、`更新日志.txt`、`首日实测检查表.md`、`测试报告-公司环境.md` |
| `installer/pcmig.iss` | `使用说明.txt`、`更新日志.txt`、`首日实测检查表.md` |
| `README.md` | `更新日志.md`、`使用说明.txt`、`发布流程.md`、`RELEASE.md`、两篇 PMML |

---

## 10. 仓库其它入口

| 位置 | 内容 |
|---|---|
| [`../README.md`](../README.md) | 项目主页：能力、架构、构建、测试、使用、发布概览 |
| [`../src/`](../src/) | 源码（`PCMig.Core` / `PCMig.WinUI` / `PCMig.Cli` / `PCMig.Diagnostics` / `PCMig.Diagnostics.Abstractions` / `PCMig.Gui`） |
| [`../tests/`](../tests/) | 测试工程 |
| [`../matrix/migration-matrix.yaml`](../matrix/migration-matrix.yaml) | 迁移策略矩阵（扫描 / 传输 / 验证三方统一排除口径） |
| [`../tools/`](../tools/) | 发版脚本、稳定性测试台、截图工具、实验室编排脚本 |
| [`../lab/three-vm/`](../lab/three-vm/) | 三 VM 测试框架 |
| [`../installer/`](../installer/) | Inno Setup 安装脚本 |

> 构建与发版产物目录（`dist`、`bin`、`obj` 等）以及本地历史归档已在 `.gitignore` 中排除，不随仓库发布。

---

## 11. 预览版演进（v0.5.4 Preview.1 – Preview.7）

v0.5.4 共产出七轮本地预览版。**审核后的公开摘要**见 [`预览版演进记录.md`](预览版演进记录.md)（含每轮产出日期、门禁结论、修复项与已知缺陷，不含内部路径、企业数据与内网地址）。

内网研发证据（原始报告 / 逐趟账本 / 回归日志 / UIA 证据）保留在内部工作区，清单见第 7 节索引。