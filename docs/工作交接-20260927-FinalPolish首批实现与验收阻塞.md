# 工作交接 · 20260927 · Final Polish 首批实现与验收阻塞

> **新增档案，不覆盖任何历史交接。**
>
> 权威工作区：`E:\Project\deepseek work\PCMig`  
> 分支：`feature/winui-v0.5.0` ｜ HEAD：`c9aef30454081fd81a13c8c9feef029f0629ad67`  
> 当前状态：**未提交的既有脏工作树之上继续开发；未发版；未触碰交付区 `E:\Project\PCMig` 与工作副本 `D:\PCMig`。**

---

## 1. 本会话用户目标与边界

用户正式将 PCMig v0.5.0 进入 **Final Polish / 精修**：冻结 Desktop Acrylic、Developer Visual Tuning、Layer 0–5、语义 Material Token 与整体浅蓝/浅红/浅橙方向；禁止重建材质体系、禁止 nested Acrylic、禁止改迁移业务与业务 ViewModel。

明确实施顺序：更新日志恢复 → 响应式布局 → 人工标注缺陷 → Motion → 视觉扫描 → 完整回归。用户随后授权自动执行，并将备份策略改为：本会话开始前建立一次可靠基线，随后普通阶段不反复备份；交付区/工作副本/破坏性操作等原授权边界仍不变。

功能冻结仍有效：Core、CLI、Robocopy、连接/共享/迁移/校验/安全逻辑、存档格式、业务 ViewModel、导航语义均未授权改动。

## 2. 人工标注图已逐张视觉审阅

用户提供的原始证据目录为 `D:\Users\User\Desktop\新建文件夹 (5)`；**没有删除、覆盖、重命名或向其中写入任何文件。**

| 标注图 | 人工问题（已确认） |
|---|---|
| `微信图片_20260927123506_126_3.png` | 四页标题数字 Badge 偏位/平；侧栏数字 Badge 与图标过近；IP/用户名/密码/搜索输入的文字视觉不居中；上下步按钮材质层次不统一。 |
| `微信图片_20260927123728_130_3.png` | Developer 调节入口靠近 Caption Buttons；Developer Panel 底部内容/说明被裁。 |
| `微信图片_20260927123912_132_3.png` | Step2 Badge、目录搜索、目标路径、下一步均有同类问题。 |
| `微信图片_20260927124030_134_3.png` | Step4 搜索输入仍有同类垂直居中问题。 |

人工标注优先于 OCR/像素抽样；后续必须用真实窗口人眼复验。

## 3. Update Log 历史调查结论

- 历史真实功能名为**“更新日志”**，不是伪造的 About/VersionHistory。
- WPF 旧实现仍完整保留在 `src\PCMig.Gui\ChangelogWindow.xaml(.cs)`：版本列表、正文、上一版/下一版、TXT 打开与 Markdown 解析；旧入口是 `BtnChangelog` 与 `Changelog_Click`。
- 唯一历史内容源仍为 `docs\更新日志.md`；CLI 与安装器仍有自己的日志入口。
- WinUI 0.5.0 中此前完全没有更新日志入口、View 或数据资源链；不是仓库删除旧实现，而是 WinUI 新 UI 线从未移植。
- WinUI 仍不在 `PCMig.sln`，当前 `tools\release.ps1` 仍只打 WPF Gui/CLI；本会话**没有**把发版目标切到 WinUI，也没有修改发版脚本或更新日志版本内容。

## 4. 本会话实际新增/编辑的 Final Polish 文件

> 注意：仓库在本会话开始时已有大量未提交/未跟踪文件。本表只记录本会话可由操作记录确认的文件；不把所有 `git status` 项误报为本会话产出。

| 文件 | 本会话内容 |
|---|---|
| `src\PCMig.WinUI\Presentation\ChangelogEntry.cs`（新增） | 嵌入 Markdown 的只读版本条目模型与解析器；不接触迁移状态或存档。 |
| `src\PCMig.WinUI\Views\ChangelogPanel.xaml(.cs)`（新增） | 右侧更新日志 Panel：版本选择、默认最新版本、上一/下一版、关闭及焦点入口。 |
| `src\PCMig.WinUI\PCMig.WinUI.csproj` | 仅追加 `docs\更新日志.md` 的 `EmbeddedResource`，显式 ASCII `LogicalName=PCMig.WinUI.Assets.CHANGELOG.md`；未改版本或 `.pri` publish Target。 |
| `src\PCMig.WinUI\MainWindow.xaml(.cs)` | 将产品 Header 的 v0.5.0 Badge 作为更新日志入口；挂接 Changelog Panel；与 Tuning Panel 互斥、关闭时焦点回日志入口；增加响应式命名列与布局应用调用。 |
| `src\PCMig.WinUI\Themes\Controls.xaml` | `PCMigTextBox` 与 `PCMigPasswordBox` 统一加入 `VerticalContentAlignment=Center`。 |
| `src\PCMig.WinUI\Views\Step1ConnectPage.xaml` | 标题 Badge 完整居中；IP/用户名/密码/搜索输入纵向 Padding 对称化。 |
| `src\PCMig.WinUI\Views\Step2SelectDataPage.xaml` | 标题 Badge 完整居中；目录搜索与目标路径输入纵向 Padding 对称化。 |
| `src\PCMig.WinUI\Views\Step3ProgressPage.xaml` | 标题 Badge 完整居中。 |
| `src\PCMig.WinUI\Views\Step4ResultPage.xaml` | 标题 Badge 完整居中；搜索输入纵向 Padding 对称化。 |
| `src\PCMig.WinUI\Views\StepNavigationControl.xaml` | 数字 Badge 与功能图标的 Grid `ColumnSpacing` 由 8 调为 14 DIP。 |
| `src\PCMig.WinUI\Views\DeveloperTuningPanel.xaml` | 加入内部 `ScrollViewer`、Panel EntranceThemeTransition；窗口收小时允许内部滚动，保留全部 7 个 Slider 和四个操作。 |
| `src\PCMig.WinUI\Presentation\ResponsiveLayoutController.cs`（新增） | 纯视觉 Wide/Normal/Compact 布局计算；当前驱动侧栏宽、导轨间距、主区顶部间距与浮层 MaxHeight，不读取业务状态。 |
| `src\PCMig.WinUI\Themes\Motion.xaml`（新增） | Fast/Normal/Slow、语义缓动名、Page/Panel 进入距离等集中 Motion Token。 |
| `src\PCMig.WinUI\App.xaml` | 合并 `Themes/Motion.xaml`，位置在 Typography 后、Controls 前，保留 Controls 最后覆盖 ThemeResource 的契约。 |

### 已实现但只能静态确认的行为

1. Changelog 使用嵌入的 `docs\更新日志.md`，按 `## v...` 分节并从顶部版本表配日期；默认选最新条目，提供前/后版本切换。
2. 四页标题数字均显式 `HorizontalAlignment/VerticalAlignment/TextAlignment=Center`；输入 Style 显式垂直居中，调用点不再使用此前不对称的上/下 Padding。
3. Panel 与四页根 UserControl 使用 `EntranceThemeTransition`；该框架 ThemeTransition 理论上随系统动画设置工作，但**未取得真实视频证据**。
4. `ResponsiveLayoutController` 在 Root `SizeChanged` 中应用；Compact 目前只是密度收紧与 Panel 高度控制，尚非完整结构重排。

## 5. 备份与回退

用户授权的会话前一次性三重备份已创建并由主控复核：

| 项 | 位置 / 结果 |
|---|---|
| Tag | `v0.5.0-before-final-polish-20260927` → `c9aef30454081fd81a13c8c9feef029f0629ad67` |
| 源码镜像 | `E:\Project\镜像备份源码\PCMig-v0.5.0-before-final-polish-20260927`，208 文件；robocopy `/L` 无差异。 |
| Git bundle | `E:\Project\镜像备份源码\PCMig-v0.5.0-before-final-polish-20260927.bundle`；`git bundle verify` 退出码 0、48 refs、完整历史。 |
| 清单 | `E:\Project\镜像备份源码\PCMig-v0.5.0-before-final-polish-20260927-清单.md`，含 18 项关键文件 SHA256 MATCH 与回退命令。 |

主控还独立复核了 `MainWindow.xaml`、`MainWindow.xaml.cs`、`Themes\Controls.xaml`、`DeveloperTuningPanel.xaml` 的源/镜像 SHA256 MATCH。

### 备份已知瑕疵

备份子任务一度错误扫描了未排除的 bin/obj，生成了误导性的 `E:\Project\镜像备份源码\PCMig-v0.5.0-before-final-polish-20260927-关键文件SHA256.txt`。它**不是权威结论**，权威清单是上表的 `-清单.md`；由于没有用户明确授权删除/改名，该文件被保留未动。此瑕疵不影响 robocopy `/L` 无差异、bundle verify 与关键 SHA256 MATCH 三类独立证据。

回退禁止使用 `git reset --hard`、`git checkout .`、`git restore .`、`git clean -fd`。推荐先 `git stash -u` 保全当前未提交内容，再从 tag 查看；整目录恢复或 bundle clone 必须另获明确授权。

## 6. 验证结果与失败记录

| 验证 | 实测结果 |
|---|---|
| `dotnet build src\PCMig.WinUI\PCMig.WinUI.csproj -c Release` | 多次通过，最近一次 0 warning / 0 error。 |
| `dotnet test tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj -c Release --no-build` | 130/130 通过。 |
| Motion 初次构建 | 曾因 WinUI XAML 不支持 `CubicBezierEasing` 报 2 个 WMC0001；已移除无效类型并重新构建通过。 |
| `git diff --check -- src\PCMig.WinUI` | 报 `MainWindow.xaml:32` 的既有空白尾随行；本会话未完成清理，后续可安全清理。 |
| 真实桌面启动 | 两次请求启动刚 Build 的 `PCMig.WinUI.exe` 均被 CUA 宿主返回 `user rejected`。没有用 PowerShell/脚本绕过。 |

**因此未完成的验收绝不可说已完成**：未获得真实窗口截图、四页截图、Developer Panel 截图、Update Log 截图、动画视频、WER、DPI 100/125/150、窗口尺寸/比例矩阵、最小窗口行为、人眼视觉复验。

## 7. 未完成项 / 风险

1. Responsive 未完成：Step2 双栏→Compact 的重排、Step3 四统计卡折行、Step4 工具条换行/分组、Step1 主区滚动、Bottom Status Bar 收紧均尚未落实。
2. Windows 真实最小尺寸未完成：没有 `WM_GETMINMAXINFO / ptMinTrackSize`；不能宣称实际阻止窗口缩小到不可用。
3. Button 精修未完成：Secondary Button 的 Hover/Pressed 仍可能回退到默认 WinUI ThemeResource；Primary/Secondary/Danger/Icon 的完整四态未统一。
4. Developer 入口迁移未完成：目前仍在标题栏右侧；用户要求迁至产品 Header 的版本信息附近。
5. Motion 未完成：没有 `IsTransitioning` 防重入、方向性 Page 出/入场、Sidebar 选中条过渡、Panel Exit、Dialog 动效或视频验收。当前仅有基础 ThemeTransition 与集中 Token。
6. Update Log 的视觉/完整行为未实机验证；当前移植是轻量解析/呈现，未逐项复刻 WPF 的粗体 Run、总览或“用记事本打开 TXT”兜底。
7. 本机 CUA Desktop 启动权限仍被宿主拒绝。必须先恢复实际启动/交互能力，才能继续用户指定的“真实运行”闭环。
8. DeveloperVisualSettings 本地持久化可在启动时改变全局 Material alpha；本会话没有改动其隔离策略，也未能真实查看当前保存值对画面的影响。

## 8. 下一会话建议顺序

1. 先在 DSH 实际启用 Desktop/CUA 的应用启动与交互；不要用脚本绕过拒绝。
2. 显式 Build WinUI 项目，并启动刚生成的：`src\PCMig.WinUI\bin\Release\net8.0-windows10.0.19041.0\win-x64\PCMig.WinUI.exe`。
3. 先看四页、Badge、输入框、Developer Panel、Update Log；以人工标注图为优先基线。
4. 再完成 Responsive 的结构性 Compact 重排和 Win32 最小追踪尺寸，实测后决定最终最小 DIP。
5. 统一 Secondary/Button 四态和 Developer 入口迁移。
6. 完成 IsTransitioning 与真实 Motion，录制 Step 1→2→3→4、反向、Developer Panel、Update Log 的真实视频。
7. 完整执行 Build、130+ Tests、真实截图/DPI/窗口矩阵、WER/异常日志/进程清理；所有缺项如实记录。
8. 未发版前不得改 `tools\release.ps1`；发版时仍须先更新日志、使用说明、五道闸门、四件套、SHA256、后三验和测试报告。

## 9. 纪律与安全

- 权威源始终是 `E:\Project\deepseek work\PCMig`；`I:\K\deepseek work` 仅为旧镜像，未读取为事实依据。
- 本会话没有发版、没有删除、没有覆盖交付区/工作副本、没有使用破坏性 Git 命令。
- 本文不含任何凭据明文。
- 所有 `.ps1` 未被本会话编辑；BOM 规则仍有效。
