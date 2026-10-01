# 工作交接 — 20260928 — Responsive Uniform Scaling 与 Motion 全阶段（U56–U61）

> 交接对象：接手 PCMig v0.5.0 UI Motion / Responsive 工作的下一位（或下一个会话）
> 权威工作区：`E:\Project\deepseek work\PCMig`（唯一事实来源）
> 本轮起点：上一会话交接的「UniformScaleHost Spike」施工卡（D1–D6）
> 本轮终点状态：**应用可编译、可运行、Uniform Scaling 与整页 Push 已通过人工验收；Utility Panel Motion 已实现待人工确认**

---

## 一、本轮最终结论（先看这段）

| 项 | 状态 | 证据 |
|---|---|---|
| **Whole-App Uniform Scaling** | ✅ 人工已验收 | 174/174 几何抽样 PASS，最大误差 0.88 px；Artifact 见 `archive\screenshots\uniform-host\` |
| 四页 × 四档（1.00/0.95/0.90/0.85） | ✅ 完整"小号 PCMig"，无裁切/重叠/结构 reflow | 16 张截图 + 反向放大对拍 + 归一化边界剖面 |
| Native Caption Buttons | ✅ 不参与缩放（恒 46×32） | geometry-summary.txt |
| MinimumUIScale / MaximumUIScale | ✅ **0.75 / 1.15**（已接入并实测钳制） | clamp\clamp-report.json |
| **整页 Vertical Push 页面切换** | ✅ **人工已验收，不要再改** | 7 个 GIF + 接触印相 |
| 快速连点（重叠/空白 → 动画消失） | ✅ 已修为**接管式过渡**（34 运动步 / 3 静止步） | `archive\screenshots\motion\takeover\` |
| **Utility Panel Origin Reveal** | ⚠️ **已实现，等人工确认** | `archive\screenshots\utility-motion\` |
| 更新日志重复入口 | ✅ 已按用户决定删除，只留 v0.5.0 徽章 | Header 截图 `header-after-entry-removal.png` |

**用户明确冻结的**：静态 UI（Acrylic / DAEL / 四页视觉 / Step4 瘦身布局）、整页 Push 的参数与观感、Utility Acrylic 生命周期。
**用户明确禁止的**：再回到"小位移 + Fade"的 Cross-Slide；用"抑制动画"来防重叠；碰 Material / Brush / Acrylic Controller。

---

## 二、本轮干过的事（按时间顺序，含关键数据）

### 阶段 1：UniformScaleHost Spike（PASS）
- 新增 `Presentation\UniformScaleHost.cs`：`PCMIG_UNIFORM_HOST=1` 时在**构造函数最末**运行时重挂 ——
  `window.Content` 摘除 → 固定 1424×892 的 DesignSurface → `Viewbox(Stretch=Uniform)` → 挂回。
  **XAML 一字未改**，未设环境变量时生产路径逐位不变。
- 修掉 XAML 里套 Viewbox 的死路：`Stretch=None` 时 Viewbox 尺寸 = 子元素 DesiredSize ⇒ 内容固定 1424×892；`NaN` 时又不填满窗口。**唯一安全的隔离是运行时重挂。**

### 阶段 2：四个集成点（全部实测通过）
| # | 集成点 | 处理 | 实测 |
|---|---|---|---|
| 1 | `FindName` 名字作用域 | 新增 `ApplicationRoot => UniformScaleHost.ApplicationRoot ?? Content as FrameworkElement`，响应式与浮层定位都改用它 | 不传它会让 `FindName` 全 null ⇒ 响应式整链**静默失效** |
| 2 | 浮层定位坐标系 | `PositionOverlayPanels` 的 root 用 `ApplicationRoot`（尺寸恒 1424×892） | 面板设计坐标 (476.6, 115.3) vs 生产 1.00 的 (477, 116) |
| 3 | 自定义标题栏拖动区 | 不把标题栏排除在缩放外 | 设计 x=60 处拖动 → 窗口位移 (37,23) |
| 4 | 输入坐标 + 视口自校正 | 见下 | 点击视觉中心 → 聚焦 + 回读 `192.168.1.10` |

### 阶段 3：启动视口修正
- Uniform 模式下原根尺寸恒等于设计面 ⇒ 其 `SizeChanged` 不再代表视口 ⇒ `OnRootSizeChanged` 的观测值改读 `XamlRoot.Size`。
- 修正前启动 client=1424×**922**（多 30 DIP 空白带），修正后 = 1424×892 ✓

### 阶段 4：Minimum / Maximum 标定
- 24 张标定图（1.05/1.10/1.15 × 0.80/0.75/0.70 × 四页）。
- **MinimumScale = 0.75**（0.75 四页完整清晰无留白；0.70 需 client 997×624，现有最小尺寸下会出现纵向留白）
- **MaximumScale = 1.15**（协调，不呈电视 UI）
- 下限由 **Win32 最小追踪尺寸**实现（`MainWindow` 的两个最小尺寸常量在 Uniform 模式下由 `UniformScaleHost.MinimumScale` **派生** = 1068×669；非 Uniform 仍 960×640）
- 上限由 **Viewbox `MaxWidth/MaxHeight = DesignSurface × 1.15` + 居中**实现
- 钳制实测：请求 client 900×600 → 实得 1068×700（scale 0.7527）；请求 1800×1200 → **停在 1.1527** 且内容居中
- 已知无害细节：下限档实际 client 高 700 而非 669（差 31 px，`ExtendsContentIntoTitleBar` 的固定偏移）⇒ 上下各约 15 px 留白，但**内容绝不低于 0.75**。**不要**用硬编码 `-31` 去"修"

### 阶段 5：Motion 演进（三代，勿走回头路）
| 版本 | 机制 | 结果 |
|---|---|---|
| U56 | 26 DIP 小位移 + Fade（Cross-Slide） | ❌ 用户否决："机制不对" |
| U57 | **整页视口高度 Push**（无 Fade、420 ms、Clip 限 Workspace） | ✅ 观感通过 |
| U58 | MotionState **抑制** + 460 ms 窗口防重叠 | ❌ 副作用：**点得快时动画直接消失** |
| **U59（当前）** | **接管式过渡**：每段 Push 从页面**当前实际位置**接续 | ✅ 动画持续在跑且不重叠 |

### 阶段 6：Utility Panel Origin Reveal（U60）+ 入口删除（U61）
- Token（`Themes\Motion.xaml`）：Open **260 ms** / Close **190 ms**、Scale **0.945→1** / Close **1→0.985**、TranslationY **−7→0** / Close **0→−5**、缓动 cubic-bezier `(0.16,1)(0.30,1)`
- **Scale 原点对准触发入口**：`CenterPoint.X = 按钮中心 − 面板左缘`，`CenterPoint.Y = 0`（面板顶边）
- 统一 `PCMigUtilityPanelAnchorGap = 5 DIP`（两个面板共用）
- 锚点 = **实际被点的元素**（`sender`）
- 顺序重排：透明就位 → 定位 → `UpdateLayout()` → 再定位 → **才**播动画
- **修掉"覆盖感"根因**：Developer 面板原先**没有 MaxHeight**，内容超高触发旧夹紧逻辑把面板**上提 27 DIP** 盖住入口 ⇒ (a) 给面板加 `MaxHeight="720"`（它内部本来就有 ScrollViewer）(b) 夹紧策略改为**保顶部间距、缩面板高度**
- 删除 `ChangelogEntryButton`（"历史更新"文字入口），更新日志只保留 v0.5.0 徽章入口

---

## 三、当前产品行为（可直接复测的事实）

1. `PCMIG_UNIFORM_HOST` 未设 ⇒ 生产路径**逐位不变**（Motion 是正式功能，默认生效）
2. Uniform 模式：整台 UI 等比缩放，**结构恒为 Canonical**（Step2 左右、Step3 四列统计卡 + 两列明细、Step4 工具栏不折行）
3. 窗口缩小到 client 1068×669 即停（0.75）；放大到 1638×1026 后内容居中不再变大
4. 四页切换 = 整页纵向 Push；快速连点 = 每段都从当前位置接续，动画不消失、不重叠
5. 更新日志只有 v0.5.0 徽章一个入口；面板从徽章下方 5 DIP 处展开（等比缩放下屏幕距离 = 5 × scale）
6. Developer 材质调节面板从它自己的图标下方展开；面板最高 720，超出滚动

---

## 四、代码结构与关键文件（本轮涉及）

| 文件 | 说明 |
|---|---|
| `Presentation\UniformScaleHost.cs` | **新增**：env 开关、DesignSurface、Viewbox、重挂、失败还原、Min/Max 常量 |
| `Presentation\PageTransitionCoordinator.cs` | **新增**：整页 Push 编排、**代次归属**、`CollapseAllExcept` 防重叠闸门、接管式起点 |
| `Presentation\MotionDirector.cs` | `PlayPagePush` / `ReadTranslationY` / `ResetTransitionState` / `PlayUtilityPanelOpen` / `PlayUtilityPanelClose` / `ResetUtilityPanelState` / `ScheduleFallback`（强引用定时器） |
| `Presentation\MotionState.cs` | 未改动；**现在只服务面板链**（页面链已取消抑制） |
| `Themes\Motion.xaml` | 全部 Token（页面 Push、Utility Open/Close、AnchorGap） |
| `MainWindow.xaml` | 页面容器命名 `PageViewport`；删除 `ChangelogEntryButton` |
| `MainWindow.xaml.cs` | Install 接线、Canonical 锁定、`ApplicationRoot`、Clip 维护、`TryPlacePanel`/`ResolvePanelAnchor`、`TogglePanel`/`ClosePanel`、最小尺寸派生 |
| `Views\DeveloperTuningPanel.xaml` | 加 `MaxHeight="720"` |
| `Presentation\WindowMinimumSize.cs`、`ResponsiveLayoutController.cs`、`ShellResponsiveLayout.cs` | 未改动（前者复用，后两者**上一会话**已接线） |

---

## 五、踩过的坑（工具侧，务必逐条避开）

### A. PowerShell 脚本侧（最容易反复踩）
1. **变量名大小写不敏感** ⇒ `$root`（UIA 元素）覆盖 `$Root`（路径）、`$Frames`（计数）覆盖 `$frames`（数组）、`$r` 覆盖 `$R`。
   **本轮一共踩了 5 次**，其中两次把几百个帧写进了 `System.Windows.Automation.AutomationElement\...` 这种目录。
   ⇒ **硬规则：路径/计数变量一律用不可能撞名的名字**（`$RecordingRoot`、`$FrameCount`），禁用单字母与近似名。
2. **PowerShell 函数名撞内置别名**：定义 `function R` 后调用实际执行 `Invoke-History` ⇒ 替换被静默跳过。用动宾式名（`Invoke-Rep`）。
3. **`Invoke-Expression` 子作用域里的变量赋值会覆盖外层**：我用它注入 GIF 编码器时，脚本内部的 `$Root = ...` 把我的 `$Root` 覆盖 ⇒ **push 的 7 个 GIF 被 panel 帧覆盖**（已用原始帧重建）。⇒ 注入外部脚本时**在第一个变量赋值处截断**，或注入后重新赋值。
4. **PS 5.1 按 ANSI 读无 BOM 的 UTF-8 脚本** ⇒ 脚本里的中文（元素名匹配）全部乱码、`-like` 静默失配。
   ⇒ 一律 `Get-Content -Raw -Encoding UTF8` 执行；给脚本加 BOM 也可以，但**编辑器改过会掉 BOM**。
5. **`pwsh` 在本机不存在**，实际是 PowerShell 5.1：不支持 `??`、三元、`(if ...)` 表达式。
6. **ExecutionPolicy 禁用脚本文件** ⇒ 用 `Invoke-Expression (Get-Content -Raw -Encoding UTF8 $path)` 执行。
7. **`$ErrorActionPreference='Stop'` + 文本手术脚本**要带"命中次数断言 + 花括号平衡校验 + 关键锚点存在性检查"，全部通过才写文件（本轮的 `apply-*.ps1` 都是这个模式，可复用）。
8. **文本手术锚点必须容忍行尾**：文件多为 CRLF/LF 混排，用"逐行 Escape + join `\r?\n`"构造正则，锚点**必须包含 `{`**（漏掉会把成员插到类名与 `{` 之间 → 13 个编译错误）。

### B. WinUI / Composition 侧
9. **`PrintWindow(hwnd, hdc, 2)` 能捕获 Composition 位移**（整页 Push 可逐帧取证）—— 但**不重现实时指针状态**（hover/pressed 像素差恒 0.00，已在生产路径交叉归因）。
10. **`CopyFromScreen` 不可信**：本会话实测与窗口内容 meanAbs=239（窗口被遮挡或合成路径差异）⇒ 不要用它取证。
11. **Composition 的 `KeyFrameAnimation` 没有 `Completed` 事件** ⇒ 用 `Compositor.CreateScopedBatch(CompositionBatchTypes.Animation)`；另挂定时兜底。
12. **`DispatcherQueueTimer` 必须持有强引用**（否则被 GC 回收，兜底永不触发）—— `MotionState` 里本来就对，我在 `MotionDirector` 里一度漏了。
13. **`visual.Properties.TryGetVector3(...)` 返回 `CompositionGetValueStatus` 枚举**，不是 `bool`（写成 `if (...)` 会 CS0029）。
14. **`Microsoft.UI.Xaml.Duration` 不能与 `TimeSpan` 直接相加**，要取 `.TimeSpan`。
15. **Storyboard 无法解析 `Translation` / `TranslateX`**（抛 "Cannot resolve TargetProperty"）⇒ 位移一律走 Composition。
16. **`ElementCompositionPreview.SetIsTranslationEnabled`** 是让 `Translation` 通道可动画的前提。
17. **UIA `IsOffscreen` 不能判断"页面是否可见"**：被 Composition 推出裁剪视口的页面同样报 true。
18. **WinUI 的 `Collapsed` 元素在 UIA 树中不出现**（或 IsOffscreen=true）⇒ 用它判可见性可以，但**判"在途"不行**。
19. **XAML 编译错误会级联出"Unknown type 'Step1ConnectPage'"这类假错误**：真因通常是 C# 编译先失败（本次是 CS0029）。看完整输出、别只 grep `error`。
20. **exe 被运行中的进程锁定** ⇒ 构建报 MSB3027/MSB3021；先 `Stop-Process PCMig.WinUI`。

### C. 测试/自动化侧
21. **首次点击会被用于激活非前台窗口**（不是 HitTest 失败）⇒ 点击前 `Ensure-Foreground`（含 ALT 轻敲）+ 在无害空白处先烧掉一次"激活点击"。
22. **键盘加速器 / SendKeys 在窗口未真正激活时静默失败** ⇒ 同上；否则会把"快捷键没生效"误判成代码回归。
23. **UIA `BoundingRectangle` 可能返回 ±∞** ⇒ 转 `[int]` 前判 `IsNaN/IsInfinity`。
24. **探针元素自身的 padding/对齐会污染推算**：用 `CloseButton.Y − 16` 推面板顶边得到的结果与文本元素推算差 39 DIP，互相矛盾而不可用。
25. **半透明 Acrylic 面板在像素上没有强对比边界** ⇒ 整帧边缘密度与列剖面都找不到可靠的面板顶边。
26. **把多帧拼成接触印相会给视觉模型造成明显误读**（编造出应用里不存在的步骤数/文案）⇒ 判断动画只信**单帧原图**与 **CSV 量化**，拼接图仅作人眼速览。
27. **抓帧必须节流**：PrintWindow 单帧只要约 10 ms，不加节流会在 420 ms 动画结束前就把 burst 用光（实测 18 帧只覆盖 187 ms）⇒ 按 24 ms 定间隔取样，22 帧覆盖约 520 ms。
28. **本机没有 ffmpeg / ImageMagick / Python** ⇒ 自研 GIF89a 编码器（`archive\scripts\png-sequence-to-gif.ps1`），产出后**必须用 GDI+ 回读验证**（历史上曾产出非法 GIF）。GIF 正确顺序：GCE → Image Descriptor → LZW min code size → LZW 数据子块。

---

## 六、我自己引入的回归与失误（诚实记录）

| # | 失误 | 后果 | 修法 |
|---|---|---|---|
| 1 | U58 用"抑制动画"防重叠 | 用户实测"点得快动画直接消失" | U59 改接管式 |
| 2 | U58 把 `MotionWindow` 从 230 改成 460 ms | 该常量被**面板链共用** ⇒ 连带抑制 Utility Panel 开合 | 恢复 230 ms 并注明仅面板链使用 |
| 3 | 合成长时间用了 `-Path ...\*` 但**漏 `-Recurse`** | 归档时漏掉嵌套目录内容 | 补 `-Recurse` |
| 4 | `Invoke-Expression` 覆盖 `$Root` | push 的 7 个 GIF 被 panel 帧覆盖 | 用原始帧重建（已恢复） |
| 5 | 变量名撞名 5 次 | 帧写错目录、脚本报错 | 已列入工具坑硬规则 |

---

## 七、未完成的事（按优先级）

### P0 —— Utility Panel Motion 的人工确认（本轮交付但未验收）
- 用户第十九节的位置验收**我没能可靠量化**（见工具坑 24/25）。需要人工看：
  - `验收截图\UtilityPanel动效\三档位置截图\`（三档 × 三入口 9 张）
  - `验收截图\UtilityPanel动效\锚点特写\`（锚点→面板顶边 3× 特写 9 张）
  - 判据：`PanelTop − AnchorBottom` 在设计坐标下应约 5 DIP（屏幕上 = 5 × 整体缩放），**不得一个 20 一个覆盖入口**
- 观感确认：`验收截图\UtilityPanel动效\*.gif`（Developer 开关 ×2、Changelog 开关 ×2、互切两个方向）
- 若"Scale + Translation 在 Acrylic 上明显糊/闪/不自然"，用户已授权降级为 **Vertical Fade Reveal**（Opacity 0→1、TranslationY −10→0、220 ms，关闭反向）—— **不要在没实测 iOS-style Reveal 之前直接用备用方案**
- 通过后 Utility Panel Motion 即可**正式冻结**

### P1 —— Progress Sweep（用户第 31 节，尚未开工）
- 迁移 Running 时在 Progress Fill 上做柔和 Highlight Sweep，**必须 Clip 在已完成 Fill 区**（Progress=35% ⇒ 只在 0–35% 移动，不能扫满 100%）
- Running 运行 / Paused·Stop·Error 停止 / Completed 停循环（可给一次完成高亮）/ 0% 不跑 / 系统关动画则关闭
- Token 已预留：`PCMigMotionProgressSweepDuration`
- ⚠️ 需要先摸清 Step3 与底栏进度条的模板结构（我本轮**没有**读过这两处 XAML）

### P2 —— 极端 Aspect Ratio 下的留白处理
- 窗口宽高比与 1424:892 不一致时，Viewbox 居中会露出窗口 Backdrop（Acrylic）。当前行为正确但未做产品决策（是否接受留白 / 是否改对齐）

### P3 —— 未接线的既有事项（来自上一会话，仍未做）
- `ResponsiveLayoutController` 的旧 Wide/Normal/Compact 分支在 Uniform 模式下已由 Canonical 锁定旁路，但代码里仍存在；是否清理未决
- 施工卡 D5 提到的"禁用态按钮无 DAEL 边"（需用户授权改 `Themes\Controls.xaml`）
- `tests\PCMig.Core.Tests\WinUiStep1ContractTests.cs` 的契约变更记录

---

## 八、回退点与备份

| 用途 | 位置 |
|---|---|
| **主文件级快照（勿删）** | `E:\Project\镜像备份源码\PCMig-v0.5.0-pre-responsive-motion-20260928-131622`（2963 文件 / 700 条 SHA256） |
| 本轮各阶段改动前快照 | `archive\tmp\*.before-u56-uniformhost.*` / `*.before-u57-push.*` / `*.before-u58-rapidfix.*` / `*.after-u58-rapidfix.*` / `*.before-u59-takeover.*` |
| 桌面归档目录的三次清空前全量备份 | `archive\backup-影响面代码-清空前-20260928\`、`archive\backup-新建文件夹4-分类目录-20260928\`、`archive\backup-新建文件夹4-20260928-u59\`、`archive\backup-新建文件夹4-20260928-u61\` |
| 关闭新特性 | 不设 `PCMIG_UNIFORM_HOST` 重启（Motion 默认生效，无法用 env 关闭） |
| 慢放取证 | `PCMIG_MOTION_SLOWMO=<倍数>`（默认 1，不影响产品） |

**Git：HEAD 仍是 `c9aef30`，不含本轮成果。不要 `reset --hard`、不要用旧 tag 覆盖工作目录。**

---

## 九、新会话接手第一件事

1. **读**：`D:\Users\User\Desktop\新建文件夹 (4)\判断文档\` 里的 6 份文档（尤其 `Phase-A-施工卡.md` 与本交接文档）
2. **不要重新侦察**：本轮所有结论、参数、坑都在上面
3. **先确认构建与运行**：
   ```powershell
   cd "E:\Project\deepseek work\PCMig"
   dotnet build src\PCMig.WinUI\PCMig.WinUI.csproj -c Release -m:1     # 必须 -m:1
   ```
   （`.sln` **不包含** WinUI 项目，必须单独构建这个 csproj）
4. **要跑应用看效果**：必须挂在**受管后台作业**下（`run_in_background: true` + `Wait-Process`），否则 pwsh 调用结束会回收 GUI 进程
5. **接着做 P0**（Utility Panel 人工确认）→ 通过后做 P1（Progress Sweep）

### 可复用的脚本（全在 `archive\scripts\`）
| 脚本 | 用途 |
|---|---|
| `apply-u57-push-motion.ps1` / `apply-u59-takeover.ps1` / `apply-u60-utility-shell-1/2.ps1` | 文本手术模板（命中断言 + 花括号校验 + 原子写入） |
| `uniform-host-capture.ps1` | 四档/任意档 × 四页截图（PrintWindow，支持 `UH_SCALES`/`UH_PREFIX`/`UH_META`） |
| `uniform-host-uia-geometry.ps1` + `uniform-host-geometry-summary.ps1` | UIA 几何抽样与比值判定 |
| `uniform-host-analysis.ps1` | 反向放大对拍 + 归一化边界剖面 |
| `uniform-host-interaction.ps1` | 真实输入验收（HitTest/输入/滚轮/拖动/浮层） |
| `rapid-workspace-probe.ps1` / `rapid-visibility-probe.ps1` | 快速连点探针（**注意**：两者的判据都有已知缺陷，见工具坑 17/24/25） |
| `u59-takeover-check.ps1` | 接管式过渡验证（帧间差异序列） |
| `u60-panel-record.ps1` / `u60-panel-shots.ps1` / `u60-anchor-closeup.ps1` / `u60-gap-measure.ps1` | 面板开合录屏、三档截图、锚点特写、像素剖面 |
| `png-sequence-to-gif.ps1` / `make-push-gifs.ps1` | GIF 编码器 + 批量合成（**注意坑 3**） |

---

## 十、环境事实（省得再测）

| 量 | 值 |
|---|---|
| DPI / RasterizationScale | 96 / 1 |
| 窗口外框 vs 客户区 | 1440×900 vs **1424×892**（差 +16/+8） |
| Canonical DesignSurface | **1424 × 892** |
| 生产最小客户区 | 960×640；Uniform 模式 **1068×669**（= 1424/892 × 0.75） |
| Uniform 上限对应客户区 | 1638×1026（= × 1.15） |
| 页面视口（PageViewport）高度 | 0.85 档约 583 DIP（Push 的位移距离就用它） |
| 构建 | `dotnet build src\PCMig.WinUI\PCMig.WinUI.csproj -c Release -m:1`，约 35 s |
| exe | `src\PCMig.WinUI\bin\Release\net8.0-windows10.0.19041.0\win-x64\PCMig.WinUI.exe` |
| 窗口类名 | `WinUIDesktopWin32WindowClass`，标题 `PCMig 迁移工具 · v0.5.0` |