# PMML Terminology Registry

> **PMML terminology is project-internal terminology.
> It is not Apple, Microsoft, WinUI or industry official terminology.**

本文档是 PCMig 项目内 **PMML 术语的权威登记表**（Single Source of Truth）。

- 本文档**只做已有术语的登记**，**不新增设计**、**不创造新术语**。
- 本文档中出现的每个术语，其定义与参数均**取自现有源码与既有规范**，不含任何未在代码中出现的主张。
- 与其它文档冲突时：**参数以源码为准**，**规则以 `docs/PMML-UI修改硬性规范.md` 为准**。

| 项目 | 值 |
| --- | --- |
| 文档类型 | Engineering Documentation（术语登记） |
| 登记范围 | PMML 总称 + 9 个术语 + 3 个技术分层 |
| 数据基线 commit | `b68613dc3600767e4e7cf2b24e413e5c91a16280` |
| 状态 | **FROZEN 术语表**（登记，非设计） |

---

## 一、PMML 总称

### PMML

| 字段 | 内容 |
| --- | --- |
| **Identifier** | `PMML` |
| **Full Name** | PCMig Material Motion Language |
| **中文名称** | PCMig 材质动效语言 |
| **技术职责** | PCMig 的**项目内部**材质与动效设计语言的统称；用于命名、描述与冻结已成熟的 UI 表现，使后续新增 UI 有可引用的统一口径 |
| **对应底层实现** | 无单一实现文件。落点分布在 `src/PCMig.WinUI/Themes/`（`Colors.xaml` / `Materials.xaml` / `Motion.xaml` / `Typography.xaml` / `Controls.xaml`）与 `src/PCMig.WinUI/Presentation/`（`MotionDirector.cs` / `InteractionFeedback.cs` / `FluidZoomTransitionCoordinator.cs` / `BackdropSpike.cs` 等） |
| **关联文档** | `docs/PCMig-Visual-Motion-Language.md`（完整规范）、`docs/PMML-UI修改硬性规范.md`（硬性规则）、`docs/PMML-Implementation-Audit.md`（实现审计）、`PMML-Engineering-Overview.md`、`PMML-Implementation-Map.md` |
| **状态** | **FROZEN**（项目所有者 2026-09-30 宣布冻结；2026-10-01 仅文档状态纠正，正文未改） |

**边界声明**：PMML 是 PCMig 内部设计语言，**不是** Apple Human Interface Guidelines、**不是** Microsoft Fluent Design、**不是** WinUI 官方规范，**也不是**任何行业标准术语。文档中出现的技术概念（如 Acrylic、Mica、Composition Animation）可在注明时对应到行业概念，但**不得**把 PMML 描述为官方规范名。

---

## 二、术语登记

术语按技术分层分组。每条的「对应底层实现」均为**代码中真实存在的落点**。

### 2.1 Material System（材质系统）

#### DAM

| 字段 | 内容 |
| --- | --- |
| **Identifier** | `DAM` |
| **Full Name** | Desktop Acrylic Material System |
| **中文名称** | 桌面亚克力材质系统 |
| **技术职责** | 定义窗口 Layer 0/1 的背景材质路线：声明式 backdrop + 运行时装配，以及 Acrylic 的着色/亮度参数 |
| **对应底层实现** | 声明：`src/PCMig.WinUI/MainWindow.xaml:9`（`<MicaBackdrop Kind="BaseAlt"/>`）；装配：`src/PCMig.WinUI/Presentation/BackdropSpike.cs:158`（`Install(Window)`，默认 `kind = "acrylic"`）；参数：`BackdropSpike.cs` 的 `TintOpacity` / `LuminosityOpacity` / `TintColor` / `FallbackColor` |
| **关联文档** | 完整规范 §1/§2；审计文档 DAM 节；`PMML-Implementation-Map.md`（DAM 族 12 条） |
| **状态** | REALIZED（含已登记偏差 L-10：Acrylic 显式参数与 Mica 系统默认回退**不同源**） |

#### LMDS

| 字段 | 内容 |
| --- | --- |
| **Identifier** | `LMDS` |
| **Full Name** | Layered Material Depth System |
| **中文名称** | 分层材质深度系统 |
| **技术职责** | 规定界面的**固定五层**材质深度模型，禁止自创层级 |
| **对应底层实现** | 层与画刷的映射见 `src/PCMig.WinUI/Themes/Materials.xaml`（Surface 样式族）与 `Themes/Colors.xaml`（材质画刷） |
| **关联文档** | 完整规范 §1/§3；`PMML-Implementation-Map.md`（LMDS 族 7 条） |
| **状态** | REALIZED（含已登记偏差 **L-01**：代码实际分层为 `L0 Mica/Ambient → L1 Shell → L2 Card → L3 Input 内嵌 → L4 Selected/Primary`，**无独立 Overlay/Tool 层**；工具浮层走 `PCMigUtilityAcrylic*` 属 DAM 变体） |

**五层定义**（固定，禁止新增）：

| 层 | 名称 | 说明 |
| --- | --- | --- |
| L0 | Backdrop | 窗口背景（DAM 材质） |
| L1 | Base Surface | 基础表面（Shell） |
| L2 | Elevated Surface | 抬升表面（Card） |
| L3 | Interactive Surface | 交互表面（Input 等） |
| L4 | Overlay · Tool Surface | 浮层与工具表面 |

#### DSL-45

| 字段 | 内容 |
| --- | --- |
| **Identifier** | `DSL-45` |
| **Full Name** | 45° Directional Surface Lighting |
| **中文名称** | 45° 方向性表面光照 |
| **技术职责** | 全界面**单一光源**约定（左上 → 右下）；亮边必须与暗边成对出现 |
| **对应底层实现** | `src/PCMig.WinUI/Themes/Colors.xaml`（`NavCardEmbossTopBrush` / `NavCardEmbossEdgeBrush` / `EdgeHighlightDiagonalBrush` / `EdgeHighlightSoftDiagonalBrush` / `EdgeHighlightCrispBrush`）、`Themes/Materials.xaml`（DAEL 画刷族与 `PCMigSurface` 受光四件套） |
| **关联文档** | 完整规范 §4；`PMML-Implementation-Map.md`（DSL45 族 8 条） |
| **状态** | REALIZED（含已登记偏差 **L-13**：高光/浮雕实现有 8 套并存、其中 6 个资源键零引用） |

#### ESR

| 字段 | 内容 |
| --- | --- |
| **Identifier** | `ESR` |
| **Full Name** | Subtle Embossed Material Relief |
| **中文名称** | 细微浮雕材质 |
| **技术职责** | 表面的四件套表现 = `edge highlight` + `subtle border` + `soft shadow` + `material contrast`；统一 `BorderThickness = 1` |
| **对应底层实现** | `src/PCMig.WinUI/Themes/Materials.xaml`（Surface 样式族的 Border/Shadow Setter）、`Themes/Colors.xaml`（边缘画刷） |
| **关联文档** | 完整规范 §5；`PMML-Implementation-Map.md`（ESR 族 3 条） |
| **状态** | REALIZED（含 **G-03**：自定义 Shadow **无数值**，`ThemeShadow` 三键零参数） |

### 2.2 Motion System（动效系统）

#### DCST

| 字段 | 内容 |
| --- | --- |
| **Identifier** | `DCST` |
| **Full Name** | Directional Cross-Slide Transition |
| **中文名称** | 方向性交叉滑动转场 |
| **技术职责** | **页面导航**的统一转场方式（四页切换一律走此路径） |
| **对应底层实现** | `src/PCMig.WinUI/Presentation/MotionDirector.cs`（`PlayPagePush`）、`Presentation/PageTransitionCoordinator.cs`（只写 Visibility，方向与位移透传） |
| **关联文档** | 完整规范 §6；`PMML-Implementation-Map.md`（DCST 族 8 条） |
| **状态** | REALIZED（含已登记偏差 **L-05**：`Motion.xaml:14-15` 注释与实现不符，**PMML 以生效实现为准**） |

**生效参数**（以源码为准）：

| 参数 | 值 |
| --- | --- |
| 时长 | `PCMigMotionPagePushDuration = 0:0:0.42` |
| 位移 | **整页视口高度**纵向 Push（旧 `PageSlideDistance` 26 已废弃） |
| Opacity | **恒 1**（**不做淡入淡出**） |
| Scale | **无** |
| 缓动 | `CubicBezier (0.10,0.90)(0.20,1.00)` |
| 方向 | forward / backward 仅符号相反（对称） |
| 兜底 | 时长 + `140 ms` |

#### OACT

| 字段 | 内容 |
| --- | --- |
| **Identifier** | `OACT` |
| **Full Name** | Origin-Anchored Container Transform |
| **中文名称** | 源点锚定容器形变 |
| **技术职责** | **工具浮层**的开关动效：被点击入口本身连续 morph 成完整面板，主视觉是**几何形变**而非淡入 |
| **对应底层实现** | `src/PCMig.WinUI/Presentation/FluidZoomTransitionCoordinator.cs`（主路径，`ApplyFrame()` 为相位权威）、`Presentation/MotionDirector.cs`（fallback 路径） |
| **关联文档** | 完整规范 §7；`PMML-Implementation-Map.md`（OACT 族 14 条） |
| **状态** | REALIZED（含已登记偏差 **L-03b**：`Motion.xaml` 相位注释与实际参数不符） |

**生效参数**：

| 参数 | 值 |
| --- | --- |
| Open | `PCMigFluidZoomOpenDuration = 0:0:0.36` |
| Close | `PCMigFluidZoomCloseDuration = 0:0:0.30` |
| 兜底 | 时长 + `120 ms` |
| Fallback（**已被用户否决为主路径**） | Origin-aware Reveal：`Reveal 0.26` / `Dismiss 0.19` / `RevealOffset 7` / `RevealScale 0.945` / `DismissScale 0.985` / `DismissOffset 5` |

**用户否决记录**：上一版 Origin Reveal（Fade + 0.945 Scale）被用户人工否决为「本质还是淡入弹窗」；Token 保留但**只作 fallback**。

#### TAOP

| 字段 | 内容 |
| --- | --- |
| **Identifier** | `TAOP` |
| **Full Name** | Trigger-Anchored Overlay Placement |
| **中文名称** | 触发锚定浮层定位 |
| **技术职责** | 规定浮层**出现在哪里**：由真实触发入口的位置计算落点（谁触发就锚谁） |
| **对应底层实现** | `src/PCMig.WinUI/MainWindow.xaml.cs:1604`（`TryPlacePanel`）、`:1215`（`OtherVisibleToolPanel` 互斥辅助）、`PositionOverlayPanels()` |
| **关联文档** | 完整规范 §8；`PMML-Implementation-Map.md`（TAOP 族 9 条） |
| **状态** | REALIZED（R12 正面案例：Task Picker 为正式参考实现） |

**归属表**：

| 对象 | 定位方式 |
| --- | --- |
| Task Picker（`Step2SelectDataPage`） | ✅ TAOP 正式参考实现（`Flyout Placement="BottomEdgeAlignedLeft"`） |
| 材质调节面板 | TAOP + OACT |
| 更新日志面板 | TAOP + OACT |
| 验证进度面板 | TAOP + OACT |
| 诊断中心面板 | TAOP + OACT |
| 线程数 ComboBox | **原生 ComboBox 内部定位**（非 TAOP） |

**已登记失败教训**：为修正原生 ComboBox 浮层位置而加的 `Popup.VerticalOffset` 后置校正**实测无效，已删除**（连带 `AlignJobsDropDownBelowCombo()` 与 `DropDownOpened` 订阅一并移除）。

#### MHE

| 字段 | 内容 |
| --- | --- |
| **Identifier** | `MHE` |
| **Full Name** | Material Hover Elevation |
| **中文名称** | 材质悬停抬升 |
| **技术职责** | 悬停/按下的统一反馈方式：**只允许 Translation / Elevation**，**禁止 Scale 放大** |
| **对应底层实现** | `src/PCMig.WinUI/Presentation/InteractionFeedback.cs`（只走 Translation 通道，全文件无任何 Scale 或 CenterPoint 写入） |
| **关联文档** | 完整规范 §9；`PMML-Implementation-Map.md`（MHE 族 11 条） |
| **状态** | REALIZED（含 **G-05**：Step Card 悬停零反馈的根因未完全定位） |

**七 Token**：

| Token | 值 |
| --- | --- |
| `PCMigMotionHoverEnterDuration` | `0:0:0.13` |
| `PCMigMotionHoverExitDuration` | `0:0:0.15` |
| `PCMigMotionPressedDuration` | `0:0:0.10` |
| `PCMigMotionHoverLiftCard` | `2` |
| `PCMigMotionHoverLiftButton` | `1.3` |
| `PCMigMotionPressedSinkCard` | `0.5` |
| `PCMigMotionPressedSinkButton` | `0.6` |

**单位纪律**：Canonical DIP，与 `UniformScaleHost` 同坐标系 ⇒ **不得再乘 `ApplicationUIScale`**（否则双倍位移）。

### 2.3 Progress Visual System（进度视觉系统）

#### Immersive Transfer Progress

| 字段 | 内容 |
| --- | --- |
| **Identifier** | `Immersive Transfer Progress` |
| **Full Name** | PCMig Immersive Transfer Progress |
| **中文名称** | PCMig 沉浸式迁移进度 |
| **技术职责** | 迁移进度的高保真视觉表达；把「已确认的迁移事实」与「任务活跃度」在视觉上严格分离 |
| **对应底层实现** | `src/PCMig.WinUI/Controls/ImmersiveProgress/`（7 文件：`ImmersiveTransferProgress.xaml(.cs)` / `ImmersiveTransferProgressRenderer.cs` / `ImmersiveProgressAnimationState.cs` / `ImmersiveProgressParticle.cs` / `ImmersiveProgressParameters.cs` / `ImmersiveTransferProgressAutomationPeer.cs`）；生产接线 `Views/Step3ProgressPage.xaml(.cs)`（Hero）与 `MainWindow.xaml:123`（Footer Compact） |
| **关联文档** | 完整规范附录 B；硬性规范 R21–R36；`PMML-Implementation-Map.md`（PROG 族 12 条 + IP 族 7 条） |
| **状态** | REALIZED（含 **G-08** 证据缺口：三档 `EffectsQuality` 与局域光学耦合缺真机视觉对照） |

**五种语义严格分离**：

| 视觉元素 | 语义 | 是否影响 `Value` |
| --- | --- | --- |
| Progress Head | **已确认的迁移事实**（它就是事实的图形表示） | 是（它就是真值） |
| Push Band | 任务活动 | **绝不** |
| Particles | 数据物质微弱流动 | **绝不** |
| Ripple | 局部交互反馈 | **绝不** |
| Head Halo · Rim | 当前活跃前沿的光照 | **绝不** |

**渲染所有者只有一个** = Win2D（官方 NuGet，固定版本 `Microsoft.Graphics.Win2D 1.4.0`）。

**必须遵守的核心规则**（详见硬性规范）：

- **R25** 填充元素常驻满宽，可见长度只由 `InsetClip.RightInset` 标量表达；**禁止**按帧写 `Width` 等布局属性
- **R31** Capsule Integrity：Host **永远透明**，所有动态层服从同一 Capsule 几何；**禁止用矩形裁剪**让圆角端部变成平切
- **R34** Volumetric Head Halo：Head 高光必须是连续衰减的**局部体积光场**，**禁止**以可辨识 Stroke/Rim 作为主要发光
- **R35** Pause Keeps Material Alive：`Paused`/`Interrupted` **冻结事实**但**保留材质活性**；只有 `Failed`/`Completed` 才是终止态
- **R36** Embedded Material Context：嵌入卡片时 Host 必须透明并真正透出父 Surface（生产宿主用 `CanvasControl`，**不用**交换链宿主）

---

## 三、技术分层总览

```
PMML — PCMig Material Motion Language
│
├── Material System（材质系统）
│   ├── DAM      Desktop Acrylic Material System     桌面亚克力材质系统
│   ├── LMDS     Layered Material Depth System       分层材质深度系统（L0–L4 五层）
│   ├── DSL-45   45° Directional Surface Lighting    45° 方向性表面光照
│   └── ESR      Subtle Embossed Material Relief     细微浮雕材质
│
├── Motion System（动效系统）
│   ├── DCST     Directional Cross-Slide Transition  方向性交叉滑动转场（页面导航）
│   ├── OACT     Origin-Anchored Container Transform 源点锚定容器形变（浮层开关）
│   ├── TAOP     Trigger-Anchored Overlay Placement  触发锚定浮层定位（浮层落点）
│   └── MHE      Material Hover Elevation            材质悬停抬升（悬停/按下）
│
└── Progress Visual System（进度视觉系统）
    └── Immersive Transfer Progress                  沉浸式迁移进度
```

**不属于 PMML 术语的相邻概念**（登记以免误用）：

| 概念 | 说明 |
| --- | --- |
| `Roll Reveal` | ComboBox 下拉的**自上而下卷帘**效果（`Themes/PcmigComboBoxRoll.xaml`），属 Motion Family 表内条目，但**不是**独立缩写术语 |
| `PMML State Transition` | 控件 VisualState 的标准状态过渡（时长取 `Fast/Normal/Slow` = `0.14/0.20/0.26`），Family 名，非缩写 |
| `PCMigSurface` | 控件类名（`Views/PCMigSurface.xaml`），是实现载体，非 PMML 术语 |
| `DAEL` | 画刷族前缀（`PCMigDAELBrush` / `PCMigDAELBrushSubtle`），是实现命名，非独立缩写 |

---

## 四、术语使用纪律

1. **不得创造新术语**。新增缩写必须先有实现、再登记到本文档，顺序不可颠倒。
2. **不得把 PMML 术语描述为官方规范**。可注明「对应行业概念」，但不得写成 Apple / Microsoft / WinUI 官方名。
3. **同一概念不得有两个名字**。已登记的 Identifier 为唯一写法。
4. **术语与实现的对应关系可被验证**。每条登记的「对应底层实现」必须能在源码中定位；行号会漂移，符号名与文件路径是稳定锚点。
5. **状态字段不是装饰**。`REALIZED` / `SCATTERED` / `KNOWN GAP` / `STALE` 等状态如与源码不符，应更新本文档而非忽略。

---

## 五、关联文档

| 文档 | 定位 |
| --- | --- |
| `docs/PCMig-Visual-Motion-Language.md` | PMML v1.0 完整设计规范（FROZEN） |
| `docs/PMML-UI修改硬性规范.md` | 改 UI 前必读的硬性规则（R1–R36 唯一权威清单） |
| `docs/PMML-Implementation-Audit.md` | v0.5.0 UI 真实世界实现审计 |
| `docs/PMML-Legacy-Deviations.md` | Legacy Deviation 与 Known Gap 登记簿 |
| `docs/pmml-engineering/PMML-Engineering-Overview.md` | 本档案体系的入口总览 |
| `docs/pmml-engineering/PMML-Implementation-Map.md` | 术语 → 源码落点索引地图 |
| `docs/pmml-engineering/PMML-Source-Traceability.csv` | 152 条逐条源码追溯 |
| `docs/pmml-engineering/PMML-Source-Traceability-Methodology.md` | 追溯方法论 |
| `docs/pmml-engineering/PMML-Evidence-Classification.md` | 证据分级与 24 条 NO_PROBE 分类 |
| `docs/pmml-engineering/PMML-Engineering-Closure-Report.md` | 本档案体系收口报告 |
