# PMML Legacy Deviations & Known Gaps（v1.0）

> 本文件登记 PCMig v0.5.0 中**已存在但尚未统一**的历史差异（`Legacy Deviation`）与**尚未覆盖**的缺口（`PMML v1.0 Known Gap`）。
> **本轮 PMML v1.0 不修任何一条**（用户口径：Freeze 本身不能破坏成熟 UI；发现问题只登记，不扩大战线）。
> 每条都带 `文件:行号`，可逐条回读源码。来源：`archive/pmm-l-audit/`（A/B/C 三份原始审计）+ 主控复核。

---

## 一、Legacy Deviations（历史不统一）

| # | 现象 | 证据（文件:行号） | 影响面 | 建议（未来单独清理，不在本轮） |
|---|---|---|---|---|
| **L-01** | **代码内 L0–L4 与 PMML 冻结口径语义不同**：代码 L3 = Input 内嵌、L4 = Selected/Primary，**没有独立 Overlay/Tool 层**；工具浮层走 `PCMigUtilityAcrylic*`（DAM 变体） | `Themes/Materials.xaml:2-9`（声明原文）；`Materials.xaml:376-377`（Utility Acrylic） | LMDS 语义 | 未来统一层级命名（保留既有键为别名） |
| **L-02** | **底栏层级归属与结构不一致**：结构上属 Shell 级行，材质却用 L2 的 `SecondarySurface`→`PCMigCardMaterial` | `MainWindow.xaml:115` | LMDS | 未来归入 L1/L2 明确定义 |
| **L-03** | **页面步骤徽章 5 份重复手写**（`#B8FFFFFF` + `CornerRadius="17"` + 内层高光 + `ElevationHigh`），未 Token 化 | `Step1ConnectPage.xaml:21,46`・`Step2SelectDataPage.xaml:197-208`・`Step3ProgressPage.xaml:31-42`・`Step4ResultPage.xaml:53-64` | ESR/Token | 提取为共享 Style |
| **L-03b** | **OACT 时间轴注释与实现不符**：`Motion.xaml` 写「0→45% 表面 / 30–72% 边缘光 / 55% 起接管」，实际以 `ApplyFrame()` 为准：fill **0→32%**（初始 factor 0.22）、stroke **10→55%**、panel **55→100%**、shell fade **80→100%**、source **0→22%/22→70%/70→88%**、geometry `EaseOutCubic`、其它通道 staged linear Segment | 注释 `Themes/Motion.xaml:34` 区；实现 `Presentation/FluidZoomTransitionCoordinator.cs:355-404`（`:371,:372,:373,:389,:402-404,:360`） | 文档一致性 | 改注释（零视觉影响） |
| **L-04** | **Step Card 选中描边不可见**：代码设了 `AccentEdgeBrush`，但宿主 `BorderThickness="0"` ⇒ 描边被丢弃；同语义 `SelectedNavigationSurface` 反而是 `1` 且**零引用** | `Presentation/StepNavigation.cs:116`・`Views/StepNavigationControl.xaml:79`・`Materials.xaml:179` | ESR/交互 | 二选一收敛（属 UI 行为变更，需授权） |
| **L-05** | **注释与实现不符**：注释称"旧页上移淡出、新页自下方进入淡入"，实际 **Opacity 恒 1、整页视口高度 Push** | 注释 `Themes/Motion.xaml:14-15`；实现 `Presentation/MotionDirector.cs` | 文档一致性 | 改注释（零视觉影响） |
| **L-06** | **死代码 + 过期注释**：`PreparePageEntrance` / `PreparePanelEntrance` / `PlayPanelExit` 无调用点，但 3 处 XAML 注释仍声称在运行 | `MotionDirector.cs:119-137,142-157,165-218`；`ChangelogPanel.xaml:5-7`、`DeveloperTuningPanel.xaml:16-18`、4 个 Step 页注释 | 维护性 | 删死代码或改注释（需授权） |
| **L-07** | **无消费者 Token ⇒ 声称的动效不存在**：`DurationNormal(0.20)`、`DurationSlow(0.26)`、`EaseStandard`、`DialogScaleStart(0.98)`；~~`ProgressSweepDuration(1.60)`~~ **已于 2026-10-05 接线**（`ProgressMotionDriver.ResolveSweepSeconds()` 读 Token，兜底常量同值） | `Themes/Motion.xaml`；`Presentation/ProgressMotionDriver.cs` | Motion | 其余仍删除或真正接线（需授权） |
| **L-08** | **缓动四族并存**：`(0.10,0.90)/(0.20,1.00)`、`(0.16,1.0)/(0.30,1.0)`、手写 `EaseOutCubic`、`CubicEase` | `MotionDirector.cs:370,565`・`InteractionFeedback.cs:300`・`FluidZoomTransitionCoordinator.cs:578-582`・`PcmigComboBoxRoll.xaml:219,229` | Motion | 收敛为 `standard`/`exit` 两键 |
| **L-09** | **零引用资源**：24 项（A 审计口径；C 审计按页面引用口径为 23 项） | 清单见 `A-materials-tokens.md` 附录 A；例：`PCMigRadiusStepCard/Button/Inset`、`PCMigTitleBarGhostButton`、`PCMigToggle`、`Views/PCMigSurface.xaml` 整套控件 | Token/维护性 | 逐项决定删除或接线 |
| **L-10** | **Backdrop 两条路线参数不同源**：Acrylic 显式 `TintOpacity 0.02 / Luminosity 0.00`，Mica 回退用**系统默认** | `BackdropSpike.cs:189-192` vs `:264-266`；`MainWindow.xaml:8` | DAM | 统一参数来源 |
| **L-11** | **景深靠散落 Z 轴**：`ThemeShadow` 三键零参数，"强度"由就地 Z 决定；**37 处 XAML Translation**、**12 个不同 Z 值**（0/1/2/4/6/8/10/12/14/16/24/26），无 Z Token；同一 Style 使用点 Z 不一致 | `Materials.xaml:148`；`MainWindow.xaml:41/51/115`・`PcmigComboBoxRoll.xaml:288`・`Step3ProgressPage.xaml:31/54/90-108/129/185`・`Step4ResultPage.xaml:53/83/134/225`；`PCMigSurface.xaml.cs:99-102`；`MainWindow.xaml:115`=10 vs `ShellHintCard.xaml:12`=8 | LMDS/ESR | 提取 Z Token |
| **L-12** | **近白半透明面 alpha 各自为政**：18 个不同 alpha；含同值异键与近值异键 | `Materials.xaml:19/20/22/24/25/27/138/144/145/146/378/382`・`Colors.xaml:21/26/30`・`Controls.xaml:18-21` | DAM | 归并到 Surface Family |
| **L-13** | **高光/浮雕 8 套并存，6 键零引用** | `Colors.xaml:14/17/20/44-53`・`Materials.xaml:309/320`；在用：`Materials.xaml:83-127`、`257-284`；`:18` 仅 `:384` 一处用 | DSL-45/ESR | 收敛为 1–2 套 |
| **L-14** | **Opacity 双轨**：仅 4 个 `Ambient*Opacity` Token；控件态/装饰/动效 Opacity 全硬编码；`Motion.xaml` 内 0 个 Opacity 键 | `Colors.xaml:6`；`Controls.xaml:168/171/174`・`Step1ConnectPage.xaml:34-37`・`MotionDirector.cs:234/479/615/621/640` | Token | 未来提取 Token |
| **L-15** | **硬编码替代 token**：Step4 日志面板用字面量 `#F21A2033`/`#33FFFFFF`，而同值 token `PCMigSemanticDarkMaterial` 零引用 | `Step4ResultPage.xaml:225`；`Materials.xaml:29` | DAM/Token | 改用 token |
| **L-16** | **PMML 相关注释过期**：`PcmigComboBoxRollStyle` 注释称"那两个 ComboBox"，实际**只有 1 个消费点**（ThreadsCombo） | `Step2SelectDataPage.xaml:390-391` | 文档一致性 | 改注释 |
| **L-17** | **圆角字面量散落**（2026-10-05 审计新增）：17 个 XAML 文件共 **50 处 `CornerRadius=`**，其中 **33 处为字面量**（16×7、10×8、17×4、18×3、12×3、15×2、11/9/7/3/2 各 1、`12,12,0,0`×1），与新增的 §19.1 八档体系不符。**本轮已收敛三条链**（ComboBox 收起态 / Popup 浮层 / TaskPicker 三段），其余留待单独授权清理 | `grep -rn 'CornerRadius="' src/PCMig.WinUI`（逐处见 `PMML-Implementation-Audit.md` 附录 A §19.1） | Token/ESR | 逐处归入 §19.1 八档后删除字面量 |
| **L-18** | **三个圆角 Token 仍零引用**：`PCMigRadiusStepCard(16)`、`PCMigRadiusButton(12)`、`PCMigRadiusInset(13)`（本轮新增的 `PCMigRadiusOverlay` / `PCMigRadiusListItem` **已有消费点**，不再是零引用） | `Themes/Materials.xaml:162/163/164`（定义）；除注释 `:206` 外无消费点（L-09 的子集） | Token | 接线或删除（见 L-09） |
| **L-19** | **旧进度动效路线仍留在仓库（2026-10-05 Round-3 新增）**：`Presentation/ProgressMotionDriver.cs` 已被执行书 §7 **冻结**（只许下线/删除、不许再扩展），并且 **Step3 生产路径对它的引用数已为 0**（旧 `<Border PCMigProgressTrack>` + `TotalProgressFill` 已整体换成 `controls:ImmersiveTransferProgress`）；但 **底栏仍用它**（`MainWindow.xaml.cs` 的 `_footerMotion`）。附带影响：L-07 中「`ProgressSweepDuration` 已接线」这条现在指向的是一条**已冻结**的路线（`PCMigMotionProgressSweepDuration` → 冻结驱动） | `Presentation/ProgressMotionDriver.cs`；`MainWindow.xaml.cs`（`_footerMotion`）；`MainWindow.xaml:122`（`FooterProgressFill`）；对照：`Views/Step3ProgressPage.xaml`（已无引用） | Motion / 维护性 | 二选一收敛：①底栏改成 `ImmersiveTransferProgressVariant.Compact`（**必须做变体，不许复制代码**）后整体删除旧路线；②保留底栏旧条但删除驱动中已无消费者的 Sweep 分支。**需单独授权** |
| **G-08** | **新进度视觉语言的证据缺口（2026-10-05 Round-3 新增）**：① `EffectsQuality` 三档（High/Balanced/Reduced）与 **Band↔粒子局域光学耦合**只有代码与单元测试，**缺真机视觉对照**；② 五档 DPI（100/125/150/175/200%）未取证；③ 提示卡四通道饱和与内部滚动的像素证据仍缺；④ 运行期 30 s 性能采样（CPU/GPU/帧率/GC）未做 | 探针页与 `timeline.csv` 已覆盖判据 ①~③⑤⑦⑨⑩⑪⑫⑬；耦合与三档对照见 `docs/PCMig-Visual-Motion-Language.md` 附录 B.N 的 OPEN 项 | 证据充分性（`PMML-R30`） | 需要时按 B.N 重跑探针与生产场景并补录屏；未做之前一律标注 `NOT VISUALLY VERIFIED` |

---

## 二、PMML v1.0 Known Gaps（尚未覆盖 / 未实现）

| # | 缺口 | 现状（证据） | PMML 规定 |
|---|---|---|---|
| **G-01** | **Dark 主题零覆盖** | `ThemeDictionaries` 全仓 **0 处**；`RequestedTheme` 仅在 `TextInputTemplates.xaml:150-152` 强制 Light；34 个颜色键只按浅色定义 | 同一 Surface Family + 不同 Theme 参数（**PMML-R9**）；本轮不补 |
| **G-02** | **Reduced Motion 只覆盖一半** | **存在**：`MotionDirector.SystemAnimationsEnabled` 读 `UISettings.AnimationsEnabled`（`MotionDirector.cs:56-79`）；**缺口**：XAML Storyboard 与 `BrushTransition`/`ScalarTransition` 无闸门（`StepNavigationControl.xaml:82,102,114`）；无 `ReduceMotion`/`AdvancedEffectsEnabled`/应用内开关 | `Translation ↓`・`Scale ↓/disabled`・`Opacity retained`・`Functional state unchanged`（**PMML-R8**）；本轮不重构 |
| **G-03** | **Shadow 无数值** | `ThemeShadow` 三键零参数（`Materials.xaml:148`），blur/opacity/offset 由框架决定 | 只按 Low/Medium/High 命名语义冻结 |
| **G-04** | **Dialog 动效不存在**（Progress Sweep 已于 2026-10-05 实现） | `DialogScaleStart` 仍无消费者（`Motion.xaml`）；`ProgressSweepDuration(1.60)` **已接线** —— `PCMigMotionProgressSweepDuration` → `ProgressMotionDriver.SweepSeconds`（见 Audit「Progress 族 UI Closure 更新」） | Dialog 若要实现必须走 OACT / PMML State Transition |
| **G-05** | **Step Card 零反馈根因未完全定位** | 子代理 C 自述未定位（与 L-04 相关但不完全等价） | 登记待查 |
| **G-06** | **未做真机视觉实测** | 本轮为**纯代码审计**（用户口径：不重新设计、不改 UI）；`PcmigComboBoxRoll.xaml`（330 行）仅抽样取证 | 如需视觉冻结证据，另开授权轮 |
| **G-07** | **字体/字号体系未逐页 Token 化核对** | Typography 逐页计数见 `C-surfaces-controls.md` §C；存在页面直接写字号的情况待核 | 未来纳入 `PMML.Typography.*` |

---

## 三、处理口径（冻结纪律）

1. **本轮不动任何一条**：PMML v1.0 只做"命名 / 描述 / 冻结"。
2. 任何未来清理都必须：**单独授权 → 走 PMML Compliance Gate → 在说明里引用本文件条目号**。
3. 若某次改动**新引入**了不统一：Gate 里 `Legacy Deviation Introduced` 填 `YES`，并**同时在本文件新增一条**。
4. `Known Gap` 不是"可以随便偏离"的借口：缺口范围内仍受 PMML-R1…R15 约束（例如：即使没有 Dark，也不得新增手写的白色半透明框）。