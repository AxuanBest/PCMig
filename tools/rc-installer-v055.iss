; PCMig v0.5.5-rc.1 发布候选安装脚本
; ============================================================
; 四条安装链身份互不覆盖（AppId / 安装目录 / 数据根 / 快捷方式名 四项全不同）：
;   · 正式 v0.5.4   AppId {3B8E5A42-7C1F-4E9A-A2D1-9C0F5E7B3D61} → {autopf}\PCMig
;   · 预览链         AppId {7A4D2E91-5B36-4C08-9F1E-2D8C4A6B7E50} → {autopf}\PCMig-Preview2
;   · v0.5.4-rc.1   AppId {C41F7A26-9E3B-4D57-8A62-6B0D91E4F3C8} → {autopf}\PCMig-RC1
;   · 本 RC 链       AppId {8E2B4D17-6C39-4A85-B3F1-5D7A9C0E2B64} → {autopf}\PCMig-RC1-v055
; 数据根 %LocalAppData%\PCMig-RC1-v055（由载荷内 preview-isolation.json 指定，rc 身份才会读）
;
; 为什么 v0.5.5-rc.1 不复用 v0.5.4-rc.1 的身份：
;   ① 复用 AppId 会让 Inno 把两者视作同一产品的升级 —— 安装 v0.5.5-rc.1 会直接
;      覆盖本机已装的 v0.5.4-rc.1，用户无法并存对照，也无法回退；
;   ② 复用安装目录 {autopf}\PCMig-RC1 与数据根 %LocalAppData%\PCMig-RC1 会让
;      两个候选版的作业/日志/诊断写进同一个根，正是本次要验证的「数据隔离」反例；
;   ③ 本次修复的正是「旧副本被误删」，验收时更需要能把 v0.5.4-rc.1 留在本机做对照。
;
; 隔离设计（四项全部与正式版及既有 RC 不同）：
;   · AppId            = {8E2B4D17-6C39-4A85-B3F1-5D7A9C0E2B64}
;   · DefaultDirName   = {autopf}\PCMig-RC1-v055
;   · DefaultGroupName = PCMig-RC1-v055
;   · 桌面/开始菜单快捷方式文件名带「v0.5.5 RC1」后缀，不与同名 .lnk 互覆盖
;
; 构建（由 tools\rc-build.ps1 调用，不手工裸编译）：
;   ISCC.exe /DAppVersion=0.5.5-rc.1 /DNumericVersion=0.5.5.0
;            /DSourceDir=<已审计 app 目录> /DOutputDir=<新目录>
;            /DOutputName=PCMigSetup-0.5.5-rc.1 tools\rc-installer-v055.iss
;
; ★ 纪律：本脚本绝不写入仓库 dist\，绝不产出 dist\PCMigSetup-<正式版本>.exe。
; ★ 本脚本没有任何 [Registry] 段 —— 有意为之：进程级数据隔离不得用账户级旋钮实现。
; ============================================================

; ── 编译期参数（全部由构建脚本注入，缺省值仅为防呆）──────────────────────
#ifndef AppVersion
  #define AppVersion "0.0.0-rc.INVALID"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef OutputName
  #define OutputName "PCMigSetup-RC-INVALID"
#endif

#define MyAppName "PCMig 迁移工具（v0.5.5 RC1）"
#define MyAppVersion AppVersion
#define MyAuthor "郑子轩（Axuanbest）"
#define MyCopyright "郑子轩 个人制作"
#define MyPreviewTag "v0.5.5-rc.1"

[Setup]
AppId={{8E2B4D17-6C39-4A85-B3F1-5D7A9C0E2B64}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Axuanbest
AppPublisherURL=https://github.com/Axuanbest
AppComments=本地发布候选（RC）（{#MyPreviewTag}）· 非正式发布件 · 由 {#MyAuthor} 个人制作
DefaultDirName={autopf}\PCMig-RC1-v055
DefaultGroupName=PCMig-RC1-v055
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
SetupIconFile=..\installer\pcmig.ico
UninstallDisplayIcon={app}\PCMig.WinUI.exe
; VersionInfoVersion / VersionInfoProductVersion 是 Win32 定长版本字段，
; 必须纯数字四段（0.5.5.0）；传 0.5.5-rc.1 会被 ISCC 直接拒绝。
#ifndef NumericVersion
  #define NumericVersion "0.5.5.0"
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

; ② 隔离配置的显式声明（编译期失败闸门）
;   第 ① 行是 recursesubdirs 通配，缺文件不报错；本行指名道姓，源文件不存在时
;   ISCC 在编译期直接失败 —— 缺隔离配置的 RC 包根本装不出来。
Source: "{#SourceDir}\preview-isolation.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autodesktop}\PCMig 迁移工具 v0.5.5 RC1"; Filename: "{app}\PCMig.WinUI.exe"
Name: "{group}\PCMig 迁移工具 v0.5.5 RC1 (WinUI)"; Filename: "{app}\PCMig.WinUI.exe"
Name: "{group}\PCMig 命令行 v0.5.5 RC1 (CLI)"; Filename: "{app}\pcmig-cli.exe"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{group}\更新日志"; Filename: "{app}\更新日志.txt"

[Run]
Filename: "{app}\PCMig.WinUI.exe"; Description: "安装完成后启动 PCMig 迁移工具（v0.5.5 RC1）"; Flags: postinstall nowait skipifsilent unchecked
