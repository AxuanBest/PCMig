# PCMig V0.4.6 Test Execution Report（详细执行报告）

> 基线：**v0.4.6**（tag `v0.4.6` → `9a77381`，起点 HEAD `d818f95`）
> 执行者：Captain（DSH 会话）+ 7 个并行只读审计子代理
> 备份基线：tag `v0.4.6-before-testlab` → `d818f95`（2026-09-19 14:21 打）
> 状态：**增量填充中**（已完成 B0 / B1 批次；B2+ 待用户决策）
> 配套：`Private-Test-Lab-Blueprint.md`（设计）｜ `Coverage-Matrix-V046.md`（矩阵）

---

## 一、执行摘要

| 批次 | 内容 | 结果 |
|---|---|---|
| **审计期** | 7 个并行只读审计单元（SMB / VHD / 故障注入 / Fixture / 文件系统 / GUI / 企业模拟） | 全部交付，**零文件修改** |
| **B0** | 单元测试扩库 + 真实日志 Fixture + 正则防复发回归 | **dotnet test 85/85 通过**（原 64） |
| **B1** | 本地基线 + SMB 回环 + SMB 边界 | **7 个场景 6 PASS + 1 BLOCKED（诚实标注）** |

**产品健康度判断（本阶段可见部分）**：核心迁移链路在 L1/L2 层**数据完整性无可指摘** ——
1268 个文件（含空目录、零字节、中文、特殊字符、深路径、长路径、伪占位符、只读/隐藏属性）
在**本地与真实 SMB 回环两条路径**上，全量 SHA256 与目录结构**完全一致**，续传/回执/job-state 全部自洽。

**同时确认 3 个真实缺陷尚未修复**（见 §五），其中 Candidate Bug #1 影响**弹窗与崩溃兜底**。

---

## 二、环境快照（可审计基线）

每个 Run 目录都落有 `environment.json`，内容如下（取自实际文件）：

| 项 | 值 |
|---|---|
| 主机 | **AXUAN** |
| OS | Windows 11 专业版，build **26200.8875** |
| CPU / 内存 | AMD Ryzen 5 9600X（6C/12T）｜31.1 GB 总 / 约 13.5 GB 可用 |
| PowerShell | **5.1.26100.9444** |
| dotnet SDK | **8.0.425** |
| **被测 CLI** | `I:\PCMig\Portable\pcmig-cli.exe` |
| **CLI SHA256** | `4EC0E64222B49F60…`（每个 environment.json 内记录完整值） |
| git HEAD | `d818f95def8b1588b84a40720a02e304457c39ee` |
| **是否管理员** | **False**（Medium 完整性级别） |
| 磁盘 | C 118.6 / D **FAT32 24.5** / E 130.1 / F 129.8 / **G 177.6** / H 27 / I **75.97** / **J 128** / K 80（GB 空闲） |
| SMB 服务 | `LanmanServer` Running、`LanmanWorkstation` Running |
| 已导出共享 | **13 个**：`ADMIN$ C$ D$ E$ F$ G$ H$ I$ J$ K$ IPC$ Users 项目` |
| 测试根 | **`J:\pcmig-lab\`**（与 VM 盘 G、TEMP 盘 E 物理分离，避免 IO 互相污染） |

> ⚠ 交接文档记载 I 盘剩 17.9 GB —— 实测 **75.97 GB**（两个 API 一致复核），该数字已过时。

---

## 三、逐场景详情

### B0-1 `UNIT-01` 单元测试回归

| 项 | 内容 |
|---|---|
| 何时测 | 2026-09-19 14:20–14:24（多轮） |
| 版本 | v0.4.6 源码（HEAD `d818f95` + 本轮测试代码改动） |
| 环境 | L0 单元层，dotnet 8.0.425 |
| Setup | 无需 |
| 实际执行 | `dotnet test "I:\deepseek work\PCMig\PCMig.sln" -c Release` |
| 预期 | 全绿，且用例数 ≥ 64 |
| **实际** | **已通过! 失败 0 / 通过 85 / 跳过 0 / 1 秒** |
| 通过条件 | 0 失败 |
| 证据 | 控制台输出；`tests\PCMig.Core.Tests\`（新增 2 个文件） |
| Cleanup | N/A（只产生 bin/obj，已被 .gitignore 覆盖） |
| **结果** | **RUNTIME_VERIFIED / PASS** |

**新增 21 个用例明细**：
- `RealWorldLogSampleTests`：8 → **24**（新增 16 条，全部标注 `[真实]` / `[构造]`）
- `SourceTreeHygieneTests`（**新文件**）：**5** 条真源码扫描用例

### B0-2 `FIX-01` 真实日志 Fixture 建库

| 项 | 内容 |
|---|---|
| 何时 | 2026-09-19 14:19 |
| Setup | 扫描 `C:\ProgramData\PCMig\Jobs\`（21 个历史 job / 118.7 MB 真实日志） |
| 实际执行 | 以 **GBK/CP936** 逐字节解码，正则提取错误行 |
| **实际结果** | 4 类真实原文全部采到 |
| 证据 | `J:\pcmig-lab\fixtures\robocopy\error-{112,82,59,2}.txt` + `README.encoding.txt` |
| 通过条件 | 采到 ≥1 条真实原文且编码标注正确 |
| Cleanup | 原件未动（只读） |
| **结果** | **RUNTIME_VERIFIED / PASS** |

**采集到的逐字原文**：

```
# 错误 112（磁盘空间不足）— 来源 JOB-20260914-002001-5b64
2026/09/14 00:22:00 错误 112 (0x00000070) 正在复制文件 \\192.168.134.131\E$\迁移全量测试\大文件\大文件-2.bin
磁盘空间不足。
正在等待 5 秒... 正在重试...

# 错误 82（无法创建目录或文件）— 来源 JOB-20260913-130046-e486
2026/09/13 13:03:21 错误 82 (0x00000052) 正在复制文件 \\192.168.134.131\E\迁移全量测试\海量小文件\pic_021845.jpg
无法创建目录或文件。

# 错误 59（意外的网络错误）— 来源 JOB-20260914-002001-5b64
2026/09/14 00:20:46 错误 59 (0x0000003B) 正在复制文件 \\192.168.134.131\E$\迁移全量测试\海量小文件\pic_002133.jpg
出现了意外的网络错误。

# 错误 2（系统找不到指定的文件）— 来源 JOB-20260914-002001-5b64
2026/09/14 00:20:46 错误 2 (0x00000002) 正在复制文件 \\192.168.134.131\E$\迁移全量测试\海量小文件\pic_002131.jpg
系统找不到指定的文件。
```

> ⚠ **编码事实**：robocopy 日志为 **OEM/GBK 无 BOM**（前 8 字节 `0D 0A 2D 2D…`）。
> 用 `-Encoding UTF8` 读会 **100% 漏检**（审计期实际踩到）。Fixture **禁止转 UTF-8 后再断言**。
>
> ⚠ **时序警告**：112 的 job 运行于 **2026-09-14**，而磁盘满熔断是 **v0.3.6（09-16）** 才引入。
> 它证明的是"熔断引入**前**的真实现象"，**不能**用于判定当前熔断是否有效。

### B0-3 `FIX-02` 正则 / 控制字符防复发回归

| 项 | 内容 |
|---|---|
| 实际执行 | 新增 `SourceTreeHygieneTests.cs`（5 条真源码扫描用例） |
| 覆盖 | ① 全树 `*.cs` 无 0x08/0x0B/0x0C ② `IsSpaceErrorText` 正则仍含 `\s+` ③ `ExtractWin32Error` 仍含 `\d+` ④ 无"裸 s+/d+"可疑残留 ⑤ GBK 与 UTF-8 编码差异（锁定 fixture 读取方式） |
| 机制价值 | **自动进 `release.ps1` 闸门 0**（指向测试 csproj），无需额外接线 —— 今后任何"反斜杠被工具链吃掉"都会让构建变红 |
| **结果** | **RUNTIME_VERIFIED / PASS**（随 85/85 一起通过） |

### B1-1 `DATA-01` 测试数据生成器

| 项 | 内容 |
|---|---|
| 产物 | `J:\pcmig-lab\lib\testdata.ps1`（9 种 Kind + MixedDataset） |
| 实际执行 | `-Kind MixedDataset -Root ... -Seed 20260919 -Force`，连跑两次 |
| **实际结果** | **1267 文件 / 51 目录 / 71.093 MB / 2 秒** |
| 确定性验证 | 两次结果：文件数、总字节、**4 个关键文件 SHA256 全部一致** → ✔ 可重复 |
| 15 项要素 | 空目录×3 / 零字节×5 / 小文件×200 / 中型×5 / 大文件×1 / 中文×20 / 空格×5 / 特殊字符×20 / 深层目录 / **长路径** / 重复名×4 / 预置已存在目标 / 大量小文件×1000 / **伪占位符(Offline 位)×2** / 只读+隐藏 |
| 证据 | 数据集 `manifest.json`（含参数、计数、关键哈希） |
| **结果** | **RUNTIME_VERIFIED / PASS** |

### B1-2 `LOCAL-BASE-01` 本地基线迁移

| 项 | 内容 |
|---|---|
| 何时 | 2026-09-19 14:25（首跑）/ 14:26（修复后重跑） |
| Setup | 生成数据集 + 建空目标 + `Assert-LabSafePath` 护栏 |
| 命令 | `pcmig quick --host AXUAN --source J:\pcmig-lab\test-data\base --target J:\pcmig-lab\test-data\out\LOCAL-BASE-01 --yes --threads 16 --jobs J:\pcmig-lab\jobs` |
| 预期 | `phase=completed` + 全量 SHA256 与目录结构完全一致 |
| **实际** | `JOB-20260919-142612-*`：phase=**completed**、failedObjects=**0**、percent=100、bytes=74,548,744/74,548,744、**回执 17/17**、**1268 文件全量 SHA256 一致 + 51 目录结构一致**、耗时 2 秒 |
| 通过条件 | 上述全部成立 |
| 证据 | `J:\pcmig-lab\runs\20260919-142612-LOCAL-BASE-01\`（environment / command / pcmig.log / verify-jobstate / verify-integrity / verify-cli.log / result.json） |
| Cleanup | **PASS**（目标树已删，jobs 任务数已记录） |
| **结果** | **RUNTIME_VERIFIED / PASS** |

### B1-3 `SMB-LOOPBACK-02` 真实 SMB 回环迁移（UNC 源）

| 项 | 内容 |
|---|---|
| 源 | **`\\AXUAN\J$\pcmig-lab\test-data\base`**（真实 UNC + TCP 445 + SMB 会话 + NetShareEnum + robocopy 网络分支） |
| 目标 | `J:\pcmig-lab\test-data\out\SMB-LOOPBACK-02` |
| 预期 / **实际** | 与 LOCAL-BASE-01 完全一致：completed / 0 失败 / **1268 文件全量 SHA256 一致** / 51 目录一致 / 耗时 2 秒 |
| 关键意义 | 这是 PCMig 的**产品定义链路**（SMB 直拉），此前自动化为 **0 覆盖** |
| 前置断言 | 管理共享可读（免提权可行性已在审计期证明） |
| 证据 | `runs\20260919-142617-SMB-LOOPBACK-02\` |
| **结果** | **RUNTIME_VERIFIED / PASS** |

### B1-3 `SMB-BADHOST-03` 不存在的主机名

| 项 | 内容 |
|---|---|
| 命令 | `pcmig preflight --host PCMIG-NO-SUCH-HOST-XYZ --source \\PCMIG-NO-SUCH-HOST-XYZ\D$\nope` |
| 预期 | 预检阻断并点名原因 |
| **实际** | **exit=1**，输出含阻断项；耗时 4 秒 |
| **结果** | **RUNTIME_VERIFIED / PASS** |

### B1-3 `SMB-BADUSER-04` 不存在的用户（真实 1326）

| 项 | 内容 |
|---|---|
| 命令 | `pcmig preflight --host AXUAN --source \\AXUAN\J$\... --user AXUAN\PCMigNoSuchUser99 --password <口令>` |
| 预期 | 真实 `Win32Error=1326` 登录失败路径 |
| **实际** | **输出含 1326 = True** → 真实登录失败路径可达 |
| **诚实性断言** | 同一场景内**显式记 `BLOCKED`**：*"同机回环由登录令牌认证，密码不参与校验；真实密码校验只能由 L3/L4 域环境承接"* |
| **结果** | 1326 路径 **RUNTIME_VERIFIED / PASS**；密码校验 **BLOCKED**（不得当 PASS） |

### B1-3 `SMB-PRESESSION-05` 已有会话复用

| 项 | 内容 |
|---|---|
| 命令 | `pcmig preflight --host AXUAN --source \\AXUAN\J$\...`（空凭据） |
| **实际** | exit=0，445 ✔，IPC$/共享信息 ✔，耗时 0 秒 |
| **结果** | **RUNTIME_VERIFIED / PASS** |

---

## 四、脚手架自身的缺陷与修复（如实记录自伤项）

**这是本轮最值得记录的部分之一**——测试框架自己也有"假 PASS/脏输出"，全部当场抓出并修掉：

| # | 缺陷 | 现象 | 修复 |
|---|---|---|---|
| S1 | **`Compare-Tree` >16MB 不哈希** | 300MB 大文件只比长度，内容损坏测不到 → **大文件场景的"完整性 PASS"在内容层面是空的** | 新模块改为**全量 SHA256**，并引入 `PASS_PARTIAL` 状态显式标注未哈希项 |
| S2 | **`Compare-Tree` 不比对目录** | 空目录丢失完全检测不到 | 新增双向目录相对路径集合比对（**已实测：删掉空目录立刻判 FAIL**） |
| S3 | **退出码陷阱** | `CompletedWithErrors`（含盘满）也返回 0 | 判定一律读 `job-state.json` 的 phase+failedObjects+回执 |
| S4 | **无 job 隔离** | 任务落进生产 `%ProgramData%\PCMig\Jobs`（已有 25 个历史 Job） | 所有 CLI 调用强制 `--jobs J:\pcmig-lab\jobs` |
| S5 | **`write`/`edit` 工具丢 BOM** | 实测：写完 `.ps1` 前 3 字节变 `3C 23 0A`，PS 5.1 按 GBK 读中文脚本 → **字符串闭合被破坏、解析失败**（铁律 8 的经典事故） | 改后立即复查并补 `EF BB BF`；`.ps1` 改用 PowerShell `ReplaceExact` 修改 |
| S6 | **PS 5.1 语法** | 误用 `$(if ...)` 与 `$args`（自动变量） | 全部改 if/else 赋值 + 重命名 `$cliArgs` |
| S7 | **汇总输出污染返回值** | `Complete-LabRun` 用 `Write-Output` → 多行文本被当成场景结果，导致"2 个场景非 PASS"误报 | 改 `Write-Host`（不进管道） |
| S8 | **单元素数组解包** | `Get-LabResults` 返回嵌套数组 → 6 条断言被当成 1 条显示 | 返回 `@(...)` 并修正汇总格式 |
| S9 | **.NET Core 无 GBK** | `Encoding.GetEncoding(936)` 抛 `NotSupportedException` | 测试项目加 `System.Text.Encoding.CodePages@8.0.0` + 静态构造注册 |
| S10 | **csproj 中文注释乱码** | 我用 PowerShell 插入的中文注释被写成乱码 | 改纯 ASCII 注释（避免二次编码事故） |

> **S1/S2 是"假 PASS"治理的核心**：修复前，任何大文件或涉及空目录的场景，都会被判 PASS 而实际未验证内容。

---

## 五、产品缺陷（已复现，尚未修复 —— 一行生产代码都没动）

### Candidate Bug #1 —— Error/Warning 弹窗**永久失效**（P0）

| 环节 | 事实 |
|---|---|
| 源码 | `AppDialog.xaml.cs:52-65`：Error→`"ErrorTextColor"`、Warning→`"WarnTextColor"`，L65 用 **`FindResource`**（找不到会抛异常，`TryFindResource` 才返回 null） |
| 主题实测 | 三套主题各 **152 个 key**，但 `ErrorTextColor` / `WarnTextColor` **命中 = 0** |
| **真实运行证据** | `app-20260918.log` **L1316–1361 连续 10 次** `ResourceReferenceKeyNotFoundException: 未找到"WarnTextColor"资源` |
| **崩溃日志** | `crash-20260918-233603.log` + `crash-20260918-234105.log` **两份**，同异常、位置=界面线程 |
| 后果 | 弹窗从未显示；`Result` 保持 `None` |
| 影响（5 个调用点） | ① `MainViewModel.cs:811-812` 迁移前提醒（Information，**不受影响**）② **`MainViewModel.cs:829-831` 目标盘空间不足**（Warning → 静默变"已取消" + 误导性文案"预检未能完成"）③ **`MainViewModel.cs:1188-1190` 扫描不完整**（Warning）④ `MainViewModel.cs:715-717` / `2024-2026` 未完成任务（Question，不受影响）⑤ **`App.xaml.cs:99-104` 全局崩溃兜底**（→ 兜底弹窗自己抛异常 → 被 L106 `catch { }` 吞掉 → **崩溃提示永远弹不出来**） |
| 等级 | **CODE_VERIFIED + 历史 RUNTIME 证据**；本轮**未在 GUI 现场复现** |
| 处置建议 | 走第二十一节 7 步：先加回归测试再修；修法二选一（补齐 3 套主题 key / 改 `TryFindResource` + 兜底） |

### Candidate Bug #2 —— UNC 目标 CLI 判死、GUI 放行

- `pcmig preflight --target \\AXUAN\J$\x` → `✘ 目标盘检查: Drive name must be a root directory…`，**exit=1**
- 根因：`PreflightChecker.cs:239-241` 对任何 target 都执行 `new DriveInfo(Path.GetPathRoot(...))`，**无 UNC 分支**
- GUI 侧 `MainViewModel.cs:823-841` 吞异常**只警告不阻断** → 同输入 CLI 判死、GUI 放行
- `docs\` **零记录**，不是已知限制。**属"设计未定义"边界，需用户拍板**

### Candidate Bug #3 —— 测试体系自身 3 处"假 PASS"（旧 `stability-test.ps1`）

| 处 | 事实 |
|---|---|
| P2 | 打断点写死 600ms，脚本自认"未真正打断"，L135 仍判 PASS |
| P3 | `WriteAllBytes` 顺带更新 LastWriteTime → robocopy 默认就重拷 → **验证不到 PCMig 修复能力**；真测须恢复原时间戳 |
| `verify` L2 | 抽样 0 项时 CLI 明说"未抽样任何对象"、退出码 0、无 `✘` → 而旧判定只查 `✘` → **内容级校验空转被判 PASS**（本轮已在新框架中改为 `NOT_RUN`） |

**附加事实**：CLI 路径下"内容损坏但大小+时间戳未变"**没有修复通道** —— `ForceOverwriteFromSource` 全仓库唯一赋值点是 `MainViewModel.cs:1707`（GUI「尝试修复」）。

---

## 六、假 PASS 治理记录

| 曾可能被判 PASS 的情形 | 现在的判定 | 依据 |
|---|---|---|
| 大文件（>16MB）内容损坏 | **FAIL**（全量 SHA256） | 新 `Compare-LabTree` |
| 空目录丢失 | **FAIL**（目录集合比对） | 同上（已实测） |
| CLI `CompletedWithErrors`（含盘满） | **FAIL**（读 phase≠completed） | `job-state.json` |
| `verify --level 2` 抽样 0 项 | **NOT_RUN** | 输出含"未抽样任何对象" |
| 打断未真正发生 | **NOT_RUN** | 设计规格（B3 实施） |
| 回环"错误密码仍通过预检" | **BLOCKED** | 回环认证绕过，实测确认 |

---

## 七、未验证项如实清单

| 项 | 状态 | 原因 |
|---|---|---|
| **磁盘满真端到端 + 熔断** | **BLOCKED** | 需管理员建/挂 VHD；本会话未提权 |
| FAT32 目录满（错误 82） | **BLOCKED** | 同上（真实素材已有，但需 VHD 重造） |
| 逐字 robocopy 112 在 v0.4.6 下的熔断行为 | **BLOCKED** | 必须用 v0.4.6 二进制 + 小容量 VHD 重造（旧日志早于熔断功能） |
| Kill / Resume 矩阵 | NOT_RUN | B3 批次 |
| 网络中断 → 恢复 → Resume | NOT_RUN | B3 批次 |
| 大文件 1/5/10GB | NOT_RUN | B5 批次（需先确认磁盘） |
| 10 万小文件 | NOT_RUN | B5 批次 |
| GUI Smoke / 高 DPI | NOT_RUN | B6 批次（DPI 切换需注销会话） |
| **密码校验 / 1219 冲突 / IPC$ 缺失(67)** | **BLOCKED → L3/L4** | 回环结构上不可制造 |
| **AD / DNS / GPO / EDR / 企业 VPN** | **COMPANY_ONLY** | 本机无 Server 介质 |
| OneDrive 真实云端回源 | COMPANY_ONLY | 本机无 OneDrive（但 `Offline` 位伪占位符已可测） |
| 跨不同 DPI 显示器拖动 | COMPANY_ONLY | 本机单显示器 |

---

## 八、附件索引

### 证据运行目录（`J:\pcmig-lab\runs\`）

| RunId | 场景 | 总体 |
|---|---|---|
| `20260919-140131-SMB-LOOPBACK-T01` | 审计期手跑 SMB 回环 | 无 result.json（早期手跑，证据为 `environment.json` + robocopy 原文） |
| `20260919-142510-LOCAL-BASE-01` | 首跑（暴露脚手架缺陷 S7/S8） | **FAIL**（已在后续修复；保留为迭代证据） |
| `20260919-142516-SMB-LOOPBACK-02` | 首跑 | PASS |
| `20260919-142542-LOCAL-BASE-01` | 修复后 | PASS |
| `20260919-142547-SMB-LOOPBACK-02` | 修复后 | PASS |
| `20260919-142612-LOCAL-BASE-01` | 最终 | **PASS** |
| `20260919-142617-SMB-LOOPBACK-02` | 最终 | **PASS** |
| `20260919-142652-SMB-BADHOST-03` | 边界 | **PASS** |
| `20260919-142657-SMB-BADUSER-04` | 边界 | **BLOCKED**（密码校验不可测） |
| `20260919-142712-SMB-PRESESSION-05` | 边界 | **PASS** |

每个目录内含：`environment.json` / `command.txt` / `pcmig.log` / `verify-jobstate.json` / `verify-integrity.json` / `verify-cli.log` / `result.json` / `cleanup`（在 result.json 内）。

### 代码与配置产物

| 路径 | 说明 |
|---|---|
| `J:\pcmig-lab\lib\LabCommon.ps1` | 公共模块（完整性核对 / 证据落盘 / 安全护栏 / SMB 基线） |
| `J:\pcmig-lab\lib\testdata.ps1` | 测试数据生成器（确定性） |
| `J:\pcmig-lab\scenarios\local-base.ps1` | 本地 + SMB 回环基线场景 |
| `J:\pcmig-lab\scenarios\smb-boundary.ps1` | SMB 边界与凭据场景 |
| `J:\pcmig-lab\fixtures\robocopy\error-*.txt` | 真实日志 GBK 原件（4 类） |
| `tests\PCMig.Core.Tests\SourceTreeHygieneTests.cs` | 新增（正则/控制字符防复发） |
| `tests\PCMig.Core.Tests\RealWorldLogSampleTests.cs` | 扩写（8 → 24 用例） |
| `tests\PCMig.Core.Tests\PCMig.Core.Tests.csproj` | 加 CodePages 包 |

### 工作区改动（未提交，`git status` 可见）

```
 M tests/PCMig.Core.Tests/PCMig.Core.Tests.csproj
 M tests/PCMig.Core.Tests/RealWorldLogSampleTests.cs
?? docs/Coverage-Matrix-V046.md
?? docs/Private-Test-Lab-Blueprint.md
?? tests/PCMig.Core.Tests/SourceTreeHygieneTests.cs
```

> **生产代码零改动**：`src\**` 一行业都没动。

---

## 九、下一步（等待用户决策）

| 批 | 内容 | 阻塞项 |
|---|---|---|
| **B2** | FAT32 VHD 预检（已有真实版）/ Disk Full 熔断 / FAT32 目录满 | **需提权**（`New-VHD`/`Mount-VHD`/`Format-Volume`） |
| **B3** | Kill/Resume 矩阵 + 网络中断恢复 | 无（可立即开工） |
| **B4** | 权限 + 文件系统边界 | 部分需 ACL 操作 |
| **B5** | 大文件 / 10 万小文件 | 需确认磁盘配额 |
| **B6** | GUI Smoke + 高 DPI | DPI 需注销会话窗口 |
| **L3** | Corporate Simulation 2 台 VM | **需 Windows Server 介质决策** |

---

*本报告随每个批次增量更新。所有结论标注证据等级；未实测项一律 NOT_RUN / BLOCKED / COMPANY_ONLY，未做任何生产代码修改。*
---

## 附录 B：Candidate Bug #1 修复记录（WarnTextColor / ErrorTextColor 资源契约缺失）

> 决策依据：用户 2026-09-19 明确判定「**修**」，并按"先回归测试再修复"的顺序执行。
> **本附录是唯一一处对 `src\` 的改动记录** —— 修复期间未做任何其他 UI 改造。

### B.1 缺陷性质

**不是 UI 美化问题，而是"异常路径依赖的资源契约缺失"。**
尤其它落在**异常处理路径**上，不能因为正常流程看不出来就忽略。

### B.2 证据链（修复前，全部实测）

| 环节 | 事实 | 位置 |
|---|---|---|
| 引用点 1 | `brushKey = "ErrorTextColor"`（`MessageBoxImage.Error`） | `AppDialog.xaml.cs:57` |
| 引用点 2 | `brushKey = "WarnTextColor"`（`MessageBoxImage.Warning`） | `AppDialog.xaml.cs:59` |
| 解析方式 | `var brush = (Brush)FindResource(brushKey);` —— **无 try/catch** | `AppDialog.xaml.cs:65` |
| **主题定义** | **三套主题（Glass / Glass.Dark / Classic）内 `ErrorTextColor`、`WarnTextColor` 定义数 = 0** | 实测 |
| 调用链 | `ApplyContent`(:39) 抛异常 → **`ShowDialog()`(:40) 永远执行不到** → `Result` 保持 `None` | `AppDialog.xaml.cs:39-41` |
| 遮蔽 | 崩溃兜底外层 `catch { }` 静默吞掉异常 | `App.xaml.cs:106` |
| 历史运行证据 | 同一会话内**连续 10 次** `ResourceReferenceKeyNotFoundException: 未找到"WarnTextColor"资源` | `app-20260918.log:1316-1361` |
| 崩溃日志 | `crash-20260918-233603.log` + `crash-20260918-234105.log`（**两份**） | `%ProgramData%\PCMig\Logs` |

### B.3 影响面（3 个调用点受影响）

| 调用点 | 图标 | 受影响 |
|---|---|---|
| `MainViewModel.cs:829` 目标盘空间不足 | **Warning** | ✅ 受影响（静默变"已取消" + 误导文案"预检未能完成"） |
| `MainViewModel.cs:1188` 扫描不完整 | **Warning** | ✅ 受影响 |
| **`App.xaml.cs:104` 全局崩溃兜底** | Error / Warning | ✅ 受影响（**崩溃提示永远弹不出来**） |
| `MainViewModel.cs:811` 迁移前提醒 | Information | ❌（走 `AccentColorBrush`） |
| `MainViewModel.cs:715` / `2024` 未完成任务 | Question | ❌（同上） |

**附带发现**：`Controls.xaml:138` 的 `DangerButton` 样式也引用 `ErrorTextColor`，但该样式**全仓库无任何使用点** → 属**潜在**隐患（`DynamicResource` 缺失时不抛异常，只静默不生效）。

### B.4 修复顺序（严格按用户要求）

1. ✅ **先加最小回归测试** —— 新增 `tests\PCMig.Core.Tests\GuiResourceContractTests.cs`（2 个用例）
2. ✅ **明确测试证明 key 存在** —— `Themes_DefineAllSemanticColorKeysUsedByAppDialog`
3. ✅ **明确 Error / Warning / Crash Fallback 的引用** —— 见 B.2 表格（逐行核实）
4. ✅ **先验证测试能抓住缺陷** —— 修复前运行：**2 个用例 FAIL**（`失败: 2，通过: 85`）
5. ✅ **再补回资源** —— 三套主题各追加 2 个 key，**key 数 152 → 154，三套同步**
6. ✅ **跑新增回归测试** —— 转 PASS
7. ✅ **跑完整测试套件** —— **87 / 87 通过**；GUI 编译 **0 警告 0 错误**
8. ✅ **运行时验证** —— 启动 GUI + 遍历四页 + 打开更新日志：`ResourceReferenceKeyNotFound` **新增 0 行**、新 crash **0 个**

### B.5 修复内容（唯一改动）

三套主题各在"语义色"区段追加两个 key：

| 主题 | 新增 | 取值 | 依据 |
|---|---|---|---|
| `Glass.xaml` | `WarnTextColor` / `ErrorTextColor` | `#FFB45309` / `#FFC62828` | 与既有 `WarningColor`/`ErrorColor` 同色调，保证亮底可读 |
| `Glass.Dark.xaml` | 同上 | `#FFF5B942` / `#FFFF8A80` | 沿用暗色主题既有色调，保证暗底可读 |
| `Classic.xaml` | 同上 | `#FFB45309` / `#FFC62828` | 与经典主题既有色调一致 |

**未做**：没有改 `AppDialog.xaml.cs`（不改成 `TryFindResource`，保持原设计意图）；没有动任何布局/视觉/其他 token；`MainViewModel` 未动。

### B.6 回归测试的一个实测修正（如实记录）

`NoDanglingSemanticResourceReferences` 首版**只扫三套主题**，导致把 `Controls.xaml` 里以 **Style** 形式定义的
`GlassInnerSurface` / `GlassContentSurface` / `DialogSurface` / `DialogInnerSurface`
**误报为"悬空引用"**。

复核后确认：它们确实是合法定义（只是控件样式而非主题 token），**是我的检查范围太窄**。
已把收集范围扩大为**全部 Gui XAML**，未去"修"不存在的 bug。

### B.7 结论

| 项 | 结果 |
|---|---|
| 缺陷是否真实存在 | **是**（引用真实、key 确实缺失、历史 10 次异常 + 2 份 crash 日志） |
| 是否按纪律"先测后修" | **是**（第 4 步真实观察到 FAIL） |
| 完整测试套件 | **87 / 87 通过** |
| 生产代码改动范围 | 仅 3 个主题文件各 +2 行；**`MainViewModel` / UI 布局 / 其他逻辑零改动** |
| 运行时验证 | 通过（零新增资源键异常、零新 crash） |
---

## 附录 C：Candidate Bug #2 结论与修复记录（UNC 作为目标位置）

> 决策依据：用户 2026-09-19 判定「**算 Bug**」，但**本次只统一行为，不擅自扩展 UNC Target 功能**。
> 要求：先读产品文档 / 使用说明 / CLI 帮助 / GUI 行为 / 相关测试 / Target Path 约束，确认当前契约。

### C.1 产品契约取证（修复前）

| 证据 | 内容 |
|---|---|
| CLI 帮助中 `--target` 全部示例 | `--target <D:\目录>`、`--target D:\` —— **全部本地盘符，从未出现 UNC** |
| `docs\使用说明.txt` 示例 | `--target D:\` —— 本地盘符 |
| `README.md` 示例 | `--target D:\`；拓扑图写明 "新电脑目标路径" |
| 产品定位 | **Direct Pull：在新电脑上运行，把数据写到本机磁盘** |
| GUI 目标选择器 | `FolderBrowserDialog`（`MainViewModel.cs:663`）—— **选不到 UNC** |
| 相关测试 | 无任何覆盖 UNC 目标的测试 |

**结论：UNC 目标不在产品契约内。**（属情况 A：不允许。但要明确 —— 并非"有意禁止"，而是目标盘校验对 UNC 不适用所致。）

### C.2 复核：此前"CLI 判死 / GUI 放行"的结论不成立（如实纠正）

此前审计（子代理 B）报告"GUI 同样调用却吞异常**只警告不阻断**"。**本轮逐行复核后判定该结论不准确**：

| 复核证据 | 事实 |
|---|---|
| `MainViewModel.cs:763-771` | GUI **确实有** `if (!pre.OverallPass) { …AddFailRow… StatusMessage=…; return; }` —— **会阻断** |
| `MainViewModel.cs:435` | GUI **连接阶段**预检调用 **不传 target** → 该路径不触发目标盘检查 |
| `MainViewModel.cs:823-838` | 空间守护是**另一处**独立调用（仅在 target 已可解析时才能走到） |
| `MainViewModel.cs:840-845` | 该 `catch` 兜的是**空间检查**异常，与目标盘检查无关 |

**真实缺陷只有一条**：`PreflightChecker` 对 UNC 目标走 `new DriveInfo("\\\\server\\share")` 抛 `ArgumentException`，
catch 后**直接把异常文案当检查详情** → 用户看到的是
`Drive name must be a root directory (i.e. 'C:\') or a drive letter ('C'). (Parameter 'driveName')` ——
**不可读、且看不出"UNC 不被支持"**。

> 方法论教训：**静态分析可以定位嫌疑，但不能替代逐行复核**。子代理的判断未经复核就写进报告，是本次要纠正的自伤项。

### C.3 修复内容（统一行为，不扩功能）

**唯一改动**：`src\PCMig.Core\Preflight\PreflightChecker.cs` 的目标盘 catch 分支 ——
显式识别 UNC 并给出面向用户的 Error 详情；同时把判定抽成 `internal static bool IsUncTarget(string?)` 纯函数，使其可被单元测试直接覆盖（**行为不变**，仅为可测性）。

**为什么这一处改动就能统一 CLI 与 GUI**：两者**共用同一个 `PreflightChecker`**，
且都依据 `report.OverallPass` 阻断（CLI `Program.cs:212/332`；GUI `MainViewModel.cs:763`），
因此检查项与结论天然一致。

**未做**：没有新增 UNC Target 功能；没有改 GUI 布局；没有动 `MainViewModel` 的业务逻辑。

### C.4 回归测试（新增，用户要求"至少建立能保护这条规则的可执行测试"）

新增 `tests\PCMig.Core.Tests\TargetPathContractTests.cs`（**12 个用例**）：

| 用例组 | 覆盖 |
|---|---|
| `IsUncTarget_DetectsUncPaths`（4 例） | `\\server\share`、`\\192.168.1.10\D$`、含子目录、**含前后空格** |
| `IsUncTarget_RejectsLocalAndEmptyPaths`（7 例） | `D:\`、中文路径、`C:\Users\...`、正斜杠、空串、纯空格、`null` |
| `IsUncTarget_SingleBackslashIsNotUnc`（1 例） | 单反斜杠开头**不得**误判为 UNC |

**运行级别标注**：全部为 **L0 纯逻辑**（不依赖网络与 GUI，可进发版闸门 0）。

### C.5 验证结果（修复前 → 修复后）

| 项 | 修复前 | 修复后 |
|---|---|---|
| UNC 目标文案 | `Drive name must be a root directory (i.e. 'C:\') or a drive letter ('C'). (Parameter 'driveName')` | `不支持把网络路径当作目标位置：\\localhost\J$\…。本工具在新电脑上运行、把数据写到本机磁盘，因此目标必须是本机盘符路径（如 D:\迁移目标）。若目标是另一台机器的共享，请在那台机器上就地运行本工具，或先把共享映射为本地盘再试。` |
| UNC 目标判定 | exit=1 阻断（文案不可读） | **exit=1 阻断 + 文案可读**（行为不变，仅消息改善） |
| 本地盘符目标 | `✔ 目标盘 J:\: 可用 127.05 GB` → 通过 | **同样通过**（无回归） |
| 完整测试套件 | 87 / 87 | **99 / 99**（+12 新用例） |
| GUI 编译 | — | **0 警告 0 错误** |

### C.6 结论

| 项 | 结果 |
|---|---|
| 是否为 Bug | **是**，但**比原报告窄**：不是"CLI/GUI 行为不一致"，而是"UNC 目标的报错文案不可读且未说明契约" |
| 是否按契约统一行为 | **是**（未经由新增功能，而是让共用组件给出明确、一致的判定与提示） |
| 是否擅自扩展功能 | **否**（UNC Target 仍不被支持） |
| 生产代码改动 | 仅 `PreflightChecker.cs` 一处 catch 分支 + 1 个 internal 纯函数 |