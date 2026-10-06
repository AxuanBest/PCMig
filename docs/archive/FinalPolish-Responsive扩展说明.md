# Final Polish · 响应式扩展说明（Responsive Token 化 + Header / BottomBar / Step1 接线）

- 执行者：实现子代理（响应式布局轨道）
- 范围：**只改** `<仓库根>\src\PCMig.WinUI\`
- 对应指令节：§5 / §6 / §7 / §8 / §36 / §37 / §38 / §39 / §54 / §55
- 状态：**代码完成，未编译验证**（主控正在用运行中的 PCMig.WinUI 做 Motion 取证，构建会撞 exe 文件锁；按主控指令本轮不 build、不 test）

---

## 一、新增 / 修改文件清单

| # | 文件 | 动作 | 说明 |
|---|---|---|---|
| 1 | `src\PCMig.WinUI\Presentation\ResponsiveLayoutController.cs` | **重写** | `ResponsiveLayout` 由 4 个值扩展为 **31 个位置 Token + 38 个派生 Token**；`Calculate(width, height, dpiScale = 1.0)`（可选参数，向后兼容）；新增 `LayoutMode` 断点常量、DensityScale 曲线、§7 权重表 |
| 2 | `src\PCMig.WinUI\Presentation\ShellResponsiveLayout.cs` | **新增** | 把 Token 落到真实元素（Header / 底栏 / 工作区外壳 / Step1），全部按 `x:Name` 解析，解析不到就跳过；**目前尚无调用点** |
| 3 | `src\PCMig.WinUI\MainWindow.xaml` | 修改（仅加 `x:Name`，未改任何布局数值） | 新增 30 个 `x:Name`；`Height="82"` / `Height="64"` 两个行定义加名；`ColumnSpacing="44"` / `<Grid Grid.Column="2" Width="320"` / `<ColumnDefinition Width="80"/>` **字面量原样保留**（契约测试要求） |
| 4 | `src\PCMig.WinUI\Views\Step1ConnectPage.xaml` | 修改（仅加 `x:Name`） | 新增 25 个 `x:Name`（标题块 / 表单卡 / 三个输入 / 前置图标 / 连接按钮 / 共享卡 / 空状态提示）；绑定、事件、markup 结构一字未改 |

**未改**（禁区或他人领地）：`MainWindow.xaml.cs`、`Themes\*.xaml`（含 Motion.xaml）、`Presentation\MotionState.cs`、`Presentation\MotionDirector.cs`、`Views\StepNavigationControl.*`、`Views\DeveloperTuningPanel.*`、`Views\ChangelogPanel.*`、`Views\Step2/3/4*`、`tests\**`、`tools\**`。

---

## 二、ResponsiveLayout 完整 Token 表

`dpiScale` 默认 1.0；下表为**独立复算脚本**（PowerShell 重写同一套公式）输出的实测值。

| Token | Wide @1424×891（density 1.00） | Normal @1200×800（0.9558） | Compact @960×640（0.86） | 驱动方式 |
|---|---|---|---|---|
| `Mode` | Wide | Normal | Compact | 断点 1320 / 1120 |
| `WindowWidth` / `WindowHeight` | 1424 / 891 | 1200 / 800 | 960 / 640 | 入参（DIP） |
| `EffectiveWidth` | 1384 | 1166 | 936 | `w − 2·PageMargin` |
| `EffectiveHeight` | 671 | 597 | 455 | `h − (58 + HeaderHeight + BottomBarHeight + WorkspaceTopGap)` |
| `AspectRatio` | 1.598 | 1.5 | 1.5 | `w / h` |
| `DpiScale` | 1.0（可由调用方传真实值） | ← | ← | 参数，仅用于圆角物理像素吸附 |
| `DensityScale` | 1.00 | 0.9558 | **0.86** | 连续曲线（见第三节） |
| `PageMargin` | 20 | 17 | 12 | 档位基值 × 权重 1.00 |
| `WorkspacePadding` | 20 | 17 | 12 | 权重 1.00 |
| `CardPadding` | 12 | 10.5 | 9 | 权重 0.75 |
| `CardGap` | 16 | 13.5 | 10.5 | 权重 0.90 |
| `SectionGap` | 12 | 9.5 | 7 | 权重 0.90 |
| `SidebarWidth` | 276 | 246 | 220 | 档位（结构） |
| `HeaderHeight` | 82 | 76.5 | 67.5 | 权重 0.45 |
| `BottomBarHeight` | 64 | 59 | 52.5 | 权重 0.45 |
| `IconSize` | 18 | 16.5 | 14.5 | 权重 0.60 |
| `ButtonHeight` | 34 | 33.5 | 30 | 权重 0.45 |
| `InputHeight` | 46 | 43 | 39.5 | 权重 0.45 |
| `BadgeSize` | 34 | 31 | 27.5 | 权重 0.60 |
| `CardCornerRadius` | 16 | 13.5 | 11 | 权重 0.60 + DPI 吸附 |
| `ControlCornerRadius` | 12 | 11.5 | 9 | 权重 0.60 + DPI 吸附 |
| `FontSizeBody` | 15 | 14.5 | 13.5 | 权重 **0.25（最低优先级）** |
| `FontSizeCaption` | 13 | 12.5 | 11.5 | 权重 0.25 |
| `FontSizeTitle` | 29 | 27.5 | 26 | 权重 0.25 |
| `TitleSpacing` | 12 | 9.5 | 7 | 权重 0.90 |
| `RailGap` | 18 | 13.5 | 8.5 | 权重 0.90 |
| `WorkspaceTopGap` | 16 | 9.5 | 7 | 权重 0.90 |
| `HeaderGap` | 14 | 11.5 | 8.5 | 权重 0.90 |
| `BottomBarGap` | 44 | 21 | 10.5 | 权重 0.90 |
| `PanelMaxHeight` | 815 | 724 | 564 | **`max(420, h − 76)` 原公式未动**（避免与 §22/§40 冲突） |

### 派生 Token（复合量，集中定义，不散落各 View）

| Token | Wide | Normal | Compact | 备注 |
|---|---|---|---|---|
| `HeaderPaddingX` / `Y` | 24 / 12 | 21 / 10 | 16 / 8 | 现状 `Padding="24,12"` |
| `HeaderLogoSize` / `HeaderLogoCornerRadius` | 54 / 16 | 50 / 15 | 44 / 13 | 现状 `54×54 / CornerRadius 16` |
| `HeaderTitleRowGap` | 10 | 8 | 6 | 现状 `Spacing="10"` |
| `BadgePaddingX` / `Y` | 10 / 4 | 10 / 4 | 9.5 / 3.5 | 现状版本 Badge `Padding="10,4"` |
| `CapsulePaddingX` / `Y` | 16 / 11 | 14 / 9 | 12 / 8 | 现状源/目标胶囊 `Padding="16,11"` |
| `SourceStatusMaxWidth` | 170 | 140 | 110 | 现状 `MaxWidth="170"` |
| `FooterBarPaddingX` / `Y` | 22 / 9 | 18 / 8 | 13 / 7 | 现状底栏 `Padding="22,9"` |
| `FooterProgressWidth` | 320 | 200 | 140 | 现状 `Width="320"`；Compact 仍 ≥140，**进度条不消失** |
| `FooterSpacerWidth` | 80 | 20 | 0 | 见下「连续性」 |
| `FooterActionPaddingX` / `Y` | 12 / 6 | 12 / 6 | 11 / 5.5 | 现状四动作按钮 `Padding="12,6"` |
| `FooterActionGroupGap` | 12 | 10 | 8 | 现状按钮组 `Spacing="12"` |
| `FooterRateGap` | 6 | 6 | 5 | 现状两组速率 `Spacing="6"` |
| `ShellBottomMargin` | 16 | 14 | 10 | 现状 `Margin="20,0,20,16"` |
| `WorkspacePaddingBottom` | 18 | 16 | 12 | 现状工作区 `Padding="20,20,20,18"` |
| `SecondaryCardPadding` | 14 | 12.5 | 11 | 现状共享卡 `Padding="14"` |
| `InputPaddingLeft` / `Y` / `Right` | 42 / 8 / 14 | 40 / 7 / 14 | 38 / 6 / 14 | 现状 `Padding="42,8,14,8"`；Right 固定 14（不收紧） |
| `PasswordPaddingRight` | 46 | 46 | 46 | 给「显示密码」按钮让位，现状值 |
| `InputIconMarginLeft` | 15 | 14 | 13 | 现状前置图标 `Margin="15,0,0,0"` |
| `FieldRowSpacing` / `FieldColumnSpacing` | 8 / 14 | 7.5 / 13.5 | 7 / 12 | 现状表单 `RowSpacing="8" ColumnSpacing="14"` |
| `SharesHeaderGap` | 10 | 9 | 8 | 现状共享卡表头 `ColumnSpacing="10"` |
| `TitleStackGap` | 6 | 6 | 4 | 现状标题块 `Spacing="6"` |
| `ConnectButtonMinWidth` | 196 | 176 | **0** | 现状 `MinWidth="196"`；Compact 交给整行宽 |
| `EmptyHintGap` | 8 | 7 | 6 | 现状空状态 `Spacing="8"` |
| `EmptyArtWidth` / `Height`（**未接线**） | 116 / 88 | 113 / 85.5 | 106.5 / 80.5 | 空状态插画，见「未做」 |
| `SharesSearchWidth`（**未接线**） | 230 | 200 | 150 | 共享搜索框，见「未做」 |
| `EffectiveWidthPx` / `HeightPx` | 诊断口径 = DIP × DpiScale，**不参与布局计算**（§9 口径分离） | | | |

### Canonical 基线不变的证据

把同一套公式在 PowerShell 里独立重写后复算：`width = 1424` 时 **57 项 Token 与现有 XAML 字面量逐项相等**
（20 / 20 / 12 / 276 / 18 / 16 / 82 / 64 / 18 / 34 / 46 / 34 / 16 / 12 / 15 / 13 / 29 / 12 / 14 / 44 / 24,12 / 54 / 16 / 10 / 10,4 / 16,11 / 22,9 / 18 / 42,8,14 / 15 / 6 / 12 / 10 / 6 / 196 / 8 / 8,14 / 320 / 80 / 170 / 116,88 / 230 / 1.00 等）。
结论：**Canonical 视口下本轮的 Token 不会改变任何既有视觉数值**，改动只发生在窗口离开 Canonical 之后。

---

## 三、DensityScale：最低值与待实测状态

- 曲线（全程连续，档位边界两侧取同值）：
  - `w ≥ 1424` → **1.00**
  - `1120 ≤ w < 1424` → 线性 `0.94 → 1.00`
  - `960 ≤ w < 1120` → 线性 `0.86 → 0.94`
  - `w < 960`（理论上被 `WM_GETMINMAXINFO` 挡住）→ 钳在 0.86
- **最低值 `MinimumDensityScale = 0.86` 是具名常量，注释明确标注「待实测确认」**：§6 允许 0.86 或 0.82，要求真机验证后决定。**本轮没有做真机视觉验证，因此 0.86 是占位值，不是实测结论**。验证之后只需改这一个常量（必要时下调到 0.82），Token 公式与所有 View 都不用动。
- 额外说明：DensityScale **不是** RenderTransform 缩放（§4 禁止），它是 `ReadOnly` 计算值，只用于驱动上表 Token。

### 一处**有意保留的结构性跳变**（需主控判断是否接受）

`LayoutMode` 切换处，档位基值本身是离散的，因此存在跳变，最大一处是 **底栏列距在 `w=1120` 由 11.5 跳到 21**（Wide/Normal 边界由 21.5 跳到 43）。
这与 §5「LayoutMode 决定结构性变化」一致，但确实是"突然跳变"。若判为不可接受，下一步可把档位基值也做成跨边界插值（本轮未做，因为会削弱"Wide = 现状"的锚定）。
`FooterSpacerWidth` 已做成**跨带连续**，不再有 80 DIP 的突降：960→0、1119→20、1120→20、1300→2、1319→0、1320→0、1400→61.5、1424→80。

---

## 四、§7 缩放优先级如何落实

不是靠"按顺序改代码"，而是把同一个 `DensityScale` 下降量**按权重分配**给不同 Token（权重写在 `ResponsiveLayout` 里，具名常量）：

| §7 顺序 | 对象 | 权重 | 效果（density 1.00 → 0.86 时该 Token 的收缩比例） |
|---|---|---|---|
| 1 | 外围 Margin（`PageMargin` / `WorkspacePadding` / `ShellBottomMargin`） | 1.00 | 收 14%（最狠） |
| 2 | 区域间 Gap（`CardGap` / `SectionGap` / `RailGap` / `HeaderGap` / `TitleSpacing` / `BottomBarGap` / `FieldRowSpacing` / `FieldColumnSpacing`） | 0.90 | 收 12.6% |
| 3 | Card Padding（`CardPadding` / `SecondaryCardPadding`） | 0.75 | 收 10.5% |
| 4 | 装饰图标与圆角（`IconSize` / `BadgeSize` / `CardCornerRadius`） | 0.60 | 收 8.4% |
| 5 | 部分按钮 / Input 高度（`ButtonHeight` / `InputHeight` / `FooterActionPadding`） | 0.45 | 收 6.3% |
| 6 | 局部布局方向 | 结构性（见 Step1 重排），不参与权重 | Compact 才触发 |
| 最后 | 字号（`FontSizeBody` / `Caption` / `Title`） | **0.25** | 只收 3.5%（几乎不动） |

- 结果一致性检查：density 0.86 时 → Margin 0.86、Gap 0.874、CardPadding 0.895、Icon 0.916、Control 0.937、**Font 0.965**。严格单调，**字号永远最后、最小幅度**。
- 另外还做了"少缩字号"的取舍：底栏**百分比 / 字节数 / 状态下文**（§7 点名保护）在 Compact 下字号**完全不动**；只有 Header 辅助描述与底栏四个动作按钮文字轻微缩小（13.5 / 11.5）。

---

## 五、Header / Bottom Status Bar / Step1 各改了什么

### §37 Header（`MainWindow.xaml` + `ShellResponsiveLayout.ApplyShellChrome`）

按 §37「先收紧 Gap → 再缩小辅助文字 → 最后才隐藏次要描述」的顺序：

1. **Gap 与内边距**：`AppTitleBar.Padding` 24,12 → 21,10 → 16,8；`HeaderIdentityStack.Spacing` 14 → 11.5 → 8.5；`HeaderTitleRow.Spacing` 10 → 8 → 6。
2. **装饰块**：品牌方块 54 → 50 → 44（含圆角 16 → 15 → 13，与边长联动，仍是圆角方块）；版本 Badge `Padding` 10,4 → 9.5,3.5、`CornerRadius` 12 → 11.5 → 9、并显式 `MinWidth=0`（防止系统默认最小宽把 Badge 撑成"按钮"）；Developer Tool 28×28 → 25×25 → 21×21（**缩尺寸而不是挪出可视区**）。
3. **源/目标胶囊**：`Padding` 16,11 → 14,9 → 12,8，`CornerRadius` 16 → 13.5 → 11；`SourceStatusText.MaxWidth` 170 → 140 → 110（源名不再把胶囊撑爆、不与目标压在一起）。
4. **辅助文字（第 2 步）**：`HeaderSubtitle`（"让电脑迁移更简单 · …"）字号 13 → 12.5 → 11.5。
5. **第 3 步（隐藏次要描述）没有做** —— 因为按上表推算的 Compact 预算下，收 Gap + 缩辅助文字已经够，**无需隐藏任何信息**。这比"先隐藏"更符合 §37 的优先级。
6. 标题不截断：产品名、版本 Badge、Developer Tool 三者的间距由 Token 驱动，压缩时有下限（Compact 6 DIP），不存在重叠；Developer Tool 仍在版本 Badge 之后（§21 的位置现状已满足）。
7. 竖向：`HeaderRow.Height` 82 → 76.5 → 67.5（Compact 时 44 的方块 + 8×2 内边距 = 60 ≤ 67.5，不裁）。

### §36 Bottom Status Bar（`ShellBottomBar` 分支）

1. `BottomBar.Padding` 22,9 → 18,8 → 13,7。
2. `BottomBarGrid.ColumnSpacing` 44 → 21 → 10.5。
3. 第 6 列隔离列（现状 `Width="80"`）按宽度**连续让位**：Canonical 80 → 1320 附近 0，优先保住右端四个动作按钮不被挤出窗口。
4. 进度轨道 320 → 200 → **140**（不消失）。
5. 四动作按钮：`Padding` 12,6 → 12,6 → 11,5.5；显式 `MinWidth=0`；`MinHeight` = `ButtonHeight`（34 → 30，下限保证"不压成不可用"）。
6. 按钮组 `Spacing` 12 → 10 → 8；两组速率占位 `Spacing` 6 → 5。
7. **没有隐藏任何内容**：百分比、字节数、进度、方向/速度两组占位、网络绿点、四个动作按钮在 Compact 下**全部保留**。§36 允许的"调整信息优先级（隐藏次要项）"这一步**未使用**，因为按预算 960 宽下仍有余量（估算余量约 60 DIP，见第八节的不确定性说明）。
8. 竖向：`BottomBarRow.Height` 64 → 59 → 52.5（Compact 内容高 30 + 7×2 = 44 ≤ 52.5）。

### §38 Step1 页面内部横向布局（`Step1ConnectPage` 分支）

1. 间距：`RootGrid.RowSpacing` 12 → 9.5 → 7；`FormGrid.RowSpacing` 8 → 7.5 → 7；`FormGrid.ColumnSpacing` 14 → 13.5 → 12；`FormCard.Padding` 12 → 10.5 → 9；共享卡 `Padding` 14 → 12.5 → 11；共享卡表头 `ColumnSpacing` 10 → 9 → 8；标题块 `Spacing` 6 → 6 → 4；标题行 `Spacing` 12 → 9.5 → 7。
2. 控件高度：三个输入 `MinHeight` 46 → 43 → 39.5；输入框内边距 `42,8,14,8` → `40,7,14,7` → `38,6,14,6`（密码框右侧保持 46 给「显示密码」让位）；前置图标字号 18 → 16.5 → 14.5、左边距 15 → 14 → 13。
3. **Compact 局部变两行（§38 核心）**：`FormGrid` 由 3 行扩为 5 行（运行时按需增删 `RowDefinition`，回到 Wide/Normal 会**精确还原**原 3 行与原始站位）：
   - 行 0：`IP 或电脑名` 标签 + 输入（跨 3 列）
   - 行 1：`用户名` 标签 + 输入（占整行宽）
   - 行 2：`密码` 标签 + 输入（占整行宽）
   - 行 3：测速开关 + 说明（跨 5 列）
   - 行 4：`连接并列出共享` 按钮（跨 5 列、`Stretch`、`MinWidth=0`）
   - 效果：Compact 下输入框不再被挤成 ~160 DIP 的窄条，按钮文字不会被裁，标签与输入分列不会重叠。
4. 页面标题 Badge：`34×34 / CornerRadius 17` → `27.5×27.5 / CornerRadius 13.75`（**半径恒为边长一半，任何档位都是正圆**，不裁数字）；页面标题字号 29 → 27.5 → 26；页面副标题 13 → 12.5 → 11.5。
5. 空状态提示块 `Spacing` 8 → 7 → 6。

---

## 六、§39 写死 `Height=` 的处理清单

扫描范围：`src\PCMig.WinUI\**\*.xaml`（排除 bin/obj）。

| 位置 | 原写死值 | 处理 | 理由 |
|---|---|---|---|
| `MainWindow.xaml` 行定义 | `Height="82"`（产品 Header 行） | 加名 `HeaderRow`，运行时由 `HeaderHeight` 驱动（82 / 76.5 / 67.5） | 窗口变矮时该行是纯装饰高度，写死会在 Compact 下白占 15 DIP |
| `MainWindow.xaml` 行定义 | `Height="64"`（底栏行） | 加名 `BottomBarRow`，运行时由 `BottomBarHeight` 驱动（64 / 59 / 52.5） | 同上 |
| `MainWindow.xaml` 品牌方块 | `Width/Height="54"` | 加名 `HeaderLogo`，运行时由 `HeaderLogoSize` 驱动（54 / 50 / 44） | 缩装饰块，§7 第 4 优先级 |
| `MainWindow.xaml` Developer Tool | `Width/Height="28"` | 沿用已有名 `DeveloperTuningButton`，运行时 28 / 25 / 21 | 防止被挤进系统 Caption Buttons 区域 |
| `MainWindow.xaml` 进度轨道容器 | `Width="320"` | 加名 `FooterProgressHost`，运行时 320 / 200 / 140 | Compact 下 320 会顶掉右侧按钮 |
| `MainWindow.xaml` 隔离列 | `Width="80"` | 运行时改列宽（**XAML 字面量保留**，契约测试要求） | 窄窗口让位给内容 |
| `Step1ConnectPage.xaml` 标题 Badge | `Width/Height="34"` | 加名 `PageBadge`，运行时 `BadgeSize` 驱动，半径 = 边长/2 | §11：必须始终正圆、数字不裁 |
| `Step1ConnectPage.xaml` 连接按钮 | `MinWidth="196"` | 加名 `ConnectButton`，运行时 196 / 176 / 0 | §38：Compact 不许"按钮文字被裁" |
| `Step1ConnectPage.xaml` 空状态插画 | `Width="116" Height="88"` | **未改** | 见「未做」 |
| `ProgressBar Height="12"` / 各 Ellipse·Path 装饰尺寸 / Vendor 图形 | —— | **保留** | 固定尺寸是对的（进度轨道厚度、矢量插画比例），改它们不会解决任何裁切 |
| 各 `Themes\*.xaml` 的 `MinHeight`（如 `PCMigTextBox` = 46） | —— | **保留**，实例级 `MinHeight` 覆盖 | 样式默认值仍是 Wide 档的正确值，且不能为了响应式去改全局样式（会影响 WPF 之外的其它面） |

**判断**：真正会因缩放导致裁切的是"结构性行高 + 会被挤出窗口的 Auto 列内容（进度轨道/隔离列）+ 装饰块尺寸"，这些已全部转为 Token 驱动。**没有发现"必须静态改成 MinHeight/Auto 才算修好"的写死 Height**：因为本项目的写死值都带明确意图（呼吸感、矢量比例），改成 Auto/Star 反而会破坏 Canonical 基线。这一步的结论是"改为运行时 Token 驱动"，不是"改成 Auto"。

---

## 七、Build / Test 实测输出

**本轮没有产出一份可用的 Build/Test 证据**，原因是主控正在用运行中的 PCMig.WinUI（PID 233600）做 Motion 取证，并明确要求本轮不 build、不 test。

唯一一次构建尝试（在我的代码只有一半时执行，且撞上 exe 文件锁）：

```
dotnet build src\PCMig.WinUI\PCMig.WinUI.csproj -c Release
  warning MSB3026: 无法将 obj\...\apphost.exe 复制到 bin\...\PCMig.WinUI.exe。
    文件被"PCMig.WinUI (233600)"锁定。  （重试 10 次）
  error   MSB3027 / MSB3021: 超出了重试计数 10。失败。
  10 个警告 / 2 个错误 / 已用时间 00:22.66
```

→ 这两个错误**全部是文件锁**，与代码无关，**不能作为"0 警告 0 错误"的证据**。真正的编译验证由主控统一执行。

已经做过的**替代性静态核对**（不依赖编译）：

1. `ResponsiveLayout` 成员引用核对：`ShellResponsiveLayout.cs` 引用的 **51** 个成员、`MainWindow.xaml.cs` 引用的 **5** 个（`Mode` / `SidebarWidth` / `RailGap` / `WorkspaceTopGap` / `PanelMaxHeight`）**全部存在**。主控先前报的 `HeaderTitleRowGap` / `HeaderLogoCornerRadius` 缺失**已补齐**（另外还漏了 `FooterSpacerWidth` 用到的私有辅助 `Ratio`，一并补上）。
2. XAML 完整性核对：`MainWindow.xaml`（15227 B / 40 个 `x:Name`）、`Step1ConnectPage.xaml`（13004 B / 26 个 `x:Name`）**XML 良构性检查通过**，两份文件都处于**完整状态**（不存在写到一半的元素；主控先前看到的 `WMC0610 XBF syntax error at (22,2625)` 是并发读中间态）。
3. Canonical 基线数值复算：见第二节，57 项全 MATCH。
4. 契约测试字面量保留核对：`ColumnSpacing="44"`、`<Grid Grid.Column="2" Width="320"`、`<ColumnDefinition Width="80"/>`、Logo Path Data、`<Window>` 无 `Width=/Height=` —— **全部原样保留**（`WinUiDpiContractTests.FooterRhythm_KeepsMeasuredValues` 等不会被我的 XAML 改动打红）。
   另：没有新增任何 `<TextBlock>`（`Typography_UsesNamedSemanticStyles` 的 95% 语义样式比例不受影响）。

---

## 八、未做 / 未验证项（诚实清单）

**没做：**

1. **`ShellResponsiveLayout.Apply(...)` 目前没有任何调用点**。我只加了 `ShellResponsiveLayout.cs`，**没有**改 `MainWindow.xaml.cs`（禁区）。所以现在这套 Token **不会被真实应用**，Header / 底栏 / Step1 的响应式行为**还没有生效**。需要主控补一行调用（见第九节）。
2. **3 个元素没有接上 Token**（因为主控在我加名到一半时要求停止改 XAML）：
   - 共享搜索框（`SharesSearchWidth` 230/200/150 已定义，未接）
   - 空状态插画尺寸（`EmptyArtWidth/Height` 116×88 → 106.5×80.5 已定义，未接）
   - 共享卡内容容器（未加名）
   → 后果：**Compact + 最小高度 640 时，Step1 空状态插画区可能仍偏紧**（插画 88 DIP + 三行文案），这是我认为**最可能残留的裁切风险点**。
3. **Step2 / Step3 / Step4 没有接 Token**：它们仍只有既有的 `ApplyLayoutMode(mode)`（双栏→单栏重排），没有走 DensityScale 驱动的 Token。这四个页面的"统一密度"没有完成。
4. **没有视觉验证**：零截图、零真机尺寸对比（§53 要求的同屏 Wide/Normal/Compact/Minimum 对比图**一张都没有**）。所有关于"够不够宽/会不会裁"的结论都是**算术估算**，不是实测。
5. **没有验证 DPI**（100/125/150/175/200%）。`DpiScale` 参数目前只在圆角吸附上生效，且默认 1.0 —— 如果主控调用时不传 `CurrentScale()`，圆角吸附等于没开。
6. **没有验证 §8 的比例矩阵**（16:9 / 16:10 / 3:2）。
7. **Minimum Window Size 没有动**（960×640 DIP 是既有值，`MinimumDensityScale = 0.86` 与它对齐；§9 要求"先完成 Compact 再定最小尺寸"，而 Compact 尚未真机验证，所以我没有改它，也不建议现在改）。

**不确定 / 可能不对：**

1. **底栏在 1120–1424 波段是否会裁**：按我的字符宽度估算，Canonical 1424 下底栏固定内容 + 8×44 间距 ≈ 1378 DIP，而可用宽 ≈ 1340 DIP —— 也就是**估算值本身是超的**。但现状截图是验收过的，说明我的字符宽度估算偏大；**我没有独立的真实测量**。我的改动在 ≥1424 与现状**逐字一致**（不引入回归），在 1320–1423 只做了"隔离列让位"的改善。**这一波段需要用真机截图确认**。
2. **Compact 下底栏"全部保留"是否真的放得下**：估算余量约 60 DIP（960 宽时）。若实际字符更宽，"不隐藏次要信息"的选择会导致右端裁切 —— 届时最省事的补救是隐藏两组 `--` 速率占位（§36 允许"调整信息优先级"），但我**没有**先做这个取舍，因为它是不可逆的信息损失。这个判断需要主控用截图定。
3. **运行时增删 `RowDefinition`（Step1 Compact 重排）没有真机跑过**：`Grid.RowDefinitions.Add/RemoveAt` + `Grid.SetRow/SetColumn` 是标准 API，但"反复在 Compact/Wide 之间拖动窗口是否会残留多余行或错位"**没有被验证**。逻辑上我做了幂等保护（`while Count < 5 Add` / `while Count > 3 RemoveAt`，且**先复位站位再删行**），但这是纸面推理。
4. **`FindName` 解析路径没有真机验证**：`ShellResponsiveLayout` 用 `root.FindName("…")` 按字符串取元素。我认为 `Window.Content`（根 Grid）的 namescope 能取到 `MainWindow.xaml` 里所有 `x:Name`，但**没有运行验证**。风险：如果某个名字解析失败，那一项**静默跳过**（不崩、不报错），表现为"某处没有响应式"。主控若发现"部分生效部分没生效"，第一嫌疑就是它。名字列表见第五节对应关系，全部 55 个名字已用脚本核对存在。
5. **字号在 Compact 下的实际观感**（标题 26、辅助文字 11.5、底栏按钮 13.5）没有看过，可能偏小，需要人眼定。
6. **`MinHeight = layout.ButtonHeight`（底栏按钮 34/30）**：我判断它等于现状自然高度（15px 字 + 6×2 内边距 + 2 边框 ≈ 34），所以 Wide 档不改变外观；但 WinUI 默认 `Button.MinHeight` 是否为 32 我没有确认，**若默认更小，Wide 档按钮会因此长 2–4 DIP**。
7. **档位边界的结构性跳变**（底栏列距 11.5 → 21 @1120）是否需要进一步平滑，见第三节末尾 —— 我没有自作主张去插值。

---

## 九、需要在 `MainWindow.xaml.cs` 里补的调用

只需**一行**（放在 `ApplyResponsiveLayout` 内、算出 `layout` 之后）。建议同时把窗口 DPI 传进去，让 `DpiScale` 真正生效（只影响圆角吸附）：

```csharp
private void ApplyResponsiveLayout(double width, double height)
{
    if (SidebarColumn is null || RailGapColumn is null || WorkspaceLayout is null) return;
    var layout = ResponsiveLayoutController.Calculate(width, height, CurrentScale()); // ← 加 CurrentScale()
    SidebarColumn.Width = new GridLength(layout.SidebarWidth);
    RailGapColumn.Width = new GridLength(layout.RailGap);
    WorkspaceLayout.Margin = new Thickness(0, layout.WorkspaceTopGap, 0, 0);
    TuningPanel.MaxHeight = layout.PanelMaxHeight;
    ChangelogPanel.MaxHeight = layout.PanelMaxHeight;
    ShellResponsiveLayout.Apply(this, layout);                                        // ← 新增这一行
    PageSelectData.ApplyLayoutMode(layout.Mode);
    PageProgress.ApplyLayoutMode(layout.Mode);
    PageResult.ApplyLayoutMode(layout.Mode);
}
```

- `Calculate(width, height)` 不传 DPI **也仍然可编译**（`dpiScale` 是可选参数，默认 1.0）——向后兼容已保证。
- 若主控希望先把 Step2/3/4 也接上，可在各页加 `ApplyResponsiveLayout(ResponsiveLayout)`，但**那属于新增工作**，本轮没做。
- 建议启用顺序：先只加这一行 → 真机拖窗口截图 → 若"整体生效但某处没动"，按第五节的名字对应关系查 `FindName` → 再决定是否调 `MinimumDensityScale`。

---

## 十、回退方式

- 本轮**没有**发版、**没有**改任何业务文件、**没有**改测试、**没有**执行破坏性 git。
- 回退到"响应式不生效"的等价状态：删掉第九节那一行调用即可（`ShellResponsiveLayout.cs` 变成惰性代码，XAML 里多出的 `x:Name` 无副作用）。
- 回退 Token 公式：只改 `Presentation\ResponsiveLayoutController.cs` 一个文件。