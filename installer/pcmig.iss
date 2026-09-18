; ============================================================
; PCMig 迁移工具 - Inno Setup 安装脚本
; 构建: ISCC.exe installer\pcmig.iss（先执行 publish 到 dist\app）
; ============================================================
#define MyAppName "PCMig 迁移工具"
#define MyAppVersion "0.4.1"
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
OutputBaseFilename=PCMigSetup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
DisableProgramGroupPage=yes
SetupIconFile=..\src\PCMig.Gui\Assets\pcmig.ico
UninstallDisplayIcon={app}\PCMig.exe
VersionInfoVersion=0.4.1.0
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
Name: "{autodesktop}\PCMig 迁移工具"; Filename: "{app}\PCMig.exe"
Name: "{group}\PCMig 迁移工具 (GUI)"; Filename: "{app}\PCMig.exe"
Name: "{group}\PCMig 命令行 (CLI)"; Filename: "{app}\pcmig-cli.exe"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{group}\更新日志"; Filename: "{app}\更新日志.txt"
Name: "{group}\首日实测检查表"; Filename: "{app}\首日实测检查表.md"

[Run]
Filename: "{app}\PCMig.exe"; Description: "安装完成后启动 PCMig 迁移工具"; Flags: postinstall nowait skipifsilent unchecked
