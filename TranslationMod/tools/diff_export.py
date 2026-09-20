#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""严格比对 Content/zh_CN.xml 与运行时收集表 Logs/zh_CN.xml（逐 Screen 逐 Entry）。"""

import os
import sys
import xml.etree.ElementTree as ET
from collections import OrderedDict

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
CONTENT = os.path.join(HERE, "..", "Content", "zh_CN.xml")
EXPORT = os.path.abspath(os.path.join(
    HERE, "..", "..", "..", "publish", "Windows", "Logs", "zh_CN.xml"))


def load(path):
    root = ET.parse(path).getroot()
    screens = OrderedDict()
    for s in root.findall("Screen"):
        name = s.get("Name") or ""
        rows = [(e.get("Original") or "", e.get("Translation") or "")
                for e in s.findall("Entry")]
        screens.setdefault(name, []).extend(rows)
    return screens


def pairs(screens):
    return {(n, o) for n in screens for o, _ in screens[n]}


for label, p in (("Content", CONTENT), ("Export ", EXPORT)):
    sc = load(p)
    raw = sum(len(v) for v in sc.values())
    uniq = len(pairs(sc))
    untr = sum(1 for n in sc for o, t in sc[n] if t == o or t == "")
    print(f"{label}: bytes={os.path.getsize(p)} screens={len(sc)} raw={raw} uniq={uniq} untranslated={untr}")
    print(f"         screens -> {list(sc)}")

ct = load(CONTENT)
ex = load(EXPORT)
pct, pex = pairs(ct), pairs(ex)
print(f"\n(Content) - (Export): {len(pct - pex)}")
for n, o in sorted(pct - pex):
    print(f"   ONLY-CONTENT [{n}] {o[:70]!r}")
print(f"\n(Export) - (Content): {len(pex - pct)}")
for n, o in sorted(pex - pct):
    print(f"   ONLY-EXPORT  [{n}] {o[:70]!r}")

# 同 (screen, original) 但译文不同
cmap = {(n, o): t for n in ct for o, t in ct[n]}
emap = {(n, o): t for n in ex for o, t in ex[n]}
diff = [k for k in cmap if k in emap and cmap[k] != emap[k]]
print(f"\n译文不同的条目: {len(diff)}")
for k in diff[:30]:
    print(f"   {k}\n      content={cmap[k][:70]!r}\n      export ={emap[k][:70]!r}")

# 全局键冲突（同一个 Original 出现多次）
print("\n全局重复 Original（同表内）:")
for label, sc in (("Content", ct), ("Export", ex)):
    seen = {}
    for n in sc:
        for o, t in sc[n]:
            seen.setdefault(o, []).append((n, t))
    dup = {o: v for o, v in seen.items() if len(v) > 1}
    print(f"  {label}: {len(dup)}")
    for o, v in list(dup.items())[:10]:
        print(f"     {o[:60]!r} x{len(v)} {v}")
