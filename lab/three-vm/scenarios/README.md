# scenarios\ —— 场景定义与核心库

> 来源：`E:\PCMigLab\Staging\recovery-gate\`（2026-10-06 **复制** 22 个脚本，**canonical 仍在原处**，此处为仓库内索引与副本）。
> 定性：**场景定义与执行能力，§6 必留**。

## 一、场景 → 脚本 → 证据 映射

| 场景 | 目标 | 脚本 | 证据位置 |
|---|---|---|---|
| **场景 G** | 30 s 性能采样 / 渲染负载 | `round3-scenario-G.ps1` | `E:\PCMigLab\Evidence\` |
| **场景 H** | 目标盘已满 → 进度真值（曾虚报 42 GiB / 99.9%） | `round3-scenario-H.ps1` | `E:\PCMigLab\Evidence\Trust-Critical-Recovery\` |
| **暂停 / 恢复连续性** | 暂停→恢复后进度不跳变、不重复计入 | `pause-resume-continuity.ps1` | 同上 |
| **停止 / 恢复连续性** | 停止→重启→采纳中断任务→恢复 | `stop-continuity-test.ps1` | 同上 |
| **case04** | 迁移结果校验（Verify） | `case04-verify.ps1` | 同上 |
| **case05** | 反复暂停 / 恢复 | `case05-repeat-pause.ps1` | 同上 |
| **case07** | 停止后恢复 | `case07-stop-resume.ps1` | 同上 |
| **case08 / 08b / 08c** | 强杀进程 → 恢复；探针；浮层 | `case08-kill-recover.ps1` | 同上 |
| **用例编排** | 逐用例运行 + 结果汇总 | `case-run.ps1` | `E:\PCMigLab\Staging\ctl\` |
| **Step1 连接** | `localhost` / 域环境共享发现 | `step1-connect.ps1` `step1-set-shares.ps1` | `E:\PCMigLab\Staging\recovery-gate\` |
| **Step1 → Step2** | 选盘 / 选共享 → 进入 Step2 | `step1-select-and-step2.ps1` | 同上 |
| **Step2 准备** | 计划生成 / 目录树 | `step2-prepare.ps1` | 同上 |
| **任意步骤跳转** | 直达 Step1–Step4 | `goto-step.ps1` | 同上 |
| **证据采集** | 诊断日志 / Loss Ledger / Flight Recorder 收集 | `collect-diag.ps1` | `E:\PCMigLab\Evidence\` |

## 二、共享库

| 文件 | 作用 |
|---|---|
| `uia-lib.ps1` | **UI 自动化主库**（窗口激活、InvokePattern 点击、ScrollPattern、断言、SELFCHECK 截图） |
| `round3-proc-lib.ps1` | 进程与作业控制库（启停、强杀、句柄、作业目录解析） |
| `manifest.ps1` | 复验包清单 / 哈希生成 |
| `pack-deliverable.ps1` | 交付物打包（**只复制不改动源文件**） |
| `pack-source.ps1` | 源码快照打包 |
| `make-dataset.ps1` | **数据集生成器**（见下） |
| `launch-app.ps1` | 启动应用（注意：其 STALE-WARNING 对 `src\…\obj\` 中间产物是**误报**） |

## 三、数据重新生成（§7 / §12：删数据、留能力）

2026-10-06 已删除 **424.48 GB** synthetic payload（`42 GB` 级 8 份 + 小文件树 + 临时 publish）。需要复跑场景时按下表重新生成：

| 原目标 | 原体积 / 文件数 | 重新生成方式 |
|---|---|---|
| `E:\PCMigLab\Staging\{phE-run2}` | 43.18 GB / 12,006 文件 | `ctl\lib-cases.ps1` 生成大规模小文件树 |
| `E:\PCMigLab\Staging\{handoff-look, phA5-gate, phA4-gate, phE-gate}` | 各 42.00 GB | `make-dataset.ps1`（6 个 bin：4×8 GiB + 1×8 GiB + 1×2 GiB + dir01–dir41 目录骨架） |
| `D:\PCMigLab-R3\{scenG, shotcheck, shotcheck2, bc-check}` | 各 42.00 GB | 同上 |
| `C:\Users\User\PCMigLab-RG\ds-large\big\*.bin` | 42.00 GB / 6 文件 | 同上（`PCMIG-DS2` 共享源，场景 H 用） |
| `C:\Users\User\PCMigLab-RG\src\big\*.bin` | 3.30 GB / 3 文件 | 同上（约 1.2 GiB ×3） |

- 目录骨架已保留（`big\` / `src\big\` 目录仍在），生成后直接落回同名路径即可被既有场景复用。
- 测试参数与结果摘要保留在 `E:\PCMigLab\Evidence\` 与 `E:\PCMigLab\Staging\ctl\cases\`。
- **小型快速回归**用 `E:\PCMigLab\smoke-data\`（57.23 MB / 179 文件，≤100 MB，见 `PCMig\lab\smoke-data\README.md`），不必生成大 payload。

## 四、纪律

1. 场景脚本改动需用户当轮明确指令（涉及 `tools\release.ps1` 同类纪律）。
2. 所有 `.ps1` 必须 UTF-8 **带 BOM**。
3. 复现证据与修复证据必须是**两类独立证据**（不同方法）；故障类连续复现 3 次 + 修复后回归 3 次。
4. 截图必须带 SELFCHECK；ROI 锚点只认 `aid=TotalImmersiveProgress`。
5. `.ps1` 若通过 `<` / `>` 重定向写中文日志，注意 PowerShell 5.1 默认编码陷阱 —— 一律用 `Out-File -Encoding utf8` 或 `[IO.File]::WriteAllText(..., UTF8Encoding($true))`。