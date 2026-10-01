# 工作交接 · 20260927 · Final Polish 后半程实现与 DPI 验收卡点

> **新增档案，不覆盖任何历史交接。** 前置档案：`docs\工作交接-20260927-FinalPolish首批实现与验收阻塞.md`（首批实现，本会话的起点）。
>
> 权威工作区：`E:\Project\deepseek work\PCMig`
> 分支：`feature/winui-v0.5.0` ｜ HEAD：`c9aef30454081fd81a13c8c9feef029f0629ad67`
> 当前状态：**仍在既有脏工作树之上继续；未提交任何改动；未发版；未触碰交付区 `E:\Project\PCMig`、工作副本 `D:\PCMig`、`tools\*.ps1`、发版脚本。**

---

## 0. 一句话结论

上一份交接第 7 节列出的 8 项未完成/风险中，本会话**实际解决了 5 项并全部取得真实实测证据**（结构重排、Win32 最小尺寸、Button 四态、Developer 入口迁移、Motion 防重入），另**新打通**了真实桌面启动闭环、窗口尺寸/比例矩阵与一个可交付的流程动画。

**剩余 2 项卡住**：DPI 125%/150% 真实实测（需用户授权改系统缩放）与完整人眼视觉自查（视觉后端持续超时）。目标已按纪律标记 **blocked**。

---

## 1. 本会话用户目标与边界

1. 用户先要求"读取上一份工作交接文档并快速了解，以便继续工作"。
2. 随后要求把会话内容、改动文件、规划方案**全部写进工作交接**。
3. 用户从选项中选定 **"先打通真实桌面验收（推荐）"**：重建并启动 WinUI EXE、真实窗口截图、视觉自查。
4. 之后以 goal 自动续轮方式推进 Final Polish 剩余项。

**边界（全程遵守）**：功能冻结有效 —— 未动 ViewModel / Command / 绑定 / 迁移与 Robocopy 逻辑 / 状态机 / 错误处理 / 网络检测 / 存档格式 / 导航语义；只改 UI / 视觉 / Theme / Style / 模板层 + 新增 presentation 辅助类。未发版、未改发版脚本、未碰交付区与工作副本、未用破坏性 git 命令。

---

## 2. 本次会话实际新增/编辑的文件

> 仓库中存在**更早会话遗留的大量未提交/未跟踪文件**（`git status` 里 `M src/PCMig.Gui/*`、`M src/PCMig.Core/*`、`M tools/release.ps1`、一批 `?? docs/*` 与 `?? tools/l3-*` 等**均非本会话产出**）。下表只列可由操作记录与文件时间戳确认的本会话条目。
>
> 归属判据：本会话第 2–9 轮改动集中在 **14:59–15:49**；13:56–13:57 的 `ResponsiveLayoutController.cs`、`Motion.xaml` 属**首批（上一会话）**，见前置交接第 4 节。

### 新增

| 文件 | 内容 |
|---|---|
| `src\PCMig.WinUI\Presentation\WindowMinimumSize.cs`（2893 B） | 基于 `SetWindowSubclass` + `WM_GETMINMAXINFO` 的最小追踪尺寸；先 `DefSubclassProc` 再抬高 `ptMinTrackSize`；`SubclassProc? _proc` 保活；`_installed` 幂等。 |
| `src\PCMig.WinUI\Presentation\MotionState.cs`（4116 B） | Motion 防重入 `IsTransitioning`：按 key 记录过渡在途、自释放 `DispatcherQueueTimer`、`TryBegin/Release`；**默认无副作用**，仅 `PCMIG_MOTION_TRACE=1` 时写 `%TEMP%\pcmig-motion-trace.log`。 |
| `archive\scripts\dpi-measure.ps1`（6613 B，UTF-8 **BOM 已验 EF BB BF**） | **只读** DPI 验收脚本：不写注册表、不改系统设置；按 `scale=GetDpiForWindow/96` 线性推算期望值，逐项判 PASS/FAIL。 |
| `archive\screenshots\final-polish-20260927\**` | 本会话全部真实截图/证据（清单见第 3 节末）。 |

### 修改

| 文件 | 本会话内容 |
|---|---|
| `src\PCMig.WinUI\MainWindow.xaml.cs` | ① `MinimumClientWidthDip=960` / `MinimumClientHeightDip=640` 常量与 `WindowMinimumSize.Install(hwnd, 960, 640, CurrentScale())`；② `ApplyResponsiveLayout` 把档位下发给 3 个页面 `ApplyLayoutMode(layout.Mode)`；③ 订阅 `Nav.Changing` 并新增 `OnNavChanging` / `PrepareEntrance` / `EnsureEntrance` / `PageForStep`；④ 两个浮层按钮处理器改用 `PrepareEntrance`。 |
| `src\PCMig.WinUI\MainWindow.xaml` | `DeveloperTuningButton` 从 `TitleBarRoot` 第 3 列（Caption 条）迁入产品 Header，紧邻 `ChangelogButton`。 |
| `src\PCMig.WinUI\Presentation\StepNavigation.cs`（6665 B） | 新增 `public event Action<StepKind, StepKind>? Changing`；`Current` setter 改为"值真的变化时**先广播 Changing 再 Set**"，使 Shell 可在可见性变化前判定动效重入。**纯通知，不改导航语义**。 |
| `src\PCMig.WinUI\Themes\Materials.xaml` | 新增 `ButtonSurfaceHoverBrush` #3DFFFFFF / `ButtonSurfacePressedBrush` #1FFFFFFF / `ButtonSurfaceDisabledBrush` #26FFFFFF。 |
| `src\PCMig.WinUI\Themes\Controls.xaml` | `PCMigSecondaryButton` 由"仅 Setter"改为**完整 ControlTemplate 四态**（Normal / PointerOver / Pressed / Disabled）；`PCMigFooterActionButton` 的硬编码 `#3DFFFFFF`/`#1FFFFFFF` 改指共享 token（**值等价，零视觉位移**）；文件内不再有裸写 `Value="#..."`。 |
| `src\PCMig.WinUI\Views\Step2SelectDataPage.xaml(.cs)` | 栅格命名 `SelectColumns` / `SelectRightStack` / `SelectLeftCard` + `ApplyLayoutMode`：Compact 时右栏下移到第 2 行、列宽 0。 |
| `src\PCMig.WinUI\Views\Step3ProgressPage.xaml(.cs)` | `StatCardsGrid` + `StatCard0..3`、`LowerPanesGrid` + `LowerPaneLeft/Right` + `ApplyLayoutMode`：Compact 时统计卡 2×2、下区堆叠。 |
| `src\PCMig.WinUI\Views\Step4ResultPage.xaml(.cs)` | 工具条由横向 `StackPanel` 改为 `ToolbarGrid`（7 Auto 列 + 2 行）+ `ApplyLayoutMode`：Compact 时后 3 个按钮换到第 2 行。 |

---

## 3. 逐项实测证据（真实数据，非推断）

### 3.1 Win32 最小追踪尺寸（第 2 轮）
- `SetWindowPos 600×400` → 被夹到 **960×640**；`1200×800` 不受影响。
- **真实拖拽**右下角冲向 ~720×480 → 同样夹到 **960×640**；恢复后正常。
- 960×640 下 UIA 扫描 82 个元素，**横向越界 0**。

### 3.2 Compact 结构重排（第 3 轮）
- Step2 Compact：右栏内容从 y=371 下移到 y=935/1131；Step3 Compact：统计卡 2×2（y=494/580，x=414/721）；Step4 Compact：工具条两行（y=400/453）。
- **Wide 恢复是在进入过 Compact 之后验证的**：Step2 右栏 x=1318/1219；Step3 四卡同 y=536；Step4 同 y=406。
- Compact 下四页横向越界全 0；Step2 滚动生效（连接与安全提示 Y 1131 → 748）。

### 3.3 Button 四态材质（第 4 轮，像素亮度实测）
| 按钮 | 正常 | 悬停 | 按下 | 禁用 |
|---|---|---|---|---|
| Primary | 138.75 | **148.90 (+10.15)** | **111.95 (−26.80)** | — |
| Secondary | 229.81 | 230.68 (+0.87) | 229.09 (−0.72) | 220.93 |

Secondary 禁用态文字最低亮度 **131.7 → 161.1**（前景变暗确认）。方向全部正确。

### 3.4 Developer 入口迁移（第 5 轮）
- 从 `TitleBarRoot` 第 3 列 **x=1403 y=164**（Caption 条内）迁到产品 Header **x=580 y=217**（`ChangelogButton` x=516 w=54 的右侧）。
- 开合接线完好：打开时 `GlobalSlider` 1233,304 / `SaveButton` 1417,715；关闭后 NOT-FOUND。
- **视觉后端独立印证**（本会话唯一一次成功调用，非我口述）："版本徽章右侧还有一个灰色滑块调节小图标（即触发悬浮提示的按钮）……悬浮提示文字为『开发者材质调节（Developer Visual Tuning）』"。

### 3.5 Motion 防重入 `IsTransitioning`（第 6 轮）
以 `PCMIG_MOTION_TRACE=1` 启动后读 `%TEMP%\pcmig-motion-trace.log`：

| 场景 | BEGIN | SUPPRESS | END | 结论 |
|---|---|---|---|---|
| 页面：基线 1 次 + 突发 **40 次 / 34.3ms 每次**（远快于 230ms 判定窗口） | **7** | **34** | **7** | 7+34=41 = 1+40，无遗漏 |
| 面板：20 次快速开合（30ms 间隔） | **4** | **16** | **4** | 4+16=20 |

- 突发后可见页面 = **最后一次请求的步骤** → **状态永远照常切换，没有吞点击**。
- BEGIN == END → 无在途泄漏；突发后再单次导航**重新出现 BEGIN** → 没有被永久卡在"过渡中"；面板 churn 后仍能正常开/关。
- 设计取舍：被抑制的只是**重入的那一次入场动画**（摘掉该页/该面板的 `Transitions`），导航/面板状态一律照常切换。

### 3.6 尺寸 / 比例矩阵（第 7 轮）
- **外层窗口 1440×900 = 1.6000，16:10 精确**；Win32 客户区 1424×892。
- 代码声明的 XAML 视口目标是 1424×**891** DIP，`OnRootSizeChanged` 用 **±1.5 DIP 容差**一次性自校正；892 与 891 差 1px 落在容差内，**判定为设计内取整差，未修改**。
- 响应式断点**实测精确落在 client 1320 / 1120 DIP**：outer 1336（client 1320）侧栏仍 276；outer 1330（client 1314）首次 246；outer 1136（client 1120）仍 246；outer 1130（client 1114）首次 220。
- **四页 × 六档宽度（1700/1440/1280/1160/1100/960）= 24 组合，横向越界全部 0。**

### 3.7 DPI 验收脚本（第 9 轮）
- 本机：**单显示器 1920×1080、`GetDpiForWindow = 96`（100%）、无 `PerMonitorSettings` 覆盖、`LogPixels` 未设置。**
- 在 100% 下跑 `dpi-measure.ps1`：**PASS=9 / FAIL=0，exit 0**。逐项：client 1424×892（期望 1424×891，容差 2）、比例 1.5964、侧栏 276/246/220、最小夹取 960×640、最小尺寸下落入 Compact 档。
- 期望值按 scale 线性推算，因此 **125% / 150% 可直接复用同一脚本**，无需改代码。

### 3.8 流程动画（第 8 轮，"视频"的替代形态）
- 本机**没有 ffmpeg、也没有可脚本化的录屏器**（`Get-Command ffmpeg` → 无）。
- 用 **10 帧真实窗口截图**（步骤1→2→3→4→反向回3→反向回2→开发者面板开/关→更新日志开/关）经 WPF `GifBitmapEncoder` 合成动画。
- **踩到真坑并修复**：编码器把每帧延时写成 **0**、且**没有 NETSCAPE2.0 循环扩展**（这样的 GIF 只会闪一下）。按 GIF 字节结构改写 10 个 `21 F9 04` 控制块并插入循环扩展后回读：`delays=30,30,30,30,30,30,30,30,30,30`、`loop=0（无限）`。
- **验证没改坏**：GIF 回读解出的第 1/10 帧与原帧做像素比对，差异 **0.5421% / 0.573%**（阈值 24）——纯 256 色量化损失。
- 产物：`flow-10frames.gif`（10 帧、860×538、300ms/帧、无限循环、2226 KB）、`flow-filmstrip.png`（带标签胶片条 1412×1322）。

### 3.9 回归与稳定性
| 验证 | 结果 |
|---|---|
| `dotnet build src\PCMig.WinUI\PCMig.WinUI.csproj -c Release` | 本会话每次均 **0 警告 / 0 错误** |
| `dotnet test tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj -c Release` | **130/130 通过，失败 0，exit 0** |
| `git diff --check -- src/PCMig.WinUI` | **exit 0，无输出（干净）** |
| 近 3 小时应用事件日志 | **无 PCMig 相关事件** |
| WER | `ReportArchive` 不存在；`CrashDumps` 内最新 PCMig.WinUI dump 为 **09-27 12:21**，**早于本会话改动**，即**无证据表明本轮改动导致崩溃** |

### 3.10 证据清单（`archive\screenshots\final-polish-20260927\`，共 80+ 文件）
关键：`v2-step1..4.png`、`v2-step2/3/4-compact.png`、`v2-developer-panel.png`、`v2-changelog-panel.png`、`flow-10frames.gif`、`flow-filmstrip.png`、`flow-frames/01..10.png`、`min-960x640-*.png`、`step2/3/4-reflow-compact.png`、`step2/4-reflow-wide.png`、`state-primary-1..3.png`、`state-secondary-1..4.png`、`button-four-states-sheet.png`、`header-developer-entry.png`、`uia-step1..4.txt`、`size-matrix.txt`、`size-ratio-dpi-matrix.txt`、`dpi-matrix-results.txt`。

---

## 4. 未完成 / 未验证（如实记录，不得当成已完成）

| # | 项 | 状态 |
|---|---|---|
| 1 | **DPI 125% / 150% 真实实测** | **完全未做**。脚本已就绪并在 100% 验证通过，但从未在 125%/150% 下运行过。 |
| 2 | **完整人眼视觉自查** | **未完成**。本会话视觉后端 11+ 次调用中**仅 1 次成功**（header 局部），其余全部 60s 超时；连 27KB 单图也超时。未做全页人眼验收。 |
| 3 | **真视频（MP4）** | 未产出。本机无编码器/录屏器；只交付了动画 GIF。**GIF 的实际播放效果我也没看过**（只做了结构校验 + 帧像素回读）。 |
| 4 | Motion 其余四项 | **未做**：方向性 Page 出/入场、Sidebar 选中条过渡、Panel Exit、Dialog 动效。本会话只做 `IsTransitioning` 防重入。 |
| 5 | Update Log 视觉/完整行为实机验证 | 未做（面板已截图 `v2-changelog-panel.png`，但未做视觉审阅）。 |
| 6 | `DeveloperVisualSettings` 本地持久化对画面影响 | 未查。 |
| 7 | 125%/150% 下的最小尺寸/断点 | 未实测（脚本已参数化）。 |
| 8 | 会话结束时进程状态 | EXE 已不在运行；**未记录其退出码**（无崩溃证据，但也无法断言"正常退出"）。 |
| 9 | 未跑 `tools\stability-test.ps1` | 未跑（本会话未改迁移逻辑，且该脚本属"改脚本需授权"清单，未触碰）。 |

---

## 5. 卡点与需要的授权

### 卡点 A：DPI 125%/150% —— 需要用户决定
- 本机**单屏 1920×1080、系统缩放 100%**，不改变系统缩放就**造不出 125%/150% 的真实 DPI 环境**。
- 试过且**不成立**的替代路径：`SetThreadDpiAwarenessContext` 只能让进程**看到更低 DPI**（视作 96），无法抬到 120/144；兼容性覆盖项同理；`SPI_SETLOGICALDPIOVERRIDE` 需注销才生效（更 disruptive）。
- 因此**必须改「设置 → 显示 → 缩放」**，属系统级改动。按 `AGENTS.md` 三点五（权限放宽不构成豁免）与"破坏性动作先报路径与回退"，**未获当轮明确指令前不做**；已连续 3 轮（第 7/8/9 轮）请求授权，未获回复。
- **需要用户二选一**：① 授权我改到 125%（必要时 150%）跑完后改回 100%（无文件副作用，唯一风险是改回失败会让桌面停留 125%）；② 用户自己在设置里切换，我只跑脚本。

### 卡点 B：视觉后端 —— 环境问题
- 视觉后端**间歇可用**（本会话唯一一次成功就在开头），其余全部 60s 超时；`inspect_image` 走桥接文件同样超时。
- 按纪律**已停止反复重试**（改写问题解决不了超时）。需要后端恢复，或由用户人工看图给结论。

---

## 6. 备份与回退

| 项 | 位置 / 结果 |
|---|---|
| 会话前整体基线 | tag `v0.5.0-before-final-polish-20260927` → `c9aef30454081fd81a13c8c9feef029f0629ad67`；镜像 `E:\Project\镜像备份源码\PCMig-v0.5.0-before-final-polish-20260927`（208 文件）+ bundle + `-清单.md` |
| 中点基线（首批之后） | tag `v0.5.0-final-polish-firstbatch-20260927` → `8bf775332ed9d0677192a1c8ed14398b4fa6f52f`（由 `git stash create` 产生，**仅覆盖 tracked 改动，不含未跟踪新文件**）；镜像目录（214 文件）、bundle（6,140,626 B、49 refs、`git bundle verify` 通过）、11/11 关键文件 SHA256 MATCH、清单 `PCMig-v0.5.0-final-polish-firstbatch-20260927-清单.md` |
| 归属提示 | 会话前镜像建在**首批实现之前**，因此**不包含首批与后半程改动**；后半程（本会话）**没有任何备份**，改动全部只在工作树里，**未提交**。 |

**回退禁止**：`git reset --hard` / `git checkout .` / `git restore .` / `git clean -fd`。建议先 `git stash -u` 保全，再按 tag 查看；整目录恢复或 bundle clone 需另行授权。

**逐文件回退提示**：本会话改动集中在 `src\PCMig.WinUI\**` 与 `archive\**`；`archive\**` 为纯新增，删除即可回退；WinUI 源码可用 `git checkout -- <file>`（**单文件**，非整目录）回到 HEAD 版本——但那会一并丢掉首批改动，务必先 `git stash -u`。

---

## 7. 可复现命令（下一会话直接照用）

```powershell
# 0) 每次 pwsh 都是新进程：执行策略必须在同一进程里设
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force

# 1) 重建（必须先杀在跑的 EXE，否则 bin 被锁：MSB3026/MSB3027）
Get-Process PCMig.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 1500
cd 'E:\Project\deepseek work\PCMig'
dotnet build src\PCMig.WinUI\PCMig.WinUI.csproj -c Release

# 2) 带 Motion 诊断启动（不设该变量则完全无副作用）
$env:PCMIG_MOTION_TRACE='1'
$exe='E:\Project\deepseek work\PCMig\src\PCMig.WinUI\bin\Release\net8.0-windows10.0.19041.0\win-x64\PCMig.WinUI.exe'
$p=Start-Process $exe -PassThru
# 诊断日志: %TEMP%\pcmig-motion-trace.log   （BEGIN / SUPPRESS / END）

# 3) 真实窗口截图（PrintWindow，失败自动降级 CopyFromScreen）
& 'E:\Project\deepseek work\archive\scripts\uishot-winui.ps1' -ProcId $p.Id -Out 'D:\out.png' [-UseScreen]
#   输出 WINDOW/CAPTURE-METHOD/SHOT 三行，可判定用的是哪条路径

# 4) DPI 验收（只读；改完系统缩放后跑）
& 'E:\Project\deepseek work\archive\scripts\dpi-measure.ps1' -Out '...\dpi-matrix-results.txt'
```

**导航与测量约定（本会话验证有效）**：UIA `AutomationId`（x:Name 即可）或 `NameProperty`；用 `InvokePattern` 触发，**不要靠鼠标坐标猜**。四页可见性判定可用各页副标题文本存在性（折叠页元素不在 UIA 树里）。

---

## 8. 踩坑与修复（对后续会话最省时间的部分）

1. **PowerShell 单字母函数名会被别名覆盖**：`function R` 被 `Invoke-History` 抢走 → 报"找不到接受实际参数"。**用多词函数名**（如 `ShowRects`/`GotoStep`）。
2. **`$x = MyFunc ...` 会把函数里所有管道输出都吞进 `$x`**，导致诊断行静默消失 → 函数内的报告用 `Write-Host`，返回值才用 `return`。
3. **PowerShell 函数调用不能带括号**：`NavCard()` → `应为表达式` 解析错误。写 `NavCard`。
4. **`Set-ExecutionPolicy -Scope Process` 必须和脚本在同一 pwsh 进程**：每次 `run_code` 的 pwsh 都是全新进程，漏写必报"在此系统上禁止运行脚本"。
5. **`SetCursorPos` 不会触发 WinUI `PointerOver`**：悬停态必须用**真实点击**（`mouse_event` LEFTDOWN/UP）诱导，且测量时指针要留在控件上。
6. **残留的浮层面板会给下层页面压暗**，导致颜色测量错（按钮读成 196,215,238 而非纯蓝 66,149,246）→ 测色前先循环关掉遮罩（`CloseButton` 直到"上一版"消失）。
7. **构建报 MSB3026/MSB3027/MSB3021"文件被 PCMig.WinUI 占用"**：在跑的 EXE 锁住 `bin\...\PCMig.WinUI.exe` → **先杀进程再 build**。
8. **WPF `GifBitmapEncoder` 不写每帧延时（回读为 0）且不加 NETSCAPE2.0 循环扩展** → 必须事后按字节改写 GCE 延迟并插入循环块，否则 GIF 是废品。`.NET System.Drawing` 无法原生导出多帧 GIF。
9. **`run_code` 里把参数对象直接写在模板字符串后面** → `Expression expected`。安全写法：用 `string[].join("\n")` 拼脚本，或让模板以 `\`;` 单独结尾再调 `tools.pwsh({...})`。
10. **未跟踪目录 `archive\` 不在 git 跟踪内**：全部证据（截图/GIF/脚本/矩阵文本）只存在于文件系统，**不受 git 保护**，别指望 tag 能恢复。

---

## 9. 纪律遵守声明

- 未发版；`tools\release.ps1` 与所有 `tools\*.ps1` **未编辑**；未改 `docs\更新日志.md` 版本内容。
- 未删除/覆盖/移动交付区 `E:\Project\PCMig`、工作副本 `D:\PCMig`、镜像备份源码。
- 未执行任何破坏性 git 命令；未提交、未 stash 现有改动。
- 未改动功能冻结范围内的业务代码（Core / CLI / ViewModel / Command / 绑定 / 迁移与 Robocopy / 状态机 / 存档格式 / 导航语义）。
- 系统级改动（显示缩放）**未执行**，等用户当轮指令。
- 本文件为**新增**交接档案，未覆盖任何历史交接。文中不含凭据明文。
- 新增/编辑的 `.ps1` 已复查前三字节 `EF BB BF`。

---

## 10. 下一会话建议顺序

1. **先拿用户对卡点 A 的决定**（授权改缩放 / 用户自己改），然后跑 `dpi-measure.ps1`，把 100/125/150 三档结果追加进 `dpi-matrix-results.txt`。
2. **重试视觉后端**完成四页 + Compact + 两个浮层的完整人眼自查；若仍不可用，请用户人工看图（`v2-*.png`、`flow-filmstrip.png`）给结论。
3. 若用户要求真视频：先解决编码器（安装 ffmpeg 或授权），再录 步骤1→4→反向 + 两个面板。
4. 如需继续 Motion：方向性 Page 出/入场、Sidebar 选中条过渡、Panel Exit、Dialog 动效（注意延续 `MotionState` 的 key 机制与 `PrepareEntrance` 模式，别另起一套）。
5. 发版前必须回到铁律：先写 `docs\更新日志.md` + `docs\使用说明.txt`，再走 `tools\release.ps1` 五道闸门、四件套、逐文件 SHA256、发版后三验；**当前 WinUI 仍不在 `PCMig.sln`，`release.ps1` 仍只打 WPF Gui/CLI**，把 WinUI 纳入发版是独立决策，需用户明确授权。
