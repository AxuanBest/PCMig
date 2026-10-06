# 三 VM 实验室框架（lab\three-vm）

> 本目录是**索引与框架说明**；大体积数据与正式证据的 canonical 位置在仓库外的实验室根 `<实验室根>\`。
> 建立：2026-10-06（PCMig v0.5.1 发版后工作区整理）。
> 纪律来源：用户《PCMig 发版后工作区整理 / 清理执行书（修订版）》§6「三 VM 框架必须完整保留」——**保留框架 ≠ 保留大体积数据**。

## 一、这是什么

PCMig 的**域环境 / SMB 共享 / 故障注入 / UI 自动操作**测试框架。三台 Hyper-V 虚拟机 + 宿主 + 一套场景脚本，用于在受控条件下复现企业内网换机迁移的真实约束：

域不可达、DNS 不可达但 IP 可达、共享被撤销、权限不足、目标盘已满、路径过长、文件被锁、断网 / 断电。

覆盖的验证链路：`Scan → Plan → Transfer → Verify`，以及暂停 / 恢复 / 停止 / 强杀后恢复的**进度真实性**。

## 二、虚拟机（2026-10-06 实测快照，原始数据见 `configs\`）

| VM | 角色 | State | Gen | vCPU | 内存 | 虚拟磁盘 |
|---|---|---|---|---|---|---|
| LAB-DC01 | 域控 + DNS | Off | 2 | 2 | 4096 MB | OS |
| LAB-SRC01 | 源机（旧机） | Off | 2 | 4 | 6144 MB | OS + Data1 |
| LAB-DST01 | 目标机（新机） | Off | 2 | 4 | 6144 MB | OS + Data1 + Data2 |

- 虚拟交换机：`PCMig-Lab-Switch`
- MAC：DC01 `00155DFC4304` / SRC01 `00155DFC4302` / DST01 `00155DFC4303`
- ⚠ 三个 VM 当前**全部挂在检查点差分盘（`.avhdx`）**上 ⇒ 存在 Hyper-V 检查点。合并 / 移除检查点可回收约 **315 GB**，但会丢失回滚点，属破坏性动作 —— **需人工明确授权后才可执行**，本次未动。
- canonical 路径：`<实验室根>\VMs\<VM名>\`

## 三、宿主共享（2026-10-06 实测，完整输出见 `configs\host-shares-20261006.txt`）

| 共享名 | 路径 | 用途 |
|---|---|---|
| `PCMIG-DS1` | `<宿主共享根>\ds-small` | 12,000 文件小文件树（Scan / Plan 压力、进度粒度） |
| `PCMIG-DS2` | `<宿主共享根>\ds-large` | 大文件 payload 根（`\big`），场景 H 的 42 GiB 源 |
| `Users` | `C:\Users` | 用户目录直连场景 |

> `ds-large\big` 与 `src\big` 的 42 GiB / 3.3 GiB payload 已于 2026-10-06 按 §7 删除（保留目录骨架），需要时用 `scenarios\make-dataset.ps1` 重新生成。

## 四、目录结构

| 目录 / 文件 | 内容 |
|---|---|
| `README.md` | 本文件：框架总览与 canonical 路径表 |
| `docs\` | 文档索引（RouteA 规格、三 VM 复验包位置、场景说明） |
| `scripts\` | 用例库与生成器脚本副本（`lib-cases.ps1` / `labfile.ps1`） |
| `runner\` | **UI 自动化操作 Runner**：UIA 点击 / 滚动 / 截图 / OCR 能力脚本（27 个） |
| `configs\` | VM / 网络 / 磁盘 / 共享配置快照（可重新生成） |
| `scenarios\` | 场景定义与核心库（`round3-scenario-G/H`、`uia-lib.ps1`、`pause-resume-continuity.ps1` 等 22 个） |
| `evidence-template\` | 证据采集格式说明与模板 |

## 五、canonical 路径表（重要：本目录不替代下列位置）

| 资产 | canonical 位置 |
|---|---|
| VM 磁盘与配置 | `<实验室根>\VMs\` |
| 用例矩阵（Case matrix）与故障注入 | `<实验室根>\Staging\ctl\`（`cases\` / `faults\` / `host\` / `lib-cases.ps1` / `run-*.ps1`） |
| 场景执行库与 UI 自动化主库 | `<实验室根>\Staging\recovery-gate\`（`uia-lib.ps1` / `round3-scenario-*.ps1` / `pause-resume-continuity.ps1` / `case0*.ps1`） |
| 小型 smoke 夹具（≤100 MB，唯一一套） | `<实验室根>\smoke-data\` |
| 正式证据 | `<实验室根>\Evidence\`（`RouteA\` 等，禁删） |
| 三 VM 复验包 | `<实验室根>\three-vm\review-packages\` |
| 仓库内 L3 编排脚本 | `tools\l3-*.ps1` / `tools\l3-*.cmd` / `tools\pcmiglab-vm.ps1` |
| 仓库内历史证据批次 | `PCMig\archive\evidence\`（`a6-pause-resume` / `a7-two-ui-bugs` / `diagnostics-d61-20261001` 等） |
| 仓库内历史自动化脚本 | `PCMig\archive\scripts\` |

## 六、2026-10-06 清理口径（删数据、留能力）

**删除**（424.48 GB synthetic payload，§7 / §12）：

```
<实验室根>\Staging\{phE-run2, handoff-look, phA5-gate, phA4-gate, phE-gate, pcmig-publish}
<实验室根-R3>\{scenG, shotcheck, shotcheck2, bc-check}
<宿主共享根>\ds-large\big\*.bin   (6 个)
<宿主共享根>\src\big\*.bin        (3 个)
```

**保留**：VM 本体与配置、全部场景脚本与生成器、用例矩阵、故障注入库、UI 自动化 Runner、smoke 夹具、证据目录。

**重新生成**：见 `scenarios\README.md` 的「数据重新生成」一节（生成脚本、参数、预期体积）。

## 七、执行纪律（长期有效）

1. 装饰层（Band / 粒子 / Ripple / Halo）永不许影响 `Value` / `Percent` / `ConfirmedBytes` / `Receipt` / `Verifier` / `JobState`。
2. 截图必须带 SELFCHECK；屏幕像素归因必须用**同一次运行**的 UIA 原点；ROI 锚点只认 `aid=TotalImmersiveProgress`。
3. **视觉终验永远是人工**，不得用像素脚本代替结论。
4. 切换目标卷（挂 / 卸 VHD）前**必须先关闭应用**，否则 WinUI 主进程会 `Responding=False` 并空转烧 CPU。
5. 本机窗口激活只认 `Shell.Application` 的 `MinimizeAll()` + `UndoMinimizeAll()`；`SetForegroundWindow` / `SetWindowPos(HWND_TOPMOST)` / `PrintWindow` 都会拿到陈旧或错误窗口。
6. UIA 点击启动项必须用 `InvokePattern`，鼠标坐标点击对 `Shell.Transfer.Start` 无效。
7. 所有 `.ps1` 必须 UTF-8 **带 BOM**（前三字节 `EF BB BF`），否则 PowerShell 5.1 按 GBK 解析中文并报 `Missing closing ')'`。