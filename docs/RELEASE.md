# PCMig Release Guide

本文件描述 PCMig 的**工程发布规则**：版本声明点、构建与测试基线、发版闸门、交付物构成、哈希校验、发版后验证、tag 策略与已发布版本的不可变性。

面向对象：任何需要构建、测试或发布本项目的人。

---

## 1. 当前正式版本

| Tag | 版本 | 主题 | 状态 |
|-----|------|------|------|
| `v0.5.1` | 0.5.1 | 可信度紧急修正：目标盘写满不再虚报 + 旧 WPF 前端退出交付物 | **当前稳定版** |
| `v0.5.0` | 0.5.0 | WinUI 3 全新界面 + Diagnostics 可信度体系 + 沉浸式传输进度 | 并列保留的正式版本 |

两个版本各有独立的 release commit 与 annotated tag，**历史安装包全部保留、互不覆盖**。

每个正式版本对应一个 annotated tag `v<主>.<次>.<修订>`，指向该版本的 release commit。

---

## 2. 前置条件

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10 1809+ / Windows 11 |
| .NET SDK | **8.0** |
| Windows App SDK | 构建 WinUI 前端所需（`net8.0-windows10.0.19041.0`） |
| Inno Setup | **6**（安装包） |
| Python | 3.x（`tools/md2txt.py` 生成随包纯文本日志） |
| Robocopy | Windows 内置 |

---

## 3. 构建

```powershell
# 结束可能占用输出文件的进程，避免 MSB3021 / MSB3027 假失败
Get-Process PCMig.WinUI, PCMig.Cli, PCMig.Gui, PCMig -ErrorAction SilentlyContinue | Stop-Process -Force

dotnet build PCMig.sln -c Release
```

**当前构建基线**：`0 error / 4 warning`。

4 条已知警告均为构建与测试基础设施提示，非功能性缺陷：

- `WMC1506` ×3 —— `src/PCMig.WinUI/Views/PCMigSurface.xaml` 中可静态化的动态资源引用；
- `xUnit2031` ×1 —— `tests/PCMig.Core.Tests/A5PresentationRegressionTests.cs` 中的断言写法建议。

---

## 4. 测试

```powershell
dotnet test tests/PCMig.Core.Tests/PCMig.Core.Tests.csproj -c Release
dotnet test tests/PCMig.Diagnostics.Tests/PCMig.Diagnostics.Tests.csproj -c Release
```

**当前测试基线**

| 工程 | 结果 |
|---|---|
| `tests/PCMig.Core.Tests` | **569 / 569 通过**（失败 0 / 跳过 0） |
| `tests/PCMig.Diagnostics.Tests` | **382 / 382 通过**（失败 0 / 跳过 0） |

涉及数据正确性的改动还必须跑 `tools/stability-test.ps1`（逐文件 SHA256 三方核对：自写脚本 + 应用自带 verify + robocopy 清单）。

---

## 5. 版本号声明点（必须全部一致）

发版脚本会写入并**反向自查**下列全部声明点，任何一处与目标版本不一致即中止：

| # | 位置 | 声明形式 |
|---|---|---|
| 1–4 | `src/PCMig.Cli`、`src/PCMig.Core`、`src/PCMig.Gui`、`src/PCMig.WinUI` 的 `.csproj` | `<Version>` |
| 5 | `src/PCMig.Gui/MainWindow.xaml` | `Title="PCMig 迁移工具 vX.Y.Z"` |
| 6 | `src/PCMig.WinUI/MainWindow.xaml` | `Title="PCMig 迁移工具 · vX.Y.Z"`（注意多一个 `·`） |
| 7 | `src/PCMig.WinUI/MainWindow.xaml` | 标题栏版本徽章 `TitleBarVersionText` 的 `Text="vX.Y.Z"` |
| 8 | `src/PCMig.WinUI/MainWindow.xaml` | 标题行「更新日志」徽章内的版本 `Text="vX.Y.Z"` |
| 9 | `README.md` | 当前稳定版本说明 |
| 10–11 | `installer/pcmig.iss` | `MyAppVersion` 与 `VersionInfoVersion` |

说明：

- 第 5 项（旧 WPF 前端）仍留在源码树并随源码树一起改版本号，但**不进入正式用户交付物**（见第 11 节）。
- `src/PCMig.WinUI/MainWindow.xaml` 中的版本徽章是**运行时真值**（没有 `.cs` 给它赋值），必须由脚本按正则 `Text="v[0-9.]+"` 替换；替换后脚本会反向自查该文件里所有 `Text="v<数字>"` 是否**全部**等于目标版本。
- `src/PCMig.WinUI/PCMig.WinUI.csproj` 原本没有 `<Version>` 节点，v0.5.0 起由脚本写入；该节点若被删除，声明式自查会失败（属设计，不要绕过）。

---

## 6. Changelog 要求（先写日志，再打包）

缺少任何一项，发版脚本**直接中止**——这是设计，不是意外。

1. `docs/更新日志.md`
   - 顶部「版本号与发布日期对照」表**加一行**；
   - 正文**按新到旧插入** `## vX.Y.Z — 主题` 一节，写清 **背景 / 改了什么 / 为什么（根因）**；面向使用者，不含内部隐私。
2. `docs/使用说明.txt`
   - 顶部新增一段 `■ vX.Y.Z …`（用户视角：这一版对他意味着什么）。
3. 第 5 节列出的全部版本声明点一致。

`docs/更新日志.txt`（记事本可直接打开，UTF-8 带 BOM + CRLF）由 `tools/md2txt.py` 从 Markdown 生成，与 md 同源。

---

## 7. 发布流程

**唯一发版入口**：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "tools/release.ps1" -Version X.Y.Z
```

禁止手工 `dotnet publish`、禁止直接调用 Inno Setup、禁止手工打包 —— 手工路径会绕过版本声明自查、口令残留扫描与逐文件哈希校验。

脚本依次执行：

```
版本一致性写入 + 反向自查
  → 五道闸门
  → WinUI publish（-c Release -r win-x64 --self-contained true -p:Platform=x64）
  → 交付树硬门禁（禁止历史经典前端入口混入）
  → 生成安装包与 Portable
  → 逐文件 SHA256 校验（全部 MATCH 才通过）
  → 交付与历史安装包并列保留
```

### 五道闸门（全绿才继续，任何一道红都不许绕过）

1. `docs/更新日志.md` 正文有本版条目；
2. 版本号与发布日期对照表有本版一行；
3. `docs/使用说明.txt` 有本版段落；
4. 交付区**无同名安装包**（拒绝同版本覆盖）；
5. 仓库**无真实口令残留**（扫描 `.ps1/.py/.md/.txt/.cs/.xaml/.iss/.json/.yaml`，排除 `bin|obj|dist`）。

### 版本号只发一次

脚本拒绝同版本覆盖。需要精修就**顺延版本号**；跳号必须写清原因并记进更新日志。

---

## 8. 交付物（四件套）

| # | 产物 | 位置 | 说明 |
|---|---|---|---|
| 1 | 安装包 | 交付区根目录 | `PCMigSetup-<版本>.exe` |
| 2 | Portable | 交付区 `Portable\` | **整棵 app 目录树**（WinUI 是 unpackaged + WindowsAppSDK/Win2D 自包含应用，原生组件必须与 exe 同目录，**不能压成单文件**） |
| 3 | 纯文本日志 | 交付区根目录 | `更新日志.txt`（UTF-8 带 BOM + CRLF，与 md 同源） |
| 4 | 工作副本 | 发版工作副本目录 | 安装态自检启动用；同样必须是整棵 app 树 |

Portable 目录内容：`PCMig.WinUI.exe` + 其原生组件、`pcmig-cli.exe`、`matrix\migration-matrix.yaml`、`使用说明.txt`、`更新日志.txt`、`首日实测检查表.md`。

### 逐文件 SHA256（7 组）

`PCMig.WinUI.exe` 两处、`pcmig-cli.exe` 两处、安装包、`更新日志.txt`、`使用说明.txt` —— 共 **7 组**，**全部 MATCH 才算交付成功**，不一致立即中止。

### 历史安装包

交付区按「**各版本并列存放**」使用：`PCMigSetup-*.exe` 历史安装包**一律全量保留，脚本绝不删除**。

---

## 9. 发版后验证（不接受"看起来没问题"）

**三处入口都必须能看到本版**：

1. 应用内顶栏「更新日志」—— 本版条目在最上方且默认选中；
2. 记事本打开交付区 `更新日志.txt` 与 `Portable\更新日志.txt` —— 含本版；
3. 命令行 `pcmig-cli.exe changelog` —— 输出含本版。

**冷启动与四步可用性验证**（WinUI 成为主界面后新增的最低门槛）：启动**新构建**的 WinUI 主界面，确认标题 / 徽章 / 版本信息确实是本版；确认 Step1 / Step2 / Step3 / Step4 四页都能打开，无 XAML 资源错误、无启动崩溃。

**本轮根因 / 修法 / 验证方式**必须追加进 `docs/测试报告-公司环境.md`，缺这一条不算完工。

---

## 10. Tag 策略与不可变性

- 每个正式版本一个 annotated tag `v<主>.<次>.<修订>`。
- **发布 tag 是交付物的身份，永不移动、永不覆盖、永不删除、永不重建。**
- 改动**前**的基线 tag 命名 `<当前版本>-before-<描述>`；改动**完成并验证后**命名 `<当前版本>-<描述>`。
- 禁止 `rebase`、`amend`、`filter-repo`、`BFG`、`reset --hard`、`git clean`、`force push`、任何形式的历史重写。
- **v0.5.0 与 v0.5.1 的历史与发行资产保持 immutable**：不移 tag、不删 tag、不重发。
- 仓库内的 `*-before-*` 等历史基线 tag 属开发过程记录，**不标记其名字所指的状态**；引用历史版本请以 GitHub Releases 中的历史安装包与 `docs/历史版本索引.md` 为准。
- 工作区撤销改动请用 `git stash` 或 `git revert`；禁止使用 `git reset --hard`、`git checkout .`、`git restore .`、`git clean -fd`。

---

## 11. 前端策略：WinUI 3 是正式前端，WPF 是历史实现

- **正式用户交付物只允许两个可执行文件**：`PCMig.WinUI.exe` 与 `pcmig-cli.exe`。
- 旧 WPF 前端（`src/PCMig.Gui`）**源码保留在仓库中**作为历史实现与回退参考，但**不再进入发布链**：不 publish、不进 `dist/app`、不进 `Portable`、不进安装目录、不进开始菜单、不进安装完成自检、不进 SHA256 清单。
- 不允许再以「备用」「经典界面」「回退」任何名义把旧 WPF 可执行文件随安装包或 Portable 一起发布。
- `tools/release.ps1` 对 `dist/app`、`Portable`、工作副本**三处**都有硬门禁：一旦出现历史经典前端可执行文件立即中止发版。
- `installer/pcmig.iss` 不含历史经典前端的 `[Icons]` / `[Run]` 条目。

发布必须走 `tools/release.ps1`，它会 `dotnet publish src/PCMig.WinUI/PCMig.WinUI.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o dist/winui` 并把 `dist/winui` 整棵目录树铺进交付树。任何情况下都不得只打旧前端而把 WinUI 留在仓库里。

---

## 12. Release assets 与源码快照是两件事

三件事**分别记录、互不推断**：

1. 历史 Release **是否存在**；
2. 安装包**是否存在**；
3. 是否存在**精确源码快照**。

> 有安装包 ≠ 有源码快照；有更新日志 ≠ 有源码快照。

安装包属**发布产物**，通过 GitHub Releases 作为 Release asset 分发，**不写入 Git 历史**：v0.5.0 / v0.5.1 已发布（Latest = v0.5.1），Legacy v0.1.x – v0.4.x 待通过独立归档 Release 集中上传。也不为缺少源码快照的历史版本补建版本 tag。

逐版本证据索引见 `docs/历史版本索引.md`。

---

## 13. 安全

- **真实口令绝不进交付物、绝不进仓库**；示例一律写 `<口令>`。第 5 道闸门会全仓扫描，命中即中止。
- 交付文档提及凭据一律用「见某处」引用，不复制明文。
- 诊断日志中的敏感字段按 `RedactionPolicy` 脱敏。
- 产品安全边界不许放松：不迁移凭据 / Cookie / 证书、驱动、安全软件、整盘 ACL、Windows 系统目录（见 `matrix/migration-matrix.yaml` 的 `securityBlockedFileNames`）。

---

## 14. 已知基础设施限制

- 发版脚本面向 Windows + PowerShell + Inno Setup + Windows App SDK 的本机环境编写，**未做 CI 化**，尚无自动化流水线。
- 安装包**不进入普通 Git tree**，而是通过 GitHub Releases 作为 Release asset 分发：**v0.5.0 与 v0.5.1 已完成发布，当前 Latest Release 为 v0.5.1**。仍可确认的 Legacy v0.1.x – v0.4.x 历史安装包**尚未批量上传**，计划由独立的 Legacy Installers Archive Release 集中归档；该归档发布与「历史源码快照是否可用」分别记录，互不推断。
- 界面视觉与动效的最终验收由人工完成；仓库内没有、也不主张存在自动视觉验收流程。
- 回退通道 `PCMIG_CLASSIC_UI=1`（或 exe 旁 `classic-ui.flag`）属旧 WPF 源码内的能力，从 v0.5.1 起不再随交付物提供；仓库内开发调试仍可使用。

---

## 15. 发版执行清单

```
[ ] 0. 前置：.NET SDK 8 / Inno Setup 6 / Python 可用
[ ] 1. 改代码 / 视觉 → dotnet build PCMig.sln -c Release（0 error）
[ ] 2. 跑两个测试工程，结果不低于当前基线（569 / 382）
[ ] 3. 视觉改动：截图 + 视觉自查通过
[ ] 4. 写 docs/更新日志.md（正文一节 + 对照表一行）
[ ] 5. 写 docs/使用说明.txt（顶部 ■ vX.Y.Z 一段）
[ ] 6. 跑 tools/release.ps1 -Version X.Y.Z（五道闸门全绿）
[ ] 7. 发版后三验：应用内更新日志 / 记事本 txt / pcmig changelog
[ ] 8. 冷启动与四步可用性验证
[ ] 9. 本轮根因·修法·验证追加进 docs/测试报告-公司环境.md
[ ] 10. 记下交付物清单与逐文件 SHA256（7 组全 MATCH）
```

---

## 相关文档

- [`更新日志.md`](更新日志.md) —— 逐版本变更说明
- [`历史版本索引.md`](历史版本索引.md) —— 逐版本证据索引（安装包 / Release / 源码快照）
- [`发布流程.md`](发布流程.md) —— 发版流程说明
- [`稳定性守则.md`](稳定性守则.md) · [`稳定性验收标准.md`](稳定性验收标准.md) —— 稳定性约束
- [`测试报告-公司环境.md`](测试报告-公司环境.md) —— 逐轮根因 / 修法 / 验证记录
