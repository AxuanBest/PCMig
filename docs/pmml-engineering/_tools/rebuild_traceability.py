#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
PMML Source Traceability — 重建（验证驱动）

设计要点：
  · 原 CSV 有 21~22 行被未转义逗号破坏，其 EvidenceType/ImplementationStatus 已不可从
    原始字节恢复。因此本工具**不猜测**这些字段，而是对每一行做**源码重新验证**，
    用验证结果重新判定 EvidenceType 与 ImplementationStatus。
  · 所有 SourcePath 一律重新定位到当前工作树（HEAD 见 Commit 列）。
  · 新增 Commit 列：记录该行号的验证基线 commit，未来漂移可检测。
只读 src/ tests/；只写 docs/pmml-engineering/ 下产物。不修改任何业务代码。
"""
import csv, json, os, re, subprocess, sys
from collections import OrderedDict

import os as _os
REPO = _os.environ.get("PCMIG_REPO") or _os.path.abspath(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", "..", ".."))
ENG = os.path.join(REPO, "docs", "pmml-engineering")
SNAPSHOT = os.path.join(ENG, "history",
                        "PMML-Source-Traceability.snapshot-2c0183b-v0.5.0-era.csv")
RECOVERED = os.path.join(ENG, "_recovered.json")

HEAD = subprocess.run(["git", "rev-parse", "HEAD"], cwd=REPO,
                      capture_output=True, text=True).stdout.strip()

# ----------------------------------------------------------------- 源码访问
_cache = {}
def lines_of(rel):
    rel = rel.replace("/", os.sep)
    if rel not in _cache:
        p = os.path.join(REPO, rel)
        try:
            with open(p, "r", encoding="utf-8", errors="replace") as f:
                _cache[rel] = f.readlines()
        except OSError:
            _cache[rel] = None
    return _cache[rel]

def all_source_files():
    """工作树里所有 .cs / .xaml（只读）。"""
    out = subprocess.run(
        ["git", "ls-files", "src/", "tests/"], cwd=REPO,
        capture_output=True, text=True).stdout
    return [l.strip() for l in out.splitlines()
            if l.strip().endswith((".cs", ".xaml"))]

SOURCE_FILES = None

def locate(token, prefer=None, whole_word=False):
    """全仓搜索 token，返回 (rel, line, text)。prefer 命中的文件优先。"""
    global SOURCE_FILES
    if SOURCE_FILES is None:
        SOURCE_FILES = all_source_files()
    pat = (re.compile(r"\b" + re.escape(token) + r"\b") if whole_word
           else re.compile(re.escape(token)))
    order = []
    if prefer:
        for p in prefer:
            if p in SOURCE_FILES:
                order.append(p)
    order += [f for f in SOURCE_FILES if f not in order]
    for rel in order:
        ls = lines_of(rel)
        if not ls:
            continue
        for i, t in enumerate(ls, 1):
            if pat.search(t):
                return rel, i, t.strip()
    return None, None, None

def file_of(pathfield):
    """从 SourcePath 字段取第一个文件路径。"""
    if not pathfield:
        return None
    first = re.split(r"[;]", pathfield)[0].strip()
    first = re.split(r":\d", first)[0].strip()
    return first or None

def main():
    data = json.load(open(RECOVERED, encoding="utf-8"))
    rows = data["rows"]
    print(f"rows loaded: {len(rows)}  HEAD={HEAD[:12]}")

    # 为每一行提取"线索 token"：优先取 符号键 中的驼峰标识符 / PCMig* 资源键
    report = []
    for r in rows:
        fid = r["FeatureId"]
        sym = r.get("SymbolOrResourceKey", "") or ""
        mech = r.get("Mechanism", "") or ""
        note = r.get("Note", "") or ""
        pref = file_of(r.get("SourcePath"))
        toks = [t for t in re.findall(r"\b(?:PCMig|Immersive|System)[A-Za-z0-9_]+", sym)]
        toks += [t for t in re.findall(r"\b[A-Z][A-Za-z0-9_]{4,}\b", sym)
                 if not t.isupper()]
        toks += [t for t in re.findall(r"\b[A-Z][A-Za-z0-9_]{4,}\b", mech)]
        # 去重保序
        seen, uniq = set(), []
        for t in toks:
            if t.lower() not in seen:
                seen.add(t.lower()); uniq.append(t)
        report.append({
            "FeatureId": fid,
            "preferFile": pref,
            "tokens": uniq[:5],
            "oldPath": r.get("SourcePath", ""),
            "oldSym": sym,
        })

    json.dump({"head": HEAD, "probes": report}, open(
        os.path.join(ENG, "_probes.json"), "w", encoding="utf-8"),
        ensure_ascii=False, indent=1)
    print("wrote _probes.json")

if __name__ == "__main__":
    main()
