# -*- coding: utf-8 -*-
"""验证输出 CSV 的转义完整性（读回自查）。"""
import csv, os
import os as _os
REPO = _os.environ.get("PCMIG_REPO") or _os.path.abspath(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", "..", ".."))
OUT = os.path.join(REPO, r"docs\pmml-engineering\PMML-Source-Traceability.csv")
SNAP = os.path.join(REPO, r"docs\pmml-engineering\history\PMML-Source-Traceability.snapshot-2c0183b-v0.5.0-era.csv")

def audit(path, label):
    with open(path, encoding="utf-8", newline="") as f:
        rows = list(csv.reader(f))
    hdr, data = rows[0], rows[1:]
    widths = set(len(r) for r in data)
    print(f"=== {label} ===")
    print(f"  header cols : {len(hdr)} -> {hdr}")
    print(f"  data rows   : {len(data)}")
    print(f"  row widths  : {sorted(widths)}")
    print(f"  ragged rows : {sum(1 for r in data if len(r) != len(hdr))}")
    for i, r in enumerate(data):
        if len(r) != len(hdr):
            print(f"    [!] row {i+1} width={len(r)} id={r[0]!r}")
    print()

audit(SNAP, "SNAPSHOT (original, known-broken)")
audit(OUT, "CURRENT (rebuilt)")

# 逐行字段非空检查
with open(OUT, encoding="utf-8", newline="") as f:
    rd = list(csv.DictReader(f))
print("=== CURRENT field sanity ===")
for c in rd[0].keys():
    empty = sum(1 for r in rd if not (r[c] or "").strip())
    print(f"  {c:22} empty={empty}/{len(rd)}")
print()
print("=== Commit column values ===")
from collections import Counter
print(Counter(r["Commit"] for r in rd))
print()
print("=== sample rows ===")
for r in rd[:3]:
    for k, v in r.items():
        print(f"    {k:22} {v[:110]}")
    print()
