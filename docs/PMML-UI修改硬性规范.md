# PMML UI 修改硬性规范（短版执行规则）

> 适用：**PCMig** 全部 UI 改动 ｜ 完整规范：`docs/PCMig-Visual-Motion-Language.md` ｜ 实现审计：`docs/PMML-Implementation-Audit.md`
> 本文件是**改 UI 前必读、改完必过**的执行规则短版。与完整规范冲突时以完整规范为准；与 `docs/RELEASE.md`（硬性规则）冲突时以硬性规则为准。

---

## 一、什么时候必须走本规范

| 改动内容 | 要求 |
|---|---|
| 尺寸 / Margin / Padding / 颜色 / Shadow / Material / ControlTemplate / Motion / 布局 | **必须**先读完整规范 + 本文件，完成后执行 §四 的 PMML Compliance Gate |
| 文案 / 数字 / 状态文字 / 日志文本 / 数据绑定内容 / 纯业务逻辑（**视觉零影响**） | 只需在说明里写一行 `PMML Visual Impact: None`，**不走**完整 Gate |

判定口径：**只要它可能改变任何一个像素，就走 Gate。**

---

## 二、术语速查（PMML v1.0）

| 缩写 | 全称 | 一句话职责 |
|---|---|---|
| **PMML** | PCMig Material Motion Language | 整套内部设计语言（本项目自有，非 Apple/Microsoft/WinUI 官方规范） |
| **DAM** | Desktop Acrylic Material System | 背景材质：Tint / Transparency / Luminosity / Edge Highlight / Light-Dark 适配 |
| **LMDS** | Layered Material Depth System | L0 Backdrop / L1 Base / L2 Elevated / L3 Interactive / L4 Overlay·Tool —— 新 Surface 必须归层 |
| **DSL-45** | 45° Directional Surface Lighting | 全局唯一虚拟光源：左上 → 右下约 45° |
| **ESR** | Subtle Embossed Material Relief | 轻浮雕：亮边 + 细边框 + 柔阴影（不是夸张 Neumorphism） |
| **DCST** | Directional Cross-Slide Transition | 主页面 Step1↔2↔3↔4 的方向性交叉滑动 |
| **OACT** | Origin-Anchored Container Transform | Tool Surface 如何**动**（Translation + Scale + Opacity，源点锚定） |
| **TAOP** | Trigger-Anchored Overlay Placement | 浮层**在哪里**（触发器锚定；与 OACT 严格区分） |
| **MHE** | Material Hover Elevation | Hover **不等于换色**：按真实实现包含高光/阴影/抬升 |

> 这些是 **PCMig 项目内部术语**。可以在文档里注明其对应的行业概念（如 Acrylic、Composition animation、Flyout placement），**不得**把它们描述成 Apple / Microsoft / WinUI 的官方规范名称。

---

## 三、硬性规则（PMML-R）

| 规则 | 内容 |
|---|---|
| **PMML-R1** | 单一光源：**DSL-45**（左上→右下）。新组件不得自行改变光源方向。 |
| **PMML-R2** | 单一材质族：新 Surface 必须从 **DAM** 派生（不得手写新的半透明白框）。 |
| **PMML-R3** | 所有 Surface 必须明确属于 **LMDS** L0–L4 之一；禁止自创 L2.5 / L3.7。 |
| **PMML-R4** | **Blur 只能属于 Surface Background Material；Foreground 必须锐利**：Text / Icon / CheckBox / ComboBox Item / List Item 不得进入 Blur Visual。L0/L4 是主要 blur-bearing layers，**获准的 DAM Acrylic Variant 可存在于其它语义层**。 |
| **PMML-R5** | 页面导航 = **DCST**（禁止某页自己 Fade / 自己 Scale / 从右边飞入）。 |
| **PMML-R6** | Tool Overlay = **OACT + TAOP**（怎么动 + 在哪里，两者都要交代）。 |
| **PMML-R7** | Hover = **MHE** 家族（不得退化成"只是换个底色"）。 |
| **PMML-R8** | **动画不得成为业务逻辑依赖**：动画失败/被关闭不得改变任何业务状态。 |
| **PMML-R9** | Light / Dark 属于**同一套设计系统**：同一 Surface Family + 不同 Theme 参数。 |
| **PMML-R10** | 禁止用**负 Margin / 随意 Translate / 魔法 Scale** 掩盖错误布局。 |
| **PMML-R11** | **同一状态不允许有多个 UI 写入者**（TreeView Row Push 的教训正式写入）。 |
| **PMML-R12** | Popup / Flyout 必须**明确 Placement ownership**，不得靠系统默认行为碰运气。 |
| **PMML-R13** | 任何 Material Blur 不得导致文字/图标模糊。 |
| **PMML-R14** | 新 UI **优先复用**现有 Resource / Token / Style；确需新增时说明理由并登记。 |
| **PMML-R15** | **偏离 PMML 必须显式授权**；未授权偏离不得合入。 |
| **PMML-R16** | **显示真值连续性**：Pause / Stop / Interrupted / Resumable 等状态切换**不得无理由清除已呈现的累计进度**（Bytes / Percent / 已完成对象数）。UI 另有"显示高水位"（presentation high-water），它**只影响显示**，永不写回受回执约束的业务真值。允许回退的唯一情形是**有解释的**回退（新 JobId、显式重新规划、`RetryState.RollingBack` 带原因、源标识/计划指纹变化、用户新建任务）。 |
| **PMML-R17** | **关键数值禁止截断**：主百分比、已传字节、速度、ETA、对象进度等 Critical Numeric 一律**禁止** `TextTrimming` / 省略号；按最大合法串预留宽度，宽度稳定且不得推动相邻 Action 按钮。 |
| **PMML-R18** | **固定几何优先**：容器的外层尺寸**不得由内容驱动**。需要伸缩的内容放进**唯一一个**可变视口行（`Height="*"`），其余行 `Auto`；外层高度取自 token，内容溢出只在视口内滚动，且**不得出现随内容弹出/消失的可见滚动条**。 |
| **PMML-R19** | **字体字面必须真实存在**：`FontWeight` / `FontFamily` 必须落在本机实际注册的字面上（本机 `Microsoft YaHei UI` 只有 290/400/700，**无 600**）；**禁止**用 Magic `LineHeight` 掩盖字体度量问题。任何字体相关的"看起来被削/被裁"必须先用**逐列首墨迹的真机像素测量**分类为「父级裁切 / 行盒不足 / 低字号栅格化」三者之一，再决定改什么。 |
| **PMML-R20** | **呈现层单一时间线**：同一进度语义（大号百分比、字节文本、进度条填充）必须消费**同一个** VisualPercent 源，禁止文本读瞬跳真值而条走补间。视觉值只允许**落后**于已确认显示真值，**永不超出**；不得预测、不得外推、不得自爬。 |
| **PMML-R21** | **进度头（Progress Head）只代表"已经确认的迁移事实"**：Push Band / Particle / Ripple / Glow / Halo 一律**不得**修改或推进 `Value` / `Percent` / `ConfirmedBytes` / `Receipt` / `Verifier` / `JobState`。**事实由 Head 表示，活跃度由装饰表示**：Head 停住而 Push Band 继续跑是**正确**行为（后台任务仍在工作），Head 自己往前爬是**缺陷**。 |
| **PMML-R22** | **几何稳定 / 光照动态**：Head 必须是**稳定的圆弧胶囊几何**（右端永远是 `)`，不是 `\|`）；禁止用正弦 / 噪声扰动路径去伪造"液面前沿"，禁止廉价液体头。允许变化的是**光照**（Halo / Rim / Band 亮度），不是几何。 |
| **PMML-R23** | **同源**：进度头、大号百分比、已传字节必须消费**同一个** `ProgressPresentationCoordinator.VisualProgress`（同一时刻三者数值必须互相自洽；"数字 62.3% 而条还在 47%" 与"条先走、数字后追"都不允许）。禁止多个独立动画源驱动同一条进度几何。 |
| **PMML-R24** | **VisualProgress 只许落后，不许领先**：永不预测、永不外推、永不自我爬升；正常运行期不得无理由倒退；Running 期**目标变化只更新目标**，不得重启一段离散动画或清空速度状态（那会造成速度断点）。必须用 `dt` 驱动并以高精度时间戳计算，`dt` 上限 1/30 s（防止窗口还原后大跳）。 |
| **PMML-R25** | **同时最多一条 Push Band**：它是**宽而柔的两层光压**（外场 + 亮核），不是硬白条；一个周期内沿方向朝 Head 连续减速（`sin(t·π/2)` 型），禁止线性、禁止分段变速、禁止突然减速、禁止跑马灯 / 并行扫描 / 多条同跑。 |
| **PMML-R26** | **粒子属于 Progress Space，不属于屏幕空间**：生产默认活跃 `≤14`（High）/ `9`（Balanced）/ `4`（Reduced），**固定池**、禁止每帧创建对象、禁止越出填充胶囊；粒子只表达**数据物质的活跃**，永不表示额外进度；Head 前进时粒子被轻微拖带。 |
| **PMML-R27** | **局域光学耦合**：Push Band 经过粒子时，粒子亮度 / 半径 / 光晕必须按距离衰减增强（`influence = exp(-d²/2σ²)`）；Ripple 只是**极稀疏**的局部反馈（同时 ≤4、峰值不透明度 ≤0.20、带冷却），不得成为主视觉、不得变成游戏技能特效。 |
| **PMML-R28** | **Renderer 与业务层严格隔离**：进度视觉控件**禁止**访问 Robocopy / SMB / Receipt / Verifier / JobState / 日志 / 源路径 / 目标路径，只接受 `Value` / `Maximum` / `VisualState` / `EffectsQuality` 四个输入。该边界必须有测试。 |
| **PMML-R29** | **必须支持 Reduced Effects / Reduced Motion**：`High / Balanced / Reduced` 三档只影响装饰强度；关闭特效**不得**改变业务状态、`Value` 与 Automation `Value`。远桌面（RDP / Horizon）与低性能模式默认 Balanced 或 Reduced，但**未经用户授权不得改变业务 UI 逻辑**。 |
| **PMML-R30** | **真机验收不得只证明"像素变了"**：必须同时证明 ① Head 与 VisualProgress 一致；② 正常运行期无无理由倒退；③ 无无理由前跳；④ Push Band 连续且同时只有一条；⑤ 粒子在填充胶囊内；⑥ **真正终止态**（`Failed` / `Completed`）后装饰停止，而 `Paused` / `Interrupted` 按 R35 保留低强度活性（见 R35，v0.5.0 口径）；⑦ `Completed` 只在 Core 真完成时才到 100%。缺任一条即 `NOT VISUALLY VERIFIED`。 |
| **PMML-R31** | **Capsule Integrity（胶囊完整性）**：Immersive Progress 的 Host **永远透明**；Track、Fill 与**所有动态层**（Push Band / Particle / Ripple / Fill overlay）必须服从**同一个 Capsule 几何**。禁止在胶囊外再绘制可见矩形背景 / 壳体；**禁止用矩形裁剪**让动态层把圆角端部重新填成平切或方切（旧写法 `CreateLayer(1f, Rect(0, Top, ProgressWidth, Thickness))` 即违规）。生产路径**不得**再画全轨道 1 DIP 外描边（"外面套壳"），也不得有全宽硬 Top Edge 条。Host 四角像素必须等于父背景 —— 这是强制验收项，不是建议。 |
| **PMML-R32** | **Progress-Space Chroma Field（进度空间色场）**：Fill **不得**是固定纯蓝色。基础颜色必须由**锚定整个 Track 空间**的 Aqua/Cyan → 青蓝 → 电蓝色场构成（`EndPoint = TrackWidth`，**不是** `ProgressWidth`），进度的增长只负责"**逐渐揭示**"这个色场，因此颜色随进度自然变化。Head 使用**独立移动**的 Cyan/White 光场。禁止 Hue 随时间自由循环、禁止彩虹化、禁止用 Success Green 冒充 Aqua/Cyan。纵向只允许极轻的材质修饰，色彩主导必须来自横向色场。Completed 收尾后色场必须**继续保留**（不得退化成"一根纯蓝矩形"）。 |
| **PMML-R33** | **Hero / Compact Material Family（材质同族）**：Step3 Hero 与 Footer Compact 必须使用**同一个** Immersive Progress 控件族、**同一个** VisualProgress、**同一套** Capsule / Chroma / Head 语义。Compact 只允许**减少装饰层**（关闭粒子与 Ripple、Push Band 强度降到 Hero 的约 50~65%）与使用更小的几何 token（`CompactHostHeight/Thickness/Radius`，仍满足 `Radius = Thickness/2`）。**不得**退回旧 `Rectangle` / 固定蓝 Fill / 独立的像素补间路线。两条进度条在同一时刻的 Value 差必须 ≤0.1%。 |
| **PMML-R34** | **Volumetric Head Halo（体积式 Head 光晕）**：Immersive Progress Head 的活动高光**必须**由连续衰减的局部体积光场表达（至少外层 + 内层两层无硬边界的径向/高斯衰减，中心位于 Head **后方** 3~6 DIP，最亮处在 Head 内部/后方）。**禁止**以可辨识的 Stroke / Rim / Outline / 锐光弧作为主要 Head Glow —— 正常比例下能沿着圆弧画出一条连续白线即**不合格**。若确需极弱 Rim，只能作为几乎不可察觉的光学细节（`thickness ≤ 0.5 DIP`、`opacity ≤ 0.10~0.15`）。Push Band 抵达 Head 时只允许让光场强度略升，**禁止**把能量集中成一根沿轮廓的白线。验收方法：Head 区域放大 6~8 倍后仍必须像"一团光"，而不是"一根线"。 |
| **PMML-R35** | **Pause Keeps Material Alive（暂停不熄灭材质）**：`Paused` / `Interrupted`（可续传）状态**冻结 Progress 事实**（Head / Percent / ConfirmedBytes 一律不动），但**必须保留材质活性** —— Push Band 继续周期运行（速度降到 Running 的 40~60%、周期拉长、亮度降低）、Particle 保留（数量与流速降到 40~60%）、Halo 保留（强度略降，不得降到"熄灭"档）。**禁止**为了表达"暂停"把整个材质直接熄成一张死图。只有 `Failed` / `Completed` 才是真正终止态（Band 停、粒子淡出、Ripple 归零）。判据：`Pause ≠ Failed ≠ Dead`。 |
| **PMML-R36** | **Embedded Material Context（嵌入材质上下文）**：嵌入 Content Card / Footer Surface 或其他内容容器内部的视觉控件，**不得**自行使用 Window / Root / Ambient 层级 Material 覆盖自身的矩形 Bounds —— 包括"取祖先链上第一个不透明色当自己的清屏/底色"这类等价做法（内容卡片通常是半透明 Acrylic/ShellMaterial，会被这类逻辑跳过，从而一路取到**窗口级**底色，效果等同于在控件矩形上铺了一层窗口 Material）。控件 Host 必须**透明**，并且**必须在实际宿主合成路径上真正透出最近的父 Surface**；`ClearColor = Transparent` **不是充分条件**——宿主本身若走交换链（`CanvasAnimatedControl` 底层 `CanvasSwapChainPanel`/`CanvasSwapChain`），WinUI 3 会把它当 external content 开孔，alpha=0 处透出的是**窗口背景**而不是父卡片。⇒ 生产控件必须使用能参与 XAML Alpha 合成的宿主（**`CanvasControl`**，经 `CanvasImageSource` 合成，v0.5.0 起为唯一生产宿主）。**只有控件自身有语义的几何区域**（如 Capsule Track）才允许绘制独立材质；胶囊外的四个角必须直接透出父 Surface。 |

---

## 三之二、真机取证纪律（Round-2 新增，2026-10-05）

任何"视觉已修"的结论必须来自**可复现的真机像素证据**，且证据本身必须先自检：

| 纪律 | 说明 |
|---|---|
| **截图必自检** | 屏幕捕获前必须验证抓到的确实是目标窗口（本机曾出现 `GetWindowRect` / `IsIconic` / `IsWindowVisible` / `DWMWA_CLOAKED` / `GetForegroundWindow` 全部正常、但屏幕像素却是浏览器的情况）。脚本必须内置 `SELFCHECK maxLuma=… verdict=OK|WARN` 之类的守卫，**未通过自检的截图不得作为证据**。 |
| **坐标永不硬编码** | 窗口原点与控件矩形每次启动都变（实测见过 `26,26` / `104,104` / `208,208` / `234,234`），一律从当次 UIA 读取或从 `origin-<Tag>.txt` 读取。 |
| **不依赖 `Translation` 的导出** | `RenderTargetBitmap.RenderAsync` 与 `Translation`（含 MotionDirector 入场动画）**不兼容**，会抛 `ArgumentException: The specified property was not found or cannot be animated. Context: Translation` ⇒ 应用内位图导出必须选**无 Translation** 的视觉树，或先落位再导出。 |
| **诚实标注证据强度** | 每个结论必须标 `VERIFIED FIXED（真机证据）/ CODE FIXED / NOT VISUALLY VERIFIED / OPEN`，**不得**用"探针已证明"替代"用户真机所见"。 |
| **单显示器 DPI 限制** | 本机为单显示器 1920x1080 @ 96 DPI，五档 DPI 真机截图**不可行**；此时以**离屏 DPI 矩阵**作为等价证据，并如实标注 `NOT VISUALLY VERIFIED`。 |

---

## 四、PMML Compliance Gate（改完必须逐项声明）

```
PMML Compliance
---------------
DAM:                        PASS / N/A
LMDS:                       PASS / N/A
DSL-45:                     PASS / N/A
ESR:                        PASS / N/A
Motion Family:              DCST / OACT / MHE / State / N/A
TAOP:                       PASS / N/A
ProgressHead(Fact/Activity):PASS / N/A（R21 / R24：Head 只走事实，装饰不得推进 Value）
Lighting(Geometry Stable):  PASS / N/A（R22：几何稳定、光照动态）
SameSource(VisualProgress): PASS / N/A（R23：头 / 百分比 / 字节同源）
Typography:                 PASS
Foreground Sharpness:       PASS
Light/Dark:                 PASS / Known Gap
Accessibility:              PASS / Known Gap
Legacy Deviation Introduced: NO / YES（必须列出并登记到 PMML-Legacy-Deviations.md）
```

- 任一项 **FAIL ⇒ 不允许直接合入**。
- `Known Gap` 必须在 `docs/PMML-Legacy-Deviations.md` 里有对应条目（含文件:行号与原因）。
- 新增 Surface / Token / 控件族，必须同时更新完整规范与审计文档。

---

## 五、Accessibility / Reduced Motion

- 若当前实现**没有** Reduced Motion 检测：按完整规范登记的 `PMML v1.0 Known Gap` 处理，**本轮不重构**；
- 任何 Reduced Motion 行为都**不得改变业务状态**（只降低 Translation / Scale，保留 Opacity，功能态不变）。

---

## 六、与死律的关系

本规范只覆盖**设计语言一致性**，不豁免 `docs/RELEASE.md` 的任何一条：功能冻结、先写日志再打包、五道闸门、SHA256、发版后三验、两类独立证据等照旧执行。