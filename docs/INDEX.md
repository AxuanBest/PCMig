# PCMig 文档索引（INDEX）

> **本文件是 PCMig 的唯一文档入口与知识导航。**
> 它不是最高规则 —— 它是「先读什么、去哪儿找」的导航层。

## 权威顺序（自上而下，下层不得覆盖上层）

| # | 位置 | 性质 |
| --- | --- | --- |
| 1 | `E:\Project\deepseek work\AGENTS.md` | 工作区最高约束（根目录纪律） |
| 2 | `PCMig\AGENTS.md` | 项目约束（十一条死律、权限条款、PMML 入口） |
| 3 | `docs\发版铁律.md` | 发版最高约束（五道闸门） |
| 4 | **`docs\INDEX.md`（本文件）** | 文档导航（无规则效力） |
| 5 | 当前工作交接（Current Handover） | 当前会话的交接事实 |
| 6 | 当前任务对应的具体文档 | 具体方案 / 规范 / 证据 |

> 新增、移动、归档任何文档后，请回到本文件登记。历史文档（`docs\handover\history\`、`docs\archive\`、`docs\qa\history\`）只用于追溯，**不得覆盖当前规则**。

---

## 〇 Current State（截至 2026-10-01）

| 项目 | 状态 |
| --- | --- |
| 当前发布版本 | **v0.4.9**（`installer\pcmig.iss`、Cli/Core/Gui 三处 csproj 均为 `0.4.9`） |
| 当前开发线 | **v0.5.0 WinUI**（分支 `feature/winui-v0.5.0`，**尚未发版**） |
| PMML | **v1.0 — FROZEN**（UI 冻结） |
| Diagnostics | **D6.1 — COMPLETE** |
| D6.2 Real World Validation | **NOT STARTED**（等用户明确触发语） |
| Stage B | **NOT AUTHORIZED** |
| 测试基线 | `PCMig.Core.Tests` 290 + `PCMig.Diagnostics.Tests` 286 = **576 / 0** |
| 构建基线 | sln `0 error / 1 warning`（既有 xUnit2031）；WinUI `0 error / 3 warning`（既有 WMC1506 ×3） |
| Git 分支 / 治理前 Checkpoint | `feature/winui-v0.5.0` · **`2c0183b`**（`checkpoint: preserve v0.5.0 PMML frozen and Diagnostics D6.1`） |
| 当前工作交接 | `docs\工作交接-20261001-D6.1诊断收口与全量验收.md` |
| 文档入口 | 本文件（`docs\INDEX.md`） |

**一句话**：产品功能处于冻结状态（PMML v1.0 FROZEN / D6.1 COMPLETE），下一步是 D6.2 真实环境验证，但**未获授权前不得开始**。

---

## 一、【任何会话必读】启动三件套

1. `..\AGENTS.md`（工作区根约束）→ 2. `PCMig\AGENTS.md`（十一条死律）→ 3. `docs\发版铁律.md`
4. 本文件（`docs\INDEX.md`）→ 5. **当前工作交接**（见 [§七 Current Handover](#七current-handover)）

> 看不到 `AGENTS.md` 就不要动代码；看不到当前交接就不要动结构。

---

## 二、【改 UI】PMML 硬性链路（v1.0 FROZEN）

按顺序读，不得跳读：

1. `docs\PCMig-Visual-Motion-Language.md`（视觉与动效语言，v1.0 FROZEN）
2. `docs\PMML-UI修改硬性规范.md`（硬性规范）
3. `docs\PMML-Implementation-Audit.md`（实现审计）
4. `docs\PMML-Legacy-Deviations.md`（历史偏差）

**硬规则**：任何影响 UI 视觉 / 布局 / 材质 / 动画 / `ControlTemplate` 的修改，开工前必读以上两篇规范，收工后按 PMML Compliance Gate 逐项声明；纯文案 / 逻辑改、视觉零影响时写 `PMML Visual Impact: None`。

> 这四篇是 `tests\PCMig.Core.Tests\PmmlContractTests.cs` 的**裸路径依赖**，且 `PmmlContractTests` 断言 `PCMig\AGENTS.md` 正文必须含其中两篇的文件名。**不得移动、不得改名。**

---

## 三、【Architecture】现行方案与技术发现

现行设计（代码注释 / 记忆系统直接引用，**优先保持原路径**）：

- `docs\方案-诊断中心与自诊断架构.md`（诊断子系统架构）
- `docs\方案-20260928-业务接线与可信度修复.md`
- `docs\A5-节流与竞态修复设计.md`（被 `MainWindow.xaml.cs`、`MigrationSessionViewModel.cs`、`UiFlushPump.cs` 注释引用）

技术发现 / 备忘（历史事实，原位保留）：

- `docs\技术发现-20260927-关闭崩溃转储级定位.md`
- `docs\技术发现-20260927-关闭应用必崩.md`
- `docs\技术发现-20260927-布局不变量实测与源码注释不符.md`
- `docs\技术发现-20260927-最小窗口尺寸边框未计入.md`
- `docs\技术发现-20260928-UniformScaleHost-Spike.md`
- `docs\技术备忘-20260927-视频验收阻塞重评估与内置H264编码器.md`

---

## 四、【Diagnostics】诊断子系统（D6.1 COMPLETE）

- `docs\方案-诊断中心与自诊断架构.md` —— 架构（现行）
- `docs\诊断系统实施-阶段证据.md` —— 阶段证据（**不可移动**：`D5UiWiringContractTests` 读取；只增不改）
- `docs\诊断系统实施-D6.1-进度.md` —— D6.1 进度
- `docs\诊断系统实施-自检报告-20261001.md` —— 独立自检报告（人类审计报告）
- `docs\诊断系统实施-基线指纹-D1.txt` —— D1 基线指纹证据（**保留原位**，在此登记为 Baseline / Evidence）

**机器生成，禁止手工编辑**（由 Contract Test 重算覆盖）：

- `docs\诊断系统实施-事件覆盖矩阵.md` ← `tests\PCMig.Diagnostics.Tests\CoverageMatrixTests.cs:136`
- `docs\诊断系统实施-配置项接线审计.md` ← `tests\PCMig.Diagnostics.Tests\OptionsWiringAuditTests.cs:21,114`

**未完成事项**（详见当前交接）：23 个事件仍为 `Reserved`；`DIA.SerializationFailed` 只有计数器没有事件；`FS.FileReadFailure` 口径缺失；性能数字仍为候选值。

---

## 五、【Release】发版与交付

- `docs\发版铁律.md` —— **发版最高约束**（五道闸门；发版必须走 `tools\release.ps1`）
- `docs\发布流程.md`
- `docs\稳定性守则.md`
- `docs\稳定性验收标准.md`
- `docs\更新日志.md` —— 发版前置写入（同时被 3 个 csproj 以 `EmbeddedResource` 编译期内嵌 → **不可移动、不可改名**）
- `docs\更新日志.txt` —— 由 `更新日志.md` 生成（release.ps1 校验 UTF-8 BOM）
- `docs\使用说明.txt` —— 随包交付（release.ps1 + `installer\pcmig.iss` 引用 → **不可移动**）
- `docs\测试报告-公司环境.md` —— 发版后追加验证结果（死律 5）
- `docs\首日实测检查表.md` —— 随包交付（release.ps1:145 + iss:49）

> 已知债务：`首日实测检查表.md` 内容仍是旧口径；`tools\release.ps1` 交付复制清单存在缺口。**均登记不改**，留待 Release Governance 轮。

---

## 六、【QA】测试与验证

**当前 QA**

- `docs\First-Day-Company-Test-Checklist.md` —— 现行公司环境首日验证清单（C-01…C-15）
- `docs\首日实测检查表.md` —— 随包检查表
- `docs\测试报告-公司环境.md` —— 真实环境测试报告（持续追加）

**历史 QA**（`docs\qa\history\`，仅追溯）

- `Coverage-Matrix-V046.md`、`Test-Execution-Report-V046.md`、`Test-Execution-Report-V046-AddendumA.md`
- `Private-Test-Lab-Final-Report.md`、`Step1视觉验收结论-V12-20260926.md`
- `Private-Test-Lab-Blueprint.md`、`Corporate-Simulation-Blueprint.md`（实验室蓝图）

**实验室 / 工具链**

- `tools\l3-*.ps1`、`tools\pcmiglab-vm.ps1`、`tools\l3\*` —— L3 实验室部署与场景工具
- `archive\scripts\diagnostics-audit-20261001\` —— 独立诊断探针工程（`AuditProbe.csproj` / `Program.cs` / `ComponentAudit.cs` / `PackageAudit.cs`）
- `tools\stability-test.ps1`、`tools\uishot.ps1` —— 稳定性台与 UI 截图

**故障注入 / 证据**

- `archive\evidence\diagnostics-audit-20261001\*`、`archive\evidence\diagnostics-d61-20261001\*`（events / incidents / metrics / snapshots / flight）—— **原始机器证据，禁止删除；其中的空目录是证据结构，不是垃圾**

**未来三 VM / Stage B**：未开始，等授权。

**已取代**：`docs\archive\公司域环境验证清单.md`（Superseded by `First-Day-Company-Test-Checklist.md`；其「对 IT 申请资源的话术」与「给安全团队的评审要点」两块独有内容已合并保留）。

---

## 七、【Current Handover】当前工作交接

**当前**：`docs\工作交接-20261001-D6.1诊断收口与全量验收.md`

规则（与 `AGENTS.md` 铁律 10 一致）：

- `docs\` 根目录**任何时刻原则上只保留一份**真正的当前工作交接；
- 产生新交接时：旧当前交接 → `docs\handover\history\`，新交接留在 `docs\` 根目录；
- 历史交接**只移动，不改写、不合并、不删除**；
- 交接文档**只增不覆**，历史交接永久保留，**凭据不得写明文**。

---

## 八、【History】历史与归档（仅追溯，不覆盖当前规则）

- `docs\handover\history\` —— 全部历史工作交接（`工作交接-*.md`、`工作交接说明_v0.4.*.md`）。这些文档**互相引用多为同目录相对名**，整体归档后互引自动保持有效。
- `docs\archive\` —— 已被取代 / 已交付的历史文档（Superseded / Historical）：
  - `公司域环境验证清单.md` —— 已被 `First-Day-Company-Test-Checklist.md` 取代（两块独有内容已合并保留）
  - `FinalPolish-用户指令原文-20260927.md` —— 用户 67 节指令原文归档（文件带只读属性，**不可删**）
  - `FinalPolish-本轮交付报告-20260928.md`、`FinalPolish-完成度审计-20260928.md`、`FinalPolish-Responsive扩展说明.md`
  - `FinalPolish-标注项独立复核-20260927.md` —— 与交付报告结论**相反**的独立复核（**不可删**）
  - `阶段A-交付说明-20260929.md` —— 阶段 A 业务接线交付说明
  - `现场验证清单-v0.3.8.txt` —— v0.3.8 期现场清单（已被首日 / 公司清单取代）
- `docs\工作区治理\` —— Workspace Cleanup & Governance 过程报告（含第一轮盘点报告，其路径口径为**治理前快照**，属历史事实，不再更新）。
- `PCMig\archive\` —— 备份、证据、截图、历史安装包（`PCMigSetup-*.exe`）、审计材料。**绝对保护区**：`archive\backup-*`、`archive\evidence\*`、`archive\screenshots\*`、`archive\pmm-l-audit\*`、`archive\baseline-*`、`archive\packages\*` —— 一律不得删除。

---

## 九、【Guardrails】移动 / 改名之前必查

以下位置**以裸路径**依赖 `docs\` 下的具体文件，移动即断（详见 `docs\工作区治理\第一轮-Workspace-Inventory-20261001.md` §2.8）：

| 依赖方 | 被依赖文档 |
| --- | --- |
| `tests\PCMig.Core.Tests\PmmlContractTests.cs:47-50`、`:130-131` | 4 篇 PMML |
| `tests\PCMig.Core.Tests\WinUiDpiContractTests.cs:213`（注释） | `工作交接-20260925-WinUI-Step1视觉Pass2.md` |
| `tests\PCMig.Diagnostics.Tests\D5UiWiringContractTests.cs:265` | `诊断系统实施-阶段证据.md` |
| `tests\PCMig.Diagnostics.Tests\CoverageMatrixTests.cs:136` | `诊断系统实施-事件覆盖矩阵.md` |
| `tests\PCMig.Diagnostics.Tests\OptionsWiringAuditTests.cs:21,114` | `诊断系统实施-配置项接线审计.md` |
| `src\PCMig.WinUI\PCMig.WinUI.csproj:30`、`src\PCMig.Cli\PCMig.Cli.csproj:29-30`、`src\PCMig.Gui\PCMig.Gui.csproj:32-33` | `docs\更新日志.md` |
| `src\PCMig.WinUI\MainWindow.xaml.cs:243`（注释） | `技术发现-20260927-关闭崩溃转储级定位.md` |
| `src\PCMig.WinUI\Presentation\MigrationSessionViewModel.cs:2662`、`UiFlushPump.cs:45`（注释） | `A5-节流与竞态修复设计.md` |
| `tools\release.ps1` `:55-64,66-70,124-129,143-145,200` | `更新日志.md`、`使用说明.txt`、`更新日志.txt`、`首日实测检查表.md`、`测试报告-公司环境.md` |
| `installer\pcmig.iss:47-49` | `使用说明.txt`、`更新日志.txt`、`首日实测检查表.md` |
| `PCMig\AGENTS.md`、`README.md:39,111,116,118` | `发版铁律.md`、`更新日志.md`、`使用说明.txt`、`测试报告-公司环境.md`、`发布流程.md`、两篇 PMML |

**结论**：物理位置 ≠ 信息架构。信息架构由本 INDEX 表达；能不动路径就不要动，除非引用已全部修完且验证通过。

---

## 十、【Governance】治理历史

- 第一轮（Workspace Inventory，只读）：`docs\工作区治理\第一轮-Workspace-Inventory-20261001.md`
- 第二轮（Workspace Cleanup & Governance，执行）：`docs\工作区治理\第二轮-Workspace-Cleanup-Report-20261001.md`
- 治理证据与脚本：`E:\Project\deepseek work\archive\pcmig-governance-20261001\`
- 治理前增量备份：`PCMig\archive\backup-pre-governance-20261001-133659\`（336 文件，SHA256 336/336 MATCH）

---

## 十一、【Roadmap】下一步

1. **D6.2 Real World Validation**（未开始，等用户触发语）
   - Deep Trace 端到端真实窗口 / 高 DPI / 面板动效逐帧
   - 性能实测（当前数字全是候选值）
   - 210 万文件级真实迁移
   - 多会话导出与离线回放
2. **Stage B**（未授权）
3. **三 VM 企业模拟实验室**（未开始）
4. **Release Governance**（登记未改）：`tools\release.ps1` 交付清单、`首日实测检查表.md` 陈旧口径、`AGENTS.md` 版本号与 INDEX 指针
5. **Harness / DSH Workspace Cleanup**（工作区根 174 散落文件 + 12 重复目录，本轮明确不碰）

---

*本文件由 Workspace Cleanup & Governance 轮建立（2026-10-01）。文档治理规则：机器生成文档禁手改；历史文档只追溯；移动前先查硬引用。*
