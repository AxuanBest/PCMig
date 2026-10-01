# PMML UI 修改硬性规范（短版执行规则）

> 适用：**PCMig** 全部 UI 改动 ｜ 完整规范：`docs/PCMig-Visual-Motion-Language.md` ｜ 实现审计：`docs/PMML-Implementation-Audit.md`
> 本文件是**改 UI 前必读、改完必过**的执行规则短版。与完整规范冲突时以完整规范为准；与 `docs/发版铁律.md`（死条律）冲突时以死条律为准。

---

## 一、什么时候必须走本规范

| 改动内容 | 要求 |
|---|---|
| 尺寸 / Margin / Padding / 颜色 / Shadow / Material / ControlTemplate / Motion / 布局 | **必须**先读完整规范 + 本文件，完成后执行 §四 的 PMML Compliance Gate |
| 文案 / 数字 / 状态文字 / 日志文本 / 数据绑定内容 / 纯业务逻辑（**视觉零影响**） | 只需在说明里写一行 `PMML Visual Impact: None`，**不走**完整 Gate |

判定口径：**只要它可能改变任何一个像素，就走 Gate。**

---

## 二、术语速查（PMML v1.0）

| 缩写 | 全称 | 一句话职责 |
|---|---|---|
| **PMML** | PCMig Material Motion Language | 整套内部设计语言（本项目自有，非 Apple/Microsoft/WinUI 官方规范） |
| **DAM** | Desktop Acrylic Material System | 背景材质：Tint / Transparency / Luminosity / Edge Highlight / Light-Dark 适配 |
| **LMDS** | Layered Material Depth System | L0 Backdrop / L1 Base / L2 Elevated / L3 Interactive / L4 Overlay·Tool —— 新 Surface 必须归层 |
| **DSL-45** | 45° Directional Surface Lighting | 全局唯一虚拟光源：左上 → 右下约 45° |
| **ESR** | Subtle Embossed Material Relief | 轻浮雕：亮边 + 细边框 + 柔阴影（不是夸张 Neumorphism） |
| **DCST** | Directional Cross-Slide Transition | 主页面 Step1↔2↔3↔4 的方向性交叉滑动 |
| **OACT** | Origin-Anchored Container Transform | Tool Surface 如何**动**（Translation + Scale + Opacity，源点锚定） |
| **TAOP** | Trigger-Anchored Overlay Placement | 浮层**在哪里**（触发器锚定；与 OACT 严格区分） |
| **MHE** | Material Hover Elevation | Hover **不等于换色**：按真实实现包含高光/阴影/抬升 |

> 这些是 **PCMig 项目内部术语**。可以在文档里注明其对应的行业概念（如 Acrylic、Composition animation、Flyout placement），**不得**把它们描述成 Apple / Microsoft / WinUI 的官方规范名称。

---

## 三、硬性规则（PMML-R）

| 规则 | 内容 |
|---|---|
| **PMML-R1** | 单一光源：**DSL-45**（左上→右下）。新组件不得自行改变光源方向。 |
| **PMML-R2** | 单一材质族：新 Surface 必须从 **DAM** 派生（不得手写新的半透明白框）。 |
| **PMML-R3** | 所有 Surface 必须明确属于 **LMDS** L0–L4 之一；禁止自创 L2.5 / L3.7。 |
| **PMML-R4** | **Blur 只能属于 Surface Background Material；Foreground 必须锐利**：Text / Icon / CheckBox / ComboBox Item / List Item 不得进入 Blur Visual。L0/L4 是主要 blur-bearing layers，**获准的 DAM Acrylic Variant 可存在于其它语义层**。 |
| **PMML-R5** | 页面导航 = **DCST**（禁止某页自己 Fade / 自己 Scale / 从右边飞入）。 |
| **PMML-R6** | Tool Overlay = **OACT + TAOP**（怎么动 + 在哪里，两者都要交代）。 |
| **PMML-R7** | Hover = **MHE** 家族（不得退化成"只是换个底色"）。 |
| **PMML-R8** | **动画不得成为业务逻辑依赖**：动画失败/被关闭不得改变任何业务状态。 |
| **PMML-R9** | Light / Dark 属于**同一套设计系统**：同一 Surface Family + 不同 Theme 参数。 |
| **PMML-R10** | 禁止用**负 Margin / 随意 Translate / 魔法 Scale** 掩盖错误布局。 |
| **PMML-R11** | **同一状态不允许有多个 UI 写入者**（TreeView Row Push 的教训正式写入）。 |
| **PMML-R12** | Popup / Flyout 必须**明确 Placement ownership**，不得靠系统默认行为碰运气。 |
| **PMML-R13** | 任何 Material Blur 不得导致文字/图标模糊。 |
| **PMML-R14** | 新 UI **优先复用**现有 Resource / Token / Style；确需新增时说明理由并登记。 |
| **PMML-R15** | **偏离 PMML 必须显式授权**；未授权偏离不得合入。 |

---

## 四、PMML Compliance Gate（改完必须逐项声明）

```
PMML Compliance
---------------
DAM:                        PASS / N/A
LMDS:                       PASS / N/A
DSL-45:                     PASS / N/A
ESR:                        PASS / N/A
Motion Family:              DCST / OACT / MHE / State / N/A
TAOP:                       PASS / N/A
Typography:                 PASS
Foreground Sharpness:       PASS
Light/Dark:                 PASS / Known Gap
Accessibility:              PASS / Known Gap
Legacy Deviation Introduced: NO / YES（必须列出并登记到 PMML-Legacy-Deviations.md）
```

- 任一项 **FAIL ⇒ 不允许直接合入**。
- `Known Gap` 必须在 `docs/PMML-Legacy-Deviations.md` 里有对应条目（含文件:行号与原因）。
- 新增 Surface / Token / 控件族，必须同时更新完整规范与审计文档。

---

## 五、Accessibility / Reduced Motion

- 若当前实现**没有** Reduced Motion 检测：按完整规范登记的 `PMML v1.0 Known Gap` 处理，**本轮不重构**；
- 任何 Reduced Motion 行为都**不得改变业务状态**（只降低 Translation / Scale，保留 Opacity，功能态不变）。

---

## 六、与死律的关系

本规范只覆盖**设计语言一致性**，不豁免 `docs/发版铁律.md` 的任何一条：功能冻结、先写日志再打包、五道闸门、SHA256、发版后三验、两类独立证据等照旧执行。