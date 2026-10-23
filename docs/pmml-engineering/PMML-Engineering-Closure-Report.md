# PMML Engineering Closure Report

> **本报告 = PMML 工程技术档案本轮收口任务的最终交付说明。**
> 记录：已完成文档、追溯状态、未解决问题、风险说明、后续维护规则。

| 项目 | 值 |
| --- | --- |
| 执行令 | 「PMML Engineering Documentation 收口任务令 v1.0」 |
| 执行日期 | 2026-10-14 |
| 基线 commit | `b68613dc3600767e4e7cf2b24e413e5c91a16280` |
| 目标目录 | `docs/pmml-engineering/` |
| 业务代码改动 | **0** |
| Git 操作 | **无**（无 commit / 无 push / 无 tag / 无 release） |

---

## 一、状态声明

```
PMML Engineering Documentation: READY FOR MAINTENANCE
Source Traceability:            152/152 records processed
Business Code Changes:          0
Git:                            No commit / No push
v0.5.4 Preview.1:               NOT TESTED
```

以上五行是本轮收口的**正式状态结论**，后续会话可直接引用。

---

## 二、已完成文档列表

`docs/pmml-engineering/` 共 **6 份主文档 + 1 份数据 + 3 个辅助目录**。

### 2.1 六份主文档

| # | 文件 | 定位 | 核心内容 |
| --- | --- | --- | --- |
| 1 | `PMML-Engineering-Overview.md` | **体系入口** | PMML 定位与边界声明、三系统分层、文档地图、与既有四份旧文档的关系与**权威优先级**、按任务查表、当前状态摘要 |
| 2 | `PMML-Terminology-Registry.md` | **术语注册表** | 权威登记 PMML 总称 + 9 个 Feature Family，每个含 7 字段（Identifier / Full Name / 中文名称 / 技术职责 / 底层实现 / 关联文档 / 状态）+ 相邻概念登记 |
| 3 | `PMML-Implementation-Map.md` | **落点索引地图** | 152 条按 21 族组织，含裁定标记、状态标记、每族排查入口、进度视觉 R21–R36 速查、注意事项汇总 |
| 4 | `PMML-Source-Traceability-Methodology.md` | **方法论** | 数据来源、commit 基线、追溯模型、可信证据四元组、七种 Evidence Type、五种裁定、行号漂移口径、重建管道、维护规则 |
| 5 | `PMML-Evidence-Classification.md` | **证据分级** | Level A/B/C/D 定义与判定顺序、152 条等级分布、24 条 NO_PROBE 四分类逐条六字段表、措辞规范 |
| 6 | `PMML-Engineering-Closure-Report.md` | **收口报告** | 本文件 |

> **说明**：第 2 份 `PMML-Terminology-Registry.md` 由用户在执行确认中追加纳入本轮范围。
> 其定位经用户明确：**不是新增设计，不允许创造新术语，只做已有术语的权威登记。**

### 2.2 数据与辅助目录

| 路径 | 内容 |
| --- | --- |
| `PMML-Source-Traceability.csv` | **数据权威**：152 行 × 11 列 |
| `_recovered.json` | 重建中间产物（见 §三） |
| `history/` | `PMML-Source-Traceability.snapshot-2c0183b-v0.5.0-era.csv` + `README-快照说明.txt` |
| `diff/` | `差异报告.md` + `relocation.csv`（逐条裁定原始数据） |
| `_tools/` | 6 个 Python 脚本（重建 / 重定位 / 审计 / 判据核验 / 差异报告 / 锚点校验） |

---

## 三、`_recovered.json` 登记（按用户要求）

```
File:   docs/pmml-engineering/_recovered.json

Type:   Intermediate Recovery Artifact
Status: Not part of PMML Engineering Documentation
Reason: Generated during recovery/reconstruction process.
```

**处置：保持不动，不删除。**

保留理由：它是重建管道的**恢复链**——若将来 `PMML-Source-Traceability.csv` 需要第三次重建
（例如换基线 commit），`_recovered.json` 保留了从快照解析出的中间结构，
删除会使恢复过程无法逐步复现。

**使用纪律**：它**不是**权威数据。任何引用都必须回到 `PMML-Source-Traceability.csv`。

---

## 四、Source Traceability 状态

### 4.1 数据指标

| 指标 | 值 |
| --- | --- |
| 记录数（Rows） | **152** |
| 列数（Columns） | **11** |
| 字段错位行 | **0** |
| 编码 | UTF-8 无 BOM |
| 行尾 | LF（CRLF 0 / LF 153） |
| SHA256 | `D6A1B6566E29B47F88F039C8E0F446531684EC47359DE36ECDF6E2D6ED57E373` |
| Bytes | 51,675 |

### 4.2 列定义

`FeatureId` / `CanonicalEnglish` / `CanonicalChinese` / `Mechanism` / `SourcePath` /
`SymbolOrResourceKey` / `UsedBy` / `EvidenceType` / `ImplementationStatus` / `Commit` / `Note`

### 4.3 行号裁定分布

| 裁定 | 数量 | 含义 |
| --- | ---: | --- |
| `UNCHANGED` | **67** | 行号与基线完全一致 |
| `RELOCATED` | **29** | 同文件内行号漂移（**非错误**） |
| `LOCATED` | **29** | 原本无行号，本轮补全 |
| `MANUAL-ANCHOR` | **2** | 人工指定锚点（`DAM-002` / `PROG-001`） |
| `NO_PROBE` | **24** | 无可检索标识符（见 Evidence Classification） |
| `FILE_GONE` | **1** | 引用文件已删除（`PROG-009`） |
| **合计** | **152** | |

### 4.4 本轮 CSV 修改（严格限于授权范围）

按用户执行令，**只允许修改两个 Evidence Anchor**。实际 diff：

| 指标 | 值 |
| --- | --- |
| 行数变化 | 152 → 152（**UNCHANGED**） |
| 列数变化 | 11 → 11（**UNCHANGED**） |
| 被触及行 | **2 / 152** |
| 差异字段数 | **2** |
| 修改前 SHA256 | `4981175B0A9C6C290EA3D8994762BBA8CC7DCEC42311F5C8BDD734FCEECB8115` |
| 修改后 SHA256 | `D6A1B6566E29B47F88F039C8E0F446531684EC47359DE36ECDF6E2D6ED57E373` |

| FeatureId | 字段 | 旧值 | 新值 | 修因 |
| --- | --- | --- | --- | --- |
| `DAM-002` | `SourcePath` | `Presentation/BackdropSpike.cs:30` | `Presentation/BackdropSpike.cs:158` | `:30` 是类声明 `internal static class BackdropSpike`，非该条描述的 `Install` 装配入口；`:158` = `public static bool Install(Window window)` |
| `PROG-001` | `SourcePath` | `Presentation/ProgressPresentationCoordinator.cs:145` | `Presentation/ProgressPresentationCoordinator.cs:276` | `:145` 是同文件更早出现的 `ApplyTruth`；该条真正描述的是 `:276` 的 `private double ClampVisual()` 硬守卫 |

**未修改**：`FeatureId`、`EvidenceType`（= Evidence Level）、`ImplementationStatus`（= Status）、
`Commit`、其余 150 行 —— 全部保持原值。

**自校验**：锚点修正脚本内置守卫，覆盖后的锚点行**必须真的包含该记录的标识符**，
否则中止写入（避免再次出现 `DAM-002` / `PROG-001` 那类「首命中不精确」的错误）。

### 4.5 不可直接引用的记录

| 类别 | 条目 | 说明 |
| --- | --- | --- |
| 文件已删除 | `PROG-009` | `ProgressMotionDriver.cs`（818 行），2026-10-06 经用户授权删除，对应偏差登记册 **L-19 已 CLOSED** |
| 锚点失配 | `VERIFY-004` | 见 §五 · 未解决问题 1（**存在真实缺陷，本轮未修改**） |
| 无可检索标识符 | `NO_PROBE` 24 条 | 见 `PMML-Evidence-Classification.md` |
| 人工锚点 | `MANUAL-ANCHOR` 2 条 | `DAM-002` / `PROG-001`，已自校验通过 |

---

## 五、未解决问题

按严重程度排列。

### 5.1 未解决问题 1 — `VERIFY-004` 锚点失配（**本轮发现，未修改**）

| 项 | 内容 |
| --- | --- |
| 现象 | CSV 中 `VERIFY-004` 的 `SourcePath` 指向 `MainWindow.xaml.cs:1032`，但该行实际是 `SafeCompleteFooterAction(string phase, Exception ex)` —— 底栏兜底处理器，**与该条主题完全无关** |
| 连带 | 该条 `UsedBy` 字段仍写 `EnsureVerifyPanel` —— **该方法已不存在** |
| 正确锚点 | `MainWindow.xaml.cs:1190` `public void ShowVerificationPanel(FrameworkElement? anchor)` |
| 唯一调用点 | `Views/Step4ResultPage.xaml.cs:265` `shell.ShowVerificationPanel(ToolbarVerify);` |
| 互斥辅助 | `MainWindow.xaml.cs:1215` `private FrameworkElement OtherVisibleToolPanel(FrameworkElement self)` |
| 不变量状态 | **仍成立** —— 打开面板只走 `TogglePanel(VerificationProgressPanel, OtherVisibleToolPanel(...), realAnchor, onOpened: null)`，**不执行闸门、不触发 IO、不开始验证** |
| 语义更名 | 旧 `EnsureVerifyPanel` → 新 `ShowVerificationPanel(FrameworkElement?)` |
| 为何未修 | 执行令只授权修改 `DAM-002` / `PROG-001` 两个锚点；本条不在授权范围内 |
| 建议动作 | 下一轮单独授权后修正 `SourcePath` 与 `UsedBy` 两字段 |

**结论**：事实（不变量）正确，**锚点错误**。已记入 `PMML-Implementation-Map.md` §5.3 与本报告。

### 5.2 未解决问题 2 — 原 CSV 创建者仍未确认

- 已排除：本会话、四个只读 sweep 子代理、Git 历史任何分支任何提交、`git stash`。
- 可确证：内容素材**来自本轮四路 sweep**（含只有各路专属 sweep 才会产出的
  `ApplyStepCardHeight` 死代码、「Footer Isolation 不存在」、`PCMIG_HINTCARD_ANIMATION`、
  `RollScale`、`BOOT` 族十标记点）。
- 处置：已保留来源 commit (`2c0183b`) 快照与完整说明，**内容可用性不受影响**。

### 5.3 未解决问题 3 — 24 条 `NO_PROBE` 需人工回读

- 已四分类（`COMMENT_ONLY` 8 / `TOKEN_REFERENCE_ONLY` 4 / `BEHAVIOR_ASSERTION` 8 / `NEED_MANUAL_REVIEW` 4），
  每条带 `Required Future Action`。
- **不删除**，因为它们是设计意图或资源引用的**唯一线索**。
- 其中 4 条 `NEED_MANUAL_REVIEW` 优先级最高（`LMDS-007` / `TAOP-008` / `IP-006` / `TYP-001`）。

### 5.4 未解决问题 4 — `IP-006` `ReducedMotion` 绑定来源未确认

- 现象：`Controls/ImmersiveProgress/ImmersiveTransferProgress.xaml` 的 `ReducedMotion` 属性
  **未确认存在 XAML 绑定来源**。
- 影响：若无人设置，该属性恒为 `false` ⇒ Reduced Motion 实际未接线（与 **G-02** 相关）。
- 这是本档案体系中**优先级最高的技术未决项**。

### 5.5 未解决问题 5 — 未做全量「新增实现」自动差集

- 原 CSV 是**设计语言要素枚举**，不是全量源码清单；做全量差集会产出大量噪声。
- 本轮人工核对到的相关新增仅 3 项：`ShowVerificationPanel`、`OtherVisibleToolPanel`、
  `VerificationPanelWidth` 常量。
- 处置：不做自动差集，改为在维护规则中要求「新增实现时手工追加记录」。

### 5.6 未解决问题 6 — `PROG-007` / `IP-002` 映射分叉

- 现象：`JobPhase.Canceled` 在 Hero 侧映射 `Warning`（`Views/Step3ProgressPage.xaml.cs:383`），
  在 Footer Compact 侧映射 `Interrupted`（`MainWindow.xaml.cs:807`）。
- 影响：同一业务相位在两条进度条上呈现不同视觉状态，与 **R33**（同一控件族同一语义）存在张力。
- 处置：**不修**（属 UI 实现，本轮禁止修改 `src/`），登记为待裁定项。

---

## 六、风险说明

### R-1 —— 档案与源码可能再次漂移

行号是**文件内容的函数**，不是要素的属性。任何对 `Themes/` `Presentation/` `Views/` 的改动
都可能让 152 条中的若干条失效。本轮实测：一度有约 26% 的行号不可直接采信。

**缓解**：`Commit` 字段已固定基线；重建管道（`_tools/rebuild_traceability.py` +
`relocate_v6.py`）可复用；结论稳定性由 `sourcePath + symbolKey` 承载，**不依赖行号**。

### R-2 —— `STALE` 条目可能被误读为「实现有问题」

26 条 `STALE` 中绝大多数是**注释与实现不符**（实现本身正确），
例如 `DCST-004`（`Motion.xaml:14` 注释称淡出、实际 Opacity 恒 1）。

**缓解**：`PMML-Implementation-Map.md` §1.2 已明确「引用 `STALE` 条目时必须以实现为准」。

### R-3 —— 证据等级被越级引用

`NO_PROBE` 24 条与 `Comment Claim` 类记录**没有直接运行证据**。
若被当作「已实现事实」引用，会导致结论失真。

**缓解**：`PMML-Evidence-Classification.md` 已给出措辞规范
（Level C 禁止写「已实现」，只能写「设计意图记录」）。

### R-4 —— 用户否决过的设计可能被误认为现行方案

`OACT-012` 的 Origin Reveal 六 Token（`RevealScale 0.945` 等）**已被用户人工否决**为
「本质还是淡入弹窗」，当前只作 fallback。

**缓解**：该条状态已标 `FALLBACK ONLY`，并在 Implementation-Map 与 Terminology Registry 中注明。

### R-5 —— 未提交状态

本轮所有产物**全部未提交**（`docs/pmml-engineering/` 11+ 项未跟踪）。
工作树同时存在 v0.5.4 Preview.1 的动效修复改动（3 文件）。

**缓解**：用户已明确选择「暂不提交，保持工作树」；本报告不触发任何 Git 操作。

### R-6 —— `v0.5.4 Preview.1` NOT TESTED

本轮**未执行** v0.5.4 Preview.1 的任何测试。安装/升级/卸载链验证仍为
`PENDING REALWORLD`，G7 未关闭。

---

## 七、后续维护规则

### 规则 1 —— 数据权威只有一份

`PMML-Source-Traceability.csv` 是**唯一事实来源**。
其它五份文档都是它的**视图或说明**，不得反向修改它。

### 规则 2 —— 行号漂移不等于错误

行号变化一律记 `RELOCATED`，**不记为错误**。
判定真伪的锚点是 `SourcePath + SymbolOrResourceKey`，不是行号。

### 规则 3 —— 增删实现时必须同步档案

新增或删除 UI 实现要素时，必须同步追加/更新 CSV 记录，并至少更新：
`PMML-Implementation-Map.md`（族内落点）与 `PMML-Evidence-Classification.md`（等级）。

### 规则 4 —— 重建而非修补

当出现大批量漂移时，**重建**（复用 `_tools/rebuild_traceability.py` + `relocate_v6.py`），
不要逐条手工修补。

> **踩坑留档**：单独跑一个只改锚点的脚本、随后又跑全量重定位脚本，会把修正**覆盖掉**。
> 锚点修正必须做成重定位脚本内的**权威覆盖表**（`ANCHOR_OVERRIDES`），并带自校验。

### 规则 5 —— 修正锚点必须带自校验

任何人工指定的锚点，都必须验证「该行真的包含该记录的标识符」，否则中止写入。
这是防止 `DAM-002` / `PROG-001` 那类「首命中不精确」错误复发的手段。

### 规则 6 —— 证据等级不得越级

禁止把 Level C（注释/设计意图）或 Level D（未验证）描述为已实现事实。
`NO_PROBE` **不是错误**，它表示「有引用线索、缺直接运行证据」。

### 规则 7 —— 与既有旧文档的权威优先级

```
源码实现
  > docs/PMML-UI修改硬性规范.md （R1–R36 唯一权威清单）
  > docs/PCMig-Visual-Motion-Language.md （PMML v1.0 FROZEN）
  > docs/pmml-engineering/ （本档案体系）
  > docs/PMML-Implementation-Audit.md （历史审计）
  > docs/PMML-Legacy-Deviations.md （偏差与缺口登记）
```

冲突时按此顺序取信。**源码永远是最终裁决者。**

### 规则 8 —— 改动 UI 前的工作流

1. 查 `PMML-Implementation-Map.md` 找落点与现状
2. 查 `PMML-Evidence-Classification.md` 判断该条证据强度
3. 查 `docs/PMML-Legacy-Deviations.md` 看是否属已登记偏差
4. 按 `docs/PMML-UI修改硬性规范.md` §四 走 PMML Compliance Gate
5. 改完更新 CSV 与受影响的视图文档

### 规则 9 —— 缓存与快照不动

`history/` 与 `_recovered.json` 是**恢复链**，只增不删。
新增重建时，旧快照移入 `history/` 并标注来源 commit。

---

## 八、本轮边界遵守情况

| 执行令约束 | 遵守情况 |
| --- | --- |
| 创建 `docs/pmml-engineering/` 文档文件 | ✅ 6 份主文档 |
| 修改 PMML 文档目录内未提交文件 | ✅ 仅 `PMML-Source-Traceability.csv`（2 行 2 字段） |
| 修正文档性问题 | ✅ 已执行（见 §4.4） |
| **禁止**修改 `src/` | ✅ 未改动 |
| **禁止**修改 `tests/` | ✅ 未改动 |
| **禁止**修改业务逻辑 | ✅ 未改动 |
| **禁止**修改 UI 实现 | ✅ 未改动 |
| **禁止**执行 v0.5.4 Preview.1 测试 | ✅ 未执行 |
| **禁止** Git commit | ✅ 无 |
| **禁止** Git push | ✅ 无 |
| **禁止** tag/release | ✅ 无 |

**业务代码改动 = 0。**

---

## 九、已知文档瑕疵（本体系自身，诚实登记）

| 项 | 说明 |
| --- | --- |
| `VERIFY-004` 锚点 | 见 §5.1，事实正确、锚点错误 |
| 24 条 `NO_PROBE` | 无自动可检索标识符，需人工回读 |
| 原 CSV 创建者 | 未确认（内容可用性不受影响） |
| 无全量新增实现差集 | 见 §5.5，按维护规则手工追加 |
| `PROG-007` / `IP-002` 分叉 | 见 §5.6，待裁定 |

---

## 十、状态声明（最终）

```
PMML Engineering Documentation: READY FOR MAINTENANCE
Source Traceability:            152/152 records processed
Business Code Changes:          0
Git:                            No commit / No push
v0.5.4 Preview.1:               NOT TESTED
```

---

## 十一、关联文档

| 文档 | 定位 |
| --- | --- |
| `PMML-Engineering-Overview.md` | 体系入口 |
| `PMML-Terminology-Registry.md` | 术语注册表 |
| `PMML-Implementation-Map.md` | 落点索引地图 |
| `PMML-Source-Traceability-Methodology.md` | 裁定口径与维护规则 |
| `PMML-Evidence-Classification.md` | 证据分级 |
| `PMML-Source-Traceability.csv` | 数据权威 |
| `diff/差异报告.md` | 重建差异原始报告 |
| `history/README-快照说明.txt` | 快照来源与已知缺陷 |
