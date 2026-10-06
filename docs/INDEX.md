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

构建与测试命令、当前基线（Core 569 / Diagnostics 382）见 [`RELEASE.md`](RELEASE.md) 第 3–4 节。

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

---

## 6. 发布

| 文档 | 内容 |
|---|---|
| [`RELEASE.md`](RELEASE.md) | **发布规则总纲**：版本声明点、构建与测试基线、五道闸门、交付四件套、逐文件 SHA256（7 组）、发版后验证、tag 策略与已发布版本不可变性 |
| [`发布流程.md`](发布流程.md) | 发版流程说明 |

发布只能通过 `tools/release.ps1 -Version X.Y.Z` 执行，禁止手工 `dotnet publish` 或直接调用 Inno Setup。

---

## 7. 界面改版与缺陷定位过程记录

v0.5.0 之前一段界面改造与缺陷定位记录，作为技术参考保留：

| 文档 | 内容 |
|---|---|
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

历史安装包与逐版本证据见 [`历史版本索引.md`](历史版本索引.md)；历史安装包以 Release asset 形式提供，**不写入 Git 历史**。

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
