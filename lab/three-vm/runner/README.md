# runner\ —— UI 自动化操作 Runner（能力脚本集）

> 来源：`E:\Project\deepseek work\`（工作区根）2026-10-06 归位，27 个脚本。
> 定性（§9 判据 D「未来仍有复用价值」）：**测试能力脚本，保留**。
> canonical 主库仍是 `E:\PCMigLab\Staging\recovery-gate\uia-lib.ps1`（本目录是能力副本，用于在仓库内直接复用与追溯）。

## 一、能力分组

### 1. 窗口与启动（先把 PCMig 真实窗口弄到前台）
| 脚本 | 作用 |
|---|---|
| `uia_preview.ps1` / `uia_preview2.ps1` | 启动应用 + 定位主窗口 + 打印 UIA 树概览 |
| `uia_run.ps1` | 通用「启动 → 等待 → 操作 → 收尾」骨架 |
| `uia_page.ps1` | 页面级导航（Step1–Step4 切换 + 元素就绪判定） |
| `uia_check.ps1` | 元素存在性 / IsEnabled / 文本断言 |

### 2. 点击与交互
| 脚本 | 作用 |
|---|---|
| `uia14.ps1` `uia18.ps1` | InvokePattern 点击（**必须用 InvokePattern，坐标点击对 `Shell.Transfer.Start` 无效**） |
| `uia19.ps1` `uia19b.ps1` `uia19c.ps1` | 暂停 / 恢复 / 停止控件交互序列 |
| `uia20.ps1` `uia20b.ps1` `uia20c.ps1` `uia20d.ps1` | 浮层（flyout）light-dismiss 与强制激活对照实验组 |
| `uia_p1.ps1` `uia_p2.ps1` `uia_p3.ps1` | 三阶段交互回归（对应 Step1/Step2/Step3 主要动作） |
| `uia_q2.ps1` `uia_q3.ps1` | 快速回归子集（冒烟用） |

### 3. 滚动与到达页面底部
| 脚本 | 作用 |
|---|---|
| `uia_bottom.ps1` `uia_bottom2.ps1` | 滚动到内容底部（ScrollPattern / 滚轮计数） |
| `scrollpat.ps1` `scrollpat2.ps1` | 通用 ScrollPattern 封装 |

### 4. 取值与像素归因
| 脚本 | 作用 |
|---|---|
| `uia_credit.ps1` `uia_credit2.ps1` | 读取进度相关 credited / confirmed 数值（只读断言） |
| `shots.ps1` | 批量截图（**截图必须带 SELFCHECK**） |

### 5. OCR
| 脚本 | 作用 |
|---|---|
| `ocr.ps1` | 屏幕文字提取（用于无法从 UIA 读到的自绘文本的**辅助**核对） |

## 二、使用纪律（长期有效）

1. **视觉终验永远是人工**：本目录任何脚本的输出都只是**过程证据**，不得代替用户目视验收结论。
2. **截图必须带 SELFCHECK**；屏幕像素归因必须使用**同一次运行**的 UIA 原点，不得跨运行拼接坐标。
3. **ROI 锚点只认 `aid=TotalImmersiveProgress`**；不得用其它元素或绝对像素当锚。
4. 窗口激活只认 `Shell.Application` 的 `MinimizeAll()` + `UndoMinimizeAll()`；`SetForegroundWindow` / `SetWindowPos(HWND_TOPMOST)` / `PrintWindow` 会拿到陈旧或错误窗口。
5. 启动迁移必须用 UIA `InvokePattern`；鼠标坐标点击对 `Shell.Transfer.Start` 无效。
6. 所有 `.ps1` 必须 UTF-8 **带 BOM**（前三字节 `EF BB BF`）。
7. 装饰层（Band / 粒子 / Ripple / Halo）永不许影响 `Value` / `Percent` / `ConfirmedBytes` / `Receipt` / `Verifier` / `JobState`；脚本断言不得去读装饰层来推断进度真值。