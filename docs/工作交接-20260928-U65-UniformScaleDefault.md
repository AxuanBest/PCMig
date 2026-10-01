# 工作交接 — 20260928 — U65 P0：UniformScaleHost 改为生产默认（四页最小窗口裁切修复）

> 交接对象：接手 PCMig v0.5.0 收尾工作的下一位（或下一个会话）
> 权威工作区：`E:\Project\deepseek work\PCMig`
> 触发：用户人工实测报 P0 —— 最小窗口下 Step1 / Step3 / Step4 下半部分被裁切（Step2 正常）
> 结果：**已修复 · 构建 0 错误 · 单元测试 130 通过 / 0 失败 · 用户要求的全部验收项通过**
> 前置交接：`docs\工作交接-20260928-U64-HoverLiftPressedSink.md`（U62/U63/U64 三批，已冻结）

---

## 一、真实根因（★ 与用户初始判断不同，请务必按这个理解）

用户的观察**完全正确**（Step1/3/4 在最小窗口确实被裁），但根因**不是**"UniformScaleHost 内部还有第二层 Scale/Reflow"：

| 环境 | `uniActive` | LayoutMode | 页面根 vs 页面视口 | Step3 统计卡 | 「对象明细」 | 结果 |
|---|---|---|---|---|---|---|
| **旧生产路径**（`PCMIG_UNIFORM_HOST` 未设）960×670 | **False** | **Compact** | p3=**816×436** vs viewport **676×407**（溢出） | **两行两列**（重排） | **整卡不可见** | ❌ 裁切 |
| Uniform 启用 0.75 档 | True | **Wide** | 等比缩放，零溢出 | **一行四列** | 完整并排 | ✅ 完整 |

**真相**：`UniformScaleHost` 当时是**默认关闭**的 Spike 开关（需要 `PCMIG_UNIFORM_HOST=1`）。
用户看到的是**未启用**时的旧响应式路径：窗口缩到最小 ⇒ 落进 `LayoutMode.Compact` ⇒
**结构性重排**（Step3 统计卡一行四列→两行两列、下方双栏塌陷、Step4 工具栏折两行、侧栏副标题截断）
⇒ 重排后内容变高、页面根**溢出页面视口** ⇒ 下方被裁。

用户第 12 节描述的那段逻辑（`if (UniformScaleHost.IsActive) { 用 Canonical 1424×892，不跑 Compact }`）
**当时就已经存在且正确**（实测 `uniActive=True` 时 `mode=Wide`、`design=1424×892`）——
问题仅仅是**它没有被默认启用**。

---

## 二、修法（一行语义 + 注释，未碰任何布局代码）

`Presentation\UniformScaleHost.cs`：

```csharp
// 修复前
public static bool IsRequested =>
    string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal);

// 修复后：默认启用；显式 PCMIG_UNIFORM_HOST=0 才回退旧响应式路径
public static bool IsRequested =>
    !string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "0", StringComparison.Ordinal);
```

连带更新了三处注释（避免后人误读成"要设 env 才启用"）：类摘要、`EnvironmentVariable` 说明、
`Install` 内的早退注释、`MainWindow` 构造函数的接线注释。**布局代码一行未改。**

只改了 2 个文件：`Presentation\UniformScaleHost.cs`（语义 + 注释）、`MainWindow.xaml.cs`（注释 + 一个默认关闭的诊断方法）。

---

## 三、验证（用户第十六～二十一节清单，逐项）

| 验收项 | 结果 | 证据 |
|---|---|---|
| Build | ✅ 0 错误 | `dotnet build ... -c Release -m:1` |
| Tests | ✅ **130 通过 / 0 失败** | `dotnet test tests\PCMig.Core.Tests` |
| **默认路径即启用** | ✅ `uniActive=True`、`mode=Wide`、`design=1424×892`（不设任何 env） | `%TEMP%\pcmig-responsive-diag.log` |
| 0.75 四页截图 | ✅ 三档 × 四页 = 12 张 | `archive\screenshots\u65-uniform-clip\scale{100,085,075}-step{1..4}.png` |
| Step1 完整 | ✅ 「可用共享」卡（含插画与三行提示）+「下一步」按钮 + 底栏全在 | 视觉对比 1.00 vs 0.75 |
| Step2 保持完整（Golden Reference） | ✅ 未改动；对拍 meanAbs 5.93 | 反向放大报告 |
| Step3 完整 | ✅ **统计卡仍一行四列**、双栏并排完整（对象明细 + 正在复制的文件） | 视觉对比 |
| Step4 完整 | ✅ 工具栏**仍一行**、报告清单三条记录、**实时日志深色卡含四行内容** | 视觉对比 |
| RootScrollRequired = false | ✅ 四页页根都是 `Grid`；Step1/2/3 无任何 ScrollViewer，Step4 的两处在报告/日志卡**内部**（第 17 节允许）；运行期页面根 == 视口（`1048x582` == `1048x582`，零溢出） | XAML 静态 + 运行期日志 |
| 1.00 vs 0.75 几何对拍 | ✅ UIA 逐点 = ×0.75（侧栏卡 275×65 → 207×49 = ×0.7527；扣掉窗口偏移与 15.5 px 垂直居中后每个元素吻合） | `u65-uniform-clip\uia-geometry.json` |
| **反向放大重合** | ✅ Step1 5.83 / Step2 **5.93** / Step3 5.62 / Step4 6.47（pct>32 2.98–4.13%），与历史 Uniform 验收 5.55–6.48 / 2.10–3.18% **同级别**；`bestTopOffset=16` 与理论 15.5 吻合 | `u65-uniform-clip\reverse-upscale-report.json` |
| 快速导航回归 | ✅ 1→4→1→2→3→4 后**蓝色导航项 = 1**（落在最终页 Step4） | `u65-smoke\rapid-nav-final.png` |
| Fluid Zoom 冒烟 | ✅ 打开变化 213292 px；关闭后与打开前差异 **0** | `u65-smoke\panel-open/closed.png` |
| Hover 冒烟 | ✅ Pressed 位移 2074 px（Hover 本身需真实鼠标，见下"局限"） | `u65-smoke\pressed-down.png` |
| 冻结项未碰 | ✅ `PageTransitionCoordinator` / `FluidZoomTransitionCoordinator` / `InteractionFeedback` / `Motion.xaml` / `Themes\Controls.xaml` / 四页 XAML **一字未改** | 本轮只改 2 个文件 |
| U63 导航颜色未回归 | ✅ 蓝色项恒为 1（同上） | 同上 |

---

## 四、诊断方法（后人可复用）

`MainWindow.DiagResponsive(...)`（env `PCMIG_RESPONSIVE_DIAG=1`，**默认关闭、零行为影响**）在每次布局决策时记录：
`in=WxH mode=LayoutMode client=... uniActive=... uniScale=... design=... appRoot=... shell=... workspace=... viewport=... p1..p4=...`

**关键判读法**：把**页面根元素尺寸**与**页面视口尺寸**对比 —— 页面根 > 视口就是"内容溢出被裁"的直接证据，
不需要靠截图猜。修复前 960 档是 `viewport=676x407 / p1=816x430 / p3=816x436`，修复后是 `viewport=1048x582 == p1=1048x582`。

配套脚本（`archive\scripts\`）：
`u65-uniform-clip-diag2.ps1`（三档 × 四页 UIA 几何 + 尺寸日志；`U65_UNIFORM=0` 可跑旧路径对照）、
`u65-reverse-upscale.ps1`（反向放大对拍，含最优垂直对齐搜索）、
`u65-smoke.ps1`（Root Scroll + 快速导航蓝色块聚类 + Fluid Zoom + Pressed 冒烟）。

**取证坑（本轮新增）**：
- **页面 `x:Name` 不是 UIA AutomationId**（除非显式设 `AutomationProperties.AutomationId`）⇒ 用 `x:Name` 找页面元素会全部找不到；
- 用**固定像素坐标点击侧栏卡**在不同缩放档会错位（曾把 Step4 误拍成"Step1"）⇒ 一律用 **UIA 按 `AutomationProperties.Name`** 定位后点击；
- UIA 的 `IsOffscreen` **判不了被 Clip 裁掉的内容**（坑 #17 的同一件事）⇒ 判断"是否被裁"要靠**页面根 vs 视口尺寸**或视觉对比。

---

## 五、冻结与边界

- **本轮只修 Uniform Scaling 的四页一致性**，没有做任何新的 UI 优化（符合用户第 21 节）；
- 未提高 `MinimumUIScale`（仍 0.75）、未改最小窗口尺寸（仍 Canonical × 0.75 = 1068×669）、
  未给任何页单独减字体/Padding、未加 Root ScrollViewer、未针对小窗口做 Compact 特调布局（用户第 11 节禁止项，一条未犯）；
- **非 Uniform 路径完整保留**（`PCMIG_UNIFORM_HOST=0` 即回到旧响应式），符合用户第 8 节"不是删除 ShellResponsiveLayout"；
- U62 / U63 / U64 的冻结内容**一字未改**。

## 六、回退

| 用途 | 位置 |
|---|---|
| 只回退本轮 | `PCMig\src\PCMig.WinUI\Presentation\UniformScaleHost.cs` 把 `IsRequested` 改回 `== "1"`；`MainWindow.xaml.cs` 删掉 `DiagResponsive` 方法与其调用（可选） |
| 运行期回退（无需改代码） | 设 `PCMIG_UNIFORM_HOST=0` 重启 ⇒ 回到旧响应式路径（实测会重新出现 Compact 重排与裁切，**仅作对照**） |
| 改动前其他快照 | `archive\tmp\*.before-u62-fluidzoom.*`、`*.before-u63-selectionstate.*` |
| 主文件级快照（勿删） | `E:\Project\镜像备份源码\PCMig-v0.5.0-pre-responsive-motion-20260928-131622` |

**Git：HEAD 仍 `c9aef30`（不含本会话成果）。不要 `reset --hard` / `checkout .` / `clean -fd`。**

## 七、局限（如实记录）

1. **Hover Lift 仍无自动化证据**（合成输入驱动不了指针移动），需真实鼠标；本轮只验证了 Pressed 路径（2074 px）；
2. **Reduced Motion（系统关动画）仍未在真实"关动画"机器状态下实测**；
3. UIA 探针里 `实时日志` 一项在所有档都报 Missing —— 那是**探针名不匹配**（视觉已确认该卡完整存在，含四行日志与「打开日志目录」按钮），不是缺陷；
4. 本轮**未**重测"极端 Aspect Ratio 留白"（窗口比例与 1424:892 不一致时的 Backdrop 露出），那不属本次 P0 范围。