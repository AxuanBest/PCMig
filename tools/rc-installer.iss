; PCMig v0.5.4-rc.1 发布候选安装脚本
; 与 tools\preview-installer.iss（Preview 链）和 installer\pcmig.iss（正式链）三者互不覆盖：
;   · 正式 v0.5.3   AppId {3B8E5A42-7C1F-4E9A-A2D1-9C0F5E7B3D61}  → {autopf}\PCMig
;   · 预览链         AppId {7A4D2E91-5B36-4C08-9F1E-2D8C4A6B7E50}  → {autopf}\PCMig-Preview2
;   · 本 RC 链       AppId {C41F7A26-9E3B-4D57-8A62-6B0D91E4F3C8}  → {autopf}\PCMig-RC1
; 数据根 %LocalAppData%\PCMig-RC1（由载荷内 preview-isolation.json 指定，rc 身份才会读）
; ============================================================
; PCMig 迁移工具 —— **预览版专用** Inno Setup 安装脚本
;
; ★ 2026-10-15 新增（v0.5.4 Preview.2 实验安装包任务令）★
; ★ 2026-10-15 修订（数据隔离整改纠偏令）★ —— 见下方"整改记录"
;
; 为什么另起一个 .iss，而不是改 installer\pcmig.iss：
;   ① installer\pcmig.iss 是**正式发布件**的身份与流程定义，其 AppId
;      {3B8E5A42-7C1F-4E9A-A2D1-9C0F5E7B3D61} 与 DefaultDirName {autopf}\PCMig
;      被 tests\PCMig.Core.Tests\P1InstallerScriptTests.cs:74 直接断言钉住；
;   ② 若预览包复用该 AppId，Inno 会把两者视作**同一产品的升级**，
;      正式 PCMigSetup-0.5.3.exe 与预览包会互相覆盖，且卸载条目只有一个；
;   ③ 实测教训：v0.5.4-Preview.1 的安装器正是复用了正式 AppId 且安装到
;      {autopf}\PCMig，导致 C:\Program Files\PCMig 当前装的是 0.5.4-Preview.1，
;      与正式 v0.5.3 完全无法区分。本脚本存在的意义就是**不重复这个错误**。
;
; 隔离设计（四项全部与正式版不同，缺一不可）：
;   · AppId           = {C41F7A26-9E3B-4D57-8A62-6B0D91E4F3C8}   ← 独立身份
;   · AppName         = PCMig 迁移工具（Preview.2）              ← 独立显示名
;   · DefaultDirName  = {autopf}\PCMig-RC1                   ← 独立目录
;   · DefaultGroupName= PCMig-RC1                            ← 独立开始菜单组
;   另：桌面图标与开始菜单项的**文件名**都带 Preview.2 后缀，
;       避免与已装 Preview.1 / 正式版在用户桌面上互相覆盖同名 .lnk。
;
; 构建（由 tools\preview-installer-build.ps1 调用，一般不手工执行）：
;   ISCC.exe /DAppVersion=0.5.4-preview.2 /DSourceDir=<已审计 app 目录> \
;            /DOutputDir=<新目录> /DOutputName=PCMigSetup-Preview2-...  tools\preview-installer.iss
;
; ★ 纪律：本脚本**绝不**写入仓库 dist\，**绝不**产出 dist\PCMigSetup-<正式版本>.exe。
;
; ══════════════════════════════════════════════════════════════════════════
; ★ 整改记录（2026-10-15）：为什么 [Registry] 段被**整段删除** ★
; ══════════════════════════════════════════════════════════════════════════
;
; 【旧做法】本脚本原先在 [Registry] 段写：
;     Root: HKCU; Subkey: "Environment"; ValueName: "PCMIG_JOBS"    → %USERPROFILE%\.pcmig-preview2\Jobs
;     Root: HKCU; Subkey: "Environment"; ValueName: "PCMIG_LOG_DIR" → %USERPROFILE%\.pcmig-preview2\Logs
;   并在 [Code] 里记录原值、卸载时恢复。
;
; 【为什么这是错的】
;   HKCU\Environment 是**账户级**配置，不是"本预览版进程私有"。一旦安装：
;     · 该用户下**所有** PCMig 进程都会继承这两个变量 —— 包括正式 v0.5.3、
;       已装的 Preview.1、以及任何新启动的实例；
;     · JobManager.JobsRoot / LogBootstrap.AppLogDir 都会因此被改向预览目录；
;     · 正式版的作业与日志被静默改向，两个版本的数据从此混在一起；
;     · 卸载若异常（崩溃、强制删除、系统还原）会留下指向已删目录的悬空值。
;   → 工具选错了：**进程级的数据隔离，绝不该用账户级的旋钮实现。**
;     更糟的是，这个污染无法被测出来 —— 测试进程看不见别的账户状态。
;
; 【新做法】改为**随产物分发**的隔离配置：
;     {app}\preview-isolation.json   （见 [Files] 段的第二行 Source）
;   由 src\PCMig.Core\Util\PreviewIsolation.cs 在启动时按 AppContext.BaseDirectory 读取。
;   性质与旧做法的根本区别：
;     · 只有预览产物自己的 app 目录里才有这个文件 ⇒ 正式产物天然不受影响；
;     · 与"怎么启动"无关（双击 EXE / 快捷方式 / 安装后启动 / CLI 全部读同一份）；
;     · **零注册表、零环境变量、零账户残留** —— 卸载时随安装目录一起消失；
;     · 判定"是否预览身份"看程序集 AssemblyInformationalVersion 是否含 "-preview"，
;       不看文件名或目录名（那些可以被随手改）；
;     · 配置缺失/损坏/指向正式根/不可写时 **Fail Closed**：拒绝启动，
;       **绝不**回退到 %ProgramData%\PCMig。宁可不让它启动，也不静默污染。
;     · 每一条行为都有正负双向测试：tests\PCMig.Core.Tests\PreviewIsolationTests.cs
;
; 【环境变量通道怎么办】保留，但**降级为次级**：
;   PCMIG_JOBS / PCMIG_LOG_DIR 仍是被测试钉住的既有通道（便携场景、测量隔离都要用），
;   但在**预览身份**下，preview-isolation.json 的优先级**高于**环境变量 —— 否则账户级
;   变量又能改写预览行为，等于没隔离。正式身份下 PreviewIsolation 完全不介入，
;   环境变量语义与改动前逐位一致。
;
; 【本脚本现在不做什么】
;   · 不写任何注册表键（[Registry] 段已整段移除）；
;   · 不改任何环境变量（[Code] 段里读写 HKCU\Environment 的代码已整段移除）；
;   · 不广播 WM_SETTINGCHANGE（没有环境变更需要广播了）；
;   · 预建数据目录的职责交给运行时（PreviewIsolation.Evaluate 会自行建目录并验证可写），
;     安装器不再替运行时决定数据根 —— 单一事实来源。
; ============================================================

; ── 编译期参数（全部由构建脚本注入，缺省值仅为防呆，不允许直接裸编译）──────
#ifndef AppVersion
  #define AppVersion "0.0.0-preview.INVALID"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef OutputName
  #define OutputName "PCMigSetup-Preview2-INVALID"
#endif

#define MyAppName "PCMig 迁移工具（v0.5.4 RC1）"
#define MyAppVersion AppVersion
#define MyAuthor "郑子轩（Axuanbest）"
#define MyCopyright "郑子轩 个人制作"
#define MyPreviewTag "v0.5.4-rc.1"

[Setup]
; ★ 独立 AppId —— 与正式版 {3B8E5A42-...} 及历史预览 {7F3C1D62-...}/{7F20B30C-...} 均不同
AppId={{C41F7A26-9E3B-4D57-8A62-6B0D91E4F3C8}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Axuanbest
AppPublisherURL=https://github.com/Axuanbest
AppComments=本地发布候选（RC）（{#MyPreviewTag}）· 非正式发布件 · 由 {#MyAuthor} 个人制作
; ★ 独立安装目录 —— 绝不与正式版 {autopf}\PCMig 重合
DefaultDirName={autopf}\PCMig-RC1
; ★ 独立开始菜单组
DefaultGroupName=PCMig-RC1
; ★ 只允许卸载本预览版自己的条目（AppId 已独立，双保险）
UninstallDisplayName={#MyAppName} {#MyAppVersion}
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
DisableProgramGroupPage=yes
; ★ SetupIconFile 相对**本 .iss 文件所在目录**（tools\）解析。图标与正式安装器共用
;   installer\pcmig.ico（只读引用，不改动正式 installer\ 目录）。
SetupIconFile=..\installer\pcmig.ico
UninstallDisplayIcon={app}\PCMig.WinUI.exe
; ★ 预览版**不开** SetupLogging 默认——避免往 %TEMP% 留安装日志；
;   需要分段测量时由构建脚本加 /DSetupStageLog=1 打开。
; ★ 注意：VersionInfoVersion 与 VersionInfoProductVersion 都属于 Win32 **定长版本字段**
;   （写进 VS_FIXEDFILEINFO），必须是纯数字四段如 0.5.4.0；传 0.5.4-preview.2 会被
;   ISCC 直接拒绝（实测报 "…is invalid" 并中止编译）。
;   人可读的预览标识由 AppName / AppVerName 承载（它们允许自由字符串）。
#ifndef NumericVersion
  #define NumericVersion "0.5.4.0"
#endif
VersionInfoVersion={#NumericVersion}
VersionInfoCompany=Axuanbest
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#NumericVersion}
VersionInfoDescription={#MyAppName} 安装程序（本地发布候选，非正式发布）
VersionInfoCopyright={#MyCopyright}

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; ① 程序文件（构建脚本已确保 SourceDir 里包含 preview-isolation.json）
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

; ② ★ 隔离配置的显式声明（失败闸门）★
;   SourceDir 里应当已有 preview-isolation.json（由 tools\preview-build.ps1 生成，见其
;   "-IsolationDataRoot" 参数）。这里再**声明一次**，用途是让"隔离配置是安装包的组成部分"
;   这件事在 .iss 里显式可见、可被静态门禁检查。
;   ★ 与第 ① 行的区别：第 ① 行是 recursesubdirs 通配，**缺文件也不报错**；
;     本行指名道姓，源文件不存在时 ISCC 会在**编译期**直接失败 —— 缺隔离配置的
;     预览包根本装不出来。这就是我们要的：宁可编译失败，也不产出无隔离的预览包。
Source: "{#SourceDir}\preview-isolation.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; ★ 所有快捷方式名都带 Preview.2 后缀 ⇒ 不会覆盖正式版或 Preview.1 的同名 .lnk
Name: "{autodesktop}\PCMig 迁移工具 Preview.2"; Filename: "{app}\PCMig.WinUI.exe"
Name: "{group}\PCMig 迁移工具 Preview.2 (WinUI)"; Filename: "{app}\PCMig.WinUI.exe"
Name: "{group}\PCMig 命令行 Preview.2 (CLI)"; Filename: "{app}\pcmig-cli.exe"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{group}\更新日志"; Filename: "{app}\更新日志.txt"

[Run]
Filename: "{app}\PCMig.WinUI.exe"; Description: "安装完成后启动 PCMig 迁移工具（Preview.2 预览版）"; Flags: postinstall nowait skipifsilent unchecked

; ============================================================
; ★ 运行时数据隔离 ★ —— 现在由随产物分发的 preview-isolation.json 承担。
;
; 数据根（预览）：%LocalAppData%\PCMig-RC1\
;     ├─ Jobs\        作业数据（JobManager.JobsRoot）
;     ├─ Logs\        应用日志（LogBootstrap.AppLogDir）
;     ├─ Diagnostics\ 诊断会话（通过 DiagnosticRuntimeOptions.StorageRoot 注入）
;     ├─ Settings\    视觉标定（DeveloperVisualTuning.SettingsPath）
;     └─ Exports\     诊断导出（不再写共享桌面 Desktop\PCMig-Diagnostic）
;
; 完整设计、Fail Closed 语义与替代方案取舍见
; src\PCMig.Core\Util\PreviewIsolation.cs 的类注释（那里是单一事实来源）。
;
; ★ 本脚本**没有任何 [Registry] 段** —— 这是有意为之，不是遗漏。
;   负向门禁会扫本文件确认它不出现 HKCU/HKLM + Environment 的写入组合，
;   见 tools\preview-installer-gate-tests.ps1 的 N9 用例。
; ============================================================
