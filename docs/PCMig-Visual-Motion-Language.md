# PCMig Material Motion Language v1.0

> **PMML = PCMig Material Motion Language** ｜ 状态：**PMML v1.0 — FROZEN**（由项目所有者于 2026-09-30 宣布冻结；2026-10-01 仅为文档状态纠正，规范正文未改动）
> **FROZEN 只能由项目所有者（用户）宣布**：该宣布已于 2026-09-30 完成（PMML v1.0 FROZEN）。本行原为「宣布前」的约束说明，2026-10-01 仅做状态纠正，不构成对规范正文的修订。
> 适用范围：`PCMig.WinUI`（v0.5.0）现有全部 UI。
> 本文件是 **PCMig 项目内部设计语言规范**。文中术语为项目自有命名，**不是** Apple / Microsoft / WinUI 的官方规范；
> 对应的行业概念与底层技术在每节以「对应底层技术」注明。
> 参数来源：`docs/PMML-Implementation-Audit.md`（逐条 `文件:行号` + 原始值）。**本文档不含任何未在代码中出现的数值。**
> 执行短版：`docs/PMML-UI修改硬性规范.md` ｜ 历史差异：`docs/PMML-Legacy-Deviations.md`

---

## 0. 定位与边界

PMML v1.0 的任务是**把当前已经成熟的 UI 命名、描述、冻结**，不是重新设计：

- 冻结对象：材质（Acrylic/Backdrop）、层级（Surface 分层）、光照方向、浮雕、页面过渡、工具浮层变换与锚定、Hover 抬升、Design Token、控件族状态矩阵；
- **不在本轮**：任何颜色/圆角/材质/动画/布局/卡片尺寸的改动；Token 大重构；Dark 补齐；Reduced Motion 落地实现。
- 发现的问题一律登记为 `Legacy Deviation`（历史不统一）或 `PMML v1.0 Known Gap`（尚未覆盖），**不扩大战线**。

---

## 1. 术语体系（正式名称）

| 缩写 | 全称 | 中文 | 职责 | 本文档章节 |
|---|---|---|---|---|
| **PMML** | PCMig Material Motion Language | PCMig 材质运动语言 | 整体设计系统（本文件） | 全文 |
| **DAM** | Desktop Acrylic Material System | 桌面亚克力材质系统 | Backdrop blur / Tint / Transparency / Luminosity / Edge Highlight / Surface / Light-Dark 适配 | §2 |
| **LMDS** | Layered Material Depth System | 分层材质景深系统 | 固定层级 L0–L4，所有 Surface 必须归层 | §3 |
| **DSL-45** | 45° Directional Surface Lighting | 45° 定向表面光照 | 全局唯一虚拟光源：左上→右下约 45° | §4 |
| **ESR** | Subtle Embossed Material Relief | 轻浮雕材质起伏 | 亮边 + 细边框 + 柔阴影 + 材质对比（非夸张 Neumorphism） | §5 |
| **DCST** | Directional Cross-Slide Transition | 方向性交叉滑动过渡 | Step1↔2↔3↔4 主页面过渡 | §6 |
| **OACT** | Origin-Anchored Container Transform | 源点锚定容器变换 | Tool Surface **怎么动**（Translation+Scale+Opacity，源点锚定） | §7 |
| **TAOP** | Trigger-Anchored Overlay Placement | 触发器锚定浮层定位 | 浮层**在哪里**（与 OACT 严格区分） | §8 |
| **MHE** | Material Hover Elevation | 材质 Hover 抬升 | Hover 不等于换色：Translation + Elevation（**禁止 Scale 放大**） | §9 |

**LMDS 层级**（固定五层，禁止新增）：

| 层 | 名称 | 语义 |
|---|---|---|
| **L0** | Backdrop | 窗口背景 / 环境层（唯一允许 Blur 的层） |
| **L1** | Base Surface | 基础承载面（Shell / 页面底卡） |
| **L2** | Elevated Surface | 抬升卡片（页面内容卡、统计卡） |
| **L3** | Interactive Surface | 可交互面（按钮、输入框、可点卡片、导航项） |
| **L4** | Overlay / Tool Surface | 浮层与工具面（Task Picker、Flyout、材质调节、更新日志、Dialog、Tooltip） |

> ⚠ **与代码既有命名不同（Legacy Deviation L-01）**：`Themes/Materials.xaml:2-9` 里的项目自有分层是
> `L0 Mica/Ambient → L1 Shell → L2 Card → L3 Input 内嵌 → L4 Selected/Primary`，**没有独立 Overlay/Tool 层**
> （工具浮层走 `PCMigUtilityAcrylic*`，属 DAM 变体）。PMML 冻结的是**语义层**；代码既有键名保留不动。

---

## 2. DAM — 桌面亚克力材质系统

**对应底层技术**：WinUI 3 `AcrylicBrush`（含 `TintColor` / `TintOpacity` / `TintLuminosityOpacity` / `FallbackColor`）、`SystemBackdrop`（Desktop Acrylic / Mica）、以及项目自定义的**表面材质画刷**。

### 2.1 已冻结的材质画刷（真实值）

| 资源键 | 值 | 用途 |
|---|---|---|
| `PCMigShellMaterial` | `SolidColorBrush #2BFFFFFF` | Shell 层材质（≈16.9% 白） |
| `PCMigCardMaterial` | `SolidColorBrush #AFFFFFFF` | 卡片材质（≈68.6% 白）；Primary/Secondary/Elevated 共用 |
| `PCMigInsetMaterial` | `SolidColorBrush #59FFFFFF` | 内嵌面材质（≈34.9% 白） |
| `SelectedSurfaceBrush` | `AcrylicBrush TintColor #E4F1FF, TintOpacity 0.45, FallbackColor #E4F0FF` | 选中导航面（**真 Acrylic**） |
| `PCMigUtilityAcrylic80` | `AcrylicBrush TintColor #F2F6FF, TintOpacity 0.02, TintLuminosityOpacity 0, FallbackColor #F2F2F2` | 工具浮层（材质调节 / 更新日志） |
| `PCMigUtilityAcrylicInset80` | `AcrylicBrush TintColor #F2F6FF, TintOpacity 0.10, TintLuminosityOpacity 0, FallbackColor #E9EFF8` | 工具浮层内嵌区 |
| `BaseBackdropBrush` | `LinearGradientBrush (0,0)→(1,1)`：`#2E9AB4FF` → `#28947CFF`@0.28 → `#2EC894FF`@0.62 → `#54FF9CD6`@1 | 环境底衬 |
| 环境色（色值 + 强度） | `AmbientBlueColor #8CB4FF`/0.12 ・ `AmbientLavenderColor #BCA4FF`/0.24 ・ `AmbientPinkColor #FFA8CC`/0.22 ・ `AmbientPeachColor #FFD094`/0.09 | 四象限环境光 |

> 材质层级口径（代码注释原文）：**禁止用 `UIElement.Opacity` 做层次**（会把文字/图标/焦点一起透明）——层次由 Tint + 材质画刷表达。

### 2.2 DAM 硬规则（冻结）

- **PMML-R4**：**Blur 只能属于 Surface Background Material**；Foreground（Text / Icon / CheckBox / ComboBox Item / List Item / 任何控件内容）**必须保持锐利**，不得进入 Blur Visual。**L0/L4 是主要 blur-bearing layers，但获准的 DAM Acrylic Variant 可以存在于其它语义层** —— 现有 `SelectedSurfaceBrush`（L3 选中导航面）与 `PCMigUtilityAcrylic*`（工具面）即为既有获准例外。
- **PMML-R13**：任何 Material Blur 不得导致文字/图标模糊。
- 表面材质一律从 §2.1 的既有画刷派生（**PMML-R2**）。

### 2.3 Known Gap

- 覆盖度与 Light/Dark 差异见 `PMML-Legacy-Deviations.md`（本轮只登记，不补齐）。

---

## 3. LMDS — 分层材质景深系统

**对应底层技术**：WinUI 3 `Border` + `Shadow`（`ThemeShadow`）+ 分层 `Translation`（Z 轴抬升）。

### 3.1 已冻结的 Surface Style（真实值，取自 `Themes/Materials.xaml`）

| Style 键 | LMDS 层 | 背景材质 | 边框 | 圆角 | Padding | 阴影 |
|---|---|---|---|---|---|---|
| `ShellMaterial` | L1 | `PCMigShellMaterial` | `PCMigSurfaceRimLightBrush` / 1 | **22** | 18 | `ElevationLow` |
| `PrimarySurface` | L2 | `PCMigCardMaterial` | `PCMigSurfaceRimLightBrush` / 1 | **20** | 20 | `ElevationHigh` |
| `ElevatedSurface` | L2 | `PCMigCardMaterial` | `PCMigSurfaceRimLightBrush` / 1 | **18** | 16 | `ElevationMedium` |
| `SecondarySurface` | L2 | `PCMigCardMaterial` | `PCMigSurfaceRimLightBrush` / 1 | **16** | 16 | `ElevationLow` |
| `InsetSurface` | L2(内嵌) | `PCMigInsetMaterial` | `InsetBorderBrush` / 1 | **13** | 12 | 无 |
| `ListItemSurfaceBrushBorder` | L3 | `ListItemSurfaceBrush` | `BorderNormalBrush` / 1 | **10** | — | 无 |
| `SelectedNavigationSurface` | L3 | `SelectedSurfaceBrush`（Acrylic） | `AccentEdgeBrush` / 1 | **15** | 13,15 | `ElevationMedium` |

### 3.2 圆角 Token（真实值）

`PCMigRadiusInput = 14` ・ `PCMigRadiusStepCard = 16` ・ `PCMigRadiusButton = 12` ・ `PCMigRadiusInset = 13`

### 3.3 硬规则

- **PMML-R3**：所有 Surface 必须明确属于 L0–L4 之一；**不允许**自创 `L2.5` / `L3.7` 之类的新层级。
- 新增 Surface **必须先选择一个既有 Surface Family**（§3.1），不得随手手写一个新的"白色半透明框"（**PMML-R14**）。

---

## 4. DSL-45 — 45° 定向表面光照

**对应底层技术**：`LinearGradientBrush`（对角 `StartPoint 0,0 → EndPoint 1,1`）、`RadialGradientBrush`（`Center` 置于左上）、以及上下缘成对的亮/暗边画刷。

### 4.1 冻结的光源方向

**左上 → 右下，约 45°。**（代码注释原文：「方向与全局一致：光源在左上 → 上缘亮、下缘暗。」）

### 4.2 实现证据（真实资源，逐条可核）

| 资源键 | 值/形态 | 说明 |
|---|---|---|
| `NavCardEmbossTopBrush` | `LinearGradientBrush (0,0)→(1,0)`：`#FFFFFFFF`→`#F2FFFFFF`@0.55→`#D8FFFFFF`@1 | 步骤卡**上缘亮线** |
| `NavCardEmbossEdgeBrush` | `LinearGradientBrush (0,0)→(1,1)`：`#8CFFFFFF`→`#4D46618C`@0.45→`#B33A5478`@1 | 步骤卡**下/右缘暗收边**（与上缘成对） |
| `EdgeHighlightDiagonalBrush` | `LinearGradientBrush (0,0)→(1,1)`：`#FFFFFFFF`→`#8AFFFFFF`@0.34→`#3EFFFFFF`@0.72→`#2AFFFFFF`@1 | 对角高光描边（方向感来源） |
| `EdgeHighlightSoftDiagonalBrush` | 同上结构，峰值 `#E0FFFFFF` | 柔化版 |
| `EdgeHighlightCrispBrush` | `LinearGradientBrush (0,0)→(0,1)`：`#FFFFFFFF`→`#C8FFFFFF`@0.045→`#68FFFFFF`@0.11→`#40FFFFFF`@1 | 上缘 1–2 DIP 清晰亮线（实测参考图有亮度尖峰） |
| `PCMigDAELBrush` | `RadialGradientBrush Center=0.06,0.08 GradientOrigin=0,0 Radius=1.25`：`#FFFFFFFF`→ … | 左上光源的径向衰减（L1/L2 边框高光） |
| `PCMigDAELBrushSubtle` | 同结构，峰值 `#8CFFFFFF` | 步骤卡专用弱化版 |
| `PCMigCornerGlowBrush` | `RadialGradientBrush Center=0,0 GradientOrigin=0,0 Radius=0.62` | 左上角辉光 |

### 4.3 硬规则

- **PMML-R1**：单一光源 DSL-45。**新组件不得自行改变光源方向**（不得出现"右下打光"）。
- 亮边必须与暗边成对出现（只有白线在近白底上不可见——这是本项目实测过的物理原因）。

---

## 5. ESR — 轻浮雕材质起伏

**对应底层技术**：`Border` 细边框 + 边缘高光画刷（§4.2）+ `ThemeShadow` + 材质明暗差。

**构成四件套**：`edge highlight` + `subtle border`（统一 `BorderThickness = 1`）+ `soft shadow` + `material contrast`。

### 5.1 阴影（真实值）

| 资源键 | 类型 | Blur / Opacity / Offset |
|---|---|---|
| `ElevationLow` / `ElevationMedium` / `ElevationHigh` | **`ThemeShadow`**（框架类型） | **由框架决定，项目未定义数值**（`Themes/Materials.xaml` 仅声明三种命名阴影） |

> 如实记录：PMML v1.0 **没有**自定义 Shadow blur/opacity/offset 数值。三档阴影只按"命名语义"冻结（Low/Medium/High），具体参数由 WinUI `ThemeShadow` 提供。

### 5.2 硬规则

- **禁止**大硬阴影、过度塑料质感、高对比浮雕（不是传统 Neumorphism）。
- 新 Surface 的浮雕必须复用 §4.2 与 §5.1 的既有资源。

---

## 6. DCST — 方向性交叉滑动过渡（主页面）

**对应底层技术**：Composition `Vector3KeyFrameAnimation`（`Translation`）+ `ScalarKeyFrameAnimation`（`Opacity`），由 `Presentation/MotionDirector.cs` 驱动。

### 6.1 冻结参数（全部来自 `Themes/Motion.xaml`，逐条真实）

| Token | 值 | 含义 |
|---|---|---|
| `PCMigMotionPagePushDuration` | `0:0:0.42` | 整页 Push 总时长 |
| `PCMigMotionPageEnterDistance` | `10` | 进入位移（DIP） |
| `PCMigMotionPageSlideDistance` | `26` | **已废弃**（U57 起改为整页视口高度的 Push；兼容保留） |
| 方向常量 | `DirectionForward = 1` / `DirectionBackward = -1` | 前进/后退方向 |
| **缓动（生效）** | **`CubicBezier (0.10, 0.90)(0.20, 1.00)`** | `MotionDirector.cs:370`（`AnimateSlideFade`，DCST 实际运行路径） |
| 缓动（**legacy / dead-path / reserved**） | `PCMigMotionEaseStandard` / `PCMigMotionEaseExit` → `CubicEase EaseOut/EaseIn`（`MotionDirector.cs:98-108`，仅 `:186` 死路径 `PlayPanelExit` 使用） | **不是** DCST 生效 easing；不得写成当前实现 |
| 兜底完成回调 | 时长 + **140 ms** | 动画回调丢失时的兜底 `Finish` |
| 时长缩放 | `居留时长 × scale` | 由 `UniformScaleHost` 语义决定（Canonical DIP，**不得再乘 ApplicationUIScale**） |

**生效实现（主控回读审计确认）**：整页**视口高度**的纵向 Push，**Opacity 恒为 1（无淡入淡出）**，`0.42s`，**无 Scale**，forward/backward 仅符号相反（对称），**接管式可中断**。
> ⚠ `Themes/Motion.xaml:14-15` 的注释「旧页上移淡出、新页自下方进入淡入」**与实现不符**，已登记为 Legacy Deviation **L-05**。PMML 以**生效实现**为准。

### 6.2 硬规则

- **PMML-R5**：页面导航一律 **DCST**。新主页面默认必须复用 DCST；不得出现"这个页面自己 Fade、那个页面自己 Scale、第三个从右边飞进来"。
- 允许的中断行为：由 `MotionDirector` 统一调度（含兜底），页面不得自行接管。

---

## 7. OACT — 源点锚定容器变换（Tool Surface 怎么动）

**对应底层技术**：`ShapeVisual` + `Matrix4x4 TransformMatrix`（U62 Fluid Zoom，matched-geometry）+ Composition 动画；旧路径为 `Translation`/`Scale`/`Opacity` 组合。

### 7.1 冻结参数（真实 Token）

**当前主路径 —— U62 Fluid Zoom（Matched-Geometry，被点击的入口本身连续 morph 成面板）：**

| Token | 值 |
|---|---|
| `PCMigFluidZoomOpenDuration` | `0:0:0.36` |
| `PCMigFluidZoomCloseDuration` | `0:0:0.30` |
| 时间轴（**权威：`FluidZoomTransitionCoordinator.ApplyFrame()`**） | 见下方相位表（`FluidZoomTransitionCoordinator.cs:355-404`） |

**相位表（逐帧实测；`ApplyFrame()` 是唯一驱动路径）**

| 通道 | 相位 | 实现 |
|---|---|---|
| fill（表面填充，初始 factor **0.22**） | **0 → 32%** | `fillFactor = 0.22 + 0.78 * Segment(t, 0.00, 0.32)`（`:371`） |
| stroke（边缘光） | **10 → 55%** | `Segment(t, 0.10, 0.55)`（`:372`） |
| real panel（真实面板接管） | **55 → 100%** | `Segment(t, 0.55, 1.00)`（`:389`） |
| shell fade（Shell 让位） | **80 → 100%** | `_shellOpacityFrom * (1 - Segment(t, 0.80, 1.00))`（`:373`） |
| source（触发入口） | **0→22% 淡出 ／ 22→70% 隐藏 ／ 70→88% 恢复** | `:402-404` |
| geometry（位置/尺寸/形状 morph） | 全程 | **`EaseOutCubic(t)`**（`:360`） |
| 其它通道 | 全程 | **staged linear Segment**（分段线性，非单条曲线） |

> ⚠ `Themes/Motion.xaml` 里「0→45% / 30–72% / 55%」的旧注释**不是**实际参数、**不再引用**（登记 Legacy Deviation **L-03b**）。
> fallback 路径（Origin Reveal）的缓动单独登记：`MotionDirector.cs:565` 的 `CubicBezier (0.16, 1.0)(0.30, 1.0)`。
> 「Spring-like」**仅作视觉感受描述**，不是 easing 技术名，更**不是** Spring Physics。

**Fallback 路径 —— Origin-aware Reveal（Token 保留，已被用户否决为主视觉）：**

| Token | 值 |
|---|---|
| `PCMigMotionUtilityRevealDuration` | `0:0:0.26` |
| `PCMigMotionUtilityDismissDuration` | `0:0:0.19` |
| `PCMigMotionUtilityRevealOffset` | `7` |
| `PCMigMotionUtilityRevealScale` | `0.945` |
| `PCMigMotionUtilityDismissScale` | `0.985` |
| `PCMigMotionUtilityDismissOffset` | `5` |
| `PCMigUtilityPanelAnchorGap` | `5`（PanelTop − AnchorBottom 的统一间距） |
| 兜底完成回调 | 时长 + **120 ms** |

### 7.2 冻结语义

- **Transform origin / Trigger origin**：容器从**真实触发入口**下方展开（非中央凭空出现）；
- **reverse close**：关闭方向略短（收回要更干脆）；
- 当前实现为**插值缓动**（`CubicEase`），文档只称 **Spring-like Easing**，**不得**声称为真实 Spring Physics。

### 7.3 硬规则

- **PMML-R6**：Tool Overlay 一律 **OACT + TAOP**（怎么动 + 在哪里，两者都要明确）。
- 两个 Utility Panel（材质调节 / 更新日志）与各自触发入口之间的间距**只允许用 `PCMigUtilityPanelAnchorGap`**，不得分别手调 Margin。

---

## 8. TAOP — 触发器锚定浮层定位（浮层在哪里）

**对应底层技术**：`FlyoutBase.Placement`（`BottomEdgeAlignedLeft` 等）+ `Popup` 锚定；**不用**魔法 `VerticalOffset`、负 Margin、`TranslateY`、运行后坐标校正。

### 8.1 已冻结的归属表

| 浮层 | 锚定 | 归属 |
|---|---|---|
| **PMML Task Picker**（已有任务） | `Flyout Placement="BottomEdgeAlignedLeft"`，从控件**下方向下**展开 | TAOP ✅（本轮正式参考实现） |
| 材质调节面板（Developer Visual Tuning） | 从标题栏触发入口下方展开（`AnchorGap = 5`） | TAOP + OACT |
| 更新日志面板 | 同上 | TAOP + OACT |
| 后续诊断中心 | 必须复用 TAOP | 规划中 |
| 线程数 `ComboBox` | **原生 ComboBox 内部定位**（非 TAOP） | 原生行为，已实测落位正确（P0/`PcmigComboBoxRoll` 场景） |

### 8.2 硬规则

- **PMML-R12**：Popup / Flyout **必须明确 Placement ownership**，不得靠系统默认行为碰运气。
- **PMML-R10**：禁止用负 Margin / 随意 Translate / 魔法 Scale 掩盖错误布局。
- **已登记的失败教训**：为修正原生 ComboBox 浮层位置而加的 `Popup.VerticalOffset` 后置校正**实测无效**，已删除（详见审计文档 §TAOP）。

---

## 9. MHE — 材质 Hover 抬升

**对应底层技术**：`ElementCompositionPreview.SetIsTranslationEnabled` + `Vector3KeyFrameAnimation`（`Translation`），由 `Presentation/InteractionFeedback.cs` 驱动。

### 9.1 冻结参数（真实 Token）

| Token | 值 | 场景 |
|---|---|---|
| `PCMigMotionHoverEnterDuration` | `0:0:0.13` | Hover 进入 |
| `PCMigMotionHoverExitDuration` | `0:0:0.15` | Hover 离开 |
| `PCMigMotionPressedDuration` | `0:0:0.10` | 按下 |
| `PCMigMotionHoverLiftCard` | `2`（DIP） | 卡片上浮 |
| `PCMigMotionHoverLiftButton` | `1.3`（DIP） | 按钮上浮 |
| `PCMigMotionPressedSinkCard` | `0.5`（DIP） | 卡片下沉 |
| `PCMigMotionPressedSinkButton` | `0.6`（DIP） | 按钮下沉 |

**冻结的设计口径（代码注释原文）**：主反馈只能是 **Translation / Elevation**；**禁止用 Scale 放大**（会让文字发虚、边缘变形）。单位 = Canonical DIP，与 `UniformScaleHost` 同一坐标系 ⇒ **不得再乘 `ApplicationUIScale`**（否则双倍位移）。

### 9.2 硬规则

- **PMML-R7**：Hover = MHE 家族；不得退化成"只是换个底色"。
- 新交互 Surface 必须复用 MHE 家族参数。

---

## 10. PMML Motion Family（动画体系冻结）

| 用途 | 家族 | 参数来源 |
|---|---|---|
| Page Navigation | **DCST** | §6（`Themes/Motion.xaml`） |
| Tool / Overlay Open-Close | **OACT + TAOP** | §7 + §8 |
| Hover / Pressed | **MHE** | §9 |
| Standard State Transition | **PMML State Transition**（控件 VisualState；时长取 `PCMigMotionDurationFast/Normal/Slow`＝`0.14/0.20/0.26`，导航选中态取 `PCMigMotionDurationSelection`＝`0.16`） | §11 + 审计文档 |
| Overlay 下拉（ComboBox） | **Roll Reveal**（自上而下卷帘：`RollScale.ScaleY 0→1`，Opened `0.20s` EaseOut / Closed `0.13s` EaseIn） | `Themes/PcmigComboBoxRoll.xaml` |
| 进度扫光 | **Reserved Token / Not Implemented**（`PCMigMotionProgressSweepDuration = 0:0:1.60` **无消费者**） | Known Gap **G-04**（详见下方分栏） |

**禁止（冻结）**：任何页面/面板/窗口**自行发明**进入方式（自 Fade、自 Scale、从右侧飞入），除非获得显式授权（**PMML-R15**）。

---

## 11. Design Token 冻结

### 11.1 分类（PMML Token 命名空间）

```
PMML.Material.*     材质画刷与 Acrylic 参数（§2.1）
PMML.Surface.*      Surface Style 与层级（§3.1）
PMML.Corner.*       圆角（PCMigRadius* + Surface 自带圆角）
PMML.Border.*       边框厚度与边缘画刷（§4.2）
PMML.Shadow.*       ElevationLow / Medium / High（ThemeShadow）
PMML.Light.*        光源方向与光照画刷（DSL-45）
PMML.Motion.*       Duration / 位移 / 缩放（Motion.xaml）
PMML.Easing.*       standard / exit（→ CubicEase EaseOut / EaseIn）
PMML.Spacing.*      各 Surface 的 Padding 与 `PCMigUtilityPanelAnchorGap`
PMML.Typography.*   Typography.xaml 的 Style 键
PMML.Opacity.*      Ambient*/FutureNavigation* 等强度常量
```

### 11.2 已冻结的 Token 实例（示例，完整清单见审计文档）

`PMML.Motion.Duration.Fast = 0.14` ・ `Normal = 0.20` ・ `Slow = 0.26` ・ `Selection = 0.16` ・ `PagePush = 0.42` ・ `FluidZoomOpen = 0.36` ・ `FluidZoomClose = 0.30` ・ `UtilityReveal = 0.26` ・ `UtilityDismiss = 0.19` ・ `HoverEnter = 0.13` ・ `HoverExit = 0.15` ・ `Pressed = 0.10` ・ `ProgressSweep = 1.60`
`PMML.Corner.Input = 14` ・ `StepCard = 16` ・ `Button = 12` ・ `Inset = 13`
`PMML.Light.Direction = 45°（左上→右下）`
`PMML.Material.Shell = #2BFFFFFF` ・ `Card = #AFFFFFFF` ・ `Inset = #59FFFFFF`

### 11.3 硬规则

- **PMML-R14**：新 UI 优先复用现有 Resource / Token / Style。
- 同类值存在**多个接近但不相同**的情况下：**不要立即大规模重构**，登记为 `Legacy Deviation`（见 `PMML-Legacy-Deviations.md`）。
- Token 冻结**不得破坏成熟 UI**：本轮不改任何现有值。

---

## 12. Control Families（控件族冻结）

v1.0 冻结以下族；每族须记录：Surface 层 / 材质 / Typography / Border / Corner / Shadow / Normal / Hover / Pressed / Disabled / Focus / Motion / Light / Dark。
**逐族真实实现（含 `文件:行号`）见 `PMML-Implementation-Audit.md` §Control Families。**

Primary Button ・ Secondary Button ・ Icon Button ・ TextBox ・ PasswordBox ・ ComboBox ・ **Task Picker** ・ ToggleSwitch ・ CheckBox ・ Navigation Item ・ Card ・ Tool Surface ・ Flyout / Popup ・ Dialog ・ Tooltip ・ Progress ・ Log Row

---

## 13. PMML Task Picker（第一个正式参考实现）

| 项 | 冻结内容 |
|---|---|
| 结构 | 收起态 Presenter（`Button` + `PCMigTaskPickerPresenterStyle`）→ `Flyout Placement=BottomEdgeAlignedLeft` → `ListView`（`ExistingJobsList`） |
| 收起态 | 单行、`TextTrimming=CharacterEllipsis`、高度 = `ComboBoxMinHeight`（≈32）、**不被任务文本撑高** |
| 展开态 | 两行/项（`JobId` / `PhaseText` + `Percent`），`ListView.MaxHeight=320` 自带滚动 |
| 材质/圆角/阴影/选中 | 全部取自 ComboBox 同套主题资源（`ComboBoxDropDownBackground`、`OverlayCornerRadius`、`ComboBoxItemBackground*`、`ComboBoxItemPillFillBrush`） |
| 点击语义 | **对象化**：`ClickedItem as JobSummary` → `JobDir` → `AdoptExistingJobAsync`；**零索引运算** |
| 显示 vs 载入 | 收起态显示（有 CurrentJob 显示它；否则第一条真实任务作 **preview**）**不等于**已载入；不写任何 `SelectedIndex` |
| TAOP | 从控件底边向下展开（实测：控件底边 y=383 → 浮层顶边 y=389，间距 6px） |
| 归属 | **TAOP 的实际参考之一**；PMML-R12 的正面案例 |

---

## 14. Accessibility（Reduced Motion）

**现状（如实，已更正审计前假设）**：Reduced Motion **部分存在** ——
- **存在**：`MotionDirector.SystemAnimationsEnabled` 读 `Windows.UI.ViewManagement.UISettings().AnimationsEnabled`（`Presentation/MotionDirector.cs:56-79`，异常时保守按 `true`）；
- **不存在**：`ReduceMotion` / `AdvancedEffectsEnabled` / `AccessibilitySettings` / 应用内开关；
- **覆盖缺口**：只覆盖 C# Composition 路径；**XAML Storyboard 与 `BrushTransition`/`ScalarTransition` 无闸门**（`Views/StepNavigationControl.xaml:82,102,114`）⇒ 登记 **Known Gap G-02**。

**规定（未来行为，本轮不重构）**：

```
Reduced Motion:
  Translation ↓
  Scale ↓ / disabled
  Opacity retained
  Functional state unchanged
```

**PMML-R8**：动画不得成为业务逻辑依赖；动画被关闭/失败不得改变任何业务状态。
`Accessibility` 在 Gate 中当前应填 **Known Gap**（除非改动本身实现了 Reduced Motion）。

---

## 15. Light / Dark

- **PMML-R9**：Dark Mode **不是**另一套设计语言 —— 必须是**同一 Surface Family + 不同 Theme 参数**。
- **当前实现现状（确证）**：**零 Dark 覆盖** —— `ThemeDictionaries` 全仓 **0 处**，`RequestedTheme` 仅在 `Themes/TextInputTemplates.xaml:150-152` **强制 Light**，34 个颜色键只按浅色定义 ⇒ 登记 **Known Gap G-01**（最大缺口）。
- **不为 Freeze 强行补齐**。

---

## 16. 硬性规则汇总（PMML-R1 … R15）

| 规则 | 内容 |
|---|---|
| **PMML-R1** | 单一光源：DSL-45（左上→右下）。 |
| **PMML-R2** | 单一材质族：新 Surface 必须从 DAM 派生。 |
| **PMML-R3** | 所有 Surface 必须属于 LMDS L0–L4。 |
| **PMML-R4** | Blur 只能属于 Surface Background Material；Foreground 必须锐利。L0/L4 为主要 blur-bearing layers，获准的 DAM Acrylic Variant 可存在于其它语义层。 |
| **PMML-R5** | 页面导航 = DCST。 |
| **PMML-R6** | Tool Overlay = OACT + TAOP。 |
| **PMML-R7** | Hover = MHE。 |
| **PMML-R8** | 动画不得成为业务逻辑依赖。 |
| **PMML-R9** | Light / Dark 属于同一设计系统。 |
| **PMML-R10** | 禁止负 Margin / 随意 Translate / 魔法 Scale 掩盖错误布局。 |
| **PMML-R11** | 同一状态不允许有多个 UI 写入者（TreeView Row Push 教训）。 |
| **PMML-R12** | Popup / Flyout 必须明确 Placement ownership。 |
| **PMML-R13** | 任何 Material Blur 不得导致文字/图标模糊。 |
| **PMML-R14** | 新 UI 优先复用现有 Resource / Token / Style。 |
| **PMML-R15** | 偏离 PMML 必须显式授权。 |

---

## 17. PMML Compliance Gate

见 `docs/PMML-UI修改硬性规范.md` §四（逐项声明模板 + FAIL 处理）。任一项 **FAIL 不允许直接合入**。

---

## 18. Legacy Deviations / Known Gaps 索引

- 历史不统一（本轮不改）：`docs/PMML-Legacy-Deviations.md`
- v1.0 已知缺口：同文件 `Known Gap` 分节（Reduced Motion、Dark 覆盖、ThemeShadow 无数值、Scattered 材质/圆角等）

---

# 附录 A：UI Closure 视觉基础标准（PMML v1.0 §19–§24，2026-10-05 增补）

> **来源与授权**：由项目所有者于 2026-10-05 下达的 UI Closure 指令（`docs/UI-CLOSURE-ISSUES-20261005.md`，R15 授权出处见该文件 §118）。
> **性质**：本附录收录**跨页面可复用的基础视觉 / 动效 / 布局标准**，不收录任何一次性问题记录。
> 写法纪律：每条都写成「以后所有同类 UI 都必须遵守」，而不是「这次修了什么」。参数与 `docs/PMML-Implementation-Audit.md` 的实现侧逐条对应。
> **参数集中原则**：本节出现的数值都是**上限/下限/基准**。调用点不得自行发明数值：能落在 Token 的必须走 Token，不能的必须回到本节登记后才可使用。

## §19 几何基础标准

### §19.1 Corner Radius System（收敛，不新增孤立值）

任何新 Surface / 控件族必须归入下表某一档，**不得**引入表中不存在的孤立圆角值（如 6/7/11/15/18）。层级与材质景深（LMDS）同源：

| 档位 | Token | 值(DIP) | 适用 |
|---|---|---|---|
| Secondary / Step Card / **Overlay** | `PCMigRadiusSecondarySurface` / `PCMigRadiusStepCard` / **`PCMigRadiusOverlay`** | 16 | 次级材质卡、步骤卡、**Flyout / Popup / Dropdown 面板容器** |
| Inset Surface | `PCMigRadiusInset` | 13 | 内嵌面板（提示卡等） |
| Input / Picker | `PCMigRadiusInput` | 14 | TextBox、ComboBox 收起态、下拉选择器、路径框 |
| Button | `PCMigRadiusButton` | 12 | 所有按钮 |
| **List Item** | **`PCMigRadiusListItem`** | 10 | **列表条目、行内 chip（与 `ListItemSurfaceBrushBorder` 同值）** |
| Badge | `PCMigRadiusBadge` | 9 | 徽章 |

- **PMML-R21（新增）**：控件**收起态**与**展开态**必须使用同一族圆角语义：收起态走 Input 档（14），展开的浮层容器走 Overlay 档（16）。禁止一端继承系统 `ControlCornerRadius`(4)、另一端继承 `OverlayCornerRadius`(8) —— 系统默认值与本设计语言不属同一体系。
- 轨道类（ProgressTrack / ProgressFill）使用半径 = **高度 ÷ 2**（当前高 12 ⇒ 半径 6），不单列 Token。

### §19.2 Spacing 与 Safe Gap

| 语义 | 值(DIP) | 说明 |
|---|---|---|
| 页根行间距 | 12 | 四个页面根容器 |
| 卡内元素间距 | 10 | 卡内纵向节奏 |
| 底栏列间距 | 18 | 底栏 Grid |
| 标准段间距（Section Gap） | 12 | 侧栏内相邻区块之间的最小安全间隙；与 `HintCard.Margin.Top` 同源 |

- **PMML-R22（新增）**：任何低于 8 DIP 的间距都必须注释说明理由（紧凑态 chip / 图标与文字配对例外）。低于该阈值的元素**不得**用于两个可交互目标之间。

### §19.3 Critical Numeric Label Geometry（关键数字标签几何）

**关键数字标签** = 百分比、已传/计划字节、速率、ETA、对象进度等"用户扫一眼就要读到全部字符"的字段。

- **PMML-R16（新增）**：关键数字标签**禁止** `TextTrimming`（含 `CharacterEllipsis`），宽度必须按**最长合法串**预留并可容纳完整字符（含 `%`、单位、空格）；数值位数变化**不得**推动相邻的 Data / Speed / ETA / Action 区域。
- 预留宽度必须由**程序化测量**决定（字体自然宽度 + 余量），不得手调。已登记的基准（100% DPI，Microsoft YaHei UI）：

| 字段 | 字号/字重 | 最长合法串 | 实测宽 | 预留 |
|---|---|---|---|---|
| 底栏百分比 | 17 Bold | `100.0%` | 61.0 px | **68** |
| 底栏 ETA | 13 Regular | `约 23 小时 59 分` | 100.0 px | **112** |

- 新增关键数字字段时，必须补测并在此表登记后才可预留宽度；**禁止**四张统计卡各自加 Magic Margin 来"躲开裁切"。

### §19.4 No Text Clipping（不得裁切墨迹）

- **PMML-R18（新增）**：**行高必须 ≥ 字体的自然行高 + 余量**，只按「字号 × 1.4」推导是**不充分**的 —— 实测存在"配置值恰等于自然行高（零余量）"与"配置值小于自然行高（真裁切）"两种情况。Critical Numeric 与汉字混排的标签，必须在此口径下复核。
- 自然行高基准（Microsoft YaHei UI，ascent+descent，实测量）：11→15、13→18、17→23、**20→28**、40→54。
- 已登记的 LineHeight（→ 为增补后值）：Footer 17→**26**、FooterValue 13→**20**、TotalPercent 40→**60**、StatValue 20→**32**。
- **禁止**通过"给每个 TextBlock 单独设 Height/MinHeight"来规避；统一走 `Themes/Typography.xaml` 的样式族。
- 验收必须在 **100% / 125% / 150% DPI** 下确认无上下裁切，且视觉中心与基线一致（`UseLayoutRounding` 开启时尤其要复核亚像素舍入）。

## §20 排版与光学基线

### §20.1 Metric Card Typography / Optical Baseline

- **PMML-R23（新增）**：同一行内的 Metric Card 必须**共享同一套数值/标签样式与垂直基线**：数值用 `PCMigTextStatValue`，标签用 `PCMigTextStepSubtitle`，**不得**给单张卡单独设 FontSize / FontWeight / Padding / Margin 来对齐。
- 卡内节奏：`Padding=14` + `StackPanel Spacing=4`；标签在上、数值在下；数值 `TextWrapping=NoWrap` + 固定行高。
- **对齐口径**：以「标签墨迹上沿 → 卡顶距离」为不变量（实测基准 59 px @ 整窗缩放 1.0）。新增统计卡必须复核该距离与同排其它卡一致（允许 ±2 px）。
- 诊断提示：截图标注（红框/黑块/箭头）会覆盖真实边缘。**判定裁切必须基于未遮挡的对照截图或程序化像素测量**，不得仅凭带标注的图下结论（本项目曾据此证伪一次"文字被裁切"的误判）。

## §21 布局隔离与锚定

### §21.1 Footer Layout Isolation（底栏动态内容与动作区隔离）

- **PMML-R24（新增）**：底栏动态数值区（百分比 / 已传·计划 / 速率 / ETA）**不得**改变动作区（开始 / 暂停 / 停止 / 恢复）的 X 位置。隔离手段优先级：
  1. 关键数字按 §19.3 预留固定宽度；
  2. 单位/位数变化造成的伸缩由**中间的弹性区**（进度轨道列）吸收，不得裁切关键数字、不得压缩动作按钮；
  3. 动作按钮保持固定宽度与固定顺序（查看 / 开始 / 暂停 / 停止 / 恢复各自 `Width` 固定）。
- 验收字符串集：百分比 `0.0% / 9.9% / 99.9% / 100.0%`；字节 `MB / GB / TB` 三档；ETA `— / 约 5 秒 / 约 59 分 59 秒 / 约 1 小时 20 分 / 约 23 小时 59 分`。

### §21.2 Anchored Popup Alignment（锚定浮层几何对齐）

- **PMML-R20（新增）**：锚定浮层（Flyout / Popup / Dropdown 面板）的**内容 Border 左右几何边界必须与锚点控件一致**；默认同宽。阴影允许视觉外溢，但**不计入内容边界**。
- 实现纪律：
  - 宽度**由锚点驱动**（`anchorWidth − flyoutChrome`），不得绑定固定数值或用负 Margin / 魔法偏移凑；
  - **`flyoutChrome` = FlyoutPresenter 的 `Padding` 左右 + `BorderThickness` 左右**（当前 `2+2+1+1 = 6 DIP`）。该常量必须带注释说明其构成，禁止出现身份不明的补偿数字；
  - 禁止给浮层列表设 `MinWidth/MaxWidth` 硬夹取（会与锚点宽度打架）；上限只用于防御极端视口。
- 验收：同一锚点在 100%/125%/150% DPI、窗口缩放与滚动状态下，浮层左右边界与锚点差值均为 0（±1 px 抗锯齿容差）。

## §22 进度视觉语言（Progress Visual Language）

> 本节收敛此前散落在 §10/§12/§13 的四处进度条描述；对外行为以本节为准，实现参数以 `docs/PMML-Implementation-Audit.md` 的 Progress 族为准。

### §22.1 真值与呈现的分工

- 进度条由三个**互相独立**的东西构成：**数据真值**（引擎派发的 `ProgressTruthSnapshot`）、**渲染宽度**（`InsetClip.RightInset` 标量）、**装饰物**（前沿柔光 / 扫描高光 / 粒子）。
- **PMML-R25（新增）**：填充元素**常驻满宽**，可见长度只由裁剪标量表达；**禁止**按帧写 `Width` / `Canvas.Left` / `Margin` / `Clip.Rect` 等布局或几何属性来驱动进度动画（`Width` 是布局属性，60 fps 插值 = 每秒 60 次布局）。

### §22.2 Leading Edge Glow（前沿柔光）

- **PMML-R19（新增）**：装饰物必须裁剪或衰减排布在**已完成区**内，**禁止**越界覆盖未完成区、文字与百分比。
- 前沿柔光登记参数：带宽 **26 DIP**、亮峰距带左端 **9 DIP**（使峰值恰落在已完成区右缘）、峰值 alpha **0x5A**、色相取项目 Ambient 蓝紫（`#8CB4FF` / `#BCA4FF`）、右侧**渐隐至全透明**（因此视觉外溢部分不可见，不构成越界）。
- **挂载位置纪律**：柔光挂**轨道宿主**子树 —— 填充元素的右边缘被裁成硬边，柔光挂在那里会被切平。

### §22.3 Sweep / Shimmer（扫描高光）

- 登记参数：带宽 **44 DIP**、峰值 alpha **0x1E**（约 12%，克制）、渐变 `透明 → 淡白/淡蓝 → 透明`、单程时长取 Token **`PMML.Motion.Duration.ProgressSweep = 1.60s`**（`Themes/Motion.xaml` 的 `PCMigMotionProgressSweepDuration`）。
- **挂载位置纪律**：扫描高光挂**填充元素**子树 ⇒ 自动被裁剪在已完成区内，绝不扫进未完成区。
- 禁止"100% 后无限 shimmer"（见 §23.2）。

### §22.4 Progress Particle Motion（粒子流）

- 登记参数：粒子数 **8**（低密度）、半径 2.0 / 1.65 / 1.3 DIP 三档、颜色在 Ambient 蓝与蓝紫之间交替、透明度峰值 **0.26**、单粒子周期 **0.90s**、错相方式 = 负 `DelayTime`、活动带宽度 **34 DIP**。
- **几何纪律**：粒子容器整体跟随**真实前沿**（与填充前沿使用同一组起止值、同一时长并行插值），粒子只在**前沿往后 34 DIP 的带内**流动 ⇒ 永远不会跑进未完成区。
- **形态纪律**：低密度柔和亮点，**不得**呈火花、星空、闪粉、噪点；禁止高密度粒子与每帧新建 Composition 对象。

### §22.5 Progress State Motion（进度装饰的状态映射）

- **PMML-R26（v0.5.0 起按 R35 修订）**：进度装饰在 `Running` / `Holding` / `Warning` / `Preparing` 全开；**`Paused` / `Interrupted`（可续传）保留低强度活性**（Push Band 降速降亮、粒子数量与流速降到 40~60%、Halo 保留但略降）。**只有 `Failed` / `Completed` 才是真正终止态**，必须立即落到真值并关闭装饰。
- 「暂停时粒子仍前流」「暂停时扫描高光继续扫」曾按旧口径被定为**视觉撒谎**（用户指令 §23 假修复清单）；**该定性已被 v0.5.0 用户最终要求取代**：用户明确要求「Pause 时 Progress Head / 百分比 / 字节完全冻结，但粒子、Push Band、Halo 不要全部熄灭，只降低活性」。⇒ 判据从"暂停必须全停"改为"暂停必须冻结**事实**、保留**活性**"。真正禁止的是暂停时**进度数字或 Head 继续爬**（那才是撒谎）；装饰层永远不得改变 Value / Percent / ConfirmedBytes。

## §23 运动与入场

### §23.1 Progress Motion / Smooth Interpolation（平滑插值）

- **PMML-R17（新增）**：**数据真值与视觉插值必须解耦**。UI 在 Composition 层做高帧率平滑，业务快照**不需要** 60 Hz；动画只能插值**真实的新旧值之间**。
- 登记参数：追赶上限 **轨道宽度的 55% / 秒**、单段时长 **60 – 400 ms**、段内**线性**（不加 easing）。
- **为什么不加 easing**：`InsetClip` 无可回读的当前动画值，新一段的起点必须用「上一段起止值 + 时长 + 时间戳」精确复现 —— 带 easing 就无法精确复现，会造成**回跳**。
- **禁止**（用户指令 §23 假修复清单）：预测下一进度、外推、卡在 9x% 自爬到 99%、随机加进度、动画值回写业务、完成态不走满 100%。
- 系统关闭动画（Reduced Motion）时直接 `SnapTo` 真值；**装饰物连对象都不建**，业务状态零变化。

### §23.2 Expandable Panel Motion（可扩张面板动画）

- **PMML-R27（新增）**：任何会随内容增长的面板必须同时满足三件事：
  1. **有上界**：增长不得碰 / 压 / 穿相邻区块；上界 = 「容器可用高度 − 上方区块高度 − 标准段间距」，并保留下界（当前提示卡下界 160 DIP：标题行 + 一行状态 + 署名行）；超出后由面板**内部**轻量滚动消化；
  2. **扩张/收缩必须是动画**，不得一帧跳高/缩回，也不得引发 Workspace 大范围抖动：时长 **180 ms**、`CubicEase/EaseOut`、目标差 < 0.5 DIP 不重播；
  3. 动画属性必须是**参与布局的高度**（XAML `Height`）。**禁止**用 Composition `Visual.Size/Offset` 冒充面板扩张 —— 它只改渲染尺寸、不参与布局，会导致父容器不重排、命中测试错位、滚动条长度错误。
- 禁止反向监听面板自身 `SizeChanged` 反推上界（面板高度正是被上界约束的 ⇒ 形成回环）。

### §23.3 Text Entrance Motion（文本/状态入场）

- **PMML-R28（新增）**：状态文本"出现"时统一使用轻入场：`Opacity 0→1` **+** 垂直位移（Option B：`Offset (0,6,0) → (0,0,0)`）或水平位移（Option A：`TranslateX −8…−12 → 0`），时长 **170 ms**，**不 bounce、不 overshoot、不大位移**。
- **触发纪律（关键）**：只在「折叠 ↔ 显示」或「文本内容真正变化」时播一次；**同一句话重复抵达绝不重播** —— 否则会退化为"每两秒自播"的干扰源。
- 覆盖范围：提示卡各通道新消息、连接中/生成计划/复制 object/当前文件变化/日志新增/成功·Warning/状态标题切换。
- **降级**：`Reduced Motion` 关闭时直接显示终值；入场动画全部包在 try/catch 中，**装饰失败不得影响文本内容本身**。

### §23.4 Log / Status Item Entrance

- 日志行与状态项沿用 §23.3 同一套 Token 与触发纪律，**不得**各自发明时长；列表新增项只对**新增的那一项**播放入场，已有项不重播。

## §24 Motion 性能规则

- **PMML-R29（新增）**：动画必须是 GPU-friendly 的：
  - 优先 **Composition Layer**（`Offset` / `Opacity` / `Clip` / `Scale`）与原生 ThemeTransition；
  - **禁止**高频 `DispatcherTimer` 改 `Width`、每帧触发大量 XAML layout、每帧创建/销毁 Composition 对象、主线程 60 Hz 业务轮询、高密度粒子；
  - Composition 对象**在挂载时一次性创建**，稳态零创建；尺寸变化只改 `Size` 并重启动画；
  - `Forever` 动画必须配有停止路径（状态映射见 §22.5）。
- **PMML-R30（新增）**：动画属性名是字符串（`StartAnimation("Offset.X", …)` 等），**拼错会静默不生效**。新增动画必须至少有一次真机可播放证据（截图/短捕获或 UIA + 像素证据），不得只凭代码存在就宣称已实现。
- 环境依赖：`Microsoft.WindowsAppSDK 2.5.1`；`Microsoft.UI.Composition` 与 `Windows.UI.Composition` 是**两套类型不可混用**；一个 XAML 元素**只能挂 1 个 child visual**（多处装饰必须合并到同一 `ContainerVisual` 下）。
- **PMML-R31（新增）**：禁止为动画引入重量级第三方依赖（社区工具包需先评估），业务核心不得为纯视觉问题而改动。

---

## 附录 A 规则索引补充（接 §16）

| 规则 | 内容 |
|---|---|
| **PMML-R16** | 关键数字标签禁止 Trimming；宽度按最长合法串程序化预留；数值位数变化不得推动相邻元素。 |
| **PMML-R17** | 数据真值与视觉插值解耦；动画只插值真实新旧值；禁止预测/外推/自爬/回写业务。 |
| **PMML-R18** | 行高 ≥ 字体自然行高 + 余量；Critical Numeric 在多 DPI 下不得顶裁。 |
| **PMML-R19** | 装饰物必须裁剪或衰减排布在已完成区内；Reduced Motion 可关且不改变业务状态。 |
| **PMML-R20** | 锚定浮层内容边界与锚点左右几何一致；阴影可外溢但不计入内容边界。 |
| **PMML-R21** | 控件收起态与展开态使用同一族圆角语义（Input / Overlay），禁止继承系统默认圆角。 |
| **PMML-R22** | 间距 < 8 DIP 必须注释理由；不得用于两个可交互目标之间。 |
| **PMML-R23** | 同行 Metric Card 共享同一套数值/标签样式与垂直基线，禁止单卡私有排版。 |
| **PMML-R24** | 底栏动态数值不得改变动作区 X 位置；伸缩由中间弹性区吸收。 |
| **PMML-R25** | 填充常驻满宽，可见长度只由裁剪标量表达；禁止按帧写布局/几何属性驱动进度。 |
| **PMML-R26** | 进度装饰在 Running / Holding / Warning / Preparing 全开；**Paused / Interrupted 保留低强度活性**（R35）；仅 Failed / Completed 立即落真值并关装饰。暂停时**进度事实必须冻结**（数字/Head 自己爬 = 撒谎），但活性材质不得全部熄灭。 |
| **PMML-R27** | 可扩张面板必须有上界（含下界与内部滚动）、有动画、用参与布局的属性。 |
| **PMML-R28** | 文本入场统一轻动效与触发纪律（只在折叠↔显示或真变化时播一次）。 |
| **PMML-R29** | 动画必须 GPU-friendly；Composition 对象一次性创建；Forever 必须可停。 |
| **PMML-R30** | 字符串属性名的动画必须有真机可播放证据；拼错会静默失效。 |
| **PMML-R31** | 禁止为动画引入重量级依赖；业务核心不得为纯视觉问题改动。 |

---

# 附录 B：PCMig Immersive Transfer Progress（Round-3，2026-10-05）

> **编号口径声明（重要，避免两套编号混淆）**：本文件 **附录 A** 里的 `PMML-R16 … R31` 是 v1.0 时期的补充编号；`docs/PMML-UI修改硬性规范.md` 里的 `PMML-R16 … R20`（显示真值连续性 / 关键数值不截断 / 固定几何 / 字体字面 / 呈现层单一时间线）与本次新增的 **`PMML-R21 … R30`** 是 Round-2 / Round-3 的正式编号。**凡涉及进度视觉语言（Progress Visual Language）与迁移真值边界，以本附录 B 与《PMML-UI修改硬性规范》的 R21~R30 为准**；同号冲突时以硬性规范为准。

本附录是 PCMig 0.5.x 起**官方进度视觉语言**的正式规范。它取代此前的"旧进度动效路线"（`ProgressMotionDriver` + Sweep + 8 粒子方案）：旧路线只许下线/删除，**不得再扩展**（不加 Sweep 参数、不加粒子、不调 `InsetClip` 跨度、不再做厚度 A/B 投票）。

## B.A 产品语义（这是什么）

- 控件正式名称：**PCMig Immersive Transfer Progress**（不可再叫"实验效果""探针效果"）。
- 它要同时是四件事：
  1. **真实**（Head 不说谎：只表示已确认的迁移事实）；
  2. **流畅**（VisualProgress 吸收离散真值的跳变）；
  3. **有生命**（Push Band / Particle 表达"后台任务仍在工作"）；
  4. **克制**（Particle / Ripple 永远只是辅助，不是主视觉）。
- **五种语义严格分离**（写代码时必须能一一对应）：

| 元素 | 语义 | 允许影响 Value？ |
|---|---|---|
| **Progress Head**（进度头 / 填充右端） | 已经确认的迁移事实 | 它**就是**事实的图形表示 |
| **Push Band**（推光带） | 任务活动 / 数据正在工作 | **绝不** |
| **Particles**（粒子） | 数据物质的微弱流动 | **绝不** |
| **Ripple**（涟漪） | 局部交互反馈 | **绝不** |
| **Head Halo / Rim** | 当前活跃前沿的光照 | **绝不** |

- 判定成败的不是"像素变了/条动了/代码里调用了 Composition 动画"，而是**用户真机所见**（见 B.N 证据闸门）。

## B.B 几何稳定 / 光照动态（核心原则）

- **Geometry = Stable / Lighting = Dynamic**。
- Track 是纤细胶囊；Progress Fill 的**右端永远是稳定圆弧**（`)`），永远不是平切（`|`）。
- Head 的几何不得被任何"液面/波浪/噪声路径"扰动；允许变化的是 Halo / Rim / Band 的**亮度与不透明度**。
- `FillWidth < Thickness` 时 `radius = min(Thickness/2, FillWidth/2)`，避免低进度几何翻折。

## B.C 尺寸 Token（第一版正式尺寸，不再投票）

| Token | 值 | 含义 |
|---|---|---|
| `PCMigImmersiveProgressHostHeight` | **16** DIP | 布局槽（上下各留 2 DIP 呼吸） |
| `PCMigImmersiveProgressThickness` | **12** DIP | 实际动态材质厚度 |
| `PCMigImmersiveProgressRadius` | **6** DIP | 端帽半径 |

- 用户真机验收后若判定 12 太粗/太细，**只许改 Token，不许改布局结构**。
- 底栏（Footer）保留轻量静态条（8 / 4）以免两道满血 Push Band 互相竞争；将来若统一，必须做 `ImmersiveTransferProgressVariant.Compact`，**不得复制一份代码**。

## B.D Progress Truth Boundary（真值边界，最容易出错的地方）

- 完整链路：
  `Robocopy / Engine → ProgressTruthSnapshot → Core 可信真值 → ContinuationDisplayState → 已确认呈现目标 → ProgressPresentationCoordinator → VisualProgress → { 主百分比文本, 主字节文本, ImmersiveTransferProgress }`
- `ImmersiveTransferProgress` **永远不知道** Robocopy / SMB / Receipt / Verifier / JobState / 日志 / 源路径 / 目标路径；它只接受 **`Value` / `Maximum` / `VisualState` / `EffectsQuality`** 四个输入（`PMML-R28`，**该边界必须有测试**）。
- `/Z` 与 `/J` 会先把目标文件预分配到最终长度 ⇒ **这些通道上 `FileInfo.Length` 永远不能当作已确认字节**。唯一例外是 `/MT` 的 Bulk 通道（那里目标长度是唯一证据，禁掉它会退化成 UI-02 的"暂停后 0 B"假归零）。
- 被打断（`MarkInterrupted`）时写入回执的 `TargetBytes` 语义是**"截至被打断时刻的可信已确认字节"**，不是目标的逻辑长度。

## B.E VisualProgress（连续状态滤波）

- 长期存活的 `TargetProgress` / `VisualProgress`；每个渲染节拍：
  `visual += (target − visual) × (1 − exp(−k × dt))`，默认 **`k = 10`**（时间常数 100 ms）。
- 硬边界：`VisualProgress ≤ TargetProgress`；正常运行期**不回退**；没有新目标时追平即停；**不预测、不外推、不自我爬向 99%**。
- 目标变化**只修改目标**：不重启动画、不清空速度状态、不新建动画段（`PMML-R24`）——"每来一个目标就播一段 0.2~1.2 s 线性动画"会让速度在每个目标处重启，这本身就是抖动源，Round-3 已废弃。
- `dt` 必须来自高精度时间戳并夹取 `dt = min(dt, 1/30 s)`，防止窗口还原后大跳。
- `Completed` 可以用 0.05 pp 的吸附精确落到 100（一位小数显示分辨率的一半 ⇒ 屏幕数字不变，不算无证据前跳）。

## B.F Push Band（推光带）

- **同时最多一条**（`ActiveBandCount ≤ 1`）；两层柔光：外场 `SigmaX≈26~34 / SigmaY≈5~6 / Opacity 0.18~0.28`，亮核 `SigmaX≈9~12 / Opacity 0.16~0.26`。
- 周期 `1.20~1.45 s`（活动 `0.90~1.05 s`，静止 `0.25~0.40 s`）；静止段不透明度**精确为 0**（这就是"同时最多一条"的实现口径）。
- 运动 `x(t) = sin(t × π/2)`，`t ∈ [0,1]`：先快、持续减速、靠近 Head 时柔和收尾。**禁止线性、禁止分段变速、禁止突然减速。**
- 它是"宽而柔的两次光压"，**不是**硬白条；不是跑马灯，不做并行扫描。

## B.G Particle（粒子）

- 生产默认活跃：**High 14 / Balanced 9 / Reduced 4**（池容量 16），半径 `0.55~1.20 DIP`，寿命 `0.8~1.35 s`，跟随因子 `0.18~0.28`。
- 出生轨迹窗口 `max(64 DIP, ProgressWidth × 0.18)`，范围 `Head − TrailLength` 到 `Head − 6 DIP`，并**必须被裁剪在填充胶囊内**：
  - 胶囊装不下粒子时（`progressWidth` 过小）**一枚都不许有**；
  - 粒子 X 必须被夹在 `[capsuleLeft, capsuleRight]` 内（真机曾出现 `pMin = −1.163`）。
- 粒子属于 **Progress Space**，不是屏幕空间自由飞行；Head 前进时轻微拖带（`follow factor`）。
- **固定池**：绝不允许每帧 `new Particle` / LINQ / 频繁 `List.Add/Remove`。

## B.H Ripple（涟漪）

- 同时最多 `3~4` 个；半径 `1.5 → 4.5 DIP`；时长 `180~260 ms`；峰值不透明度 `≤ 0.20`；冷却 `≥ 220 ms`；`Reduced` 档**关闭**。
- 只在 Push Band 与粒子局域影响越过阈值（`influence ≥ 0.55`）且冷却允许时触发；**它是极稀疏的反馈，不得成为主视觉**。

## B.I Head Halo / Rim（前沿光照）

- Halo 长度 `36~56 DIP`（当前 **48**），外圈不透明度 `0.25~0.40`（当前 **0.34**），局部光晕 `16~24 DIP`（当前 20）；Rim `1 DIP`、不透明度 `0.65~0.82`（当前 0.74）。
- **不得是白色描边圆环**；Halo 只能有一点越过 Head（约 `haloLength × 0.35`），其余被裁剪在填充胶囊语义内。

## B.J 状态机（12 态）

| 状态 | Head | Push Band | 粒子 | Ripple | Halo |
|---|---|---|---|---|---|
| Idle / Preparing | 跟随 VisualProgress | — | — | — | 弱 |
| Running | 追 VisualProgress | 运行 | 活跃 | 允许 | 满 |
| Holding | **静止** | **继续跑** | 减缓 | 少量 | 保持（表达"还在干活"） |
| Pausing | 保持可信值 | 减速/淡出 | 淡出 | 停 | 渐弱 |
| Paused / Interrupted | 冻结（事实不动） | **继续（降速降亮，R35）** | 降到 40~60% 数量/流速 | 保留 | 保留（略降） |
| Stopping | 同 Pausing | 停 | 淡出 | 停 | 减弱 |
| Interrupted | **冻结在可信高水位** | 停 | 停 | 停 | 停 |
| Verifying | 100% 或真实完成点 | 停 | 停 | 停 | 极弱（**不得再有"正在传输"动画**） |
| Completed | 100%（**仅 Core 真完成**） | 停 | 收尾 | 停 | 收尾 |
| Warning / Failed | 保留最后可信值 / Error 语义 | 停 | 淡出 | 停 | 停 |

- **真正终止态**（`Failed` / `Completed`）装饰必须**全停**（`band = 0 / particles = 0 / ripple = 0`），有测试锁定；`Paused` / `Interrupted`（可续传）**不属于终止态**，按 R35 保留低强度活性（`TerminalStates_StopAllDecorations` 只覆盖 Failed/Completed，Paused/Interrupted 由 `RecoverableStates_KeepMaterialAlive_ButWeaker` 锁定）。

## B.K Reduced Effects / Reduced Motion

- `EffectsQuality.High / Balanced / Reduced` 只影响装饰强度（粒子数、Ripple 开关、Band 强度、Halo 强度、是否用重模糊）。
- 远桌面（RDP / Horizon）/ 集成显卡 / 虚拟机 / 低性能模式默认 Balanced 或 Reduced，但 **未经用户授权不得改变业务 UI 逻辑**。
- Reduced Motion：Head 仍可直接跟随 VisualProgress，Push Band 可关闭或减速，粒子 / Ripple 关闭，**业务值与 Automation `Value` 完全不变**（`PMML-R29`）。

## B.L 性能与渲染架构

- 渲染所有者**只有一个**：Win2D（官方 NuGet `Microsoft.Graphics.Win2D`，**固定版本**，加入前必须验证 `net8.0-windows10.0.19041.0` + Windows App SDK 2.5.1 + unpackaged / self-contained 可还原可构建）。
- 禁止"几十个 XAML Ellipse + Storyboard"、禁止每帧创建 XAML 元素或 Storyboard。
- 每帧严格 9 层顺序：1 Track → 2 Fill → 3 Push Band 外场 → 4 Push Band 亮核 → 5 Head Halo → 6 Head Rim → 7 Particles → 8 Ripples → 9 Border；所有动态层裁剪在 Fill 胶囊内。
- 每帧禁止：`new Brush` / `new Geometry` / `new Particle` / LINQ / 重量级 `List` 增删 / 新 Storyboard。渐变**形状**只在创建设备或换调色板时写入；帧内只改 `StartPoint` / `EndPoint` / `Opacity`。
- **线程纪律（真机崩溃教训）**：Win2D `CanvasAnimatedControl.Update/Draw` 跑在**游戏循环线程**，不是 UI 线程。该线程**不得**读 DependencyProperty、不得读 `Application.Current.Resources`、不得读 `MotionDirector.SystemAnimationsEnabled`（跨线程访问 XAML 对象会以 `0xc000027b`（`RPC_E_WRONG_THREAD`）崩掉整个应用）。正确做法：UI 线程把值 + 调色板发布成**不可变快照**，渲染线程只读快照。
- 每帧零分配的**自检**也必须在暂停画布后再做离屏绘制（同一批画刷被渲染线程就地改写，Win2D 画刷不是线程安全的）。
- `DeviceLost` 时必须能恢复，且**绝不影响迁移业务**。

## B.M Accessibility

- 自定义 Canvas 不能只让 UIA 看到一个 Canvas：必须实现 `ProgressBar` / `RangeValueProvider` 语义（`Minimum = 0` / `Maximum = 100` / `Value =` 与视觉一致的可访问值）。
- Automation Name = `迁移总进度 56.3%`；屏幕阅读器**不得**读取 BandPhase / Particle / Ripple / Glow（纯视觉层）。
- 真机口径（WinUI 3）：`IRangeValueProvider` **没有** `RangeValueChanged` 事件，值变化要靠 `RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, old, new)`，并用 `ListenerExists(AutomationEvents.PropertyChanged)` 守卫。

## B.N Evidence Gate（证据闸门）

- 取证工具：`ImmersiveProgressVisualProbe`（显式环境变量挂载，确定性序列 `0 / 10 / 24.8 / 47.3 / 62.3 / 75 / 90 / 100` + Holding / Pause / Resume），输出 `frames\` 与 `timeline.csv`，列：
  `timestamp / rawPercent / effectiveConfirmedPercent / visualPercent / headX / bandCenterX / bandPhase / activeParticles / activeRipples / state`（实现中还输出 `bandOpacity / haloStrength / particleMinX / particleMaxX / progressWidth / hostWidth` 等）。
- 13 条验收判据：① `headX` 与 `visualPercent` 误差 ≤1 px；② `visualPercent ≤ confirmedPercent`；③ 正常运行无回退；④ Running 期无无理由大前跳；⑤ 新目标到达后视觉连续追上；⑥ 没有"一段跑完—停顿—再一段"的速度断点；⑦ 一个周期内 Push Band 单调朝 Head 且持续减速；⑧ `ActiveBandCount ≤ 1`；⑨ 所有粒子在填充胶囊内；⑩ Paused / Interrupted 后 **Head 与事实冻结**，但 Band / 粒子 / Halo 保留低强度活性（R35）；⑪ Failed 后装饰停；⑫ Holding 期 Head 不动而 Band 可继续；⑬ `Completed` 仅在 `settled = true` 时到 100%。
- 每个结论必须诚实标注 `VERIFIED FIXED（真机证据）/ CODE FIXED / NOT VISUALLY VERIFIED / OPEN`；`PMML-R30` 的七项缺一即不得称"视觉已验证"。

## B.O 实现坐标（本规范的唯一实现）

```
src\PCMig.WinUI\Controls\ImmersiveProgress\
  ImmersiveTransferProgress.xaml(.cs)         控件：DependencyProperty + Visual State + Theme + Automation + Reduced Effects + Renderer 生命周期 + UI 线程快照
  ImmersiveTransferProgressRenderer.cs        Renderer：Track / Fill / Head Halo / Push Band / Particle / Ripple / Border（9 层）
  ImmersiveProgressAnimationState.cs          动画状态：BandPhase / 粒子池 / Ripple / dt / 随机种子 / 上次进度位置
  ImmersiveProgressParticle.cs                粒子与 Ripple 的池与结构（胶囊内约束）
  ImmersiveProgressParameters.cs              全部 Token 化参数（无散落魔法数字）
  ImmersiveTransferProgressAutomationPeer.cs  ProgressBar / RangeValue 语义
```

- 主题：尺寸 Token 在 `Themes\Materials.xaml`；颜色全部走 `Themes\Colors.xaml` 的 `ImmersiveProgress*Brush`（Renderer **不得**硬编码颜色）。
- 生产接线：`Views\Step3ProgressPage.xaml(.cs)` —— 一个进度语义**只有一个视觉拥有者**，绝不把旧 Fill 与新 Canvas 叠起来。