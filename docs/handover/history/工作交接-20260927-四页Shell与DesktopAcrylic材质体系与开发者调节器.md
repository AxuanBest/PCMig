# 工作交接 · 20260927 · 四页 Shell + Desktop Acrylic 材质体系 + 开发者视觉调节器

> 本文件为**新增**交接文档（不覆盖任何历史交接）。
> 会话时间：2026-09-26 ~ 2026-09-27 ｜ 分支 `feature/winui-v0.5.0` ｜ HEAD `c9aef30`（**本会话全部改动未提交**）
> 权威工作区：`<仓库根>` ｜ 交付区：`<交付区>` ｜ 工作副本：`<发版工作副本>`
> **本会话未发版、未触碰交付区与工作副本、未执行任何破坏性 git 命令。**

---

## 一、本会话的目标演进（用户逐轮下达的优先级）

| 阶段 | 用户目标 | 结果 |
|---|---|---|
| 1 | Step 1 视觉保真收口（V10/V11/V9/A22/A23/V12） | 进行中被用户**主动冻结** |
| 2 | **Step 1 局部抛光全部冻结进 Final Polish backlog**；解冻 WinUI UI/Presentation；做 **Shared Shell + 四页真实导航** | ✅ 完成 |
| 3 | **State Integration Gate**（PageReadiness 真接线）+ **Stability Gate**（COMException 定位） | ✅ 完成 |
| 4 | **Layered Transparency Technical Spike**（Desktop Acrylic vs Mica；六场景；移动测试） | ✅ 机制确认（数据有部分作废，见 §五） |
| 5 | **Layered Translucent Material Hierarchy 正式实现**（Round 1 底层再透一点 → Round 2 上层逐级增实） | Round 1 ✅ 锁定；Round 2 第一批 ✅；后续批次待做 |
| 6 | **改变策略：不再靠截图猜透明度** → 实现 **Developer Visual Tuning 实时调节器**，由用户亲手标定 | ✅ 完成并验证 |

---

## 二、本会话完成的事（含证据）

### 2.1 Shared Shell + 四页真实导航（Stage13）
- `MainWindow.xaml` 从 30KB 单文件降为 **Shell 装配层**（约 15KB）：Ambient / TitleBar / ProductHeader / 侧栏容器 / **ContentHost（四页共存）** / BottomStatus。
- 新增 `Views\StepNavigationControl.xaml(.cs)`（侧栏四卡，可点）、`Views\ShellHintCard.xaml(.cs)`；Step1 从 MainWindow 原样搬入 `Views\Step1ConnectPage.xaml`。
- 新增 `Presentation\StepNavigation.cs`：四步身份/图标/标题/选中态与**已访问态**的唯一数据源。
- 导航：侧栏点击 + **WinUI 官方 `KeyboardAccelerator`**（Ctrl+1..4 / Ctrl+Tab / Ctrl+Shift+Tab）+ 上一步/下一步。
- **导航可用性与业务可用性分离**：UIA 实测四个侧栏按钮在未连接/无数据时 **全部 `enabled=True`**，而业务按钮为 `False`。
- 证据：`archive\step1-fidelity-20260926-r2\stage13\evidence\`（四页截图 + OCR + 边界报告）。

### 2.2 State Integration（PageReadiness 真接线）
- 新增 `Presentation\PageReadiness.cs`：把既有 `ConnectionViewModel` 投影成四页共用状态文案（**纯投影、零业务规则**）。
- **接线机制经过两次失败才确定**：
  1. `{x:Bind State.X}`（嵌套路径）→ **渲染为空**（State 在 InitializeComponent 时为 null，之后不重算）；
  2. 经典 `{Binding X}` + DataContext → 本页**同样渲染为空**；
  3. **最终方案：命名元素 + 代码直推 + `PropertyChanged` 订阅**（`ApplyState(PageReadiness)`）。
- **实测证据（截图 OCR 逐字）**：Step2 `请连接旧电脑后加载目录` / `尚未连接旧电脑` / `连接成功后将在这里展示…`；Step3 `尚未开始迁移（需要先 Step 1 连接与 Step 2 选择）。`；Step4 `尚无校验结果（需要先完成迁移）。`
- 顺带修掉一个**启动即崩**：`Readiness` 必须在 `InitializeComponent` **之前**构造，否则传 null 在页面 `ApplyState` 里抛 `NullReferenceException`。

### 2.3 Stability Gate：COMException `0x80040111` 根因定位并修复
异常：`Windows.ApplicationModel.LimitedAccessFeatures`（退出码 `0xC000027B`），**修复前四页自动化抓图每次必崩 1 次**。

**实验链（同一构建、同一操作、正常 Alt+F4 关闭，看退出码与崩溃增量）**：

| 实验 | 结果 |
|---|---|
| 不切页 + 关闭 / 切 Step2 / 切 Step3 + 关闭 | 退出码 0，**0 崩溃** |
| **切 Step4 + 关闭** | **0xC000027B，1 崩溃** |
| 访问过 Step4 → 回 Step1/Step3 → 关闭 | **仍崩** |
| Step4 内容全 Collapsed / 只留标题 / CheckBox 换掉 | **仍崩** |
| **按 Ctrl+4 但 Step4 页根本不显示** | **仍崩** ⇒ 与页面内容无关 |
| **去掉「把『下一步』按钮置灰」这一行** | **退出码 0，0 崩溃** ✅ |

**根因**：运行期把带自定义 `ControlTemplate`（含 `ThemeShadow` + `Translation` 的 Disabled VisualState）的主按钮 `IsEnabled=false`，会在**窗口关闭时**触发 WinRT 类激活失败。
**修法**：Step4 是最后一步 → **隐藏**「下一步」而不是置灰（`NextStepButton.Visibility = Current == Result ? Collapsed : Visible`）。
**验证**：修复后四页自动化抓图 **werDelta = 0**（此前每次必为 1）。

### 2.4 契约测试"字符串存在却报缺"—— 根因是**我自己的断言 token 多带一个结尾引号**
文件里是 `IsOn="{Binding BenchmarkOnConnect, Mode=TwoWay}"`（名字后是逗号），而断言写成 `"{Binding BenchmarkOnConnect"`（带结尾引号）→ 永远断言失败。用"裸词对照实验"证明测试读取路径/编码/文件版本全部正常。已修正 → **130/130**。

### 2.5 Desktop Acrylic 技术路线（**用户已人眼确认方向正确**）
**API 审计（编译器 + 运行时反射双实证，WindowsAppSDK 2.5.1 / Microsoft.WinUI 3.0.0.2609）**：
- `Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop` **存在但没有任何可调参数**（TintOpacity/LuminosityOpacity/TintColor/FallbackColor 全不存在，CS1061 实证）；
- 可调参数**只在 `DesktopAcrylicController`**：`TintOpacity`(float) / `LuminosityOpacity`(float) / `TintColor` / `FallbackColor` / `Kind` / `State` / `IsSupported()`；
- 挂载方式 = `AddSystemBackdropTarget(ICompositionSupportsSystemBackdrop)`；`SetTarget(Window, config)` **不存在**（只有 `(WindowId, CompositionTarget)` 旧签名）；
- **`SetSystemBackdropConfiguration(config)` 是必需调用** —— 不调用 → `State` 恒为 **Fallback**，界面显示 `FallbackColor`（近白）**伪装成"透明生效"**。这是此前所有"越调越白"的真正原因；
- 本机系统默认：`TintOpacity=0`、**`LuminosityOpacity=0.85`**、`TintColor=#FCFCFC`、`FallbackColor=#F9F9F9`（白雾来源）；
- **窗口失焦会让控制器掉回 Fallback**（实测 `Active`(1.5s) → `Fallback`(4s)）。

**有效测量（同一窗口/页面/截图方式）**：

| 场景（PCMig 后方真实内容） | Desktop Acrylic Shell | Hue | **Mica 基线** |
|---|---|---|---|
| light 白 | `#FFFFFF` | 0 | `#CDDDEB` |
| dark 近黑 | `#111315`（lum 0.075） | 210 | `#CDDDEB` |
| warm 橙红 | `#E15520` | 16.5 | `#CDDDEB` |
| cool 蓝青 | `#1373D4` | 210.0 | `#CDDDEB` |
| purple 紫 | `#7A20D0` | 270.7 | `#CDDDEB` |
| 移动测试（背景左→右） | `#E15520` → `#1273D4` | 16.5→210 | `#CDDDEB` 恒定 |

**结论**：Desktop Acrylic **真实继承后方内容的明暗与色相**；**Mica 在全部场景给出同一个 `#CDDDEB`，完全不随后方窗口变化** → Mica 降级为回退路径（实现保留）。

### 2.6 Material 层级体系（Round 1 + Round 2 第一批）
- **Round 1（底层校准，已锁定）**：四候选小范围搜索 → 锁定 **`TintOpacity 0.02 / LuminosityOpacity 0.00`**。
  关键发现：四档参数只差 1~2 个色阶、桌面背景下**完全一致** → **白雾主因不在最底层**。
- **Round 2 第一批**：识别出根因 —— 上层原先**全部是应用内 `AcrylicBrush`**，而**应用内 AcrylicBrush 采样的是桌面壁纸、不是窗口自己的 backdrop** → 底层再透上层依旧乳白。
  改为**语义 Token（四页共用一套，集中 `Themes\Materials.xaml`）**：

| Token | 值 | 层 |
|---|---|---|
| `PCMigShellMaterial` | `#2BFFFFFF` | L2 Shell |
| `PCMigWorkspaceMaterial` | `#40FFFFFF` | L2 Header/Sidebar/Workspace |
| `PCMigCardMaterial` | `#AFFFFFFF` | L3 Card |
| `PCMigInsetMaterial` | `#59FFFFFF` | 卡内嵌入 |
| `PCMigControlMaterial` | `#D6FFFFFF` | L4 Input/Search/次级按钮 |
| `PCMigPrimaryMaterial` | `#F2FFFFFF` | L5 Primary |
| `PCMigSemanticDarkMaterial` | `#F21A2033` | L-S Step4 深色日志（不随透明体系变浅） |

  旧键名（`ShellMaterialBrush`/`PrimarySurfaceBrush`/`SecondarySurfaceBrush`/`ElevatedSurfaceBrush`/`InsetSurfaceBrush`）保留为 `<StaticResource>` 别名 → 四页既有引用零改动（运行时可用，已实测）。

### 2.7 Developer Visual Tuning（本会话最后一件，已完成并验证）
- 入口：标题栏右侧弹性空白列（`Grid.Column="3"`，紧邻系统按钮预留列左侧）加入 **28×28 小图标按钮**（`&#xE9E9;`，透明底无边框）——**不占系统按钮预留列、不动窗口控制按钮**。
- 面板：**不新开窗口**，主窗口内右侧浮动浮层（宽 348，用现有 Material Token 作底）。
- 7 条**连续** Slider（0–100，`StepFrequency=1`）：Global / Backdrop / Shell·Workspace / Card / Inset / Control / Primary。**拖动立即生效，无需重启**。
- **映射曲线（50 = 当前设计默认，不是 50% 不透明度）**：每层三锚点 `0→alpha0`、`50→该层设计 alpha`、`100→alpha255`，中间连续分段线性；`Global` 为二次曲线（50 时不改变任何层）。
- **Backdrop Slider = 感知材质强度**（Desktop Acrylic 无可调 BlurRadius）：内部联合映射 `TintOpacity`/`LuminosityOpacity`/`FallbackColor`（0→0/0；50→0.02/0.00；100→0.55/0.45）。
- **实现硬约束**：只改**已存在画刷实例的 `Color` alpha**（替换资源对象不会刷新已解析的 `StaticResource`）；**不引入嵌套 Acrylic**；**不用 `UIElement.Opacity`**；持久化只写 `%LocalAppData%\PCMig\DeveloperVisualSettings.json`（与业务配置完全隔离），导出 JSON 内含**解析后的真实 token 值**。
- **验证（真实鼠标/键盘，未用会崩的 UIA 注入）**：入口 `28×28` 点击成功 → 面板内 **7 个 Slider** → Shell/Workspace 初值 **50** → 真实点击轨道 95% → 值 **98** → 卡片区 `#F2F0F2`(50) → **`#FBFBFC`(98)** → `#EFEDEF`(点回 50)。

---

## 三、改动文件清单（32 个，完整逐文件 SHA256 见桌面包）

```
src\PCMig.WinUI\MainWindow.xaml                 Shell 装配 + 开发者入口按钮 + 调节浮层
src\PCMig.WinUI\MainWindow.xaml.cs              Shell 装配 + 导航 + 入口事件 + Readiness 构造顺序修复
src\PCMig.WinUI\App.xaml / App.xaml.cs          PCMIG_DIAG 门控的 first-chance 诊断钩子（仅诊断用）
src\PCMig.WinUI\app.manifest                    交接前既存改动
src\PCMig.WinUI\Presentation\StepNavigation.cs  四步导航唯一数据源（新增）
src\PCMig.WinUI\Presentation\PageReadiness.cs   四页共享状态投影（新增）
src\PCMig.WinUI\Presentation\BackdropSpike.cs   Desktop Acrylic 安装 + 感知强度映射（新增）
src\PCMig.WinUI\Presentation\DeveloperVisualTuning.cs  调参数据/映射曲线/持久化/导出（新增）
src\PCMig.WinUI\Presentation\ConnectionViewModel.cs / ObservableObject.cs / ShareItem.cs  既有（列出对照）
src\PCMig.WinUI\Views\DeveloperTuningPanel.xaml(.cs)   调节面板（新增）
src\PCMig.WinUI\Views\StepNavigationControl.xaml(.cs)  侧栏导航组件（新增）
src\PCMig.WinUI\Views\ShellHintCard.xaml(.cs)          侧栏提示卡组件（新增）
src\PCMig.WinUI\Views\Step1ConnectPage.xaml(.cs)       Step1（从 MainWindow 搬入）
src\PCMig.WinUI\Views\Step2SelectDataPage.xaml(.cs)    Step2（新增）
src\PCMig.WinUI\Views\Step3ProgressPage.xaml(.cs)      Step3（新增）
src\PCMig.WinUI\Views\Step4ResultPage.xaml(.cs)        Step4（新增）
src\PCMig.WinUI\Themes\Colors.xaml / Controls.xaml / Typography.xaml
src\PCMig.WinUI\Themes\Materials.xaml                  语义 Material Token（Round 2 第一批）
tests\PCMig.Core.Tests\WinUiDpiContractTests.cs        视口/DPI 契约（新增 15 条）
tests\PCMig.Core.Tests\WinUiStep1ContractTests.cs      按新架构更新断言
```

**冻结范围核算**（Core/Robocopy/SMB/Scan/Planner/Verify/Repair/Resume/Migration Engine/CLI/WPF）：
`checked=104 / match=103 / missing=0 / mismatch=1（WinUiStep1ContractTests.cs，测试文件按新架构更新）/ extra=3（3 个新增文件）` → **冻结范围内产品代码 0 改动**。

---

## 四、当前验证状态

| 项 | 结果 |
|---|---|
| Build | **0 错误** |
| Core Tests | **130 / 130**（原 115 + 新增 15 条视口/DPI 契约） |
| 四页抓图 | **4/4**，**werDelta = 0**（COMException 修复后一直为 0） |
| 窗口视口 | Canonical 1424×891 DIP；**当前机器显示缩放已变为 100%**（截图 1426×893，此前 125% 为 1782×1116） |
| 导航 | 四页可切换；侧栏四按钮在任何业务状态下 `enabled=True` |
| TwoWay 输入 | 真实键盘输入后 ViewModel 属性逐字符变更（`Host=[192.168…]`、`CanConnect` 翻转）——**直接证据** |
| 冻结边界 | 见 §三 |
| 进程纪律 | 每轮结束复查 `PCMig = 0 / 背景窗口 = 0` |

---

## 五、已知限制 / 未解决项（**如实列出，勿当已完成**）

1. **逐层阶梯测量数据作废**：`stage18\evidence\layer-ladder.json` 显示四页 Shell 全 `#FFFFFF/#FDFDFF`（近白），已定位三个原因：
   - 我的 **State 证据有漏洞**：控制器 State 只在启动后 1.5s/4s 各记录一次，**不能证明"抓图那一刻"是 Active**；
   - 本轮的阶梯测量**漏了"背景窗口确实在 PCMig 后方"的全屏取样验证**；
   - 早期取样区打偏（落在提示卡上），后续修正后仍近白 → 说明抓到的很可能不是透明后的画面。
   **必须重测**（方法：抓图前直接读 `controller.State`，不读日志；同时全屏取样证明背景可见）。
2. **Step2 空态文案在截图里看不到**（同结构 Step3 的空态文字正常）→ 判断为视口下方（below the fold），**需人眼确认**。
3. **侧栏选中卡左缘蓝色竖条未确认渲染**（已进 Final Polish backlog）。
4. **`PCMIG_ACRYLIC_ALWAYS_ACTIVE`**：正式实现**跟随真实激活状态**（符合 Windows 语义）；仅自动化截图时钉住并标注【实验态】。**不要把它当作正式产品的默认行为。**
5. **未做的阶段**：Card/Control/Primary 三批层级验证、Reference Environment Proxy 保真、Final Polish backlog（1px 高光 / spacing / 插画细节 / 局部阴影 / 字号微调 / 侧栏竖条）。
6. 本会话踩过的坑已固化进脚本注释与本文 §七，但**未全部沉淀成项目级文档**。

---

## 六、当前规划方案（下一步）

**立即（用户当前要求）**：用户亲手用 Developer Visual Tuning 拖动 7 个参数完成视觉标定 → 通过「复制参数」/「导出 JSON」把数值交回 → **由主模型固化回 `Themes\Materials.xaml` 与 Backdrop 默认值**（这一步才算把标定结果落地）。

**随后（Round 2 余下批次，每批必看四页）**：
1. 修好 State 实时读取 + 背景可见性验证 → **重跑逐层阶梯测量**；
2. 验证 **Card（Layer 3）** 批次；
3. 验证 **Control / Search / Secondary（Layer 4）** 批次；
4. 验证 **Primary / Important（Layer 5）+ Semantic Dark（Step4 日志）** 批次；
5. 四页统一复查 + 大阶段刷新桌面包。

**再之后**：
6. **Reference Environment Proxy**：按正式参考图窗口**外围可见环境**制作测试代理背景（**只能作为 PCMig 外部的测试环境，禁止塞进 PCMig 内部当背景**），用于 Reference Fidelity 判断；
7. 最后才打开 **Final Polish backlog**。

**长期不变的目标**：底层高度透明并真实继承后方环境；上层 Shell→Workspace→Card→Input→Primary **逐级增实**；不靠人工蓝紫粉伪造环境色；不为了透明把所有元素一起降 Opacity。

---

## 七、本会话踩过的坑（按"能省下一次踩坑"的价值排序）

**WinUI / XAML**
1. `UIElement.Translation` 是 `Vector3`，绑字符串会抛 `Cannot be converted to type Vector3` → **切页即 APPCRASH**。
2. `x:Bind` 绑到 **`Style` 属性不会随属性变更刷新**（选中态渲染不出来）→ 改绑画刷。
3. **`PropertyChanged` 少了 `Raise` 就等于绑定永不刷新**（`IsSelected` setter 曾丢掉一行 `RaiseAll()`）。
4. **`{x:Bind}` 的嵌套路径在 UserControl 上不会随后置注入重算**；经典 `{Binding}` + DataContext 在本项目实测也不生效 → 用**命名元素 + 代码直推 + 订阅**。
5. **ScrollViewer 内容里的星号行拿不到高度** → 空态面板被压成 0 高；改 `Auto`。
6. 替换资源字典里的画刷对象**不会**刷新已解析的 `StaticResource` 引用 → 实时调参必须改**画刷实例的 `Color`**。
7. 运行期把带自定义模板（ThemeShadow + Translation）的主按钮 `IsEnabled=false` → **关闭窗口时崩**（§2.3）。
8. 引用不存在的资源键**编译能过、运行时崩**（用 `poc-startup.log` 看真实异常）。

**Desktop Acrylic**
9. 不调用 `SetSystemBackdropConfiguration` → `State` 恒为 Fallback，看到的是 **FallbackColor 近白板**（伪装成透明生效）。
10. 系统默认 `LuminosityOpacity=0.85` + 近白 TintColor = 白雾来源；只调 `TintOpacity` 无效。
11. 应用内 `AcrylicBrush` 采样**桌面壁纸**而非窗口 backdrop → 嵌套 Acrylic 必然乳白/雾化。

**自动化与 PowerShell**
12. `file://` URL 路径含空格未做百分号编码 → 浏览器打开的是**"未找到文件"**（采到错误页，数据全废）。
13. **PowerShell 变量名大小写不敏感**：`$scenes`（目录）与 `$scene`（循环变量）是同一个变量 → 文件名/URL 全乱。
14. `Start-Process -ArgumentList @(...)` 会拼成一行且**不给含空格路径加引号** → 进程秒退；必须自己拼字符串并加引号。
15. 参数里的 `#` 会被 PowerShell 当**注释**。
16. PowerShell 函数返回 `OrderedDictionary`/数组会被**枚举拆散** → 返回 `[pscustomobject]`。
17. `@((250 - $ox), 690, …)` 每个减法**必须加括号**，否则被解析成数组运算。
18. `$host` 是只读自动变量，不能赋值。
19. DSH 的 `edit`/`write` 工具会**剥掉 UTF-8 BOM**（`.ps1` 与 `.xaml` 改完都要补回 `EF BB BF`）。
20. **`Stop-Process` 过滤字符串若出现在自身命令行里会杀掉自己**（本轮真实发生）；过滤进程必须排除 `$PID` 并用 `-File` 之类精确匹配。
21. 无边框 WinForms 窗体 `MainWindowTitle` 为空 → 让窗口**自己把 HWND 写进文件**再由外部读取；且需 `-STA`。
22. UIA 的 `ValuePattern.SetValue` / `InvokePattern` 注入**会让本应用崩溃** → 只能"真实鼠标点击 + 真实键盘"驱动，UIA 仅用于**只读**。

---

## 八、回退方式（都可零成本回退）

| 回退目标 | 做法 |
|---|---|
| 关闭 Desktop Acrylic（回到 XAML Mica） | 运行时设 `PCMIG_BACKDROP=mica`；或设 `PCMIG_BACKDROP=off` 完全不接管 |
| 关闭开发者调节器 | 删 `MainWindow.xaml.cs` 中 `TuningPanel.Attach(...)` / `LoadIfExists()` 两行；或删面板 XAML 元素 |
| 完全回到四页 Shell 之前 | git tag **`v0.5.0-before-4page-shell-20260926`**（= HEAD `c9aef30`）；或快照 `archive\step1-fidelity-20260926-r2\stage12-shell\before\` |
| 桌面包回退 | `<工作区根>\archive\desktop-package-history\` 下按时间戳选目录，整目录复制回 `<用户目录D>\Desktop\<桌面交付根>` |
| 视觉参数回默认 | 调参面板「恢复默认」；或删除 `%LocalAppData%\PCMig\DeveloperVisualSettings.json` |

**禁止**：`git reset --hard` / `git checkout .` / `git restore .` / `git clean -fd`（会不可逆抹掉本会话未提交成果，撤销请用 `git stash`）。

---

## 九、纪律与坐标（发版相关，务必继续遵守）

- 发版只能跑 `<仓库根>\tools\release.ps1`，**先写日志再打包**，五道闸门全绿，一个版本号只发一次；**本会话未发版**。
- 交付四件套 + 逐文件 SHA256 全 MATCH；发版后三验；故障类连续复现 3 次 + 回归 3 次。
- 功能冻结范围（Core/Robocopy/SMB/UNC/Scan/Planner/Verify/Repair/Resume/Migration Engine/CLI/WPF）继续冻结，本会话产品代码 0 改动。
- 所有 `.ps1` 必须 UTF-8 **带 BOM**。
- `<旧镜像根>` 是旧镜像，**只可作覆盖目标，绝不可作事实依据或代码来源**。
- 敏感凭据一律"见某处"引用，不复制明文（本文件无任何凭据）。

---

## 十、桌面判断包（`<用户目录D>\Desktop\<桌面交付根>`）

当前结构（**旧内容已清空重建**）：
- `本次改动文件\`：**32 个**改动源文件（按原路径）+ `改动文件清单.json`（逐文件 SHA256）
- `四页截图\`：Step1–Step4 最新实拍（1426×893，100% 缩放）
- `判断文档\`：调参过程 4 张截图、`developer-tuning-verification.json`、四个验证脚本
- 合计 46 文件

---

## 十一、接手后建议的第一个动作

1. 先跑一次 `dotnet build` + `dotnet test` 确认基线（应为 0 错误 / 130 通过）；
2. 启动 `src\PCMig.WinUI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\PCMig.WinUI.exe`，点标题栏右侧 Sliders 图标，确认 7 个 Slider 能实时改变界面；
3. 取用户标定数值 → 固化进 `Themes\Materials.xaml` 与 `BackdropSpike` 默认值；
4. 按 §六 顺序推进 Round 2 余下批次（**每批必看四页**）。