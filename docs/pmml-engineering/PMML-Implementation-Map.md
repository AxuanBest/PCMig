# PMML Implementation Map

> 本文档把 `PMML-Source-Traceability.csv` 的 **152 条记录**按 **Feature Family** 重新组织成
> 一份**源码落点索引地图**，用于快速回答：「这个设计要素在代码哪里？它现在可信吗？」

| 项目 | 值 |
| --- | --- |
| 文档类型 | Engineering Documentation（索引地图） |
| 基线 commit | `b68613dc3600767e4e7cf2b24e413e5c91a16280` |
| 记录数 | 152 / 21 族 |
| 数据权威 | `PMML-Source-Traceability.csv`（本文档是它的**视图**，不是副本） |
| 状态 | READY FOR MAINTENANCE |

---

## 一、怎么读这张地图

### 1.1 裁定标记

每条记录带一个**行号裁定**（来自 `diff/relocation.csv`）：

| 标记 | 含义 | 能否直接引用 |
| --- | --- | --- |
| `UNCHANGED` | 行号与 HEAD 完全一致 | ✅ 直接引用 |
| `RELOCATED` | 同文件内行号漂移 | ✅ 按当前行号引用 |
| `LOCATED` | 原本无行号，本次补全 | ✅ 直接引用 |
| `MANUAL-ANCHOR` | 人工指定锚点 | ✅ 直接引用 |
| `NO_PROBE` | 无可检索标识符 | ⚠ 见 Evidence Classification |
| `FILE_GONE` | 引用文件已不存在 | ❌ 见该条目说明 |

### 1.2 状态标记

`REALIZED` / `STALE` / `SCATTERED` / `KNOWN GAP` / `DEAD CODE` / `CONFLICT` /
`PLATFORM LIMITATION` / `DIAGNOSTIC ONLY` / `REMOVED` / `DEPRECATED` /
`FALLBACK ONLY` / `DEAD TOKEN` / `RISK` / `DEFECT OPEN` / `SEMANTIC CONFLICT` / `CLOSED`

> `STALE` 表示**注释与实现不符**（实现本身通常是对的）。引用 `STALE` 条目时**必须以实现为准**。

### 1.3 哪些是关键入口

下表 ★ 标出的条目是各系统的**主要实现入口**，排查问题从这里开始。

---

## 二、Material System（材质系统）

### 2.1 DAM — Desktop Acrylic Material System（12 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `DAM-001` | `MainWindow.xaml:9` | UNCHANGED | REALIZED | ★ 声明式 backdrop：`<MicaBackdrop Kind="BaseAlt"/>` |
| `DAM-002` | `Presentation/BackdropSpike.cs:158` | MANUAL-ANCHOR | REALIZED | ★ **运行时装配入口**：`Install(Window)`，默认 `kind = "acrylic"` |
| `DAM-003` | `Presentation/BackdropSpike.cs:189` | UNCHANGED | REALIZED | ★ Acrylic 参数：`TintOpacity` / `LuminosityOpacity` / `TintColor` / `FallbackColor` |
| `DAM-004` | `Themes/Materials.xaml:19` | LOCATED | REALIZED | Shell 材质画刷 |
| `DAM-005` | `Themes/Materials.xaml:22` | LOCATED | REALIZED | Card 材质画刷 |
| `DAM-006` | `Themes/Materials.xaml:24` | LOCATED | REALIZED | Inset 材质画刷 |
| `DAM-007` | `Themes/Materials.xaml:147` | UNCHANGED | STALE | 环境色 / 底色渐变 |
| `DAM-008` | `Themes/Materials.xaml:425` | RELOCATED | REALIZED | Utility Acrylic 变体 |
| `DAM-009` | `MainWindow.xaml:28` | RELOCATED | REALIZED | 氛围层 Ambient 椭圆 |
| `DAM-010` | `Themes/Colors.xaml:9` | UNCHANGED | STALE | `BaseBackdropBrush` 渐变 |
| `DAM-011` | `MainWindow.xaml:23` | UNCHANGED | **KNOWN GAP** | 氛围层注释与实测不符（真透明未生效） |
| `DAM-012` | `Presentation/DeveloperVisualTuning.cs:273` | UNCHANGED | REALIZED | 材质调节持久化路径 |

**排查入口**：`DAM-002` → `DAM-003`。已知偏差 **L-10**（Acrylic 显式参数 vs Mica 系统默认回退，不同源）。

### 2.2 LMDS — Layered Material Depth System（7 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `LMDS-001` | `Themes/Materials.xaml:6` | RELOCATED | **SEMANTIC CONFLICT** | ★ 五层定义（与代码实际分层冲突，偏差 **L-01**） |
| `LMDS-002` | `Themes/Materials.xaml:156` | RELOCATED | REALIZED | L0/L1 层对应 |
| `LMDS-003` | `Themes/Materials.xaml:157` | RELOCATED | REALIZED | L2 层对应 |
| `LMDS-004` | `Themes/Materials.xaml:157` | RELOCATED | REALIZED | L3 层对应 |
| `LMDS-005` | `Themes/Materials.xaml:156` | RELOCATED | REALIZED | L4 层对应 |
| `LMDS-006` | `Themes/Materials.xaml:148` | UNCHANGED | STALE | 景深阴影 |
| `LMDS-007` | `MainWindow.xaml:41` | **NO_PROBE** | SCATTERED | 景深 Z 值清单（37 处 / 12 个值，偏差 **L-11**） |

**注意**：`LMDS-001` 是 **SEMANTIC CONFLICT** —— PMML 规定的五层与代码实际层不完全对应（代码无独立 Overlay/Tool 层）。这是**已登记的设计层偏差**，不是缺陷。

### 2.3 DSL-45 — 45° Directional Surface Lighting（8 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `DSL45-001` | `Themes/Colors.xaml:42` | NO_PROBE | STALE | 光源方向（注释表述） |
| `DSL45-002` | `Themes/Colors.xaml:44` | UNCHANGED | STALE | `NavCardEmbossTopBrush` 亮边 |
| `DSL45-003` | `Themes/Colors.xaml:49` | UNCHANGED | STALE | `NavCardEmbossEdgeBrush` 暗边 |
| `DSL45-004` | `Themes/Colors.xaml:17` | UNCHANGED | REALIZED | `EdgeHighlightDiagonalBrush` |
| `DSL45-005` | `Themes/Colors.xaml:18` | UNCHANGED | REALIZED | `EdgeHighlightSoftDiagonalBrush` |
| `DSL45-006` | `Themes/Colors.xaml:20` | UNCHANGED | STALE | `EdgeHighlightCrispBrush` |
| `DSL45-007` | `Themes/Materials.xaml:83` | UNCHANGED | REALIZED | ★ DAEL 画刷族 |
| `DSL45-008` | `Themes/Materials.xaml:298` | LOCATED | STALE | `PCMigSurface` 受光四件套 |

**物理约束**（源码注释原文）：侧栏卡片底色近纯白（实测 RGB(251,253,255)），**白叠白不可见** ⇒ 浮雕必须「亮边 + 暗边**成对**」。已知偏差 **L-13**（8 套高光/浮雕并存，其中 6 个键零引用）。

### 2.4 ESR — Subtle Embossed Material Relief（3 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `ESR-001` | `Themes/Materials.xaml:131` | RELOCATED | REALIZED | ★ 浮雕边缘高光 |
| `ESR-002` | `Themes/Materials.xaml:148` | UNCHANGED | STALE | 阴影（**G-03** 无数值） |
| `ESR-003` | `Themes/Colors.xaml:55` | RELOCATED | SCATTERED | 边框/边缘画刷族 |

---

## 三、Motion System（动效系统）

### 3.1 DCST — Directional Cross-Slide Transition（8 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `DCST-001` | `Presentation/MotionDirector.cs:802` | LOCATED | REALIZED | ★ `PlayPagePush` 实现 |
| `DCST-002` | `Themes/Motion.xaml:16` | UNCHANGED | REALIZED | ★ `PCMigMotionPagePushDuration = 0:0:0.42` |
| `DCST-003` | `Presentation/MotionDirector.cs:370` | **NO_PROBE** | STALE | 缓动 `(0.10,0.90)(0.20,1.00)`（现于 `:335` / `:915`） |
| `DCST-004` | `Themes/Motion.xaml:14` | **NO_PROBE** | STALE | ★ 过期注释（偏差 **L-05**，注释称淡出、实际 Opacity 恒 1） |
| `DCST-005` | `Themes/Motion.xaml:18` | UNCHANGED | DEPRECATED | `PageSlideDistance 26`（U57 起废弃） |
| `DCST-006` | `Presentation/PageTransitionCoordinator.cs:76` | RELOCATED | REALIZED | 页面切换编排（只写 Visibility） |
| `DCST-007` | `MainWindow.xaml.cs:1743` | UNCHANGED | REALIZED | Shell 侧 `OnNavChanged` 接线 |
| `DCST-008` | `MainWindow.xaml:60` | UNCHANGED | STALE | 页面实例与 Visibility 绑定 |

**关键口径**：生效实现 = **整页视口高度纵向 Push / 0.42s / Opacity 恒 1 / 无 Scale**。`Motion.xaml:14-15` 的注释与实现不符，**以实现为准**。

### 3.2 OACT — Origin-Anchored Container Transform（14 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `OACT-001` | `Presentation/FluidZoomTransitionCoordinator.cs:68` | LOCATED | REALIZED | ★ 主路径协调器 |
| `OACT-002` | `Themes/Motion.xaml:35` | UNCHANGED | REALIZED | ★ Open 时长 `0:0:0.36` |
| `OACT-003` | `Themes/Motion.xaml:36` | UNCHANGED | REALIZED | ★ Close 时长 `0:0:0.30` |
| `OACT-004` | `Presentation/FluidZoomTransitionCoordinator.cs:371` | UNCHANGED | STALE | 相位：fill 区间 |
| `OACT-005` | `Presentation/FluidZoomTransitionCoordinator.cs:372` | UNCHANGED | STALE | 相位：stroke 区间 |
| `OACT-006` | `Presentation/FluidZoomTransitionCoordinator.cs:389` | UNCHANGED | STALE | 相位：panel 区间 |
| `OACT-007` | `Presentation/FluidZoomTransitionCoordinator.cs:373` | UNCHANGED | STALE | 相位：shell fade |
| `OACT-008` | `Presentation/FluidZoomTransitionCoordinator.cs:402` | **NO_PROBE** | REALIZED | 相位：source 入口 三段 |
| `OACT-009` | `Presentation/FluidZoomTransitionCoordinator.cs:360` | UNCHANGED | REALIZED | ★ geometry `EaseOutCubic(t)` |
| `OACT-010` | `Presentation/FluidZoomTransitionCoordinator.cs:610` | LOCATED | REALIZED | 缓动函数构造 |
| `OACT-011` | `Presentation/FluidZoomTransitionCoordinator.cs:200` | UNCHANGED | REALIZED | ★ 诊断输出（`anim=` 现场证据点） |
| `OACT-012` | `Themes/Motion.xaml:21` | UNCHANGED | **FALLBACK ONLY** | ★ Origin Reveal 六 Token（**已被用户否决为主路径**） |
| `OACT-013` | `Presentation/MotionDirector.cs:565` | **NO_PROBE** | STALE | fallback 缓动 `(0.16,1.0)(0.30,1.0)`（现于 `:666` / `:1110`） |
| `OACT-014` | `Presentation/MotionDirector.cs:173` | RELOCATED | **DEAD CODE** | `PlayPanelExit`（偏差 **L-06**） |

**相位权威** = `ApplyFrame()`。**用户否决记录**：Origin Reveal 被人工否决为「本质还是淡入弹窗」，Token 保留但只作 fallback。

### 3.3 TAOP — Trigger-Anchored Overlay Placement（9 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `TAOP-001` | `MainWindow.xaml.cs:1604` | UNCHANGED | REALIZED | ★ `TryPlacePanel` 定义 |
| `TAOP-002` | `Themes/Motion.xaml:29` | LOCATED | REALIZED | ★ `PCMigUtilityPanelAnchorGap = 5` |
| `TAOP-003` | `MainWindow.xaml.cs:1640` | **NO_PROBE** | REALIZED | `edgeMargin = 24` / `topSafety = 44` |
| `TAOP-004` | `MainWindow.xaml.cs:1660` | **NO_PROBE** | REALIZED | 垂直溢出改为**高度夹紧**（U60 口径） |
| `TAOP-005` | `MainWindow.xaml.cs:1672` | **NO_PROBE** | REALIZED | Scale 原点 = 触发按钮中心 |
| `TAOP-006` | `MainWindow.xaml.cs:184` | RELOCATED | REALIZED | 面板定位接线 |
| `TAOP-007` | `Views/Step2SelectDataPage.xaml:37` | RELOCATED | REALIZED | ★ Task Picker（**正式参考实现**） |
| `TAOP-008` | `Views/Step2SelectDataPage.xaml.cs` | **NO_PROBE** | **REMOVED** | `AlignJobsDropDownBelowCombo`（**失败尝试，已删除**） |
| `TAOP-009` | `MainWindow.xaml.cs:1041` | RELOCATED | STALE | 浮层切换入口 |

**排查入口**：`TAOP-001`。**已登记失败教训**：为修正原生 ComboBox 浮层位置而加的 `Popup.VerticalOffset` 后置校正实测无效，已删除。

### 3.4 MHE — Material Hover Elevation（11 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `MHE-001` | `Presentation/InteractionFeedback.cs:285` | LOCATED | REALIZED | ★ 实现（只走 Translation，**无 Scale**） |
| `MHE-002` | `Themes/Motion.xaml:47` | UNCHANGED | REALIZED | `HoverLiftCard = 2` |
| `MHE-003` | `Themes/Motion.xaml:48` | UNCHANGED | REALIZED | `HoverLiftButton = 1.3` |
| `MHE-004` | `Themes/Motion.xaml:49` | UNCHANGED | REALIZED | `PressedSinkCard = 0.5` |
| `MHE-005` | `Themes/Motion.xaml:50` | UNCHANGED | REALIZED | `PressedSinkButton = 0.6` |
| `MHE-006` | `Themes/Motion.xaml:44` | UNCHANGED | REALIZED | `HoverEnterDuration = 0:0:0.13` |
| `MHE-007` | `Themes/Motion.xaml:45` | UNCHANGED | REALIZED | `HoverExitDuration = 0:0:0.15` |
| `MHE-008` | `Themes/Motion.xaml:46` | UNCHANGED | REALIZED | `PressedDuration = 0:0:0.10` |
| `MHE-009` | `App.xaml:1` | LOCATED | STALE | 全仓 **MHE 零命中**（本项目不用 WinUI MHE 机制） |
| `MHE-010` | `Views/StepNavigationControl.xaml:33` | UNCHANGED | **DEFECT OPEN** | 步骤卡悬停零反馈（**G-05**） |
| `MHE-011` | `Views/StepNavigationControl.xaml:17` | **NO_PROBE** | STALE | 注释声称有位移反馈，实测**无** |

**单位纪律**：Canonical DIP，与 `UniformScaleHost` 同坐标系 ⇒ **不得再乘 `ApplicationUIScale`**。

### 3.5 NAV — 步骤导航（6 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `NAV-001` | `Views/StepNavigationControl.xaml:19` | UNCHANGED | REALIZED | ★ 导航控件 ItemsControl |
| `NAV-002` | `Views/StepNavigationControl.xaml:72` | RELOCATED | REALIZED | 步骤卡模板 |
| `NAV-003` | `Themes/Motion.xaml:7` | UNCHANGED | REALIZED | ★ `PCMigMotionDurationSelection = 0:0:0.16` |
| `NAV-004` | `Views/StepNavigationControl.xaml:58` | RELOCATED | **PLATFORM LIMITATION** | 描边/文字色**无 Transition**（WinUI 只提供 Background/Opacity 过渡） |
| `NAV-005` | `Views/StepNavigationControl.xaml:99` | UNCHANGED | REALIZED | 选中条 |
| `NAV-006` | `Presentation/StepNavigation.cs:102` | RELOCATED | **DEFECT OPEN** | 选中描边不可见（偏差 **L-04**） |

---

## 四、Progress Visual System（进度视觉系统）

### 4.1 PROG — 进度呈现（12 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `PROG-001` | `Presentation/ProgressPresentationCoordinator.cs:276` | MANUAL-ANCHOR | REALIZED | ★ **硬守卫** `ClampVisual()`：视觉绝不越过已确认真值 |
| `PROG-002` | `Presentation/ProgressPresentationCoordinator.cs` | **NO_PROBE** | REALIZED | ★ 指数滤波 `visual += (target-visual)*(1-exp(-k*dt))`，`k = 10` |
| `PROG-003` | `Presentation/ProgressPresentationCoordinator.cs:75` | LOCATED | REALIZED | ★ 真值入口 `ApplyTruth` |
| `PROG-004` | `Presentation/MigrationSessionViewModel.cs:3955` | LOCATED | REALIZED | 会话侧真值发布 |
| `PROG-005` | `Presentation/MigrationSessionViewModel.cs:1229` | LOCATED | REALIZED | 进度快照 |
| `PROG-006` | `MainWindow.xaml:123` | UNCHANGED | REALIZED | ★ Footer 进度宿主 |
| `PROG-007` | `MainWindow.xaml:123` | UNCHANGED | **CONFLICT** | Hero/Compact 映射分叉（`JobPhase.Canceled` 在 Hero→`Warning`、在 Compact→`Interrupted`） |
| `PROG-008` | `MainWindow.xaml:123` | UNCHANGED | REALIZED | Footer 进度接线 |
| `PROG-009` | `Presentation/ProgressMotionDriver.cs` | **FILE_GONE** | CLOSED | ★ **已删除实现**（见下） |
| `PROG-010` | `Themes/Motion.xaml:52` | LOCATED | **DEAD TOKEN** | `PCMigMotionProgressSweepDuration` 零消费者（**G-04**） |
| `PROG-011` | `MainWindow.xaml:123` | UNCHANGED | REALIZED | Footer Compact 变体 |
| `PROG-012` | `MainWindow.xaml.cs:48` | RELOCATED | REALIZED | 呈现节拍常量 |

**`PROG-009` 已删除实现说明**：`Presentation/ProgressMotionDriver.cs`（818 行 / 40.1 KB）
已于 **2026-10-06 经用户授权删除**，对应偏差登记册 **L-19 已 CLOSED**。
当前 `src/` **零生产引用**；仅测试保留断言：
`tests/PCMig.Core.Tests/Batch5UiLayoutContractTests.cs:184`、
`tests/PCMig.Core.Tests/ImmersiveVisualContractTests.cs:318/325/326`。

**当前进度实现锚点** = `MainWindow.xaml:123`（`FooterProgressHost` + `Variant="Compact"`）
+ `Controls/ImmersiveProgress/`（Hero 与 Compact 同一控件族，**R33**）。

### 4.2 IP — Immersive Progress 控件（7 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `IP-001` | `Controls/ImmersiveProgress/ImmersiveProgressParameters.cs:51` | UNCHANGED | REALIZED | ★ `Interrupted = 7`（12 态枚举） |
| `IP-002` | `Views/Step3ProgressPage.xaml.cs:383` | UNCHANGED | **CONFLICT** | Hero 侧 `JobPhase.Canceled → Warning` |
| `IP-003` | `Controls/ImmersiveProgress/ImmersiveProgressParameters.cs` | LOCATED | REALIZED | 尺寸参数 |
| `IP-004` | `Controls/ImmersiveProgress/ImmersiveProgressParameters.cs` | LOCATED | REALIZED | 装饰参数 |
| `IP-005` | `Controls/ImmersiveProgress/ImmersiveTransferProgress.xaml` | LOCATED | REALIZED | 控件 XAML |
| `IP-006` | `Controls/ImmersiveProgress/ImmersiveTransferProgress.xaml` | **NO_PROBE** | STALE | ⚠ `ReducedMotion` 绑定来源**未确认** |
| `IP-007` | `Controls/ImmersiveProgress/ImmersiveTransferProgressRenderer.cs` | **NO_PROBE** | REALIZED | ★ Renderer 只接受 4 个输入（**R28**） |

**⚠ 高优先级未决项**：`IP-006` —— 若 `ReducedMotion` 无人设置则恒为 false，
则 Reduced Motion 实际未接线（与 **G-02** 相关）。

### 4.3 进度视觉的硬性规则速查

| 规则 | 要点 |
| --- | --- |
| **R21** | 进度头**只代表已确认的迁移事实**；装饰绝不推进 `Value` |
| **R23** | 进度头 / 大号百分比 / 已传字节必须**消费同一个** `VisualProgress` |
| **R24** | `VisualProgress` 只许**落后**不许领先 |
| **R25** | 同时**最多一条** Push Band；填充常驻满宽，只改 `InsetClip` 标量 |
| **R26** | 粒子属 **Progress Space**，不属屏幕空间 |
| **R31** | Capsule Integrity：Host 永远透明，禁止矩形裁剪 |
| **R32** | Color Field 锚定**整个 Track** 空间，非 `ProgressWidth` |
| **R33** | Hero / Compact 同一控件族、同一 `VisualProgress`；两值差 ≤ 0.1% |
| **R34** | Head 高光必须是**体积光场**，禁止可辨识 Stroke/Rim |
| **R35** | `Paused`/`Interrupted` **冻结事实但保留活性**；只有 `Failed`/`Completed` 是终止态 |
| **R36** | 嵌入卡片时必须真正透出父 Surface（用 `CanvasControl`，非交换链） |

---

## 五、其它族

### 5.1 BOOT — 启动与闸门（10 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `BOOT-001` | `App.xaml.cs:26` | UNCHANGED | REALIZED | ★ `StartupTimeline.Begin` |
| `BOOT-002` | `App.xaml.cs:94` | UNCHANGED | REALIZED | ★ **`MotionDirector.MarkShellInteractive()`**（v0.5.4 回归修复） |
| `BOOT-003` | `Presentation/MotionDirector.cs:88` | UNCHANGED | REALIZED | ★ `SystemAnimationsEnabled` 七级优先级判定 |
| `BOOT-004` | `Presentation/MotionDirector.cs:88` | LOCATED | REALIZED | 缓存字段 `_animationsEnabled` |
| `BOOT-005` | `App.xaml:6` | UNCHANGED | STALE | 资源合并顺序 |
| `BOOT-006` | `MainWindow.xaml.cs:23` | UNCHANGED | REALIZED | Canonical 客户区尺寸 `1424×891` |
| `BOOT-007` | `MainWindow.xaml.cs:34` | UNCHANGED | REALIZED | 最小客户区尺寸 |
| `BOOT-008` | `Presentation/ResponsiveLayoutController.cs` | **NO_PROBE** | REALIZED | 响应式断点 `1320 / 1120` |
| `BOOT-009` | `Presentation/ShellResponsiveLayout.cs:348` | UNCHANGED | **DEAD CODE** | `ApplyStepCardHeight` 只自递归，无外部调用者 |
| `BOOT-010` | `Presentation/ShellResponsiveLayout.cs` | **NO_PROBE** | STALE | 引用已改名 API 的注释 |

**v0.5.4 回归修复要点**：`BOOT-002` 是修复的核心——`MarkShellInteractive()` 在
`Activate()` 之后调用，使此后读到的动画偏好 false 才算用户真实选择。

### 5.2 HINT — 提示卡（12 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `HINT-001` | `Views/ShellHintCard.xaml.cs:209` | UNCHANGED | REALIZED | ★ `ApplyAdaptiveHeight()` |
| `HINT-002` | `Views/ShellHintCard.xaml.cs:261` | UNCHANGED | REALIZED | ★ `ApplySurfaceHeight()`（**唯一**写高处） |
| `HINT-003` | `Themes/Motion.xaml:69` | UNCHANGED | REALIZED | `HintRevealDuration = 0:0:0.26` |
| `HINT-004` | `Themes/Motion.xaml:70` | UNCHANGED | REALIZED | `HintCollapseDuration = 0:0:0.22` |
| `HINT-005` | `Themes/Motion.xaml:57` | UNCHANGED | REALIZED | `HintLineDuration = 0:0:0.17` |
| `HINT-006` | `Themes/Motion.xaml:58` | UNCHANGED | REALIZED | `HintLineOffset = 6` |
| `HINT-007` | `Presentation/MotionDirector.cs:476` | UNCHANGED | REALIZED | 展开缓动 |
| `HINT-008` | `Presentation/MotionDirector.cs:334` | **NO_PROBE** | STALE | 逐行缓动 `(0.10,0.90)(0.20,1.00)` |
| `HINT-009` | `Views/ShellHintCard.xaml.cs:56` | UNCHANGED | REALIZED | 节流常量 `600 ms` |
| `HINT-010` | `Presentation/MotionDirector.cs:412` | RELOCATED | REALIZED | 行入场重置 |
| `HINT-011` | `Views/ShellHintCard.xaml.cs:68` | UNCHANGED | REALIZED | 订阅属性白名单（只订 4 条通道） |
| `HINT-012` | `Views/ShellHintCard.xaml:74` | UNCHANGED | REALIZED | 滚动容器 |

### 5.3 VERIFY — 验证面板（6 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `VERIFY-001` | `Views/VerificationProgressPanel.xaml.cs:73` | RELOCATED | REALIZED | ★ 打开面板不可触发验证 |
| `VERIFY-002` | `Views/VerificationProgressPanel.xaml.cs:179` | RELOCATED | REALIZED | ★ 面板与取消按钮同拍就位（实测教训） |
| `VERIFY-003` | `Views/VerificationProgressPanel.xaml.cs:29` | **NO_PROBE** | REALIZED | 关闭面板 ≠ 取消 |
| `VERIFY-004` | `MainWindow.xaml.cs:1032` | RELOCATED | REALIZED | ⚠ **锚点已失配**（见下） |
| `VERIFY-005` | `MainWindow.xaml:130` | RELOCATED | REALIZED | 验证浮层实例 |
| `VERIFY-006` | `Views/ChangelogPanel.xaml:5` | **NO_PROBE** | DEAD CODE | 浮层声明式转场已移除 |

**⚠ `VERIFY-004` 已知锚点缺陷**：该条 `SourcePath` 指向 `MainWindow.xaml.cs:1032`，
但该行实际是 `SafeCompleteFooterAction`（底栏兜底处理器），**与本条主题无关**。
该条的 `UsedBy` 字段仍写 `EnsureVerifyPanel` —— **该方法已不存在**。

**正确锚点**（本轮人工核实，**未写入 CSV**，因执行令只授权修改 `DAM-002` / `PROG-001`）：

| 项 | 位置 |
| --- | --- |
| 新 API | `MainWindow.xaml.cs:1190` `public void ShowVerificationPanel(FrameworkElement? anchor)` |
| 唯一调用点 | `Views/Step4ResultPage.xaml.cs:265` `shell.ShowVerificationPanel(ToolbarVerify);` |
| 互斥辅助 | `MainWindow.xaml.cs:1215` `private FrameworkElement OtherVisibleToolPanel(FrameworkElement self)` |

**不变量仍成立**：打开面板只走
`TogglePanel(VerificationProgressPanel, OtherVisibleToolPanel(...), realAnchor, onOpened: null)`，
**不执行闸门、不触发 IO、不开始验证**。

### 5.4 STATUS — 状态通道（4 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `STATUS-001` | `Presentation/StatusChannel.cs:20` | UNCHANGED | REALIZED | ★ 五通道枚举 |
| `STATUS-002` | `Presentation/MigrationSessionViewModel.cs:958` | UNCHANGED | REALIZED | ★ `SetChannel` |
| `STATUS-003` | `Presentation/MigrationSessionViewModel.cs:969` | UNCHANGED | REALIZED | 对象通道早退 |
| `STATUS-004` | `src/PCMig.WinUI` | **NO_PROBE** | STALE | ⚠ 「Footer Isolation」**该机制不存在** |

### 5.5 RULE — 规则与统一性（8 条）

| FeatureId | 锚点 | 裁定 | 状态 | 说明 |
| --- | --- | --- | --- | --- |
| `RULE-001` | `docs/PCMig-Visual-Motion-Language.md:392` | UNCHANGED | **CONFLICT** | 规范与实现冲突项 |
| `RULE-002` | `docs/PCMig-Visual-Motion-Language.md:62` | RELOCATED | REALIZED | 规则条目 |
| `RULE-003` | `Controls/ImmersiveProgress/ImmersiveProgressParameters.cs` | LOCATED | SCATTERED | 参数分散 |
| `RULE-004` | `Themes/Controls.xaml:225` | LOCATED | SCATTERED | 控件样式分散 |
| `RULE-005` | `Themes/Materials.xaml:19` | **NO_PROBE** | SCATTERED | 近白 alpha 散落（**L-12**，18 个值） |
| `RULE-006` | `Presentation/MotionDirector.cs:10` | RELOCATED | SCATTERED | 缓动多族并存（**L-08**） |
| `RULE-007` | `Themes/Controls.xaml:3` | RELOCATED | SCATTERED | 控件族定义 |
| `RULE-008` | `Themes/Colors.xaml` | **NO_PROBE** | REALIZED | 颜色 Token 存档规则 |

> `RULE-001` / `RULE-002` 是**指向 `docs/` 下文档**的记录（非源码），
> 曾因路径含 `..` 被错误规约，本轮已修正候选路径解析。

### 5.6 TYP / A11Y / THEME / COMBO / PROBE（其余 16 条）

| FeatureId | 锚点 | 裁定 | 状态 |
| --- | --- | --- | --- |
| `TYP-001` | `Themes/Typography.xaml:82` | NO_PROBE | PLATFORM LIMITATION |
| `TYP-002` | `Themes/Typography.xaml` | NO_PROBE | REALIZED |
| `TYP-003` | `Themes/Typography.xaml:49` | LOCATED | REALIZED |
| `A11Y-001` | `Views/StepNavigationControl.xaml:53` | RELOCATED | KNOWN GAP |
| `A11Y-002` | `Controls/ImmersiveProgress/ImmersiveProgressParameters.cs` | LOCATED | KNOWN GAP |
| `A11Y-003` | `Presentation/MotionDirector.cs:105` | RELOCATED | REALIZED |
| `THEME-001` | `Themes/Materials.xaml:33` | LOCATED | KNOWN GAP |
| `THEME-002` | `Themes/Materials.xaml:168` | LOCATED | SCATTERED |
| `COMBO-001` | `Themes/PcmigComboBoxRoll.xaml:221` | LOCATED | REALIZED |
| `COMBO-002` | `Views/Step2SelectDataPage.xaml:392` | RELOCATED | STALE |
| `PROBE-001` | `Views/MetricTypographyProbe.xaml.cs:72` | LOCATED | DIAGNOSTIC ONLY |
| `PROBE-002` | `Views/GlyphContourProbe.xaml.cs:43` | LOCATED | DIAGNOSTIC ONLY |
| `PROBE-003` | `Views/ImmersiveProgressVisualProbe.xaml.cs:63` | LOCATED | DIAGNOSTIC ONLY |
| `PROBE-004` | `Presentation/Step2TreeQaProbe.cs:24` | LOCATED | DIAGNOSTIC ONLY |
| `PROBE-005` | `MainWindow.xaml:43` | UNCHANGED | **RISK** |

---

## 六、需注意事项汇总

| 类别 | 条目 | 说明 |
| --- | --- | --- |
| **已删除实现** | `PROG-009` | `ProgressMotionDriver.cs`，2026-10-06 授权删除（L-19 CLOSED） |
| **已移除但留档** | `TAOP-008` | `AlignJobsDropDownBelowCombo`，失败尝试 |
| **确认不存在** | `STATUS-004` | 「Footer Isolation」机制从未存在 |
| **锚点失配** | `VERIFY-004` | `:1032` 与主题无关；正确 API 在 `:1190` |
| **高优先级未决** | `IP-006` | `ReducedMotion` 绑定来源未确认 |
| **语义冲突** | `LMDS-001` | 五层定义与代码实际分层冲突（L-01） |
| **映射分叉** | `PROG-007` / `IP-002` | `JobPhase.Canceled` 在 Hero→`Warning`、Compact→`Interrupted` |
| **死代码** | `OACT-014` `BOOT-009` | `PlayPanelExit` / `ApplyStepCardHeight` |
| **死 Token** | `PROG-010` | `PCMigMotionProgressSweepDuration` 零消费者 |
| **平台限制** | `NAV-004` `TYP-001` | WinUI 不提供描边/文字色过渡；字号栅格化 |

---

## 七、统计视图

| 项目 | 值 |
| --- | ---: |
| 记录总数 | 152 |
| Feature Family | 21 |
| `UNCHANGED` | 67 |
| `RELOCATED` | 29 |
| `LOCATED` | 29 |
| `MANUAL-ANCHOR` | 2 |
| `NO_PROBE` | 24 |
| `FILE_GONE` | 1 |
| 涉及文件 | `src/PCMig.WinUI/` 下 40+ 文件 + `docs/` 2 份 |
| 主要落点目录 | `Themes/`（材质与动效 Token）、`Presentation/`（逻辑）、`Views/`（页面与浮层）、`Controls/ImmersiveProgress/`（进度控件） |

---

## 八、关联文档

| 文档 | 定位 |
| --- | --- |
| `PMML-Source-Traceability.csv` | **数据权威**（本文档是它的视图） |
| `PMML-Terminology-Registry.md` | 术语定义 |
| `PMML-Source-Traceability-Methodology.md` | 裁定口径与行号漂移处理 |
| `PMML-Evidence-Classification.md` | 证据分级（判断可信度） |
| `PMML-Engineering-Overview.md` | 体系入口 |
| `PMML-Engineering-Closure-Report.md` | 收口报告（未决项汇总） |
| `diff/relocation.csv` | 逐条裁定原始数据 |
