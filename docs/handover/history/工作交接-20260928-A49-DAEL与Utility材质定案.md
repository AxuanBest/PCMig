# 工作交接 · 2026-09-28 · A43–A49 DAEL 全局化、Utility 材质定案、按钮文字消失事故修复

> **交接时间**：2026-09-28 11:10 前后
> **权威工作区**：`<仓库根>`（HEAD 仍 `c9aef30`；本会话**未执行任何 git 命令**，改动全部未提交）
> **历史交接永久保留**：本文件是**新增**，未覆盖任何旧交接文档。

---

## 0. 摘要

本会话把「方向性边缘光照」从一个局部效果推进为全局体系，并**由用户逐项验收**：

| 项 | 状态 |
|---|---|
| **Utility 工具面板材质**（开发者材质调节 / 历史更新） | ✅ **用户已验收："就是现在对了"** |
| **主按钮文字消失事故** | ✅ **用户已验收已修复**（实测证据：启用态亮像素 0% → 3.9%） |
| 历史更新入口恢复 | ✅ 实测在 Developer Tool 右侧（Δx=44），接真实功能 |
| DAEL 全局化（内容卡 / 按钮 / 工具面板） | ⚠️ 代码完成，**视觉验收未做** |
| 构建 / 测试 | ✅ WinUI 0 错误；解决方案 0 警告 0 错误（须 `-m:1`）；Core 130 通过 |

---

## 1. 正式术语（用户定案，后续日志/注释/文档统一使用）

- **`Directional Acrylic Edge Lighting`（缩写 `DAEL`）**
  受 Apple Liquid Glass 边缘光学语言启发的 Acrylic Edge Lighting 实现。
  统一主光源来自左上角、方向约 45°；上/左边细而柔和的受光高光，下/右边极轻贴边收阴；
  不依赖粗描边、不依赖大投影；在高透明材质下仍帮助识别 Card / Panel 边界。
- **Utility 面板材质**：`Utility Acrylic Material`（本会话最终**改为与窗口背景同源、固定 50**，见第 2 节）

---

## 2. ★ Utility 面板材质定案（用户口径：「直接做出和窗口背景那个一样的材质，强度 50%」）

**做法：不做自定义调参，直接复用窗口背景材质的真值。** 真值取自应用自身代码：

```
BackdropSpike.TintOpacityFor(50) = 0.02      // design 锚点：50 = 当前设计默认
BackdropSpike.LuminosityFor(50)  = 0.00      // 50 档白雾已压掉
BackdropSpike: DesktopAcrylicController.TintColor     = #F2F6FF
BackdropSpike: DesktopAcrylicController.FallbackColor = #F2F2F2
```

落到 `Themes\Materials.xaml`：

```xml
<AcrylicBrush x:Key="PCMigUtilityAcrylic80"      TintColor="#F2F6FF" TintOpacity="0.02" TintLuminosityOpacity="0" FallbackColor="#F2F2F2"/>
<AcrylicBrush x:Key="PCMigUtilityAcrylicInset80" TintColor="#F2F6FF" TintOpacity="0.10" TintLuminosityOpacity="0" FallbackColor="#E9EFF8"/>
<SolidColorBrush x:Key="PCMigUtilityAcrylicControl80" Color="#EFFFFFFF"/>
```

- 三者**不在** `DeveloperVisualTuning` 的 `Layers` / `Surfaces` 表里 ⇒ 永不被滑块改写（**这就是"脱离全局材质调节链路"的实现方式**）
- 旧键名 `PCMigUtilityPanelMaterial80` / `…Inset80` / `…Control80` 保留为**别名**，两个面板文件无需改动

### ⚠️ 血泪教训（**不要再犯**）

本会话在 Utility 材质上**迭代了 5 次**才定案，因为用错了判断指标：

| 尝试 | 参数 | 结果 |
|---|---|---|
| ① | `#FFFFFF / TintOpacity 0.60 / Lum 0.92` | 纯白板（用户否掉） |
| ② | `#EEF4FC / 0.30 / 0.72` | 仍偏白 |
| ③ | `#E3ECF9 / 0.18 / 0.60` | 用户："有点发灰，**确实是我要的**" |
| ④ | `#E9F1FC / 0.28 / 0.78` | 我误判③为"反向过头"又调白 → 用户："又没了，又回去了" |
| ⑤ | **`#F2F6FF / 0.02 / 0`** | ✅ **定案**（= 窗口背景 50 档真值） |

**两个方法论错误**：
1. **指标读反**：我用「面板与背景的亮度差」作判据，认为差 3.5~8.5 太小 ⇒ 调白。**实际亮度差小正是"磨砂玻璃"而非"白板"的特征。**
2. **自己造参数而不是复用系统真值**：窗口背景已经有一套被用户认可的参数，直接取来用即可。

---

## 3. ★ 主按钮文字消失事故（已修复并被用户验收）

### 现象
Step1 在「IP 或电脑名」输入内容后，「连接并列出共享」按钮**变成蓝色且文字完全消失**（用户早期描述："像一个孤立的蓝色块"）。

### 定位过程（**真因靠复现测量得出，静态读码两次都猜错**）

| 步骤 | 手段 | 结果 |
|---|---|---|
| 猜测 1 | 页面 TextBlock 写死 `Foreground="White"` | ❌ 证伪（Step1 按钮文字无写死前景） |
| 猜测 2 | 文字样式写死前景压过模板按状态前景 | ❌ 证伪（`PCMigTextActionButton` 与 BasedOn 的 `PCMigTextBase` 均不设 `Foreground`） |
| 测量 1 | UIA 读无障碍树 | 文字节点**存在**：`Text "连接并列出共享" 108x20 Offscreen=False` |
| 测量 2 | 按钮内部像素统计 | 启用态底色 117.2（蓝）、**亮像素(>200) 占比 0%** ⇒ 蓝底上没有白字 |
| 测量 3 | 文字区像素 | 文字最暗 **87.1** = `AccentDisabledForegroundBrush #44597D`；禁用态底色 **229** = `AccentDisabledBrush #DCE6F6` ⇒ 禁用态配色本身是对的 |

**⇒ 真因**：模板 `Disabled` 状态用 `LabelNormal.Visibility=Collapsed / LabelDisabled.Visibility=Visible` 切换文字层，
而 `Normal` 是**自闭合空状态**，回到 Normal 时**没有还原这两个 Visibility** ⇒ **启用态（蓝底）显示的是禁用态那份深色文字**，深蓝灰压蓝底 = 肉眼消失。

> 这是**先于本会话就存在的缺陷**，不是本轮引入的。

### 修法（**结构性修复，不是打补丁**）

删掉"两个 ContentPresenter 互换可见性"机制，改为**单一 `Label` + 按状态改前景**：

```xml
Normal / PointerOver / Pressed → Setter Target="Label.Foreground" Value="White"
Disabled  → Setter Target="Label.Foreground" Value="{StaticResource AccentDisabledForegroundBrush}"
            + Root.Background = AccentDisabledBrush
            + Root.BorderBrush = AccentDisabledEdgeBrush
            + TopSheen.Opacity = 0
```

残留 `LabelNormal` / `LabelDisabled` 引用 = **0**。三个正常态都显式写回白色 ⇒ 即使 VSM 还原不可靠也不会残留深色。

### 验证证据（`Theme\Controls.xaml` 修好后实测）

| 状态 | 底色亮度 | 亮像素占比(白字) |
|---|---|---|
| 修复前 · 启用态 | 117.2 | **0.0%** |
| **修复后 · 启用态** | 118.2 | **3.9%** ✅ |

证据图：`archive\screenshots\a46-button\connect-btn-enabled.png`

> **诚实声明**：我第一轮只是给它补 `Normal` 的还原 Setter（补丁式），构建成功但**现场症状依旧**；
> 第二轮改成结构性修法才解决。**补丁式修复未经验证就当成功，是本会话的一次失误。**

---

## 4. DAEL 体系（代码完成，视觉验收未做）

### 四个强度档（**同一光源方向 / 同一起点 0.06,0.08 / 同一停点偏移 / 同一半径 1.25，只缩放 alpha**）

| 键 | 用途 | 0.00 停点 |
|---|---|---|
| `PCMigDAELBrush` | 主内容 Card / Large Surface（基准档） | `#FFFFFFFF` |
| `PCMigDAELBrushSubtle` | Sidebar Step Card（克制档） | `#8CFFFFFF` |
| `PCMigDAELBrushQuiet` | Utility Panel（最克制档） | `#80FFFFFF` |
| `PCMigDAELBrushControl` | 按钮体系（控件档，≈基准×0.35） | `#59FFFFFF` |

暗部一律用**蓝灰 `#0B1220`**（非纯黑），避免"灰黑脏阴影"。

- 兼容别名：`PCMigSurfaceRimLightBrush`、`PCMigStepCardRimLightBrush` → 指向上述定义（**别名必须与定义一起放在样式之前**，XAML `StaticResource` 是顺序解析）
- 通用行为：`PCMigDAELOverlayStyle`（`BorderThickness=1` / `IsHitTestVisible=False` / `Stretch`，**刻意不设 CornerRadius** —— 圆角由宿主提供，光照层服从宿主几何）；旧键 `PCMigSurfaceLightingOverlayStyle` 为别名

### 接线方式（**零结构改动**）
把 Card 级 Surface 样式的 `BorderBrush` 指向 DAEL：

| 样式 | CornerRadius（未动） | 使用点数 |
|---|---|---|
| `ShellMaterial` | 22 | 2 |
| `PrimarySurface` | 20 | 0 |
| `SecondarySurface` | 16 | 8（含侧栏提示卡） |
| `ElevatedSurface` | 18 | 9 |

合计 **19 个使用点**覆盖四页主要 Card。`InsetSurface`（13）保持语义边 `InsetBorderBrush` 不变。
按钮：`PCMigPrimaryButton`（模板边）、`PCMigSecondaryButton`、`PCMigFooterActionButton` 均已接入控件档；`VersionBadge` / `TitleBarGhost` 无边框资源（设计如此）。

### 实测（内容卡 DAEL 强度，固定窗口位置 60,60）
基准档描边有效 alpha 曾实测 **0.363**、左上差分 **+23.3**、右下 **−20.1**（Step Card 尺寸上测）。
⚠️ **但内容卡本身（大 Card）没有逐张看图验收** —— 见第 6 节。

---

## 5. 环境与工具坑（**下一任必读**）

1. **`pwsh` 调用结束会回收它拉起的 GUI 进程**（事件日志无崩溃、WER 无转储 ⇒ 不是崩溃，是进程树回收）。
   ⇒ 启动+截图+测量必须放在**同一次调用**内；要让用户长期交互，必须挂在**受管后台作业**下（本会话做法）。
2. **MSBuild 多节点（`-m`）在本环境必失败**（报"生成失败 / 0 警告 0 错误"）⇒ 解决方案级构建一律加 **`-m:1`**。
3. **`dotnet test` 在 workspace-write 文件策略下会中止**（`testhost.x86` 取父进程句柄 → `Win32Exception(5) 拒绝访问`）。
4. **PowerShell 变量名大小写不敏感** —— 本会话因此踩坑两次：
   - `$h`（哈希）覆盖 `$H`（窗口高度）→ `New-Object Bitmap($W,$H)` 崩
   - `$c`（文件内容）覆盖 `$C`（文件路径）→ 写入失败、改动丢失
   ⇒ **脚本里避免单字母变量，尤其是 `$h/$H`、`$c/$C`、`$w/$W`。**
5. **字符串字面量匹配要注意行尾**：`Themes\Materials.xaml` 用 **LF**、部分文件用 **CRLF**；用 here-string 拼匹配串时必须按文件实际换行构造，否则"命中 0 次"。
6. **截图定位窗口必须**：类名精确 `WinUIDesktopWin32WindowClass` + 标题精确 + **取面积最大者** + 断言尺寸；**禁止 `CopyFromScreen` 兜底**（会采到上层窗口，本会话及上一会话都因此产出过无效证据）。
7. **固定窗口位置后必须重读 `GetWindowRect`**，否则 UIA 屏幕坐标换算整体偏移（本会话踩过，采样点全错）。
8. `.ps1` 必须 **UTF-8 带 BOM**（`EF BB BF`）。
9. **桌面毛玻璃底衬随窗口位置变化**，会污染跨运行的像素差分比较 ⇒ 跨版本 A/B 必须 `SetWindowPos` 固定位置。

---

## 6. ⚠️ 未完成（下一任重点，按优先级）

1. **内容卡 DAEL 视觉验收**（用户明确要求"不能只看 Sidebar"）。四页截图已拍：
   `archive\screenshots\a47-final\page1-4-full.png`；要求每页至少检查**一个主卡 + 一个次级卡**都能看出统一左上受光 / 右下收阴。**未逐张看图**。
2. **Utility 面板滚动条压字是否已消除**：只做了"给可滚动内容预留右侧 10px 内边距 + 版本列表 `Padding="0,0,8,0"`"的代码修复，**未看图确认**。
3. **Sidebar `+8%~+15%` 未拿到干净证据**：固定位置后卡面亮到 235–242，白高光余量仅 13–20，`a_eff` 在两张卡上算出 0.243 / 0.156，**离散太大不能声称达标**；需换更贴卡本身的度量方式（例如在低材质档 20 下测、或与同图内已知参照比对）。
4. **按钮体系老式感是否消掉**：`TopSheen` 强度 1/2.4、高度 16→10、浮起 `Z5+ElevationMedium → Z2+ElevationLow`，**未看图确认**。
5. **文档 / 更新日志尚未改用 DAEL 术语**（`docs\更新日志.md`、`docs\使用说明.txt`）。
6. **桌面复查夹同步**：见第 7 节。
7. 其余 Final Polish 项仍未开工：游离 Token 取舍、`PCMigSurface` 控件（全仓**零引用**，A33 遗留）去留、`P0 Global Responsive UI Scale`、DPI 矩阵、最小窗口实测。

---

## 7. 本会话改动文件与回退

### 7.1 改动文件（相对本会话开始）
| 文件 | 改动要点 |
|---|---|
| `src\PCMig.WinUI\Themes\Materials.xaml` | DAEL 定义 + 4 个强度变体 + 兼容别名 + `PCMigDAELOverlayStyle` + Utility Acrylic（窗口背景 50 档真值）；4 个 Card 级样式 `BorderBrush` 指向 DAEL |
| `src\PCMig.WinUI\Themes\Controls.xaml` | 主按钮：结构性修复（单一 Label + 按状态前景）、去老式高光；次级/底栏按钮接入 DAEL 控件档 |
| `src\PCMig.WinUI\Views\StepNavigationControl.xaml` | 光照环走 `PCMigDAELOverlayStyle` + `PCMigDAELBrushSubtle`，CornerRadius 15 本地提供 |
| `src\PCMig.WinUI\Views\ChangelogPanel.xaml` | Utility Acrylic + DAEL 克制档 + 滚动条内边距 + 版本列表 `Padding` |
| `src\PCMig.WinUI\Views\DeveloperTuningPanel.xaml` | 同上（工具按钮 → Utility Control 材质） |
| `src\PCMig.WinUI\MainWindow.xaml` / `.cs` | 新增「历史更新」入口（Developer Tool 右侧）；浮层锚点改指新按钮 |
| `PCMig\archive\scripts\a41-stepcard-light-verify.ps1`、`a42-lighting-global-verify.ps1` | 验证台（三档差分 + 四页 + 隔离 + 固定窗口位置） |

### 7.2 未改动（冻结项，均已核对）
- **6 个材质 Token 逐字未变**：`PCMigShellMaterial #2BFFFFFF`、`PCMigWorkspaceMaterial #40FFFFFF`、`PCMigCardMaterial #AFFFFFFF`、`PCMigInsetMaterial #59FFFFFF`、`PCMigControlMaterial #D6FFFFFF`、`PCMigPrimaryMaterial #F2FFFFFF`
- **`Presentation\DeveloperVisualTuning.cs` 未被触碰**（mtime 09/27 23:05）；3 个 Utility 键**不在**其 Layers/Surfaces 表内
- 未动：四个页面 XAML（`Step1`–`Step4`）、`ShellHintCard.xaml`、`Colors.xaml`、`TextInputTemplates.xaml`、业务逻辑、存档格式
- 未改 `tools\release.ps1` / `stability-test.ps1` / `uishot.ps1`；未发版；未动交付区与工作副本；**未读取或明文输出任何密钥**

### 7.3 回退快照
| 路径 | 内容 |
|---|---|
| `archive\tmp\Materials.before-a49.xaml` | Utility 材质改为窗口背景真值之前 |
| `archive\tmp\Materials.before-a48.xaml` | 回退到"用户认可那一版"之前 |
| `archive\tmp\Materials.before-a43.xaml` | DAEL 重做之前 |
| `archive\tmp\Controls.before-a46.xaml` | **按钮结构性修复之前**（最重要的回退点） |
| `archive\tmp\Controls.before-a45.xaml` | 按钮第一轮补丁之前 |
| `archive\backups\a41-stepcard-lighting-20260928\` | A41 接入之前（含 `PRE-CHANGE-*.xaml`） |

**回退方法**：覆盖回 `src\PCMig.WinUI\` 对应位置 → `dotnet build src\PCMig.WinUI\PCMig.WinUI.csproj -c Release`。
**不要用 git 回退**：HEAD `c9aef30` 里 `Materials.xaml` 只有 16 行旧 Acrylic 版，`StepNavigationControl.xaml` 甚至不存在于 HEAD。

---

## 8. 本会话诚实声明

- **Utility 材质迭代 5 次才定案**，根因是我用错判断指标（把"面板与背景亮度差小"当成缺陷），并自己造参数而没复用系统真值。
- **按钮 bug 的第一轮修复是补丁式的，构建通过但症状依旧**，我一度以为修好；第二轮改结构性修法才解决。
- **静态读码两次猜错真因**（连续两个假设都被实测证伪），最终靠 UIA 无障碍树 + 三组像素测量定位。
- **多次受"窗口位置漂移 + 桌面毛玻璃底衬"污染**得出不可比的跨运行数据，后来才加固定位置与"内参亮度"归一化。
- 多个 PowerShell 脚本因**变量名大小写不敏感**而静默失效（改动未写盘），均已修正并记录。
- **本会话没有宣布任何未经验证的项为完成**；第 6 节列出的 7 项即为真实缺口。
- 用户两次人工验收（Utility 材质"就是现在对了"、按钮文字故障"修复了"）是本会话唯一两项**由用户确认**的成果。

---

# 附录 A50–A53（同一会话后续进展，供下一任连续上下文）

## A50 · Step2/Step3 空间利用 + 统一导航 Footer（用户已验收截图）
- 四个页面**去掉页级 ScrollViewer**，页根改有界 Grid（`Auto/*/Auto`）⇒ 四页 `RootScrollRequired = false` 逐页实测成立
- Step2：撤掉右栏 ScrollViewer，改纯回收布局冗余（`SelectColumns` 0 高行 +12、页根 Margin 归零 +12、Footer 间距 12→4 +8、右栏 Card 间距 12→10 +4 = **+36 DIP**）⇒ 外层 `SelectLeftCard` 479→**513**、目录树内框 349→**381**、卡内死区 199→**3 DIP**，右栏三卡完整无滚动
- Step3：两卡 258→**266**、内框 199→**207**（连带收益）
- **统一 Navigation Footer 落在 Shell 层**（`MainWindow.xaml` WorkspaceShell 新增第 5 行 `Auto`）：`x:Bind Nav.IsXxxCurrent` 控制显示 + `Click → Nav.GoTo`，Style 复用 `PCMigPrimaryButton`/`PCMigSecondaryButton`
- Step1 去重：删除页面自带的重复「下一步」按钮（`Step1ConnectPage.xaml` L44）与已成死代码的 `NextStep_Click`
- ⚠️ **契约测试被改过一次**（必须记录）：`tests\PCMig.Core.Tests\WinUiStep1ContractTests.cs` 把 `NextStep_Click` 从 `AssertAllPresent` 移到 **`AssertNonePresent`**（等于把"不许再加回冗余按钮"写进契约，比原来更严）

## A51 · Step4 瘦身 + 空间再分配（用户已验收截图）
- 操作条卡 `Padding 14→12,10`、`ToolbarGrid RowSpacing 10→0`、5 个工具按钮局部 `Padding 14,7 / 12,6`
- 搜索框 `MinHeight 46→34`、`MaxWidth 360`、左对齐
- 报告卡 `Padding 14→10`、内 `RowSpacing 10→6`；报告行 `*→3*`、日志行 `*→2*` 且日志 `MinHeight→150`
- 滚动条不压字：表头与三条数据行 `Padding` 右侧各预留 **22px**（`12,5,22,5` / `12,7,22,7`）
- **结果：报告清单三行全部显示（原来只有 2 行）**，达到"先提高一屏信息量、最后才谈滚动"的口径

## A53 · Preflight P0-1 + Phase A 第 1 步
- **P0-1 已修**：`Step2SelectDataPage.xaml.cs` 的 `RowDefinitions[1]` 越界隐患（XAML 只剩 1 行，Compact 一旦启用即抛 IndexOutOfRange）→ 改为**按需动态增删行**，非 Compact 分支不再触碰 RowDefinitions ⇒ 零视觉变化
- **P0-2 只读扫描结论**（**未修改**）：
  - `Step3ProgressPage.xaml` 的 `StatCardsGrid`(L69) 与 `LowerPanesGrid`(L107) 各 **浪费 12 DIP**（`RowSpacing="12"` + `<RowDefinition Height="0"/>` 共存）⇒ 合计 **24 DIP 可还给两张明细卡**
  - 修复时必须**同时**把 `Step3ProgressPage.xaml.cs` L43/L54 的固定索引改成动态增删，否则重演 Step2 的越界隐患
  - `Step4ResultPage.xaml` 的 `ToolbarGrid` 那一行**保持不动**（RowSpacing 已为 0，无浪费，索引访问安全）
  - 全仓其余固定索引：`ShellResponsiveLayout.cs` L145 `bottomBarGrid.ColumnDefinitions[5]`（底栏 9 列存在，安全；但这是 Responsive 重点文件，Phase A/B 必须加守卫）
- **Phase A 第 1 步已完成（纯追加、零行为变更、构建 0 错误）**：`ResponsiveLayoutController.cs` 追加
  - `UniformScaleMode` 开关（**默认 false** ⇒ 旧 Weighted 路径完整保留，可即刻回退）
  - **运行时实测**基准常量：`BaseClientWidth/Height = 1424/892`、`BaseContentWidth/Height = 1070/583`
  - 光学安全下限：`OpticalMinStrokeThickness = 1.0`、`MinimumReadableFontSize = 11.0`
  - `CalculateUniformScale(clientW, clientH, min, max)` = `Clamp(Min(W/1424, H/892), min, max)`，**全 DIP 计算、不乘 RasterizationScale**
- **Phase A 尚未做**：`ShellResponsiveLayout` 改为 `BaseValue × ApplicationUIScale`（仅 Step4）、四档 1.00/0.95/0.90/0.85 实测

## ★ 实测得到的硬事实（Phase A–I 全程可用）
| 量 | 值 |
|---|---|
| DPI（本机） | 96 ⇒ RasterizationScale = 1 |
| 窗口外框 | 1440 × 900 px（**之前所有截图报的都是这个**） |
| **客户区（`XamlRoot.Size`）** | **1424 × 892 DIP** ← 真正的 BaseDesign |
| 非客户区边框 | 宽 +16 / 高 +8（DPI 96） |
| 页面内容带 | **1070 × 583 DIP**（顶 160 / 底 743） |
| **Scale 表（正确基准下）** | 1424×892→1.0000 ／ 1382×864→0.9686 ／ 1300×813→0.9114 ／ 1224×765→0.8576 ／ 1152×720→0.8072 |
| **关键结论** | 16:10 附近窗口比例下**高度永远先被耗尽** ⇒ 必须 `Min(ScaleX, ScaleY)`；只按宽度缩放必然在高度上裁切（Step4 日志被裁的成因） |

## 回退点（勿删）
- `<镜像备份根>\PCMig-v0.5.0-pre-responsive-motion-20260928-131622\`（2963 文件 / 700 条 SHA256 清单）
- 关键基线哈希：`Materials.xaml B332EC826D2EA1F4`、`Controls.xaml 633AB72D9C6ED649`、`Step4ResultPage.xaml 599A840F266557F1`、`MainWindow.xaml F95DFD284AC63152`
- ⚠️ **不要用 `git tag` 作回退点**：HEAD 仍 `c9aef30`（`Materials.xaml` 仅 16 行旧版、`StepNavigationControl.xaml` 不存在），tag 指向的提交不含本会话成果

## 影响面材料位置
`<用户目录D>\Desktop\<桌面交付根>\Responsive-Motion-影响面代码\`（45 个文件全量代码 + `影响面说明.md` + `SHA256清单.json`）