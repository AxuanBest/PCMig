# PCMig 工作交接：WinUI Visual Pass 2.5 与 Canonical 16:10 视口（2026-09-25）

> **交接性质**：按铁律 49 新建，只增不覆；不替换任何历史交接文档（`工作交接-20260925-WinUI-Step1视觉Pass2.md` 等一律保留）。
>
> **凭据纪律**：全文不记录口令、账号、密钥或可还原凭据；本会话未输入任何真实凭据，也未发起任何网络连接。
>
> **状态定义**：WinUI 技术基线 ✅｜Core 集成 ✅｜Visual Pass 1 ✅｜Visual Pass 2（Design System）✅ 基础｜**Visual Pass 2.5（Design System Closeout）进行中、未通过**｜Canonical 16:10 视口 ✅ 已建立｜Compact 自适应 ⬜ 未开始｜Pass 3 ⬜ 未开始｜Step 2 ⬜ 未开始。
>
> **本会话未发版**：未运行 `tools/release.ps1`，未写更新日志/使用说明，未触碰交付区 `E:/Project/PCMig` 与工作副本 `D:/PCMig`。

---

## 1. 本会话完成事项与结论

1. **修复 WinUI 窗口初始化编译错误（单点、纯 Shell）**。
   - 现象：`error CS0103: 当前上下文中不存在名称"Win32Interop"`，WinUI Build 失败。
   - 根因：Windows App SDK 下 `Win32Interop` 不是可直接使用的裸类型名；而 `Microsoft.UI.Xaml.Window.AppWindow` 属性已可直接取得 AppWindow（已从本机包 `Microsoft.WinUI.xml` 的运行时契约条目确认）。
   - 修法：删除 HWND/WindowId 转换与多余 using，改为 `this.AppWindow.Resize(new SizeInt32(1440, 900));`。
   - 结论：Build 0 warning / 0 error；**只解决初始窗口尺寸，未改任何业务、绑定或事件逻辑**。

2. **建立 Canonical 16:10 视觉验收视口（含实测读数）**。
   - 正式目标图 `docs/ui/v0.5-reference/step1-connect.png` 实测 **1586×992**，比例 **1.59879**（≈16:10）。
   - 本机显示器 125% 缩放（物理 1920×1200，工作区 1920×1140）。
   - **关键实测**：WinUI 进程当前 **DPI 不感知**。因此 `Resize(1440,900)` 得到的是**物理 1800×1125**（客户端 1780×1115），`RasterizationScale=1`，XAML 可用空间 = 物理 ÷ 1.25 = **1424×891 DIP**（比例 1.598，与目标图一致）。
   - 结论：本机 Canonical 验收截图取物理 **1800×1125**；强制物理 1440×900 只等于 **1136×711 DIP**，属更矮视口（Compact 量级）。**1440×900 物理 ≠ 1440×900 DIP**。

3. **修正主工作区行分配（本会话最实质的布局修正）**。
   - 根因：主工作区 Grid 行为 `Auto/Auto/150/Auto`，剩余高度无人吸收 → Connection Card 下方留下大片**无功能空白**；且早期把 1440×824 当主基准，反过来压缩了字体/控件/间距。
   - 修法：共享区行改为 `*`（自适应吸收剩余高度），恢复 Canonical 输入密度（MinHeight 46、Padding 14,8、FontSize 14、RowSpacing 12）。
   - **同尺寸前后客观对比**（同为物理 1800×1125 窗口，像素级测量）：
     - 主按钮「下一步」位置：y=804–857 → **y=929–982**（下移 100 DIP）；
     - 共享区高度：≈141 DIP → **≈241 DIP**；
     - 「下一步」底边到状态栏的空白：≈135 DIP → **≈34 DIP**。
   - 结论：「Connection 下方大片空白」已由 Shares Workspace 吸收，结构性问题按数据确认修正。

4. **已知但未修（需授权）的实际缺陷：WinUI 进程 DPI 不感知**。
   - 影响：125% 缩放下渲染被系统整体拉伸（RasterizationScale=1），画面偏软；窗口物理尺寸与 DIP 关系不符合直觉。
   - 修法建议：`src/PCMig.WinUI/app.manifest` 增加 per-monitor v2 DPI awareness（纯 Shell 配置）。**本会话未改**，因为它会改变所有截图的清晰度与坐标换算，属独立决策。

5. **完成交接前新鲜三重备份并验证**（tag + 镜像 + bundle + 清单；镜像 **175/175 SHA256 MATCH**）。

6. **边界独立核对**：对 `src/PCMig.Core`、`src/PCMig.Gui`、`src/PCMig.Cli`、`src/PCMig.WinUI/Presentation`、`PCMig.sln`、`tests`、`tools`、`matrix` 共 **103 文件**与 Pass 2.5 前镜像逐文件 SHA256 比对 → **missing 0 / extra 0 / mismatch 0**（原始 JSON 见第 4 节）。结论：本轮未触碰任何冻结范围实现。

7. **如实记录的能力限制**：本会话主控模型与子模型**均不支持图像输入**，且 `describe_image` 工具报错 `baseURL must be an absolute http(s) URL`。因此**主控无法亲自看图**，Step 1 最终视觉验收**未完成**，改以像素几何测量作替代证据（方法局限见第 7 节）。

---

## 2. 权威坐标、版本与工作树状态

| 项 | 事实（本会话实测） |
|---|---|
| 权威工作区 | `E:/Project/deepseek work/PCMig` |
| 交付区 | `E:/Project/PCMig`（本会话未触碰） |
| 工作副本 | `D:/PCMig`（本会话未触碰） |
| 当前分支 | `feature/winui-v0.5.0` |
| HEAD | `c9aef30454081fd81a13c8c9feef029f0629ad67` |
| HEAD 主题 | `v0.5.0 WinUI technical baseline before visual reconstruction` |
| 版本元数据 | 已发稳定交付仍为 **v0.4.9**；WinUI 标题栏显示 v0.5.0 仅为开发中视觉标识，**未发版** |
| 工作树 | **74 条**（33 已跟踪修改 + 41 未跟踪）；严禁 reset / checkout . / restore . / clean -fd |
| remote | 无 remote；本会话未 push |
| 源码镜像（旧） | `E:/Project/镜像备份源码/PCMig-v0.5.0-before-visual-pass25-20260925`（175/175 MATCH，20:00 时点） |

> **必须知悉**：HEAD 仍停在 `c9aef30`；Visual Pass 1/2/2.5 的 Theme/XAML/窗口初始化改动**全部仍在未提交工作树**。对这些成果，**镜像备份是唯一完整回档载体**；tag/bundle 只承载已提交历史。

---

## 3. 本会话已改文件（全部位于 `src/PCMig.WinUI/`，纯 Presentation）

### 3.1 已跟踪、被本会话修改

| 文件 | 说明 | 当前 SHA256 |
|---|---|---|
| `src/PCMig.WinUI/MainWindow.xaml.cs` | 新增 2 行：在 TitleBar 初始化后设初始外窗 1440×900（`this.AppWindow.Resize`）；无其他改动 | `608A2FA5E773F5CDC20E97DA764FFB358DFC60BA01475625D050BFC7F6F88FED` |
| `src/PCMig.WinUI/MainWindow.xaml` | 主工作区行 `150`→`*`；连接表单 RowSpacing `7`→`12`（另含 Pass 1/2 的 Shell 结构，见历史交接） | `0D195C019CFA3815997556C1EFAE89A295035E148F02E3FD46A606FC5D33AE30` |
| `src/PCMig.WinUI/Themes/Materials.xaml` | 八层语义材质 TintOpacity/CornerRadius/Padding 收口（Pass 2 起） | `620F3D0F7BD5F6E8928BC98C418FC13A7CA1F7AAF5886607101A583F10118DBA` |
| `src/PCMig.WinUI/App.xaml` | 合并 Colors/Materials/Typography/Controls 四套资源 | `B23FE1DCD239DBDA15B6148FDCB3B9839636EA95982BF5826E68E011A7B0315A` |

### 3.2 未跟踪（新增）

| 文件 | 说明 | 当前 SHA256 |
|---|---|---|
| `src/PCMig.WinUI/Themes/Colors.xaml` | Ambient/Text/Accent/Border/Surface/Opacity Token + AccentGradient/AccentDisabled | `36E6F1A6DEF22FC0846318FDA6EBD4DDD10C66D60FEC892C9E5F6E1A3170F3B9` |
| `src/PCMig.WinUI/Themes/Controls.xaml` | Primary Button（含 Disabled 可识别）/Secondary/TextBox/PasswordBox/Toggle Style | `9D8D6BCFA32BC43B97A7A3D27ECA125E2A9F9A2DB7EBAA8D414EAA748201C0DC` |
| `src/PCMig.WinUI/Themes/Typography.xaml` | Display/Page/Section/Field/Body/Muted/Button 文字层级 | `7EB450557EFF9034382E330AE409CC584A2572A9E1AE0160EBFDE8F52E04F8D0` |

### 3.3 明确未改动

- `src/PCMig.Core/**`、Robocopy、SMB/NetworkShare、Scan、Planner、状态机、Verify、Repair、Resume、Report、存档格式：**零改动**（103 文件 0 差异）。
- `src/PCMig.Gui/**`（WPF 稳定版）、`src/PCMig.Cli/**`、`PCMig.sln`、`tests/**`、`tools/**`、`matrix/**`：**零改动**。
- `src/PCMig.WinUI/Presentation/ConnectionViewModel.cs`（Core 适配器）：**零改动**。

---

## 4. 测试与证据

| 验证 | 结果 | 证据文件 |
|---|---|---|
| WinUI Release Build | ✅ **0 warning / 0 error**（exit 0） | `archive/winui-clean-20260925/build2.log` |
| Core Tests | ✅ **115/115**（0 失败 0 跳过） | `archive/winui-clean-20260925/tests2.log` |
| 冻结边界独立核对 | ✅ 103 文件，missing 0 / extra 0 / SHA256 mismatch 0 | `archive/pcmig-boundary-compare-1790339624064-93ed122d-8e27-446e-9c6b-6a3d0509d3db.json` |
| Canonical 16:10 实拍 | ✅ 物理 1800×1125，黑边 0.01% | `archive/screenshots/winui-canonical-star-20260925.png` |
| Compact 实拍 | ✅ 物理 1440×900，黑边 0% | `archive/screenshots/winui-compact-star-20260925.png` |
| 布局行分配前后对比 | ✅ 同尺寸 1800×1125 前后测量（主按钮 +100 DIP、共享区 141→241 DIP） | `archive/screenshots/winui-canonical-natural-20260925.png` 与上表 Canonical 图；测量脚本 `archive/scripts/winui-accent-bands.ps1` |
| 视图几何剖面 | ✅ 行列亮度剖面 + 最强边缘 + 20 段亮度带 | `archive/winui-clean-20260925/geometry-profile.json`，脚本 `archive/scripts/winui-geom-edges.ps1` |
| XAML 可用空间实测 | ✅ 初始 1424×891 DIP（RasterizationScale=1）；强制物理 1440×900 时 1136×711 DIP | `archive/winui-size-20260925-b9b06828/xaml-metrics.json`、`xaml-metrics-1440.json` |
| 窗口/产物指纹 | ✅ 外窗 1800×1125、客户区 1780×1115、WindowDpi 96；exe/dll/pri SHA256 已记录 | `archive/winui-size-20260925-b9b06828/instrumented-window.json`、`instrumentedcanonical-window.json` |

**截图工具与脚本（本会话新增，均在 `archive/` 而非仓库根）**：

- `archive/scripts/winui-capture-region.ps1`：DPI 感知的窗口定位 + 区域截图（UTF-8 带 BOM，已复查前三字节 `EF BB BF`）。
- `archive/scripts/winui-geom-edges.ps1`、`winui-geom-profile.ps1`、`winui-accent-bands.ps1`、`winui-accent-bands2.ps1`：几何测量（只读）。

**临时诊断代码状态**：测量期间曾在 `MainWindow.xaml.cs` 内临时写入一次性指标探针，**已全部移除**；当前源码经逐行复核为干净状态（37 行，无任何诊断代码），并已基于干净源码重新 Build/Test/截图。

**如实声明**：本会话**未做**故障类的「3 次复现 + 3 次回归」（本轮是视觉布局修正，不是故障修复）；也**未完成** Step 1 最终视觉验收（原因见第 1.7 与第 7 节）。

---

## 5. 发版与哈希 / 三验

**本会话未发 v0.5.0 正式版**，未运行 `tools/release.ps1`，未写 `docs/更新日志.md` / `docs/使用说明.txt` / 测试报告的发行条目，未触碰交付区与工作副本。

因此：**交付四件套、逐文件 SHA256 全 MATCH、五道闸门、发版后三验，本轮全部不适用，且不得伪称完成。**

WinUI 自包含发布的既有性质（历史记录）：约 523 文件 / 220.7 MB，不能直接进入现有 WPF 四件套，需单独发布策略授权。

---

## 6. 备份与回退

### 6.1 本轮交接前最新三重备份（本会话亲自创建并逐项验证）

| 重 | 位置 | 验证结果 |
|---|---|---|
| tag | `v0.5.0-pass25-before-handover-20260925` | annotated；tag 对象 `d038586c…`；解析 commit = `c9aef30…` = HEAD |
| 镜像 | `E:/Project/镜像备份源码/PCMig-v0.5.0-pass25-before-handover-20260925` | 175 文件；**全量 SHA256 175/175 MATCH**（missing 0 / extra 0 / mismatch 0） |
| bundle | `E:/Project/镜像备份源码/PCMig-v0.5.0-pass25-before-handover-20260925.bundle` | 6,064,217 字节；`git bundle verify` exit 0；**45 refs**；含本 tag 与 HEAD；complete history |
| 清单 | `E:/Project/镜像备份源码/PCMig-v0.5.0-pass25-before-handover-20260925-清单.md` | 记录时间、HEAD、tag、bundle、175/175、回退命令与限制 |

### 6.2 回退方式

```powershell
# 未提交工作树（含 Pass 1/2/2.5 全部视觉成果）只能以镜像回档：
robocopy "E:/Project/镜像备份源码/PCMig-v0.5.0-pass25-before-handover-20260925" "E:/Project/deepseek work/PCMig" /E

# 已提交历史可从 bundle 在新目录重建：
git clone "E:/Project/镜像备份源码/PCMig-v0.5.0-pass25-before-handover-20260925.bundle" <新目录>
```

**禁止**：把 tag 当成未提交视觉成果的完整回档点；禁止 `git reset --hard` / `git checkout .` / `git restore .` / `git clean -fd`。

---

## 7. 已知限制与风险（下一会话动手前必须知悉）

1. **【最高优先级】本会话无视觉能力**：主控模型与子模型均不支持图像输入；`describe_image` 工具因 `baseURL` 未配置而不可用。→ **Step 1 最终视觉验收未完成**，用户在会话中自行查看了截图。**下一会话若仍无图像能力，视觉验收环节无法完成**；建议换用支持图像输入的模型或修复该插件配置。
2. **几何测量的方法局限**：`winui-geom-edges.ps1`/`winui-accent-bands.ps1` 用亮度剖面与蓝色掩码推导布局边界，**不是目视判断**；参考图含桌面背景与装饰元素，其蓝色掩码噪声较大（同一脚本在参考图上曾因数组索引报错，已修）。这些数字可用于**结构定位与前后对比**，**不能替代**对材质层级、边缘高光、环境光、字体可读性的主观验收。
3. **DPI 不感知缺陷未修**：见第 1.4。未授权前不得改动 `app.manifest`。
4. **Compact（1440×824 / 1366×768）未做自适应**：当前共享区在 compact 视口（XAML 1136×711 DIP）下被压到约 98 DIP，虽不再溢出且「下一步」与状态栏不重叠，但**尚未实现** Padding/Gap/Header 密度/区块比例的降级规则；禁止为了 compact 反向压缩 Canonical。
5. **无公司 SMB 真机复测**：IPC$ 正常、无 IPC$、`\\IP\D$`、手动 share、错误口令、DNS 多地址六类场景仍未在可达主机验证；适配器目前只有编译、静态契约与 UNC 解析夹具证据。
6. **口令行为**：WinUI 适配器不持久化口令；Step 2 若需再次连接，必须重新输入或设计受控短期来源，**禁止把口令塞进 ViewModel 字段**。
7. **NuGet 网络风险**：Windows App SDK 大包在企业网络曾反复 `ResponseEnded`；本机靠 BITS + 本地缓存绕过，换机/CI 需离线包策略。
8. **`src/PCMig.WinUI/.vs/` 存在 IDE 状态目录**（镜像已排除 `.vs`，不在备份内）。清理属破坏性动作，需用户当轮明确指令。
9. **工作树庞大且含历史未提交成果**：74 条改动，其中大量属本会话之前的成果；后续若提交必须精确分组，**禁止 `git add -A`**。
10. **未验证项**：DPI 多档位/跨显示器/高对比度/关闭透明效果/RDP 均未验证；Windows App SDK 2.5.1 自包含体积约 220MB。

---

## 8. 下一会话待办与建议命令

### 8.1 先决条件

1. 让**具备图像输入能力**的主控（或修复 `describe_image` 配置）就位，否则不要声称完成视觉验收。
2. 本机若需改动前备份：镜像/tag/bundle 已就位（第 6 节）；**新改动前如需新回档点，按铁律 43–48 以新命名重建三重备份**，不要复用旧 tag。

### 8.2 视觉验收顺序（严格按此，不得跳到下一步）

1. **Canonical 16:10 验收**：以物理 1800×1125（XAML 1424×891 DIP）实际运行截图，对照 `docs/ui/v0.5-reference/step1-connect.png`。
2. 逐项对照：Header 比例｜Sidebar 宽度与四步+提示卡完整｜Page Header｜Connection Workspace｜**Shares Workspace 高度与空状态**｜Next Action 位置｜Bottom Status｜Material 层级｜Edge Highlight｜Elevation｜Typography 可读性｜Ambient 局部性｜Input Inset｜Primary/Disabled Button。
3. 有明显偏差就在 **Step 1 本页**继续收口，**不得**因「功能已完成/代码结构漂亮」而进入下一页。
4. Canonical 通过后，再做 **Compact 自适应**（VisualState + AdaptiveTrigger，纯 XAML），最后做更低尺寸/DPI/RDP fallback 验证。

### 8.3 建议命令

```powershell
cd "<仓库根>"

# 1) 干净构建（0 warning / 0 error）
dotnet build src\PCMig.WinUI\PCMig.WinUI.csproj -c Release --no-restore -p:NuGetAudit=false

# 2) 回归（必须 115/115）
dotnet test tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj -c Release --no-restore

# 3) 关闭残留进程 → 启动刚 Build 的新 EXE → 定位窗口 → 截图（同一条命令内完成）
Get-Process PCMig.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process "<仓库根>\src\PCMig.WinUI\bin\Release\net8.0-windows10.0.19041.0\win-x64\PCMig.WinUI.exe"
Start-Sleep -Seconds 8
powershell -NoProfile -ExecutionPolicy Bypass -File "<工作区根>\archive\scripts\winui-capture-region.ps1" -Out "<工作区根>\archive\screenshots\<下一张>.png" -X 0 -Y 0
# Compact 复测：追加 -Width 1440 -Height 900
```

**注意**：截图必须对应**刚 Build 的新产物**（本会话踩过：旧进程占用 exe 导致 Build 失败；以及 8 秒等待不足导致 `NO-PROCESS`）。

### 8.4 待用户决策项

1. 是否授权修 **DPI 不感知**（`app.manifest` 加 per-monitor v2）。
2. 何时开始 **Compact 自适应**（建议 Canonical 通过之后）。
3. 是否清理 `src/PCMig.WinUI/.vs/`。

---

*本交接文件仅记录本会话实际执行与实测事实；未记录任何凭据；明确区分了「已验证」「替代证据」与「未完成」三类。历史交接档案一律保留。*
