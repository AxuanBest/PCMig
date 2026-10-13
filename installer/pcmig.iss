; ============================================================
; PCMig 迁移工具 - Inno Setup 安装脚本
; 构建: ISCC.exe installer\pcmig.iss（先执行 publish 到 dist\app）
;
; ★ P1-10（2026-10-11）★ 两条命令行开关，正式发布件默认一字不变：
;   · /DSetupSuffix=-candidate-20261011   候选包输出后缀（**绝不覆盖** dist\PCMigSetup-<版本>.exe 正式发布件）
;   · /DSetupStageLog=1                   安装器分段日志（回答"双击→欢迎页到底花了多久"）
;   例：ISCC.exe /DSetupSuffix=-candidate /DSetupStageLog=1 installer\pcmig.iss
; ============================================================
#ifndef SetupSuffix
  #define SetupSuffix ""
#endif

#define MyAppName "PCMig 迁移工具"
#define MyAppVersion "0.5.3"
#define MyAuthor "郑子轩（Axuanbest）"
#define MyCopyright "郑子轩 个人制作"

[Setup]
AppId={{3B8E5A42-7C1F-4E9A-A2D1-9C0F5E7B3D61}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Axuanbest
AppPublisherURL=https://github.com/Axuanbest
AppComments=由 {#MyAuthor} 个人制作 · 企业内网 Windows 数据/用户环境迁移（直拉模式）
DefaultDirName={autopf}\PCMig
DefaultGroupName=PCMig
OutputDir=..\dist
OutputBaseFilename=PCMigSetup-{#MyAppVersion}{#SetupSuffix}
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
DisableProgramGroupPage=yes
#ifdef SetupStageLog
; ★ P1-10 ★ 分段测量时才写安装日志（正式发布件保持默认：不写日志）。
SetupLogging=yes
#endif
SetupIconFile=pcmig.ico
UninstallDisplayIcon={app}\PCMig.WinUI.exe
VersionInfoVersion=0.5.3.0
VersionInfoCompany=Axuanbest
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} 安装程序（{#MyAuthor} 个人制作）
VersionInfoCopyright={#MyCopyright}

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
; ★ v0.5.1（2026-10-06）★ 正式用户交付物只保留 WinUI 主界面（PCMig.WinUI.exe）与 CLI（pcmig-cli.exe）。
;   旧 WPF 前端（PCMig-classic.exe）**退出正式交付链**：源码仍在仓库 src\PCMig.Gui 作历史实现/回退参考，
;   但不再随安装包或 Portable 发布，因此这里不再有「经典界面·回退」入口。
;   图标文件也已从 WPF 工程目录解耦到 installer\pcmig.ico（见上面 SetupIconFile）。
Name: "{autodesktop}\PCMig 迁移工具"; Filename: "{app}\PCMig.WinUI.exe"
Name: "{group}\PCMig 迁移工具 (WinUI)"; Filename: "{app}\PCMig.WinUI.exe"
Name: "{group}\PCMig 命令行 (CLI)"; Filename: "{app}\pcmig-cli.exe"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{group}\更新日志"; Filename: "{app}\更新日志.txt"
Name: "{group}\首日实测检查表"; Filename: "{app}\首日实测检查表.md"

[Run]
; ★ v0.5.1（2026-10-06）★ 安装完成自检只启动 WinUI 主界面；「启动经典界面（回退）」条目已随旧 WPF 前端一并删除。
Filename: "{app}\PCMig.WinUI.exe"; Description: "安装完成后启动 PCMig 迁移工具（WinUI 界面）"; Flags: postinstall nowait skipifsilent unchecked

; ============================================================
; ★ P1-10 ★ 安装器分段日志（只在 /DSetupStageLog=1 时编译进来）
;
; 为什么用 [Code] 记而不是靠 Inno 自带日志：自带日志只有"哪一刻写了哪一行"，
; 而我们要回答的是**用户从双击到看见欢迎页之间各段各花了多久**，尤其是
; "解压/校验准备"到"欢迎页真的画出来"这一段。这里用 InitializeSetup /
; InitializeWizard / CurPageChanged 三个官方挂钩点，把相对起点毫秒写进日志。
;
; ★ 纪律：不开开关时整个 [Code] 段不存在 ⇒ 正式发布包的安装行为逐位不变。★
; ============================================================
[Code]
#ifdef SetupStageLog
var
  GStageStart: Cardinal;

{ Inno 6 的 Pascal Script 里没有 GetTickCount，直接声明 kernel32 里的原始函数：
  这是最省事也最稳的取毫秒口径（差值是相对的，不依赖机器开机多久）。 }
function WinTick: Cardinal;
  external 'GetTickCount@kernel32.dll stdcall';

function InitializeSetup(): Boolean;
begin
  GStageStart := WinTick;
  Log('PCMIG-STAGE setup-init (0 ms)');
  Result := True;
end;

procedure InitializeWizard();
begin
  Log(Format('PCMIG-STAGE wizard-init (+%d ms since InitializeSetup)', [WinTick - GStageStart]));
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  Log(Format('PCMIG-STAGE page-%d-rendered (+%d ms since InitializeSetup)', [CurPageID, WinTick - GStageStart]));
end;
#endif
