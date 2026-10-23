# PMML Source Traceability Methodology

> 本文档说明 `PMML-Source-Traceability.csv` 的**数据来源、追溯模型、判定口径与重建流程**。
> 它是该 CSV 的**方法论说明**，不是设计规范；设计规范见 `docs/PCMig-Visual-Motion-Language.md`。

| 项目 | 值 |
| --- | --- |
| 文档类型 | Engineering Documentation（方法论） |
| 当前 HEAD | `b68613dc3600767e4e7cf2b24e413e5c91a16280` |
| 快照基线 commit | `2c0183b` |
| 记录总数 | 152 |
| 状态 | READY FOR MAINTENANCE |

---

## 一、数据来源

### 1.1 记录的原始来源

CSV 的 152 条记录是**按 PMML 设计语言要素逐条枚举**的审计产物，不是全量源码清单。

素材内容可确证来自一次四路并行源码盘点（Presentation / Themes / Views+App / ImmersiveProgress+tests 四个关注面），但**原文件的创建者尚未确认**——已排除本会话、四个 sweep 子代理与 Git 历史。详见 `history/README-快照说明.txt`。

### 1.2 覆盖范围

| 维度 | 值 |
| --- | --- |
| 记录数 | 152 |
| 列数 | 11 |
| Feature Family | 21 族 |
| 引用文件 | 全部落在 `src/PCMig.WinUI/` 下 |

**Feature Family 分布**：

| Family | 条数 | Family | 条数 |
| --- | ---: | --- | ---: |
| OACT | 14 | DCST | 8 |
| DAM | 12 | RULE | 8 |
| PROG | 12 | LMDS | 7 |
| HINT | 12 | IP | 7 |
| MHE | 11 | NAV | 6 |
| BOOT | 10 | VERIFY | 6 |
| TAOP | 9 | PROBE | 5 |
| DSL45 | 8 | 其余 7 族 | 15 |

### 1.3 明确不覆盖的内容

- **业务逻辑**（迁移引擎、Robocopy 参数、回执、验证闸门）—— 不属于设计语言
- **`src/PCMig.Core` / `src/PCMig.Cli` / `src/PCMig.Gui` / `src/PCMig.Diagnostics`** —— 非 UI 层
- **逐行全量源码清单** —— 本 CSV 是要素枚举，做全量差集会产生大量噪声

---

## 二、commit 基线

### 2.1 checkpoint commit：`2c0183b`

原 CSV 的行号对应 **`2c0183b`**（`checkpoint: preserve v0.5.0 PMML frozen and Diagnostics D6.1`），即 **v0.5.0 时代**。

**判定证据**（决定性）：

| 证据 | 内容 |
| --- | --- |
| 主证据 | `2c0183b` 版 `Presentation/MotionDirector.cs` 为 **666 行**，其 **L370 恰为** `var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.…`，与 CSV 中 `DCST-003` 的记录**完全吻合** |
| 反证 1 | 当前 HEAD 版同文件 **1163 行**，L370 是**注释** —— 不匹配 |
| 反证 2 | 工作树版同文件 **1219 行**，L370 是 `catch (Exception ex)` —— 不匹配 |
| 辅助证据 | CSV 把 `Materials.xaml` 的 Surface 样式记在 `:168-179`；当前树为 `:217-228`（偏移 **+49**），名称与数值全对 |

### 2.2 当前 HEAD：`b68613dc`

`b68613dc3600767e4e7cf2b24e413e5c91a16280`（2026-10-13，`候选版收口：四项定点修复`）。

CSV 的 `Commit` 字段逐行记录**该行号的验证基线**。当前 152/152 均为 `b68613dc…`。

---

## 三、追溯模型

### 3.1 八个字段

每条记录由以下字段构成，共同定义一个**可验证锚点**：

| 字段 | 含义 | 稳定性 |
| --- | --- | --- |
| `FeatureId` | 要素编号（`<Family>-<NNN>`） | **永久稳定**（重编号会断开历史引用） |
| **PMML Family** | 所属术语族（由 `FeatureId` 前缀表达） | 稳定 |
| `CanonicalEnglish` | 要素的规范英文名 | 稳定 |
| `CanonicalChinese` | 中文名 | 稳定 |
| `Mechanism` | 该要素的实现机制一句话 | 稳定 |
| `SourcePath` | 源码锚点（**文件:行号**） | ⚠ **会漂移** |
| `Symbol` | 标识符 / ResourceKey / 表达式（即 `SymbolOrResourceKey`） | 稳定 |
| `Commit` | 该行号的验证基线 commit | **每次重验证后更新** |
| `EvidenceType` | 证据类型（见 §五） | 稳定 |
| `ImplementationStatus` | 实现状态 | 稳定 |

### 3.2 为什么 `FeatureId` 而不是 `Symbol` 作主键

同一个符号名可能在多个文件出现（如 `Install`、`ApplyTruth`）。`FeatureId` 是**人对一条设计语言要素的命名**，不依赖源码结构，因此在重构后仍然有效。

### 3.3 行号不是身份

**行号只是「上次看到它时它在哪里」的坐标，不是该要素的身份。**
要素的身份 = `FeatureId` + `Symbol` + `SourcePath` 的**文件部分**。

---

## 四、可信证据

### 4.1 四元组

一条记录若要被当作**可信证据**，必须同时具备：

```
Commit + Path + Symbol + Context
```

| 成分 | 作用 | 缺失后果 |
| --- | --- | --- |
| **Commit** | 说明这个行号是在哪个代码状态下验证的 | 无法判断是「漂移」还是「记录本来就不对」 |
| **Path** | 定位到文件（行号可漂移，文件不会无故改名） | 无法复查 |
| **Symbol** | 在文件内唯一定位该要素 | 无法区分同名符号 |
| **Context** | 该行附近的实际语义（函数、注释、调用链） | 可能命中同名但不同义的符号 |

### 4.2 单有行号不构成证据

反例（本档案的真实教训）：

- `TAOP-001` 记录 `MainWindow.xaml.cs:1604`。若只用「行号 + `TryPlacePanel`」搜索，会先命中 `:1200` 的**调用处**；而 `:1604` 才是**定义处**。仅凭行号或仅凭符号名都会给出误导结果，必须 `Path + Symbol + Context` 同时满足。

### 4.3 证据强度声明纪律

任何结论必须标注证据强度，**不得用「探针已证明」替代「用户真机所见」**：

| 标注 | 含义 |
| --- | --- |
| `VERIFIED FIXED（真机证据）` | 有真机可复现证据（截图 / UIA / 像素测量） |
| `CODE FIXED` | 代码已改，但未做真机视觉验证 |
| `NOT VISUALLY VERIFIED` | 只有代码与单元测试，缺真机视觉对照 |
| `OPEN` | 问题仍存在 |

---

## 五、Evidence Type（证据类型）

每次重验证时，把每条记录归入以下**七类之一**：

| Evidence Type | 定义 | 判据 |
| --- | --- | --- |
| **Symbol Definition** | 源码中某符号的**定义处**（类、方法、属性、常量声明） | 锚点行是声明语句本身 |
| **Call Site** | 某符号的**调用处**（调用点而非定义点） | 锚点行含对该符号的调用 |
| **Resource Definition** | XAML 资源的**定义处**（`x:Key` / `Style` / `Brush`） | 锚点行在 ResourceDictionary 内 |
| **Resource Usage** | XAML 资源的**引用处**（`{StaticResource X}` / `{x:Bind}`） | 锚点行引用已有资源键 |
| **Comment Claim** | 仅存在于**注释**中的主张（可能与实现不符） | 锚点行是注释 |
| **Behavioral Observation** | 通过运行/审计**观察到的行为**，非静态定义 | 需运行或交互才能观察 |
| **Manual Verification** | 人工回读源码后确认的结论 | 人工逐行核对，非自动工具产出 |
| **UNCONFIRMED** | 尚未确认 | 无法自动检索或无足够证据 |

> **注意**：`Evidence Type` 描述的是**这条证据是怎么获得的**，`Implementation Status` 描述的是**该要素在代码中的状态**。两者正交，不可互相替代。

---

## 六、行号变化：RELOCATED 不是错误

### 6.1 五种裁定

重验证时对每条记录给出**一个**裁定：

| Verdict | 含义 | 是否算问题 |
| --- | --- | --- |
| `UNCHANGED` | 行号与当前 HEAD **完全一致** | ❌ 不是问题 |
| `RELOCATED` | 同文件内找到，**行号漂移** | ❌ **不是问题**（见 §6.2） |
| `LOCATED` | 原记录**未给行号**，本次补全到具体行 | ❌ 不是问题（是改进） |
| `MANUAL-ANCHOR` | 自动搜索落点不准确，**人工回读后指定正确锚点** | ❌ 不是问题（是修正） |
| `NO_PROBE` | 原记录只写 `Comment`/表达式，**无可检索标识符** | ⚠ 需人工回读 |
| `FILE_GONE` | 引用文件在当前 HEAD **不存在** | ⚠ 需登记 |

### 6.2 为什么行号变化不属于错误

**行号是文件内容的函数，不是要素的属性。** 任何在其上方插入或删除行的改动，都会让行号整体位移。这是正常的代码演进，不是溯源错误。

反例：`Themes/Materials.xaml` 的 Surface 样式块整体 **+49 行**，导致 16 条记录同时位移。**没有一条是错的**——文件、符号、数值全部正确，只是坐标变了。

**因此**：`RELOCATED` 记录**不需要修正 CSV**，只需要在下一次引用时按新行号读取。真正需要处理的是 `NO_PROBE` 与 `FILE_GONE`。

### 6.3 漂移成因分类

| 成因 | 说明 | 典型 |
| --- | --- | --- |
| 文件增行 | 新增内容把原有内容整体下推 | `Materials.xaml` +49 |
| 文件重构 | 方法被大幅改写，行数剧增 | `MotionDirector.cs` 666 → 1163（v0.5.4 动效回归修复 +61 行净增，含重构） |
| 内容删除 | 死代码被删，后续内容上移 | 见 `FILE_GONE` |
| 搜索假象 | **不是漂移**：搜索命中了更早出现的同名符号 | `TAOP-001` 命中 `:1200` 调用处而非 `:1604` 定义处 |

**最后一条尤其重要**：它意味着「自动搜索报告漂移」**不等于**「真的漂移」。判定时必须先验证原行号是否仍然成立。

---

## 七、重建流程

### 7.1 完整管道

```
原 CSV（快照）
    │
    ├─ ① 建立快照 ──────────→ history/PMML-Source-Traceability.snapshot-2c0183b-v0.5.0-era.csv
    │                          （逐字节复制，SHA256 记录在 README-快照说明.txt）
    │
    ├─ ② 修复 CSV 转义缺陷 ─→ _recovered.json（恢复出的 152 条完整记录）
    │                          （原 22 行因渐变坐标含未转义逗号而字段错位）
    │
    ├─ ③ 重定位 SourcePath ─→ _tools/relocate_v6.py
    │                          （逐条判定 UNCHANGED / RELOCATED / LOCATED /
    │                            MANUAL-ANCHOR / NO_PROBE / FILE_GONE）
    │
    ├─ ④ 增加 Commit 字段 ──→ PMML-Source-Traceability.csv（11 列）
    │
    ├─ ⑤ 重新验证技术发现 ─→ _tools/verify_findings.py
    │                          （33 条高价值断言逐一回读）
    │
    └─ ⑥ 输出差异报告 ─────→ diff/差异报告.md + diff/relocation.csv
```

### 7.2 关键实现纪律

| 纪律 | 原因 |
| --- | --- |
| **先验原行号，再全文件搜索** | 原行号成立是最强证据；直接搜索会命中更早的同名符号（把 UNCHANGED 从 50 误降为 67 的反向修正即由此产生） |
| **优先非注释行** | 避免把 `BackdropSpike.Install` 定位到文档注释而非实现（`find_in` 优先 `///`/`//`/`<!--`/`*`/`/*` 之外的行） |
| **只在原引用文件内搜索** | 行号漂移的正解是**同文件位移**；全仓搜索会命中无关项目（`src/PCMig.Gui`、`tests/**` 等） |
| **目录级引用才做受限搜索** | 且白名单仅 `src/PCMig.WinUI/` + 三个测试项目 |
| **人工锚点必须自校验** | 覆盖后的锚点行**必须真的含该记录的标识符**，否则中止写入（见 `ANCHOR_OVERRIDES` 的严格模式） |

### 7.3 已修复的工具缺陷（两次假阳性，留档以免重犯）

| # | 缺陷 | 表现 | 修正 |
| --- | --- | --- | --- |
| 1 | 路径正则 `^(.+?)(?::(\d+))?$` 非贪婪 + 可选组回溯 | 把 `:88-117`、`:2-9` 这类**行号范围整体吞进文件名** ⇒ `open()` 全失败 ⇒ 一轮跑出 **152/152 NOT_FOUND** | 改为显式匹配扩展名：`^(?P<file>.+?\.(?:cs\|xaml\|md))(?::(?P<start>\d+)(?:-(?P<end>\d+))?)?\s*$` |
| 2 | `..` 路径规约 | `src/PCMig.WinUI/../docs/x.md`（作者意图 = **仓库根** `docs/`）被 `normpath` 规约成 `src/docs/x.md` ⇒ `RULE-001`/`RULE-002` 被**误判 FILE_GONE** | 先剥 `:行号-行号` 再 normpath，并按候选路径逐一验存在性。`FILE_GONE` 3 → **1** |

---

## 八、当前状态

### 8.1 裁定分布

| Verdict | 数量 | 说明 |
| --- | ---: | --- |
| `UNCHANGED` | **67** | 行号未变，可直接引用 |
| `RELOCATED` | **29** | 行号漂移，更新后可用 |
| `LOCATED` | **29** | 原记录无行号，已补全 |
| `MANUAL-ANCHOR` | **2** | 人工指定锚点（`DAM-002` / `PROG-001`） |
| `NO_PROBE` | **24** | 需人工回读（见 `PMML-Evidence-Classification.md`） |
| `FILE_GONE` | **1** | `PROG-009` → `ProgressMotionDriver.cs`（已授权删除） |
| **合计** | **152** | |

### 8.2 EvidenceType 分布

| Evidence Type | 数量 |
| --- | ---: |
| Implementation Fact | 117 |
| UNCONFIRMED | 19 |
| Comment Claim | 12 |
| Design Rule | 3 |
| Design system | 1 |

### 8.3 ImplementationStatus 分布（前 8）

| Status | 数量 |
| --- | ---: |
| REALIZED | 93 |
| STALE | 26 |
| SCATTERED | 8 |
| KNOWN GAP | 4 |
| DIAGNOSTIC ONLY | 4 |
| DEAD CODE | 3 |
| CONFLICT | 3 |
| 其余 9 种 | 11 |

---

## 九、维护规则

1. **每次 UI 改动后**，对受影响的 `FeatureId` 重新验证 `SourcePath` 与 `Symbol`，更新 `Commit` 字段。
2. **行号漂移不必修正**，但引用时须按当前行号读取。
3. **`FeatureId` 永不重编号**。删除要素时保留编号并标记状态，不重新分配。
4. **新增要素才分配新编号**，且必须同时更新本 CSV、`PMML-Implementation-Map.md` 与 `PMML-Evidence-Classification.md`。
5. **`NO_PROBE` 不删除**。它表示「存在设计或资源引用线索，但缺少直接运行证据」，不是错误。
6. **不得把 `Comment Claim` 描述为已实现事实**。注释可能过期（已有 `STALE` 26 条为例）。
7. **重验证必须走本文 §七 的管道**，不得手工改 CSV（避免转义再次损坏）。

---

## 十、关联文件

| 文件 | 定位 |
| --- | --- |
| `PMML-Source-Traceability.csv` | 本方法论所描述的产物（152 条 / 11 列） |
| `history/PMML-Source-Traceability.snapshot-2c0183b-v0.5.0-era.csv` | 原文件快照（只读，不再修改） |
| `history/README-快照说明.txt` | 快照来源判定证据与已知缺陷 |
| `diff/差异报告.md` | 人类可读差异报告 |
| `diff/relocation.csv` | 逐行裁定（机器可读） |
| `PMML-Evidence-Classification.md` | 证据分级与 NO_PROBE 分类 |
| `PMML-Implementation-Map.md` | 要素 → 源码落点索引 |
| `_tools/` | 可复现脚本（`rebuild_traceability.py` / `relocate_v6.py` / `audit_csv.py` / `verify_findings.py` / `make_diff_report.py` / `fix_anchors.py`） |
