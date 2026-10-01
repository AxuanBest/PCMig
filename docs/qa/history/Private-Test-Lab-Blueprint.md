# PCMig V0.4.6 Private Test Lab Blueprint

> 制定日期：**2026-09-19** ｜ 基线版本：**v0.4.6**（tag `v0.4.6` → `9a77381`，HEAD `d818f95`，工作区 clean）
> 制定方式：**7 个并行只读审计单元 + Captain 现场实测**。所有数字均为实测，未实测项一律标注等级。
> 本文档是**设计蓝图**，不是实施记录。**实施尚未开始**，等待用户批准。
> 证据等级词表：`RUNTIME_VERIFIED`（本次真跑）/ `CODE_VERIFIED`（仅源码确认）/ `NOT_RUN` / `BLOCKED` / `COMPANY_ONLY`

---

## 〇、本轮审计已经产出的真实运行结果（不是计划）

审计阶段**不仅仅是读代码**——已完成 3 项真实运行，证据已落盘 `J:\pcmig-lab\runs\`：

| Scenario | 结果 | 等级 | 关键证据 |
|---|---|---|---|
| **S-FAT32-PREFLIGHT-01** | ✅ 真实拦截 | **RUNTIME_VERIFIED** | 目标 `D:\`（真实 32GB FAT32）→ `✘ 目标盘文件系统 FAT32: D:\ 是 FAT32：单文件不能超过 4GB…`，`exit=1`，`总体: 存在阻断项` |
| **SMB-LOOPBACK-T01** | ✅ 真实直拉成功 | **RUNTIME_VERIFIED** | `JOB-20260919-140131-c578`：UNC 源 `\\AXUAN\J$\…` → 本地目标，2 对象完成，**逐文件 SHA256 全一致**，目录结构保留，`exit=0` |
| **S-PREFLIGHT-FULL-01** | ✅ 7 项检查真实执行 | **RUNTIME_VERIFIED** | TCP 445 通 / IPC$ 会话建立 / NetShareEnum 发现 12 共享 / robocopy 10.0.26100.8875 可用 |

**同时发现 3 个真实缺陷/陷阱**（详见 §五）。

---

## 一、审计结论：现有体系盘点

### 1.1 64 个单元测试（`dotnet test PCMig.sln -c Release` 实跑 → 64 通过 / 0 失败 / 1 秒）

| 文件 | 用例 | 真实日志样本 | 构造字符串 | 真实进程/文件系统 | 纯逻辑 |
|---|---|---|---|---|---|
| `GuardTests.cs` | 20 | 0 | 15 | 0 | 5 |
| `RealWorldLogSampleTests.cs` | 8 | **5** | 3 | 0 | 0 |
| `QuoteArgTests.cs` | 7 | 0 | 7 | 0 | 0 |
| `RobocopyEndToEndTests.cs` | 2 | 0 | 0 | **2** | 0 |
| `RoboParsingTests.cs` | 8 | 0 | 6 | 0 | 2 |
| `StoreAndLockTests.cs` | 6 | 0 | 1 | **5** | 0 |
| `PhaseViewTests.cs` | 8 | 0 | 0 | 0 | 8 |
| **合计** | **64** | **5** | **32** | **7** | **15** |

**结论：真实日志覆盖度 5/64；真实运行时覆盖度 7/64；GUI 层 0。** 现有测试是好的"防错网"，但**不构成环境验证**。

### 1.2 `tools\stability-test.ps1` 已覆盖与关键缺陷

**已覆盖**：P1 完整迁移（自写逐文件 SHA256 + `verify --level 2`）、P2 硬杀进程→resume、P3 篡改→重跑。

**12 条缺陷（审计发现，全部带行号）**：

| # | 级别 | 缺陷 | 行号 |
|---|---|---|---|
| D1 | 高 | **P2 假 PASS**：`Start-Sleep 600ms` 写死；L128 自认"未真正打断"，L135 仍判 PASS | L126-136 |
| D2 | 高 | **P3 假 PASS**：`WriteAllBytes` 顺带更新 LastWriteTime → robocopy 默认就重拷，**验证不到修复能力** | L142-150 |
| D3 | 中 | `verify` 判定只查 `✘` 不查 `⚠` → L2 抽样 0 项（内容级空转）被判 PASS | L111 |
| D4 | 中 | `Compare-Tree` 只枚举 `-File` → **空目录/目录结构完全不断言** | L24-25 |
| D5 | 中 | `hashLimitMb=16` → **300MB 大文件只比长度，内容损坏测不到** | L23/L32 |
| D6 | 中 | O(n²) 比对 → 10 万文件不可用 | L26-30 |
| D7 | 中 | 退出码陷阱：`CompletedWithErrors`（含盘满）**也返回 0** | L102 |
| D8 | 中 | **无 job 隔离** → 任务落进生产 `%ProgramData%\PCMig\Jobs`（本机已有 25 个历史 Job） | 全文 |
| D9 | 低 | 报告不可审计：缺 Scenario ID / Run ID / 环境快照 / 期望 vs 实际 / Cleanup 结果 | L160-163 |
| D10 | 低 | `Remove-Item $Root -Recurse -Force` 无条件删除，`-Root` 可指向任意路径 | L90 |
| D11 | 低 | 大文件生成 `$fs` 无 try/finally；`BulkMb` 非 4 倍数时实际偏小且无断言 | L73-79 |
| — | 正确 | JOB-ID 正则、两个 `.ps1` 的 BOM、全 `.cs` 零控制字符 | L83/L120 |

### 1.3 工具链与运行环境（实测）

| 项 | 值 |
|---|---|
| 主机 | **AXUAN**，Windows 11 专业版 build **26200** |
| CPU / 内存 | AMD Ryzen 5 9600X 6C/12T ｜ 总 31.1 GB / 可用 **13.5 GB** |
| PowerShell | **5.1.26100.9444**（`.ps1` 必须 UTF-8 **带 BOM**） |
| dotnet | SDK 8.0.425 |
| 当前权限 | **Medium 完整性级别（未提权）**；`Get-VM` / `New-VHD` / `New-SmbShare` 会被拒 |
| 磁盘 | C 118.6 / D **FAT32 24.5** / E 130.1 / F 129.8 / **G 177.6** / H 27 / I **75.97** / **J 128** / K 80（GB 空闲）|
| SMB 服务 | `LanmanServer` **Running**、`LanmanWorkstation` Running |
| 已导出共享 | **13 个**：`ADMIN$ C$ D$ E$ F$ G$ H$ I$ J$ K$ IPC$ Users 项目` |
| 当前 SMB 会话 | **0**（干净起点）｜ `net use` 空 ｜ `EnableInsecureGuestLogons=False` ｜ `LimitBlankPasswordUse=1` |

> ⚠ **绝对禁碰**：`Users`(=C:\Users) 与 `项目`(=H:\项目) 是**真实数据共享**。

> ⚠ **一处过时数据已更正**：交接文档记载 I 盘仅剩 17.9 GB —— 实测现为 **75.97 GB**（两个 API 一致复核）。磁盘条件比文档记录宽松。

### 1.4 lab 历史资产复用判定

| 资产 | 判定 |
|---|---|
| `lab\answer\autounattend.xml` + `New-AnswerIso.ps1` | ✅ **可复用**：自动装 Win10、计算机名 `OLDPC-WIN10`、开共享防火墙组、**`LocalAccountTokenFilterPolicy=1`**（允许本地管理员远程访问——正是 PCMig 需要的对端配置）。⚠ 含明文密码，扩展前必须处理 |
| `lab\vm-gen-massdata.ps1` | ✅ **可复用思路**：VM 内造 5 万小文件 / 40 层深路径 / 持锁进程 |
| `lab\diskpart.txt` | ✅ 可复用（EFI+MSR+主分区模板） |
| `lab\fill-e.ps1` | ❌ **不可复用且有风险**：会把**真实物理 E 盘**填到剩 150MB。只保留 `fsutil createnew` + "填充到剩 N MB"算法思路 |
| `lab\unlock.ps1` | ⚠ **危险**：`Stop-Process` 杀掉所有非自身 powershell。不可直接复用 |
| `lab\uia-changelog.ps1` | ✅ **可复用**：UIA 通过 `AutomationId` Invoke 实测成功（见 §五-3 的方法学缺陷） |
| `lab\answer.iso` | ✅ 保留（应答盘，非安装源）|
| `G:\HyperV\PCMig-OldPC\PCMig-OldPC.vhdx` | ⭐ **重大发现**：**18.0 GB 既有 Hyper-V 虚拟机**（12.0 GB 基盘 + 18.0 GB avhdx 差分盘 + 快照 + VMRS）。这是历史上真实建过的 PCMig 测试 VM |
| `E:\Documents\Virtual Machines\` 8 台 VMware VM | ⚠ 见 §七 |

---

## 二、Lab 目录结构（设计）

**不放在项目工作区内**（避免污染 git 与 4 GB 的 dist）。统一落在 **`J:\pcmig-lab\`**（128 GB 空闲、无任何既有占用、与 VM 盘物理分离）：

```
J:\pcmig-lab\
├─ scenarios\        # 场景定义（每场景一个 .ps1 或 .json 描述）
├─ lib\             # 公共模块（Compare-Tree / 数据生成 / 证据落盘）—— 唯一实现，禁止复制
├─ fixtures\        # 证据素材（真实日志、编码标注）
│   └─ robocopy\    # GBK/936 无 BOM 的真实日志原件 + *.encoding.txt
├─ test-data\       # 造数据根（按 Scenario 分子目录，用后清理）
├─ vhd\             # VHD/VHDX 专用目录（路径白名单唯一允许位置）
├─ smb\             # SMB 场景源/目标
├─ jobs\            # ⚠ 所有 CLI 调用必须 --jobs 指向这里，隔离生产 jobs 根
├─ runs\            # 证据（每 Run 一个目录，见 §四）
├─ reports\         # 汇总报告
├─ screenshots\     # GUI 基线截图（PNG，不入 git）
└─ cleanup\         # 清理脚本与清理记录
```

**已创建并已使用**：`J:\pcmig-lab\runs\20260919-140131-SMB-LOOPBACK-T01\`（含 `environment.json` / `command.txt` / `pcmig.log` / `robocopy-object-00000{1,2}.log` / `evidence\`）。

**结构决策 vs 用户建议**：用户提议的 `I:\PCMig-TestLab\` **不采用** —— 理由：① I 盘含 17.3 GB 备份镜像，磁盘满测试有挤爆备份的风险；② J 盘 128 GB 完全干净、与 G 盘 VM 物理分离、与 E 盘 TEMP 分离，避免 IO 互相污染。

---

## 三、统一执行框架（所有 Scenario 强制契约）

### 3.1 生命周期（五段，缺一不可）

```
Setup → Run → Verify → Evidence → Cleanup
```

| 段 | 失败时的行为（硬规定） |
|---|---|
| Setup FAIL | **绝不进入 Run**，整体记 `BLOCKED` |
| Run 未真正触发故障 | 记 **`NOT_RUN`**（不是 PASS！） |
| Verify FAIL | 整体记 `FAIL` |
| Evidence 缺失 | 整体记 `FAIL`（无证据＝没测） |
| Cleanup FAIL | **单独字段** `result.cleanup=failed` 记录 + 保留现场，**不掩盖主结果** |

### 3.2 结果类型（七选一，禁止混用）

`PASS` / `FAIL` / `NOT_RUN` / `BLOCKED` / `CODE_VERIFIED` / `RUNTIME_VERIFIED` / `COMPANY_ONLY`

**硬禁令**：源码正确 ≠ PASS；Mock 通过 ≠ 真实通过；脚本执行完 ≠ 场景验证成功。

### 3.3 七条全局强制规格（每条都是源码里踩出来的）

1. **所有 CLI 调用必须带 `--jobs <lab>\jobs`** —— 否则污染生产 jobs 根（本机已有 25 个历史 Job），且无参 `resume` 会选错历史任务。
2. **禁止用退出码判成功** —— `Program.cs:570`：`Completed` 与 `CompletedWithErrors`（含磁盘满）**都返回 0**。判定一律读 `job-state.json` 的 `phase` + `LastError` + 回执。
3. **打断必须"到达区间才动手"** —— 轮询已传字节到源总量 10%–60% 才 kill/pause；若 3 秒内已 100% → 记 `NOT_RUN`（不是 PASS），并提示增大数据集。
4. **`pause`/`stop` 是异步的** —— 只写文件就返回（`CmdPause` 恒返回 0）→ 必须轮询 `job-state.json` 直到 phase 变化或超时，超时记 FAIL。
5. **硬杀场景必须断言"无孤儿 robocopy"** —— 12 秒窗口内 `Get-Process robocopy` 为空 + CLI 日志无 `AssignProcessToJobObject 失败` + 目标字节冻结 + `status` 显示 Interrupted + `job.lock` 可独占获得。
   > ⚠ `ProcessJobGuard.cs:89` 挂接失败时**静默无日志** → 必须显式断言日志。
6. **清理必须做基线比对** —— SMB 共享数/名字集合、`Get-SmbConnection` 数、`net use` 条目、VHD 挂载、测试用户，全部与 Setup 前快照比对，不一致报 `CLEANUP_FAIL`。
7. **危险资源硬禁** —— 脚本头部断言拒绝 `-Root` 落在 `C:\Users` / `H:\项目` / `\\AXUAN\Users` / `\\AXUAN\项目`；禁止对非 `PCMigLab*` 前缀执行 `Remove-SmbShare`；禁止操作 C:–K: 真实盘的挂载/格式化。

### 3.4 证据目录模板

```
runs\<yyyyMMdd-HHmmss>-<SCENARIO-ID>\
├─ environment.json     # 时间/主机/OS/各盘剩余/CLI路径+SHA256/版本/git HEAD/是否管理员
├─ scenario.json        # Scenario ID / 期望结果 / 判定条件
├─ setup.json           # Setup 结果
├─ command.txt          # 执行的命令行原文
├─ pcmig.log            # CLI stdout+stderr 全量
├─ robocopy-*.log       # 从 <job>\logs\robocopy\ 复制的原文（GBK）
├─ job\                 # job.json / job-state.json / plan.json / receipts\ / preflight.json
├─ verify.json          # 独立校验结果
├─ result.json          # 七种结果类型 + 每项断言 pass/fail
├─ cleanup.json         # 清理结果 + 基线比对
└─ screenshots\         # 需要时的截图
```

**`environment.json` 已实现并落盘示例**见 `runs\20260919-140131-SMB-LOOPBACK-T01\`。

---

## 四、Scenario 总表（按批次，含 Environment Level）

**Environment Level 定义**（用户新增要求）：
`L0` Unit ｜ `L1` Local PC ｜ `L2` Local SMB/VHD ｜ `L3` Corporate Simulation ｜ `L4` Real Company

| Batch | Scenario ID | 内容 | Level | 需要提权 | 当前状态 |
|---|---|---|---|---|---|
| **B0** | `UNIT-01` | 64 单测持续回归（release 闸门 0） | L0 | 否 | ✅ 已存在，RUNTIME |
| **B0** | `FIX-01` | 真实日志 Fixture 建库（112/82/59/2 原件入库） | L0 | 否 | ⏳ 素材已定位 |
| **B0** | `FIX-02` | 正则/控制字符防复发静态回归 | L0 | 否 | ⏳ 设计已定 |
| **B1** | `LOCAL-BASE-01` | 本地基线迁移 + MixedDataset（第九节 14 项全要素） | L1 | 否 | ⏳ 待实施 |
| **B1** | `SMB-LOOPBACK-01` | UNC 直拉基线（管理共享） | **L2** | 否 | ✅ **RUNTIME_VERIFIED** |
| **B1** | `SMB-PREFLIGHT-01` | 预检 7 项检查 | **L2** | 否 | ✅ **RUNTIME_VERIFIED** |
| **B2** | `FAT32-PREFLIGHT-01` | FAT32 目标拦截 | **L2** | 否 | ✅ **RUNTIME_VERIFIED** |
| **B2** | `DISKFULL-01` | 小容量 VHDX 真实盘满 → 熔断 → 释放 → Resume | **L2** | **是** | ⏳ 设计完成 |
| **B2** | `FAT32-DIRFULL-01` | FAT32 单目录 2 万文件上限（错误 82） | L2 | 是 | ⏳ |
| **B3** | `KILL-01/02` | 硬杀 PCMig / 杀 robocopy | L1 | 否 | ⏳ |
| **B3** | `NET-RECOVER-01` | SMB 共享消失 → 恢复 → Resume | **L2** | 部分 | ⏳ |
| **B4** | `PERM-01` `FSBOUND-01` | 权限 / 文件系统边界 | L1-L2 | 部分 | ⏳ |
| **B5** | `SCALE-01/02` | 大文件 1/5/10GB＋10 万小文件 | L1-L2 | 否 | ⏳ |
| **B6** | `GUI-SMOKE-01` `DPI-01` | GUI Smoke + 高 DPI | L1 | 否 | ⏳ |
| **B7** | `COMPOSITE-01` | 组合故障 | L2 | 视情 | ⏳ |
| **B8** | `ONEDRIVE-01` | 占位符（`Offline` 位可本地造） | L1 | 否 | ⏳ |
| **L3** | 见 §七 | Corporate Simulation | **L3** | 是 | ⏳ 需授权 |
| **L4** | 见 `..\First-Day-Company-Test-Checklist.md` | 企业环境 | **L4** | — | COMPANY_ONLY |

---

## 五、本轮发现的问题（3 个，全部尚未修改任何代码）

### Candidate Bug #1 —— Error/Warning 弹窗**永久失效**（P0 候选）

**证据等级：CODE_VERIFIED + 真实历史 RUNTIME 证据（本次现场核实）**

| 环节 | 事实 |
|---|---|
| `AppDialog.xaml.cs:52-65` | Error → `"ErrorTextColor"`、Warning → `"WarnTextColor"`、其余 → `"AccentColorBrush"`；**L65 用 `FindResource(brushKey)`** |
| 三套主题实测 | `Glass.xaml` / `Glass.Dark.xaml` / `Classic.xaml` 各自 **152 个 key**，但 **`ErrorTextColor` 与 `WarnTextColor` 命中 = 0**（三个文件全部 0）|
| WPF 语义 | `FindResource` 找不到 key **直接抛 `ResourceReferenceKeyNotFoundException`**（`TryFindResource` 才返回 null） |
| 后果 | `ApplyContent` 在 L65 中断 → `ShowDialog()`(L40) 从未执行 → `Result` 保持 `None` |
| 真实日志证据 | `%ProgramData%\PCMig\Logs\app-20260918.log` **同一会话内连续 10 次** `ResourceReferenceKeyNotFoundException: 未找到"WarnTextColor"资源`（L1316–1361）|
| 崩溃日志证据 | `crash-20260918-233603.log` 与 `crash-20260918-234105.log` **两份**，内容均为该异常、位置=界面线程、致命 False |
| 影响面（**5 个调用点**） | ① `MainViewModel.cs:811-812` 迁移前提醒（**Information，不受影响**）② **`MainViewModel.cs:829-831` 目标盘空间不足**（Warning → 异常 → 静默走"已取消"，异常被 L840 catch 吞成**误导性文案**"⚠ 目标盘剩余空间预检未能完成"）③ **`MainViewModel.cs:1188-1190` 扫描不完整**（Warning）④ `MainViewModel.cs:715-717` / `2024-2026` 未完成任务询问（Question，不受影响）⑤ **`App.xaml.cs:99-104` 全局崩溃兜底**（非致命传 Warning、致命传 Error → **兜底弹窗自己抛异常 → 被 L106 `catch { }` 静默吞掉 → 崩溃提示永远弹不出来**）|

**处置建议**：走用户第二十一节 7 步流程（复现 → 保存证据 → 定位 → **先加回归测试** → 修复 → 重测 → 再决定版本）。修复有二选一（补齐 3 套主题 key / 改 `TryFindResource` + 兜底）。**本审计未改任何代码。**

### Candidate Bug #2 —— UNC 目标被 CLI 判死、GUI 放行（行为不一致）

**证据等级：RUNTIME_VERIFIED（Agent B 两次独立复现 + 历史上 2026-09-19 真实 job 印证）**

```
pcmig preflight --target \\AXUAN\J$\x
✘ 目标盘检查: Drive name must be a root directory (i.e. 'C:\') or a drive letter ('C'). (Parameter 'driveName')
总体: 存在阻断项      EXIT=1
```

- 根因（CODE_VERIFIED）：`PreflightChecker.cs:239-241` 对任何 target 都执行 `new DriveInfo(Path.GetPathRoot(...))`，**无 UNC 分支** → 抛 `ArgumentException` → catch 落 `:269` 记 `"Error"` → `OverallPass=false`（`:300`）。
- **GUI 行为不一致**：`MainViewModel.cs:823-841` 同样调用但吞异常**只警告不阻断**。
- `docs\` 中**零记录** → 不是已知限制。
- 影响：GUI 目标由 `FolderBrowserDialog` 选择（选不到 UNC），真实 GUI 用户几乎不会撞；**CLI/脚本用户会撞**。
- **处置：按纪律只报告，不擅自改产品行为。** 属"设计未定义"边界，需用户拍板。

### Candidate Bug #3 —— 测试体系自身的"假 PASS"（3 处）

| 处 | 事实 |
|---|---|
| `stability-test.ps1` P2 | 打断点写死 600ms，自认"未真正打断"仍判 PASS |
| `stability-test.ps1` P3 | `WriteAllBytes` 顺带改时间戳 → robocopy 默认就重拷 → **验证不到 PCMig 修复能力**。真测须**恢复原 LastWriteTime** |
| `pcmig verify --level 2` | 本次实测：L2 抽样 **0 项**，CLI 明确打印 `⚠ 未抽样任何对象：本次内容级（SHA-256）校验一个文件都没有抽到`，**但退出码 0、无 `✘`** → 而 `stability-test.ps1:111` 只查 `✘` → **"内容级校验空转"被判 PASS** |

**另注**：CLI 路径下"内容损坏但大小+时间戳未变"**没有修复通道** —— `ForceOverwriteFromSource` 全仓库唯一赋值点是 `MainViewModel.cs:1707`（GUI「尝试修复」）；CLI 的 `quick`/`run`/`resume` 从不设置它。这是**既有能力差异，不是本轮缺陷**，但测试设计必须知道。

### 一条被更正的旧结论（重要）

**磁盘满（错误 112）的真实原始 robocopy 日志一直存在** —— 在 `C:\ProgramData\PCMig\Jobs\` 下 **21 个历史 job / 118.7 MB 真实日志**中。交接文档 §21.8-1 说"未留存"**应更正**。

实测真实错误码分布（GBK/936 解码）：

| 错误码 | 真实次数 | 原文样例 |
|---|---|---|
| **112** `0x70` 磁盘空间不足 | **18** | `2026/09/14 00:22:00 错误 112 (0x00000070) 正在复制文件 \\192.168.134.131\E$\迁移全量测试\大文件\大文件-2.bin` + `磁盘空间不足。` |
| **82** `0x52` 无法创建目录或文件 | **33849** | `错误 82 (0x00000052) 正在复制文件 …pic_021845.jpg` + `无法创建目录或文件。` + `错误: 超过重试限制。` |
| **59** `0x3B` 网络意外错误 | 10 | `错误 59 (0x0000003B) …` |
| **2** `0x02` 找不到文件 | 2 | `错误 2 (0x00000002) …` |
| 5 / 32 / 39 / 67 | **0** | — |

⚠ **编码关键**：`…\logs\robocopy\*.log` 是 **OEM/GBK 无 BOM**（前 8 字节 `0D 0A 2D 2D…`）。用 `-Encoding UTF8` 读会**全部漏检**。Fixture 入库**禁止转 UTF-8 后再断言**。

⚠ **时序警告**：那份 112 日志来自 **2026-09-14**，而磁盘满熔断（`_spaceCode`）是 **v0.3.6（09-16）** 才引入的 → 它证明的是"熔断引入**前**的真实现象"，**不能**用来判定当前熔断是否有效。熔断必须用 **v0.4.6 二进制 + 小容量 VHD 重新制造**。

---

## 六、关键测试学限制（必须写进每个 Scenario 的注释）

### 6.1 ★ 回环认证绕过 —— 最容易造出"假 PASS"的地方

实测：
1. `--user AXUAN\axuanbest --password <口令>` → **`已建立会话 \\AXUAN\IPC$`，预检通过**。
   → 同机回环由**登录令牌**认证，**密码完全不参与校验**。
2. `--user AXUAN\NoSuchUser99` → 真 `Win32Error=1326`（这一条是真的）。

**推论**：
- **能**真实覆盖：UNC 解析 / TCP 445 / SMB 会话建立 / NetShareEnum / robocopy 网络分支 / 1326 文案 / "共享不存在"文案。
- **不能**覆盖：**密码校验**、**1219 真实冲突**、**IPC$ 缺失(67)**、**域认证**。
- 🚨 **假 PASS 陷阱**：回环下源路径永久可达，`NetworkShare.cs:307` 的"源均可访问→依赖现有连接"会让**凭据全错**时预检仍"通过"。**绝不能把"凭据场景预检通过"当成凭据生效的证据。**

### 6.2 IPC$ 判定方法（避免误报）

`Test-Path \\AXUAN\IPC$` = **False** 但**不是故障** —— IPC$ 非文件系统路径。PCMig 用 `WNetAddConnection2` 连它**实测成功**。脚本断言**禁止**用 `Test-Path` 判 IPC$ 可用性。

### 6.3 其他实测确认的陷阱

| 项 | 事实 |
|---|---|
| `stop`(Immediate) 的终态 | `TransferOrchestrator.cs:326-329`：`File.Exists(pause.request) ? Paused : Interrupted` → **`stop` 终态是 `Paused`；硬杀才是 `Interrupted`**（与测试报告 §20 的表述不一致，**以代码为准**） |
| robocopy ExitCode | `1` 是**成功**（`IsSuccess: 0<=code<8`）；**9** = 有文件失败 → 非 Success；**16** = 严重错误 |
| `/J` 与 `/Z` 通道 | 大文件阈值默认 **512MB**；`/J` 需"任一端网络路径"**且**走 Large 通道。20MB 样本**不含 `/J` 是正确的** → 取 `/J` 证据必须造 **≥512MB** 文件 |
| `Compare-Tree` 哈希上限 | 默认 **16MB** → 300MB/10GB 场景"完整性 PASS"**在内容层面是空的**。**这是当前最可能产生假 PASS 之处，必须先修** |
| `Verifier` L2 抽样 | 注释称"大文件必抽"，实现是**纯均匀路径哈希**（`StableHash(相对路径)%100<1`）→ 512MB 大文件命中率仅 ~1%。**注释与实现不符**，是否算 Bug 待用户判定 |
| PS 5.1 长路径 | `LongPathsEnabled=1` 时 **305 字符路径 `Test-Path`/`Get-FileHash`/`Get-ChildItem` 全部成功** → 测试工具**不需要** `\\?\` 前缀（与交接文档 §11.2 的警告方向相反，属本次新增结论） |
| `uishot.ps1` | 工作区文件**无 BOM**（前三字节 `70 61 72 61`），与铁律 8 不符；但 `git status` clean（**仓库原样，非本会话改动**），全 ASCII，PS 5.1 下功能不受影响（仅记录） |
| `uishot.ps1` 行为 | **会自动启动 GUI**（L22-27）→ 回归脚本必须自己管进程生命周期；`Px` 采样无边界检查（当前尺寸下低风险）；`GetWindowRect` 含 DWM 阴影 |

---

## 七、L3 Corporate Simulation 拓扑（可执行设计）

### 7.1 环境实测能力（本机）

| 项 | 实测 | 判定 |
|---|---|---|
| Hyper-V | **全栈已启用**：`Microsoft-Hyper-V-All`=1、`vmms` **Running**、`vmcompute` Running、`HvHost` Running | ✅ 可用（需提权管理）|
| Hypervisor | **`HypervisorPresent=True`** | 权威判据为真 |
| SLAT / VMMonitorMode | WMI 显示 False | ⚠ **不是硬件缺陷** —— Hyper-V 已接管时 Windows 隐藏这两项 |
| 现有 Hyper-V VM | `C:\ProgramData\Microsoft\Windows\Hyper-V` 为空，**但 `G:\HyperV\PCMig-OldPC\PCMig-OldPC.vhdx` 存在（18.0 GB + avhdx 差分 + 快照 + VMRS）** | ⭐ **既有资产，待注册** |
| VMware | 注册表 26.0.0，**但 `C:\Program Files\VMware\VMware Workstation` 目录不存在**（无 `vmware.exe`/`vmrun.exe`）｜服务与驱动仍在运行｜VMnet1 192.168.219.1/24 Up、VMnet8 192.168.134.1/24 Up | 🔴 **半损坏：现在无法启动任何 VMware VM** |
| VirtualBox / Docker / Podman / Windows Sandbox | **全部未安装** | — |
| WSL | 已装 wsl.exe，**无任何发行版** | — |
| Windows 安装介质 | ✅ **已有**：Win11 25H2 (8.54 GB)、Win11 24H2 商业版 (7.73 GB)、**Win10 22H2 商业版 (6.99 GB)** | 客户端 VM 可建 |
| **Windows Server 介质** | 🔴 **没有**（全盘 iso/esd 扫描无 Server 版本）| **AD/DNS/GPO 的前置阻塞** |
| 宿主网络 | 有线 192.168.1.250/24（家用路由 NAT 后），WLAN Disconnected，DNS 192.168.1.1 | 隔离可行 |

### 7.2 推荐拓扑（**两阶段，不一次上 4 台**）

**阶段 A（L2+，0 台 VM，立即可做）**：宿主自带 SMB 服务即对端

```
[宿主机 AXUAN]  ──  SMB 服务端（13 个共享 / 管理共享）
                 └─ 客户端：PCMig CLI / GUI + VHD 目标盘
```
覆盖：SMB 全链路、显式凭据（1326/不存在用户）、管理共享、ACL、共享消失/恢复、CFA、VHD（FAT32/NTFS/盘满）、Resume、Kill。
**成本：0 GB / 0 授权。收益最高。**

**阶段 B（L3，2 台 VM —— 推荐起点）**

```
        ┌──────────────── host-only：192.168.219.0/24（VMnet1，已就绪）──────────┐
        │                                                                        │
   ┌────┴─────┐        ┌──────────┐        ┌──────────────┐                      │
   │  DC01    │◄──────►│  FS01    │◄──────►│  PC01(源)     │                      │
   │ AD+DNS   │        │ SMB+ACL  │        │ Win10/11 客户端│                     │
   │PCMigLab  │        │ 管理共享 │        │ 被 PCMig 拉取 │                      │
   │  .local  │        │ Share    │        └──────────────┘                      │
   └──────────┘        └──────────┘                ▲                             │
                                                    │ SMB 直拉                    │
                                           ┌────────┴───────┐                     │
                                           │  PC02 / 宿主    │                     │
                                           │  运行 PCMig     │                     │
                                           └────────────────┘                     │
        └────────────────────────────────────────────────────────────────────────┘
                    宿主 192.168.1.x 家用网络完全不受影响（不用 Bridged）
```

| 节点 | 角色 | 规格 | 来源 |
|---|---|---|---|
| **DC01** | **AD 域 + DNS**（`PCMigLab.local`）｜域用户 `PCMigTestUser` / `PCMigAdmin` / `PCMigOperator`｜OU `PCMig-Clients` / `PCMig-Servers` | 2 vCPU / 2–3 GB / 40 GB | **需 Windows Server 评估版**（本机没有）|
| **FS01** | 文件服务器：`\\FS01\PCMigShare`（普通共享）+ 管理共享 `D$`（Win Server 默认导出）｜NTFS ACL 模型 | 2 vCPU / 2 GB / 40 GB | 同上 |
| **PC01** | **源电脑**（被拉取端）：Win10/11 客户端，加域，装被迁移的用户数据 | 2 vCPU / 4 GB / 60 GB | ✅ **本机已有 Win10/Win11 ISO** |
| **PC02** | **目标电脑**（跑 PCMig）：`Win11 Client + 宿主 SMB 服务` —— **或用宿主直接扮演** | 2 vCPU / 4 GB / 60 GB | ✅ 已有 ISO；**也可用宿主节省一台** |

**网络**：**VMnet1（host-only, 192.168.219.0/24）** —— 天然隔离、不干扰宿主上网、与宿主网段 192.168.1.x 无冲突。
⚠ 不要把 VM 接入宿主 192.168.1.x，不要用 Bridged。

**资源**：2 台仅需 **6–8 GB 内存**（当前可用 13.5 GB ✅）；磁盘用 dynamic 精简后实际约 **80 GB**（放 `G:\PCMigLab\VMs\`，177.6 GB 空闲，与 J 盘测试数据物理分离）。

**为什么 2 台足够**：据审计测算，**2 台（Server DC+FS ＋ Client）即可覆盖约 90% 可模拟企业特征**；4 台需 12–16 GB 内存，**必须串行启动**，而增量收益仅约 5%。

### 7.3 L3 专属 Scenario（必须在 L3 真实跑，不可用回环替代）

| # | Scenario | 验证什么 | 优先级 |
|---|---|---|---|
| L3-01 | 域用户凭据直拉：`--user PCMigLab\PCMigTestUser --password <真实密码>` 拉 `\\FS01\PCMigShare` | **真实密码校验**（回环做不到）、`DOMAIN\user` 字符串格式 | **P0** |
| L3-02 | 错误域密码 | 1326 真实路径 + 文案 | **P0** |
| L3-03 | 管理共享 `\\FS01\D$`（域管理员 vs 普通域用户） | 管理共享权限模型、UAC 远程令牌过滤 | **P0** |
| L3-04 | DNS：`\\FS01`（短名）与 `\\FS01.PCMigLab.local`（FQDN）双路径 | `PreflightChecker.cs:24-65` 的 DNS 多地址解析分支 | **P0** |
| L3-05 | NTFS ACL：`icacls /deny` 于 FS01 子目录 | 权限拒绝（错误 5）真实分支 + 扫描残缺阻断 | **P0** |
| L3-06 | **1219 真实冲突**：先用凭据 A 连，再用凭据 B | `NetworkShare.cs:185-199` 复用而非杀会话（公司血泪教训） | **P0** |
| L3-07 | **共享消失 → 恢复 → Resume**：`Remove-SmbShare` / 停 `LanmanServer` | 状态机 Interrupted → Resume 完整性 | **P0** |
| L3-08 | 域 GPO：UAC 令牌过滤、防火墙、PS 执行策略 | PCMig 在受策略约束客户端上的行为 | P1 |
| L3-09 | PC01 普通域用户（非管理员）跑 PCMig | 非管理员下的迁移能力边界 | P1 |
| L3-10 | Defender Controlled Folder Access | 写保护拦截行为 | P1 |
| L3-11 | 网络分段：SMB 通/DNS 不通、DNS 通/SMB 不通 | 预检各分支定性 | P1 |

### 7.4 L3 硬约束

1. **隔离**：测试域**绝不加入真实工作域**；真实公司账号**绝不进入测试域**；Bridged 网卡**禁用**。
2. **不降低安全**：模拟**限制**而非关闭安全功能。EDR / 内核级安全软件**不安装**（用户已明确禁止）。
3. **可销毁**：全部 VM 用 dynamic 盘 + **checkpoint**，可一键回滚到干净基线。
4. **不假装等同公司**：L3 的结论**只能**表述为"已尽可能接近并覆盖 Windows 企业环境中可复现的部分"，**绝不**声称等于公司环境。

---

## 八、提权需求清单（当前会话未提权，Medium 完整性）

| 操作 | 需管理员 | 实测依据 |
|---|---|---|
| 访问 `\\<本机名>\J$` 管理共享（回环） | **不需要** ✅ | 本次实测：SMB Loopback 全链路跑通、exit=0 |
| `Get-SmbShare` / `Get-LocalUser`（只读） | **不需要** ✅ | 实测成功 |
| `New/Remove-SmbShare`、`Grant/Revoke-SmbShareAccess` | **需要** | SMB 服务端配置 |
| `New/Remove-LocalUser` | **需要** | 本地账户库 |
| `New-VHD` / `Mount-VHD` / `New-Partition` / `Initialize-Disk` / `Format-Volume` | **需要** | 存储栈 |
| `Get-VM` / `New-VM` / Hyper-V 私有交换机 | **需要** | 实测 `Get-VM` 报 "You do not have the required permission" |
| 改 `HKLM`（LanmanWorkstation / Lsa / Defender 排除） | **需要** | — |
| 改显示缩放（高 DPI 测试） | **不需要**（但**必须注销会话**） | `app.manifest` = `true/PM` |

---

## 九、需要用户决策的事项（阻塞项）

| # | 事项 | 为什么阻塞 |
|---|---|---|
| **1** | **是否提权执行 B2 批次**（VHD：FAT32 / Disk Full） | `New-VHD`/`Mount-VHD`/`Format-Volume` 全部需管理员；本会话审批已禁用，不会请求提权。→ 三选一：(a) 你手动管理员 PowerShell 跑我给的脚本；(b) 你另起提权会话；(c) 先做免提权批次 |
| **2** | **Candidate Bug #1（WarnTextColor）如何处置** | 涉及生产代码修改 → 需批准走 7 步流程；且**必须先加回归测试** |
| **3** | **Candidate Bug #2（UNC 目标 CLI/GUI 不一致）是否算 Bug** | 属"设计未定义"边界，需拍板是否拦截/是否统一 |
| **4** | **测试根 `J:\pcmig-lab\` 是否确认**（替代你提议的 `I:\PCMig-TestLab\`） | 理由：I 盘有 17.3 GB 备份镜像，磁盘满测试会挤爆备份区 |
| **5** | **Defender 排除是否允许**（`Add-MpPreference -ExclusionPath`） | 10 万文件场景下 Defender 实时扫描是最大瓶颈；但**这会改本机安全配置** |
| **6** | **L3 平台与介质**：(a) 是否允许下载 Windows Server 评估版 ISO（无它则 AD/DNS/GPO 全做不了）；(b) 是否重装 VMware Workstation（现无 `vmware.exe`，8 台旧 VM 无法启动）还是统一用 Hyper-V；(c) 是否允许注册已有的 `G:\HyperV\PCMig-OldPC` VM | L3 前置条件 |
| **7** | **高 DPI 测试窗口** | 125%/150%/200% 切换**需要注销会话**（会中断你当前的 DSH 会话），必须安排独立时间窗 |
| **8** | **Agent B 保留的 4 个探测 job 是否保留** | `JOB-20260919-135802-b0c4`（UNC 目标阻断证据）、`-135808-f031`（直拉成功）、`-135857-50b5`（不存在共享）、`-135946-e813`（**含 robocopy 命令行证据**） |

---

## 十、实施顺序（批次，每批留证据再进下一批）

| 批 | 内容 | 提权 | 预估 |
|---|---|---|---|
| **B0** | 64 单测回归 + Fixture 建库（112/82/59/2 真实日志入库 + 反例集）+ 正则防复发静态回归 | 否 | 小 |
| **B1** | 本地基线迁移（MixedDataset）+ SMB Loopback 扩展（不存在共享/坏主机/1326/已有会话） | 否 | 小 |
| **B2** | FAT32 预检（✅ 已完成）+ **Disk Full VHD** + FAT32 目录满 | **是** | 中 |
| **B3** | Kill/Resume 矩阵 + robocopy kill + 网络中断恢复 | 部分 | 中 |
| **B4** | 权限 + 文件系统边界 | 部分 | 中 |
| **B5** | 大文件 1/5/10GB + 10 万小文件（**串行、独占 IO**) | 否 | 大 |
| **B6** | GUI Smoke + 高 DPI（**独立窗口，需注销**） | 否 | 中 |
| **B7** | 组合故障 | 视情 | 大 |
| **B8** | 占位符 / 其他环境项 | 否 | 小 |
| **L3** | Corporate Simulation 2 台 VM | **是** | 大 |

**并行纪律**（用户第四节）：**脚本/Fixture/文档可并行准备；真实重型 IO 必须串行**。10GB SMB 复制 **不得**与 10 万文件压力测试同时跑。

---

## 十一、本轮未做的事（如实声明）

1. **未修改任何生产代码、任何 XAML、任何主题**。`git status` 保持 clean。
2. **未创建 VHD、未创建 SMB 共享、未创建测试用户、未装域控、未建 VM**。
3. **未执行 Disk Full / Kill-Resume / 大文件 / 10 万文件 / GUI Smoke / 高 DPI**（全部为设计完成、实施未开始）。
4. **未验证**：`Get-Help Mount-VHD` 参数实跑、256MB VHDX 能否格式化为 NTFS、真实 112 逐字行在 v0.4.6 下的熔断行为、L2 抽样率调整后的行为。
5. **保留的现场**：`J:\pcmig-lab\runs\20260919-140131-SMB-LOOPBACK-T01\`（证据）、`J:\pcmig-lab\jobs-probe\`（1 个 job）。
6. `lab\` 下 Agent 遗留的 `answer.iso` 等**未删未动**。

---

*本 Blueprint 由 7 个并行只读审计单元（SMB / VHD / Fault Injection / Fixture / Filesystem / GUI / Corporate Simulation）+ Captain 现场实测汇总而成，所有结论标注证据等级，未实测项一律标 NOT_RUN/BLOCKED/COMPANY_ONLY，未做任何源码修改。*