# 工作交接 — 20260927 — GIF 编码器缺陷修复与 Dialog 动效范围核查

> 承接 `工作交接-20260927-真实动效证据与标注原文更正.md`。
> 本轮（Round 6）**修复了一个我自己引入的真实缺陷**：上一轮交付的 `R-nav.gif`
> **是坏文件**（GDI+ 拒绝解码）。已定位根因、修复、并用**独立解码器**验证通过。

---

## 一、上一轮的 GIF 是坏文件（自我更正）

### 症状
`archive\evidence\R-nav.gif`（200,043 字节）**无法被 System.Drawing/GDI+ 解码**，
报 `Out of memory.`（GDI+ 对格式错误的通用错误码，不是真的内存不足）。

**对照证明不是解码器的问题**：同一进程解 `C:\Windows\System32\@facial-recognition-windows-hello-rejuv.gif`
正常返回 `帧=155 尺寸=662x375`。

⇒ **是我的编码器产出了非法 GIF。上一轮"GIF 已产出"的说法只对了一半：文件存在，但不可用。**

### 根因（精确定位到一行）
`archive\scripts\winui-png-to-gif.ps1` 的图像描述符写入段：

```powershell
$bw.Write([byte]0)                 # no local table, not interlaced   ← 打包字节，正确
# LZW data, split into <=255-byte sub-blocks                          ← ❌ 缺一行
$lzw = LzwEncode $fr.Px 8
```

**GIF89a 的规定顺序是**：图像描述符（10 字节）→ **LZW 最小码长（1 字节）** → LZW 数据子块。
**编码器从未写出那个"LZW 最小码长"字节**，导致整个后续数据流前移 1 字节。

**字节级证据**：修正前首个图像描述符在 @808，其 `+10` 处（应为 LZW 最小码长，值 8）
实际是 `0xFF` —— 而 `0xFF` 正是合法 LZW 流（256 色，9 位码，clear=256）的**首字节**
（`clear(9) + 编码0(9)` = bit 0..8 全 1、bit 9..11=000 ⇒ 首字节 `0xFF`）。
**即：LZW 数据整体顶掉了本该属于"最小码长"的位置。**

### 修复
在打包字节之后插入：

```powershell
$bw.Write([byte]8)     # LZW minimum code size
```

**修复量的自洽验证**：文件从 **200,043 → 200,061 字节 = +18 字节 = 18 帧 × 1 字节**，精确吻合。

### 修复后验证（**独立解码器**，非我的解析器）
`System.Drawing.Image` + `FrameDimension` / `PropertyTagFrameDelay`：

| 项 | 值 |
|---|---|
| 可解码 | ✅ |
| **帧数** | **18** |
| 尺寸 | 720 × 450 |
| 每帧延迟 | 8（1/100 s）= **80 ms** |
| 总时长 | **1440 ms** |
| 循环次数 | **0 = 无限循环** |

---

## 二、排查过程中我犯的错（记录下来，避免重犯）

这一轮我在字节算术上**反复自相矛盾**，烧掉了大量预算。根因是两个：

### 错误 1：`[byte]` 上的 `-shl` 会按字节宽度截断
```powershell
([byte]2 -shl 8)        # = 0      ← 错！结果被截成 byte
([int][byte]2 -shl 8)   # = 512    ← 正确
```
任何 16 位小端读取**必须**先转 `[int]`：
```powershell
[int]$b[$i] + ([int]$b[$i+1] -shl 8)    # 正确
$b[$i] + ($b[$i+1] -shl 8)              # 错（当 $b[$i+1] 是 byte 时）
```
这个陷阱让 720×450 的画布一度被读成 208×194。

### 错误 2：GCE 的字节长度我算错过一次
GCE 有两种等价数法，我混用了：
- **含终止符**：`0x21`(1) + label`0xF9`(1) + blockSize 字节(1) + payload 4 + 终止`0x00`(1) = **8 字节**
- **不含终止符**：7 字节

我在脚本里用 8、在手工核对里用 7，两套口径互相"证伪"，导致长时间无法收敛。
**规范锚点**：`blockSize` 字节本身要计入，`payload` 长度由 `blockSize` 给出，其后必须有一个 `0x00`。

### 错误 3（最该记的一条）：我曾用"色表中 0x2C 的个数"判断帧数
全文件 `0x2C` 出现 **782 次**，我一度据此怀疑帧数异常。
**但 LZW 压缩数据近似随机，`0x2C` 出现 782 次完全正常。**
**⇒ 不得用"某个字节出现次数"来判断压缩流的结构。**

### 教训
**当自己的解析器与字节 dump 矛盾时，不要继续改解析器 —— 立刻改用独立现成解码器裁定。**
我这轮最后就是靠 `System.Drawing` + 参照 GIF 才收敛的。
另外：**`write` 工具会剥掉 UTF-8 BOM**（死律 8 的老问题），含中文注释的 `.ps1` 一旦丢 BOM，
PS 5.1 会用 ANSI 读它并**改写正文**，使偏移类 bug 更加不可复现 —— 排错脚本应尽量纯 ASCII。

---

## 三、Motion System 的 Dialog 动效：**范围核查结论**

按 67 节指令，Motion System 应含 Dialog 动效。**全项目搜证结果**：

| 查证项 | 结果 |
|---|---|
| `ContentDialog` | **0 命中** |
| `AppDialog` / `ShowAsync` | **0 命中** |
| `PCMigMotionDialogScaleStart` | **仅 `Themes\Motion.xaml:13` 一处定义，全项目无消费者（孤儿 token）** |
| `MotionDirector` 的 Dialog 方法 | **不存在**（只有 `PreparePageEntrance` / `PreparePanelEntrance` / `PlayPanelExit`） |
| 唯一的弹出式 UI | `Step1ConnectPage.xaml:23` 的 `Flyout`（"手动添加共享"，`AreOpenCloseAnimationsEnabled="True"`） |

**⇒ WinUI 线里根本没有对话框，因此"Dialog 动效"没有可施加的对象。**
`PCMigMotionDialogScaleStart = 0.98` 是一个**加了却没接线**的 token。

**这条必须如实计入未完成项，而不是当作"已完成"** —— 我没有为 Dialog 动效产出任何证据，
因为**没有 Dialog 可供产出证据**。

### 附带修正：Flyout 与顶层窗口的关系（推翻我备忘录里的一条桥接假设）
备忘录曾写"Flyout 不创建新的顶层窗口"。实测（`EnumWindows` 按 pid 过滤）：

| 窗口类 | 标题 | 出现时机 |
|---|---|---|
| `WinUIDesktopWin32WindowClass` | `PCMig 迁移工具 · v0.5.0` | 主窗口 |
| `Microsoft.UI.Content.PopupWindowSiteBridge` | `主机弹出窗口` | **点击按钮后出现** |
| `MSCTFIME UI` / `IME` | — | 系统自带隐藏窗口 |

**修正**：Flyout **确实**会让一个顶层窗口出现，但它不是"对话框窗口"，而是 XAML 为 popup 内容
创建的 **host 窗口**（`PopupWindowSiteBridge`），且 **Esc 之后仍然存在** ⇒ **可复用的 popup host**。
所以"没有对话框"的结论不变，但"不创建新顶层窗口"的表述**不精确，已更正**。

---

## 四、本轮其他查证（关闭崩溃方向，未修）

上一轮确证的 **P0 关闭必崩**（`Microsoft.UI.Xaml.dll` `c0000005`）本轮继续排查，**仍未修复**：

| 查证项 | 结果 |
|---|---|
| 调试器（windbg / cdb / kd / procdump / dotnet-dump） | **全部不可用**；Windows SDK Debuggers 目录不存在 ⇒ **无法读转储取调用栈** |
| `dotnet tool list --global` | 空 |
| 关闭路径唯一接线 | `MainWindow.xaml.cs:116` `Closed += (_, _) => ViewModel.Dispose();` |
| `ConnectionViewModel.Dispose()`（L228-236） | `_shareSession?.Dispose()`；logger 刻意不 Dispose |
| `WindowMinimumSize.cs` | **全文无 `RemoveWindowSubclass`**（装了子类化却从不卸载）；L16 注释自认"委托必须保活，否则命中已释放 thunk（进程级崩溃）" |
| `WindowMinimumSize.cs` git 跟踪 | **未被跟踪**（120 个 porcelain 条目） |

**`WindowMinimumSize` 仍被时间线排除**（文件 9/27 14:59，首崩 9/26 23:21）。
**`Closed` → `ViewModel.Dispose()` 与"装了不卸的子类化"是两个候选，但都无因果证据，不得断言。**

**未做（需授权）**：装 WinDbg/符号读转储；二分法逐个移除关闭路径接线。

---

## 五、回归与状态

| 项 | 结果 |
|---|---|
| `R-nav.gif` | **已修复**，18 帧 / 720×450 / 80ms / 无限循环，GDI+ 可解码 |
| `R-nav.mp4` | 143,358 字节，18 帧 1440×900 @12fps，Shell 解码器读时长 00:00:01 |
| 残留 PCMig 进程 | 无 |
| **WER 清洁** | **未达标**（每次关闭仍产生转储 + WER，见上一份交接 §8.5） |
| DPI 125/150 实测 | 仍阻塞（系统级改动需授权） |

---

## 六、未完成 / 待授权（更新）

| # | 事项 | 状态 |
|---|---|---|
| 0 | **【P0】关闭必崩** | 已确证、100% 复现；**未修**；调试器不可用，读转储需授权装工具 |
| 1 | **Dialog 动效** | **无对象可动效**（WinUI 无对话框）；`PCMigMotionDialogScaleStart` 是孤儿 token —— 需用户决定：接线到 Flyout / 新建对话框 / 删除该 token |
| 2 | DPI 125%/150% 实机 | 阻塞（需授权改系统缩放） |
| 3 | 徽章居中 / 文字上下居中 | **实测不支持"未居中"**（≤1px），需用户指认元素与判定口径 |
| 4 | 徽章质感（阴影/高光）、`TopSheen` 模板层级 | 缺陷已定位，**未修**（视觉层需授权） |
| 5 | Dev 面板按钮裁切（溢出 58 DIP） | 已量化，**未修** |
| 6 | 最小窗口尺寸边框（客户区小 16/8） | 已量化，**未修** |
| 7 | `ResponsiveLayoutController.cs:367` 注释纠错 | 未改 |
| 8 | `tools\uishot.ps1` 缺 BOM / WinUI 未进发版链 / Sidebar hover | 仅报告 |

---

## 七、本轮新增/修改文件

| 文件 | 说明 |
|---|---|
| `archive\scripts\winui-png-to-gif.ps1` | **已修复**：补写 LZW 最小码长字节（BOM 已复检 `EF BB BF`） |
| `archive\evidence\R-nav.gif` | **重新生成**，200,061 字节，18 帧，GDI+ 验证通过 |
| `archive\scripts\motion-gif-verify.ps1` | GIF 结构验证器（纯 ASCII，避免 BOM 剥除导致正文被改写） |
| `archive\evidence\gif-walk.txt` / `gif-bytes.txt` | 字节级排查记录（保留作证据） |
| `docs\工作交接-20260927-GIF编码器缺陷修复与Dialog范围核查.md` | 本文档 |