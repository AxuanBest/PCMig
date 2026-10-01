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