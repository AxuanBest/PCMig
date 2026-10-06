# docs\ —— 三 VM 框架文档索引

> 本目录**只放索引**，不复制大文档（避免与权威副本分叉）。

## 一、框架内文档

| 主题 | 权威位置 |
|---|---|
| 框架总览 / canonical 路径表 | `PCMig\lab\three-vm\README.md` |
| Route A 规格（验收范围与用例边界） | `E:\PCMigLab\Staging\RouteA-Spec.md` |
| 用例矩阵（Case matrix） | `E:\PCMigLab\Staging\ctl\cases\` |
| 故障注入定义 | `E:\PCMigLab\Staging\ctl\faults\` |
| VM 执行说明 | `E:\PCMigLab\VMs\` + 本框架 `configs\` 快照 |
| 场景 → 脚本 → 证据 映射 | `PCMig\lab\three-vm\scenarios\README.md` |
| 证据采集格式 | `PCMig\lab\three-vm\evidence-template\README.md` |

## 二、三 VM / Route A 的正式证据与交接（canonical）

| 文档 | 路径 |
|---|---|
| Route A 全会话交接 | `E:\PCMigLab\Evidence\RouteA\SESSION-HANDOFF-ROUTE-A-FULL-CLEAN.md`（+ `-QUICK.txt`） |
| Route A 当前检查点 | `E:\PCMigLab\Evidence\RouteA\CURRENT-ROUTE-A-CHECKPOINT.txt` |
| Route A Bug 账本 | `E:\PCMigLab\Evidence\RouteA\Route-A-Bug-Ledger.md` |
| Trust-Critical Recovery 施工指令 | `E:\PCMigLab\Evidence\Trust-Critical-Recovery\INSTRUCTION-Trust-Critical-Recovery-Campaign.md` |
| 场景 H 根因（**定论**） | `…\Trust-Critical-Recovery\SCENARIO-H-ROOT-CAUSE-20261006-REV2.md` |
| 委托链修复报告 | `…\Trust-Critical-Recovery\FIX-DELEGATION-CHAIN-20261006.md` |
| P3 三项裁决证据 | `…\Trust-Critical-Recovery\P3-DECISIONS-20261006.md` |
| C 轨道汇总 | `…\Trust-Critical-Recovery\TRACK-C-SUMMARY-20261006.md` |
| UI Closure 证据包 | `…\Trust-Critical-Recovery\UI-CLOSURE-20261005\` |

> ⚠ `SCENARIO-H-ROOT-CAUSE-20261006.md`（无 `-REV2`）的结论**已被推翻**，只作废弃留痕，不得作为定论引用。

## 三、三 VM 复验包

`E:\PCMigLab\three-vm\review-packages\`（原桌面副本，2026-10-06 归位）：
- `PCMig-ThreeVM-EnvironmentBuild-20261002-234157`
- `PCMig-ThreeVM-Phase0-Readiness-20261002-220044`

## 四、仓库侧相关知识

| 主题 | 位置 |
|---|---|
| L3 编排脚本 | `PCMig\tools\l3-*.ps1` / `l3-*.cmd` |
| VM 管理脚本 | `PCMig\tools\pcmiglab-vm.ps1` / `lab-admin-*.ps1` |
| 迁移矩阵 | `PCMig\matrix\migration-matrix.yaml` |
| 历史自动化脚本 | `PCMig\archive\scripts\` |
| 历史证据批次 | `PCMig\archive\evidence\` |
| 测试报告（公司环境） | `PCMig\docs\测试报告-公司环境.md` |
| 发版铁律 | `PCMig\docs\发版铁律.md` |