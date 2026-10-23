# PMML Engineering Overview

> **PMML 是 PCMig 内部 Material + Motion Engineering Language。**
> **不是 Apple、Microsoft、WinUI 官方规范，也不是任何行业标准。**

本文档是 **PMML 工程技术档案体系的入口**。它说明这套档案**是什么、由什么组成、
各文档负责什么、彼此如何引用**。

| 项目 | 值 |
| --- | --- |
| 文档类型 | Engineering Documentation（体系入口） |
| 基线 commit | `b68613dc3600767e4e7cf2b24e413e5c91a16280` |
| 档案状态 | **READY FOR MAINTENANCE** |
| 术语权威 | `PMML-Terminology-Registry.md` |
| 记录权威 | `PMML-Source-Traceability.csv` |

---

## 一、PMML 是什么

### 1.1 定位

**PMML = PCMig Material Motion Language**，是 PCMig 的**项目内部**材质与动效设计语言。

它的存在目的**不是重新设计 UI**，而是：

- 给已经成熟的 UI 表现**命名**、**描述**、**冻结**；
- 让后续新增 UI 有**可引用的统一口径**；
- 让「哪些实现符合设计语言、哪些是历史偏差」**可查、可验证**。

### 1.2 边界声明（重要）

> **PMML terminology is project-internal terminology.**
> **It is not Apple, Microsoft, WinUI or industry official terminology.**

具体含义：

| 允许 | 禁止 |
| --- | --- |
| 注明某概念**对应**行业概念（如「DAM 对应 Desktop Acrylic 材质」） | 把 PMML 描述为 Apple HIG / Microsoft Fluent / WinUI 官方规范 |
| 在 PCMig 内部使用 PMML 术语作为唯一写法 | 把 PMML 术语当作跨项目通用标准对外宣称 |
| 引用 WinUI 官方 API 名（如 `MicaBackdrop`、`DesktopAcrylicBackdrop`） | 声称这些 API 的用法是 PMML 发明的 |

### 1.3 冻结状态

| 项 | 值 |
| --- | --- |
| 版本 | PMML v1.0 |
| 状态 | **FROZEN** |
| 冻结宣布 | 项目所有者，2026-09-30 |
| 冻结只能由 | **项目所有者**宣布解除 |
| 冻结后唯一变更 | 2026-10-01 文档状态纠正（正文未改） |

**FROZEN 的含义**：冻结的是**设计语言本身**（术语、层模型、规则、参数口径），
不是「禁止任何代码改动」。UI 可以继续演进，但必须走 **PMML Compliance Gate**。

---

## 二、技术分层

PMML 由**三个系统**构成。三者职责正交，不互相替代。

```
PMML — PCMig Material Motion Language
│
├── ① Material System — 材质系统
│      「界面看起来是什么材质、分几层、怎么受光、怎么起浮雕」
│   │
│   ├── DAM       Desktop Acrylic Material System     桌面亚克力材质系统
│   ├── LMDS      Layered Material Depth System       分层材质深度系统（L0–L4）
│   ├── DSL-45    45° Directional Surface Lighting    45° 方向性表面光照
│   └── ESR       Subtle Embossed Material Relief     细微浮雕材质
│
├── ② Motion System — 动效系统
│      「什么东西怎么动、从哪里出发、动多长时间」
│   │
│   ├── DCST      Directional Cross-Slide Transition  页面导航转场
│   ├── OACT      Origin-Anchored Container Transform 浮层开关形变
│   ├── TAOP      Trigger-Anchored Overlay Placement  浮层落点定位
│   └── MHE       Material Hover Elevation            悬停/按下反馈
│
└── ③ Progress Visual System — 进度视觉系统
       「迁移进度如何既真实又流畅地呈现」
    │
    └── Immersive Transfer Progress                  沉浸式迁移进度
```

### 2.1 为什么是这三个系统

| 系统 | 回答的问题 | 不负责 |
| --- | --- | --- |
| Material | 表面**是什么**（材质、层、光、浮雕） | 它怎么动 |
| Motion | 元素**怎么动**（转场、形变、定位、反馈） | 表面是什么材质 |
| Progress | **事实如何被可视化**（真值 / 活跃度分离） | 迁移业务逻辑本身 |

### 2.2 三个系统的交叉纪律

- **Material × Motion**：动效不得改变材质语义；MHE 只允许 Translation / Elevation（**禁止 Scale**）。
- **Motion × Progress**：进度装饰（Push Band / 粒子）**绝不**影响 `Value`；事实由 Head 表示，活跃度由装饰表示。
- **Material × Progress**：进度控件嵌入卡片时必须真正透出父 Surface（**R36**），不得自取窗口级底色。

---

## 三、档案体系组成

本档案体系位于 `docs/pmml-engineering/`，由**六份文档 + 一份数据 + 三个辅助目录**构成。

### 3.1 六份文档

| # | 文档 | 职责 | 何时读 |
| --- | --- | --- | --- |
| 1 | **`PMML-Engineering-Overview.md`**（本文） | 体系入口：PMML 是什么、三层结构、文档地图 | 第一次接触 PMML 时 |
| 2 | **`PMML-Terminology-Registry.md`** | **术语权威登记**：9 个术语 + 总称，每个含 Identifier / Full Name / 中文名 / 职责 / 底层实现 / 关联文档 / 状态 | 想知道某缩写是什么时 |
| 3 | **`PMML-Implementation-Map.md`** | 要素 → 源码落点索引（21 族 × 记录 × 裁定状态） | 想知道「这个要素在代码哪里」时 |
| 4 | **`PMML-Source-Traceability-Methodology.md`** | 追溯模型、可信证据四元组、行号漂移口径、重建流程 | 想理解 / 重建 CSV 时 |
| 5 | **`PMML-Evidence-Classification.md`** | 证据分级 A/B/C/D + 24 条 NO_PROBE 逐条登记 | 判断某结论有多可信时 |
| 6 | **`PMML-Engineering-Closure-Report.md`** | 收口报告：已完成、状态、未决、风险、维护规则 | 交接 / 审计时 |

### 3.2 数据文件

| 文件 | 内容 |
| --- | --- |
| **`PMML-Source-Traceability.csv`** | **152 条**逐条源码追溯（11 列）——本体系的**数据权威** |

### 3.3 辅助目录

| 目录 | 内容 | 性质 |
| --- | --- | --- |
| `history/` | 原 CSV 快照（`2c0183b` 时代）+ 快照说明 | **只读**，不再修改 |
| `diff/` | `差异报告.md`（人类可读）+ `relocation.csv`（逐行裁定） | 生成物，可重建 |
| `_tools/` | 6 个可复现脚本 | 生成物，可重建 |

### 3.4 非档案文件

| 文件 | 性质 | 说明 |
| --- | --- | --- |
| `_recovered.json` | **Intermediate Recovery Artifact** | 恢复/重建过程中产生的中间产物，**不属于** PMML Engineering Documentation。保留不删，以免丢失恢复链。详见收口报告 §3。 |

---

## 四、与既有 PMML 文档的关系

本档案体系**不替代**既有的四份 PMML 文档，而是它们的**工程化补充**。

| 既有文档 | 定位 | 与本体系的关系 |
| --- | --- | --- |
| `docs/PCMig-Visual-Motion-Language.md` | **设计规范**（PMML v1.0，FROZEN） | 本体系**引用**它；术语定义以它为准 |
| `docs/PMML-UI修改硬性规范.md` | **硬性执行规则**（R1–R36） | 本体系**引用**它；规则冲突时以它为准 |
| `docs/PMML-Implementation-Audit.md` | **实现审计**（v0.5.0 真实世界） | 本体系是其**结构化延续**（CSV 即审计的机器可读形态） |
| `docs/PMML-Legacy-Deviations.md` | **偏差登记簿**（L-01…L-19 + G-01…G-08） | 本体系**引用**它；状态字段与它对齐 |

### 4.1 权威优先级

当文档之间出现冲突时：

```
源码                      ← 最高（参数以实际代码为准）
  ↓
硬性规范（R1–R36）
  ↓
完整设计规范（PMML v1.0）
  ↓
本档案体系（Overview / Registry / Map / Methodology / Classification）
  ↓
实现审计 / 偏差登记簿    ← 记录历史状态，可能已过期
```

**注**：审计文档与偏差登记簿记录的是「**当时**的观察」，随代码演进会过期；
本体系通过 `Commit` 字段与 `Verdict` 裁定**显式标注**新鲜度。

### 4.2 新增于本体系的四件事

相比既有文档，本体系**新增**的是：

1. **可复现的源码锚点**（152 条，带 `Commit` 基线）——既有审计是散文式描述。
2. **证据分级**（Level A/B/C/D）——既有文档未区分证据强度。
3. **裁定口径**（`UNCHANGED` / `RELOCATED` / `NO_PROBE` / …）——既有文档无行号漂移处理规范。
4. **术语登记表**——既有文档的术语散在各处，无单一权威表。

---

## 五、文档引用关系图

```
                    ┌─────────────────────────────┐
                    │  PMML-Engineering-Overview  │  ← 入口（本文）
                    └──────────────┬──────────────┘
                                   │
        ┌──────────────────────────┼──────────────────────────┐
        │                          │                          │
        ▼                          ▼                          ▼
┌───────────────┐        ┌──────────────────┐       ┌──────────────────┐
│ Terminology   │        │ Implementation   │       │   Closure        │
│ Registry      │◄──────►│ Map              │◄─────►│   Report         │
│ (术语权威)     │        │ (源码落点索引)     │       │  (收口报告)       │
└───────┬───────┘        └────────┬─────────┘       └────────┬─────────┘
        │                         │                          │
        │                         ▼                          │
        │              ┌──────────────────────┐              │
        │              │  Source-Traceability │              │
        │              │  .csv  (152 条)      │◄─────────────┘
        │              └──────────┬───────────┘
        │                         │
        │            ┌────────────┴────────────┐
        │            ▼                         ▼
        │   ┌──────────────────┐    ┌──────────────────┐
        │   │ Methodology      │    │ Evidence         │
        │   │ (方法与口径)      │    │ Classification   │
        │   └──────────────────┘    │ (证据分级)        │
        │                           └──────────────────┘
        │
        ▼
┌───────────────────────────────────────────────────────┐
│ 上游既有文档（不在本目录）                              │
│  PCMig-Visual-Motion-Language.md   （设计规范）        │
│  PMML-UI修改硬性规范.md             （硬性规则）        │
│  PMML-Implementation-Audit.md      （实现审计）        │
│  PMML-Legacy-Deviations.md         （偏差登记簿）      │
└───────────────────────────────────────────────────────┘
```

---

## 六、怎么用这套档案

### 6.1 按任务查

| 我要做的事 | 读哪份 |
| --- | --- |
| 搞清 `OACT` 是什么 | Terminology Registry |
| 找 `ClampVisual` 的实现位置 | Implementation Map → CSV |
| 判断某条结论能不能当事实用 | Evidence Classification |
| 重建 / 更新 CSV | Methodology |
| 交接给别人 | Closure Report |
| 知道有哪些历史偏差 | 上游 `PMML-Legacy-Deviations.md` |

### 6.2 改动 UI 时（必读流程）

改任何可能影响像素的东西，必须：

1. 读 `docs/PMML-UI修改硬性规范.md`（R1–R36）
2. 读 `docs/PCMig-Visual-Motion-Language.md`（设计规范）
3. 改动后跑 **PMML Compliance Gate**（硬性规范 §四）
4. 若引入新的不统一 → Gate 的 `Legacy Deviation Introduced` 填 `YES`，并**同时**在
   `PMML-Legacy-Deviations.md` 新增一条
5. 更新本体系的 `PMML-Source-Traceability.csv`（受影响的 `FeatureId`）

### 6.3 纯文档改动

文案、数字、状态文字、数据绑定内容等**视觉零影响**的改动，只需一行：
`PMML Visual Impact: None`。

**判定口径**：只要它可能改变**任何一个像素**，就走 Gate。

---

## 七、当前状态摘要

| 项 | 值 |
| --- | ---: |
| 追溯记录 | 152 |
| 行号未变（`UNCHANGED`） | 67 |
| 行号漂移（`RELOCATED`） | 29 |
| 已补全（`LOCATED`） | 29 |
| 人工锚点（`MANUAL-ANCHOR`） | 2 |
| 待人工回读（`NO_PROBE`） | 24 |
| 已删除实现（`FILE_GONE`） | 1 |
| 证据 Level A / B / C / D | 117 / 5 / 6 / 24 |
| 术语数 | 1 总称 + 9 术语 |
| 已登记偏差（上游） | L-01…L-19 + G-01…G-08 |

**状态**：

> **PMML Engineering Documentation: READY FOR MAINTENANCE**
>
> **v0.5.4 Preview.1: NOT TESTED**

---

## 八、边界与不做的事

本档案体系**明确不做**：

| 不做 | 原因 |
| --- | --- |
| 重新设计 UI | PMML 是**命名与冻结**，不是设计 |
| 修改 `src/` / `tests/` | 工程档案只描述，不实现 |
| 修复 Legacy Deviation | 每条清理必须**单独授权**并走 Gate |
| 补齐 Dark 主题 | **G-01**，需项目所有者决策 |
| 落地 Reduced Motion | **G-02**，需项目所有者决策 |
| 宣称视觉已验证 | 缺真机证据时一律标 `NOT VISUALLY VERIFIED`（**G-08**） |
| 把 `NO_PROBE` 当作错误删除 | 它表示线索存在、证据未取得 |

---

## 九、关联文档

| 文档 | 位置 | 定位 |
| --- | --- | --- |
| `PMML-Terminology-Registry.md` | 本目录 | 术语权威登记 |
| `PMML-Implementation-Map.md` | 本目录 | 源码落点索引 |
| `PMML-Source-Traceability-Methodology.md` | 本目录 | 追溯方法论 |
| `PMML-Evidence-Classification.md` | 本目录 | 证据分级 |
| `PMML-Engineering-Closure-Report.md` | 本目录 | 收口报告 |
| `PMML-Source-Traceability.csv` | 本目录 | 152 条记录 |
| `PCMig-Visual-Motion-Language.md` | `docs/` | 设计规范（PMML v1.0） |
| `PMML-UI修改硬性规范.md` | `docs/` | 硬性规则 R1–R36 |
| `PMML-Implementation-Audit.md` | `docs/` | 实现审计 |
| `PMML-Legacy-Deviations.md` | `docs/` | 偏差登记簿 |
