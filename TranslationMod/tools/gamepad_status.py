#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""核对 Pak/Dialogs/GamepadHelpDialog.xml 全部 40 条文案的翻译状态。"""

import os
import re
import sys
import xml.etree.ElementTree as ET

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
CONTENT = os.path.join(HERE, "..", "Content", "zh_CN.xml")

root = ET.parse(CONTENT).getroot()
where = {}
for s in root.findall("Screen"):
    for e in s.findall("Entry"):
        o = e.get("Original") or ""
        if o:
            where[o] = (s.get("Name"), e.get("Translation") or "")

dlg = open(os.path.join(REPO, "Pak", "Dialogs", "GamepadHelpDialog.xml"),
           encoding="utf-8-sig").read()
texts = re.findall(r'Text="([^"]*)"', dlg)

print(f"GamepadHelpDialog 共 {len(texts)} 条 Text\n")
missing, noop, ok = [], [], []
for t in texts:
    if t not in where:
        missing.append(t)
        print(f"  缺失      {t!r}")
    else:
        sn, tr = where[t]
        if tr == t:
            noop.append(t)
            print(f"  未实译    {t!r}                 [{sn}]")
        else:
            ok.append((t, tr))
            print(f"  已译      {t!r:<32} -> {tr!r}")
print(f"\n已译 {len(ok)} / 未实译 {len(noop)} / 完全缺失 {len(missing)}")
