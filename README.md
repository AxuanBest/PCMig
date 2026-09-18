# PCMig — 企业内网 Windows 数据/用户环境迁移系统

> **直拉模式（Direct Pull）**：在新电脑上运行 PCMig，输入旧电脑 IP 或电脑名，通过 SMB 网络把旧电脑共享盘的数据完整拉到新电脑。

**当前版本：v0.4.1** ｜ 设计 & 开发：郑子轩（[Axuanbest](https://github.com/Axuanbest)）

## 架构

```
新电脑 (Win11, 运行 PCMig)
   │  ① 输入旧电脑 IP + 凭据
   │  ② Preflight → Scan → Plan → (人工确认) → Transfer → Verify → Report
   ▼
\\旧电脑\D$  ──SMB──▶  Robocopy 双通道(/MT 批量 + /Z 大文件)  ──▶  新电脑目标路径
```

## 核心特性

| 特性 | 说明 |
|------|------|
| **Data Plane / State Plane 分治** | D 盘完整数据迁移；C 盘仅迁用户 Profile 和 Recipe 批准的应用配置 |
| **双通道 Robocopy** | 普通文件 `/MT:16` 高吞吐；≥512MB 大文件 `/Z` 单线程可续传 |
| **对象级调度** | 源根每个一级目录 = 一个迁移对象，逐对象执行 |
| **协作式暂停** | `pause.request` 文件驱动；Immediate 模式可立即终止当前 robocopy |
| **断点续传** | Receipt（只追加）为权威状态，`job-state.json` 损坏可由 Receipt 重建 |
| **三级进度条** | 总体 / D盘 / 当前对象，含光波动画 |
| **策略矩阵** | `matrix/migration-matrix.yaml` 统一驱动 扫描/传输/验证 三方排除口径 |
| **三层日志** | Serilog 应用级 + 任务级 + robocopy 原始日志（文本 + 结构化 JSONL） |
| **L1/L2 验证** | L1 文件数+字节比对；L2 抽样哈希校验 |

## 项目结构

```
src/PCMig.Core/    核心引擎（Preflight / Scan / Plan / Transfer / Verify / Report / Jobs / Logging）
src/PCMig.Cli/     命令行入口 (pcmig.exe)
src/PCMig.Gui/     WPF 图形界面 (PCMig.exe)
matrix/            迁移策略矩阵 (YAML)
installer/         Inno Setup 安装脚本
docs/              使用说明 & 验证清单
```

## 构建

```powershell
# 编译
dotnet build -c Release

# 发布自包含单文件
dotnet publish src\PCMig.Gui -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist\publish-gui
dotnet publish src\PCMig.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist\publish-cli

# 打包安装程序（需要 Inno Setup 6）
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\pcmig.iss
```

## 快速开始

```powershell
# GUI
PCMig.exe

# CLI 一条龙
pcmig quick --host 192.168.1.50 --target D:\ --user OLD-PC\Administrator

# 分步
pcmig preflight --host 192.168.1.50
pcmig new --host 192.168.1.50 --source \\192.168.1.50\D$ --target D:\ --user Administrator
pcmig run --job JOB-20260913-0001
pcmig verify --job JOB-20260913-0001 --level 2
pcmig report --job JOB-20260913-0001 --open
```

## 安全边界（V1 明确不做）

凭据/Cookie（DPAPI 绑定源机）、驱动、安全软件、整盘 ACL、Windows 系统目录。
详见 `matrix/migration-matrix.yaml` 的 `securityBlockedFileNames`。

## CLI 命令一览

```
preflight  预检（连通性/共享/权限/空间）
new        创建迁移任务（含预检+扫描+计划）
quick      一条龙（预检+扫描+计划+传输）
run        执行任务
pause      协作式暂停
stop       立即暂停（终止当前 robocopy，可续传）
resume     恢复任务（不带 --job 自动选最近未完成）
status     查看进度（--watch 持续刷新）
verify     验证传输结果（--level 2 抽样哈希）
report     生成报告
list       列出所有任务
```

## 路线图

- **V1.0（当前）**：Data Plane 直拉、断点续传、暂停恢复、L1/L2 验证、报告
- **V1.5**：Outlook/浏览器 Recipe、OneDrive KFM 策略、零安装远程采集（State Plane）
- **V2.0**：Win7 源兼容模式、应用安装编排（winget）、GUI 多任务视图

## 技术栈

- C# / .NET 8（WPF GUI + CLI）
- Robocopy（文件传输引擎）
- Serilog（结构化日志）
- Inno Setup（安装打包）
- YAML（策略矩阵配置）

---

郑子轩 (Axuanbest) 个人制作。

## 发布流程

**先写日志，再打包；没写日志就打不出包。**

1. 先写三处：docs/更新日志.md（本版条目 + 版本对照表一行）、docs/使用说明.txt（本版段落）；
2. 运行：

       powershell -NoProfile -ExecutionPolicy Bypass -File "tools/release.ps1" -Version 0.2.26

   脚本会校验上面三处，缺一项直接中止；然后自动写版本号、生成 docs/更新日志.txt、
   打包安装包、交付并逐文件校验哈希。
3. 详见 docs/发布流程.md（含发版后必做的验证清单与已踩过的坑）。
