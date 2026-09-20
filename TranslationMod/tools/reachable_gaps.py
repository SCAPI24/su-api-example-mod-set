#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
判定「缺失的方块名/描述」是否真的能在合成百科里显示。

判据（复刻 RecipaediaScreen.PopulateBlocksList + Block.GetCreativeValues/GetDisplayName）:
  * 可达 = CSV DefaultCreativeData >= 0，或该类重写了 GetCreativeValues
  * 若方块类重写了 GetDisplayName，则 CSV 的 DefaultDisplayName 不作为界面文案，跳过
"""

import csv
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
CONTENT = os.path.join(HERE, "..", "Content", "zh_CN.xml")

root = ET.parse(CONTENT).getroot()
table = {}
for screen in root.findall("Screen"):
    for e in screen.findall("Entry"):
        o = e.get("Original") or ""
        if o:
            table[o] = e.get("Translation") or ""

rows = []
with open(os.path.join(REPO, "Pak", "BlocksData.csv"), encoding="utf-8-sig", newline="") as f:
    rd = csv.reader(f, delimiter=";")
    next(rd)
    for row in rd:
        if len(row) >= 79:
            rows.append(row)


def src_of(class_name):
    p = os.path.join(REPO, "Survivalcraft", "Game", class_name + ".cs")
    if not os.path.isfile(p):
        return None
    return open(p, encoding="utf-8-sig", errors="replace").read()


print(f"{'class':<26} {'name':<22} {'creative':>8}  {'override':<10} 可达  缺名 缺述")
print("-" * 96)
reach_names = []
reach_descs = []
for row in rows:
    cls, name, desc, creative = row[0], row[1], row[78], row[15]
    src = src_of(cls)
    if src is None:
        continue
    has_override = bool(re.search(r"public override string GetDisplayName\(", src))
    has_cre = bool(re.search(r"public override IEnumerable<int> GetCreativeValues\(", src))
    try:
        cd = int(creative)
    except ValueError:
        cd = -999
    reachable = has_cre or cd >= 0
    miss_name = bool(name) and name not in table and reachable and not has_override
    miss_desc = bool(desc) and desc not in table and reachable
    if name not in table or desc not in table:
        print(f"{cls:<26} {name[:21]:<22} {creative:>8}  {str(has_override):<10} "
              f"{str(reachable):<5} {'YES' if miss_name else '-':<4} {'YES' if miss_desc else '-'}")
    if miss_name:
        reach_names.append((cls, name))
    if miss_desc:
        reach_descs.append((cls, name, desc))

print(f"\n可达且缺名称: {len(reach_names)}")
for c, n in reach_names:
    print(f"  {n!r}  <- {c}")
print(f"\n可达且缺描述: {len(reach_descs)}")
for c, n, d in reach_descs:
    print(f"  [{n}] {d[:100]!r}")
