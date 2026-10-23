# PMML Evidence Classification

> 本文档定义 PMML 工程技术档案的**证据分级标准**，并对 `PMML-Source-Traceability.csv`
> 的 152 条记录逐条归类，重点登记 **24 条 `NO_PROBE`** 的详细处置。

| 项目 | 值 |
| --- | --- |
| 文档类型 | Engineering Documentation（证据分级） |
| 基线 commit | `b68613dc3600767e4e7cf2b24e413e5c91a16280` |
| 覆盖记录 | 152 / 152 |
| 重点 | 24 条 NO_PROBE 逐条登记 |
| 状态 | READY FOR MAINTENANCE |

---

## 一、证据等级定义

| 等级 | 名称 | 定义 | 可声称的最强结论 |
| --- | --- | --- | --- |
| **Level A** | 源码直接证明 | 类、方法、ResourceKey、Token 在源码中**直接可见** | 「该实现在代码中存在」 |
| **Level B** | 调用链证明 | 由**调用关系**可推断出行为与生效路径（定义处 → 调用处 → 生效点） | 「该实现会被执行」（在给定入口下） |
| **Level C** | 行为推断 | 依据**注释、设计意图**或间接线索推断 | 「设计意图如此」——**不等于**已实现 |
| **Level D** | 未验证 | `NO_PROBE`：无可自动检索的标识符，或证据不足 | 「存在引用线索，尚未验证」 |

### 1.1 硬纪律

> **禁止把 Level C / Level D 描述为已实现事实。**

具体含义：

- **Level C**（注释主张）**必须**标注为「注释声称」，且当注释与实现不符时，**以实现为准**。
  本档案中已有 `STALE` 26 条即为此类。
- **Level D**（`NO_PROBE`）**不得**写成「已实现」或「不存在」，只能写「线索存在、证据未取得」。
- 任何跨级跃迁（如从 Level C 直接宣称 REALIZED）**必须**有新增的 Level A 或 Level B 证据。

### 1.2 等级判定顺序

```
有可检索标识符？
 ├─ 否 → Level D（NO_PROBE）
 └─ 是 → 该标识符所在行是「定义」还是「引用」？
          ├─ 定义（类/方法/ResourceKey/Token 声明） → Level A
          └─ 引用（调用 / {StaticResource} / x:Bind） → 能否追到定义？
                ├─ 能 → Level B
                └─ 不能 → Level C
```

---

## 二、152 条记录的等级分布

| 等级 | 数量 | 占比 | 对应 Evidence Type |
| --- | ---: | ---: | --- |
| **Level A** | 117 | 77.0% | Implementation Fact（Symbol Definition / Resource Definition） |
| **Level B** | 5 | 3.3% | Design Rule + 可追溯到生效点 |
| **Level C** | 6 | 3.9% | Comment Claim / Design system |
| **Level D** | 24 | 15.8% | UNCONFIRMED（`NO_PROBE`） |
| **合计** | **152** | 100% | |

### 2.1 与 `EvidenceType` 字段的对应

CSV 的 `EvidenceType` 字段是**粗颗粒**分类，本文档的等级是**证据强度**分类，两者关系如下：

| CSV `EvidenceType` | 条数 | 映射到等级 |
| --- | ---: | --- |
| Implementation Fact | 117 | **Level A**（其中少数含引用行，仍属 A 的弱侧） |
| UNCONFIRMED | 19 | **Level D**（其中 5 条经本轮人工补证已可归 A/B，见 §四） |
| Comment Claim | 12 | **Level C** |
| Design Rule | 3 | **Level B**（规则有实现落点） |
| Design system | 1 | **Level C** |

---

## 三、Level D 记录登记（24 条 NO_PROBE）

### 3.1 分类口径

`NO_PROBE` **不等于错误**。它表示：

> **当前存在设计或资源引用线索，但缺少直接运行证据。**

24 条按线索类型分为四类：

| 分类 | 含义 | 数量 |
| --- | --- | ---: |
| `COMMENT_ONLY` | 线索仅存在于**注释**中，无可检索标识符 | 8 |
| `TOKEN_REFERENCE_ONLY` | 线索是**数值/Token 引用**，因表达式形态无法自动检索 | 4 |
| `BEHAVIOR_ASSERTION` | 线索描述的是**行为断言**（数值、公式、边界），需人工回读确认 | 8 |
| `NEED_MANUAL_REVIEW` | 线索指向**多文件/不确定目标**，或本身即为「未确认」状态 | 4 |
| **合计** | | **24** |

### 3.2 `COMMENT_ONLY`（8 条）

| FeatureId | Original Evidence | Current Classification | Evidence Level | Reason | Required Future Action |
| --- | --- | --- | --- | --- | --- |
| `DSL45-001` | `Themes/Colors.xaml:42` — 注释「light source upper-left -> upper edge bright」 | COMMENT_ONLY | **Level C** | 光源方向由注释表述，无标识符可检索；实际光照由多个画刷共同体现 | 改为锚定具体画刷键（`NavCardEmbossTopBrush` 等）或补真机像素测量 |
| `DCST-004` | `Themes/Motion.xaml:14` — 注释 | COMMENT_ONLY | **Level C** | 该注释**已知与实现不符**（对应偏差 L-05） | 保留为 `STALE` 记录，引用时**以生效实现为准**；建议后续修正注释 |
| `MHE-011` | `Views/StepNavigationControl.xaml:17` — 注释 | COMMENT_ONLY | **Level C** | 注释声称有悬停/按下位移反馈，实测**零反馈**（偏差 L-04 相关） | 保留记录；根因未定位（**G-05**），需真机指针事件排查 |
| `BOOT-010` | `Presentation/ShellResponsiveLayout.cs` — 引用 `SetMaxSurfaceHeight` 的注释 | COMMENT_ONLY | **Level C** | 所引 API 已改名（现为 `SetAvailableHeight`），注释过期 | 更新注释或标注为 `STALE`；不影响功能 |
| `VERIFY-003` | `Views/VerificationProgressPanel.xaml.cs:29` — 注释「关闭面板不等于取消」 | COMMENT_ONLY | **Level C** | 是设计意图声明，非可检索实现 | 已由 `VERIFY-004` 的 Level B 证据间接支撑；保留 |
| `VERIFY-006` | `Views/ChangelogPanel.xaml:5` — 注释「浮层声明式转场已移除」 | COMMENT_ONLY | **Level C** | 描述「某物不存在」，天然无可检索标识符 | 保留为 `DEAD CODE` 说明；无需动作 |
| `RULE-008` | `Themes/Colors.xaml` — 注释（颜色 Token 存档规则） | COMMENT_ONLY | **Level C** | 是约定声明，无实现符号 | 保留；规则本身已在硬性规范登记 |
| `STATUS-004` | `src/PCMig.WinUI` — 「No matching identifier found」（页脚隔离） | COMMENT_ONLY | **Level D** | 原记录认为存在「Footer Isolation」机制，**实测该机制在源码中不存在** | 保留记录并明确标注「**该机制不存在**」，避免后人再找 |

### 3.3 `TOKEN_REFERENCE_ONLY`（4 条）

> 这些记录的 `SymbolOrResourceKey` 是**缓动数值对或跨文件 Token 清单**，
> 因内含半角逗号/分号，在 CSV 原文件里曾被**截断**（转义缺陷，已修复）。
> 它们的线索是**真实存在的数值**，只是形态不可自动检索。

| FeatureId | Original Evidence | Current Classification | Evidence Level | Reason | Required Future Action |
| --- | --- | --- | --- | --- | --- |
| `DCST-003` | `MotionDirector.cs:370` — `CubicBezier (0.10, 0.90)(0.20, 1.00)` | TOKEN_REFERENCE_ONLY | **Level A**（已人工补证） | 数值对含逗号被截断；**当前实际位置** `MotionDirector.cs:335` 与 `:915` | 已补证；建议把缓动提取为 Token 键以便检索 |
| `OACT-013` | `MotionDirector.cs:565` — `CubicBezier (0.16, 1.0)(0.30, 1.0)` | TOKEN_REFERENCE_ONLY | **Level A**（已人工补证） | 同上；**当前实际位置** `MotionDirector.cs:666` 与 `:1110` | 已补证；此缓动为 OACT **fallback** 路径 |
| `HINT-008` | `MotionDirector.cs:334` — `CubicBezier (0.10, 0.90)(0.20, 1.00)` | TOKEN_REFERENCE_ONLY | **Level A**（已人工补证） | 同上；确认为提示卡逐行入场缓动 | 已补证；无需动作 |
| `RULE-005` | `Themes/Materials.xaml:19` — 另注 `Colors.xaml:21;26;30` 与 `Controls.xaml:18-21` | TOKEN_REFERENCE_ONLY | **Level A**（跨文件） | 跨 3 文件的 alpha 值清单，路径串含分号不可整体检索 | 已由偏差 **L-12** 覆盖（18 个 alpha 值）；建议后续收敛为 Token |

### 3.4 `BEHAVIOR_ASSERTION`（8 条）

> 这些记录描述**具体的数值、公式或行为边界**。它们**不是**「找不到」，
> 而是「需要人工逐行回读才能确认」。本轮已对其中多条完成回读。

| FeatureId | Original Evidence | Current Classification | Evidence Level | Reason | Required Future Action |
| --- | --- | --- | --- | --- | --- |
| `OACT-008` | `FluidZoomTransitionCoordinator.cs:402-404` — `0->22% fade / 22->70% hidden / 70->88% restore` | BEHAVIOR_ASSERTION | **Level A**（人工确认） | 相位区间是数值三元组，无符号名 | 已确认与 `ApplyFrame()` 一致；保留 |
| `TAOP-003` | `MainWindow.xaml.cs:1640-1641` — `edgeMargin = 24 / topSafety = 44` | BEHAVIOR_ASSERTION | **Level A**（人工确认） | 常量赋值行含两个数值 | 已确认（`const double edgeMargin = 24;` / `const double topSafety = 44;`）；无需动作 |
| `TAOP-004` | `MainWindow.xaml.cs:1660-1665` — `availableHeight = rootHeight - top - edgeMargin` | BEHAVIOR_ASSERTION | **Level A**（人工确认） | 是公式而非符号 | 已确认与「垂直溢出改为高度夹紧」一致（U60 口径）；无需动作 |
| `TAOP-005` | `MainWindow.xaml.cs:1672-1673` — `centerPointX = (anchorLeft + anchorWidth/2) - left; centerPointY = 0.0` | BEHAVIOR_ASSERTION | **Level A**（人工确认） | 是公式 | 已确认与「Scale 原点锚在触发按钮中心」一致；无需动作 |
| `PROG-002` | `ProgressPresentationCoordinator.cs` — `visual += (target - visual) * (1 - exp(-k*dt))` | BEHAVIOR_ASSERTION | **Level A**（人工确认） | 是数学表达式，无标识符 | 已确认为指数滤波，`k = 10`；无需动作 |
| `IP-007` | `ImmersiveTransferProgressRenderer.cs` — `Accepts only Value/Maximum/VisualState/EffectsQuality` | BEHAVIOR_ASSERTION | **Level B** | 是隔离边界断言，需查调用链 | 已由硬性规范 **R28** 与契约测试锁定；保留 |
| `BOOT-008` | `ResponsiveLayoutController.cs` — `Breakpoints 1320 / 1120` | BEHAVIOR_ASSERTION | **Level A**（人工确认） | 是数值对 | 已确认断点存在；建议提取为具名常量便于检索 |
| `TYP-002` | `Themes/Typography.xaml` — `Natural line heights 11->15 / 13->18 / 17->23 / 20->28 / 40->54` | BEHAVIOR_ASSERTION | **Level A**（人工确认） | 是测量值表，非符号 | 已由硬性规范 §19.4 登记；无需动作 |

### 3.5 `NEED_MANUAL_REVIEW`（4 条）

| FeatureId | Original Evidence | Current Classification | Evidence Level | Reason | Required Future Action |
| --- | --- | --- | --- | --- | --- |
| `LMDS-007` | `MainWindow.xaml:41;51;115` + 另注 `PCMigSurface.xaml.cs:99-102` | NEED_MANUAL_REVIEW | **Level C→A 待定** | 景深 Z 值散落在 37 处 XAML、12 个不同 Z 值，无单一锚点 | 由偏差 **L-11** 覆盖；建议引入 Z 轴 Token 后重新锚定 |
| `TAOP-008` | `Views/Step2SelectDataPage.xaml.cs` — `AlignJobsDropDownBelowCombo (removed)` | NEED_MANUAL_REVIEW | **Level D**（**已在源码中不存在**） | 该方法是**失败的尝试，已删除**；记录它是有价值的历史 | 保留为「已移除」记录，**不要**再去找它；对应失败教训已登记 |
| `IP-006` | `Controls/ImmersiveProgress/ImmersiveTransferProgress.xaml` — `ReducedMotion` | NEED_MANUAL_REVIEW | **Level D** | `ReducedMotion` 属性**是否有 XAML 绑定来源未确认**；若无人设置则恒为 false | **高优先级**：需确认该属性是否真被赋值；若恒 false，则 Reduced Motion 实际未接线（与 **G-02** 相关） |
| `TYP-001` | `Themes/Typography.xaml:82-108` — `Microsoft YaHei UI registers only 290/400/700` | NEED_MANUAL_REVIEW | **Level B** | 是平台事实，需真机字重测量确认（已有探针证据） | 已由硬性规范 **R19** 登记；保留（**PLATFORM LIMITATION**） |

---

## 四、`NO_PROBE` 中已补证的部分

本轮人工回读后，以下 **5 条** 已从「待人工回读」升级为有明确证据（但仍保留在 `NO_PROBE` 裁定中，因为它们**在 CSV 里的引用形态确实无法自动检索**）：

| FeatureId | 补证结论 | 新证据等级 |
| --- | --- | --- |
| `DCST-003` | 缓动 `(0.10,0.90)(0.20,1.00)` 现位于 `MotionDirector.cs:335` 与 `:915` | Level A |
| `OACT-013` | 缓动 `(0.16,1.0)(0.30,1.0)` 现位于 `MotionDirector.cs:666` 与 `:1110` | Level A |
| `HINT-008` | 确认为提示卡逐行入场缓动，`MotionDirector.cs:334-335` | Level A |
| `TAOP-003/004/005` | 三处常量/公式已逐行核对，与硬性规范 U60 口径一致 | Level A |
| `PROG-002` | 指数滤波公式与 `k = 10` 已确认 | Level A |

> **处置口径**：这些记录**不修改 `Verdict`**，也不从 `NO_PROBE` 移出。
> 理由：`Verdict` 描述的是「**CSV 里的引用能否被自动检索**」，而不是「结论是否已确认」。
> 把已确认的结论写在本文档中，保持两个维度的独立性。

---

## 五、证据等级的使用纪律

### 5.1 引用时的措辞规范

| 等级 | 允许的措辞 | 禁止的措辞 |
| --- | --- | --- |
| Level A | 「代码中存在……」/「已实现」 | — |
| Level B | 「在……入口下会执行」 | 「一定生效」 |
| Level C | 「注释声称……」/「设计意图为……」 | 「已实现」/「事实是……」 |
| Level D | 「存在引用线索」/「尚未验证」 | 「已实现」/「不存在」（除非有反证） |

### 5.2 与 `ImplementationStatus` 的关系

`ImplementationStatus`（`REALIZED` / `STALE` / `KNOWN GAP` / …）描述**代码现状**，
`Evidence Level` 描述**证据强度**。两者独立，常见组合：

| 组合 | 含义 | 例 |
| --- | --- | --- |
| Level A + REALIZED | 实现存在且已验证 | 绝大多数记录 |
| Level C + STALE | 注释声称但已过期 | `DCST-004` / `MHE-011` |
| Level C + REALIZED | 注释与实现一致，但证据本身是注释 | `VERIFY-003` |
| Level D + REMOVED | 曾存在、现已删除 | `TAOP-008` |

### 5.3 不得做的事

1. **不得**把 Level C 的注释主张当作实现证据写入结论。
2. **不得**因为某条是 Level D 就删除它——它记录了「有一次设计引用存在过」。
3. **不得**在未取得 Level A/B 新证据的情况下，把 Level C/D 升级为「已实现」。
4. **不得**用「探针已证明」替代「用户真机所见」（见 `PMML-Source-Traceability-Methodology.md` §4.3）。

---

## 六、统计总览

| 指标 | 值 |
| --- | ---: |
| 记录总数 | 152 |
| Level A | 117 |
| Level B | 5 |
| Level C | 6 |
| Level D（NO_PROBE） | 24 |
| 其中 `COMMENT_ONLY` | 8 |
| 其中 `TOKEN_REFERENCE_ONLY` | 4 |
| 其中 `BEHAVIOR_ASSERTION` | 8 |
| 其中 `NEED_MANUAL_REVIEW` | 4 |
| `NO_PROBE` 已补证 | 5 |
| `NO_PROBE` 删除数 | **0**（全部保留） |

---

## 七、关联文档

| 文档 | 定位 |
| --- | --- |
| `PMML-Source-Traceability-Methodology.md` | 追溯方法与判定口径（本文档的上游） |
| `PMML-Source-Traceability.csv` | 152 条记录本体 |
| `diff/relocation.csv` | 逐条裁定（含 `Verdict`） |
| `diff/差异报告.md` | 裁定汇总 |
| `PMML-Implementation-Map.md` | 要素 → 源码落点索引 |
| `PMML-Engineering-Closure-Report.md` | 收口报告 |
