# PCMig 项目指令（AGENTS.md）

> 本文件是该项目的**强制开工指令**。任何 AI 会话接手本项目，**开工前必读并逐条遵守**。
> 最高约束：**`docs/发版铁律.md`（死条律）**，本文件只是入口摘要；冲突时以铁律为准。

## 一、这是什么东西

**PCMig** —— 企业内网 Windows 换机数据迁移工具（C#/.NET 8，WPF GUI + CLI，Robocopy 双通道引擎）。
在新电脑运行，输入旧电脑 IP/电脑名 + 凭据，经 SMB 把旧机共享盘数据"直拉"过来。
**当前状态（2026-10-01 Workspace 治理后）**。开工前以 `docs\INDEX.md` 与当前工作交接为准；下表哈希与数字均为治理当时从工作区实读，不凭记忆。旧状态（如「当前版本 v0.4.8（2026-09-19）」「PMML 仍是 Freeze Candidate」「Diagnostics 未获实施授权」「docs 顶层历史交接才是入口」「Solution 不完整」）**一律作废**。

| 项 | 当前值 |
|---|---|
| 权威工作区（唯一事实来源） | `E:\Project\deepseek work\PCMig`（交付区 `E:\Project\PCMig`；`I:\K\deepseek work` 永不作为事实依据或代码来源） |
| 文档入口 | `docs\INDEX.md`（**唯一导航入口**） |
| 当前工作交接（Current Handover） | `docs\工作交接-20261001-Workspace治理完成与D6.2起点.md`（旧的 D6.1 交接已归档在 `docs\handover\history\`，**只作历史追溯，不再是 Current**） |
| Workspace Governance Phase 2 | **COMPLETE** |
| Git Recovery Baseline | **ESTABLISHED** |
| Solution Governance | **COMPLETE** —— `PCMig.sln` 覆盖当前 **8 个工程**（含 `src\PCMig.WinUI` 与 `src\PCMig.Diagnostics`；治理前只有 6 个） |
| Docs Governance | **COMPLETE** —— docs 顶层 `.md` 83 → **29**；历史交接在 `docs\handover\history\`（42 篇），只作追溯 |
| PMML | **v1.0 — FROZEN** |
| Diagnostics | **D6.1 — COMPLETE** |
| 发布线 / 开发线 | 发布 **v0.4.9**；开发 **v0.5.0 WinUI**（分支 `feature/winui-v0.5.0`，**未发版**） |
| 当前 Git | 分支 `feature/winui-v0.5.0`：checkpoint **`2c0183b`** → solution **`7ba2bc1`** → governance **`c5e668c`** → 交接与报告 **`73fcb05`** / **`6f002ac`** / **`beb2902`** → **HEAD `f592d8c`** |
| 验证基线 | Build `dotnet build PCMig.sln -c Release` → **0 error / 4 warning**（全部既有：xUnit2031 ×1 + WMC1506 ×3）；Tests **Core 290 + Diagnostics 286 = 576 / 0**。证据：`archive\pcmig-governance-20261001\s1-build.log`、`s1-test.log` |
| 下一步 | **D6.2 Real World Validation**（等用户明确触发语；未获授权前不得开始） |
| Stage B | **NOT AUTHORIZED / NOT STARTED** |
| 三 VM / 210 万文件 / 大规模故障注入 | **NOT STARTED** |
| Mnemon memory sync | **PARTIAL / Provider path available** —— 当前状态摘要已写入 Mnemon Documents（Document `57fe2a88-20ee-4c6b-ac7a-6cacf95cb4d1`）；CLI 直写路径在本机不可用（缺 `mnemon.exe`，Memory Space 写入被拒）。**这不代表治理未完成**，待 Provider 可用后再补同步 |

## 一点五、新会话必读链（开工顺序，强制）

任何新的 PCMig 会话 / Agent，开工前按下列顺序读，不得跳读、不得在几十篇历史 Markdown 里随手挑一篇当依据：

1. `E:\Project\deepseek work\AGENTS.md`（工作区最高约束）
2. `E:\Project\deepseek work\PCMig\AGENTS.md`（本文件：死律 + 权限条款 + PMML 入口 + 当前状态）
3. `docs\INDEX.md`（当前文档**唯一导航入口**）
4. 由 `docs\INDEX.md` 指向的 **Current Handover**
5. 再按任务类型读对应专项文档：
   - **UI / 视觉 / 动效** → PMML 链：`docs\PCMig-Visual-Motion-Language.md` → `docs\PMML-UI修改硬性规范.md` → `docs\PMML-Implementation-Audit.md` → `docs\PMML-Legacy-Deviations.md`
   - **Diagnostics** → `docs\方案-诊断中心与自诊断架构.md` + `docs\诊断系统实施-阶段证据.md` / `诊断系统实施-事件覆盖矩阵.md` / `诊断系统实施-配置项接线审计.md` / `诊断系统实施-D6.1-进度.md`
   - **QA / Testing** → `docs\qa\` + `docs\测试报告-公司环境.md` + `docs\First-Day-Company-Test-Checklist.md` + `docs\首日实测检查表.md`
   - **Release** → `docs\发版铁律.md` + `docs\发布流程.md` + `docs\稳定性守则.md` + `docs\稳定性验收标准.md`
   - **History** → `docs\handover\history\` + `docs\archive\`（**只作历史追溯，不得当作当前规则或当前状态依据**）

`docs\INDEX.md` 是当前文档的唯一导航入口，但**它不是最高约束**：最高约束仍是本文件、工作区根 `AGENTS.md`、`docs\发版铁律.md` 及 PMML 等专项强制规则，INDEX 与当前交接都不得覆盖它们，冲突时以约束层为准。INDEX 只解决一件事：防止新会话在几十篇历史 Markdown 中随机挑文档。

## 二、坐标（唯一权威，不许用错）

| 用途 | 路径 |
|---|---|
| **权威工作区（唯一事实来源）** | `E:\Project\deepseek work\PCMig` |
| **交付区（对外交付物只落这里）** | `E:\Project\PCMig` |
| 工作副本（发版脚本自动建） | `D:\PCMig` |
| 源码镜像备份 | `E:\Project\镜像备份源码\PCMig` |
| 发版脚本 | `E:\Project\deepseek work\PCMig\tools\release.ps1` |
| 稳定性测试台 | `tools\stability-test.ps1` |
| 截图工具 | `tools\uishot.ps1` |

**`I:\K\deepseek work` 是旧的工作文件（移动硬盘镜像副本），只可作覆盖目标，绝不可作为事实依据、代码来源或判断基准。**

工作区根目录纪律：根目录只允许 `PCMig\`、`AGENTS.md`、`INDEX.md` 与 `labs\`/`archive\`/`projects\`/`dsh-data` 四个分类目录，一次性脚本一律写进 `archive\scripts\`。

## 三、十一条不可违反的死律（详见 `docs/发版铁律.md`）

1. **功能冻结**（自 v0.2.28）：只做修复、加固、纯视觉层。禁止动 ViewModel / Command / 绑定 / 迁移与 Robocopy 逻辑 / 状态机 / 错误处理 / 网络检测；禁止改存档格式（`job-state.json` 字段语义、`plan.json`、Receipt）。
2. **先写日志，再打包**：`docs/更新日志.md`（正文一节 + 对照表一行）、`docs/使用说明.txt`（顶部一段）写好才许发版。
3. **发版必须走脚本**，禁止手工打包：`powershell -NoProfile -ExecutionPolicy Bypass -File "E:\Project\deepseek work\PCMig\tools\release.ps1" -Version X.Y.Z`；五道闸门必须全绿；一个版本号只发一次。
4. **交付四件套 + 逐文件 SHA256 全 MATCH**：安装包、`E:\Project\PCMig\Portable\`、`E:\Project\PCMig\更新日志.txt`（UTF-8 BOM+CRLF）、工作副本。
5. **发版后三验**：应用内「更新日志」/ 记事本打开 txt / `pcmig changelog`；本轮根因·修法·验证追加进 `docs/测试报告-公司环境.md`。
6. **两类独立证据**：复现证据 + 修好证据（不同方法）。故障类 **连续复现 3 次 + 修复后回归 3 次**，正常路径不退化；数据正确性必跑 `tools\stability-test.ps1`。
7. **看不见的东西不改**：UI 改动走"修改→编译→启动→`uishot.ps1` 截图→视觉模型自查→通过才继续"闭环，范围锁死在 Theme/Style/模板层；回退用 `PCMIG_CLASSIC_UI=1` 或 exe 旁 `classic-ui.flag`。
8. **编码禁区**：所有 `.ps1` 必须 **UTF-8 带 BOM**（编辑后复查前三字节 `EF BB BF`）；禁止注释式批量替换（v0.3.7 事故）；XAML 隐式 Style 只改不新建；验证必须跑刚 Build 的新 EXE；截图必须定位 PCMig 真实窗口；真实口令零残留（脚本第 5 道闸门）。
9. **禁止破坏性 git 命令**：不执行 `git reset --hard` / `git checkout .` / `git restore .` / `git clean -fd`（会不可逆抹掉未提交成果）；撤销改动用 `git stash` 或 `git revert`。
10. **交接文档只增不覆**：用户要求交接时，必须**新增**而非覆盖 `docs\工作交接-YYYYMMDD-主题.md`，完整记录本会话事实、证据、回退、限制和下一步；所有历史交接文档永久保留；凭据一律不写明文。
11. **子模型优先执行，主控负责规划与验收**（铁律 50）：能安全、有效地由子模型/子代理独立承担的工作必须优先委派并尽可能扩大其范围；主控只负责总体规划、任务拆分、授权与约束边界、关键决策、结果交叉核验与最终验收，并对项目铁律、备份、证据、发版与安全负最终责任，不得把未核验的子模型结论当事实，也不得以委派为由免责。

## 三点五、权限放宽不构成豁免（AI 强制条款，v0.4.7 起）

本节为**工具权限与项目纪律的边界条款**，与工作区根 `E:\Project\deepseek work\AGENTS.md` 同名条款互为补充，冲突时**以更严者为准**。

会话权限预设切到 **完全权限**（`danger-full-access` + 审批 `never`）、或 DSH 客户端以**管理员身份**启动、或文件沙箱不再限制工作区外写入时，本条自动生效：

1. **权限只影响效率，不豁免纪律。** 第三节十条死律一条不少地继续适用。"现在不弹窗了""我是管理员"**不是**跳过先写日志再打包、五道闸门、SHA256 全 MATCH、发版后三验、两类独立证据的理由。
2. **"不再弹窗" ≠ "已获授权"。** 权限放宽只取消确认提示，不授权任何具体动作。以下动作**即使技术上完全可做，也必须先拿到用户当轮明确指令**：
   - 删除/覆盖/移动 `I:\PCMig` 交付区内容、`D:\PCMig` 工作副本、`I:\镜像备份源码\PCMig`；
   - 修改 `tools\release.ps1`、`tools\stability-test.ps1`、`tools\uishot.ps1` 等发版与验证脚本；
   - 改动功能冻结范围（第 1 条死律清单）内的代码，或 `job-state.json` / `plan.json` / Receipt 的存档格式；
   - 执行任何破坏性 git 命令、清理未提交改动。
3. **破坏性动作先报路径与回退方案**：动手前说明"要动哪些路径、能否回退、备份在哪"，没有可回退路径就先备份。
4. **机器权限不得升格为事实依据**：`I:\K\deepseek work`（旧移动硬盘镜像）即便可写，也永远不是权威源或代码来源。

## 三点六、PMML v1.0 设计语言入口（硬规则）

**任何影响 PCMig UI 视觉、布局、材质、动画或 ControlTemplate 的修改，开工前必须先读
`docs/PCMig-Visual-Motion-Language.md` 与 `docs/PMML-UI修改硬性规范.md`，并在完成后按
`PMML Compliance Gate` 逐项声明（PASS / N/A / Known Gap）。** 只改文案、数字、状态文字、日志文本、
数据绑定内容与业务逻辑（视觉零影响）的修改，写 `PMML Visual Impact: None` 即可，不走完整 Gate。

## 四、每次交付版本时必须做的事

1. 逐条自查 `docs/发版铁律.md` 第十部分清单，**并在回复中声明遵守**。
2. 给出：交付物绝对路径、哈希校验结论、三验结果、写进测试报告的条目。
3. 做不到 / 没实测 / 有疑问的，**如实说明**，不许瞒、不许装懂；有疑问先问用户再动手。
4. 技术路线与历史决策**先查 `docs/更新日志.md` 与 `docs/测试报告-公司环境.md`**，不凭猜测。

## 五、其他

- 敏感凭据（`settings.yaml`、历史会话文稿）一律用"见某处"引用，不复制明文。
- 触碰磁盘请分清本机与公司机：本机能用的只有 `I:`、`D:`、`K:`；`\\Szlt500781`、域账号等公司环境资源本机不可达。
- V1.5 路线图（Outlook Recipe 等）在功能冻结解除前**不主动开工**。
