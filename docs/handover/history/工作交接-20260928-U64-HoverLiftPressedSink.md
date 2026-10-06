# 工作交接 — 20260928 — U64 Hover Lift / Pressed Sink 恢复（UI 收尾冻结）

> 交接对象：接手 PCMig v0.5.0 UI 收尾工作的下一位（或下一个会话）
> 权威工作区：`<仓库根>`
> 本轮范围：**恢复历史交互反馈**（Hover 上浮 / Pressed 下沉）——用户明确"这不是新设计需求，是恢复 v0.4.5 已定义过、v0.5.0 大改中丢失的能力"
> 本轮终点：**已实现 · 构建 0 错误 · 单元测试 130 通过 / 0 失败 · 用户人工确认"全部完美" → PCMig v0.5.0 UI 全面冻结**
> 前置交接：`docs\工作交接-20260928-U62-FluidZoomTransition.md`（U62/U63，其中已追加"人工验收结论"节）

---

## 一、本轮做了什么（U64）

| 项 | 内容 |
|---|---|
| Hover Lift | 侧栏 Step Card 上浮 **2 DIP**（Token `PCMigMotionHoverLiftCard`）、普通按钮 **1.3 DIP**（`...LiftButton`） |
| Pressed Sink | Card 下沉 **+0.5 DIP**（`...SinkCard`）、按钮 **+0.6 DIP**（`...SinkButton`） |
| 时长 | Enter **130 ms** / Exit **150 ms** / Pressed **100 ms**（三个 Token） |
| 缓动 | cubic-bezier `(0.10,0.90)(0.20,1.00)` —— 与页面 Push / 面板链同一族手感（快速响应 + 柔和收尾，无回弹） |
| 主反馈 | **只用 Translation**，不用 Scale（用户第 6 节：Scale 会让文字发虚、边缘变形） |
| 排除项 | **标题栏两个工具入口不参与**：`DeveloperTuningButton`（材质调节齿轮）、`ChangelogButton`（v0.5.0 徽章） |

**实现**：新增 `Presentation\InteractionFeedback.cs`（约 470 行）
- 在**应用根**上挂一次路由指针事件 ⇒ 一次覆盖全部按钮（含 DataTemplate 里的侧栏卡与各 ControlTemplate 内部按钮），**不动任何 Template / 几何 / 材质**；
- 命中的"可交互宿主"**只认 `Button`** ⇒ 纯展示 Card（报告清单 / 实时日志 / 统计卡 / 对象明细 / 正在复制）天然无 Hover（用户第 9 节：避免误导"可以点"）；
- 位移走 Composition `Translation` 通道，**起点取元素当前实际位移**（接管式）⇒ 快速扫过/快速点击不跳变、不残留；
- `MotionDirector.SystemAnimationsEnabled == false` 时直接落到正确终态（不播过渡，但状态必须正确）；
- 单位是 Canonical DIP，由 UniformScaleHost 统一缩放 ⇒ **绝不再乘 `ApplicationUIScale`**（避免双倍位移）。

---

## 二、★ 本轮最重要的技术发现（下次不要再走弯路）★

### 2.1 `PointerEntered / PointerExited` 的路由策略是 **Direct** —— 祖先 `AddHandler` 收不到

上一个会话留下过一句"指针移动事件没有到达这些卡片，根因未定位"，因此把 Hover 实现整体回退、只留注释。
本轮用 `PCMIG_HOVER_DIAG=1` 日志拿到直接证据：**用户手动把鼠标移过四张 Step 卡时，日志里只有 `Pressed/Released`，
从未出现独立的 Hover** —— 即按键类（冒泡）事件能到，`PointerEntered` 到不了。

**修法**：hover 状态机改由**冒泡的 `PointerMoved`** 驱动 —— 每次移动取 `e.OriginalSource`、向上找 `Button`，
**只在"当前悬停宿主"变化时**切换状态（鼠标静止时零开销）。这才是能收到真实鼠标移动的路径。

### 2.2 合成输入**无法**验证 Hover（只能验证 Pressed）

`SetCursorPos` 与 `mouse_event(MOVE|ABSOLUTE)` 注入的移动在 WinUI 3 里**不被指针管线处理**
（实测：不按按键时 `PointerMoved` 完全不触发；一旦发生按键，后续移动才开始有 `PointerMoved`）。
⇒ 自动化截图里 Hover 恒为 0 变化（`PrintWindow` 与 `CopyFromScreen` 都试过），而 `Pressed` 可以用合成输入验证
（实测：卡区 1800 px、按钮区 725 px 变化）。

**结论**：**Hover 只能靠人工实测**（本轮即由用户确认"全部完美"）。不要因为自动截图无变化就判定实现失败。

### 2.3 修复历史遗留坑：不要把 Lift 写在两个地方

`PCMigPrimaryButton` 模板里原本已有一对 `Setter Target="Root.Translation"`（`0,-1,7` / `0,1,2`）。
若与新的统一层叠加 ⇒ **双倍位移**。本轮已把模板里那两处**移除**，改由 `InteractionFeedback` 统一驱动
（同时满足用户"数值集中在 Token、不要 magic number"的要求）。其余按钮模板本来就没有 Translation Setter。

---

## 三、三批改动的最终状态（本会话全部）

| 批次 | 主题 | 状态 | 涉及文件 |
|---|---|---|---|
| **U62** | PCMig Fluid Zoom Transition（两个 Utility Panel 的入口 morph） | ✅ 人工验收 · **冻结** | 新增 `FluidZoomTransitionCoordinator.cs`；改 `MainWindow.xaml.cs`、`Themes\Motion.xaml` |
| **U63** | 左侧四步导航 Selected / Unselected 状态颜色 | ✅ 人工验收 · **冻结** | 改 `StepNavigation.cs`、`Views\Step3ProgressPage.xaml` |
| **U64** | Hover Lift / Pressed Sink 恢复 | ✅ 人工确认"全部完美" · **冻结** | 新增 `InteractionFeedback.cs`；改 `MainWindow.xaml.cs`、`Themes\Motion.xaml`、`Themes\Controls.xaml` |

**本会话共动 8 个文件**（2 个新增 + 6 个修改）。可对照的完整代码副本与逐文件改动明细见
`<用户目录D>\Desktop\<桌面交付根>\复验说明.md`（含 47 个 UI 源码 + 3 个 UI 契约测试，已逐位校验与工作区一致）。

---

## 四、★ 全面冻结声明（用户口径）★

> 用户原话：「OK,现在已经全部完美，可以收尾了」；此前："这个交互恢复完成以后，PCMig v0.5.0 UI 全面冻结。
> 以后除非明确 Bug，否则不再继续 UI 重构。"

**不要再改**：Desktop Acrylic · DAEL 基础参数 · Material · CornerRadius · Button / Step Card 几何 ·
Sidebar 布局 · Typography · Spacing · UniformScaleHost · Full-Viewport Push · Fluid Zoom · Step1–Step4 布局 ·
Utility Panel · 颜色体系 · Selected 状态 · Hover/Pressed 的位移量与时长 Token。

**用户未要求、因而刻意不做**（后续会话不要"顺手优化"）：
1. 其余 5 处"非选中区块却用强调蓝"（`Step1ConnectPage` L23「可用共享」图标、`Step2SelectDataPage` L85/L136 区块图标、
   `DeveloperTuningPanel` L41 面板标题图标、`ChangelogPanel` L42 版本号文字）—— 保持现状；
2. 侧栏 Step Card 的四个"默认按钮外观"透明画刷覆盖（`StepNavigationControl.xaml` L44-49）—— 保持现状；
3. Fluid Zoom 的可选增强（额外 `SpriteVisual` + `DropShadow`、`EaseOutQuart` 更快起手）—— 不做；
4. 标题栏两个工具入口的 Hover —— 按用户要求**永远排除**。

---

## 五、回退（三批互不影响，可单独回退）

| 批次 | 回退步骤 |
|---|---|
| U64 | 删 `Presentation\InteractionFeedback.cs`；`MainWindow.xaml.cs` 删 `Install(...)` 与两行 `Exclude(...)`；`Themes\Motion.xaml` 删 U64 Token 块；`Themes\Controls.xaml` 的 `PCMigPrimaryButton` 恢复 `Root.Translation` 两处 Setter |
| U63 | `StepNavigation.cs` 两行改回 `IsSelected \|\| IsVisited`；`Step3ProgressPage.xaml` 两个图标改回 `AccentBrush` |
| U62 | 删 `FluidZoomTransitionCoordinator.cs`；还原 `MainWindow.xaml.cs` 的 `OpenPanel`/`ClosePanel`；`Motion.xaml` 删两个 `PCMigFluidZoom*Duration` |

**改动前快照**：`archive\tmp\*.before-u62-fluidzoom.*`、`*.before-u63-selectionstate.*`（U64 的改动全部是纯增量或可精确还原的小删除，步骤见上表与复验说明）。
**清空前的上一版桌面归档**：`archive\backup-新建文件夹4-20260928-u64\`（131 文件，含 U62/U63 的全部证据与文档）。
**主文件级快照（勿删）**：`<镜像备份根>\PCMig-v0.5.0-pre-responsive-motion-20260928-131622`。
**Git：HEAD 仍 `c9aef30`（不含本会话成果）。不要 `reset --hard` / `checkout .` / `clean -fd`。**

---

## 六、复验与验证命令

```powershell
cd "<仓库根>"
dotnet build src\PCMig.WinUI\PCMig.WinUI.csproj -c Release -m:1   # 必须 -m:1；.sln 不含 WinUI 项目
dotnet test tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj -c Release -m:1   # 130 通过 / 0 失败
```
诊断开关：`PCMIG_HOVER_DIAG=1`（Hover 状态与位移日志）、`PCMIG_FLUIDZOOM_DIAG=1`（Fluid Zoom 日志）、
`PCMIG_MOTION_SLOWMO=<倍数>`（慢放取证）、`PCMIG_UNIFORM_HOST=1`（等比缩放宿主）。全部默认关闭，不影响产品行为。

---

## 七、局限（如实记录，不许当已验收）

1. **Hover Lift 的证据形式与 Fluid Zoom 不同**：合成输入无法驱动它（见 2.2），所以**没有截图/录屏级证据**；
   但有**完整的事件日志证据链**（`archive\screenshots\u64-hover\hover-evidence-user-manual.log`，
   1039 行，来自真实鼠标操作），关键判据逐条可查：

   | 判据 | 日志实测 |
   |---|---|
   | Card 档位移 | `AnimateTo Hover card=True ... to=(0.00,-2.00,0.00) span=130ms` → 动画后 `comp=(0.00,-2.00,0.00)` ✓ 精确 −2 DIP |
   | Button 档位移 | `AnimateTo Hover card=False ... to=(0.00,-1.30,0.00)` → 动画后 `comp=(0.00,-1.30,0.00)` ✓ 精确 −1.3 DIP |
   | 接管式起点 | 34 条回落记录的 `from=(0.00,-2.00,0.00)` —— 起点取**当前值**而非固定 0 ⇒ 快速扫过不跳变 |
   | 排除名单 | `DeveloperTuningButton` / `ChangelogButton` 在**全日志中出现 0 次** ✓ 两个工具入口确实不参与 |
   | 覆盖范围 | 日志里出现 `StepCardButton`、`ConnectButton`、`PasswordRevealButton` 等宿主 |
   | 不动布局 | 每条记录都是 `comp=(0,-2,0)` 而 `xaml=(0.00,0.00,0.00)` ⇒ 位移只在合成层，**没有触发任何布局重算** |

   再加上用户人工确认"全部完美"，本条不再是"未验证"，而是"**证据形式为日志而非截图**"。
2. **Reduced Motion（系统关动画）未在真实"关动画"机器状态下实测**（代码路径完备）；
3. Fluid Zoom 投影增强未实现（`CompositionShape`/`ShapeVisual` 无 `Shadow`）；面板互切为串行（约 0.66 s）；
4. 本轮 U64 **未建独立的"改动前快照"**（改动都是纯增量/可精确还原的小删除），回退步骤已在第五节写明。