# UI-CLOSURE-ISSUES-20261005 — UI 收口问题表（本轮唯一问题清单）

> 来源：用户人工验收标注（桌面「新建文件夹 (5)」5 张 PNG）+ 用户本轮指令 §3–§16 + 代码实读定位。
> 分类口径：**A** = 一次性 UI Bug（进 Issue / Fix Report，**不进 PMML**）；**B** = 基础视觉/布局规范（进 PMML）；**C** = 基础 Motion 规范（进 PMML）。
> 证据前缀 `IMG1..IMG5` = 桌面「新建文件夹 (5)」下 5 张截图（见 §0 对照表）。

## 0. 标注图对照表（逐字转录结论）

| 编号 | 文件名 | 场景 | 用户标注（逐字） | 指向 |
|---|---|---|---|---|
| IMG1 | `62044b962aba90c8b0790c1c949b9fdc.png` | Step3 迁移中 15.9% | 「极为明显的显示内容不全，显示100%的时候也是这个样子。」+ 红框圈底栏百分比 | 底栏百分比被截断为 `15....` |
| IMG2 | `a6b9d551c503625b687a966327b5213e.png` | Step3 **已暂停** | 「虽然是暂停了，但是以传的进度还是在的，怎么会是显示为0.0%」/「现在是暂停状态而且这个暂停状态显示的是100%未传，但事实上我们已经传输了一部分数据。」 | 暂停后整体视觉归零 + 「剩余 100% 未传」 |
| IMG3 | `0b41cb807993c417499ac31042103127.png` | Step1 连接旧电脑 | 「这个也应该显示在左边提示栏里面。应用里面的一切提示用户的。信息显示都应该在左边的提示栏里面」 | 表单内「正在连接 Axuan 并发现共享...」应进左侧提示栏 |
| IMG4 | `1b756fbf3541c92c48e61bdb13df0bd4.png` | Step2 已有任务展开 | 「这个展开的框子的最右边和那个已有任务的那一栏框子不对齐。按照我箭头画的这样子，把它往左边平移一下。还有线程数和已有任务这两个框子，他们的圆角依旧跟整体的UI不统一。」 | Popup 右边界越出 Anchor；两个 ComboBox 圆角不一致 |
| IMG5 | `a2a807efac323cabe457003a71aac45a.png` | Step3 四张统计卡（放大） | 「这些传输速度下面的数字、预计剩余下面的数字和汉字以及对象进度下面的数字和已传计划下面的数字。显示的最顶部像是被一刀切了一样，被遮掩了一小部分不明显，但是一打眼就能看得出来…肯定是被什么东西遮住了，有个遮罩什么玩意儿。」 | Metric Card 数值/标签**顶部被遮挡**（用户批注）→ **像素测量部分证伪**：见下 |

**IMG5 结论（2026-10-05 程序化像素测量，`E:\PCMigLab\Evidence\UI-Closure-20261005\pixel-measure-report.md`）**：
放大图相对整窗对照图（IMG2）放大 ≈1.25×。整窗实测：**卡顶边框 y=296**、label 墨迹 y355..365、value 墨迹 y377..394（高 18）、卡底边框 y414..416 ⇒ 卡顶→label **59 px**；放大图可见带 y347..452 内 **y347..370 墨迹数 = 0**（min luminance 200..218），真实卡顶应在 y≈297 —— **恰好落在黑色标注块（y0..346）内部**。
⇒ **"最顶部像被一刀切"来自批注黑块遮挡，不是 UI 缺陷**；不得按"文字裁切"去修（不给四张卡加 Magic Margin）。
但本条仍产出两条**真实**依据：① 40 号总进度百分比 `LineHeight 52 < 自然行高 54` ⇒ **确有 2 px 裁切**；② 20 号 `LineHeight 28 = 自然行高 28` ⇒ **零余量**。二者均按 UI-04 的行高口径修正（见状态表）。

**IMG5 实测值（转录）**：传输速度 `2.07 GiB/s`｜预计剩余 `约1分15秒`｜对象进度 `0/30`｜已传/计划 `20.84 GiB / 176.9 GiB`。
**IMG1/IMG2 实测值**：迁移中 `15.9%`、`28.1 GiB / 176.9 GiB`、`147.65 MiB/s`、`约 17 分 11 秒`；暂停后 `0.0%`、`0 B / 176.9 GiB`、`0/30`。

---

## U1 — Progress Display Truth / Numeric Geometry

### UI-01 底栏百分比显示不完整（`15....` / `100...`）— A + B
- **证据**：IMG1（红框实拍 `15....`）；用户指令 §3。
- **实读根因候选**：`src\PCMig.WinUI\MainWindow.xaml:122` → `FooterPercentText` 带 `Width="48"` + `TextTrimming="CharacterEllipsis"`，样式 `PCMigTextFooterPercent`（FontSize 17 / Bold / LineHeight 24，`Themes\Typography.xaml:41`）。`100.0%` 在 17 Bold 下墨迹宽 > 48 DIP ⇒ 必然省略号。
- **要求**：Critical Percentage 禁止 TextTrimming / Ellipsis；按最大合法串（`100.0%`）预留宽度；`%` 永不消失；宽度稳定不推动 Footer 后续元素。
- **分类**：具体 Bug → A（Fix Report）；**几何规则 → B（PMML: Critical Numeric Label Geometry）**。

### UI-02 暂停 / 停止后进度视觉归零 — A
- **证据**：IMG2（`0.0%` / `0 B / 176.9 GiB` / `0/30` + 「剩余 100% 未传」，而真实已传 28.1 GiB）；用户指令 §4。
- **要求**：Paused / Stopped / Interrupted / Resumable 必须保留已传 Bytes、全局 Percent、完成对象数 / 总数；状态切换只能改 Status / Speed / ETA / Action Availability。Speed / ETA 显示 `—`。
- **分类**：A（业务显示真值修复）；PMML 只收一句基础原则「状态切换不得无理由清除已呈现的累计视觉进度」→ B。

### UI-03 用户面向单位改回 KB / MB / GB / TB — A + B
- **证据**：IMG1/IMG2/IMG5 全量出现 `MiB/s`、`GiB`、`KiB`；用户指令 §6。
- **要求**：UI 全量统一（主进度 / 底栏 / 统计卡 / 提示 / 结果页 / 校验页 / 报告文案）；内部真值仍为精确 Bytes，换算与校验逻辑零改动；**全工程只允许一个 User-Facing Formatter**。
- **分类**：A（本次替换）；格式规范 → B（PMML Data Display）。

---

## U2 — Typography / Layout

### UI-04 Metric Card 数值与标签**顶部被遮挡**（IMG5「像被一刀切」）— A + B
- **证据**：IMG5；用户指令 §5。四张卡：传输速度 / 预计剩余 / 对象进度 / 已传·计划。
- **实读**：卡片 = `Border Style="{StaticResource SecondarySurface}" Padding="14"` + `StackPanel Spacing="4"`，标签 `PCMigTextStepSubtitle`（11，**无 LineHeight**），值 `PCMigTextStatValue`（20 SemiBold / LineHeight 28）；结构见 `Views\Step3ProgressPage.xaml:93-116`。
- **待判**：是 `LineHeight` 不足导致 ascent 裁切，还是 `SecondarySurface` 的表面层（边缘高光 / 内阴影 / ESL 浮雕层）覆盖内容顶部（用户原话「有个遮罩什么玩意儿」）。**必须像素测量后定因，禁止四张卡各加一个 Magic Margin（PMML-R10）**。
- **要求**：共享 `MetricValueStyle` / Token；100% / 125% / 150% DPI 无顶裁切、无底裁切、视觉中心与基线一致。
- **分类**：A（修法）+ **B（PMML: Metric Card Typography / Optical Baseline / No Text Clipping）**。
- **★ Round-2 PHASE 5 / 5B 定因结论（2026-10-05，真机逐列首墨迹测量）★**
  - 候选根因**逐一排除**：`LineHeight` 手段无效（自然行高 25.4 DIP < 旧写死的 32，加行高只会让行盒更大）；父级布局/裁切不存在（6 变体 × 7 样本 × 5 档离屏 DPI 下 `anyClipped` 全 False，生产变体净空 9–10 物理像素）；字重回退曾被认为"不改变墨迹盒"（WPF `FormattedText.BuildGeometry()` 六字重度量完全相同），但**真机像素测量否定了该推断**。
  - **真正原因**：字体轮廓在**低字号下的栅格化**。`Microsoft YaHei UI` FontSize 20 的字符 `2`：`Normal(400)` 顶部墨迹跨 **13 行**、`SemiBold(600)` 只剩 **12 行且首行覆盖 60% 墨迹列**、`Bold(700)` 达 **70%**；同一字符在 FontSize 96 下跨 **64–68 行（ratio 0.24–0.27）** ⇒ 轮廓完好，是粗笔画在 20 px 下被 grid-fitting 把弧顶吸附到同一像素行。
  - 而生产样式请求的正是 `SemiBold(600)`（本机 `Microsoft YaHei UI` 只注册 290/400/700，无该字面）⇒ **改 `FontWeight="Normal"`**，改后真机轮廓与探针 `Normal(400)` **逐字段一致**（`2` 的 ratio 0.6000→0.5000、`S` 0.6667→0.5556）。`Medium(500)` 与 `Normal(400)` 真机完全等价，故取 `Normal`。
  - **诚实口径**：这是**渐进改善**，不是"从削平变回圆弧"的戏剧性变化；生产卡本体的截图仍缺（`RenderTargetBitmap` 与 `Translation` 不兼容）。详见 `UI-CLOSURE-ROUND2-REPORT-20261005.md` 的 E5 / E5B。

### UI-05 Footer 动态数值不得推动 Action Buttons — B（回归）
- **证据**：用户指令 §13、§15；`MainWindow.xaml:122` 底栏 9 列 Grid（Auto, Auto, *, Auto, Auto, 0, 0, Auto, Auto）。
- **要求**：Percent / Bytes / Speed / ETA 字符串变化时 Start/Pause/Stop/Resume 的 X 位置稳定；需要缩窄时优先压缩中间 elastic 列（`*` 列 + `MinWidth="160"`），绝不裁 Critical Number、绝不压 Action。
- **分类**：B（PMML: Footer Layout Isolation）。

### UI-06 「已有任务」Popup 右边界与 Anchor 不对齐 — A + B
- **证据**：IMG4（红箭头要求整体左移对齐）；用户指令 §11/§13。
- **要求**：Popup 内容 Border 的 Left/Right 与 Anchor ComboBox 几何边界一致（默认同宽）；阴影允许视觉外溢，但**不计入内容边界**。
- **分类**：A（具体修正）+ **B（PMML: Anchored Popup Alignment / Border vs Shadow Geometry）**。

### UI-07 「线程数」「已有任务」圆角与整体 UI 不统一 — A + B
- **证据**：IMG4（「他们的圆角依旧跟整体的UI不统一」）；用户指令 §12/§14。
- **要求**：Card / Popup / Dropdown / Hint Panel / Progress Track / Progress Fill / Metric Card 全部复用既有 PMML 圆角 Token（`Themes\Materials.xaml` 冻结值，见 PMML §3.2）；不得新增孤立圆角值；无语义层级的散落值（6/8/10/12/14/16）必须整理归位。
- **分类**：B（PMML: Corner Radius System）。

---

## U3 — Hint Panel / Routing

### UI-08 提示栏向上增长无上界 — A + B
- **证据**：用户指令 §7；`Views\ShellHintCard.xaml`（`Border` + `StackPanel Spacing="8"`，四段可变长文本，无 MaxHeight）。
- **要求**：Hint Panel 上边界不得超过 Step4 卡片区域；顶部与 Step4 保持**与其他 Step 卡同级的标准 Section Gap**（不得碰到 / 压住 / 穿过 Step4）；超出最大高度后转**内部轻量 Scroll**。
- **分类**：A + **B（PMML: Expandable Panel Bounds / Section Gap）**。

### UI-09 提示栏扩张 / 收缩必须动画（禁止一帧跳高）— C
- **证据**：用户指令 §8；关联 ShellHintCard 高度随内容变化。
- **要求**：高度变化 Smooth / Continuous / Natural；不得引发整个 Workspace 大范围抖动；Composition / Layout animation / Clip animation 路线优先。
- **分类**：**C（PMML: Expandable Panel Motion / Height Transition / Safe Layout Animation）**。

### UI-10 流程级提示必须路由到左侧提示栏 — A + B
- **证据**：IMG3（Step1 表单内「正在连接 Axuan 并发现共享...」）；用户指令 §10（原 §12）。
- **规则**：表单内只留字段校验错误 / 必须依附字段的即时提示 / 极短局部说明；**流程状态**（连接中 / 发现共享 / 生成计划 / 迁移中 / 暂停中 / 恢复中 / 验证中 / 完成提示）一律走 Hint / OperationalStatus 通道。
- **实读**：`ShellHintCard` 已经读 `OperationalStatus / UserHint / ErrorSummary / CurrentObjectStatus` 四个语义通道（FIX BATCH 6），因此本轮是**把 Step1/Step2 页内联的流程句子改投通道**，不是新建管道。
- **分类**：A（具体路由）+ B（PMML: 信息层级规则，小幅补充）。

---

## U4 — Motion

### UI-11 ProgressBar 像 PPT 一帧一帧跳 — C（本轮最高优先视觉项）
- **证据**：用户指令 §7（原 §7/§5）；用户人工观察「数据真实持续传输，但整条像 PPT」。
- **要求**：DATA TRUTH 与 VISUAL INTERPOLATION **解耦**；业务层只给真实 Progress Truth；UI 层用 Composition（`ScalarKeyFrameAnimation` / ImplicitAnimation / 同等级 GPU-friendly 技术）在新旧真实值之间补间；**动画目标值只能是真实最新值，不得预测、不得伪造**；新 snapshot 到达时从当前 presentation value 平滑转向新 target；避免 cancel 跳帧与过长 easing 落后真值。
- **分类**：**C（PMML: Progress Motion Token / Progress Interpolation Spec / Continuous Motion Requirement）**。

### UI-12 粒子 / 光波推进感（此前要求过，现已丢失）— C
- **证据**：用户指令 §8；`Themes\Motion.xaml:52` 仍存在 `PCMigMotionProgressSweepDuration = 0:0:1.60`（§31 曾定义 Sweep，实现已被移除或不可见）。
- **要求**：Fill 内部（**必须 Clip 在已完成 Fill 内，不得越入未完成 Track / 外部文字 / 百分比 / 邻近数据**）叠加：柔和高光 Sweep（Transparent → 淡白/淡蓝 → Transparent，Left→Right）+ 低密度 Particle-like 亮点向 Progress 前沿流动 + 略亮的 Leading Edge Glow；柔和不刺眼、非霓虹、非血条、非火花/星空/闪粉。
- **分类**：**C（PMML: Progress Visual Language / Progress Particle Motion / Progress Sweep·Shimmer / Leading Edge Glow）**。

### UI-13 状态动效开关 — C
- Running：平滑插值 + 粒子 + Sweep + Leading Edge Glow 全开；Paused：数值保真、主动效停止或极轻静止 glow、不向前流动；Stopped / Error：停止；Completed：100%、不循环强动画、允许一次极短收束后静止。
- Top 主进度条 = 完整版；Footer 小进度条 = 简化版（粒子更少、glow 更弱），**运动语言必须一致**。
- **分类**：**C（PMML: Progress State Motion）**。

### UI-14 状态文字 / 日志 / 当前文件入场动效 — C
- **证据**：用户指令 §9（原 §11）。覆盖：Hint 新消息、「正在连接…」、「正在生成计划…」、当前文件变化、日志新增、成功/警告状态、状态标题切换。
- **要求**：只对**状态切换 / 新消息进入 / 新内容新增**播放，不重复自播；Ophty 0→1 + 轻位移（X −8~12 或 Y 4~8）；轻、快、自然、不抢注意力；**禁止 bounce / overshoot / 大位移**。
- **分类**：**C（PMML: Text Entrance Motion Token / Status Update Transition / Log Item Entrance / Current File Update Motion）**。

---

## U5 — PMML Sync（只收基础视觉 / Motion）

应检查并在缺失时新增、重复时整合的 17 个章节（用户指令 §19）：
1 Corner Radius System｜2 Spacing / Safe Gap｜3 Critical Numeric Label Geometry｜4 Metric Card Typography·Optical Baseline｜5 No Text Clipping｜6 Footer Layout Isolation｜7 Anchored Popup Alignment｜8 Progress Visual Language｜9 Progress Motion·Smooth Interpolation｜10 Progress Particle Motion｜11 Progress Sweep·Shimmer｜12 Leading Edge Glow｜13 Progress State Motion｜14 Expandable Panel Motion｜15 Text Entrance Motion｜16 Log·Status Item Entrance｜17 Motion Performance Rules。

**明确不进 PMML**：Pause 归零这个具体 Bug、Case08、某个具体 JobId、某张截图的某个像素、某条具体文案。
**PMML 现存问题**：规范已 v1.0 **FROZEN**；本轮按用户显式授权同步（PMML-R15 授权偏离条款的显式授权路径）。写法必须是「以后所有同类 UI 都必须遵守」，不是「这次修了什么」。

---

## U6 — Manual Visual Verification（禁止只写静态 XAML Contract）

Progress：`0% / 9.9% / 15.9% / 99.9% / 100%`
States：Running / Pausing / Paused / Stopped / Resuming / Completed
Unit：MB / GB
Metric：短速度 / 长速度 / 短 ETA / 长 ETA / `0/30` / `30/30`
Hint：1 行 / 3 行 / 6 行 / 达到 max boundary / 超过 max boundary
Dropdown：closed / open / scroll
Motion：真实 running 进度 / pause / resume / complete —— **录制连续帧或短视频，证明非 PPT 跳帧**。

---

## 禁止清单（用户指令 §23，逐条自查）

不把 snapshot 更新频率硬改 60Hz；不 fake progress；不随机自增进度；不只调大 100% 宽度而两位数继续裁切；不给四张 Metric Card 加 Magic Margin 应付；不 Pause 后继续显示 0；不让粒子跑进未完成 Track；不 Pause 时仍向前流动；不 100% 后无限 shimmer；不每两秒重复播放文本动画；不 Hint 无 max；不 Popup「差不多」对齐；不把所有 Bug 写 PMML；不为动画引入巨大第三方 UI 框架；**不动 Business Core 解决纯视觉问题**。

---

## 状态表（随施工原地更新）

> 施工时间：2026-10-05 02:22 – 02:55（UI Closure 轮）。分类口径见文件头。
> 状态取值：**FIXED**（代码已改，Release 构建 0 error）｜**FIXED+验证**（另有程序化/真机证据）｜**OPEN**。
> 证据目录：`E:\PCMigLab\Evidence\Trust-Critical-Recovery\UI-CLOSURE-20261005\`

| ID | 标题 | 分类 | 状态与证据 |
|---|---|---|---|
| UI-01 | 底栏百分比截断 | A+B | **FIXED+验证**：`MainWindow.xaml` `FooterPercentText` `Width 48→68` + `TextTrimming None`；依据 Pillow 实测（`100.0%`@17 Bold = **61.0 px**，`0.0%`=41）⇒ 68 留 7 px 余量。**改前真机实测缺 4..6 px（`15.9%`）/14..16 px（`100.0%`）**（`pixel-measure-report.md`）。待 100% 真机截图复核 |
| UI-02 | 暂停/停止视觉归零 | A | **FIXED**：Core 三处（`MarkInterrupted` 补 `MeasureTarget`、字节累计对 Interrupted 取单调下限、保持实测口径）+ VM 结果页文案改字节口径（`Math.Max(0, end.TotalBytes - end.CompletedBytes)`）。Core 468/468 测试绿。真机暂停复验待做 |
| UI-03 | 单位 KB/MB/GB/TB | A+B | **FIXED+验证**：唯一 user-facing formatter `PCMig.Core\Util\Format.cs:10` `Units = ["B","KB","MB","GB","TB","PB"]`（除法仍 1024）；诊断证据域刻意不改。测试 PG-08 / PG-08b / Batch5 契约已随规格同步 |
| UI-04 | Metric Card 顶部遮挡 | A+B | **部分证伪 + FIXED（行高加固）**：像素测量判定"一刀切"是标注黑块遮挡（见 §0）；但热发现 **40 号 `LineHeight 52 < 自然 54`（真裁 2 px）**、**20 号 28 = 自然 28（零余量）** ⇒ `Typography.xaml` 改 40→**60**、20→**32**、17→**26**、13→**20**；`Step3ProgressPage.xaml:161` 16→**18**。不做 Magic Margin |
| UI-05 | Footer 布局隔离 | B | **FIXED+验证**：`FooterEtaText Width 100→112` + `TextTrimming None`（`约 23 小时 59 分`@13 = **100.0 px**）；百分比列固定 68 ⇒ 位数变化不推动 Action 区（四按钮各 `Width=124` 固定） |
| UI-06 | Popup 右边界对齐 | A+B | **FIXED**：`Step2SelectDataPage.xaml.cs` 新增 `AlignExistingJobsFlyoutWidth()`（`anchorWidth − 6`，chrome = Padding 2+2 + BorderThickness 1+1）；移除 `MinWidth/MaxWidth` 夹取；FlyoutPresenter `MaxWidth 380→1200`。**改前真机实测 Δ = 59 px**（红竖线口径）/ 51 px（底边口径） |
| UI-07 | 圆角不统一 | A+B | **FIXED+验证**：`Materials.xaml` 新增 `PCMigRadiusOverlay(16)` / `PCMigRadiusListItem(10)`；`PcmigComboBoxRoll` 收起态 `ControlCornerRadius(4)→PCMigRadiusInput(14)`、`PopupBorder OverlayCornerRadius(8)→PCMigRadiusOverlay(16)`；TaskPicker 三处同规则。**改前真机实测：下拉框 r≈2..3 px vs 路径框 r≈8..9 px** ⇒ 不统一成立 |
| UI-08 | Hint 无上界 | A+B | **FIXED**：`ShellHintCard.SetMaxSurfaceHeight()`（下界 160 DIP）+ `ShellHintCard.xaml` 内容包进 `ScrollViewer`；调用方 `MainWindow.UpdateHintCardBounds()`（上界 = 侧栏高 − StepNav 高 − 12 = `HintCard.Margin.Top` 同源） |
| UI-09 | Hint 高度动画 | C | **FIXED**：`ShellHintCard.AnimateSurfaceHeight()`（180 ms `CubicEase/EaseOut`，走 XAML `Height`；目标差 <0.5 不重播；不用 Composition `Size/Offset` —— 不参与布局） |
| UI-10 | 流程提示路由 | A+B | **FIXED+验证**：`ConnectionViewModel` 新增 `FlowStatus`（流程级 7 处）/ `InlineNote`（字段·错误级 8 处）两通道，`Status =` 直接赋值已归零；`MigrationSessionViewModel` 订阅 `FlowStatus` → `SetOperational`；`Step1ConnectPage.xaml:28` 改绑 `InlineNote`。真机 Steps1 复验进行中 |
| UI-11 | 进度平滑插值 | C | **FIXED**：新增 `Presentation\ProgressMotionDriver.cs`（满宽 Fill + `InsetClip.RightInset` 标量动画；追赶 55%/s、单段 60–400 ms、线性复现起点防回跳）；两个写入者接驱动（Step3 + 底栏） |
| UI-12 | 粒子/光波/前沿光 | C | **FIXED**：Sweep（44 DIP / alpha 0x1E / 1.60 s Token 驱动，挂 Fill 子树自动裁剪）+ Glow（26 DIP / 峰 9 DIP / `#5ABCA4FF`，挂轨道宿主）+ 粒子（8 颗 / 活动带 34 DIP / 0.90 s / 负 delay 错相）。时长读 `PCMigMotionProgressSweepDuration` Token |
| UI-13 | 状态动效开关 | C | **FIXED**：`SetActive(Phase == JobPhase.Running)` —— 只有 Running 开装饰；Pausing/Paused/Stopped/Failed/Interrupted/Resumable/Completed/CompletedWithErrors 一律 `SnapTo` + `IsVisible=false` + `StopAnimation`；Reduced Motion 走 `MotionDirector.SystemAnimationsEnabled` |
| UI-14 | 文本/日志入场动效 | C | **FIXED**：`ShellHintCard.SetLine()` + `PlayEntrance()`（Opacity 0→1 + `Offset (0,6,0)→0`，170 ms）；触发纪律 = 只在折叠↔显示或文本真变化时播一次，同一句话重复抵达不重播 |

**构建**：`dotnet build PCMig.sln -c Release` ⇒ 成功，**0 error / 3 warning**（= 基线 `PCMigSurface.xaml:53/54/56` WMC1506）。
**未结项**：全部 14 条均需按 U6 清单做**人工视觉复验**（用户主导，见 U6 节）。
---

## Round-2 追加（2026-10-05）— 用户视频证伪后的返修状态

> 来源：用户 Round-2 Fix Plan + 视频 `20261005-0330-07.1223683.mp4`（98.67 s / 1422x880 / 30 fps）+ 新标注截图。
> 完整报告：`docs\UI-CLOSURE-ROUND2-REPORT-20261005.md`。
> **视频证据凌驾于本表上一轮的任何 "FIXED" 标注**；本表内编号（UI-01…UI-14）按施工顺序 U1–U4 重排，与用户指令正文条目顺序不完全对应 ⇒ 引用时「编号 + 描述性标题」并列。

### 状态总览

| 问题（按描述性标题） | 上一轮标注 | Round-2 判定 | 依据 |
|---|---|---|---|
| 底栏百分比被截断为 `15....`（UI-01） | FIXED | **VERIFIED FIXED** | 契约测试 + 底栏呈现节拍 |
| 暂停后整体视觉归零、「剩余 100% 未传」（UI-02/UI-03 暂停归零） | FIXED | **VERIFIED FIXED** | 真机 30 样本恒 99.9%（`phStop\stop-samples.csv`） |
| 提示卡内容驱动变高 / 文字堆叠 / 出现滚动条（UI-07/08/09） | FIXED | **VERIFIED FIXED** | 三种内容状态 `HintScroll h=57` 完全一致；四通道满载溢出场景仍 NOT VISUALLY VERIFIED |
| 进度条「平滑补间」肉眼不可见、像变粗（UI-05） | FIXED（实为假） | **VERIFIED FIXED** | 601 帧：163 个前沿位置、单帧最大跳 24 px、无 >20 px 回退 |
| 粒子 / Sweep / Glow 肉眼无效果（UI-06） | FIXED（实为假） | **VERIFIED FIXED**（真根因：负 DelayTime 致装饰层静默降级为空） | Running 期探针 sweep/glow/particles/marker 全 True |
| 四张 Metric Card 大号数值顶部像被削（IMG5 / UI-03 数值） | FIXED（实为假，且旧结论"黑块遮挡"被撤销） | **NOT VISUALLY VERIFIED / OPEN** | 隔离探针 6 变体 × 7 样本 × 5 档 DPI 全部 `anyClipped=False`，生产变体净空最大（9–10 px）；用户真机所见尚未对齐 |
| 顶部摘要三组不共面（Round-2 新发现） | 未登记 | **VERIFIED FIXED** | 宽窗 maxΔ 0.5 DIP、窄窗 maxΔ 1.0 DIP（判据 ≤2） |
| Stop（可恢复中断）把主百分比重置为 0.0%（用户视频 43.7→44.0 s） | 未登记（上一轮 floor 只覆盖 Paused） | **VERIFIED FIXED** | Stop 后 30 样本恒 99.9%、Resume 后 16 样本恒 99.9%、`UnexpectedProgressRegression` 日志 0 条 |

### 代码级纠正（本轮新增的"上一轮假结论"更正）

1. **UI-06 假结论**：上一轮报告"粒子/Sweep/Glow 已实现"不成立 —— `ProgressMotionDriver.StartParticleLoops()` 使用**负 DelayTime** 被 Composition 拒绝，`CreateDecorations()` 抛异常后被构造函数的 `catch` **静默降级为空**，装饰层在屏幕上根本不存在。现改为惰性创建 + 正相位。
2. **UI-05 假结论**：上一轮"已实现平滑补间"不成立 —— 只是把条变粗。真因是"采样频率 ≠ 信息频率"（Core 真值约 2 s 变一次）且文本与条各读不同来源。
3. **IMG5 旧结论（黑块遮挡）撤销口径**：本轮不再以"用户看错"结案，改为隔离探针分类（§PHASE 5）；分类结果为**非字体层、非父级布局层**，故保持 OPEN 而不是宣称已修。
4. **文档缺陷更正**：`src\PCMig.WinUI\Themes\Typography.xaml` 中 `PCMigTextStatValue` 上方注释曾声称 `UseLayoutRounding=True`，实际该样式从未设置它；注释已按探针结论重写（**只改注释，未改属性值**）。

### 未关闭项（沿用报告 F 节口径）

- Metric 数值顶部被削 ⇒ **OPEN**（需用户提供其截图 DPI/缩放与具体数值以复现）
- 提示卡四通道满载内部滚动 ⇒ CODE FIXED / NOT VISUALLY VERIFIED
- 顶部摘要双行状态句真机截图 ⇒ CODE FIXED / NOT VISUALLY VERIFIED
- 视觉厚度 token 7 vs 8 DIP 的参考图像素 A/B 定档 ⇒ CODE FIXED（当前 8）