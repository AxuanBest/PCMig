# PMML Implementation Audit — 当前代码 → PMML 术语 → Resource → 参数映射

> 目的：记录 PCMig v0.5.0 UI 的**真实世界**（PMML v1.0 冻结依据）。
> 原则：**代码是多少就记多少**；没有统一 Resource 而散落魔法数字的，一律标注 `Scattered Implementation` / `Legacy Deviation`，不虚构统一值。
> 原始明细（子代理全量审计产物，未被删改）：
> ・`archive/pmm-l-audit/A-materials-tokens.md`（材质/颜色/圆角/边框/渐变/阴影/Typography/Light-Dark，按文件:行号）
> ・`archive/pmm-l-audit/B-motion.md`（202 条编号条目：页面切换/Overlay/ComboBox/Task Picker/Hover/Composition/Storyboard/Reduced Motion/不统一清单）
> ・`archive/pmm-l-audit/C-surfaces-controls.md`（10 类 Surface + 16 控件族状态矩阵 + Typography 计数 + ESR 34 条证据 + MainWindow 11 层结构 + L0–L4 判定 + Legacy 清单）
> 标注约定：`【主控核实】`＝本节数值由主控直接读源码核对；`【子代理】`＝来自上述审计文件（含 `文件:行号`，第二轮抽样复核）。

---

## DAM

**对应底层技术**：WinUI 3 `AcrylicBrush`、窗口 `SystemBackdrop`（真 Desktop Acrylic 由 `Presentation/BackdropSpike.cs` 负责，见 `Materials.xaml:12` 注释）。

### 窗口 Backdrop（真 Desktop Acrylic / Mica）【子代理：A 审计，含文件:行号】
| 项 | 真实值 | 位置 |
|---|---|---|
| XAML 声明 | `MicaBackdrop Kind=BaseAlt` | `MainWindow.xaml:8` |
| 生效装配 | `MainWindow.xaml.cs:134` → `BackdropSpike.Install`，**默认 kind = "acrylic"** | `BackdropSpike.cs:164` |
| Desktop Acrylic 参数 | `TintOpacity 0.02`・`LuminosityOpacity 0.00`・`TintColor #FFF2F6FF`・`FallbackColor #FFF2F2F2` | `BackdropSpike.cs:189-192` |
| 配置顺序 | 先 `SetSystemBackdropConfiguration`（`:206`），默认清空 XAML 层（`:231`） | `BackdropSpike.cs` |
| Mica 回退 | `MicaKind.BaseAlt`（`:264`），tint/lum 用**系统默认**而**不是** 0.02/0.00（`:265-266`） | `BackdropSpike.cs` |

⇒ **两条路线参数不同源**（Acrylic 显式 0.02/0.00 vs Mica 系统默认）＝ Legacy Deviation **L-10**。

### 表面材质画刷【主控核实】
| 资源键 | 值 | 文件:行号 |
|---|---|---|
| `PCMigShellMaterial` | `SolidColorBrush #2BFFFFFF` | `Themes/Materials.xaml`（别名链 `ShellMaterialBrush`→`PCMigShellMaterial`，`:168`） |
| `PCMigCardMaterial` | `SolidColorBrush #AFFFFFFF` | `Themes/Materials.xaml`（`PrimarySurfaceBrush`/`SecondarySurfaceBrush`/`ElevatedSurfaceBrush` 三者同源，`:169/:170/:178`） |
| `PCMigInsetMaterial` | `SolidColorBrush #59FFFFFF` | `Themes/Materials.xaml`（`InsetSurfaceBrush`，`:177`） |

### 真 Acrylic（含 Tint 参数）【主控核实】
| 资源键 | TintColor | TintOpacity | TintLuminosityOpacity | FallbackColor | 用途 |
|---|---|---|---|---|---|
| `SelectedSurfaceBrush` | `#E4F1FF` | `0.45` | — | `#E4F0FF` | 选中导航面（`Materials.xaml:147` 区） |
| `PCMigUtilityAcrylic80` | `#F2F6FF` | `0.02` | `0` | `#F2F2F2` | 工具浮层 |
| `PCMigUtilityAcrylicInset80` | `#F2F6FF` | `0.10` | `0` | `#E9EFF8` | 工具浮层内嵌区 |

### 环境层【主控核实】
- 四象限环境色 + 强度：`AmbientBlueColor #8CB4FF`/`0.12`、`AmbientLavenderColor #BCA4FF`/`0.24`、`AmbientPinkColor #FFA8CC`/`0.22`、`AmbientPeachColor #FFD094`/`0.09`（`Themes/Colors.xaml:2,6`）
- 底衬渐变 `BaseBackdropBrush`：`(0,0)→(1,1)`，`#2E9AB4FF` → `#28947CFF`@0.28 → `#2EC894FF`@0.62 → `#54FF9CD6`@1（`Colors.xaml:9`）
- 代码注释明确：**禁止用 `UIElement.Opacity` 做层次**（会把文字/图标/焦点一起透明）——层次由 Tint + 材质表达（`Materials.xaml:16` 区注释）

### Scattered / Legacy（DAM）
- 【子代理】同语义材质存在多实现：输入框实际用项目自建 `TextControlBackground` 系列（`Themes/TextInputTemplates.xaml`），**不在**声明的 L3 上；
- 【子代理】`PCMigWorkspaceMaterial`(L1) / `PCMigControlMaterial`(L3) / `PCMigPrimaryMaterial`(L4) **零承载元素**（仅 DVT 表引用）；
- 【子代理】Step4 日志面板硬编码 `Background="#F21A2033"` + `BorderBrush="#33FFFFFF"`（`Views/Step4ResultPage.xaml:225`），而同值 token `PCMigSemanticDarkMaterial`（`Materials.xaml:29`）**零引用**。

---

## LMDS

### 代码中**已存在**的分层声明（原文，`Themes/Materials.xaml:2-9`）【主控核实】
```
L0 Mica/Ambient（背景，最透）
L1 Shell 面板（tint，环境光可透）
L2 Card 卡片（更白更实，"浮在面板上"）
L3 Input 内嵌（半透明且略暗，形成凹陷感）
L4 Selected / Primary（强调 tint + 最高 elevation）
```
> 这与 PMML v1.0 冻结口径（L0 Backdrop / L1 Base / L2 Elevated / L3 Interactive / L4 Overlay·Tool）**语义不同**：
> 代码的 L3 = Input 内嵌、L4 = Selected/Primary，**且没有独立的 Overlay/Tool 层**（工具浮层走 `PCMigUtilityAcrylic*`，属 DAM 变体而非层级）。
> ⇒ 已登记为 **Legacy Deviation L-01**（见 `PMML-Legacy-Deviations.md`）。PMML 冻结的是**语义层**；代码既有命名保留不动。

### Surface Style → 层/材质/几何（真实值）【主控核实】
| Style 键 | 背景 | 边框 | 圆角 | Padding | 阴影 | 文件:行号 |
|---|---|---|---|---|---|---|
| `ShellMaterial` | `PCMigShellMaterial` | `PCMigSurfaceRimLightBrush`/1 | **22** | 18 | `ElevationLow` | `Materials.xaml:168` |
| `PrimarySurface` | `PCMigCardMaterial` | `PCMigSurfaceRimLightBrush`/1 | **20** | 20 | `ElevationHigh` | `Materials.xaml:169` |
| `SecondarySurface` | `PCMigCardMaterial` | `PCMigSurfaceRimLightBrush`/1 | **16** | 16 | `ElevationLow` | `Materials.xaml:170` |
| `InsetSurface` | `PCMigInsetMaterial` | `InsetBorderBrush`/1 | **13** | 12 | 无 | `Materials.xaml:177` |
| `ElevatedSurface` | `PCMigCardMaterial` | `PCMigSurfaceRimLightBrush`/1 | **18** | 16 | `ElevationMedium` | `Materials.xaml:178` |
| `SelectedNavigationSurface` | `SelectedSurfaceBrush`(Acrylic) | `AccentEdgeBrush`/1 | **15** | 13,15 | `ElevationMedium` | `Materials.xaml:179` |
| `ListItemSurfaceBrushBorder` | `ListItemSurfaceBrush` | `BorderNormalBrush`/1 | **10** | — | 无 | `Materials.xaml:176` |

### 圆角 Token【主控核实】
`PCMigRadiusInput = 14`、`PCMigRadiusStepCard = 16`、`PCMigRadiusButton = 12`、`PCMigRadiusInset = 13`（`Materials.xaml`）

### MainWindow 结构（层归属依据）【子代理：C 审计 §E】
11 层结构（Backdrop → 标题栏 → Shell → 页面宿主 → 底栏 …）实测：
- `BottomBar`（`MainWindow.xaml:115`）结构上属 Shell 级行，**材质却用 `SecondarySurface`→`PCMigCardMaterial`（L2）** ⇒ 层级归属与结构不一致（Legacy Deviation L-02）；
- 页面步骤徽章 **5 份重复手写**（`#B8FFFFFF` + `CornerRadius="17"` + 内层高光 + `ElevationHigh`）：`Step1ConnectPage.xaml:21,46`、`Step2SelectDataPage.xaml:197-208`、`Step3ProgressPage.xaml:31-42`、`Step4ResultPage.xaml:53-64`，**未 Token 化**（L-03）。

---

## DSL-45

**对应底层技术**：对角 `LinearGradientBrush`、`RadialGradientBrush(Center 左上)`、成对亮/暗边画刷。

### 冻结方向【主控核实】
**左上 → 右下，约 45°。** 代码注释原文：「方向与全局一致：光源在左上 → 上缘亮、下缘暗。」（`Colors.xaml:42`）

### 证据（真实资源）【主控核实】
| 资源键 | 形态 | 文件:行号 |
|---|---|---|
| `NavCardEmbossTopBrush` | `Linear (0,0)→(1,0)`：`#FFFFFFFF`→`#F2FFFFFF`@0.55→`#D8FFFFFF`@1 | `Colors.xaml:44-48` |
| `NavCardEmbossEdgeBrush` | `Linear (0,0)→(1,1)`：`#8CFFFFFF`→`#4D46618C`@0.45→`#B33A5478`@1 | `Colors.xaml:49-53` |
| `EdgeHighlightDiagonalBrush` | `Linear (0,0)→(1,1)`：`#FFFFFFFF`→`#8AFFFFFF`@0.34→`#3EFFFFFF`@0.72→`#2AFFFFFF`@1 | `Colors.xaml:17` |
| `EdgeHighlightSoftDiagonalBrush` | 同结构（峰值 `#E0FFFFFF`） | `Colors.xaml:18` |
| `EdgeHighlightCrispBrush` | `Linear (0,0)→(0,1)`：`#FFFFFFFF`→`#C8FFFFFF`@0.045→`#68FFFFFF`@0.11→`#40FFFFFF`@1 | `Colors.xaml:20` |
| `PCMigDAELBrush` | `Radial Center=0.06,0.08 GradientOrigin=0,0 Radius=1.25`，峰值 `#FFFFFFFF` | `Materials.xaml` |
| `PCMigDAELBrushSubtle` | 同结构，峰值 `#8CFFFFFF` | `Materials.xaml` |
| `PCMigCornerGlowBrush` | `Radial Center=0,0 GradientOrigin=0,0 Radius=0.62` | `Materials.xaml` |

**物理结论（代码注释原文）**：侧栏卡片底色近纯白（实测 RGB(251,253,255)），**白叠白不可见** ⇒ 浮雕必须是"亮边 + 暗边成对"。

---

## ESR

**构成四件套**：edge highlight（DSL-45 画刷）+ subtle border（统一 `BorderThickness = 1`）+ soft shadow + material contrast。

### 阴影【主控核实】
| 资源键 | 类型 | Blur / Opacity / Offset |
|---|---|---|
| `ElevationLow` / `ElevationMedium` / `ElevationHigh` | **`ThemeShadow`** | **框架决定；项目未定义数值**（`Materials.xaml` 仅声明三档命名阴影） |

> 如实记录：PMML v1.0 **没有**自定义 Shadow blur/opacity/offset。三档只按命名语义冻结。

### ESR 证据量【子代理：C 审计 §D】
子代理逐条列出 **34 条**"边缘高光 + 细边框 + 柔阴影"证据（含 `PCMigSurfaceRimLightBrush`/`PCMigStepCardRimLightBrush` 别名链、TopSheen 渐变、`#B8FFFFFF` 类边界高光）。

### Legacy（ESR）
- 【子代理】**Step Card 选中描边不可见**：`Presentation/StepNavigation.cs:116` 设 `CardBorder = AccentEdgeBrush`，但宿主 `Views/StepNavigationControl.xaml:79` 是 `BorderThickness="0"` ⇒ 描边被丢弃；同语义 `SelectedNavigationSurface`（`Materials.xaml:179`）反而 `BorderThickness=1` 且**全仓零引用**（L-04）。

---

## DCST

**对应底层技术**：Composition `Vector3KeyFrameAnimation`(`Translation`) + `ScalarKeyFrameAnimation`(`Opacity`)，由 `Presentation/MotionDirector.cs` 驱动。

### Token（真实值）【主控核实，`Themes/Motion.xaml`】
| Token | 值 | 行号 |
|---|---|---|
| `PCMigMotionPagePushDuration` | `0:0:0.42` | `Motion.xaml:16` |
| `PCMigMotionPageEnterDistance` | `10` | `Motion.xaml` |
| `PCMigMotionPageSlideDistance` | `26`（**已废弃**：U57 起改整页视口高度 Push，兼容保留） | `Motion.xaml:18` |
| 方向常量 | `DirectionForward = 1` / `DirectionBackward = -1` | `MotionDirector.cs:45,48` |
| **缓动（生效）** | **`CubicBezier (0.10, 0.90)(0.20, 1.00)`** | `MotionDirector.cs:370`（`AnimateSlideFade`） |
| 缓动（legacy/dead-path/reserved） | `standard`→`CubicEase EaseOut`；`exit`→`CubicEase EaseIn` | `MotionDirector.cs:98-108`；唯一使用点 `:186`（死路径 `PlayPanelExit`） |
| 兜底完成 | 时长 + `140 ms` | `MotionDirector.cs:325` |

### **生效实现 vs 代码注释（重要更正）**【子代理：B 审计】
- **生效**：整页**视口高度**的纵向 Push，**Opacity 恒为 1（无淡入淡出）**，Duration `0.42s`，**无 Scale**，forward/backward 仅符号相反（对称），**接管式可中断**；
- **过期注释**：`Motion.xaml:14-15` 仍写"旧页上移淡出、新页自下方进入淡入" ⇒ 与实现不一致（Legacy Deviation L-05）。

---

## OACT

**对应底层技术**：`ShapeVisual` + `Matrix4x4 TransformMatrix`（U62 Fluid Zoom matched-geometry）；fallback 为 `Translation`/`Scale`/`Opacity`。

### 主路径 U62 Fluid Zoom【主控核实】
| Token | 值 | 行号 |
|---|---|---|
| `PCMigFluidZoomOpenDuration` | `0:0:0.36` | `Motion.xaml:35` |
| `PCMigFluidZoomCloseDuration` | `0:0:0.30` | `Motion.xaml:36` |
| 时间轴（**权威 = `ApplyFrame()`**） | fill **0→32%**（初始 factor **0.22**）・stroke **10→55%** ・real panel **55→100%** ・shell fade **80→100%** ・source **0→22% 淡出 / 22→70% 隐藏 / 70→88% 恢复** ・geometry **EaseOutCubic** ・其它通道 **staged linear Segment** | `FluidZoomTransitionCoordinator.cs:355-404`（旧 `Motion.xaml` 注释作废） |

### Fallback Origin-aware Reveal【主控核实】
`Reveal 0.26` / `Dismiss 0.19` / `RevealOffset 7` / `RevealScale 0.945` / `DismissScale 0.985` / `DismissOffset 5` / `AnchorGap 5`（`Motion.xaml:21-30`）；兜底完成 `时长 + 120 ms`（`MotionDirector.cs:488,551`）。

**用户否决记录（代码注释原文）**：上一版 Origin Reveal（Fade + 0.945 Scale）被用户人工否决为"本质还是淡入弹窗"，Token 保留但只作 fallback。

### 死代码 / 无消费者【子代理：B 审计】
- `PreparePageEntrance`（`MotionDirector.cs:119-137`）、`PreparePanelEntrance`（`:142-157`）、`PlayPanelExit`（`:165-218`）**均无调用点**，但 `ChangelogPanel.xaml:5-7`、`DeveloperTuningPanel.xaml:16-18` 与 4 个 Step 页注释仍声称它们在运行（L-06）；
- 无消费者 Token：`PCMigMotionDurationNormal`(0.20)、`Slow`(0.26)、`EaseStandard`、`DialogScaleStart`(0.98)、`ProgressSweepDuration`(1.60) ⇒ **Dialog 缩放动效与 Progress Sweep 实际不存在**（L-07）。

### 缓动族（Scattered）【子代理：B 审计】
四族并存：`(0.10,0.90)/(0.20,1.00)`（`MotionDirector.cs:370`、`InteractionFeedback.cs:300`）・`(0.16,1.0)/(0.30,1.0)`（`MotionDirector.cs:565`）・手写 EaseOutCubic（`FluidZoomTransitionCoordinator.cs:578-582`）・`CubicEase`（`PcmigComboBoxRoll.xaml:219,229`）⇒ L-08。

**口径**：当前为插值缓动，PMML 只称 **Spring-like Easing**，不得声称真实 Spring Physics。

---

## TAOP

**对应底层技术**：`FlyoutBase.Placement`（`BottomEdgeAlignedLeft` 等）、`Popup` 锚定。

### 归属表【主控核实 + 子代理】
| 浮层 | 锚定/定位 | 归属 |
|---|---|---|
| **PMML Task Picker**（已有任务） | `Views/Step2SelectDataPage.xaml`：`Flyout Placement="BottomEdgeAlignedLeft"`（`:431` 区），从控件**下方向下**展开 | **TAOP 参考实现** ✅ |
| 材质调节面板 | 从标题栏触发入口下方展开，间距 `PCMigUtilityPanelAnchorGap = 5` | TAOP + OACT |
| 更新日志面板 | 同上 | TAOP + OACT |
| 线程数 `ComboBox` | **原生 ComboBox 内部定位**（非 TAOP） | 原生行为 |
| Task Picker 打开动效 | **使用框架默认 Flyout 动画，未自定义** | 【子代理：B 审计】 |

### Task Picker 实测（主控真机取证）
- 收起态 Presenter 高度 **≈32px**（本页实测 34px 含边框），与「线程数」控件同 band、中心线对齐；
- 真实点击路径：控件底边 **y=383** → 浮层顶边 **y=389** ⇒ **间距 6px，向下展开**；
- 点击语义：`ClickedItem as JobSummary` → `JobDir` → `AdoptExistingJobAsync`，**零索引**；连点三项实测 `match=True`；**第一条（listIndex=0）可正常载入**；
- 显示 vs 载入分离：收起态预览**不写 `SelectedIndex`**。

### 已登记的失败教训（PMML-R10 / R12 来源）
为修正原生 ComboBox 浮层位置而加的 `Popup.VerticalOffset` 后置校正**实测无效**（限高、限宽、选中项三组变量均无关），**已删除**；`AlignJobsDropDownBelowCombo()` 与 `DropDownOpened` 订阅一并移除。

---

## MHE

**对应底层技术**：`ElementCompositionPreview.SetIsTranslationEnabled` + `Vector3KeyFrameAnimation`(`Translation`)，由 `Presentation/InteractionFeedback.cs` 驱动。

### Token（真实值）【主控核实，`Themes/Motion.xaml`】
| Token | 值 | 行号 |
|---|---|---|
| `PCMigMotionHoverEnterDuration` | `0:0:0.13` | `Motion.xaml:44` |
| `PCMigMotionHoverExitDuration` | `0:0:0.15` | `Motion.xaml:45` |
| `PCMigMotionPressedDuration` | `0:0:0.10` | `Motion.xaml:46` |
| `PCMigMotionHoverLiftCard` | `2`（DIP） | `Motion.xaml:47` |
| `PCMigMotionHoverLiftButton` | `1.3`（DIP） | `Motion.xaml:48` |
| `PCMigMotionPressedSinkCard` | `0.5`（DIP） | `Motion.xaml:49` |
| `PCMigMotionPressedSinkButton` | `0.6`（DIP） | `Motion.xaml:50` |

**冻结口径（注释原文）**：主反馈只能是 Translation / Elevation；**禁止用 Scale 放大**（会让文字发虚、边缘变形）；单位 = Canonical DIP，与 `UniformScaleHost` 同坐标系 ⇒ **不得再乘 `ApplicationUIScale`**。

**实现细节**【子代理：B 审计】：`InteractionFeedback.cs:285-288` 用 `SetIsTranslationEnabled` + `InsertVector3("Translation", …)`，保留 X/Z（Z 是模板层级抬升）。

---

## Token

### 分类（PMML 命名空间）
`PMML.Material.*` / `PMML.Surface.*` / `PMML.Corner.*` / `PMML.Border.*` / `PMML.Shadow.*` / `PMML.Light.*` / `PMML.Motion.*` / `PMML.Easing.*` / `PMML.Spacing.*` / `PMML.Typography.*` / `PMML.Opacity.*`

### 真实 Token 清单（主控核实）

**Motion / Easing（`Themes/Motion.xaml`，53 行）**
| 类别 | Token → 值 |
|---|---|
| Duration | `Fast 0.14`・`Normal 0.20`・`Slow 0.26`・`Selection 0.16`・`PagePush 0.42`・`FluidZoomOpen 0.36`・`FluidZoomClose 0.30`・`UtilityReveal 0.26`・`UtilityDismiss 0.19`・`HoverEnter 0.13`・`HoverExit 0.15`・`Pressed 0.10`・`ProgressSweep 1.60` |
| Easing | `standard`＝`CubicEase EaseOut`・`exit`＝`CubicEase EaseIn` |
| 位移/缩放 | `PageEnterDistance 10`・`PageSlideDistance 26`(废弃)・`UtilityRevealOffset 7`・`UtilityDismissOffset 5`・`UtilityRevealScale 0.945`・`UtilityDismissScale 0.985`・`DialogScaleStart 0.98`・`UtilityPanelAnchorGap 5`・`HoverLiftCard 2`・`HoverLiftButton 1.3`・`PressedSinkCard 0.5`・`PressedSinkButton 0.6` |

**表面 / 圆角 / 边框 / 光照 / 阴影 / 不透明度**
- 表面：`PCMigShellMaterial #2BFFFFFF`・`PCMigCardMaterial #AFFFFFFF`・`PCMigInsetMaterial #59FFFFFF`
- 圆角：`PCMigRadiusInput 14`・`PCMigRadiusStepCard 16`・`PCMigRadiusButton 12`・`PCMigRadiusInset 13`
- 边框/边缘：`BorderSoft #30FFFFFF`・`BorderNormal #406783AC`・`EdgeHighlight #A8FFFFFF`・`SecondaryEdge #4DFFFFFF`・`AccentEdge #6689C8FF`・`InsetBorder #5A55789F`
- 光照（DSL-45）：见 §DSL-45 表
- 阴影：`ElevationLow/Medium/High`＝`ThemeShadow`（无数值）
- 不透明度：`Ambient*Opacity 0.12/0.24/0.22/0.09`・`FutureNavigationOpacity 0.84`・`FutureNavigationSecondaryOpacity 0.76`
- 文字：`TextPrimary #10244A`・`TextSecondary #465F84`・`TextMuted #7186A4`・`TextDisabled #AAB6C8`
- 语义色：`Success #16A36E`・`Warning #D99518`・`Danger #D64758`
- 强调色：`Accent #1677F2`・`AccentHover #338CFA`・`AccentPressed #0D5FC8`・`AccentDisabled #DCE6F6`・`AccentDisabledEdge #8FB0C8E8`・`AccentDisabledForeground #44597D`

### Scattered / 零引用【子代理：A/C 审计】
- 同类值多实现与硬编码（含 5 份步骤徽章手写、Step4 日志面板硬编码、输入框不走 L3 等）见 `PMML-Legacy-Deviations.md`；
- **23 项零引用资源**（`PrimarySurface`、`SelectedNavigationSurface`、`FutureNavigationCard`、`PCMigRadiusStepCard/Button/Inset`、`PCMigMotionProgressSweepDuration`、`Views/PCMigSurface.xaml` 整套控件等）已由 C 审计 §F.4 逐条列出（L-09）。

### 未覆盖 / 存疑（子代理自述）
- Step Card 零反馈根因未完全定位；`PcmigComboBoxRoll.xaml`（330 行）仅抽样取证；未做真机实测。

---

## Control Families（16 族状态矩阵）

完整矩阵（Normal / PointerOver / Pressed / Disabled / Selected / Focus + 实际属性值与 `文件:行号`）见 `archive/pmm-l-audit/C-surfaces-controls.md` §B。族清单：
Primary Button ・ Secondary Button ・ Icon Button ・ TextBox ・ PasswordBox ・ ComboBox ・ Task Picker（三段：Presenter / Flyout / Item）・ ToggleSwitch ・ CheckBox ・ Navigation Item ・ Card ・ Flyout ・ Dialog ・ Tooltip ・ Progress ・ Log Row。

**要点（主控核实）**：
- Task Picker 三段（`PCMigTaskPickerPresenterStyle` / `PCMigTaskPickerFlyoutStyle` / `PCMigTaskPickerItemStyle`）均落在 `Views/Step2SelectDataPage.xaml` 的 `<UserControl.Resources>`，材质/圆角/选中色全部取自 ComboBox 同套主题资源；
- 缺态一律以"未实现"记录，**不补造**。

### Progress 族（2026-10-04 更新，FIX BATCH 5 / 指令 §8）

原族条目是"用 ProgressBar + `PCMigProgressBar` 模板"。**该实现已废弃**，原因是实测而非设计偏好：

- **WinUI 3 的 `ProgressBar` 不按 `DeterminateRoot`/`ProgressBarIndicator` 驱动自定义模板的填充宽度**。运行时视觉树探针显示 `Rectangle#ProgressBarIndicator w=0` 而 `Value=47.29025842745828`；同页、同窗口几何的两态截图（0% vs 47.3%）在进度条带**逐像素零差异** ⇒ 填充宽度恒为 0，从未绘制。
- 静态取证不可行：`Microsoft.UI.Xaml.dll` 与 `PCMig.WinUI.pri` 中搜不到任何部件名字符串（ASCII 与 UTF-16 两种搜法均为 0 命中）。

**现行实现（Progress 族新条目）**：

| 项 | 真实值 | 位置 |
|---|---|---|
| 轨道 | `PCMigProgressTrack`（`Border`：`CornerRadius=6`、`Background={StaticResource ProgressTrackBrush}`） | `Themes/Controls.xaml`（Progress 族样式块） |
| 填充 | `PCMigProgressFill`（`Grid`：`Left`/`Stretch`/`Width=0`/`IsHitTestVisible=False`，内含 `Rectangle RadiusX/Y=6 Fill={StaticResource AccentGradientBrush}`） | 同上 |
| 唯一写入者（底栏） | `UpdateFooterProgressFill()`：`width = Math.Round(轨道 ActualWidth × clamp(percent,0,100)/100, 1)`；真值来自 `Session.LastTruth` | `MainWindow.xaml.cs`；`FooterProgressFill` 于 `MainWindow.xaml` |
| 唯一写入者（Step3） | `UpdateTotalProgressFill()`：同式；真值来自 `PushState` 的同一 `ProgressTruthSnapshot` | `Views/Step3ProgressPage.xaml.cs` / `Step3ProgressPage.xaml` |
| 动画 | **无**（宽度只由真值驱动；PMML-R8 合规，"填充只能装饰、不能自己推进 Value"） | — |
| 无障碍 | `AutomationProperties.SetName(进度宿主, "迁移进度 xx.x%")`（原 `RangeValuePattern` 随 ProgressBar 移除） | 两个写入者内 |
| LMDS 归属 | L3（轨道 + 填充同层）；材质/光源/圆角全部复用既有 token，未引入新 token（PMML-R14） | — |
| 已知取舍 | **未做**「前沿高光 / subtle glow」：当前无可用裁剪原语（Grid/Border 默认不裁剪子元素），强加会在 0% 时漏出亮条 | 源码注释已记录 |

**⚠ 上表「动画 = 无」「已知取舍 = 未做前沿高光」两条已由 2026-10-05 UI Closure 取代，见下节。**

实测证据（像素级）：填充覆盖整 12 DIP 轨道高度（y 843..854 全 12 行），满宽行右边界 364（预测 `285 + 0.473×170 ≈ 365.4`，圆角内缩 1 px），颜色自 `(43,152,254)` 渐变到 `(20,107,234)`。详见 `E:\PCMigLab\Evidence\Trust-Critical-Recovery\FIX-BATCH-5-UI.md` §3。

### Progress 族（2026-10-05 UI Closure 更新，用户指令 UI-05 / UI-06）

**填充元素改为"常驻满宽 + 裁剪标量"**（不再写 `Width`）：

| 项 | 本轮实现值 | 位置 |
|---|---|---|
| 填充几何 | `Width="Auto"` + `HorizontalAlignment="Stretch"`（本地值覆盖 `PCMigProgressFill` 样式的 `Width=0`），常驻满宽；可见长度由 `InsetClip.RightInset = 轨道宽 − 已完成像素` 表达 | `Views/Step3ProgressPage.xaml`（`TotalProgressFill`）/ `MainWindow.xaml`（`FooterProgressFill`） |
| 视觉补间 | 新类 `ProgressMotionDriver`（`internal sealed`，零业务引用）：追赶上限 `MaxCatchUpPerSecond = 0.55`（轨道宽 55%/s）、单段 `MinSpanSeconds = 0.06` / `MaxSpanSeconds = 0.40`、段内线性（**不加 easing** —— `InsetClip` 无可回读动画值，新段起点必须用 Stopwatch + 上段起止值精确复现，否则回跳） | `Presentation/ProgressMotionDriver.cs` |
| 调用点 | Step3：`UpdateTotalProgressFill()` 内 `SetTrackWidth / SetActive / SetTarget / SnapTo`（`normalProgress = Phase == Running && width >= _lastRenderedWidth`）；底栏：`UpdateFooterProgressFill()` 同构 | `Views/Step3ProgressPage.xaml.cs` / `MainWindow.xaml.cs` |
| 前沿柔光 | 带宽 `GlowWidth = 26f`、亮峰 `GlowPeakAt = 9f`、峰值 `#5ABCA4FF`、右侧渐隐至全透明；挂**轨道宿主**子树（挂填充会被前沿 clip 切平）；位置与前沿用同一组起止值/时长并行插值 | 同上 |
| 扫描高光 | 带宽 `SweepBandWidth = 44f`、峰值 `SweepPeakAlpha = 0x1E`（≈12%）、`SweepSeconds = 1.60`（与既有 Token `PCMigMotionProgressSweepDuration` 同源）、`IterationBehavior.Forever`；挂**填充元素**子树 ⇒ 自动被裁剪在已完成区内 | 同上 |
| 粒子流 | `ParticleCount = 8`、半径 2.0/1.65/1.3 DIP、`ParticleSpan = 34f`（活动带）、`ParticleCycleSeconds = 0.90`、负 `DelayTime` 错相、Opacity 峰值 0.26、颜色 `#8CB4FF` / `#BCA4FF` 交替 | 同上 |
| 状态映射 | `SetActive(Phase == JobPhase.Running)`：**只有 Running 开装饰**；Pausing / Paused / Stopped / Failed / Interrupted / Resumable / Completed / CompletedWithErrors 一律 `SnapTo` 真值 + `IsVisible=false` + `StopAnimation` | 两个调用点 |
| Reduced Motion | 复用 `MotionDirector.SystemAnimationsEnabled`：关闭时 `SnapTo` 真值，装饰对象仍会被创建但保持 `IsVisible=false`；业务状态零变化 | 同上 |

**实现纪律（踩坑记录）**：`VisualCollection` **没有索引器**（`Children[i]` 编译失败 CS0021）⇒ 按序号访问必须先 `ToArray()`；`Microsoft.UI.Composition` 与 `Windows.UI.Composition` **是两套类型不可混用**；一个 XAML 元素**只能挂 1 个 child visual**（装饰合并到各挂载点唯一的 `ContainerVisual` 下）；动画属性名是字符串，拼错会**静默不生效**。

### Token / 样式增量（2026-10-05）

| 项 | 变更 | 位置 |
|---|---|---|
| 新增 Token | `PCMigRadiusOverlay = 16`（Flyout/Popup/Dropdown 面板）、`PCMigRadiusListItem = 10`（列表条目/行内 chip） | `Themes/Materials.xaml` |
| 圆角收敛（原为系统默认值） | `PcmigComboBoxRoll` 收起态 `ControlCornerRadius(4)` → `PCMigRadiusInput(14)`；`HighlightBackground` 同改；`PopupBorder` `OverlayCornerRadius(8)` → `PCMigRadiusOverlay(16)`；TaskPicker Presenter/Flyout/Item 三处同规则 | `Themes/PcmigComboBoxRoll.xaml` / `Views/Step2SelectDataPage.xaml` |
| LineHeight 增量（自然行高 + 余量） | `PCMigTextFooterPercent` 24→**26**；`PCMigTextFooterValue` 18→**20**；`PCMigTextTotalPercent` 52→**60**；`PCMigTextStatValue` 28→**32**；`ObjectPaneHintText` 16→**18** | `Themes/Typography.xaml` / `Views/Step3ProgressPage.xaml` |
| 关键数字标签宽度 | `FooterPercentText` `Width 48 → 68` + `TextTrimming None`；`FooterEtaText` `Width 100 → 112` + `TextTrimming None`（依据 Pillow 实测：`100.0%`@17Bold = 61.0 px；`约 23 小时 59 分`@13 = 100.0 px） | `MainWindow.xaml` |
| 锚定浮层宽度 | `ExistingJobsPickerButton_Click` 先调 `AlignExistingJobsFlyoutWidth()`：`ExistingJobsList.Width = Math.Max(180, anchorWidth − 6)`（`flyoutChrome = 2+2+1+1`）；移除 `MinWidth/MaxWidth` 夹取；FlyoutPresenter `MaxWidth 380 → 1200`（仅防御极端视口） | `Views/Step2SelectDataPage.xaml.cs` / `.xaml` |
| 面板上界与入场 | `ShellHintCard.SetMaxSurfaceHeight(double)`（下界 160 DIP）+ `AnimateSurfaceHeight()`（180 ms `CubicEase/EaseOut`，走 XAML `Height`，**不用** Composition `Size/Offset` —— 那不参与布局）；`SetLine()` + `PlayEntrance()`（`Opacity 0→1` + `Offset (0,6,0)→0`，170 ms，只在折叠↔显示或文本真变化时播一次）。调用方：`MainWindow.UpdateHintCardBounds()`（上界 = 侧栏高 − 导航高 − 12） | `Views/ShellHintCard.xaml(.cs)` / `MainWindow.xaml(.cs)` |
| 状态路由 | `ConnectionViewModel` 新增 `FlowStatus` / `InlineNote` 两通道（15 处写入点分流：流程级 → `SetFlowStatus`，字段/错误级 → `SetInlineNote`）；`MigrationSessionViewModel` 订阅 `FlowStatus` 并转投 `SetOperational`；`Step1ConnectPage.xaml:28 StatusLineText` 改绑 `InlineNote` | `Presentation/ConnectionViewModel.cs` / `MigrationSessionViewModel.cs` / `Views/Step1ConnectPage.xaml` |

---

## Accessibility / Reduced Motion（更正）

**修正审计前假设**：Reduced Motion **不是完全缺失**——
- **存在**：`MotionDirector.SystemAnimationsEnabled` 读 `Windows.UI.ViewManagement.UISettings().AnimationsEnabled`（`MotionDirector.cs:56-79`，异常时保守按 `true`）；
- **不存在**：`ReduceMotion` / `AdvancedEffectsEnabled` / `AccessibilitySettings` / 应用内开关；
- **覆盖缺口**：只覆盖 C# Composition 路径；**XAML Storyboard 与 `BrushTransition`/`ScalarTransition` 无任何闸门**（`StepNavigationControl.xaml:82,102,114`）。

PMML 规定的未来行为（本轮不重构）：`Translation ↓`・`Scale ↓/disabled`・`Opacity retained`・`Functional state unchanged`。Gate 中 `Accessibility` 当前填 **Known Gap**。

---

## Light / Dark

- **零 Dark 覆盖（确证）**【子代理：A 审计】：`ThemeDictionaries` 全仓 **0 处**；`RequestedTheme` 仅在 `Themes/TextInputTemplates.xaml:150-152` **强制 Light**；34 个颜色键只按浅色定义。
- **PMML-R9**：Dark 不是另一套设计语言，必须是同一 Surface Family + 不同 Theme 参数。
- 未覆盖部分标 **`PMML v1.0 Known Gap`**（Dark 为其中最大一项），**不为 Freeze 强行补齐**。

---

## 补充：A 审计的三条关键量化事实（子代理，含 `文件:行号`）

1. **阴影 = Scattered Implementation（LMDS 景深靠散落 Z 轴）**：`ThemeShadow` 三键（`Materials.xaml:148`）**零参数**，"强度"由就地 Z 决定 —— 全仓 **37 处 XAML `Translation`** 的 Z 分量（如 `MainWindow.xaml:41/51/115`、`PcmigComboBoxRoll.xaml:288`、`Step3ProgressPage.xaml:31/54/90-108/129/185`、`Step4ResultPage.xaml:53/83/134/225`）+ `PCMigSurface.xaml.cs:99-102` 的 4 处映射，共 **12 个不同 Z 值**（0/1/2/4/6/8/10/12/14/16/24/26），**无 Z Token**；同一 Style 使用点 Z 不一致（`SecondarySurface`：`MainWindow.xaml:115`=10 vs `ShellHintCard.xaml:12`=8）⇒ **L-11**。
2. **近白半透明面 alpha 各自为政**：**18 个不同 alpha**（14/1F/20/26/2B/30/3D/40/42/48/59/5C/5E/AF/C4/D6/EF/F2），分布 `Materials.xaml:19/20/22/24/25/27/138/144/145/146/378/382`、`Colors.xaml:21/26/30`、`Controls.xaml:18-21`；含**同值异键**（`#30FFFFFF` = `InteractiveSurfaceBrush` 与 `BorderSoftBrush`；`#26FFFFFF` = `ButtonSurfaceDisabledBrush` 与 `RateChipSurfaceBrush`）与**近值异键**（`#5CFFFFFF` vs `#5EFFFFFF`）⇒ **L-12**。
3. **高光/浮雕 8 套并存，其中 6 键零引用**：`EdgeHighlightBrush`(`Colors.xaml:14`)、`EdgeHighlightDiagonalBrush`(`:17`)、`EdgeHighlightCrispBrush`(`:20`)、`NavCardEmbossTop/Edge`(`:44-53`)、`PCMigEdgeHighlight`+`PCMigInnerEdge`(`Materials.xaml:309/320`)；在用两套 = DAEL(`Materials.xaml:83-127`) 与 `PCMigSurface` 方向受光四件套(`Materials.xaml:257-284`)；`EdgeHighlightSoftDiagonalBrush`(`:18`) 仅 `FutureNavigationCard`(`Materials.xaml:384`) 一处使用 ⇒ **L-13**。

**资源总量与零引用**【子代理：A 审计】：6 个主题文件共 **222 个 `x:Key`**，其中 **24 个全仓无引用**（清单见 `A-materials-tokens.md` 附录 A），含 `PCMigRadiusStepCard` / `PCMigRadiusButton` / `PCMigRadiusInset` 三个圆角 Token、`PCMigTitleBarGhostButton`、`PCMigToggle`、`PCMigMotionProgressSweepDuration`。
（C 审计按"页面引用"口径统计为 23 项，与 A 的 24 项差异源于统计范围不同，两者均如实保留。）

**Opacity 双轨**【子代理：A 审计】：只有 4 个 `Ambient*Opacity` Token（`Colors.xaml:6`）；控件态/装饰/动效 Opacity **全硬编码**（`Controls.xaml:168/171/174`、`Step1ConnectPage.xaml:34-37`、`MotionDirector.cs:234/479/615/621/640`）；`Motion.xaml` 内 **0 个 Opacity 键** ⇒ **L-14**。

**框架圆角键**：`ControlCornerRadius` / `OverlayCornerRadius` **只被消费、无覆盖**（未在项目内定义）。