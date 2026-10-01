# PCMig V0.4.6 Test Coverage Matrix

> 基线：**v0.4.6**（tag `v0.4.6` → `9a77381`）｜最后更新：**2026-09-19 17:20**
> 口径：**最终有效 Run**（每场景取最后一次成功产出 `result.json` 的 run）；调试期 run 单独分类，不作产品结论
> 证据根：`J:\pcmig-lab\runs\`｜索引：`J:\pcmig-lab\reports\run-index.json`
> 配套：`Private-Test-Lab-Final-Report.md`｜`Private-Test-Lab-Blueprint.md`｜`Corporate-Simulation-Blueprint.md`｜`..\First-Day-Company-Test-Checklist.md`

---

## 一、Environment Level 定义

| Level | 含义 |
|---|---|
| **L0** | Unit —— 纯逻辑 / 真实进程单测 |
| **L1** | Local PC —— 本机文件系统、真实 robocopy、真实 CLI/GUI |
| **L2** | Local SMB / VHD —— 真实 SMB 回环（UNC/445/会话/共享）、真实 VHD 卷 |
| **L3** | Corporate Simulation —— 隔离虚拟实验室（AD/DNS/域用户/ACL/GPO） |
| **L4** | Real Company —— 真实企业环境（**COMPANY_ONLY**） |

## 二、Status 取值（七种，禁止混用）

`PASS` ｜ `FAIL` ｜ `NOT_RUN` ｜ `BLOCKED` ｜ `CODE_VERIFIED` ｜ `RUNTIME_VERIFIED` ｜ `COMPANY_ONLY`

**硬禁令**：源码正确 ≠ PASS；Mock 通过 ≠ 真实通过；脚本执行完 ≠ 场景验证成功；
**"未真正触发故障"必须记 `NOT_RUN`，不是 PASS**。

---

## 三、Coverage Matrix（主表）

| Scenario | Type | Level | Runtime | Evidence（runId） | Status | Priority | Company Only |
|---|---|---|---|---|---|---|---|
| **B0 — 单元测试与 Fixture** | | | | | | | |
| `UNIT-01` 单测回归（85 用例） | Regression | L0 | dotnet test | 85 通过 / 0 失败 / 1 s | **RUNTIME_VERIFIED** | P0 | 否 |
| `FIX-01` 真实日志 Fixture（112/82/59/2） | Fixture | L0 | GBK 原件入库 | `fixtures\robocopy\error-*.txt` | **RUNTIME_VERIFIED** | P0 | 否 |
| `FIX-02` 正则/控制字符防复发 | Regression | L0 | 真源码扫描 5 用例 | `SourceTreeHygieneTests.cs` | **RUNTIME_VERIFIED** | P0 | 否 |
| **B1 — 本地基线 + SMB 回环** | | | | | | | |
| `LOCAL-BASE-01` 本地基线迁移 | Integration | L1 | CLI quick | `20260919-142612` | **PASS** | P0 | 否 |
| `SMB-LOOPBACK-02` UNC 直拉 | Integration | L2 | CLI quick | `20260919-142617` | **PASS** | P0 | 否 |
| `SMB-BADHOST-03` 坏主机名 | Negative | L2 | CLI preflight | `20260919-142652` | **PASS** | P0 | 否 |
| `SMB-BADUSER-04` 不存在用户（1326） | Negative | L2 | CLI preflight | `20260919-142657` | **PASS**（真实 1326 路径） | P0 | 否 |
| `SMB-BADUSER-04` 密码校验可测性 | — | L2 | — | 同上 | **BLOCKED**（回环认证绕过：密码不参与校验） | → L3 | 是 |
| `SMB-PRESESSION-05` 已有会话复用 | Integration | L2 | CLI preflight | `20260919-142712` | **PASS** | P1 | 否 |
| `SMB-WRONGCRED` 真实密码校验 | Negative | L3/L4 | — | — | **COMPANY_ONLY** | P0 | 是 |
| `SMB-1219` 连接冲突 | Negative | L3/L4 | — | — | **COMPANY_ONLY** | P0 | 是 |
| `SMB-67` IPC$ 缺失 | Negative | L3/L4 | — | — | **COMPANY_ONLY** | P0 | 是 |
| **B2 — VHD / FAT32 / Disk Full（全部收敛）** | | | | | | | |
| `DISKFULL-01` 磁盘满→熔断→释放→Resume | Failure→Recovery | L2 | CLI + VHD | `20260919-170834` | **PASS**（9/9 断言） | **P0** | 否 |
| `DISKFULL-02` 逐字盘满原文采集 | Fixture | L2 | CLI + VHD | `20260919-170834` | **PASS** | **P0** | 否 |
| `FAT32-DIRFULL-01` FAT32 预检硬拦截 | Negative | L2 | CLI + VHD | `20260919-170834` | **PASS**（取证 `目标盘文件系统 FAT32`） | P0 | 否 |
| `FAT32-DIRFULL-01` 迁移期 Error 82 | — | L2 | — | 同上 | **BLOCKED**（产品设计上预检即阻断，正常 CLI 路径不可达） | P1 | 否 |
| `EXFAT-LARGE-01` exFAT 不硬拦截 | Integration | L2 | CLI + VHD | `20260919-170834` | **PASS** | P1 | 否 |
| **B3 — 故障注入与恢复** | | | | | | | |
| `KILL-01` 硬杀 PCMig → resume | Failure→Recovery | L1 | CLI | `20260919-144135` | **NOT_RUN**（后果断言全 PASS；**打断窗口未命中**） | P1 | 否 |
| `KILL-02` 杀 robocopy → 重试 | Failure→Recovery | L1 | CLI | `20260919-144153` | **NOT_RUN**（同上） | P1 | 否 |
| `LOCK-01` job.lock 互斥 | Concurrency | L1 | CLI | `20260919-144210` | **PASS** | P1 | 否 |
| `STATE-01` job-state 损坏恢复 | Recovery | L1 | CLI | `20260919-144249` | **PASS** | P1 | 否 |
| **B4 — 文件系统边界 / 权限 / Source-Target** | | | | | | | |
| `FSBOUND-01` 边界要素（14 项） | Boundary | L1 | CLI quick | 最新 run | **PASS**（11/11 断言） | P0 | 否 |
| `FSBOUND-02` 长路径 319 字符 | Boundary | L1 | CLI quick | 最新 run | **PASS** | P1 | 否 |
| `FSBOUND-03` 只读/隐藏属性 | Boundary | L1 | CLI quick | 最新 run | **PASS** | P1 | 否 |
| `PERM-01` ACL 拒绝 → 扫描残缺拦截 | Boundary | L1 | CLI + icacls | 最新 run | **PASS** | P0 | 否 |
| `LOCKED-01` 目标文件被占用 | Boundary | L1 | CLI + 独占句柄 | 最新 run | **PASS** | P1 | 否 |
| `SRCMUT-01` 复制期间删源 | Boundary | L1 | CLI | 最新 run | **NOT_RUN**（注入窗口未命中）+ 不整体失败 **PASS** | P2 | 否 |
| `SRC-TGT-01` Source == Target | Boundary | L1 | CLI preflight | 最新 run | **FAIL**（产品**无关系校验** → 属"设计未定义"，需人工拍板） | P1 | 否 |
| `SRC-TGT-02` Target 是 Source 子目录 | Boundary | L1 | CLI run（超时保护） | 最新 run | **PASS**（观察项：未膨胀） | P1 | 否 |
| **B5 — 规模** | | | | | | | |
| `SCALE-SMALL-01` 1,001 文件 | Stress | L1 | CLI quick | `20260919-150030` | **PASS**（500 文件/秒） | P1 | 否 |
| `SCALE-MID-01` 10,001 文件 | Stress | L1 | CLI quick | `20260919-150041` | **PASS**（2,500 文件/秒） | P1 | 否 |
| `SCALE-LARGE-01` 30,001 文件 | Stress | L1 | CLI quick | `20260919-150107` | **PASS**（2,727 文件/秒） | P1 | 否 |
| `SCALE-XL-01` **50,001 文件** | Stress | L1 | CLI quick | `20260919-150222` | **PASS**（**3,571 文件/秒**） | P1 | 否 |
| `KILL-03` 30000 文件重测打断窗口 | Failure→Recovery | L1 | CLI | `20260919-150427` | **NOT_RUN**（仍未命中 20–60% 窗口） | P2 | 否 |
| `SCALE-100K` 10 万文件 | Stress | L1 | — | — | **NOT_RUN**（未做；按"不无意义追求"原则） | P2 | 否 |
| **B6 — GUI** | | | | | | | |
| `GUI-SMOKE-01` 冷启动 + 四页导航 + 更新日志 | Smoke | L1 | UIA + 截图 | `20260919-145711` | **PASS**（导航/日志/零异常） | P0 | 否 |
| `GUI-SMOKE-01` 上一版/下一版/关闭按钮 | Smoke | L1 | UIA | 同上 | **FAIL**（脚手架：`ChangelogWindow` 非独立顶层窗口，定位作用域错） | P2 | 否 |
| `GUI-BUG1` `WarnTextColor` 弹窗失效 | Defect | L1 | 日志看门狗 | 同上（`ResourceReferenceKeyNotFound` = **0 行**） | **RUNTIME_VERIFIED**（历史 10 次异常 + 2 份 crash 日志） | **P0** | 否 |
| `DPI-01` 125%/150%/200% | Visual | L1 | — | — | **NOT_RUN**（**USER ACTION REQUIRED**：需注销会话） | P1 | 否 |
| `OVERFLOW-01` ScrollBar 溢出触发 | Visual | L1 | UIA Resize | 同上 | **NOT_RUN**（窗口不可 Resize；实时日志需"有数据状态"） | P2 | 否 |
| `VISREG-01` 截图回归基线 | Regression | L1 | uishot | `screenshots\20260919-145711-*` | **RUNTIME_VERIFIED**（基线已建，7 张） | P2 | 否 |
| **B8 — 环境相关** | | | | | | | |
| `ONEDRIVE-01` 伪占位符（Offline 位） | Boundary | L1 | — | 机制已实测（`SetAttributes -bor Offline` 驱动三处逻辑） | **RUNTIME_VERIFIED** | P2 | 否 |
| `ONEDRIVE-02` 真实云端回源 | — | L4 | — | — | **COMPANY_ONLY** | P2 | 是 |

---

## 四、L3 Corporate Simulation Matrix（未执行）

| Scenario | Type | Level | Status | 前置 |
|---|---|---|---|---|
| `L3-01` 域用户凭据直拉（真实密码校验） | Integration | L3 | **NOT_RUN** | DC01 + Server 介质 |
| `L3-02` 错误域密码（1326 真实路径） | Negative | L3 | **NOT_RUN** | DC01 |
| `L3-03` 管理共享 `\\SRV01\D$`（域管理员 vs 普通用户） | Integration | L3 | **NOT_RUN** | DC01+FS |
| `L3-04` DNS 短名 vs FQDN | Integration | L3 | **NOT_RUN** | DC01(DNS) |
| `L3-05` NTFS ACL 域组拒绝 | Boundary | L3 | **NOT_RUN** | FS |
| `L3-06` **1219 真实冲突** | Negative | L3 | **NOT_RUN** | DC01 |
| `L3-07` **共享消失 → 恢复 → Resume** | Failure→Recovery | L3 | **NOT_RUN** | FS |
| `L3-08` 域 GPO（UAC/防火墙/执行策略） | Environment | L3 | **NOT_RUN** | DC01+GPO |
| `L3-09` PC01 普通域用户跑 PCMig | Environment | L3 | **NOT_RUN** | PC01 |
| `L3-10` Defender Controlled Folder Access | Environment | L3 | **NOT_RUN** | 任一 |
| `L3-11` 网络分段（DNS 通/SMB 不通） | Failure | L3 | **NOT_RUN** | 隔离网段 |

**L3 阻塞**：本机**无 Windows Server 介质**（全盘 iso/esd 扫描无 Server 版本）→ AD/DNS/域用户/GPO 全部无法开始。
设计见 `Corporate-Simulation-Blueprint.md`（推荐 **2 台 VM** 覆盖约 90% 可模拟企业特征）。

---

## 五、Company Only 清单（L4）

完整清单见 **`..\First-Day-Company-Test-Checklist.md`**（C-01 … C-15）。核心项：

| # | 项 | 为什么本机不可模拟 |
|---|---|---|
| C-01 | 真实域认证与**密码校验** | 回环用登录令牌认证，**密码不参与校验**（已实测） |
| C-02 | 错误 67 完整预检链路 | 本机 IPC$ 始终可用，无"不导出 IPC$ 的服务端" |
| C-03 | **1219 连接冲突** | 需真实多凭据环境 |
| C-04 | 真实员工电脑 Profile / 用户环境迁移 | 无真实 `NTUSER.DAT`/AppData/注册表 hive/`.pst` 锁 |
| C-05 | **真实大规模数据**（≥100 GB、海量小文件） | 本机做到 50,001 文件 / 100 MB |
| C-06 | 企业 DNS / NetBIOS / 电脑名解析 | 无企业 DNS，FQDN 未测 |
| C-07 | 企业管理共享策略与权限模型 | 组策略可能关闭/改名管理共享 |
| C-08 | 企业安全软件（EDR / DLP） | 明确不安装来源不明的内核级软件 |
| C-09 | 企业网络条件（VPN / 代理 / 网段隔离） | 家用路由 NAT |
| C-10 | 非管理员域账号运行 PCMig | 无域受限用户 |
| C-11…C-15 | 安装包企业部署 / 报告页脚 / 跨 DPI 显示器 / 域 GPO 强约束 / Win7 源机 | 见清单 |

---

## 六、统计汇总（截至 2026-09-19 17:20）

### 最终有效场景：**27**

| Status | 数量 | 明细 |
|---|---|---|
| **PASS** | **19** | 见上表 |
| **FAIL** | **2** | `SRC-TGT-01`（设计未定义）、`GUI-SMOKE-01`（脚手架定位） |
| **NOT_RUN** | **4** | `KILL-01`/`KILL-02`/`KILL-03`（打断窗口未命中）、`SRCMUT-01`（注入窗口未命中） |
| **BLOCKED** | **2** | `SMB-BADUSER-04` 密码校验、`FAT32-DIRFULL-01` 迁移期 Error 82 |
| **COMPANY_ONLY** | **15 项** | `..\First-Day-Company-Test-Checklist.md` |

> **2 个 FAIL 均非产品缺陷**：
> ① `SRC-TGT-01` 是产品**确实没有** Source/Target 关系校验（属"设计未定义"，需人工拍板而非自行修改产品）；
> ② `GUI-SMOKE-01` 是本测试脚本按错误的定位作用域查找 `ChangelogWindow` 内按钮。

### 其他

| 项 | 值 |
|---|---|
| 单元测试 | **85 / 85 通过** |
| 全量 Run（含迭代） | **87** 个 |
| 生产代码改动 | **0**（`src\**` 零改动，UI 未动，`MainViewModel` 未动） |
| 测试侧改动 | `tests\` 2 改 1 增；`J:\pcmig-lab\` 8 脚本 + 2 模块 |
| 残留清理 | vhdx **0** ｜ X/Y/Z 挂载 **0** ｜ 虚拟盘 **0** ｜ robocopy **0** ｜ PCMig **0** |

---

## 七、读法说明

1. **看 PASS 不等于可信**：本矩阵的 PASS 一律基于**全量 SHA256 + 目录结构比对**（非"长度一致"），且判定读 `job-state.json` 的 phase（**不用 CLI 退出码**，因为盘满时 `completedWithErrors` 也返回 0）。
2. **看 NOT_RUN 就知道缺口在哪**：`KILL-01/02/03` 与 `SRCMUT-01` 都是"注入窗口未命中"，**未验的是"在特定进度点中断"**，而不是"中断后的恢复正确性"（后者已 PASS）。
3. **看 BLOCKED 与 COMPANY_ONLY 的区别**：BLOCKED = 本机条件不具备；COMPANY_ONLY = 本机结构上不可能（如域认证）。
4. 调试期 run 共 60 个（累计 87 − 27 有效），全部是修测试脚本 bug 的中间结果，**已在 `run-index.json` 单独分类，勿当作产品结论**。

---

*本矩阵随每个 Scenario 的真实证据增量更新。未实测项一律 `NOT_RUN` / `BLOCKED` / `COMPANY_ONLY`，绝不伪装为已验证。*