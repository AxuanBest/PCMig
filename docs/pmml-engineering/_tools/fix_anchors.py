# -*- coding: utf-8 -*-
"""
仅修正 DAM-002 / PROG-001 两行的 Evidence Anchor（SourcePath）。
硬约束：其余 150 行、以及这两行的其它全部字段，必须逐字节不变。
修正后输出 SHA256 / row count / column count / diff summary。
"""
import csv, hashlib, os, sys

import os as _os
REPO = _os.environ.get("PCMIG_REPO") or _os.path.abspath(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", "..", ".."))
CSV = os.path.join(REPO, r"docs\pmml-engineering\PMML-Source-Traceability.csv")
BEFORE = os.path.join(os.environ["TEMP"], "csv-before.csv")

# 只允许这两处锚点变更；旧值必须精确匹配，否则中止
FIXES = {
    "DAM-002":  ("src/PCMig.WinUI/Presentation/BackdropSpike.cs:30",
                 "src/PCMig.WinUI/Presentation/BackdropSpike.cs:158"),
    "PROG-001": ("src/PCMig.WinUI/Presentation/Presentation/ProgressPresentationCoordinator.cs:145",
                 "src/PCMig.WinUI/Presentation/ProgressPresentationCoordinator.cs:276"),
}
# 修正后重新读取真实旧值（上面 PROG-001 的旧值以文件实际内容为准）
FIXES["PROG-001"] = ("src/PCMig.WinUI/Presentation/ProgressPresentationCoordinator.cs:145",
                     "src/PCMig.WinUI/Presentation/ProgressPresentationCoordinator.cs:276")

FORBIDDEN = {"FeatureId", "CanonicalEnglish", "CanonicalChinese", "Mechanism",
             "SymbolOrResourceKey", "UsedBy", "EvidenceType",
             "ImplementationStatus", "Commit", "Note"}

def sha256(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest().upper()

with open(CSV, encoding="utf-8", newline="") as f:
    rd = csv.DictReader(f)
    fieldnames = rd.fieldnames
    rows = list(rd)

print(f"BEFORE: {len(rows)} rows, {len(fieldnames)} cols, sha256={sha256(CSV)[:16]}...")
print()

# 前置校验：确认只有预期差异
changed = []
for r in rows:
    fid = r["FeatureId"]
    if fid in FIXES:
        old_expected, new_value = FIXES[fid]
        actual = r["SourcePath"]
        if actual != old_expected:
            print(f"!! ABORT: {fid} SourcePath 实际为 {actual!r}，期望 {old_expected!r}")
            sys.exit(1)
        r["SourcePath"] = new_value
        changed.append((fid, old_expected, new_value))

if len(changed) != len(FIXES):
    print("!! ABORT: 未找到全部目标行")
    sys.exit(1)

# 写回：保留 CSV 标准转义，LF 行尾，UTF-8 无 BOM
with open(CSV, "w", encoding="utf-8", newline="") as f:
    w = csv.DictWriter(f, fieldnames=fieldnames, lineterminator="\n")
    w.writeheader()
    w.writerows(rows)

print("FIXED ANCHORS:")
for fid, o, n in changed:
    print(f"  {fid}")
    print(f"    old SourcePath = {o}")
    print(f"    new SourcePath = {n}")
print()

# 复读校验
with open(CSV, encoding="utf-8", newline="") as f:
    rd2 = csv.DictReader(f)
    fn2 = rd2.fieldnames
    rows2 = list(rd2)
with open(BEFORE, encoding="utf-8", newline="") as f:
    rd0 = csv.DictReader(f)
    fn0 = rd0.fieldnames
    rows0 = list(rd0)

print("AFTER:  %d rows, %d cols, sha256=%s" % (len(rows2), len(fn2), sha256(CSV)))
print()
print("=== DIFF SUMMARY (vs before) ===")
print(f"  column count : {len(fn0)} -> {len(fn2)}   {'UNCHANGED' if len(fn0)==len(fn2) else 'CHANGED!'}")
print(f"  header set   : {'UNCHANGED' if fn0==fn2 else 'CHANGED!'}")
print(f"  row count    : {len(rows0)} -> {len(rows2)}   {'UNCHANGED' if len(rows0)==len(rows2) else 'CHANGED!'}")

field_diffs = 0
rows_touched = 0
for a, b in zip(rows0, rows2):
    assert a["FeatureId"] == b["FeatureId"]
    d = {k: (a[k], b[k]) for k in fn0 if a[k] != b[k]}
    if d:
        rows_touched += 1
        for k, (x, y) in d.items():
            field_diffs += 1
            print(f"  row {b['FeatureId']:9} field {k:14}: {x!r} -> {y!r}")
            if k in FORBIDDEN:
                print(f"    !! FORBIDDEN FIELD MODIFIED: {k}")
                sys.exit(1)

print(f"  rows touched : {rows_touched} / {len(rows0)}")
print(f"  fields differ: {field_diffs} (expected 2)")
print()
print("VERDICT:", "PASS - only the two Evidence Anchors changed" if field_diffs == 2 and rows_touched == 2 else "FAIL")
