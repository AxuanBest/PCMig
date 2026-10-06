# PCMig 工作交接：WinUI Step 1 功能基线与 Visual Pass 2（2026-09-25）

> **交接性质**：本文件按铁律 49 新建，只增不覆；不替换任何历史交接文档。
>
> **凭据纪律**：全文不记录口令、账号、密钥或可还原凭据。凭据只在 WinUI `PasswordBox` 调用栈中短暂传递，未进入 Adapter 字段、任务文件或日志。
>
> **当前状态定义**：WinUI Technical PoC ✅｜Core Integration ✅｜Self-contained Deployment PoC ✅｜Step 1 Functional Wiring ✅｜Step 1 Visual Reconstruction **进行中**｜Step 1 Visual Acceptance **未通过**｜Step 2 **未开始**。

---

## 1. 本会话完成事项与结论

1. **建立 WinUI 3 独立前端 PoC**：新增 `src\PCMig.WinUI\`，采用 `net8.0-windows10.0.19041.0`、`UseWinUI=true`、`WindowsPackageType=None`、`WindowsAppSDKSelfContained=true`。PCMig.Core 通过单一 `ProjectReference` 被 WinUI 引用；WinUI 未加入 `PCMig.sln`，WPF GUI / CLI / Core 不反向依赖 WinUI。
2. **WinUI PoC Build / Run / Publish 已实测打通**：Windows App SDK 2.5.1 Stable。Release Build 0 warning / 0 error；self-contained publish 成功并独立启动。修复了 publish 默认遗漏应用自身 `PCMig.WinUI.pri` 导致 XamlParseException 的问题：`PcmigWinUICopyAppPriToPublish` 会在 Publish 后复制 PRI。
3. **建立 Step 1 Connection Adapter**：`ConnectionViewModel` 只投影 Core 的 `PreflightChecker.RunAsync` 与 `NetworkShare.ConnectForTransfer`，未复制 Robocopy / TransferOrchestrator / SMB P/Invoke。连接预检/测速经 `Task.Run` 离开 UI 线程；支持 `\\IP\共享名` 解析、无 IPC$ 的直连回退、完整 UNC 手动共享名、会话替换和应用日志接线。
4. **新增 WinUI Step 1 静态契约护栏**：6 个用例锁定 WinUI→Core 单向引用、unpackaged/self-contained 开关、Core 预检/共享调用、无迁移引擎复制、Step 1 控件/事件、Core 无 UI 依赖。
5. **功能基线 Git checkpoint 已创建**：`c9aef30454081fd81a13c8c9feef029f0629ad67`，消息 `v0.5.0 WinUI technical baseline before visual reconstruction`。提交只含 WinUI/参考图/PoC 报告/Step 1 契约测试 16 个新增文件，不含 Core/WPF/CLI/tools/构建产物。
6. **非 UI diff audit 已完成**：与 `PCMig-winui-poc-before-20260924` 镜像对比，Core、原 WPF GUI、CLI、tools、matrix、`PCMig.sln` 共 85 文件 `DIFF=0 / MISSING=0`。本轮没有对 SMB/共享/原业务 Service 做新增改动。
7. **Visual Pass 1（Shell Structure Baseline）完成**：Product Header、品牌身份、Source→Target Capsule、四步 Step Rail、Ambient Layer、Workspace Shell、Global Status Bar 的结构方向已由用户确认保留。
8. **Visual Pass 2（Design System）已开始并实际运行**：新增 `Colors.xaml` / `Typography.xaml` / `Controls.xaml`，重构 `Materials.xaml`，将全屏近不透明 Gradient 改为低 Alpha Base + 四个 Ambient Blob；建立 BaseBackdrop / Shell / Primary / Secondary / Inset / Elevated / Interactive / Selected 八层语义材质、Border/Edge 分离、ThemeShadow Elevation、Primary Button 状态模板与 Inset 输入控件 Style。最终视觉验收仍未完成。
9. **已删除用户授权的诊断残留**：`<镜像备份根>\_rcprobe_20260924` 已删除，实际释放 `3,351,675,829` 字节；它不是有效备份，删除不可回退。

---

## 2. 权威坐标、版本与工作树状态

| 项 | 事实 |
|---|---|
| 权威工作区 | `<仓库根>` |
| 交付区 | `<交付区>`（本会话未触碰） |
| 工作副本 | `<发版工作副本>`（本会话未触碰） |
| 当前分支 | `feature/winui-v0.5.0` |
| HEAD | `c9aef30454081fd81a13c8c9feef029f0629ad67` |
| HEAD 主题 | `v0.5.0 WinUI technical baseline before visual reconstruction` |
| 版本元数据 | 已发稳定交付仍是 v0.4.9；WinUI UI 显示 v0.5.0 仅为开发中产品视觉标识，未发版 |
| 工作树（交接备份时） | 32 个已跟踪修改 + 40 个未跟踪 = **72 条**；严禁 reset/checkout/restore/clean |
| remote | 无 remote；本轮未 push |

> **必须知悉**：HEAD 仍基于 `c9aef30`，当前 Visual Pass 1/2 的若干 Theme/XAML 修改仍在未提交工作树。对这些未提交成果，**镜像备份才是完整回档载体**；tag/bundle 只承载已提交历史。

---

## 3. 本会话新增/改动文件

### 3.1 已提交的 WinUI 功能基线（commit `c9aef30`）

- `src\PCMig.WinUI\PCMig.WinUI.csproj`：unpackaged/self-contained WinUI 项目、Core 单向引用、Publish PRI 修复目标。
- `src\PCMig.WinUI\App.xaml` / `App.xaml.cs`：资源合并与启动异常日志。
- `src\PCMig.WinUI\MainWindow.xaml` / `.cs`：Step 1 Window、事件接线、Custom TitleBar。
- `src\PCMig.WinUI\Presentation\ObservableObject.cs`：WinUI Presentation INPC 基类。
- `src\PCMig.WinUI\Presentation\ShareItem.cs`：共享展示 DTO。
- `src\PCMig.WinUI\Presentation\ConnectionViewModel.cs`：Core 预检/手动共享的 WinUI Adapter。
- `src\PCMig.WinUI\Themes\Materials.xaml`：PoC 初始材质资源。
- `src\PCMig.WinUI\app.manifest`：unpackaged manifest。
- `tests\PCMig.Core.Tests\WinUiStep1ContractTests.cs`：6 个 Step 1 静态契约用例。
- `docs\ui\v0.5-reference\step1-connect.png`、`step2-select.png`、`step3-progress.png`、`step4-result.png`：正式 Visual Target 原图副本。
- `docs\v0.5.0-WinUI-PoC-报告-20260924.md`：PoC 完整报告。

### 3.2 当前未提交的 Visual Pass 1/2 改动（本次交接镜像已覆盖）

- `src\PCMig.WinUI\App.xaml`：合并 Colors / Materials / Typography / Controls 四套资源。
- `src\PCMig.WinUI\MainWindow.xaml`：Shell Structure Baseline 与 Step 1 视觉布局；目前仍含部分布局数值，后续 Pass 2 继续收口。
- `src\PCMig.WinUI\Themes\Materials.xaml`：八层 Material / Elevation 资源。
- `src\PCMig.WinUI\Themes\Colors.xaml`：Ambient、Text、Accent、Border、Surface、Opacity Token（新增，未提交）。
- `src\PCMig.WinUI\Themes\Typography.xaml`：Display/Page/Section/Field/Body/Caption/Typography Token（新增，未提交）。
- `src\PCMig.WinUI\Themes\Controls.xaml`：Primary Button VisualState 与 Input/Toggle Style（新增，未提交）。
- `docs\工作交接-20260925-WinUI-Step1视觉Pass2.md`：本文件（新增，未提交）。

### 3.3 明确未改动的冻结范围

- `src\PCMig.Core\**`、Robocopy、SMB 底层实现、Scan、Planner、状态机、Verify、Repair、Resume、Report、存档格式：本轮 WinUI 开工以来没有新增修改（非 UI audit 85 文件全 MATCH）。
- `src\PCMig.Gui\**`、`src\PCMig.Cli\**`、`PCMig.sln`：未纳入 checkpoint，未被 WinUI 改动。
- 现有已修改的 Core/WPF/工具文件属于项目此前未提交成果，不得误回退或清理。

---

## 4. 测试与证据

| 验证 | 结果 | 证据/说明 |
|---|---|---|
| WinUI Release Build | ✅ 0 warning / 0 error | `dotnet build src\PCMig.WinUI\PCMig.WinUI.csproj -c Release --no-restore -p:NuGetAudit=false` |
| Core Tests | ✅ 115/115 | `dotnet test tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj -c Release --no-restore` |
| WinUI Step 1 契约 | ✅ 6/6 | ProjectReference=Core only；self-contained；Core 调用；无引擎复制；控件事件；Core 无 UI |
| WPF/CLI/Core 回归 | ✅ 先前实测 0 warning / 0 error、115/115 | `dotnet build PCMig.sln -c Release --no-restore --no-incremental`；后续继续跑 |
| WinUI Build 运行 | ✅ | bin Release EXE 启动，`poc-startup.log` 记录 `OnLaunched activated` |
| self-contained publish | ✅ PoC/Step1 功能基线曾通过 | `PCMig.WinUI.pri` 随发布复制；Visual Pass 2 当前未重新 Publish（需后续补跑） |
| Core 无 UI 反向依赖 | ✅ | WinUiStep1ContractTests + 非 UI SHA256 audit |
| 视觉截图（Pass 1） | ✅ 已生成 | `archive\screenshots\v050-winui-step1-shell-pass1-20260925.png` |
| 视觉截图（Pass 2） | ✅ 已生成 | `archive\screenshots\v050-winui-step1-design-pass2-final-20260925.png` |

**视觉验收如实声明**：当前阶段只确认 Shell 结构方向通过；Step 1 Final Visual Acceptance 未通过。后续必须以 `docs\ui\v0.5-reference\step1-connect.png` 对照最新实际运行截图，继续完成 Pass 2 收口，再进入 Connection Workspace（Pass 3）与 Shares Workspace（Pass 4）。

---

## 5. 发布与哈希 / 三验

本会话**未发 v0.5.0 正式版**，未运行 `tools\release.ps1`，未写更新日志/使用说明/测试报告的 v0.5.0 发行条目，未触碰交付区或工作副本。因此：交付四件套、SHA256、三验、发版闸门均**不适用且不得伪称完成**。

WinUI 自包含 PoC 的已知发布性质：约 523 文件 / 220.7MB。WinUI 不能直接进入现有 WPF 的四件套，需后续获得单独发布策略授权。

---

## 6. 备份与回退

### 6.1 当前交接前最新三重备份

| 重 | 位置 | 验证 |
|---|---|---|
| tag | `winui-pass2-before-handover-20260925` | annotated；解析 commit = `c9aef30` = HEAD |
| 镜像 | `<镜像备份根>\PCMig-winui-pass2-before-handover-20260925` | 174 文件 / 72,427,328 字节；全量 SHA256 **174/174 MATCH** |
| bundle | `<镜像备份根>\PCMig-winui-pass2-before-handover-20260925.bundle` | `git bundle verify` exit 0、complete history、43 refs；并已从 bundle 临时 clone 复核 |
| 清单 | `<镜像备份根>\winui-pass2-before-handover-20260925-清单.md` | 记录时间、HEAD、tag、bundle、回退与限制 |

### 6.2 回退命令与限制

```powershell
# 未提交的 Pass 1/2 状态必须以镜像回档：
robocopy "<镜像备份根>\PCMig-winui-pass2-before-handover-20260925" "<仓库根>" /E

# 已提交的技术基线可从 tag / bundle 重建：
git clone "<镜像备份根>\PCMig-winui-pass2-before-handover-20260925.bundle" <新目录>
```

**禁止**把 tag 当作当前未提交 Pass 2 的完整回档点；也禁止 `git reset --hard`、`git checkout .`、`git restore .`、`git clean -fd`。

---

## 7. 已知限制与风险

1. **Visual Pass 2 未完成最终视觉验收**：当前 Shell 结构正确，但需继续降低雾化感、验证实际层级/边缘/阴影/控件状态与参考图的一致性。
2. **Step 1 功能边界明确**：搜索、目录树精确选择、默认目标路径、专家模式、恢复任务等后移至 Step 2；不得伪称 Step 1 已包含它们。
3. **无公司 SMB 真机复测**：需后续在可达主机验证 IPC$ 正常、无 IPC$、`\\IP\D$`、手动 share、错误口令、DNS 多地址六类场景。当前 adapter 有编译、静态契约和 UNC 解析夹具证据，但非公司端到端证据。
4. **口令行为**：WinUI Adapter 不持久化口令；Step 2 若需要再调用连接能力，必须重新输入或设计受控短期来源，禁止把口令塞进 ViewModel 字段。
5. **NuGet 网络风险**：Windows App SDK 大包曾多次 `ResponseEnded` 中断；本机采用 BITS 下载与本地 NuGet 缓存绕过。换机/CI 需准备离线包策略。
6. **WinUI 自包含体积**：约 220MB，且现 WPF 发版脚本不编译/发布 WinUI；不能直接接入交付区，需后续单独决策。
7. **VS workload 查询口径**：Visual Studio 2026 有 WinUI 模板且实测 Build/Run/Publish 成功，但 `vswhere -requires Microsoft.VisualStudio.Workload.WinUI` 返回空；不要只依赖该 ID 判断环境。
8. **已有未提交历史成果**：Core/WPF/tools 等大量既有未提交项仍在工作树，后续提交必须精确分组，禁止 `add -A`。

---

## 8. 下一会话待办与建议命令

### 仅做 Step 1 Visual Reconstruction（禁止 Step 2 与业务扩展）

1. **Pass 2 收口**：亲自对照 `docs\ui\v0.5-reference\step1-connect.png` 与 `archive\screenshots\v050-winui-step1-design-pass2-final-20260925.png`，继续移除 MainWindow 里的视觉 magic number，把剩余字号/圆角/间距/未来导航样式纳入 Token；验证 ThemeShadow 与 Actual Screenshot 的层级。
2. **Pass 3 — Connection Workspace**：重构 Host / Username / Password / Benchmark / Device State / Connect Action 的构图；不变更 `ConnectionViewModel` 的 Core 调用行为。
3. **Pass 4 — Shares Workspace**：实现 Header / Search（仅 UI filter）/ Add / Empty State / Selection State / Next Action；不得改变共享发现或 SMB 逻辑。
4. 每个 Pass 必跑：

```powershell
cd "<仓库根>"
dotnet build src\PCMig.WinUI\PCMig.WinUI.csproj -c Release --no-restore -p:NuGetAudit=false
dotnet test tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj -c Release --no-restore
# 启动刚 Build 的 WinUI exe 后截图：
powershell -NoProfile -ExecutionPolicy Bypass -File "<工作区根>\archive\scripts\winui-poc-screencap.ps1" -Out "<工作区根>\archive\screenshots\<下一张>.png"
```

5. Step 1 达到完整视觉门槛后，再重新 self-contained Publish、确认 `PCMig.WinUI.pri` 在 publish 目录、独立启动并截图。
6. 视觉工作继续前，若当前状态已有任何新修改，按铁律 43–48 用新命名重做三重备份。

---

*本交接文件仅记录本次实际执行事实与明确未验证项；未记录任何凭据。新建完成后，历史交接档案一律保留。*
