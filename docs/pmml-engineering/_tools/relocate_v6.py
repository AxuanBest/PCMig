#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
PMML Source Traceability — 行号重定位 v6（终版：分类裁定）

在 v5 基础上把 NOT_FOUND 拆成三种**不同性质**的结论，避免把「溯源线索不足」
误报成「实现已消失」：

  UNCHANGED   行号与 HEAD 完全一致 —— 保留项
  RELOCATED   同文件内找到，行号漂移 —— 行号变化
  LOCATED     原行未给行号，本次定位到具体行 —— 补全
  NO_PROBE    原行只写了 `Comment` / 纯表达式，**没有可检索标识符** —— 需人工回读
  FILE_GONE   引用文件在 HEAD 不存在 —— 已删除实现
  DIR_EMPTY   只给了目录且目录内无匹配 —— 需人工回读

判定顺序：先看文件是否存在（决定 FILE_GONE），再跑 probe。
只读源码；只写 docs/pmml-engineering/ 下产物。
"""
import csv, json, os, re, subprocess
from collections import OrderedDict, Counter

import os as _os
REPO = _os.environ.get("PCMIG_REPO") or _os.path.abspath(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", "..", ".."))
ENG = os.path.join(REPO, "docs", "pmml-engineering")
RECOVERED = os.path.join(ENG, "_recovered.json")
OUT = os.path.join(ENG, "PMML-Source-Traceability.csv")
DIFFDIR = os.path.join(ENG, "diff")

HEAD = subprocess.run(["git", "rev-parse", "HEAD"], cwd=REPO,
                      capture_output=True, text=True).stdout.strip()

ALLOWED_ROOTS = ("src/PCMig.WinUI/", "tests/PCMig.Core.Tests/",
                 "tests/PCMig.Diagnostics.Tests/", "tests/PCMig.WinUI.Tests/")
DENY = ("src/PCMig.Cli/", "src/PCMig.Gui/", "src/PCMig.Core/Native/",
        "PCMig.Diagnostics.Abstractions/", "src/PCMig.Diagnostics/")
PATH_RE = re.compile(
    r"^(?P<file>.+?\.(?:cs|xaml|md))(?::(?P<start>\d+)(?:-(?P<end>\d+))?)?\s*$")

# ============================================================================
# 人工裁定锚点（Authoritative Anchor Override）
# ----------------------------------------------------------------------------
# 自动搜索用「首位命中」，对少数记录会落到调用处 / 类声明 / 同名符号，
# 而不是该记录真正描述的实现处。以下两条已由人工回读源码确认正确锚点：
#   DAM-002  :30  = `internal static class BackdropSpike`（类声明）
#              :158 = `public static bool Install(Window window)`（真正实现）
#   PROG-001 :145  = `public void ApplyTruth(...)`（真值入口）
#              :276 = `private double ClampVisual()`（该条描述的硬守卫本体）
# 覆盖只改 Evidence Anchor；事实、Status、EvidenceType 一律不动。
# ============================================================================
ANCHOR_OVERRIDES = {
    "DAM-002": {
        "line": 158,
        "reason": "自动搜索命中类声明 :30；本条描述的是运行时装配实现，"
                  "正确锚点为 Install 方法定义 :158。人工回读确认。",
    },
    "PROG-001": {
        "line": 276,
        "reason": "自动搜索命中真值入口 ApplyTruth :145；本条描述的是"
                  "「视觉不得越过已确认真值」硬守卫本体 ClampVisual :276。人工回读确认。",
    },
}

def split_ref(seg):
    """把 `src/X/../docs/A.md:392-410` 拆成 (仓库相对文件路径, 起始行)。
    必须先剥掉 `:行号-行号` 再 normpath，否则行号会被当成路径段。
    注意：原作者写 `src/PCMig.WinUI/../docs/...` 意图是**仓库根**的 docs/，
    但 normpath 会规约成 `src/docs/...`（`..` 只上升到 src/）。因此这里按
    候选顺序逐个尝试，返回**实际存在**的那个。"""
    m = PATH_RE.match(seg.strip())
    if not m:
        return None, None
    raw = m.group("file").replace("\\", "/")
    start = int(m.group("start")) if m.group("start") else None
    cands = []
    n = os.path.normpath(raw).replace("\\", "/")
    cands.append(n)
    # `src/<Proj>/../docs/x.md` 的作者意图通常是仓库根的 docs/x.md
    parts = raw.split("/")
    if ".." in parts:
        i = parts.index("..")
        cands.append("/".join(parts[:i - 1] + parts[i + 1:]))
    base = raw.rsplit("/", 1)[-1]
    cands.append(base)                      # 根下同名
    cands.append("docs/" + base)            # 仓库 docs/
    for c in cands:
        if c and os.path.exists(os.path.join(REPO, c.replace("/", os.sep))):
            return c, start
    return (cands[0] if cands else None), start

_c = {}
def norm(rel):
    """规范化仓库相对路径：消解 `src/X/../docs/...` 这类相对爬升，统一分隔符。"""
    if not rel:
        return rel
    r = rel.replace("\\", "/")
    return os.path.normpath(r).replace("\\", "/")

def lines_of(rel):
    k = norm(rel).replace("/", os.sep)
    if k not in _c:
        try:
            with open(os.path.join(REPO, k), "r", encoding="utf-8",
                      errors="replace") as f:
                _c[k] = f.readlines()
        except OSError:
            _c[k] = None
    return _c[k]

# 纯描述词：不可作为检索 probe
NOISE = set("""Comment Comments Note Notes Only Many Same Also See This That Then When With From
File Files Version Working Tree Uncommitted Rationale Recorded Header Long Sentence Sentences
Stretch Execution Card Shell Row Rows Material Radius Padding Elevation Low Medium High Hard
Guard Visual Never Exceed Confirmed Truth Height Revealed Composition Clip Opacity Translation
Per Frame Animated Cached Field Condition Local Session Thread Window Activated Definitive
Verdict Caches Legacy Structurally Uses Assistive Technology Step Steps Item Items Control Button
Buttons Styles Style Surface Surfaces Microsoft YaHei SemiBold Measured DELETED Default Breakpoints
Acceptance Boundary Asserted Only""".split())

def probes(sym, note):
    text = (sym or "") + " " + (note or "")
    keys = re.findall(r"\b(?:PCMig|Immersive|Pmmig)[A-Za-z0-9_]+", text)
    slashed = re.findall(r"\b([A-Z][A-Za-z0-9_]{3,})\s*/", sym or "")
    camels = [t for t in re.findall(r"\b[A-Z][A-Za-z0-9_]{3,}\b", text)
              if t not in NOISE]
    seen, out = set(), []
    for t in keys + slashed + camels:
        if t.lower() in seen or t in NOISE:
            continue
        seen.add(t.lower()); out.append(t)
    return out[:10]

def is_comment_line(t):
    s = t.strip()
    return (s.startswith("///") or s.startswith("//") or s.startswith("<!--")
            or s.startswith("*") or s.startswith("/*"))

def line_text(rel, lineno, plist):
    """读取某行，并返回该行命中的 probe（无命中则 None）。
    用于人工锚点覆盖后的严格自校验：锚点行必须真的含该记录的标识符。"""
    ls = lines_of(rel)
    if not ls or not lineno or lineno < 1 or lineno > len(ls):
        return None, None
    text = ls[lineno - 1]
    for p in plist:
        if re.search(r"\b" + re.escape(p) + r"\b", text):
            return text.strip(), p
    return None, None

def line_contains(rel, lineno, plist):
    ls = lines_of(rel)
    if not ls or not lineno or lineno < 1 or lineno > len(ls):
        return None, None
    text = ls[lineno - 1]
    for p in plist:
        if re.search(r"\b" + re.escape(p) + r"\b", text):
            return lineno, text.strip()
    return None, None

def find_in(rel, plist):
    """在文件内定位。优先**非注释**行；只有当该 probe 只在注释里出现时，
    才回退到注释行（避免把 `BackdropSpike.Install` 定位到文档注释而非实现）。"""
    ls = lines_of(rel)
    if not ls:
        return None, None, None
    for p in plist:
        pat = re.compile(r"\b" + re.escape(p) + r"\b")
        code_hit = None
        cmt_hit = None
        for i, t in enumerate(ls, 1):
            if pat.search(t):
                if is_comment_line(t):
                    if cmt_hit is None:
                        cmt_hit = (i, t.strip())
                else:
                    code_hit = (i, t.strip())
                    break
        if code_hit:
            return code_hit[0], code_hit[1], p
        if cmt_hit:
            return cmt_hit[0], cmt_hit[1], p
    return None, None, None

def allowed(rel):
    r = (rel or "").replace("\\", "/")
    if any(d in r for d in DENY):
        return False
    return r.startswith(ALLOWED_ROOTS)

def dir_files(prefix):
    o = subprocess.run(["git", "ls-files", prefix], cwd=REPO,
                       capture_output=True, text=True).stdout
    return [l.strip() for l in o.splitlines()
            if l.strip().endswith((".cs", ".xaml")) and allowed(l.strip())]

def main():
    rows = json.load(open(RECOVERED, encoding="utf-8"))["rows"]
    out_rows, differ, need_review = [], [], []
    stat = Counter()

    for r in rows:
        fid = r["FeatureId"]
        old = (r.get("SourcePath", "") or "").strip()
        sym, note = r.get("SymbolOrResourceKey", ""), r.get("Note", "")
        first = re.split(r";", old)[0].strip()
        old_file, old_line = split_ref(first)

        plist = probes(sym, note)
        ln = txt = probe = None
        used_file = old_file

        if old_file and not os.path.exists(os.path.join(REPO, old_file)):
            verdict, new_path = "FILE_GONE", old_file
        elif not plist:
            verdict = "NO_PROBE"
            new_path = f"{old_file}:{old_line}" if (old_file and old_line) else (
                old_file or old)
        elif old_file:
            # 先验原行号；不成立再全文件搜索
            ln, txt = line_contains(old_file, old_line, plist)
            if ln:
                probe = "LINE-UNCHANGED"
            else:
                ln, txt, probe = find_in(old_file, plist)
            if ln:
                new_path = f"{old_file}:{ln}"
                verdict = ("LOCATED" if old_line is None
                           else "UNCHANGED" if ln == old_line else "RELOCATED")
            else:
                new_path = f"{old_file}:{old_line}" if old_line else old_file
                verdict = "NO_PROBE"
        else:
            found = False
            for rel in dir_files(first + "/"):
                l2, t2, p2 = find_in(rel, plist)
                if l2:
                    ln, txt, probe, used_file, found = l2, t2, p2, rel, True
                    break
            if found:
                new_path = f"{used_file}:{ln}"
                verdict = "LOCATED" if old_line is None else "RELOCATED"
            else:
                new_path, verdict = old, "DIR_EMPTY"

        stat[verdict] += 1

        # ★ 人工裁定覆盖（Authoritative Anchor Override）★
        # 自动搜索是「首位命中」，对少数记录会选中调用处/类声明而非实现处。
        # 这些记录已由人工回读源码确认正确锚点，在此强制覆盖，并保留原判定为
        # AutoVerdict，便于审计「哪些是机器结论、哪些是人工结论」。
        override = ANCHOR_OVERRIDES.get(fid)
        auto_verdict = verdict
        if override:
            o_line = override["line"]
            o_text, o_probe = line_text(old_file, o_line, plist) if old_file else (None, None)
            new_path = f"{old_file}:{o_line}"
            verdict = "MANUAL-ANCHOR"
            probe = o_probe or "MANUAL"
            txt = o_text or override.get("expect", "")
            ln = o_line
            if not o_text:
                # 严格模式：人工锚点行若不含任何 probe，视为锚点失配，中止写入
                raise SystemExit(
                    f"!! ANCHOR OVERRIDE MISMATCH: {fid} -> {new_path} "
                    f"does not contain any of {plist}. Refusing to write.")

        rec = {"FeatureId": fid, "CanonicalChinese": r.get("CanonicalChinese", ""),
               "OldSourcePath": old, "NewSourcePath": new_path,
               "OldLine": old_line or "", "NewLine": ln or "",
               "Delta": (ln - old_line) if (ln and old_line) else "",
               "Verdict": verdict, "Probe": probe or "",
               "AutoVerdict": auto_verdict if override else "",
               "AnchorReason": override["reason"] if override else "",
               "MatchedLineText": (txt or "")[:150]}
        differ.append(rec)
        if verdict in ("NO_PROBE", "FILE_GONE", "DIR_EMPTY"):
            need_review.append(rec)

        out_rows.append(OrderedDict([
            ("FeatureId", fid),
            ("CanonicalEnglish", r.get("CanonicalEnglish", "")),
            ("CanonicalChinese", r.get("CanonicalChinese", "")),
            ("Mechanism", r.get("Mechanism", "")),
            ("SourcePath", new_path),
            ("SymbolOrResourceKey", sym),
            ("UsedBy", r.get("UsedBy", "")),
            ("EvidenceType", r.get("EvidenceType", "")),
            ("ImplementationStatus", r.get("ImplementationStatus", "")),
            ("Commit", HEAD),
            ("Note", (note or "").strip()),
        ]))

    os.makedirs(DIFFDIR, exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(out_rows[0].keys()), lineterminator="\n")
        w.writeheader(); w.writerows(out_rows)
    with open(os.path.join(DIFFDIR, "relocation.csv"), "w", encoding="utf-8",
              newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(differ[0].keys()), lineterminator="\n")
        w.writeheader(); w.writerows(differ)

    print("=== v6 relocation (classified verdicts) ===")
    for k in ("UNCHANGED", "RELOCATED", "LOCATED", "NO_PROBE", "FILE_GONE", "DIR_EMPTY"):
        if stat[k]:
            print(f"  {k:11} {stat[k]}")
    print(f"  {'TOTAL':11} {len(out_rows)}")
    print()
    print(f"=== 需人工回读 ({len(need_review)}) ===")
    for x in need_review:
        print(f"  {x['Verdict']:10} {x['FeatureId']:11} {x['OldSourcePath']}")

if __name__ == "__main__":
    main()
