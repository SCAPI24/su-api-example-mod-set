#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""写入后的最终自检。"""

import os
import sys
import xml.etree.ElementTree as ET

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
CONTENT = os.path.abspath(os.path.join(HERE, "..", "Content", "zh_CN.xml"))

raw = open(CONTENT, "rb").read()
crlf = raw.count(b"\r\n")
lf = raw.count(b"\n")
bom = raw[:3] == b"\xef\xbb\xbf"
print(f"文件 {len(raw)} 字节, BOM={bom}, CRLF={crlf}, LF={lf}")

try:
    root = ET.fromstring(raw.decode("utf-8"))
    print("XML 严格解析: OK")
except Exception as e:
    print(f"XML 解析失败: {e}")
    raise SystemExit(1)

screens = root.findall("Screen")
total = sum(len(s.findall("Entry")) for s in screens)
print(f"Screen {len(screens)} / Entry {total}")

keys = {}
for s in screens:
    for e in s.findall("Entry"):
        o = e.get("Original")
        t = e.get("Translation")
        if o is None or t is None:
            print(f"  属性缺失: {e.attrib}")
        keys.setdefault(o, []).append((s.get("Name"), t))
dups = {k: v for k, v in keys.items() if len(v) > 1}
print(f"全局重复 Original: {len(dups)}")
for k, v in list(dups.items())[:10]:
    print(f"   {k[:60]!r} x{len(v)} {v}")

conflict = {k: v for k, v in keys.items() if len({t for _, t in v}) > 1}
print(f"全局译文冲突: {len(conflict)}")
for k, v in list(conflict.items())[:10]:
    print(f"   {k[:60]!r} {v}")

noop = [o for o, v in keys.items() if v[0][1] == o]
print(f"仍为原文自指（未实译）: {len(noop)}")
for o in noop:
    print(f"   [{keys[o][0][0]}] {o[:70]!r}")
