# PCMig 项目指令（AGENTS.md）

> 本文件是该项目的**强制开工指令**。任何 AI 会话接手本项目，**开工前必读并逐条遵守**。
> 最高约束：**`docs/发版铁律.md`（死条律）**，本文件只是入口摘要；冲突时以铁律为准。

## 一、这是什么东西

**PCMig** —— 企业内网 Windows 换机数据迁移工具（C#/.NET 8，WPF GUI + CLI，Robocopy 双通道引擎）。
在新电脑运行，输入旧电脑 IP/电脑名 + 凭据，经 SMB 把旧机共享盘数据"直拉"过来。
当前版本 **v0.4.1**（2026-09-18）。

## 二、坐标（唯一权威，不许用错）

| 用途 | 路径 |
|---|---|
| **权威工作区（唯一事实来源）** | `I:\deepseek work\PCMig` |
| **交付区（对外交付物只落这里）** | `I:\PCMig` |
| 工作副本（发版脚本自动建） | `D:\PCMig` |
| 源码镜像备份 | `I:\镜像备份源码\PCMig` |
| 发版脚本 | `I:\deepseek work\PCMig\tools\release.ps1` |
| 稳定性测试台 | `tools\stability-test.ps1` |
| 截图工具 | `tools\uishot.ps1` |

**`I:\K\deepseek work` 是旧的工作文件（移动硬盘镜像副本），只可作覆盖目标，绝不可作为事实依据、代码来源或判断基准。**

工作区根目录纪律：根目录只允许 `PCMig\`、`AGENTS.md`、`INDEX.md` 与 `labs\`/`archive\`/`projects\`/`dsh-data` 四个分类目录，一次性脚本一律写进 `archive\scripts\`。

## 三、九条不可违反的死律（详见 `docs/发版铁律.md`）

1. **功能冻结**（自 v0.2.28）：只做修复、加固、纯视觉层。禁止动 ViewModel / Command / 绑定 / 迁移与 Robocopy 逻辑 / 状态机 / 错误处理 / 网络检测；禁止改存档格式（`job-state.json` 字段语义、`plan.json`、Receipt）。
2. **先写日志，再打包**：`docs/更新日志.md`（正文一节 + 对照表一行）、`docs/使用说明.txt`（顶部一段）写好才许发版。
3. **发版必须走脚本**，禁止手工打包：`powershell -NoProfile -ExecutionPolicy Bypass -File "I:\deepseek work\PCMig\tools\release.ps1" -Version X.Y.Z`；五道闸门必须全绿；一个版本号只发一次。
4. **交付四件套 + 逐文件 SHA256 全 MATCH**：安装包、`I:\PCMig\Portable\`、`I:\PCMig\更新日志.txt`（UTF-8 BOM+CRLF）、工作副本。
5. **发版后三验**：应用内「更新日志」/ 记事本打开 txt / `pcmig changelog`；本轮根因·修法·验证追加进 `docs/测试报告-公司环境.md`。
6. **两类独立证据**：复现证据 + 修好证据（不同方法）。故障类 **连续复现 3 次 + 修复后回归 3 次**，正常路径不退化；数据正确性必跑 `tools\stability-test.ps1`。
7. **看不见的东西不改**：UI 改动走"修改→编译→启动→`uishot.ps1` 截图→视觉模型自查→通过才继续"闭环，范围锁死在 Theme/Style/模板层；回退用 `PCMIG_CLASSIC_UI=1` 或 exe 旁 `classic-ui.flag`。
8. **编码禁区**：所有 `.ps1` 必须 **UTF-8 带 BOM**（编辑后复查前三字节 `EF BB BF`）；禁止注释式批量替换（v0.3.7 事故）；XAML 隐式 Style 只改不新建；验证必须跑刚 Build 的新 EXE；截图必须定位 PCMig 真实窗口；真实口令零残留（脚本第 5 道闸门）。
9. **禁止破坏性 git 命令**：不执行 `git reset --hard` / `git checkout .` / `git restore .` / `git clean -fd`（会不可逆抹掉未提交成果）；撤销改动用 `git stash` 或 `git revert`。

## 四、每次交付版本时必须做的事

1. 逐条自查 `docs/发版铁律.md` 第十部分清单，**并在回复中声明遵守**。
2. 给出：交付物绝对路径、哈希校验结论、三验结果、写进测试报告的条目。
3. 做不到 / 没实测 / 有疑问的，**如实说明**，不许瞒、不许装懂；有疑问先问用户再动手。
4. 技术路线与历史决策**先查 `docs/更新日志.md` 与 `docs/测试报告-公司环境.md`**，不凭猜测。

## 五、其他

- 敏感凭据（`settings.yaml`、历史会话文稿）一律用"见某处"引用，不复制明文。
- 触碰磁盘请分清本机与公司机：本机能用的只有 `I:`、`D:`、`K:`；`\\Szlt500781`、域账号等公司环境资源本机不可达。
- V1.5 路线图（Outlook Recipe 等）在功能冻结解除前**不主动开工**。