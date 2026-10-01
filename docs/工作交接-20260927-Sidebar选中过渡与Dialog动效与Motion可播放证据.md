# 工作交接 — 20260927 — Final Polish 第三轮：Sidebar 选中过渡 / Dialog 动效 / Motion 可播放证据

> 前序交接（全部保留，未覆盖）：
> - `工作交接-20260928-FinalPolish收尾-Motion修复与裁切证伪.md`
> - `工作交接-20260927-FinalPolish后半程实现与DPI验收卡点.md`
> - `工作交接-20260927-FinalPolish首批实现与验收阻塞.md`
> - `工作交接-20260927-四页Shell与DesktopAcrylic材质体系与开发者调节器.md`
>
> 本轮时间：2026-09-27 17:30 – 18:10（**以控制台 `Get-Date` 实测为准**；`docs/` 中部分既有文档用 20260928 标记，属前序会话的命名习惯，不代表真实日期）

---

## 一、本轮做了什么（四件事，全部有实测证据）

### 1. §27 Sidebar Selected 过渡 —— 从"完全没有"到"已验证无硬切"

**改动前事实**：导航选中态是**一帧灰→蓝硬切**，没有任何过渡。§27 明确禁止
（"导航 Selected 状态也不能 Hard Cut / 不要突然灰色→蓝色一帧硬切 / 但动画必须克制"）。

**改动**（`Views\StepNavigationControl.xaml`）：
- 卡片背景：`Border.BackgroundTransition` + `BrushTransition`，时长用新 Token
  `PCMigMotionDurationSelection` = `0:0:0.16`（`Themes\Motion.xaml`）。
- 选中竖条：`Border.OpacityTransition` + `ScalarTransition`，让竖条淡入淡出而非突然出现。

**⚠️ 平台限制（实测得，不是猜）**：WinUI 3 **只有** `Border.BackgroundTransition`
与 `UIElement.OpacityTransition`。以下三个属性**不存在**，用 XamlCompiler 报
`WMC0011: Unknown member`：
- `Border.BorderBrushTransition`
- `TextBlock.ForegroundTransition`
- `FontIcon.ForegroundTransition`

⇒ **描边、标题文字、副标题、图标颜色仍是瞬时切换**。这是平台能力边界，
已写在 XAML 注释里，**不用伪动画掩盖**。若要连颜色也补间，只能上 Composition API
（`Compositor.CreateColorKeyFrameAnimation`），属新方案、需另授权。

**验证（三条独立证据）**：
1. 逐帧抓帧（步骤2→3，侧栏裁剪 20,140 360×420）→ `B-sel` 序列 17 帧，
   前 3 帧 SHA256 完全相同（静止）→ 第 4 帧起**连续 15 帧各不相同** = 渐变而非硬切。
2. `vision_pixel_diff`：第 09 帧 vs 第 10 帧全图差异 **0.00%**；08 vs 09 = **0.01%**，
   且差异集中在卡片区（x 0–158 / y 70–140）—— 补间残尾、单调收敛。
3. 首轮对照实验：04→05→06→07→08→09→10 字节数 46737→46415→46529→46538→46417→46335→46274，
   连续变化后再收敛。

### 2. §30 Dialog 动效 —— 确认载体、显式声明、定义边界

**事实核查**：全项目搜索 `ContentDialog|Flyout|TeachingTip|Popup`，**只有一处命中**——
`Views\Step1ConnectPage.xaml` 的 `<Flyout Placement="BottomEdgeAlignedRight">`（"手动添加共享"）。
项目里**没有 ContentDialog**。

**改动**：显式加 `AreOpenCloseAnimationsEnabled="True"`，让"Dialog 必须有开关场动画"
成为**显式声明**而不是依赖默认值（§30 要求 Dialog 与 Panel/Page 用同一套 Pattern，不要各自设计）。

**🔴 重要工具事实（本轮踩坑换来，务必记住）**：
**Flyout 属 Popup 层，`PrintWindow` 完全抓不到它。**
- 现象：点击开启后抓 13 帧，Flyout 区域**逐像素哈希完全相同**（`bc73c5ff1c47` ×13），
  一度看起来像"没打开"。
- 判据：UIA 在主窗口内查到了 `name='手动共享名'` 的 Edit 元素 → Flyout **确实打开了**，
  只是渲染在 PrintWindow 取不到的合成层里，且**不产生新的顶层窗口**（顶层窗口数恒为 1）。
- 结论：**验证 Popup/Flyout 必须用屏幕拷贝（`-UseScreen`）或 UIA，不能用 PrintWindow。**

**已确认的 Dialog 事实**：浮层完整可见、未被窗口边缘裁切、内容齐全
（标题"手动添加共享"、输入框"手动共享名"及占位符、按钮"添加共享"）。

### 3. 清理死代码：两个 Panel 的声明式转场

`DeveloperTuningPanel.xaml` 与 `ChangelogPanel.xaml` 里各有一段
`<TransitionCollection><EntranceThemeTransition .../></TransitionCollection>`。
运行时 `MotionDirector.PreparePanelEntrance` 会先 `Transitions.Clear()` 再重新挂一次
（带 `MotionState` 防重入判定 + 尊重系统动画偏好），所以 XAML 里那段**永远被覆盖 = 死代码**。
已删除并加注释说明原因（这与前序在四个 Page 上做的清理是同一类问题）。

### 4. Motion 可播放证据 —— 本机无编码器，改用零依赖方案

**阻塞事实（复核确认）**：本机 **没有** `ffmpeg` / `magick` / `gifski` 三者中任何一个，
因此**无法产出 mp4/gif**。这是硬阻塞，不是没做。

**本轮产出的替代证据**（不是文字断言，是**可在浏览器里逐帧回放**的东西）：
- 新脚本 `archive\scripts\winui-motion-playbook.ps1`（UTF-8 **带 BOM**，语法已校验）。
  把一个目录里的 burst PNG 内联成 base64 自包含 HTML 播放器，附**逐帧 SHA256** 便于核验未篡改。
- 新脚本能力：`winui-motion-burst.ps1` 的触发器已泛化，新增 `-ClickAt 'x,y'`
  （合成点击），于是 **Dialg/Panel 动效也能用同一套仪器抓帧**（§30"一套 Pattern 一套工具"）。
  `-CtrlDigit` 改为可选。
- 产出（**注意路径在工作区根，不在 PCMig 下**）：
  | 文件 | 帧数 | 大小 | 内容 |
  |---|---|---|---|
  | `archive\evidence\A-page.html` | 17 | 2.6 MB | 主页面切换（方向性入场） |
  | `archive\evidence\B-sel.html` | 17 | 287 KB | Sidebar 选中过渡 |
  | `archive\evidence\C-dialog.html` | 17 | 2.7 MB | Dialog(Flyout) 开启 |
  - 全分辨率原帧保留在 `archive\screenshots\final-polish-20260928\playbook-frames\`（事实来源，未被改动）。
  - 页面内显示帧按 `-Scale 0.35` 缩小，仅为让 HTML 低于渲染器 4 MB 上限。
  - 三个 HTML 都**已用无头浏览器实际渲染并肉眼核对**：图片正常加载、控件齐全、计数 `1 / 17`。

**每条序列的帧差异（全部为实测）**：
- `A-page`：17/17 帧互不相同（第 03 帧内容切换，其后为入场补间）。
- `B-sel`：前 3 帧相同 + 后 14 帧互不相同（= 静止→渐变，选中过渡特征）。
- `C-dialog`：`00` 静止 → `05` 突增 8.8%（Flyout 出现）→ `13`/`14` **字节完全相同**（收敛）。

---

## 二、本轮修掉的一个自己的错误（教训）

**第一次拍 `C-dialog` 序列时证据是错的**：第 1 帧显示的是**步骤 3**页面。
原因：拍 C 之前先拍了 B（B 把页面切到了步骤 3），而 C 的合成点击打在"添加共享"按钮的
固定屏幕坐标上 —— 该按钮只存在于**步骤 1**，所以前几帧点击落空，Flyout 出现得很晚。
**修法**：重启应用（默认就在步骤 1）→ 显式 `Ctrl+1` 回步骤 1 → 再拍。
**教训**：抓动效前必须先确认目标控件**在当前页面/当前状态下真的存在**，
否则会得到"看起来有动画其实点空了"的伪证据。

---

## 三、坐标与工具坑（都很容易再踩）

1. **本项目有三套坐标系，混用必错**：
   | 坐标系 | 数值示例 | 说明 |
   |---|---|---|
   | 窗口像素（截图） | 1440×900 | 截图就是这个尺寸 |
   | 屏幕坐标（点击/抓图偏移） | 窗口原点 104,104 | `+104` 才是屏幕坐标 |
   | 客户区 DIP（UIA 报的） | 客户端原点 112,104，客户区 1424×892 | DPI=96 时 1:1 |
   - 实测：窗口 `104,104 → 1544,1004`（1440×900 含边框），客户区 **1424×892**，
     客户区在屏幕上的原点 **112,104**，`GetDpiForWindow` = **96**。
   - 换算：**屏幕坐标 = 截图坐标 + (104,104)**。
2. **不要相信视觉模型给的小按钮坐标**。本轮 `vision_ground` 把"添加共享"按钮定位到
   截图 x 985–1074 / y 589–636，**实际在 x≈1260–1372 / y≈475–518**，差了约 250px，
   导致第一次点击打在搜索框上、Flyout 当然没开。
   ⇒ **精确定位用像素扫描（找暗像素段）或 UIA，不要靠模型目测。**
3. **`AutomationElement.FromPoint` 在本应用上没用**：任何屏幕点都只返回
   `Microsoft.UI.Content.DesktopChildSiteBridge` 这个 Pane，拿不到真实控件。
4. **`edit` 工具会去掉 UTF-8 BOM**（死律 8 的隐形杀手）：本轮用 `edit` 改
   `winui-motion-burst.ps1` 后，前三字节从 `EF BB BF` 变回 `23 20 43`。
   ⇒ **每次用 `edit` 改过 `.ps1` 后必须复查前三字节并补 BOM。**
5. **HTML 渲染器上限 4 MB**（`vision_html_screenshot`）：三个全分辨率序列内联成一个页面
   达 17.6 MB，**超限导致连自查都做不到**。⇒ 按序列分页 + `-Scale` 缩小显示帧。
6. **运行中的应用会锁 exe**：改 WinUI 后必须先 `Stop-Process PCMig.WinUI` 再 `dotnet build`。
7. **`bin`/`obj` 里的脚本会让"缺 BOM"扫描出现假阳性**（本轮全量扫描命中 `lab\*.ps1`、
   `tools\uishot.ps1`）。`tools\uishot.ps1` 属发版/验证脚本，**按 AGENTS.md 三点五需当轮授权才能改，本轮只报告未改**。

---

## 四、回归结果（本轮结束时的实测值）

| 项目 | 结果 |
|---|---|
| WinUI Release build | **0 警告 / 0 错误** |
| 全解决方案 Release build | **0 警告 / 0 错误** |
| `dotnet test tests\PCMig.Core.Tests` | **130 / 130 通过**，0 失败 0 跳过，exit 0（基线未回退，未删未跳未弱化） |
| 残留进程 | 无 PCMig 进程残留 ✅ |
| 事件日志（近 1 小时） | 无 PCMig 相关错误/崩溃事件 ✅ |
| `vision_*` 自查 | 三页 playbook 均实际渲染并肉眼核对通过 ✅ |

---

## 五、本轮改动的文件

| 文件 | 改动 |
|---|---|
| `Themes\Motion.xaml` | 新增 Token `PCMigMotionDurationSelection` = 0.16s |
| `Views\StepNavigationControl.xaml` | +`BackgroundTransition`(BrushTransition) +`OpacityTransition`(ScalarTransition)；不支持的三类 transition 已移除并注明平台限制 |
| `Views\Step1ConnectPage.xaml` | `Flyout` 加 `AreOpenCloseAnimationsEnabled="True"` |
| `Views\DeveloperTuningPanel.xaml` | 删除死代码声明式 `EntranceThemeTransition` |
| `Views\ChangelogPanel.xaml` | 同上 |
| `archive\scripts\winui-motion-burst.ps1` | 触发器泛化：`-CtrlDigit` 改可选、新增 `-ClickAt` |
| `archive\scripts\winui-motion-playbook.ps1` | **新建**：burst 帧 → 自包含 HTML 播放器 |
| `archive\evidence\*.html` | **新建**：三页可播放证据 |

### 回退方法（逐条）
- Motion Token：删 `Motion.xaml` 里 `PCMigMotionDurationSelection` 一行（会连带编译失败，需同时删引用）。
- Sidebar 过渡：删 `StepNavigationControl.xaml` 里两个 `*Transition` 属性块（不影响功能，只回到硬切）。
- Flyout：删 `AreOpenCloseAnimationsEnabled="True"`。
- Panel 死代码：把两段 `UserControl.Transitions` 加回去即可（但**不推荐**，那是死代码）。
- 本轮的 HTML 证据与脚本**纯新增，删掉不影响程序**。

---

## 六、仍未完成 / 阻塞（不许当已完成）

1. **§47 Motion 视频验收 —— 硬阻塞**：本机无任何视频编码器（`ffmpeg`/`magick`/`gifski` 均不存在）。
   已用浏览器可回放的分页 HTML 作为替代证据，但**"视频"这一形式本身没做到**，不声称通过。
2. **DPI 125% / 150% 实测 —— 未做**：需要修改系统缩放，属系统级改动，**未获当轮授权**。
3. **侧边栏悬停/按下反馈 —— 未解决**：前序已穷尽 3 种方案并回退。
   已确认事实：指针移动事件**从未到达**卡片，但同坐标的合成点击能正常导航（两个事实并存）。
4. **§12 / §13 Badge 共享 Style 与材质精修 —— 未做**：`PCMigNumericBadgeBaseStyle` 等
   三个键全项目 0 命中，四页仍是内联 34×34、侧栏 32×32。视觉结果已达标，
   属纯架构整理、零视觉收益、高风险低收益，**等用户确认再动**。
5. **描边/文字/图标颜色仍瞬时切换**（WinUI 3 无对应 transition 属性，见 §一.1）。
6. **WinUI 未进发版链路**：`PCMig.sln` 未含 WinUI，`release.ps1` 仍只打包 WPF Gui/CLI，纳入需单独授权。
7. **`tools\uishot.ps1` 与 `lab\*.ps1` 缺 UTF-8 BOM**（死律 8）—— 只报告，未改。

---

## 七、`archive\` 目录不在 git 内（重要）

- `archive\` **完全在 git 管辖之外**，`Themes\`/`Views\`/`Presentation\*.cs` 多为**未跟踪文件**。
- 后果：`git clean -fd` 会瞬间销毁本轮全部成果（死律 9 已禁止该命令）。
- 备份参照点：标签 `v0.5.0-before-final-polish-20260927` → `c9aef304…`，
  镜像在 `E:\Project\镜像备份源码\PCMig-v0.5.0-before-final-polish-20260927`（+ `.bundle` + 清单，18 项 SHA256 MATCH）。
  **注意：该标签只覆盖"已跟踪"的改动，本轮及前序的 WinUI 成果未被它保护。**