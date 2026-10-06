# 工作交接 — 20260928 — U62 PCMig Fluid Zoom Transition（Matched-Geometry）

> 交接对象：接手 PCMig Utility Panel 动效 / Responsive Motion 工作的下一位（或下一个会话）
> 权威工作区：`<仓库根>`（唯一事实来源）
> 本轮起点：上一会话交接的「P0 Utility Panel Origin Reveal 待人工确认」
> 本轮终点状态：**已实现并量化验证 Matched-Geometry Fluid Zoom，构建通过；等人工目视验收**
> 前置交接：`docs\工作交接-20260928-Responsive与Motion全阶段.md`（U56–U61，仍然有效）

---

## 一、最重要的一句（用户原话口径）

用户否掉上一版的原因**不是参数不好**，而是**机制不对**：

> "它本质还是一个 Fade + 小幅 Scale 的弹窗动画。" —— 因此禁止再调 `0.945 → 0.92`、`260ms → 300ms`。
> 要的是：**被点击的那个入口本身，视觉上连续变形成面板**（Apple iOS 18 `navigationTransition(.zoom)` 的等价物）。

本轮做的**就是换机制**：主视觉从"淡入"改成**几何形变**。Fade 只留作内容交接的辅助层。

---

## 二、机制：三层结构（`Presentation\FluidZoomTransitionCoordinator.cs`，新增）

| 层 | 内容 | 承担什么 |
|---|---|---|
| **Layer A** Morph Shell | **纯 Composition** 圆角矩形（无任何文字/控件内容） | **100% 主视觉**：位置 + 非均匀尺寸 + 表面填充 + 边缘光连续变化 |
| **Layer B** Source | 真实触发元素（Developer 图标 / v0.5.0 Badge） | 打开时淡出（"被吞没"）→ 面板就位后恢复；**关闭时它从头到尾不动**（Shell 缩回它身上即可） |
| **Layer C** Destination | **真实 Panel**（不改尺寸、不缩放） | 从 55% 起淡入接管，位置尺寸已是终值 ⇒ 交接处看不出跳变 |

**为什么 Shell 是纯 Composition 而不是"把面板压成图标"**（用户第 17 节）：
把完整 Panel 缩到 28×28 会让内部 Slider / 文字压成噪点。Shell 无内容 ⇒ 非均匀形变不可能产生内容变形。

**为什么 Source 不做位移**：见第五节"我自己引入的 bug"—— 位移会污染关闭方向的几何测量。

### 2.1 动画通道：为什么是"逐帧插值"（重要，别改回去）

最初按 MS Learn 的常规做法写 Composition 关键帧动画，**构建直接失败**：

```
error CS1061: "Compositor" 未包含 "CreateMatrix4x4KeyFrameAnimation" 的定义
```

随后逐条核实官方文档（`CompositionObject.StartAnimation` 的 Remarks 给出**唯一权威的可动画属性表**）：

- ✅ 表内只有：`Visual` 的 `AnchorPoint/CenterPoint/Offset/Opacity/Orientation/RotationAngle/RotationAxis/Size/TransformMatrix`、`InsetClip` 四边、`CompositionColorBrush.Color`、`CompositionPropertySet`、effect 参数
- ❌ 表内**没有** `Visual.Scale`、**没有任何** CompositionShape / CompositionGeometry 属性
- ❌ `CompositionBrush.Opacity` **这个属性根本不存在**（写了会 CS1061）
- ❌ `CompositionSpriteShape.Shadow` / `ShapeVisual.Shadow` **都不存在**（只有 `SpriteVisual` 有 Shadow）

而矩阵**没有**对应的 KeyFrameAnimation 工厂 ⇒ `TransformMatrix` 无法用关键帧驱动。
**最终方案**：UI 线程按**真实时间**（`Stopwatch` + `CompositionTarget.Rendering`）逐帧直接给三类已确认**存在**的属性赋值：

| 通道 | 属性 | 用途 |
|---|---|---|
| 几何 | `ShapeVisual.TransformMatrix`（Matrix4x4：M11/M22 非均匀缩放 + M41/M42 平移） | 位置 + 尺寸 morph |
| 表面 | `CompositionColorBrush.Color`（只改 alpha） | 填充与边缘光"长出来" |
| 可见性 | `Visual.Opacity` | Shell 让位 / Panel 接管 / Source 淡出 |

逐帧赋值**不依赖任何"可动画"假设**、不触发布局；`CompositionTarget.Rendering` 与合成帧对齐，进度按时间算
⇒ 即使掉帧也匀速、准时收尾。圆角矩形几何固定为"面板尺寸 + 面板圆角（18）"，形变由矩阵承担
⇒ 起点尺寸极小时圆角与 1 DIP 描边被同比例压小，正好满足用户第 21 节"不要在 Source 极小时把 DAEL 挤成很粗的一圈"，且全程连续无跳变。

### 2.2 时序（`Themes\Motion.xaml`，Token 集中）

```
PCMigFluidZoomOpenDuration  = 0.36 s      PCMigFluidZoomCloseDuration = 0.30 s
```
打开：几何用 `EaseOutCubic`（**前段就走完近一半** ⇒ 不会出现用户第 37 节禁止的"0–60% 几乎不动、最后突然展开"）
表面填充 `0.22 → 1.0`（t 0→0.32）· 边缘光（DAEL 色）`0 → 1`（t 0.10→0.55）· Shell 整体让位（t 0.80→1.0）
真实 Panel 淡入（t 0.55→1.0）· Source 淡出（t 0→0.22）与恢复（t 0.70→0.88）
关闭：几何反向 · Panel 内容先退（t 0→0.40）· 填充/边缘光/Shell 依次淡出 · **Source 全程保持原位**

### 2.3 坐标系（用户第 10 节：成功关键）

Source / Destination / Overlay **全部在同一个 Canonical DesignSurface 坐标空间**内用 `TransformToVisual(ApplicationRoot)` 实测：

- `ApplicationRoot` = `UniformScaleHost.ApplicationRoot ?? Content`（Uniform 模式下是 DesignSurface 内的真实根，尺寸恒 1424×892）
- Overlay = 挂在应用根**最后一个子元素**的空 `Canvas`（`IsHitTestVisible=false`，z 序高于两个 Panel），
  内部用 `ElementCompositionPreview.SetElementChildVisual` 挂 Composition 容器 ⇒ **随 Viewbox 一起缩放**，绝不与物理窗口像素混用
- 全程不写死 X/Y

---

## 三、接入与回退链（`MainWindow.xaml.cs`）

`TogglePanel` 拆出 `OpenPanel` / `ClosePanel(panel, afterClosed)`，两条路径都保持 U60 的"**先定位、后动画**"顺序
（透明就位 → 定位 → `UpdateLayout()` → 再定位 → 才播动画）。

**三级回退，功能永不依赖动画**：
1. `FluidZoomTransitionCoordinator.TryOpen/TryClose`（首选）
2. 上一版 `MotionDirector.PlayUtilityPanelOpen/Close`（Origin Reveal，已人工验收过）
3. 系统关闭动画时，两条路径内部都会直接吸附到可见终态（Snap）

**状态机与接管**（用户第 28 节）：`Closed / Opening / Open / Closing`
- Shell 已在跑 ⇒ 新一代**从当前实际几何接续**（复用 Shell，按当前渲染矩阵反算起点，渲染逐像素不变）
- 收尾按**代次归属**：被接管的旧一代即使 `Completed`/定时兜底到达也不再收尾（同 `PageTransitionCoordinator` 的思路）
- `TogglePanel` 在 `Current == Closing` 时把点击理解为"重新打开"（从当前几何接管回面板）
- **Panel 互切**（用户第 27 节）：第一版为**串行** `Close A（完整 Fluid Zoom 回自己的入口）→ Open B`

**关闭方向实时重算 Source Rect**（用户第 24 节）✓ 每次 `TryClose` 都重新 `TryMeasure`。

**诊断开关**（默认完全关闭，不影响产品）：`PCMIG_FLUIDZOOM_DIAG=1` ⇒ 写 `%TEMP%\pcmig-fluidzoom-diag.log`
记录 `StartTransition / inherited / Started genN span / FinishNow(是否因代次被忽略)`。**本轮就是靠它定位 bug 与验证接管的。**

---

## 四、证据（全部在 `archive\screenshots\fluidzoom\`）

取证方式：`PrintWindow(hwnd,hdc,2)` 抓帧（D12 已证实能捕获 Composition 结果）+ `PCMIG_MOTION_SLOWMO=3` 慢放
+ 自研 GIF89a 编码器（GDI+ 回读验证通过）+ 像素差异包围盒序列（`diff-series.csv` / `analysis.json`）。

**判据**：与帧 0 的差异包围盒应当**从入口矩形连续长大到面板矩形**，且**前 20–25% 就已完成近一半**。

| 场景 | 入口（bitmap 坐标） | 差异包围盒终点 | 20% 时 | 结论 |
|---|---|---|---|---|
| developer-open | 图标 424,60 28×28 | **424,94 348×719** | 169×301（≈49%/42%） | ✓ 面板实际尺寸 348×719，逐位吻合 |
| developer-close | 同上 | 差异区从面板几何**收缩回**入口几何 | — | ✓ 反向 morph |
| changelog-open | Badge 360,62 54×24 | **360,63 440×748** | 226×345（≈51%/46%） | ✓ 面板实际 440×748 |
| changelog-close | 同上 | 同上反向 | — | ✓ |
| switch-dev-chg / chg-dev | — | 440×748 | — | ✓ 两段各自动画、无残留 |
| coord-switch（坐标连点互切） | — | 440×748 | — | ✓ Close A → Open B 串行 |
| scale-085（client 1210×758） | 图标 362,58 | **361,80 296×611** | 159×339 | ✓ = 348×719 × 0.8497，**缩放档不错位** |
| scale-075（client 1068×669） | 图标 320,67 | **320,87 261×538** | 148×299 | ✓ = × 0.7527 |
| prod-100（**不设** `PCMIG_UNIFORM_HOST`） | 图标 424,60 | **424,94 348×720** | 187×340 | ✓ 生产路径与 Uniform 1.00 一致 |

**接管日志证据**（`coord-switch`，慢放 3×）：

```
StartTransition opening=True  inherited=False  srcRect=416,60 28x28  panelRect=416,93 348x720   ← 打开
FinishNow RUN opening=True elapsed=1082ms                                                       ← 正常完成
StartTransition opening=False inherited=True   srcRect=416,60 28x28                             ← ★ 从当前几何接管
FinishNow IGNORED (stale gen1)                                                                  ← ★ 旧代收尾被正确作废
FinishNow RUN opening=False elapsed=903ms
StartTransition opening=True panel=ChangelogPanel srcRect=352,62 54x24 panelRect=352,91 440x720  ← ★ 串行开 B
```

**中间帧人眼证据**（单帧原图，勿用拼接图 —— 工具坑 #26）：
- `changelog-open/keyframes/t432-40pct-frame018.png`：Badge 已消失，一块半透明表面正覆盖表单文字（"正在展开"）✓
- `developer-close/keyframes/t360-40pct-frame015.png`：面板内容仍隐约可见且正在变淡、表面在缩回 ✓
- `developer-open/keyframes/t648-60pct-frame027.png`：表面已接近面板尺寸、下方文字被洗白 ✓

GIF（24 ms/帧 = 慢放 3× 原速回放，每个都通过 GDI+ 回读校验）：
`fluidzoom-developer-open.gif` / `-close` / `changelog-open` / `-close` / `switch-dev-chg` / `switch-chg-dev`
/ `coord-switch` / `scale-085` / `scale-075` / `prod-100` —— 均在各场景目录内。

---

## 五、我自己引入的 bug 与修法（诚实记录）

| # | 问题 | 怎么发现的 | 修法 |
|---|---|---|---|
| 1 | **Source 的位移漂移污染几何测量**：打开时给触发元素加了 18% 的漂移"吸入感"，但 `TransformToVisual` **会把它计入**，于是关闭时量到的入口是漂移后的位置（实测 `srcRect=445,128` 而不是图标真实 `416,60`，偏 29/68 px）⇒ Shell 会缩到图标**下方偏右** | 诊断日志里的 `srcRect` 与图标真实坐标不符 | 打开方向**取消 Source 位移**，"移动"完全交给 Layer A 的几何 morph（改完 `srcRect=416,60` ✓） |
| 2 | 表面填充时序太晚（0–45%），20% 帧几乎看不见表面 | 关键帧单帧原图 + 视觉模型未识别 | 起步量 0.22 且 `t 0→0.32` 长满；边缘光提前到 `0.10→0.55`；表面色朝白提亮一档（仍从真实材质资源派生） |
| 3 | 误判"close 动画在 500 ms 就被提前收尾" | 差异序列在 ~500 ms 后大量为 0 | **是误读**：最后非静止帧在第 34 帧（816 ms），与 900 ms 时长吻合；中途仍有多帧变化 |
| 4 | 三次"快速连点"实验全部无效（第二次点击没到应用） | 诊断日志：连 `StartTransition?` 都没有 | **不是产品 bug**：同一位置两次合成点击相隔 <500 ms 会被系统当作**双击**，WinUI Button 不触发第二次 `Click`。取证必须换位置或拉开 >500 ms（见第六节） |
| 5 | 投影增强尝试失败 | `CompositionSpriteShape.Shadow` / `ShapeVisual.Shadow` 均 CS1061 | **放弃**（只有 `SpriteVisual` 有 Shadow，需要额外 SpriteVisual + mask）；如实记录为可选后续增强 |

---

## 六、本轮新增工具坑（补进工具坑清单）

| # | 坑 | 规避 |
|---|---|---|
| 29 | **同一位置的两次合成点击 < 双击阈值（~500 ms）会被系统吞掉第二次**（Button 不触发第二次 Click）⇒ 看起来像"接管没生效" | 连点取证要换位置（不同入口）或拉开 >500 ms；**不要**据此判定产品 bug |
| 30 | **抓帧后立刻 `Bitmap.Save` 会吃掉时间**：10 帧 PNG 落盘 ≈ 1.7 s，"快速连点"实际变成 1.9 s 间隔的慢点击，静默改变了被测场景 | 设 `$env:U62_DEFER=1` 先把整段 burst 留在内存，全部点击完成后再统一落盘 |
| 31 | **`Ensure-Foreground` 最多 6×220 ms 重试**、UIA `FindAll` 遍历也慢 ⇒ 两次点击之间可能多出 1.3 s | 需要精确时序时改用**坐标直点**（跳过 UIA 与前台重试）；坐标由 `record.json` 的 `ClientOrigin` + Canonical 偏移换算 |
| 32 | 分析脚本里"帧号 × 间隔 = 时间"的假设**在多段点击场景下不成立**（段间有落盘/查找开销） | 多段场景只看 CSV 的**趋势与包围盒**，不要用 `T_ms` 判断进度百分比 |
| 33 | XAML 编译错误会级联出 `Unknown type 'Step1ConnectPage'` 等**假错误**（再次踩到，与坑 #19 同源） | 只看 `error CS` 行定位真因 |
| 34 | `CompositionSpriteShape` **没有** `Shadow`；`ShapeVisual` 也**没有**；`CompositionBrush` **没有** `Opacity` | 阴影只在 `SpriteVisual` 上（需额外 visual + mask）；淡出要么改 `Color` 的 alpha，要么改 `Visual.Opacity` |

---

## 七、未完成 / 局限（如实说明，不许当成已完成）

1. **等人工目视验收**（用户第 33–37 节的 PASS 判据是"人眼看到入口真的长大"）。
   请按顺序看：各场景 `keyframes\` 的单帧原图 → 各场景 GIF → 与 `diff-series.csv` 的包围盒序列对照。
2. **Reduced Motion（系统关闭动画）未在真实的"系统关动画"机器状态下实测**。
   代码路径是完备的（`TryOpen/TryClose` 直接拒绝 → Origin Reveal → 内部再判 → 吸附终态），但**没有实机证据**。
3. **投影增强未实现**（见第五节 #5）：浅色材质语言下"表面"与背景几乎同色（真实 Acrylic Panel 本身也如此），
   **20% 前后的早期帧在空白区域不容易看出边界**，可见性主要来自"遮挡下方文字/控件"（40–60% 帧非常清楚）。
   已做过的低成本改善：表面起步量 0.22 + 更早长满 + 表面色朝白提亮一档。
   若人眼仍觉得"早期看不出入口在长大"，可选增强是"额外 SpriteVisual + DropShadow（用填充刷作 mask）"，
   或把几何曲线从 `EaseOutCubic` 换成 `EaseOutQuart`（前段更快）。
   ⚠️ 单帧图对视觉模型不够醒目（它会去读文字），**请以 GIF 动态观感为准**。
4. **同一入口 <500 ms 连点的接管未取证**（工具限制）。但**对称路径已取证**：`coord-switch` 证明
   "打开中途点击另一入口 ⇒ 关闭从当前几何接管（`inherited=True`）"；"关闭中途重新打开"在 `rapid-click-fast` 中也是 `inherited=True`。
5. 互切是**串行**（Close A 全程 + Open B 全程）⇒ 产品速度下约 0.66 s。若嫌慢，需另做"受控快速 handoff"（本轮未做，用户第 27 节允许先串行）。

---

## 八、回退与备份

| 用途 | 位置 |
|---|---|
| **改动前快照（本轮）** | `archive\tmp\MainWindow.xaml.before-u62-fluidzoom.xaml`、`MainWindow.xaml.cs.before-u62-fluidzoom.cs`、`MotionDirector.cs.before-u62-fluidzoom.cs`、`Motion.xaml.before-u62-fluidzoom.xaml`、`DeveloperTuningPanel.xaml.before-u62-fluidzoom.xaml`、`ChangelogPanel.xaml.before-u62-fluidzoom.xaml` |
| **主文件级快照（勿删）** | `<镜像备份根>\PCMig-v0.5.0-pre-responsive-motion-20260928-131622` |
| 只回退本轮 | 删除 `Presentation\FluidZoomTransitionCoordinator.cs`，还原上面 4 个文件（`Views\*.xaml` 本轮**未改**，快照仅作对照） |
| 关闭新机制 | 无 env 开关：**删掉协调器文件 + 还原 MainWindow 的 `OpenPanel/ClosePanel`** 即回到 Origin Reveal。运行期没有开关（这是有意的：它是产品功能，不是 Spike） |
| 慢放取证 | `PCMIG_MOTION_SLOWMO=<倍数>`（默认 1，不影响产品） |
| 诊断日志 | `PCMIG_FLUIDZOOM_DIAG=1` ⇒ `%TEMP%\pcmig-fluidzoom-diag.log`（默认关闭） |
| 关闭新特性（Responsive） | 不设 `PCMIG_UNIFORM_HOST` 重启 |

**Git：HEAD 仍是 `c9aef30`（不含本会话成果）。不要 `reset --hard`、不要 `checkout .`、不要 `clean -fd`。**

---

## 九、本轮改动的文件

| 文件 | 改动 |
|---|---|
| `Presentation\FluidZoomTransitionCoordinator.cs` | **新增**（约 660 行）：三层 morph、Canonical 坐标系、逐帧插值、代次接管、三级回退、诊断 |
| `MainWindow.xaml.cs` | `TogglePanel` 拆为 `OpenPanel` / `ClosePanel(panel, afterClosed)`；接 `_fluidZoom`；互切串行；打开期间 `IsHitTestVisible=false`；关闭时 `Abort()` |
| `Themes\Motion.xaml` | 新增 `PCMigFluidZoomOpenDuration` / `PCMigFluidZoomCloseDuration` |
| `Presentation\MotionDirector.cs` | **未改**（Origin Reveal 原样保留为回退层） |
| `Views\DeveloperTuningPanel.xaml` / `Views\ChangelogPanel.xaml` | **未改** |
| `Themes\Materials.xaml` / `Controls.xaml` / `Presentation\UniformScaleHost.cs` / 页面 Push 链 | **未改**（冻结项一律未碰） |

复用的取证脚本（`archive\scripts\`）：
`u62-fluidzoom-spike.ps1`（10 个场景，env 参数 `U62_SCENE/U62_SCENE_DIR/U62_UNIFORM/U62_CLIENT_W/H/U62_FRAMES/U62_SLOWMO/U62_DEFER`）、
`u62-fluidzoom-analysis.ps1`（差异包围盒序列 + 关键帧抽取）、`u62-fluidzoom-gifs.ps1`（GIF + GDI+ 回读校验）。

---

## 十、下一步

1. **人工目视验收 Fluid Zoom**（第七节第 1 条）。若不通过，先判断是"机制"还是"可见度"：
   机制不动（几何 morph 已在量化上成立），可见度可按第五节 #5 做投影增强。
2. 通过后即可**冻结 Utility Panel Transition**，然后回到上一份交接文档的 **P1：Progress Sweep**
   （Token `PCMigProgressSweepDuration` 已预留；需先读 Step3 与底栏进度条的 XAML —— 那份文档说还没读过）。
3. 可选清理：`python? / ffmpeg?` 无 —— 本机没有，GIF 编码器继续用自研的。

---

## 十一、★ 人工验收结论（2026-09-28，用户当场确认）★

> **用户原话：「已经可以了，就这样，我全部确认验收」。**

| 项 | 结论 |
|---|---|
| U62 PCMig Fluid Zoom Transition（两个 Utility Panel 的开合动效） | ✅ **人工已验收，冻结，不要再改** |
| U63 左侧四步导航 Selected / Unselected 状态颜色 | ✅ **人工已验收，冻结，不要再改** |

**冻结的含义**（与上一份交接文档里"整页 Push 已验收、不要再改"同级）：

1. **不要再改** Fluid Zoom 的机制、时长、几何曲线、表面色、边缘光时序、三层结构；
2. **不要再改**导航的状态→颜色映射（强调色只由 `IsSelected` 决定这条规则已定案）；
3. 我此前在第七节列出的"可选增强"（额外 `SpriteVisual` + `DropShadow` 投影、`EaseOutQuart` 更快起手）
   —— **用户未要求实施，视为不做**。不要以后自行"顺手优化"；
4. 用户也**未要求**修改其余同型位置（`Step1ConnectPage` L23「可用共享」图标、`Step2SelectDataPage` L85/L136 区块图标、
   `DeveloperTuningPanel` L41 面板标题图标、`ChangelogPanel` L42 版本号文字）—— **保持现状，不要自行统一**；
5. 侧栏 Step Card 的 **Hover 零反馈**（历史根因未定位）用户未要求恢复 —— **保持现状**；
6. Reduced Motion / 系统关动画路径仍未实机实测（代码路径完备），保持如实记录，不要宣称已验证。

**验收时的证据位置**：`<用户目录D>\Desktop\<桌面交付根>\`（U62 十场景 GIF + 关键帧；U63 四页与快速切换截图 + 蓝色块聚类报告；文档 00–05）。