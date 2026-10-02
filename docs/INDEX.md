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

## 〇 Current State（截至 2026-10-02）

| 项目 | 状态 |
| --- | --- |
| 当前发布版本 | **v0.4.9**（`installer\pcmig.iss`、Cli/Core/Gui 三处 csproj 均为 `0.4.9`） |
| 当前开发线 | **v0.5.0 WinUI**（分支 `feature/winui-v0.5.0`，**尚未发版**） |
| PMML | **v1.0 — FROZEN**（UI 冻结） |
| Diagnostics | **D6.1 — COMPLETE · D6.2 — EXECUTED · D6.3 — FINAL CLOSED**（2026-10-02；**Independent Verification = COMPLETED / PASSED**；收口报告 + **最终 Closure Report（已登记，见 §四）**：R-1…R-7 全部 CLOSED，Known Trust-Critical Risk = 0；**真机运行 = 2 轮**；改动已按 **2 个本地 Commit** 落库，**未 push / 未 release**） |
| D6.2 Real World Validation | **EXECUTED**（结论：Deep Trace E2E FAIL 限定 / Secret PASS / 2-9 Action 全链路 / 跨包隐私 FAIL 限定 / Stage B NOT READY） |
| D6.3 Trust Closure | **COMPLETE**（2026-10-02；五个词 Complete/Succeeded/Clean/Included/Healthy 收口；16 个工作包 + 缺口①②③；红灯夹具 18 组 + O2 6 条；真机复验①② PASS；**独立复验已 COMPLETED / PASSED** —— 结论 Conditional GO，发现 R-1…R-5，见下一行；Stage B 未进入） |
| D6.3 Remaining Risk Closure | **COMPLETE ⇒ D6.3 = FINAL CLOSED**（2026-10-02；独立复验发现 R-1…R-5 + 报告登记 R-6/R-7 全部 CLOSED；红灯探针 5 + 3 条已移除、残留 0；Diagnostics 363 → **371 / 0**；**真机运行 = 2 轮**（Run 1 PID 82212 / Run 2 PID 54632；第三个包按「增量 / 最终收口交付包」定义，**不是**第三轮真机运行）；**改动已按 2 个本地 Commit 提交（D6.3 Final Repository Closure），未 push / 未 tag / 未 release**；Stage B 未进入） |
| Stage B | **NOT AUTHORIZED** |
| 测试基线 | `PCMig.Core.Tests` **296** + `PCMig.Diagnostics.Tests` **371** = **667 / 0**（D6.3 剩余风险关闭后；原 363） |
| Solution | `PCMig.sln` **8 个工程**（2026-10-01 补齐 WinUI 与 Diagnostics；见下文 §十） |
| 构建基线 | `dotnet build PCMig.sln -c Release` → **0 error / 4 warning**（既有：xUnit2031 ×1 + WMC1506 ×3，无新增） |
| Git 分支 / Commits | `feature/winui-v0.5.0` · checkpoint **`2c0183b`**（治理前真实产品状态）→ solution **`7ba2bc1`** → governance **`c5e668c`** → 最终交接·报告 **`73fcb05`** → D6.2 收口 **`8513c25`** → D6.3 可信度收口 **`f5a4f69`**（7 笔逻辑提交） · **D6.3 剩余风险关闭（R-1…R-7）+ 最终仓库收尾 = 2 个本地 Commit**（`fix(diagnostics): close D6.3 trust-critical risks` + `docs(diagnostics): finalize D6.3 closure handover`；**未 push / 未 tag / 未 release**；**最新 HEAD 一律用 `git log --oneline -1` 实读，不写死**） |
| Workspace 治理 | **COMPLETE**（2026-10-01 第二轮：docs 顶层 `.md` 83 → 29；清出 ≈1.09 GiB 可重建产物） |
| 当前工作交接 | `docs\工作交接-20261002-D6.3剩余风险关闭R1-R7.md` |
| 文档入口 | 本文件（`docs\INDEX.md`） |

**一句话**：产品功能仍处于冻结状态（PMML v1.0 FROZEN / D6.1 COMPLETE）；D6.2 真实验证、**D6.3 可信度收口**、**D6.3 剩余风险关闭（R-1…R-7）**与 **D6.3 最终仓库收尾**均已完成（2026-10-02），**D6.3 = FINAL CLOSED，Independent Verification = PASSED，Known Trust-Critical Risk = 0**；改动已按 **2 个本地 Commit** 落库（**未 push / 未 tag / 未 release**）；**Stage B 未获授权前不得开始**；**下一步 = 等人工下一条指令（Next Task = WAITING FOR HUMAN INSTRUCTION）**。

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

## 四、【Diagnostics】诊断子系统（D6.1 COMPLETE · D6.2 EXECUTED · **D6.3 收口完成**）

- `docs\方案-诊断中心与自诊断架构.md` —— 架构（现行）
- `docs\诊断系统实施-阶段证据.md` —— 阶段证据（**不可移动**：`D5UiWiringContractTests` 读取；只增不改）
- `docs\诊断系统实施-D6.1-进度.md` —— D6.1 进度
- `docs\诊断系统实施-自检报告-20261001.md` —— 独立自检报告（人类审计报告）
- `docs\诊断系统实施-基线指纹-D1.txt` —— D1 基线指纹证据（**保留原位**，在此登记为 Baseline / Evidence）

**D6.2 产物（2026-10-01，真实运行验证）**：

- `docs\诊断系统实施-D6.2真实验证报告.md` —— **D6.2 最终报告**（16 项问答 + 14 个 Known Gap + Stage B 就绪建议）
- `docs\诊断系统实施-Reserved事件分级.md` —— 23 个 Reserved 事件分级（A 4 / B 14 / C 5）及实现顺序建议
- 机器证据（**工作区根目录，不在 Git 仓库内**）：`E:\Project\deepseek work\archive\evidence\diagnostics-d62-20261001\`（69 文件 / 717,430 B；含 `20-d62-evidence-digest.txt` 摘要、`10-package-analysis.txt` 四包复算、`tools\ui.ps1` UIA 工具、`evidence\runs\run-01-input-canary\` 原始事件）—— **禁止删除**

**D6.3 产物（2026-10-02，可信度收口 Trust Closure）**：

- `docs\诊断系统实施-D6.3可信度收口报告.md` —— **D6.3 可信度收口报告（Trust Closure 阶段报告）**（§二十四 逐项 PASS/FAIL 终报；含缺口①②③ 红灯证明与真机复验）；其后的最终口径见 §四 末「D6.3 剩余风险关闭 ⇒ 最终收尾」
- `docs\诊断系统实施-Reserved事件分级.md` —— 口径更新：Reserved **23 → 22**（`UI.NavigationChanged` 已转 Produced，见该文末「附：D6.3 轮口径更新」）
- 机器证据（**工作区根目录，不在 Git 仓库内**）—— **禁止删除**：
  - `E:\Project\deepseek work\archive\evidence\diagnostics-d63-20261001\`（D6.2→D6.3 真机证据；含 `tools\{ui,d63-tree,d63-dump}.ps1`、`d63-synthetic-source-manifest.tsv`(20,306 行)、`chains\`、`chain9-export-010341\`、`defect18-feedback-correlation\`）
  - `E:\Project\deepseek work\archive\evidence\d63-o2-gap-fixes\`（`red-proof.md` 三处红灯逐字证明；`real-export-013026\` 缺口①修复后的实机导出包解包）

**D6.3 剩余风险关闭 ⇒ 最终收尾（2026-10-02，Remaining Risk Closure · D6.3 = FINAL CLOSED）**：

- `docs\诊断系统实施-D6.3-Final-Closure-Report.md` —— **D6.3 最终 Closure Report（正式登记进本 INDEX，2026-10-02）**。定义：**D6.3 最终可信度收口报告**（§〇 一页速览 / §一 范围与约束 / §二 逐条 R-1…R-5 / §二（续）R-6/R-7 / §三 修改文件清单 / §四 测试结果 / §五 真机复验 / §六 风险状态与证据索引 / §七 结论与下一步）。**旧有 D6.3 Trust Closure、Independent Verification、Remaining Risk 相关报告全部保留，不删除。**
- `docs\工作交接-20261002-D6.3剩余风险关闭R1-R7.md` —— 本轮 Current Handover（见 §七）
- **变更范围权威口径**：D6.3 最终变更文件范围与行数，**以 Final Closure Commit 的 `git show --name-status` / `git show --stat` 为权威**；文档中出现的 `17 files changed, 520 insertions(+), 15 deletions(-)`、`18 项改动`、`工作树 17 M + 3 ??` 等数字一律为**当时阶段快照**（取自更新指针之前的工作树），**不得当作当前最终工作树描述**。
- 机器证据（**工作区根目录，不在 Git 仓库内**）—— **禁止删除**：`E:\Project\deepseek work\archive\evidence\d63-remaining-risk-closure\`（`red-proof.md`、`red-proof-r2-silent.md`、`tests\`、`build\`、`real-export-030312\`、`real-export-r2-113637\`、`git\`）

**机器生成，禁止手工编辑**（由 Contract Test 重算覆盖）：

- `docs\诊断系统实施-事件覆盖矩阵.md` ← `tests\PCMig.Diagnostics.Tests\CoverageMatrixTests.cs:136`
- `docs\诊断系统实施-配置项接线审计.md` ← `tests\PCMig.Diagnostics.Tests\OptionsWiringAuditTests.cs:21,114`

**未完成事项**（详见当前交接与 D6.3 报告）：Reserved **22** 个事件仍未实现（原分级 A 4 / B 14 / C 5 中的 `UI.NavigationChanged` 已转 Produced）；`DIA.SerializationFailed` 只有计数器没有事件；`FS.FileReadFailure` 口径缺失；**Deep Trace 产物仍进不了包（G-1/G-3）—— 但自 D6.3 起会诚实地说 `included=false`，不再以配置意图冒充"已包含"**；性能数字仍为候选值。D6.3 已完成：跨包隐私 G-2、AutomationId 绑定 G-4、`UI.NavigationChanged` G-9。

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
- `E:\Project\deepseek work\PCMig\archive\scripts\diagnostics-audit-20261001\`（**仓库内** archive）—— 独立诊断探针工程（`AuditProbe.csproj` / `Program.cs` / `ComponentAudit.cs` / `PackageAudit.cs`）
- `tools\stability-test.ps1`、`tools\uishot.ps1` —— 稳定性台与 UI 截图

**故障注入 / 证据**

- `E:\Project\deepseek work\PCMig\archive\evidence\diagnostics-audit-20261001\*`、`E:\Project\deepseek work\PCMig\archive\evidence\diagnostics-d61-20261001\*`（**仓库内** archive；events / incidents / metrics / snapshots / flight）—— **原始机器证据，禁止删除；其中的空目录是证据结构，不是垃圾**

**未来三 VM / Stage B**：未开始，等授权。

**已取代**：`docs\archive\公司域环境验证清单.md`（Superseded by `First-Day-Company-Test-Checklist.md`；其「对 IT 申请资源的话术」与「给安全团队的评审要点」两块独有内容已合并保留）。

---

## 七、【Current Handover】当前工作交接

**当前**：`docs\工作交接-20261002-D6.3剩余风险关闭R1-R7.md`
（上一份 Current `工作交接-20261002-D6.3可信度收口完成.md` 已于 2026-10-02 按本节规则移入 `docs\handover\history\`，只作历史追溯；更早的 `工作交接-20261001-D6.2真实验证完成.md`、`工作交接-20261001-Workspace治理完成与D6.2起点.md` 亦已在历史目录。）

规则（与 `AGENTS.md` 铁律 10 一致）：

- `docs\` 根目录**任何时刻原则上只保留一份**真正的当前工作交接；
- 产生新交接时：旧当前交接 → `docs\handover\history\`，新交接留在 `docs\` 根目录；
- 历史交接**只移动，不改写、不合并、不删除**；
- 交接文档**只增不覆**，历史交接永久保留，**凭据不得写明文**。

---

## 八、【History】历史与归档（仅追溯，不覆盖当前规则）

- `docs\handover\history\` —— 全部历史工作交接（`工作交接-*.md`、`工作交接说明_v0.4.*.md`）。这些文档**互相引用多为同目录相对名**，整体归档后互引自动保持有效。当前共 **45 篇**（2026-10-02 归档：`工作交接-20261002-D6.3可信度收口完成.md` 与同一批更早归档的 `工作交接-20261001-D6.2真实验证完成.md`；2026-10-01 归档：`工作交接-20261001-D6.1诊断收口与全量验收.md`、`工作交接-20261001-Workspace治理完成与D6.2起点.md`）。
- `docs\archive\` —— 已被取代 / 已交付的历史文档（Superseded / Historical）：
  - `公司域环境验证清单.md` —— 已被 `First-Day-Company-Test-Checklist.md` 取代（两块独有内容已合并保留）
  - `FinalPolish-用户指令原文-20260927.md` —— 用户 67 节指令原文归档（文件带只读属性，**不可删**）
  - `FinalPolish-本轮交付报告-20260928.md`、`FinalPolish-完成度审计-20260928.md`、`FinalPolish-Responsive扩展说明.md`
  - `FinalPolish-标注项独立复核-20260927.md` —— 与交付报告结论**相反**的独立复核（**不可删**）
  - `阶段A-交付说明-20260929.md` —— 阶段 A 业务接线交付说明
  - `现场验证清单-v0.3.8.txt` —— v0.3.8 期现场清单（已被首日 / 公司清单取代）
- `docs\工作区治理\` —— Workspace Cleanup & Governance 过程报告（含第一轮盘点报告，其路径口径为**治理前快照**，属历史事实，不再更新）。
- **证据 / 归档有三个不同的根，不得混用**（2026-10-02 定案）：
  - **工作区权威证据根（当前 D6.x 本地原始证据的权威归档根，在 Git 仓库之外）**：`E:\Project\deepseek work\archive\` —— 其下 `E:\Project\deepseek work\archive\evidence\*`、`E:\Project\deepseek work\archive\screenshots\*`、`E:\Project\deepseek work\archive\packages\*` 等**一律不得删除**（**绝对保护区**：`E:\Project\deepseek work\archive\backup-*`、`...\archive\evidence\*`、`...\archive\screenshots\*`、`...\archive\pmm-l-audit\*`、`...\archive\baseline-*`、`...\archive\packages\*`）。
  - **仓库内历史 / 本地遗留证据目录（非当前权威证据根，不纳入 Git，已被 `.gitignore` 的 `/archive/` 规则保护）**：`E:\Project\deepseek work\PCMig\archive\` —— 仓库内备份、证据、截图、历史安装包（`PCMigSetup-*.exe`）、审计材料（`backup-*`、`baseline-A5-*`、`packages\*.nupkg`、`evidence\`、`screenshots\`、`pmm-l-audit\`、`scripts\`、`a5-rollback\`）。**同样不得删除、不得移动、不得与工作区根那棵合并。**
  - **桌面交付根**：`D:\Users\User\Desktop\新建文件夹 (4)\` —— 每大轮复验包的落点，**只复制、不改源文件**。

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
- 治理前增量备份：`E:\Project\deepseek work\PCMig\archive\backup-pre-governance-20261001-133659\`（**仓库内** archive；341 文件 = 336 项目文件 + 5 份证据，SHA256 336/336 MATCH）

---

## 十一、【Roadmap】下一步

**D6.3 最终状态（2026-10-02 人工拍板，唯一权威口径 —— 与 Final Closure Report / Current Handover / `AGENTS.md` 四处必须一致）**：

| 项 | 最终值 |
| --- | --- |
| D6.3 Diagnostics Trust Closure | **FINAL CLOSED** |
| Independent Verification | **COMPLETED / PASSED**（结论 Conditional GO；发现 R-1…R-5） |
| Remaining Risk Closure | **R-1 ～ R-7 = CLOSED** |
| Known Trust-Critical Risk | **0** |
| Diagnostics Tests | **371 / 0 / 0** |
| Core Tests | **296 / 0 / 0** |
| WinUI Release Build | **0 Error / 3 Known Warnings**（WMC1506 ×3，既有基线） |
| Real-machine Verification Runs | **2**（Run 1 PID 82212 / Run 2 PID 54632） |
| Delivery / Evidence Package Count | **≥ 3**（**与真机运行次数是两个概念，不得混为一谈**） |
| PMML Visual Impact | **None**（PMML v1.0 仍 FROZEN） |
| Stage B | **NOT ENTERED** |
| Release | **NOT STARTED** |
| Next Task | **WAITING FOR HUMAN INSTRUCTION** |

> 不自行推断下一阶段是什么。上述状态由人工拍板确认，未经新的明确指令不得改变。

1. **【已完成 · 2026-10-02】D6.3 Final Repository Closure（提交 + 文档收口）**：按人工拍板采用 **2 个本地 Commit**（`fix(diagnostics): close D6.3 trust-critical risks` + `docs(diagnostics): finalize D6.3 closure handover`；**逐文件白名单 staging，禁止 `git add .` / `git add -A`**）；最终 Closure Report 已登记进本 INDEX（§四）；`PCMig\archive\` 已由 `.gitignore` 的 `/archive/` 规则保护；**未 push / 未 tag / 未 release**。D6.3 权威变更范围以 Final Closure Commit 的 `git show --name-status` / `git show --stat` 为准。
2. **D6.2 遗留修复项**：G-1/G-3 Deep Trace 出口（`DIA.RingTriggered`/`DIA.RingSealed` → 真正写进包）**仍未做**（D6.3 只做到"诚实地说 `included=false`"）；**G-2 跨包隐私令牌化 / G-4 显式 AutomationId 绑定 / G-9 `UI.NavigationChanged` 已在 D6.3 完成**
3. **Stage B**（未授权；D6.2 结论为 **NOT READY**；D6.3 的**独立复验已完成 / PASSED**；前置清单见 D6.2 报告 §十五）
4. **三 VM 企业模拟实验室**（未开始）—— 6/9 Action 真实链路与 10 项故障场景需在此执行
5. **Release Governance**（登记未改）：`tools\release.ps1` 交付清单、`首日实测检查表.md` 陈旧口径、`AGENTS.md` 版本号与 INDEX 指针
6. **Harness / DSH Workspace Cleanup**（工作区根 174 散落文件 + 12 重复目录，明确不碰）

---

*本文件由 Workspace Cleanup & Governance 轮建立（2026-10-01）；最近一次更新：**D6.3 Final Closure / Remaining Risk Closure 轮（2026-10-02）**更新 Current State / Diagnostics 产物 / Roadmap / 最终状态块。文档治理规则：机器生成文档禁手改；历史文档只追溯；移动前先查硬引用。*
