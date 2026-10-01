# 工作交接 — 20260928 — Final Polish 收尾（Motion 致命回归修复 / Responsive 接线 / 裁切类问题证伪）

> 本文件为**新增**交接文档（死律 10：只增不覆）。历史交接文档全部保留：
> - `工作交接-20260927-FinalPolish首批实现与验收阻塞.md`
> - `工作交接-20260927-FinalPolish后半程实现与DPI验收卡点.md`
>
> 本会话继续依据 `FinalPolish-用户指令原文-20260927.md`（67 节原文归档，SHA256 已核对）
> 与 `FinalPolish-完成度审计-20260928.md`（67 节完成度矩阵）推进。

---

## 一、本会话最重要的一件事：我自己引入并修掉了一个「四页永久隐形」的致命回归

### 现象
四个步骤页面**全部不可见** —— 主内容区只剩背景渐变与右下角两个导航按钮。
视觉证据：`archive\screenshots\final-polish-20260928\diag-now.png`（目视确认"主内容区为空白异常态"）。

### 排查过程（都是真实运行证据，不是推理）
1. 自建运行期探针（`CompositionTarget.Rendering` 逐帧采样，`PCMIG_MOTION_TRACE=1` 才启用）测得
   **182 帧全部恒定**：`translateX=-10.00 opacity=0.000`（t=1ms 到 t=904ms 一点没动）。
   → 说明动画完全没跑，且页面被留在 `Opacity = 0` 的起始态。
2. 探针初版把异常**吞掉**了（只有 `Debug.WriteLine`）。补上诊断落盘后拿到真因：
   ```
   storyboard.Begin THREW COMException: 没有检测到已安装的组件。
   Cannot resolve TargetProperty (UIElement.Translation.X) on specified object.
   ```
3. 单独做对照实验，分别只动画一个属性：
   - Storyboard 目标 `Opacity` → **Begin 成功**
   - Storyboard 目标 `TranslateX` → **Begin 抛 COMException**
   - Storyboard 目标 `(UIElement.Translation.X)` → **Begin 抛 COMException**

### 根因（两条，都是 WinUI 3 的硬限制）
- `UIElement.Translation` 是 **Vector3**，不是可动画的 DIP，**Storyboard 解析不了这个属性路径**。
- 裸路径 `TranslateX` 也解析不了 —— 它属于 `RenderTransform`，不是元素自身的属性。
- 而 `catch` 把 `Begin()` 的异常吞掉后，只剩一个停在 0 的 `Opacity` —— 于是"动画失败"被伪装成"页面本身就是空的"。

### 修法（关键决策：不再和 Storyboard 较劲）
**位移交给声明式 ThemeTransition，只有透明度用 Storyboard** —— 各走自己能解析的那条路径：
- `PlayPageEntrance` / `PreparePageEntrance` 改用
  `EntranceThemeTransition { FromHorizontalOffset = ±10 }`（自带淡入、跟随系统动画偏好、渲染提交前生效不闪帧）。
- `PlayPanelExit` 只用 Storyboard 的 `Opacity` 通道（**已实测可解析**）做淡出，位移交由转场承担。
- 全部 `catch` 分支都改为"吸附终态 + 立即收尾"，绝不再让动效失败把界面留在半成品状态。

### 结果（已实测）
- 四页恢复正常渲染：`archive\screenshots\final-polish-20260928\verify\v2-step{1..4}.png`
- 目视确认：四页内容完整、标题徽章数字 1/2/3/4 **完整居中**。

### 顺带推翻的一个历史结论
`工作交接-20260927-*` 里写「PrintWindow 无法捕获本窗口动画，19 帧只有 1 个 SHA256」。
本会话实测：动画期间抓帧的**逐帧差异并非 0**（如 `hover\i1-idle.png` 与 `i2-hover-card2.png` 主内容区
存在 0.12% 差异）。**该结论不可靠，不应作为"动画没跑"的依据**。动画是否跑过，只能用运行期数值证明。

---

## 二、Responsive：接线 + 修好一个真实硬裁切

### 2.1 接线（子代理成果真正生效）
子代理把 `ResponsiveLayout` 从 4 值扩到 **31 位置 Token + 38 派生 Token**，并新增
`ShellResponsiveLayout.Apply(...)`，但它**没有调用点**（惰性代码）。
本会话接上，并顺带修掉它留下的编译错误：
- `ResponsiveLayoutController.cs` 的 `FooterSpacerWidth` **限定名引用**断点常量
  （断点定义在另一个类 `ResponsiveLayoutController` 上，record 内必须写全名，否则 CS0103）。
- `MainWindow.xaml.cs::ApplyResponsiveLayout` 改为 `Calculate(width, height, CurrentScale())`
  并追加一行 `ShellResponsiveLayout.Apply(this, layout);`（**回退 = 删这一行**）。

### 2.2 修好：Compact 档侧栏副标题「硬裁到半个汉字」
- **缺陷**（实测）：侧栏宽 220 DIP（Compact）时，步骤卡副标题被卡片边缘**硬裁**，
  且无省略号 —— 步骤 3 缺「错」、步骤 4 缺「常清单」，字被切成半截。
- **根因**：副标题 `TextBlock` 在 Grid 的 Auto 列里，**既无 `TextTrimming` 也无 `TextWrapping`**。
- **修法**：
  1. `Views\StepNavigationControl.xaml` 副标题加 `TextTrimming="CharacterEllipsis"`；
  2. `Presentation\ResponsiveLayoutController.cs` 新增 Token
     `StepSubtitleMaxWidth => Half(Math.Max(96, SidebarWidth - 76))`（扣卡片内边距 26 + 徽章列 32 + 列间距 14 + 余量 4）；
  3. `Presentation\ShellResponsiveLayout.cs` 里把该上限赋给 `StepNav.MaxWidth`。
- **结果**（实测）：`responsive\f-compact-1000-after.png` 目视确认
  `实时进度、文件流与...` / `完整性校验、报告、...` / `填 IP 与账号，列出...` —— 退化为**省略号**，
  不再出现半个汉字；**Wide 档不受影响**（未新增裁切）。完整文本仍由既有 `ToolTip` 提供。

### 2.3 窗口尺寸矩阵（本会话新补）
`archive\screenshots\final-polish-20260928\responsive\`：

| 标签 | 窗口 | 比例 | 结果 |
|---|---|---|---|
| a-16x9-1440-step2 | 1440×810 | 1.778 | 无硬裁切、无重叠、底栏四按钮完整 |
| b-3x2-1350-step2 | 1350×900 | 1.500 | 同上 |
| c-compact-1000-step1 | 1000×700 | 1.429 | 侧栏副标题已修（省略号）；其余完整 |
| d-compact-980-step2 | 980×700 | 1.400 | 同上 |
| e-canonical-1440-step1 | 1440×900 | 1.600 | Canonical 基线正常 |

---

## 三、两条"待修裁切"被证伪（重要：不要再去改它们）

审计与上一轮交接把 Step2「连接与安全提示」与 Step4「实时日志」的末行判为**文字硬裁切**。
本会话用**改变窗口高度**做判别实验（若内容随高度补全 ⇒ 是滚动视口底边裁剪，不是布局缺陷）：

### Step2（证伪）
- 1440×900：正文第二行「经过中转服务器。」只露上半截。
- 1440×1100：**同一张卡片正文完整显示**（两行齐全、句号完整），且卡片下边缘下方**仍有余量**。
- 结论：**滚动视口底边裁剪**，不是文字缺陷。证据 `verify\v2-step2-tall2.png`。

### Step4（证伪）
- 1440×900：第 3 行 `[WARN]` 只露上半截。
- 1440×1100：日志区**多显示出一行半**（第 3 行完整 + 多出第 4 行 `[ERROR] 无可用任务：…`）。
- 并查明该日志面板是**4 行静态布局行（非数据绑定列表，无滚动条）**，内容超出时由页面滚动承接。
- 结论：同属视口裁剪；**按"看不见的东西不改"未做任何修改**（也不冒险加嵌套滚动）。

### 另两条经核查为**非缺陷**
- ~~Dev Panel 底部「导出 JSON」按钮：视觉模型读成「导出 JSC」并判为被截断；
  以**源码为准**核对 `Views\DeveloperTuningPanel.xaml` 为 `Text="导出 JSON"` —— 是低分辨率字形误读，**无截断**。~~
  **⚠️ 本条已于 20260927 复核推翻，结论作废。** 文案确实是 `导出 JSON`，**但按钮真的被裁**：
  `Width="348"` + `Padding="16"` ⇒ 内容宽 316 DIP；不换行的横向 `StackPanel` 里四个按钮
  需约 374 DIP，溢出 58 DIP；像素实测面板内缘右缘 x=1390 处按钮蓝底**竖直硬切**
  （x=1389 `RGB(19,106,234)` → x=1390 `RGB(251,248,249)`），无圆角、无右边框。
  ⇒ **标注②"显示不全"是真实缺陷，未修复。** 详见
  `FinalPolish-本轮交付报告-20260928.md` 的「3.1 更正说明」。
  教训：**"源码里的字符串是对的"不能推出"屏幕上渲染完整"** —— 必须量渲染结果。
- Update Log 面板「49 个版本只列出 13 个」：该面板 `MaxHeight` 受 `PanelMaxHeight` 约束，
  列表与详情本就设计为可滚动，属预期表现。（这条仍然成立）

---

## 四、Sidebar 悬停零反馈：已穷尽排查，**未解决，已按纪律回退**

### 已确认的事实
- `Views\StepNavigationControl.xaml` 里把 `ButtonBackgroundPointerOver` / `ButtonBackgroundPressed` /
  `ButtonBorderBrushPointerOver` / `ButtonBorderBrushPressed` 全部设为 `#00FFFFFF`（全透明）。
- `CardBackground` 只区分**选中/未选中**（`Presentation\StepNavigation.cs`），与指针状态无关。
- 实测：光标悬停到卡片上，侧栏裁剪区域（20,180,420,560）**逐像素完全相同**（两次均 166207 字节）。
- **同一屏幕坐标的合成点击却能正常触发 `Click` 并完成导航**（点击 270,435 成功切到「迁移进度」）。

### 尝试过并**失败**的方案（都已回退）
- (a) 在 Button 内容的 Border 上挂 `VisualStateManager` → 无效
  （DataTemplate 中内容 VSM 的 Setter `this` 为 null，Setter 被静默忽略）。
- (b) 在 `Button.Template` 内的 `ContentPresenter` 上挂 `VisualStateManager`（照抄
  `PCMigSecondaryButton` 的写法），并用 `AccentGradientBrush`（醒目蓝）作对照实验 → **仍然无效**。
- (c) 加 `PointerEntered` / `PointerExited` 代码后置事件做判别 → **处理器一次都没被调用**。

### 结论与待办
- 即「**指针移动事件没有到达这些卡片**」与「点击能到达」并存。**根因未定位**。
- 未经证实的猜测一律不写入本文件（死律：不许装懂）。
- 已把 (a)(b)(c) 全部回退，XAML 里留下调查记录注释。
- **待办**：若要修，下一步建议从
  `ItemsControl` 的 `ItemContainerStyle` / `ContentPresenter` 容器命中测试入手，
  或先做一个最小可复现工程隔离"Button 在 DataTemplate 内 PointerEntered 不触发"。
- 另注：`StepNavigationControl.xaml` 第 17 行注释声称"悬停/按下只做极轻的位移反馈"，
  但实现里既无位移也无材质反馈 —— 属**注释声称但未实现**，一并在待办里。

---

## 五、验证结果（全绿，可复现）

| 项目 | 命令 | 结果 |
|---|---|---|
| WinUI Release build | `dotnet build src\PCMig.WinUI\PCMig.WinUI.csproj -c Release` | **0 警告 0 错误** |
| 全解决方案 Release build | `dotnet build PCMig.sln -c Release` | **0 警告 0 错误** |
| 测试套件 | `dotnet test tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj -c Release` | **130/130 通过，0 失败 0 跳过**（与基线一致，未回退；未删未跳未弱化） |
| 四页渲染 | `verify\v2-step{1..4}.png` | 全部完整渲染，徽章 1/2/3/4 居中 |
| 窗口矩阵 | `responsive\*.png` | 16:9 / 3:2 / Compact / Canonical 全测 |
| Dev Panel / Update Log | `p-devpanel.png` / `p-updatelog.png` | 两面板均可打开并正常渲染 |

---

## 六、明确未完成 / 受限（不许当完成）

1. **§12 Badge 共享 Style 未做**：`PCMigNumericBadgeBaseStyle` / `PageTitleNumericBadgeStyle` /
   `StepNavigationNumericBadgeStyle` 三个键全项目 **0 命中**；四页仍是内联 34×34、侧栏 32×32。
   即「架构上仍有重复逻辑」。
   **但视觉结果已正确**（四页徽章数字完整居中，本会话截图确认）。
   未做的理由：纯架构整理、零视觉收益、要改 4 个页面的 XAML，在已完成大量改动后属高风险低收益；
   §13 的材质质感（高光/阴影/细描边）同样未做。**留给下一轮并需先与用户确认。**
2. **§47 Motion 真实视频验收 —— 环境阻塞**：本机无任何视频编码器
   （`ffmpeg` / `magick` / `gifski` 均不存在），无法产出 mp4/gif。
   替代证据：运行期逐帧数值 + 静置帧截图。**不能声称 Motion 已通过视频验收。**
3. **DPI 125% / 150% 实测 —— 阻塞**：需修改系统缩放，属系统级改动，**未获用户当轮授权**，未做。
4. **Sidebar 悬停反馈**（见第四节）：未解决，已回退。
5. **子代理自认未做**（`docs\FinalPolish-Responsive扩展说明.md` 有全文）：
   Step2/3/4 未接 Token；3 处未接线（共享搜索框 `SharesSearchWidth`、空状态插画 116×88、共享卡内容容器）；
   `MinimumDensityScale = 0.86` 是**占位值、非实测结论**；零截图零真机算术估算。
6. **WinUI 仍未进 `PCMig.sln` 的发版链路**，`release.ps1` 仍只打包 WPF Gui/CLI —— 纳入发版需单独授权。

---

## 七、备份与回退

| 项 | 位置 |
|---|---|
| 精修前标签 | `v0.5.0-before-final-polish-20260927` → `c9aef304…` |
| 源码镜像 | `E:\Project\镜像备份源码\PCMig-v0.5.0-before-final-polish-20260927` + `.bundle` + `-清单.md`（18 项 SHA256 MATCH） |
| 用户标注图只读副本 | `archive\annotations\new-folder-5\`（与 `D:\Users\User\Desktop\新建文件夹 (5)\` 原图 SHA256 一致，**原图未被改动**） |

⚠ **重要提醒**：`archive\` 目录**不在 git 内**，且 `Themes\` / `Views\` / `Presentation\*.cs` 多为**未跟踪文件**。
`git clean -fd` 或 `git checkout .` 会瞬间销毁本会话全部成果（死律 9 已禁止）。

**逐项回退方法**
- Responsive Token 接线 → 删 `ApplyResponsiveLayout` 里的 `ShellResponsiveLayout.Apply(this, layout);` 一行。
- 侧栏副标题省略号 → 删 `StepNavigationControl.xaml` 的 `TextTrimming` 与 applier 里的 `StepNav.MaxWidth` 赋值。
- Motion → 见 `Presentation\MotionDirector.cs` 顶部注释（位移走 `EntranceThemeTransition`，透明度走 Storyboard）。

---

## 八、下一步建议顺序

1. 用户确认 §12/§13 是否要做（纯架构整理 + 材质精修，需改 4 页 XAML）。
2. DPI 125% / 150% 实测 —— 需用户授权改系统缩放，或用户自己改好后由本会话截图。
3. Motion 视频验收 —— **需先装编码器**（或用户用系统录屏自行验收）。
4. Sidebar 悬停反馈 —— 从 `ItemsControl.ItemContainerStyle` 命中测试入手（见第四节）。
5. 把 WinUI 纳入发版链路（需单独授权）。