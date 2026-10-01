# 工作交接 — 20261001 — Workspace 治理完成与 D6.2 起点

> **本文档是本项目当前唯一的 Current Handover。**
> 它取代 `docs\handover\history\工作交接-20261001-D6.1诊断收口与全量验收.md`（已归档，只作追溯，不再作为当前状态入口）。
>
> - 新增日期：2026-10-01（Phase 2 — Workspace Cleanup & Governance 收口）
> - 权威工作区：`E:\Project\deepseek work\PCMig`
> - 阅读顺序：`..\AGENTS.md` → `docs\发版铁律.md` → `docs\INDEX.md` → 本文档
> - 交付区 `E:\Project\PCMig`、工作副本 `D:\PCMig`、旧镜像 `I:\K\deepseek work` **本轮一字未碰**

---

## 〇 一页速览

| 项目 | 当前值 |
|---|---|
| 当前阶段 | **Workspace Governance: COMPLETE**（2026-10-01） |
| PMML | **v1.0 — FROZEN**（2026-09-30 宣布；10-01 仅做文档状态纠正） |
| Diagnostics | **D6.1 COMPLETE**；D6.2 **NOT STARTED** |
| Stage B | **NOT AUTHORIZED**（未授权，不得开始） |
| 发布线 | v0.4.9（`installer\pcmig.iss` 与 Cli/Core/Gui csproj 均为 `0.4.9`）；**本轮未发版** |
| 开发线 | v0.5.0 WinUI（未发版） |
| 测试基线 | **Core 290 / Diagnostics 286 = 576 通过 / 0 失败** |
| Build | sln **0 error / 4 warning**（既有：xUnit2031 ×1 + WMC1506 ×3，无新增） |
| Solution | `PCMig.sln` 现含 **8 个工程**（已补齐 WinUI 与 Diagnostics） |
| Git | checkpoint `2c0183b` → solution `7ba2bc1` → governance `c5e668c` → 最终交接 `73fcb05`（详见 §二） |
| tracked | 100 → **390**（checkpoint）→ **391**（governance）→ **393**（最终交接） |
| docs 顶层 `.md` | 83 → **29** |
| 当前交接 | **本文档**（`docs\工作交接-20261001-Workspace治理完成与D6.2起点.md`） |
| 唯一文档入口 | `docs\INDEX.md` |
| 下一任务 | **D6.2 Real World Validation**（需用户明确触发语才能开始） |

---

## 一 本轮（Phase 2）做了什么

执行顺序严格按用户裁决 D-4（`G0 → G1 → M1 → M2 → M3 → M4 → S1 → G2 → 完整 Build/Test → C1/C2/C3 → 最终交接`）。

| 阶段 | 做了什么 | 结果 |
|---|---|---|
| **G0** | 治理前增量安全封存（Modified + Untracked，保持仓库相对路径，排除 bin/obj/.vs/dist/DSH 数据） | `archive\backup-pre-governance-20261001-133659\`，**341 文件 / 5,814,770 B**；逐文件 SHA256 **336/336 MATCH，0 MISMATCH，0 MISSING** |
| **G0 基线** | 治理前跑一次真实基线 | `dotnet build PCMig.sln` → 0 error / 1 warning；`dotnet build src\PCMig.WinUI` → 0 error / 3 warning；`dotnet test PCMig.sln` → **576 / 0** |
| **G1** | 建立真实 Git Checkpoint（显式 allowlist，禁 `git add -A`/`clean`/`reset --hard`/`checkout .`/`restore .`） | 提交 `2c0183b` —— `336 files changed, 70,992 insertions(+), 1,327 deletions(-)`；tracked 100 → 390；密钥扫描 staged 全量 75,946 行 **命中 0** |
| **M1** | 建立 docs 唯一入口 | 新建 `docs\INDEX.md`（13,139 B，11 章）；46 处路径引用校验 BAD_REFS=0 |
| **M2** | 历史交接归档 | 新建 `docs\handover\history\`，移入 **41 篇**（38 篇 `工作交接-*.md` + 3 篇 `工作交接说明_v0.4.{1,5,6}.md`）；docs 顶层 `.md` 84 → 43；41 条 rename **全部 R100** |
| **M3** | 历史 / QA / 已取代文档治理（人类总结留 docs，机器证据留 archive） | 新建 `docs\qa\history\` 移入 **7 篇** QA 历史；新建 `docs\archive\` 移入 **7 项**历史交付/审计/旧清单 |
| **M4** | P-G6 合并 + P-G9 文档状态债 | `docs\公司域环境验证清单.md` 两块独有内容（IT 申请话术、安全团队评审要点）合并进 `docs\First-Day-Company-Test-Checklist.md` §八/§九后归档；PMML 页眉 `FREEZE CANDIDATE` → `PMML v1.0 — FROZEN`；诊断架构方案页眉 `DRAFT / 未获实施授权` → `APPROVED / IMPLEMENTED THROUGH D6.1 — D6.2 VALIDATION PENDING` |
| **S1** | 修 `PCMig.sln` 治理缺口（P-G1） | 补入 `PCMig.WinUI`、`PCMig.Diagnostics`，现 **8 工程**；未改任何 csproj 业务属性 |
| **G2** | Workspace Governance Commit | `7ba2bc1`（sln membership）+ `c5e668c`（governance，69 条：A 1 / M 12 / R 56，+299 / −41）+ `73fcb05`（最终交接与第二轮报告：4 files changed, +480 / −6，含 1 条 R100 归档 rename） |
| **完整验证** | 结构修改后完整 Build/Test | sln **0 error / 4 warning**（既有）；**576 / 0** |
| **C1/C2/C3** | 最后清可重建产物 | 释放 **1,174,088,631 B ≈ 1.09 GiB**（明细见 §五） |

---

## 二 四个治理 Commit 与 Git 现状

```
73fcb05  docs: add governance completion handover and second-round report (2026-10-01 13:47:29 +0800)
c5e668c  chore: govern workspace and documentation layout      (2026-10-01 13:45:12 +0800)
7ba2bc1  chore: complete PCMig solution membership             (2026-10-01 13:45:11 +0800)
2c0183b  checkpoint: preserve v0.5.0 PMML frozen and Diagnostics D6.1  (2026-10-01 13:39:12 +0800)
c9aef30  v0.5.0 WinUI technical baseline before visual reconstruction   ← 治理前的旧 HEAD
```

- 完整 hash：checkpoint `2c0183beebcd6b06d3c1c2e903204a739f1bc104`；solution `7ba2bc19bbd42229be798b3ce43087cde459bc31`；governance `c5e668cda016801427b329fd3e45ec21205fd952`；最终交接 `73fcb05c005a10d80f6f6e486f58f28089fcbe5a`。

> 上表为**本轮治理的功能提交**。其后一次纯文档同步提交（"docs: sync commit hashes"）仅修正本报告与交接文档中的 hash 引用，不含任何内容变更。
- 分支：`feature/winui-v0.5.0`；仓库根 = `E:\Project\deepseek work\PCMig` 本身。
- `2c0183b` 的含义：**"Workspace Cleanup 开始之前，真实、可恢复的 PCMig 当前产品状态"**——PMML v1.0、Diagnostics D1–D6.1、当前 WinUI/Core、当前测试、当前正式文档全部进入 Git。
- 治理后 `git status --porcelain` 仅剩 `?? archive/`：`archive/`（备份、证据、截图、历史安装包、探针工程）**有意不纳管**，按用户明令"不要为了全部纳管把数百 MB raw evidence 强塞进 Git"。
- `core.autocrlf=true`（无 `.gitattributes`）⇒ 索引内按 LF 存储，工作区文件字节未变。

---

## 三 Solution 与 Build/Test 基线

`PCMig.sln` 现含 8 个工程（修复前只有 6 个，`dotnet build PCMig.sln` 不等于构建全产品）：

| 工程 | 框架 / 关键属性 |
|---|---|
| `src\PCMig.Core\PCMig.Core.csproj` | net8.0 |
| `src\PCMig.Cli\PCMig.Cli.csproj` | net8.0 |
| `src\PCMig.Gui\PCMig.Gui.csproj` | net8.0-windows |
| `src\PCMig.Diagnostics.Abstractions\…csproj` | net8.0；**契约层：本文件不得出现 ProjectReference / PackageReference** |
| `src\PCMig.Diagnostics\…csproj` | net8.0（**S1 新纳入 sln**） |
| `src\PCMig.WinUI\PCMig.WinUI.csproj` | net8.0-windows10.0.19041.0；`UseWinUI`；`WindowsAppSDKSelfContained`；`<Platforms>x64</Platforms>`（**S1 新纳入 sln，配置映射为 `Debug|x64`/`Release|x64`**） |
| `tests\PCMig.Core.Tests\…csproj` | net8.0 |
| `tests\PCMig.Diagnostics.Tests\…csproj` | net8.0 |

ProjectReference 图：`Core → Diagnostics.Abstractions`；`Cli → Core`；`Gui → Core`；`Diagnostics → Diagnostics.Abstractions`；`WinUI → Core + Diagnostics`；`Core.Tests → Core`；`Diagnostics.Tests → Diagnostics.Abstractions + Diagnostics + Core`。

**基线（结构修改后实测，用户 §十六 要求的最终验证）**

| 命令 | 结果 |
|---|---|
| `dotnet build PCMig.sln -c Release --no-incremental` | 退出 0；**0 error / 4 warning**（`A5PresentationRegressionTests.cs:1634` xUnit2031 ×1 + `PCMigSurface.xaml:53,54,56` WMC1506 ×3）——全部是既有 warning，无新增 |
| `dotnet test PCMig.sln -c Release` | 退出 0；**Core 290 / 0 失败，Diagnostics 286 / 0 失败，合计 576 / 0** |

日志：`E:\Project\deepseek work\archive\pcmig-governance-20261001\s1-build.log`、`s1-test.log`。

---

## 四 docs 信息架构（唯一入口 = `docs\INDEX.md`）

权威顺序：`..\AGENTS.md` → `PCMig\AGENTS.md` → `docs\发版铁律.md` → `docs\INDEX.md` → 当前工作交接 → 当前任务对应文档。

```
docs\
  INDEX.md                      ← 唯一文档入口（唯一 Current Handover 指针）
  发版铁律.md  发布流程.md  稳定性守则.md  稳定性验收标准.md
  更新日志.md  更新日志.txt  使用说明.txt  测试报告-公司环境.md
  首日实测检查表.md  First-Day-Company-Test-Checklist.md  README 类
  PMML 四篇（Visual-Motion-Language / UI修改硬性规范 / Implementation-Audit / Legacy-Deviations）
  A5-节流与竞态修复设计.md  方案-诊断中心与自诊断架构.md  方案-20260928-业务接线与可信度修复.md
  诊断系统实施-阶段证据.md / -事件覆盖矩阵.md / -配置项接线审计.md / -自检报告-20261001.md / -D6.1-进度.md
  诊断系统实施-基线指纹-D1.txt（D1 基线证据，保留原位并由 INDEX §四 登记）
  handover\history\   （41 篇历史交接，只作追溯，不能覆盖当前规则）
  qa\history\         （7 篇历史 QA）
  archive\            （8 项历史交付/审计/已取代清单）
  screenshots\  ui\v0.5-reference\  工作区治理\
```

- 顶层 `.md`：**83 → 29**（移出 41 篇交接 + 7 篇 QA + 7 项历史，新增 `INDEX.md`）。
- **机器生成、禁止手工编辑**：`docs\诊断系统实施-事件覆盖矩阵.md`（由 `CoverageMatrixTests` 重算）、`docs\诊断系统实施-配置项接线审计.md`（由 `OptionsWiringAuditTests` 重算）。
- **物理位置 ≠ 信息架构**：凡被 `AGENTS.md` / `README.md` / Contract Test / `release.ps1` / csproj / installer / 源码注释以裸路径引用的文档，一律保持原位，导航由 INDEX 承担。

---

## 五 移动 / 删除 / 保护

### 5.1 移动（全部为 rename，历史正文一字未改）

| 目标 | 数量 | 内容 |
|---|---|---|
| `docs\handover\history\` | 41 | 38 篇 `工作交接-*.md` + 3 篇 `工作交接说明_v0.4.{1,5,6}.md` |
| `docs\qa\history\` | 7 | `Coverage-Matrix-V046.md`、`Test-Execution-Report-V046.md`、`Test-Execution-Report-V046-AddendumA.md`、`Private-Test-Lab-Blueprint.md`、`Private-Test-Lab-Final-Report.md`、`Corporate-Simulation-Blueprint.md`、`Step1视觉验收结论-V12-20260926.md` |
| `docs\archive\` | 8 | `公司域环境验证清单.md`（已取代）、`FinalPolish-{完成度审计,标注项独立复核,用户指令原文,本轮交付报告,Responsive扩展说明}`、`阶段A-交付说明-20260929.md`、`现场验证清单-v0.3.8.txt` |

链接修正共 **15 处 / 9 文件**（仅改路径与引用，不改历史正文；BOM 状态逐文件保持）：`docs\更新日志.md`×3、`docs\更新日志.txt`×3、`docs\测试报告-公司环境.md`×2、`src\PCMig.WinUI\Views\StepNavigationControl.xaml`×1、`tests\PCMig.Core.Tests\WinUiDpiContractTests.cs`×1、`docs\技术备忘-20260927-视频验收阻塞重评估与内置H264编码器.md`×1、`docs\FinalPolish-完成度审计-20260928.md`×1、`docs\FinalPolish-本轮交付报告-20260928.md`×2、`docs\FinalPolish-标注项独立复核-20260927.md`×1；另有 M3 批次 12 处相对引用修正（`docs\qa\history\` 内互引）。

**允许保留的历史字面值**：历史交接正文里作为"当时路径记录"出现的旧路径、以及治理前快照 `docs\工作区治理\第一轮-Workspace-Inventory-20261001.md` 内的旧路径记录，一律不改写。

### 5.2 删除（仅 C-1/C-2/C-3 明确批准范围，共释放 **1,174,088,631 B ≈ 1.09 GiB**）

| 类别 | 删除内容 | 释放 |
|---|---|---|
| **C-1** | `dist\app`、`dist\cli`、`dist\gui`、`dist\publish-cli`、`dist\publish-gui` | 331,454,431 B |
| **C-2** | 16 个 `bin`/`obj`（src 6 工程 + tests 2 工程）与 `.vs\`（含 `a5-model-dump.log`、`poc-backdrop.log` 等构建/调试产物） | 841,486,665 B |
| **C-3** | `archive\scripts\diagnostics-audit-20261001\{bin,obj}`（保留 `AuditProbe.csproj`、`Program.cs`、`ComponentAudit.cs`、`PackageAudit.cs` 等探针源文件） | 1,147,535 B |

删除用脚本带三重守卫（绝对路径必须在仓库内、非 C-3 不得位于 `archive\`、目录名必须命中白名单），逐项复验"删除后不存在"，`GUARDS_OK=True`。

### 5.3 保护（本轮未动，且不得删除）

- `archive\backup-*`、`archive\evidence\*`、`archive\screenshots\*`、`archive\pmm-l-audit*`、所有 baseline / 验收证据 / SHA256 / 正式测试日志。
- `dist\PCMigSetup-*.exe` —— **实测 49 个历史安装包全部保留**（4,692,358,997 B，v0.1.0 → v0.4.9；`release.ps1:180` 注释载明"历史安装包一律保留，全量存档在 dist\"）；同目录 `gui-extracted.manifest`（847 B）亦保留。
- `archive\evidence\diagnostics-*\…` 下约 170 个空目录 —— 是证据结构，**不是垃圾**。
- 本轮新增的治理封存：`archive\backup-pre-governance-20261001-133659\`（341 文件，含 `manifest.csv`、`SHA256.txt`、`git-status-before.txt`、`git-diff-before.patch`、`untracked-project-files-before.txt`）。

---

## 六 未做与遗留问题

1. **D6.2 Real World Validation 未开始；Stage B 未授权；三 VM 未使用；未做 210 万文件规模测试。**
2. **Diagnostics 事件口径遗留（承接 D6.1）**：23 个事件仍 `Reserved`；`DIA.SerializationFailed` 只有计数器没有事件；`FS.FileReadFailure` 口径缺失；Preflight 用中文检查名派生 code；1 条偶发未复现测试（`ActiveSegmentIsSealedAtTheCutoffSoTheFreshestEventsAreIncluded`，约 10 次整包运行出现 1 次，根因未确认）。
3. **Deep Trace 端到端**：真实窗口 / 高 DPI / 面板动效逐帧未验；**性能数字仍全是候选值**。
4. **P-G7（未修，属 Release Governance）**：`docs\首日实测检查表.md` 仍是旧口径（内容停留在 v0.2 期），本轮按用户明令"只登记、不修改"。
5. **P-G8（未修，属 Release Governance）**：`tools\release.ps1` 交付复制清单问题，本轮未动 `release.ps1` 一个字节。
6. **`AGENTS.md` 未更新（决策保留）**：其 `:10` 仍写 v0.4.8（现发布线 0.4.9），且未提及 `docs\INDEX.md`。用户本轮只授权"文档状态纠正"到 PMML 与诊断架构两篇；`AGENTS.md` 属最高规则文件，**未经明示授权不得改**。建议后续在 Release Governance Fix 中一并处理。
7. **Harness / DSH 未清理**：`E:\Project\deepseek work\` 下的 174 个散落文件、12 个重复目录、`UserData`、`.dsh*`、`.openviking`、`dsh-session-recovery`、`.preset-square` 等**本轮完全未碰**（用户 D-5 裁决），另开 "Harness / DSH Workspace Cleanup" 处理。
8. **工作树非治理性脏项**：`PCMig\archive\` 有意留在 Git 之外（2238 个未跟踪文件全部位于 `archive\` 下）。
9. **Release 治理债（P-G7/P-G8）与 `AGENTS.md` 版本口径**未闭环 —— 见第 4/5/6 条。
10. **Mnemon 长期记忆写入未完成（环境阻塞，非本轮遗漏）**：用户 §二十二 要求把当前状态写入 Mnemon。实测 `mnemon_status` → `healthy: true` 但 `commandFound: false`、Memory Spaces `total: 0`；`mnemon_runtime_memory` 写入被拒（`runtime memory archival requires an existing active writable Memory Space ... catalog=0, authorized=0, writable=0`），`mnemon_memory_body_create` 亦失败（`spawn mnemon ENOENT`，PATH 与默认安装目录均无 `mnemon.exe`）。**解锁动作**：安装官方 Mnemon Windows 版或设置 `MNEMON_CLI_PATH` 后重试写入；在此之前的持久记录 = 本交接文档 + `docs\INDEX.md`（内容已覆盖 §二十二 要求的全部事实）。

---

## 七 回退与安全

- **回退路径**：`git stash`（未提交改动）/ `git revert <hash>`（已提交改动）。
- **严禁**：`git reset --hard`、`git checkout .`、`git restore .`、`git clean -fd`。
- 结构变更全部是 rename + 索引 + 文档 metadata + solution membership；代码业务逻辑零改动、PMML 正文零改动、Diagnostics 行为零改动。
- 若不满意本轮治理，可 `git revert 73fcb05 c5e668c 7ba2bc1`（checkpoint `2c0183b` 保留全部工作成果，不会丢失）。
- 本轮**未发版**、**未跑 `tools\release.ps1`**、**未改 tools 三脚本**、**未执行任何破坏性 git 命令**。
- 交付区 `E:\Project\PCMig`、工作副本 `D:\PCMig`、镜像 `I:\K\deepseek work` 本轮完全未动。

---

## 八 下一步：D6.2 起点

**Next: D6.2 REAL WORLD VALIDATION** —— 但**必须等用户亲口说出触发语**（如"开始执行D6.2任务"）后才可开始；本会话不得自行进入。

D6.2 的既有事实（承接 D6.1 交接，见 `docs\handover\history\工作交接-20261001-D6.1诊断收口与全量验收.md`）：
- 需要真实环境/授权的验证项：Deep Trace 端到端（真实窗口 / 高 DPI / 面板动效逐帧）、性能实测（把候选值换成实测值）、Stage B、三 VM 故障注入、210 万文件规模。
- 需要先补的事件口径：23 个 `Reserved` 事件、`DIA.SerializationFailed` 事件化、`FS.FileReadFailure` 口径、Preflight code 派生规则。
- 未决问题清单 P-1…P-15 见 D6.1 交接 §十一。

**干净起点已就绪**：Git 可恢复（393 tracked / 4 commits）、Solution 代表完整产品（8 工程）、docs 有唯一入口、顶层不再被 39 篇交接淹没、历史与证据零损失、构建垃圾已清、历史安装包全保留。

---

## 九 接手须知

1. 先读：`..\AGENTS.md`（最高规则）→ `PCMig\AGENTS.md` → `docs\发版铁律.md` → `docs\INDEX.md` → 本文档。**改 UI 前必读 PMML 四篇**，否则违反 AGENTS.md 三点六。
2. 当前交接就是本文档；`docs\handover\history\` 里的 41 篇**只作追溯**，不能覆盖当前规则。
3. 两条路径硬约束（移动即断）：Contract Test / 源码注释 / csproj 内嵌 / `release.ps1` / `installer\pcmig.iss` 引用的文档路径都不可随意改名或移动；改动前先跑 INDEX §九 的"移动即断"硬路径总表核对。
4. 机器生成文档禁手改：`诊断系统实施-事件覆盖矩阵.md`、`诊断系统实施-配置项接线审计.md`。
5. 证据与日志：`E:\Project\deepseek work\archive\pcmig-governance-20261001\`（本轮）、`archive\pcmig-inventory-20261001\`（第一轮盘点）、`archive\backup-pre-governance-20261001-133659\`（治理前封存）。
6. 发版前必读 `docs\发版铁律.md`；发版必须走 `tools\release.ps1`，一个版本号只发一次。
7. **不要**为了"看起来整齐"移动文档；信息架构由 `docs\INDEX.md` 表达。

---

## 十 铁律核对（本会话自证）

| 铁律 | 实测 |
|---|---|
| 交接文档只增不覆 | ✅ 本文档为**新增**；历史交接 41 篇一字未改（仅移动 + 外部链接修正） |
| 记录做了什么 / 没做什么 | ✅ §一 / §六 |
| 记录证据 | ✅ §二（commit hash）、§三（Build/Test 日志路径）、§五（备份与 SHA256 336/336） |
| 记录回退 | ✅ §七（含禁止的破坏性 git 命令） |
| 记录限制与下一步 | ✅ §六 / §八 / §九 |
| 凭据不写明文 | ✅ staged diff 全量密钥扫描命中 0（14 处宽匹配全部是"关于口令的说明文字"，非真实凭据） |
| 未发版 / 未改发版脚本 | ✅ |
| 未执行破坏性 git 命令 | ✅ 全程 `git add`（显式路径）/ `commit` / `status` / `diff` / `log` / `ls-files` / `grep` |
| 冻结范围代码未改 | ✅ 无任何 ViewModel / Command / 绑定 / 迁移或 Robocopy 逻辑 / 状态机 / 错误处理 / 网络检测改动 |
| 破坏性动作前报路径 | ✅ 见 §五 5.2，删除脚本带三重守卫并逐项复验 |
