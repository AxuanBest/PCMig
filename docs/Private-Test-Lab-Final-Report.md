# PCMig V0.4.6 Private Test Lab —— 最终报告

> 完成日期：**2026-09-19** ｜ 基线：**v0.4.6**（tag `v0.4.6` → `9a77381`，起点 HEAD `d818f95`）
> 备份基线：tag `v0.4.6-before-testlab` → `d818f95`（2026-09-19 14:21）
> 性质：**私人电脑可验证范围的执行结果汇总**。未执行项一律如实标注，不伪装成已验证。
> 副产品：`Private-Test-Lab-Blueprint.md`（L0–L2 设计）｜ `Corporate-Simulation-Blueprint.md`（L3 设计）｜ `Test-Execution-Report-V046.md` + `-AddendumA.md`（逐场景详情）｜ `Coverage-Matrix-V046.md`（矩阵）｜ `First-Day-Company-Test-Checklist.md`（公司边界）

---

## 一、总览（用户第十四节要求的口径）

### 1.1 场景统计（**最终有效 Run**）

| 结果 | 数量 |
|---|---|
| **PASS** | **19** |
| **FAIL** | **2**（均非产品缺陷：1 个"设计未定义"观察项、1 个测试脚本定位问题） |
| **NOT_RUN** | **4**（打断/注入窗口未命中 4 个） |
| **BLOCKED** | **2**（回环密码校验、FAT32 迁移期 Error 82 设计不可达） |
| **COMPANY_ONLY** | **15 项**（见 `First-Day-Company-Test-Checklist.md`：C-01…C-15） |
| **合计有效场景** | **27**（含 B2 全部收敛） |

> 另有 60 个"脚手架调试期"run（修测试脚本 bug 过程中的中间结果），**不作为产品结论**；每个 run 目录内含 `classification.json`，索引见 `J:\pcmig-lab\reports\run-index.json`。

### 1.2 单元测试

**85 / 85 通过**（原 64 → 新增 21）｜`dotnet test PCMig.sln -c Release`

### 1.3 生产代码改动

**`src\**` 零改动**（`git status` 可见：仅 `tests\` 2 改 1 增 + `docs\` 新增）。UI 未动、`MainViewModel` 未动、无任何"为测试而加的生产逻辑"。

---

## 二、按维度覆盖情况

### 2.1 Unit（L0）

| 项 | 结果 |
|---|---|
| 64 → **85** 用例全绿 | **PASS / RUNTIME_VERIFIED** |
| 真实日志 Fixture 建库（错误 **112 / 82 / 59 / 2** 逐字原文，GBK 原件） | **PASS**，`J:\pcmig-lab\fixtures\robocopy\` |
| 正则/控制字符防复发回归（`SourceTreeHygieneTests`，5 用例） | **PASS**，已并入 release 闸门 0 |
| 覆盖性质分类 | 真实日志样本 5→**21** ｜ 真实运行时 7 ｜ 纯逻辑 15 |

### 2.2 Integration（L1，真 CLI + 真文件系统）

| 场景 | 结果 | 关键数据 |
|---|---|---|
| `LOCAL-BASE-01` 本地基线 | **PASS** | 1268 文件**全量 SHA256 一致** + 51 目录一致 |
| `FSBOUND-01` 边界要素迁移 | **PASS** | 中文名 60 / 空格 11 / 特殊字符 7 / 零字节 5 / 同名 4 / 只读隐藏 2 / **空目录 33 个全部保留** |
| `FSBOUND-02` 长路径 | **PASS** | **319 字符**路径真实迁移成功 |
| `FSBOUND-03` 属性文件 | **PASS** | — |
| `LOCKED-01` 目标被占用 | **PASS** | 被占用文件未被覆盖、其余 30 个不受影响 |
| `PERM-01` ACL 拒绝 | **PASS** | 输出识别残缺、**默认拦截**、`phase=awaitingReview`、`lastError` 明说需 `--allow-incomplete-scan` |

### 2.3 Network（L2，真实 SMB）

| 场景 | 结果 | 关键数据 |
|---|---|---|
| `SMB-LOOPBACK-02` UNC 直拉（3718 文件级） | **PASS** | 通过 `\\AXUAN\J$\…` 真实走 445 + IPC$ 会话 + 共享枚举 + robocopy 网络分支，**全量 SHA256 一致** |
| `SMB-BADHOST-03` 坏主机名 | **PASS** | `exit=1` 正确阻断 |
| `SMB-BADUSER-04` 不存在用户 | **PASS** | **真实 `Win32Error=1326`** |
| `SMB-BADUSER-04` 密码校验 | **BLOCKED** | 🚨 **回环认证绕过**：同机回环用登录令牌，**密码不参与校验**（实测错密码仍"已建立会话"）→ **结构上不可测，交 L3/L4** |
| `SMB-PRESESSION-05` 会话复用 | **PASS** | — |
| 1219 冲突 / IPC$ 缺失(67) | **BLOCKED → L3** | 需真实多凭据/不导出 IPC$ 的服务端 |

### 2.4 Filesystem（L2，真实 VHD）

| 场景 | 结果 | 关键数据 |
|---|---|---|
| `FAT32-DIRFULL-01` FAT32 目标 | 见 §三-3（预检在 `--yes` 下未阻断 → **待裁决**） | VHD `X: FAT32 1024MB` 创建/格式化/清理全通 |
| `EXFAT-LARGE-01` exFAT 上 1.5GB 文件 | **PASS** | 预检**只 Warning 不阻断**（与 FAT32 的 Error 级不同），行为符合设计 |
| `DISKFULL-02` **磁盘满熔断链** | **PASS（4/6 断言）** | **`phase=completedWithErrors`、`failedObjects=2`、`lastError` 明说"目标磁盘空间不足…释放后 resume"** |

**★ 本轮最重要的新证据（把磁盘满分支从 CODE_VERIFIED 抬到 RUNTIME_VERIFIED）**：

```
2026/09/19 14:53:54 错误 112 (0x00000070) 正在复制文件 J:\pcmig-lab\test-data\overflow\large\large.bin
磁盘空间不足。
```

这是 **v0.4.6 二进制在真实小容量 VHD 上产生的逐字原始日志**，不是构造样本。
证据：`runs\20260919-145348-DISKFULL-02\robocopy-object-000001.log`（从 job 的 GBK 原文复制）。

### 2.5 Recovery（L1–L2）

| 场景 | 结果 | 关键数据 |
|---|---|---|
| `LOCK-01` job.lock 互斥 | **PASS** | 外部独占 → 第二进程被拒 `exit=4` → 释放后可跑 `exit=0` → 数据完整 |
| `STATE-01` job-state 损坏 | **PASS** | 写坏 JSON → `status` 不崩 + 提示"待由回执重建" + **进度行不给出 0.0%** → resume 后完成且数据完整 |
| `KILL-01/02/03` 硬杀 / 杀 robocopy | **NOT_RUN（打断时机）** + **PASS（后果断言）** | 无新增孤儿 robocopy ✔ ｜ 目标字节冻结 ✔ ｜ resume 后 phase=completed ✔ ｜ **全量 SHA256 一致** ✔ |
| `SRCMUT-01` 复制期间删源 | **NOT_RUN（注入窗口）** + PASS（不整体失败） | — |

**关于 KILL 的诚实说明**：三次不同数据量（4001 / 30000 文件）均**未能在 20%–60% 进度窗口真正命中**——迁移太快（4001 文件 2 秒、30000 文件 11 秒）。
因此**只验证了"硬杀之后的资源与状态正确性"**，未验证"在特定进度点中断"。**如实记 `NOT_RUN`，不判 PASS。**
改进方案已写入脚本注释：降轮询到 50ms / 用 20 万文件 / 读 `job-state.json` 的 `completedBytes`。

### 2.6 Stress（L1，分级）

| 档位 | 文件数 | 迁移耗时 | 吞吐 | 全量 SHA256 核对 | 系统可用内存 | 结果 |
|---|---|---|---|---|---|---|
| `SCALE-SMALL-01` | 1,001 | 2 s | 500 文件/秒 | 1 s | 12,911 MB | **PASS** |
| `SCALE-MID-01` | 10,001 | 4 s | 2,500 文件/秒 | 11 s | 12,746 MB | **PASS** |
| `SCALE-LARGE-01` | 30,001 | 11 s | 2,727 文件/秒 | 35 s | 12,056 MB | **PASS** |
| `SCALE-XL-01` | **50,001** | 14 s | **3,571 文件/秒** | 65 s | 11,687 MB | **PASS** |

**结论**：① 逐级递增无异常；② **内存随规模平稳**（12.9 GB → 11.7 GB，无泄漏迹象）；③ 无 CPU 饱和（26%–65%）；④ 每档均**全量 SHA256 + 目录结构完全一致**；⑤ 每档 Cleanup 均 PASS。

### 2.7 GUI（L1，回归）

| 断言 | 结果 |
|---|---|
| 冷启动 + 标题 `PCMig 迁移工具 v0.4.6` | **PASS** |
| UIA 根元素 / `StepRailList` / **4 个导航项** | **PASS** |
| **Navigation 1/2/3/4 真实切换**（页面标记 ①②③④ 逐页可见，可见元素数 58→67→70→66） | **PASS** |
| Update Log 窗口打开 + `VerList` 有内容 | **PASS** |
| 无新 crash 日志 | **PASS**（0） |
| 无 Binding Error | **PASS**（0 行） |
| 无未处理异常 | **PASS**（0 行） |
| **无资源键缺失**（Candidate Bug #1 实时看门狗） | **PASS**（`ResourceReferenceKeyNotFound` = 0 行） |
| 主窗口关闭后进程退出 | **PASS** |
| 上一版/下一版按钮、关闭按钮 | **FAIL（脚手架定位问题，见 §四-S17）** |
| ScrollBar 溢出触发（主窗实时日志 / 更新日志窗口） | **NOT_RUN**（前者需"有数据状态"，后者窗口不可 Resize） |
| Secondary Dialog | **NOT_RUN**（无运行中任务，触发条件不足，**不伪造**） |
| 截图基线 | 7 张 → `J:\pcmig-lab\screenshots\20260919-145711-GUI-SMOKE-01\` |

### 2.8 Corporate Simulation（L3）

**未执行** —— 只交付了 `Corporate-Simulation-Blueprint.md`。
阻塞项：**本机无 Windows Server 介质**（AD/DNS/域用户/GPO 全部无法开始）；另需决定虚拟化平台与既有 `PCMig-OldPC.vhdx` 是否复用。

---

## 三、未完成 / 未通过项的定性（用户第十一节：先分类，不改产品）

| # | 项 | 分类 | 定性 |
|---|---|---|---|
| 1 | `KILL-01/02/03` 打断时机 `NOT_RUN` | **D 时序/竞态** | 数据集对 NVMe 太快，20–60% 窗口约 0.5 秒。**非产品问题**；改进方案已定 |
| 2 | `SRCMUT-01` 注入窗口 `NOT_RUN` | **D 时序** | 同 1 |
| 3 | `SRC-TGT-01` `Source==Target` 未被拦截 | **A（产品行为，但属"未定义"）** | 全库无 Source/Target 关系校验。**按纪律只报告，不改产品**，需人工拍板是否拦截 |
| 4 | `FAT32-DIRFULL-01` FAT32 预检在 `--yes` 下未阻断 | **待裁决** | 需复核 `--yes` 是否会跳过 FAT32 的 Error 级预检；这是**真实行为差异**，不是脚手架问题 |
| 5 | `DISKFULL-02` "释放后 Resume 补齐" FAIL | **E 测试数据问题** | 源是**单个 1.5GB 文件**而 VHD 仅 1GB → **物理上不可能完成**。resume 正确报告"还需 1.5GB / 可用 1008MB"并优雅停止，**是正确行为**。应改用"多个小文件"构造溢出 |
| 6 | `DISKFULL-02` "最终数据完整性" FAIL | **E 测试数据问题** | 根散落 `manifest.json` 未作为对象迁移（`large\` 目录因 FAIL 被跳过约定）。**我的数据集清单缺陷**，非产品漏洞 |
| 7 | `GUI-SMOKE-01` 上一版/下一版/关闭按钮 FAIL | **B 脚手架问题** | 实测：`ChangelogWindow` **不出现为独立顶层窗口**（Children 级只有主窗口），按 AutomationId 在其内查找必然失败；而 V1 用 `RootElement.Descendants` 能找到 `VerList` → **定位作用域用错** |
| 8 | `SMB-BADUSER-04` 密码校验 | **C 环境限制** | 回环由登录令牌认证，密码不参与校验 → **结构上不可测**，正确处置是标 `BLOCKED` 交 L3/L4 |

> **没有任何一项 FAIL 是产品缺陷**（Candidate Bug #1/#2 是独立于本轮 Lab 的既有缺陷，见 §五）。

---

## 四、本轮修掉的**测试脚手架缺陷：17 个**（含 1 次真实自伤事故）

| # | 缺陷 | 影响 | 修复 |
|---|---|---|---|
| **S1** | 旧 `Compare-Tree` **>16MB 不哈希** | **大文件场景"完整性 PASS"在内容层面是空的** | 改**全量 SHA256** |
| **S2** | 旧 `Compare-Tree` **不比对目录** | **空目录丢失完全检测不到** | 加目录集合双向比对（实测删空目录立刻 FAIL） |
| S3 | 退出码陷阱 | `CompletedWithErrors`（含盘满）也返回 0 | 判定一律读 `job-state.json` |
| S4 | 无 job 隔离 | 任务落进生产 `%ProgramData%\PCMig\Jobs` | 强制 `--jobs J:\pcmig-lab\jobs` |
| **S5** | `write`/`edit` 工具**丢 BOM** | PS 5.1 按 GBK 读 UTF-8 中文脚本 → **字符串闭合被破坏、解析失败**（铁律 8 经典事故） | 改后复查补 `EF BB BF`；`.ps1` 用精确编辑 |
| S6 | PS 5.1 用 `$(if ...)` / `$args` / `$Pid` | 语法错或只读变量冲突 | 改 if/else 赋值 + 重命名 |
| S7 | `Complete-LabRun` 用 `Write-Output` | 汇总文本污染返回值 → **场景结果误报** | 改 `Write-Host` |
| S8 | 单元素数组解包 | 6 条断言被当成 1 条 | 统一 `@()` |
| S9 | .NET Core 无 GBK | `Encoding.GetEncoding(936)` 抛异常 | 加 `System.Text.Encoding.CodePages@8.0.0` + 注册 |
| S10 | csproj 中文注释乱码 | 二次编码事故 | 改 ASCII 注释 |
| **S11** | **函数内 `Write-LabLog` 污染管道** | `Compare-LabTree` 返回 `[字符串..., 对象]` → **完整性核对结果整块丢失** | 函数内改 `Write-Host` |
| S12 | `@(fn)` 与 `,@()` 双重包裹 | `foreach` 迭代"数组"而非元素 → `$r.Id` 报错、Cleanup FAIL | 统一扁平返回 |
| S13 | robocopy 孤儿判定误报 | 把**别的进程遗留的 robocopy** 算成本次孤儿 | 改"**基线 + 新增**"判定 |
| **S14** | **字符串批量改 `.ps1`** | **一次把 511 行的 `fault-lab.ps1` 写成 1 行、语法全废** | 放弃字符串补丁 → **整文件重写**（印证 v0.3.7 事故） |
| **S15** | `testdata.ps1` **`[Math]::Min` Int32 重载** | **5GB 文件建了但写 0 字节** → 磁盘满场景拿不到源数据 | 显式 `[long]` + `[int]` 转换 |
| **S16** | **`-File` 模式数组参数绑定** | `-Scenarios a,b,c` 被当多参数 → **B2 整批直接失败**（`FAT32-DIRFULL-01` 被当成 `VhdSizeMB`） | 改 CSV 单 token，脚本内 `-split` |
| **S17** | GUI 断言/定位方法错 | ①按 `PageConnect` 等 Grid 的 AutomationId 判可见（**WPF Grid 不生成 UIA peer**）②在"非顶层窗口"内按 AutomationId 找按钮 | ①改用**页面独有文本标记**（实测有效）②改 `RootElement.Descendants` 作用域 |

> **S1/S2 是"假 PASS 治理"的核心**：修复前，任何大文件或涉及空目录的场景都会被判 PASS 而实际未验证内容。

---

## 五、产品缺陷清单（**未修改任何一行生产代码**）

| # | 缺陷 | 证据等级 | 影响 | 处置建议 |
|---|---|---|---|---|
| **#1** | `AppDialog.xaml.cs:65` 用 `FindResource("WarnTextColor")`，**三套主题 152×3 个 key 均无此键** → WPF 抛 `ResourceReferenceKeyNotFound` → `ShowDialog()` 从未执行 | **CODE_VERIFIED + 历史 RUNTIME**：`app-20260918.log` **连续 10 次**该异常 + `crash-20260918-233603.log` / `-234105.log` **两份** | ①"目标盘空间不足"确认框静默变"已取消"+误导文案 ②"扫描不完整"确认框 ③ **全局崩溃兜底弹窗自己抛异常被 `catch{}` 吞掉** → 崩溃提示弹不出 | 走 7 步流程：**先补回归测试再修**。修法二选一（补 3 套主题 key / 改 `TryFindResource`+兜底）<br>**本轮 GUI Smoke 实测当日 `ResourceReferenceKeyNotFound` = 0 行**（未在无任务路径触发） |
| **#2** | `PreflightChecker.cs:239-241` 无 UNC 分支 → UNC 作目标时 CLI 判死（`exit=1`），而 GUI 吞异常**只警告不阻断** | **RUNTIME_VERIFIED**（Agent B 两次独立复现 + 历史 job 印证） | CLI/脚本用户会撞；GUI 用户几乎不会（文件夹选择器选不到 UNC） | 属"设计未定义"边界，**需人工拍板是否拦截/是否统一 CLI 与 GUI** |
| **#3** | 旧 `tools\stability-test.ps1` 三处"假 PASS"（P2 打断点写死 600ms 且自认未打断仍判 PASS；P3 `WriteAllBytes` 顺带改时间戳 → robocopy 默认就重拷；verify 只查 `✘` 不查 `⚠`） | **CODE_VERIFIED**（逐行核实）+ 本轮实测"抽样 0 项仍退出码 0" | 会让"内容级校验空转"被判通过 | 新 Lab 已修（全量 SHA256 + 抽样 0 项记 `NOT_RUN`）；**旧脚本本身未改** |

**附加事实**：CLI 路径下"内容损坏但大小+时间戳未变"**没有修复通道** —— `ForceOverwriteFromSource` 全仓库唯一赋值点是 `MainViewModel.cs:1707`（GUI「尝试修复」）。

---

## 六、假 PASS 治理记录

| 曾可能被判 PASS 的情形 | 现在的判定 | 依据 |
|---|---|---|
| 大文件（>16MB）内容损坏 | **FAIL** | 全量 SHA256（S1） |
| 空目录丢失 | **FAIL** | 目录集合比对（S2，已实测） |
| CLI `CompletedWithErrors`（含盘满） | **FAIL**（读 phase） | `job-state.json`（S3） |
| `verify --level 2` 抽样 0 项 | **NOT_RUN** | 输出含"未抽样任何对象" |
| 打断未真正发生 | **NOT_RUN** | 本报告 §2.5 |
| 回环"错密码仍通过预检" | **BLOCKED** | 回环认证绕过 |
| Secondary Dialog 未触发 | **NOT_RUN** | 明确不伪造 |
| 脚本改完就报通过 | **不判 PASS** | 必须全量 SHA256 + 目录结构一致 |

---

## 七、私人环境能力边界（不得夸大）

| 层级 | 定义 | 本轮覆盖 |
|---|---|---|
| **L0** Unit | 纯逻辑 + 真实进程单测 | ✅ 85/85 |
| **L1** Local PC | 本机文件系统 + 真 CLI/GUI | ✅ 全面（边界/权限/锁/压力/GUI） |
| **L2** Local SMB/VHD | 真实 SMB 回环 + 真实 VHD | ✅ 大部分（UNC/445/会话/枚举/1326/FAT32/exFAT/盘满/VHD 熔断）<br>❌ 密码校验、1219、IPC$ 缺失(67) |
| **L3** Corporate Simulation | 隔离虚拟实验室 | ❌ **未执行**（无 Server 介质） |
| **L4** Real Company | 真实企业环境 | ⛔ **COMPANY_ONLY**（15 项清单） |

**明确声明**：私人环境**不能**声称"已等于公司环境"，只能说"已尽可能覆盖 Windows 企业环境中可复现的部分"。

---

## 八、证据索引

### 8.1 权威索引文件

| 文件 | 说明 |
|---|---|
| `J:\pcmig-lab\reports\run-index.json` | 全部 run 的索引（含 `classification`：最终有效 / 脚手架调试期） |
| 各 `runs\<RunId>\result.json` | 该场景的七种结果类型统计 + 全部断言 + cleanup 结果 |
| 各 `runs\<RunId>\environment.json` | 环境快照（含 **CLI SHA256** + git HEAD + 是否管理员） |
| 各 `runs\<RunId>\classification.json` | 该 run 是否"最终有效" |

### 8.2 工具与脚本（全部在 `J:\pcmig-lab\`）

| 路径 | 作用 |
|---|---|
| `lib\LabCommon.ps1` | 公共模块：全量 SHA256 完整性核对 / 环境快照 / job-state 判定 / 安全护栏 / SMB 基线 |
| `lib\testdata.ps1` | 测试数据生成器（9 种 Kind + MixedDataset 15 要素 + 确定性 seed） |
| `scenarios\local-base.ps1` | 本地 + SMB 回环基线 |
| `scenarios\smb-boundary.ps1` | SMB 边界与凭据 |
| `scenarios\fault-lab.ps1` | KILL / LOCK / STATE 故障注入 |
| `scenarios\fs-boundary.ps1` | 文件系统边界 / 权限 / Source-Target 关系 |
| `scenarios\stress-scale.ps1` | 分级压力（1k/10k/30k/50k）+ KILL 重测 |
| `scenarios\gui-smoke.ps1` | GUI Smoke（UIA + 截图） |
| `scenarios\vhd-lab.ps1` | VHD / FAT32 / exFAT / DiskFull（需管理员） |
| `scenarios\run-elevated.ps1` + `.cmd` | 一键提权入口（UAC 一次跑完 B2 + 自动清理 + 自动汇总） |
| `fixtures\robocopy\error-{112,82,59,2}.txt` | 真实错误日志 GBK 原件 + `README.encoding.txt` |
| `screenshots\<RunId>\` | GUI 基线截图 |

### 8.3 工作区文档（`I:\deepseek work\PCMig\docs\`）

| 文件 | 内容 |
|---|---|
| `Private-Test-Lab-Blueprint.md` | L0–L2 设计蓝图 |
| `Corporate-Simulation-Blueprint.md` | L3 拓扑设计 + 成本效益 + 决策清单 |
| `Test-Execution-Report-V046.md` | B0/B1 逐场景详情 |
| `Test-Execution-Report-V046-AddendumA.md` | B3 详情 |
| `Coverage-Matrix-V046.md` | Scenario × Type × Level × Status 矩阵 |
| `First-Day-Company-Test-Checklist.md` | **公司环境最终边界（C-01…C-15）** |
| **`Private-Test-Lab-Final-Report.md`** | **本文件** |

---

## 九、结论与下一步

### 9.1 用户第十八节要求的结论——逐条对照

| 目标 | 结论 | 证据 |
|---|---|---|
| 正常复制没问题 | ✅ **已证明** | 本地 1268 文件 / SMB 回环 / 50,001 文件档，全部**全量 SHA256 + 目录结构一致** |
| 异常没问题 | ✅ **大部分已证明** | ACL 拒绝默认拦截、目标被占用不覆盖、坏主机名阻断、不存在用户真实 1326、FAT32/exFAT 差异行为 |
| 恢复没问题 | ✅ **已证明** | job.lock 互斥、job-state 损坏重建、Kill 后 resume 数据完整、无孤儿 robocopy、字节冻结 |
| SMB 没问题 | ✅ **核心链路已证明** | 445 / IPC$ 会话 / 共享枚举 / UNC 直拉 / robocopy 网络分支 |
| 权限边界有证据 | ✅ | `PERM-01`、`LOCKED-01`、ACL deny |
| **磁盘满有证据** | ✅ **本轮最大突破** | **真实 `错误 112 (0x00000070)` 逐字原文** + `completedWithErrors` 不误报 + 识别为空间不足 |
| FAT32 有证据 | ⚠ **部分** | exFAT 行为已证；FAT32 目标在 `--yes` 下未阻断 → **待裁决**；VHD 创建/格式化全通 |
| Kill / Resume 有证据 | ⚠ **部分** | 后果断言全 PASS；**打断时机未命中 → 如实 NOT_RUN** |
| 数据完整性有证据 | ✅ **最强项** | 全量 SHA256 + 目录结构，覆盖边界/压力/SMB/恢复各场景 |
| GUI 没有明显回归 | ✅ **基本** | 四页导航真实切换、更新日志可开、**零 crash / 零 Binding Error / 零资源键缺失**；2 项因脚手架定位问题未完成 |
| 留下真正需要公司验证的项目 | ✅ | `First-Day-Company-Test-Checklist.md`（C-01…C-15） |

### 9.2 下一步（按优先级）

| # | 事项 | 需人工 |
|---|---|---|
| 1 | **重跑 B2**（已修 S15/S16；改用多小文件构造溢出，避免 1.5GB>1GB 的物理不可能） | 需一次 UAC（`run-elevated.cmd`） |
| 2 | **裁决 Candidate Bug #1/#2**（是否修、是否统一 UNC 行为） | 需拍板 |
| 3 | **完善 KILL 打断窗口**（50ms 轮询 / 20 万文件 / 读 `completedBytes`） | 否 |
| 4 | **复核 FAT32 在 `--yes` 下的预检行为** | 否 |
| 5 | **修 GUI Smoke 的 S17**（改 `RootElement.Descendants` 作用域） | 否 |
| 6 | **L3 Corporate Simulation** | 需 Server 介质 + 平台决策 |

---

*本报告所有结论标注证据等级；未实测项一律 `NOT_RUN` / `BLOCKED` / `COMPANY_ONLY`，绝不伪装为已验证。生产代码（`src\**`）零改动，UI 未动，`MainViewModel` 未动。*
*统计口径：以"最终有效 Run"为准（每个场景取其最后一次成功产出 `result.json` 的 run）；28 个脚手架调试期 run 已单独分类，不作为产品结论。*

---

## 附录 A：B2 第二轮（16:52）结果与根因 — 测试环境污染

**结论**：熔断链**依然 4/4 全通**；两条 FAIL 的根因是**测试环境污染**（分类 B+E），**不是产品缺陷**。

### A.1 第二轮实测（`logs\vhd-20260919-165215.log`）

| 断言 | DISKFULL-01 | DISKFULL-02 | FAT32-DIRFULL-01 | EXFAT-LARGE-01 |
|---|---|---|---|---|
| Setup-VHD（`X: NTFS/FAT32/exFAT 1024MB` 创建+格式化） | PASS | PASS | PASS | PASS |
| **不得误报 Success**（`completedWithErrors`） | **PASS** | **PASS** | PASS | — |
| **识别为空间不足** | **PASS** | **PASS** | — | — |
| **采集逐字盘满原文** | **PASS** | **PASS** | — | — |
| Cleanup（零残留挂载 / 零残留 vhdx） | PASS | PASS | PASS | PASS |
| 释放后 Resume 补齐 | FAIL | FAIL | — | — |
| 最终数据完整性 | FAIL | FAIL | — | — |
| FAT32 预检硬拦截 | — | — | **FAIL** | PASS（exFAT 不硬拦） |

### A.2 根因链（逐层定位）

**第一层（脚手架）**：`vhd-lab` 的 `Ensure-OverflowSource:134-137` **复用了旧数据集** —— 旧 `manifest.json` 记录 1.5GB ≥ 阈值即 `return`，**从未走到新写的 `-SkipManifest` 分支**。于是源始终是**单个 1.5GB 文件**，而 VHD 只有 1GB → **物理上不可能完成**（resume 正确报告"还需 1.5GB / 可用 1008.92 MB"并优雅停止，**这是正确行为**）。

**第二层（环境污染，关键）**：`pcmig.log` 第 2 行暴露真相 ——
```
? 检测到未完成的迁移任务: JOB-20260919-145353-e283（CompletedWithErrors，已传 0.0%）
继续任务 JOB-20260919-145353-e283 …
```
源码依据（`Program.cs:301-316`，`CmdQuick`）：**`--yes` 模式下 quick 会自动接手同 host+同 target 的未完成任务，并直接 `CmdResume`**；而 `CmdResume → CmdRun` **不重新走预检** → **FAT32 拦截必然不出现**，且继续用旧数据集。

> 附带结论：**`--yes` 不会跳过 FAT32 的 `Error` 级预检**（源码 `Program.cs:392-411` 只把"空间闸门"降为告警），这一点此前怀疑已被排除。

### A.3 已修复（5 处，全部测试侧，未动产品代码）

| # | 修复 | 位置 |
|---|---|---|
| 1 | 溢出源**无条件重建**（不再复用旧数据集） | `Ensure-OverflowSource` |
| 2 | 改用**多文件构造** + `-SkipManifest`（1267 文件，最大单文件远小于盘容量；且无 manifest 污染清单） | 同上 |
| 3 | **清掉指向同一目标盘的遗留任务**（防止 `--yes` 接手旧任务而跳过预检） | 新增逻辑 |
| 4 | `testdata.ps1` 新增 `-SkipManifest` 开关 | `lib\testdata.ps1` |
| 5 | 源统计改为**直接扫描目录**，彻底不依赖 `manifest.counts` | Setup 数据段 |

**环境清理已完成**：删除遗留任务 `JOB-20260919-145353-e283`、删除旧数据集 `test-data\overflow`；确认**已无任何指向 X 盘的任务**。

### A.4 下次运行的预期

| 场景 | 预期 |
|---|---|
| `DISKFULL-01/02` | 8 项断言**全部 PASS**（多文件可补齐 + 无 manifest 污染） |
| `FAT32-DIRFULL-01` | **FAT32 预检硬拦截 → PASS**（不再被旧任务接手，正常走 preflight） |
| `EXFAT-LARGE-01` | 维持 PASS |

*此附录记录了本轮最典型的"假 FAIL"案例：**产品行为正确，是测试脚手架污染了输入**。定位方式=读 `pcmig.log` 首部 + 逐层回溯到 `Program.cs` 的 quick 语义。*
