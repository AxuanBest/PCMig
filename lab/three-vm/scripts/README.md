# scripts\ —— 用例库与文件生成器（副本）

> 来源：`E:\PCMigLab\Staging\ctl\`（2026-10-06 复制，**canonical 仍在原处**）。
> 定性：**测试能力脚本，§6 / §7 必留**。

| 文件 | 作用 |
|---|---|
| `lib-cases.ps1` | 用例矩阵库：`cases\` 下每个用例的定义、前置、判定；被 `case-run.ps1` 驱动 |
| `labfile.ps1` | 文件 / 目录夹具生成原语（造大文件、造小文件树、造中文名 / 空目录 / 只读 / 长路径 / 特殊扩展名） |

## canonical 位置

```
E:\PCMigLab\Staging\ctl\
├── cases\                    用例定义（Case matrix）
├── cases-backup-20261003-0415\
├── faults\                   故障注入（盘满 / 共享撤销 / 权限不足 / 锁文件 / 路径过长 / 域不可达）
├── host\                     宿主侧准备与共享配置
├── inbox\ outbox\ outbox-copy\ raw\ log\   证据与交换区
├── lib-cases.ps1
├── labfile.ps1
└── build-groupB-*.ps1 (16 个)  B 组构建脚本
```

## 纪律

- 所有 `.ps1` 必须 UTF-8 **带 BOM**（前三字节 `EF BB BF`），否则 PowerShell 5.1 按 GBK 解码中文并报 `Missing closing ')'`。
- 单命中 `(…).Count` 返回 `$null` ⇒ 一律用 `@(…).Count`。
- `[IO.File]::ReadAllText('相对路径')` 按进程 CWD 解析 ⇒ 一律用绝对路径。
- 生成大 payload 前先确认目标卷空间（历史教训：单份 42 GiB payload 曾同时存在 8 份，占 424 GB）。