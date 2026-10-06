# PCMig 项目指令（AGENTS.md）

> 本文件是该项目的**强制开工指令**。任何 AI 会话接手本项目，**开工前必读并逐条遵守**。
> 最高约束：**`docs/发版铁律.md`（死条律）**，本文件只是入口摘要；冲突时以铁律为准。

## 一、这是什么东西

**PCMig** —— 企业内网 Windows 换机数据迁移工具（C#/.NET 8，WPF GUI + CLI，Robocopy 双通道引擎）。
在新电脑运行，输入旧电脑 IP/电脑名 + 凭据，经 SMB 把旧机共享盘数据"直拉"过来。
**当前状态（2026-10-06 ⇒ **PCMig v0.5.1 已正式发布**：release commit `8d38f3b` + annotated tag `v0.5.1`；**v0.5.0 仍是并列正式版本**（commit `022377e` + annotated tag `v0.5.0`，**tag 未移动、`PCMigSetup-0.5.0.exe` 未被覆盖**，两个版本并列保留）；v0.5.0 发布后共落库**四笔**本地 commit —— **`66d8ced`** `chore(p3): P3 三项裁决落地 + 交接与索引同步`（11 文件）、**`1594fe0`** `fix(core): 修复场景 H「盘满虚报 42 GiB / 99.9%」—— 隔离 UI 订阅者异常，恢复回冲与熔断`（3 文件）与 **`43718ce`** `docs(handover): 新增「委托链修复与 P3 裁决」交接 + 同步 INDEX/AGENTS 指针`（4 文件）；**未 push**（`git remote` = 0 个，本仓库无远端）。v0.5.1 发版当时工作树 = 0 项（干净）；**随后 2026-10-06 完成「工作区整理 / 清理」轮**（文件系统整理，**未改产品源码、未改发版脚本、未提交 commit**）与**「桌面整理（第二轮）」轮**（按用户《PCMig / Desktop 第二轮整理执行书》整理整个桌面：桌面 **53 项 → 6 项**、MOVE 42 项 / DELETE 9 项、PCMig 归档 30 项、个人文件分类归位 `D:\Users\User\Documents\Desktop-Archive\2026\`（未知个人文件**一项未删**）、§9 一次性脚本二次精简 **KEEP 11 / DELETE 88**；**未改产品源码、未改发版脚本、未提交 commit**）⇒ **当前工作树 = 69 项**（4 个 ` M`，其中 2 个为机器生成文档的时间戳行 + **65 个 `??`** ＝ `lab\three-vm\` 60 + `lab\smoke-data\README.md` 1 + 两轮交接共 4 份））**。开工前以 `docs\INDEX.md` 与当前工作交接为准；下表哈希与数字均为发版当时从工作区实读，不凭记忆。旧状态（如「发布 v0.4.9 / 开发线 v0.5.0 未发版」「D6.3 = 最新里程碑」「当前版本 v0.4.8（2026-09-19）」「PMML 仍是 Freeze Candidate」「Diagnostics 未获实施授权」「docs 顶层历史交接才是入口」「Solution 不完整」）**一律作废**。

| 项 | 当前值 |
|---|---|
| 权威工作区（唯一事实来源） | `E:\Project\deepseek work\PCMig`（交付区 `E:\Project\PCMig`；`I:\K\deepseek work` 永不作为事实依据或代码来源） |
| 文档入口 | `docs\INDEX.md`（**唯一导航入口**） |
| 当前工作交接（Current Handover） | **`docs\工作交接-20261006-桌面整理.md`**（2026-10-06「桌面整理（第二轮）」轮 —— 按用户《PCMig / Desktop 第二轮整理执行书》整理整个桌面：桌面 **53 项 → 6 项**、MOVE 42 项 / DELETE 9 项、PCMig 归档 30 项、个人文件分类归位 `D:\Users\User\Documents\Desktop-Archive\2026\`、§9 一次性脚本二次精简 **KEEP 11 / DELETE 88**（真删 179 文件）；**未改产品源码、未改发版脚本、未提交 commit**；可直接粘贴给新会话的短卡 = `docs\工作交接-20261006-桌面整理-QUICK.txt`）。上一份 Current **`docs\工作交接-20261006-工作区整理与清理.md`**（工作区清理轮：释放 **427.8 GB**、桌面 PCMig 条目 **38 → 2**、三 VM 框架归位 `PCMig\lab\three-vm\`、`E:\PCMigLab\smoke-data\` ≤100 MB 快速回归集）、更早的 **`docs\工作交接-20261006-v0.5.1正式发版.md`**、**`docs\工作交接-20261006-委托链修复与P3裁决.md`**、**`docs\工作交接-20261006-v0.5.0正式发版.md`** 与更早的产品 Current `docs\工作交接-20261002-D6.3剩余风险关闭R1-R7.md` **仍留在 `docs\` 根目录、本次未归档**（现存 **18 项**）——因为 `docs\工作交接-20261004-Trust-Critical-Recovery.md`、`docs\工作交接-20261005-*.md` 等多份并行轨道交接以**同目录相对名**引用它们，单独移动会断链；待这些并行轨道一并归档时再统一移入 `docs\handover\history\`。 |
| Workspace Governance Phase 2 | **COMPLETE** |
| Git Recovery Baseline | **ESTABLISHED** |
| Solution Governance | **COMPLETE** —— `PCMig.sln` 覆盖当前 **8 个工程**（含 `src\PCMig.WinUI` 与 `src\PCMig.Diagnostics`；治理前只有 6 个） |
| Docs Governance | **COMPLETE** —— docs 顶层 `.md` 83 → **33**（D6.3 剩余风险关闭后实读，2026-10-02：D6.2 增报告/分级/交接 3 篇，D6.3 增收口报告/最终收口报告/交接 2 篇，交替换代净增；见 INDEX §四/§六）；历史交接在 `docs\handover\history\`（**45 篇**），只作追溯 |
| PMML | **v1.0 — FROZEN** |
| Diagnostics | **D6.1 — COMPLETE · D6.2 — EXECUTED · D6.3 — FINAL CLOSED**（2026-10-02；**Independent Verification = COMPLETED / PASSED**（Conditional GO，发现 R-1…R-5）；最终 Closure Report `docs\诊断系统实施-D6.3-Final-Closure-Report.md`（**已登记进 `docs\INDEX.md` §四**）、可信度收口报告 `docs\诊断系统实施-D6.3可信度收口报告.md`、D6.2 报告仍在；**R-1…R-7 全部 CLOSED，Known Trust-Critical Risk = 0**；**真机运行 = 2 轮**；五个词 Complete/Succeeded/Clean/Included/Healthy 必须说真话） |
| 发布线 / 开发线 | 发布 **v0.5.1 — 已于 2026-10-06 正式发布**（v0.5.0 的**可信度紧急修正版**，功能与界面与 v0.5.0 一致；安装包 `E:\Project\PCMig\PCMigSetup-0.5.1.exe` **93,276,509 B / 88.96 MB**，SHA256 `4255FBD6E77CCEE1190ACDC05FC66F8A4843BE197FE993BDBE78FE495B329937`；Portable **538 文件 / 274,430,131 B**，不含 `PCMig-classic.exe`）。**v0.5.0 并列保留**（`PCMigSetup-0.5.0.exe` 158,468,801 B，**未被覆盖**；交付区并列安装包共 15 个）；分支 `feature/winui-v0.5.0`。**下一版未定 —— 等人工指令，不得自行推断** |
| 当前 Git | 分支 `feature/winui-v0.5.0`：… → Round-3 前只读基线 **`862b091`** → **release commit `022377e`（`release: PCMig v0.5.0`，75 files changed / +9692 / −616）** ＋ **annotated tag `v0.5.0`**（`git cat-file -t v0.5.0` = `tag`，指向 `022377e`）。**未 push**（`git remote` = **0 个**，本仓库无远端 ⇒ 无推送目标，未来若要 push 必须先由人工配置远端并授权）。工作树（**v0.5.0 发版提交时**）仅剩 **1 项**未提交：`tests\PCMig.Diagnostics.Tests\TestResults\diag-b3-r2.trx`（测试产物，按纪律未入库）。**⚠ 随后交接与 P3 裁决动作又产生 11 项改动（交接自身产物 4 项 + P3 三项裁决落地 4 项 + Diagnostics 测试运行重生成的 2 份文档 + `.gitignore`），已于 2026-10-06 由人工授权以**一笔独立的本地 commit**落库（逐文件白名单 staging，**未 push / 未 tag**；`chore(p3): P3 三项裁决落地 + 交接与索引同步`，sha 以 `git log --oneline -1` 实读）。⚠ 同轮会话轨道另产生 **3 项源码改动** —— `src\PCMig.Core\Transfer\RobocopyRunner.cs`、`src\PCMig.Core\Transfer\TransferOrchestrator.cs`、`src\PCMig.WinUI\Presentation\MigrationSessionViewModel.cs`（= 场景 H「盘满虚报 42 GiB / 99.9%」的委托链缺陷修复，报告 `E:\PCMigLab\Evidence\Trust-Critical-Recovery\FIX-DELEGATION-CHAIN-20261006.md`），**已于 2026-10-06 由人工授权以第二笔独立的本地 commit `1594fe0` 落库**（`fix(core): 修复场景 H「盘满虚报 42 GiB / 99.9%」—— 隔离 UI 订阅者异常，恢复回冲与熔断`，3 files changed / +44 −4）。**⇒ 工作树 = 0 项（干净）**。⚠ 本轮新增的两份交接文档与 `docs\INDEX.md` / `AGENTS.md` 指针同步（4 文件 / +475 −14），**已于 2026-10-06 由人工授权以第三笔独立的本地 commit `43718ce` 落库**（`docs(handover): 新增「委托链修复与 P3 裁决」交接 + 同步 INDEX/AGENTS 指针`）⇒ **工作树 = 0 项（干净）**。⚠ 随后**第四笔**本地 commit `0fcc2a7`（状态补记，2 文件）落库；此后 **release commit `8d38f3b`（`release: PCMig v0.5.1`，18 文件 / +302 −53）＋ annotated tag `v0.5.1`**（`git cat-file -t v0.5.1` = `tag`，指向 `8d38f3b`）落库 ⇒ **工作树 = 0 项（干净）**。**✅ `1594fe0`（场景 H 修复）已随 v0.5.1 进入正式发布物**（`PCMigSetup-0.5.1.exe` 93,276,509 B / SHA256 `4255FBD6…`）；v0.5.0 发行包不含该修复（**保持原样、不回改**），故 v0.5.1 为可信度紧急修正版、**两版并列保留**。****最新 HEAD 一律用 `git log --oneline -1` 实读，不写死。** 历史链（`2c0183b` → `7ba2bc1` → `c5e668c` → `8513c25` → `f5a4f69` → D6.3 关闭 2 笔本地 Commit → `862b091`）仍可用 `git log` 追溯 |
| 验证基线 | Build `dotnet build PCMig.sln -c Release` → **0 error / 4 warning**（全部既有：`WMC1506` ×3 @ `src\PCMig.WinUI\Views\PCMigSurface.xaml:53/54/56` + `xUnit2031` ×1 @ `tests\PCMig.Core.Tests\A5PresentationRegressionTests.cs(1692,9)`；**2026-10-06「委托链修复与 P3 裁决」轮实读修正 —— 旧记「0 error / 3 warning」漏算了 `xUnit2031` 这一条；该文件本轮未被改动**）；Tests **Core 569 / 569 + Diagnostics 382 / 382**（2026-10-06 v0.5.0 发版轮**重跑**，非沿用旧数；round-3 期间曾出现过一次 1 条 `D61ShutdownAndLossTests.QueuedEventsAreDrainedBeforeTheCleanMarkerIsWritten` 负载抖动，复跑全绿，**未放宽断言**）。**⚠ 构建前必须先 `Get-Process PCMig.WinUI -ErrorAction SilentlyContinue \| Stop-Process -Force`**，否则应用在跑会因文件锁定产生 MSB3027/MSB3021 的**假失败**（曾误看成 6 error / 33 warning）。旧基线 667/0（Core 296 + Diagnostics 371 = D6.3 时期）已作废。证据：`E:\Project\deepseek work\archive\pcmig-governance-20261001\s1-build.log`（治理时）、`E:\Project\deepseek work\archive\evidence\d63-remaining-risk-closure\`（D6.3 时期）—— 两者均在**工作区权威证据根** `E:\Project\deepseek work\archive\` 之下 |
| 下一步 | **WAITING FOR HUMAN INSTRUCTION（等人工下一条指令）** —— **「工作区整理 / 清理」轮已于 2026-10-06 完成**（按用户执行书 §0–§19 做文件系统清理与归档，**未改产品源码、未改发版脚本、未提交 commit**）：释放 **427.8 GB**（C +45.3 / D +168.1 / E +214.4）；synthetic payload 424.48 GB、临时 publish 2.83 GB、构建测试产物 770.81 MB 按明确路径清除；桌面 PCMig 条目 **38 → 2**；三 VM 框架归位 **`PCMig\lab\three-vm\`**（60 文件）+ 快速回归集 `E:\PCMigLab\smoke-data\`（57.23 MB ≤100 MB）；归档根 `archive\review-packages\`（34 条目）、`archive\bug-evidence\`（7 子类）、`archive\design-reference\`、`archive\scripts\cleanup-20261006-root-scripts\`（99 个一次性脚本）；§18 十一项验收报告 = **`archive\cleanup-20261006\ACCEPTANCE-REPORT-11.md`**（同目录 `CLEANUP-PLAN.csv` / `DELETE-LOG.txt` / `MOVE-LOG.txt` / `FINAL-VERIFY-20261006.txt`）；离场三验 = Build 0 error / 4 warning（= 基线）、Core **569/569**、Diagnostics **382/382**；**工作树 = 69 项**（4 ` M` + 65 `??`）**待授权提交**（「桌面整理（第二轮）」轮完成后的实读口径）；⚠ 如实披露：9 个**未受 git 跟踪**的 `.trx` 已按 §10 删除且**不可从 git 恢复**；三 VM 的 `.avhdx` 检查点差分盘（可回收 ≈315 GB）**未动**。**候选待办（本轮新增，均未获授权）**：⓪ 是否提交那 61 个未跟踪框架文件（`lab\three-vm\` 60 + `lab\smoke-data\README.md` 1）；⓪ 是否清理三 VM 的 `.avhdx` / `.VMRS` 检查点（**破坏性**，会丢回滚点，需人工明确授权）；⓪ 是否恢复那 9 个 `.trx`（若判定为正式证据）。**PCMig v0.5.1 已于 2026-10-06 正式发布完毕**（release commit `8d38f3b` + annotated tag `v0.5.1` + 发版后三验通过 + **7 组**逐文件 SHA256 全 MATCH；**v0.5.0 与 `PCMigSetup-0.5.0.exe` 均未被移动/覆盖，两个版本并列保留**）；「**v0.5.1 正式发版**」轮已完成并落库（`8d38f3b`，工作树 0 项）。**未获授权不得自行开工、不得推断下一阶段。** 此前两轮的候选待办，**① ② ③ 均已完成**：① **P3 三项裁决 = 已执行并落库**（`66d8ced`；B1 L-19 底栏早已是 Compact 变体且 `ProgressMotionDriver.cs` 零引用 ⇒ 已删、B2 `Models.cs:277` 注释已改、B3 `.gitignore` 已加 `**/TestResults/`；证据 `E:\PCMigLab\Evidence\Trust-Critical-Recovery\P3-DECISIONS-20261006.md`）；② **场景 H 疑点 = 已干净复现并定性根因**（UI `JOB-20261006-100102-a13a` 复现 99.9% / 45097156608 B；根因 = 委托注册顺序 → 跨线程写 ObservableCollection → 空 catch 吞异常但中断多播订阅链 → `OnRunnerErrorLine` 永不执行 ⇒ 回冲/熔断双失效）**并已修复（`1594fe0`）且随 v0.5.1 进入正式发布物**（D6 UI 三轮 + D7 CLI 三轮全 PASS，completedBytes 恒 0、熔断 `HIT-BREAKER=3`；报告 `E:\PCMigLab\Evidence\Trust-Critical-Recovery\FIX-DELEGATION-CHAIN-20261006.md`）；③ **未测三项 = C1/C2/C3 全部 CLOSED**（C1 CanvasControl 卡顿量化：最重场景 p95 1.58 ms = 一帧的 9.5% ⇒ 无卡顿证据；C2 生命周期矩阵四场景 Canvas 数恒 1、最小化 CPU 0%；C3 非默认 DPI 6/6 + UniformScale 6/6 + Hero 控件==Canvas 6/6 全等；汇总 `TRACK-C-SUMMARY-20261006.md`）。**用户侧候选待办（均**未**获授权）**：① 是否**配置远端并 push**（当前 `git remote` = 0 个；`v0.5.0` / `v0.5.1` 两个 annotated tag 与全部历史目前只在本地）；② 是否在**真实物理机**上复验 v0.5.1（本轮全部证据来自本机 lab + UI 夹具；**人工视觉终验亦未做**；✅ 修复代码 `1594fe0` 已随 v0.5.1 进入正式发布物，v0.5.0 发行包保持原样不回改）；③ **回冲路径补测** —— D6/D7 均 `HIT-RETRACT=0`，即「先入账成功后失败」的窗口本次未触发，回冲（`_pendingCredit.TryRemove` / `_largeCredited` 扣回）**尚未被实验直接证据覆盖**；④ `docs\` 根 **16 项**交接成组归档到 `docs\handover\history\`（本轮同样未执行归档，原因见「当前工作交接」行）；⑤ **PHASE G 录屏**未做；⑥ 文档旧坐标 `I:\PCMig` 统一（铁律 18/21 与附录 A 仍写旧路径）。**下一版版本号未定，等人工指令，不得自行推断。** D6.2 遗留项：G-1/G-3（Deep Trace 真正进包）仍未做；G-2/G-4/G-9 已在 D6.3 完成 |
| Stage B | **NOT AUTHORIZED / NOT STARTED** |
| 三 VM / 210 万文件 / 大规模故障注入 | **ROUTE A = FULL CRITICAL ROUTE A CLEAN / CLOSED（2026-10-04 17:08 实读，实验室检查点）**：实验室根 `E:\PCMigLab\`（规格 `Staging\RouteA-Spec.md`，夹具/用例 `Staging\ctl\{cases,host,faults}\`，证据 `Evidence\RouteA\`；LAB-DC01 / LAB-SRC01 / LAB-DST01）。**GROUP A/B/C/D = CLEAN/CLOSED**（C01–C11 VALID；D 组 13:40 关闭）；**FINAL CRITICAL MATRIX（20 行，stamp fx5）+ HIGH-RISK REPEAT GATE（9 场景，stamp gate1）= CLEAN/CLOSED（17:02）**；按用户 2026-10-04 决定采用 Risk-Based Final Gate 取代旧「A+B+C+D 连续三轮 CLEAN」要求（如实记录）。**Route A 范围内 PRODUCT BUGS OPEN = 0**。唯一受测候选构建 = 部署于 13:06:38 的 `1.0.0+d1aefb2f`（Host==Guest，与清 `git diff` 后重建的文件字节完全一致），未 push / 未 tag / 未 release。实验室侧交接：`E:\PCMigLab\Evidence\RouteA\SESSION-HANDOFF-ROUTE-A-FULL-CLEAN.md`（+ `…-QUICK.txt`）、检查点 `…\CURRENT-ROUTE-A-CHECKPOINT.txt`、账本 `…\Route-A-Bug-Ledger.md`、归档 `…\history\`。**⚠ 同日更晚进展：该候选版在用户两台真实物理机的 200+ GB 验收中暴露 P0 暂停失效 / 诊断假绿 / 进度真值 / UI 布局问题 ⇒ 已开启并行 lab 轨道 Trust-Critical Recovery（交接 `docs\工作交接-20261004-Trust-Critical-Recovery.md`，权威指令与证据 `E:\PCMigLab\Evidence\Trust-Critical-Recovery\`）。Route A 的 CLEAN 结论在其**原验收范围内**有效，但存在 **Acceptance Scope Gap（Route A 全部用例从未点击暂停控件，`RouteA-Spec.md` 亦零次提及「暂停/Pause」）**，故不得据此宣称「暂停已验收」。** |
| Mnemon memory sync | **AVAILABLE / WORKING**（2026-10-02 实读修正）—— 官方 Mnemon CLI **已安装且可用**：`mnemon --version` ⇒ **`mnemon version 0.2.9`**（npm 全局 shim `C:\Users\User\AppData\Roaming\npm\mnemon.cmd`；**不是**独立 `mnemon.exe`，故按「找 `mnemon.exe`」判断会误报缺失）；数据目录 `C:\Users\User\.mnemon`，Memory Space = **`default`**（`mnemon store list` ⇒ `* default`；`mnemon status` ⇒ 16 insights / 167 edges / oplog 22，DB `data\default\mnemon.db` 208,896 B）；DSH provider `mnemon-native` 的 `capabilities.remember = true`、`writeMode = "exact"`。**旧描述「本机缺 `mnemon.exe`、Memory Space 写入被拒」自 2026-10-02 起作废。** 当前状态摘要另存 Mnemon Documents（Document `57fe2a88-20ee-4c6b-ac7a-6cacf95cb4d1`） |

## 一点五、新会话必读链（开工顺序，强制）

任何新的 PCMig 会话 / Agent，开工前按下列顺序读，不得跳读、不得在几十篇历史 Markdown 里随手挑一篇当依据：

1. `E:\Project\deepseek work\AGENTS.md`（工作区最高约束）
2. `E:\Project\deepseek work\PCMig\AGENTS.md`（本文件：死律 + 权限条款 + PMML 入口 + 当前状态）
3. `docs\INDEX.md`（当前文档**唯一导航入口**）
4. 由 `docs\INDEX.md` 指向的 **Current Handover**
5. 再按任务类型读对应专项文档：
   - **UI / 视觉 / 动效** → PMML 链：`docs\PCMig-Visual-Motion-Language.md` → `docs\PMML-UI修改硬性规范.md` → `docs\PMML-Implementation-Audit.md` → `docs\PMML-Legacy-Deviations.md`
   - **Diagnostics** → `docs\方案-诊断中心与自诊断架构.md` + `docs\诊断系统实施-阶段证据.md` / `诊断系统实施-事件覆盖矩阵.md` / `诊断系统实施-配置项接线审计.md` / `诊断系统实施-D6.1-进度.md`
   - **QA / Testing** → `docs\qa\` + `docs\测试报告-公司环境.md` + `docs\First-Day-Company-Test-Checklist.md` + `docs\首日实测检查表.md`
   - **Release** → `docs\发版铁律.md` + `docs\发布流程.md` + `docs\稳定性守则.md` + `docs\稳定性验收标准.md`
   - **History** → `docs\handover\history\` + `docs\archive\`（**只作历史追溯，不得当作当前规则或当前状态依据**）

`docs\INDEX.md` 是当前文档的唯一导航入口，但**它不是最高约束**：最高约束仍是本文件、工作区根 `AGENTS.md`、`docs\发版铁律.md` 及 PMML 等专项强制规则，INDEX 与当前交接都不得覆盖它们，冲突时以约束层为准。INDEX 只解决一件事：防止新会话在几十篇历史 Markdown 中随机挑文档。

## 二、坐标（唯一权威，不许用错）

| 用途 | 路径 |
|---|---|
| **权威工作区（唯一事实来源）** | `E:\Project\deepseek work\PCMig` |
| **交付区（对外交付物只落这里）** | `E:\Project\PCMig` |
| 工作副本（发版脚本自动建） | `D:\PCMig` |
| 源码镜像备份 | `E:\Project\镜像备份源码\PCMig` |
| 发版脚本 | `E:\Project\deepseek work\PCMig\tools\release.ps1` |
| 稳定性测试台 | `tools\stability-test.ps1` |
| 截图工具 | `tools\uishot.ps1` |

**`I:\K\deepseek work` 是旧的工作文件（移动硬盘镜像副本），只可作覆盖目标，绝不可作为事实依据、代码来源或判断基准。**

工作区根目录纪律：根目录只允许 `PCMig\`、`AGENTS.md`、`INDEX.md` 与 `labs\`/`archive\`/`projects\`/`dsh-data` 四个分类目录，一次性脚本一律写进 `archive\scripts\`。

## 三、十一条不可违反的死律（详见 `docs/发版铁律.md`）

1. **功能冻结**（自 v0.2.28）：只做修复、加固、纯视觉层。禁止动 ViewModel / Command / 绑定 / 迁移与 Robocopy 逻辑 / 状态机 / 错误处理 / 网络检测；禁止改存档格式（`job-state.json` 字段语义、`plan.json`、Receipt）。
2. **先写日志，再打包**：`docs/更新日志.md`（正文一节 + 对照表一行）、`docs/使用说明.txt`（顶部一段）写好才许发版。
3. **发版必须走脚本**，禁止手工打包：`powershell -NoProfile -ExecutionPolicy Bypass -File "E:\Project\deepseek work\PCMig\tools\release.ps1" -Version X.Y.Z`；五道闸门必须全绿；一个版本号只发一次。
4. **交付四件套 + 逐文件 SHA256 全 MATCH**：安装包、`E:\Project\PCMig\Portable\`、`E:\Project\PCMig\更新日志.txt`（UTF-8 BOM+CRLF）、工作副本。
5. **发版后三验**：应用内「更新日志」/ 记事本打开 txt / `pcmig changelog`；本轮根因·修法·验证追加进 `docs/测试报告-公司环境.md`。
6. **两类独立证据**：复现证据 + 修好证据（不同方法）。故障类 **连续复现 3 次 + 修复后回归 3 次**，正常路径不退化；数据正确性必跑 `tools\stability-test.ps1`。
7. **看不见的东西不改**：UI 改动走"修改→编译→启动→`uishot.ps1` 截图→视觉模型自查→通过才继续"闭环，范围锁死在 Theme/Style/模板层；回退用 `PCMIG_CLASSIC_UI=1` 或 exe 旁 `classic-ui.flag`。
8. **编码禁区**：所有 `.ps1` 必须 **UTF-8 带 BOM**（编辑后复查前三字节 `EF BB BF`）；禁止注释式批量替换（v0.3.7 事故）；XAML 隐式 Style 只改不新建；验证必须跑刚 Build 的新 EXE；截图必须定位 PCMig 真实窗口；真实口令零残留（脚本第 5 道闸门）。
9. **禁止破坏性 git 命令**：不执行 `git reset --hard` / `git checkout .` / `git restore .` / `git clean -fd`（会不可逆抹掉未提交成果）；撤销改动用 `git stash` 或 `git revert`。
10. **交接文档只增不覆**：用户要求交接时，必须**新增**而非覆盖 `docs\工作交接-YYYYMMDD-主题.md`，完整记录本会话事实、证据、回退、限制和下一步；所有历史交接文档永久保留；凭据一律不写明文。
11. **子模型优先执行，主控负责规划与验收**（铁律 50）：能安全、有效地由子模型/子代理独立承担的工作必须优先委派并尽可能扩大其范围；主控只负责总体规划、任务拆分、授权与约束边界、关键决策、结果交叉核验与最终验收，并对项目铁律、备份、证据、发版与安全负最终责任，不得把未核验的子模型结论当事实，也不得以委派为由免责。

## 三点五、权限放宽不构成豁免（AI 强制条款，v0.4.7 起）

本节为**工具权限与项目纪律的边界条款**，与工作区根 `E:\Project\deepseek work\AGENTS.md` 同名条款互为补充，冲突时**以更严者为准**。

会话权限预设切到 **完全权限**（`danger-full-access` + 审批 `never`）、或 DSH 客户端以**管理员身份**启动、或文件沙箱不再限制工作区外写入时，本条自动生效：

1. **权限只影响效率，不豁免纪律。** 第三节十条死律一条不少地继续适用。"现在不弹窗了""我是管理员"**不是**跳过先写日志再打包、五道闸门、SHA256 全 MATCH、发版后三验、两类独立证据的理由。
2. **"不再弹窗" ≠ "已获授权"。** 权限放宽只取消确认提示，不授权任何具体动作。以下动作**即使技术上完全可做，也必须先拿到用户当轮明确指令**：
   - 删除/覆盖/移动 `I:\PCMig` 交付区内容、`D:\PCMig` 工作副本、`I:\镜像备份源码\PCMig`；
   - 修改 `tools\release.ps1`、`tools\stability-test.ps1`、`tools\uishot.ps1` 等发版与验证脚本；
   - 改动功能冻结范围（第 1 条死律清单）内的代码，或 `job-state.json` / `plan.json` / Receipt 的存档格式；
   - 执行任何破坏性 git 命令、清理未提交改动。
3. **破坏性动作先报路径与回退方案**：动手前说明"要动哪些路径、能否回退、备份在哪"，没有可回退路径就先备份。
4. **机器权限不得升格为事实依据**：`I:\K\deepseek work`（旧移动硬盘镜像）即便可写，也永远不是权威源或代码来源。

## 三点六、PMML v1.0 设计语言入口（硬规则）

**任何影响 PCMig UI 视觉、布局、材质、动画或 ControlTemplate 的修改，开工前必须先读
`docs/PCMig-Visual-Motion-Language.md` 与 `docs/PMML-UI修改硬性规范.md`，并在完成后按
`PMML Compliance Gate` 逐项声明（PASS / N/A / Known Gap）。** 只改文案、数字、状态文字、日志文本、
数据绑定内容与业务逻辑（视觉零影响）的修改，写 `PMML Visual Impact: None` 即可，不走完整 Gate。

## 四、每次交付版本时必须做的事

1. 逐条自查 `docs/发版铁律.md` 第十部分清单，**并在回复中声明遵守**。
2. 给出：交付物绝对路径、哈希校验结论、三验结果、写进测试报告的条目。
3. 做不到 / 没实测 / 有疑问的，**如实说明**，不许瞒、不许装懂；有疑问先问用户再动手。
4. 技术路线与历史决策**先查 `docs/更新日志.md` 与 `docs/测试报告-公司环境.md`**，不凭猜测。

## 五、其他

- 敏感凭据（`settings.yaml`、历史会话文稿）一律用"见某处"引用，不复制明文。
- 触碰磁盘请分清本机与公司机：本机能用的只有 `I:`、`D:`、`K:`；`\\Szlt500781`、域账号等公司环境资源本机不可达。
- V1.5 路线图（Outlook Recipe 等）在功能冻结解除前**不主动开工**。
- **v0.5.0 正式发版本轮（2026-10-06，已发版、已 commit、已 tag）**：从 Round-3 收尾（场景 G/H 取证 + 30 s 性能采样 + P3 裁定 + P4 报告）一路做到正式发布。会话交接 `docs\工作交接-20261006-v0.5.0正式发版.md`（短卡 `…-QUICK.txt`）；逐项根因·修法·证据·未测项见 `docs\测试报告-公司环境.md` **末尾**「## v0.5.0 发版记录（2026-10-06）」。本轮把发版链 **WinUI 化**（`tools\release.ps1` 268 行 / 9 组哈希、`installer\pcmig.iss` 主入口 = WinUI、`docs\发版铁律.md` 新增**铁律 13-A** 与 **20-A**、铁律 13/18/19 同步改造），并顺带修掉发版链自身三个缺陷：`Patch()` 假「已是目标版本」、`python` 命中 Microsoft Store 执行别名存根、`.ps1` 在编辑中丢 BOM。**用户明确授权**改 `tools\release.ps1` 与 `docs\发版铁律.md` 的原话含「那个发版流程的铁律也需要改一改了。顺带改了吧」。
- **并行 lab 轨道（Trust-Critical Recovery）**：2026-10-04 真实机 200+ GB 验收暴露 P0 暂停失效 / 诊断假绿 / 进度真值 / UI 布局问题后开启的修复战役。**最新会话交接见 `docs\工作交接-20261005-Trust-Critical-Recovery.md`**（2026-10-05 02:00：FIX BATCH 1→7 全部施工完成、两个信任级缺陷已修复并真机复测、`RECOVERY GATE` case1/2/3/4/5/7 = CLEAN；改动全部未 commit = 工作树 51 modified + 23 untracked；**唯一未结卡点 = 缺陷#3**「进程被杀→重启→采纳中断任务后首次点『恢复任务』被完全吞掉」，产品浮层 light-dismiss 与夹具强制激活两种解释尚未判定）；上一份交接 `docs\工作交接-20261004-Trust-Critical-Recovery.md` 只增不覆保留。权威施工指令、baseline、证据与检查点在 `E:\PCMigLab\Evidence\Trust-Critical-Recovery\`（`INSTRUCTION-Trust-Critical-Recovery-Campaign.md`、`SESSION-HANDOFF-…-20261005-0200.md`（+`-QUICK.txt`）、`CURRENT-TRUST-CRITICAL-CHECKPOINT.txt`、`recovery-gate\`）。该战役由用户明确授权改动 Pause 语义 / 诊断契约 / 进度真值 / UI 结构（功能冻结范围），但**不解除发版铁律**：战役结束产出的是 Final Fix Candidate，**不发版、不 push、不打 tag**。
- **并行 lab 轨道（UI Closure / 视觉与动效收口）**：2026-10-05 02:22 用户下达 UI/UX/Visual Motion 专项修复指令（14 项已确认问题 + §19 的 PMML 17 章节同步），问题表 = `docs\UI-CLOSURE-ISSUES-20261005.md`（PMML 偏离授权见其 :118，PMML-R15 路径）。**14 项全部 = FIXED**（Release 构建 0 error / 3 warning = 基线 `Views\PCMigSurface.xaml:53/54/56` WMC1506；Core 测试 468/468；真机已验 Step1 连接 `localhost` 发现 8 个共享 + 流程级提示已进左侧提示卡、表单内状态行为空）。PMML 按授权同步：规范正文新增**附录 A §19–§24 + 规则 R16–R31**、Audit 新增 Progress 族 UI Closure 更新节与 Token/样式增量表、Legacy 更新 L-07/G-04 并新增 L-17（33 处字面量圆角）/L-18（三个零引用圆角 Token）。会话交接 `docs\工作交接-20261005-UI-Closure.md`；交付报告 `docs\UI-CLOSURE-REPORT-20261005.md`；证据 `E:\PCMigLab\Evidence\Trust-Critical-Recovery\UI-CLOSURE-20261005\`（含像素测量与字体度量）。桌面复验包 `D:\Users\User\Desktop\新建文件夹 (4)\PCMig-UI-Closure-20261005\`。**改动全部未 commit / 未 push / 未 tag**；视觉终验（用户 §22 清单，运动类需连续帧或短视频）待人工。**会话级交接（做了什么 / 没做什么 / 文件坐标 / 下一步接手顺序）见 `docs\工作交接-20261005-UI-Closure-会话交接.md`；可直接粘贴给新会话的无缝衔接短卡见 `docs\工作交接-20261005-UI-Closure-QUICK.txt`。**
