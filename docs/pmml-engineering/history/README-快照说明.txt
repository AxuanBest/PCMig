PMML-Source-Traceability —— 历史快照说明
=========================================

快照文件 : PMML-Source-Traceability.snapshot-2c0183b-v0.5.0-era.csv
快照时间 : 2026-10-14 (+08:00)
SHA256   : 4648AF4BFED624E55F1F43DBFC4A75531922847F57E1017FFB9D740C5CBA814C
大小     : 47098 B / 152 数据行 / 10 列

【来源 commit 判定：2c0183b】
本快照内所有 SourcePath 行号对应的**不是**当前 HEAD，也**不是**工作树，
而是 commit 2c0183b（"checkpoint: preserve v0.5.0 PMML frozen and Diagnostics D6.1"）。

判定证据（决定性）：
  · 该 commit 的 src/PCMig.WinUI/Presentation/MotionDirector.cs 共 666 行，
    其 L370 恰为 "var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.…"，
    与本 CSV 的 DCST-003 行声称的 "MotionDirector.cs:370 = CubicBezier (0.10,0.90)(0.20,1.00)" 完全吻合。
  · 对照排除：
      HEAD b68613dc 版本 1163 行，L370 = "/// 时间轴上把新动画截断（…）"（注释）—— 不匹配
      当前工作树版本 1219 行，L370 = "catch (Exception ex)" —— 不匹配
  · 另证：该 CSV 把 Materials.xaml 的 Surface 样式记在 :168-179，
    当前树中同名同样式实际位于 :217-228（偏移 +49）。

【已知缺陷（保留原样，不作修改）】
  1. 21 / 152 行字段错位：SymbolOrResourceKey 内的渐变坐标含未转义半角逗号，
     导致内容整体右移、末列 Note 被吞。Import-Csv 因字段总数凑满 10 列而不报错。
     受影响 FeatureId：
       DAM-007, DAM-010, LMDS-006, DSL45-001, DSL45-002, DSL45-003, DSL45-006,
       DSL45-008, DCST-003, DCST-008, OACT-004, OACT-005, OACT-006, OACT-007,
       OACT-013, TAOP-009, MHE-009, IP-006, HINT-008, STATUS-004, BOOT-005
  2. 39 / 152 行 SourcePath 未给出行号（仅到文件或目录级）。
  3. 行号按 v0.5.0 时代写就，当前已漂移（人工裁定 42 条抽样中 13 条失效）。

【用途】
  仅作历史留档与差异比对基线；不得作为当前源码的可溯源证据引用。
  修复后的 Current 版本见同目录上级 PMML-Source-Traceability.csv。

【未做】
  本快照为逐字节复制，未修改任何内容；不删除、不覆盖原文件。
