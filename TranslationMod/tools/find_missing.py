#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
对照游戏源码找出 Content/zh_CN.xml 缺失的固定文案。

覆盖来源:
  1. Survivalcraft/Game/*.cs 里程序化合成配方的 CraftingRecipe.Description
  2. Pak/BlocksData.csv 的方块名 + 方块描述
  3. 方块属性标签（RecipaediaDescriptionScreen 用到）

用法:
    python Mod/TranslationMod/tools/find_missing.py
    python Mod/TranslationMod/tools/find_missing.py --csv
"""

import argparse
import csv
import glob
import os
import re
import sys
from collections import OrderedDict

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
CONTENT = os.path.join(HERE, "..", "Content", "zh_CN.xml")


def load_table(path):
    import xml.etree.ElementTree as ET
    root = ET.parse(path).getroot()
    t = OrderedDict()
    for screen in root.findall("Screen"):
        for e in screen.findall("Entry"):
            o = e.get("Original") or ""
            v = e.get("Translation") or ""
            if o:
                t[o] = v
    for e in root.findall("Entry"):
        o = e.get("Original") or ""
        if o:
            t[o] = e.get("Translation") or ""
    return t


DESC_RE = re.compile(r'Description\s*=\s*"((?:[^"\\]|\\.)*)"', re.S)


def recipe_descriptions():
    out = OrderedDict()
    for p in sorted(glob.glob(os.path.join(REPO, "Survivalcraft", "Game", "*.cs"))):
        src = open(p, encoding="utf-8-sig", errors="replace").read()
        for m in DESC_RE.finditer(src):
            out.setdefault(m.group(1), []).append(os.path.basename(p))
    return out


def blocks_csv():
    """返回 [(name, description, class_name)]，列位置取自 Pak/BlocksData.csv 表头。

    表头: 1=DefaultDisplayName, 78=DefaultDescription
    """
    path = os.path.join(REPO, "Pak", "BlocksData.csv")
    if not os.path.isfile(path):
        return []
    rows = []
    with open(path, encoding="utf-8-sig", newline="") as f:
        rd = csv.reader(f, delimiter=";")
        next(rd, None)
        for row in rd:
            if len(row) < 79:
                continue
            rows.append((row[1], row[78], row[0]))
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", action="store_true", help="同时检查 BlocksData.csv 的方块名/描述")
    args = ap.parse_args()

    table = load_table(CONTENT)
    print(f"Content 表条目: {len(table)}\n")

    descs = recipe_descriptions()
    miss = [(s, f) for s, f in descs.items() if s not in table]
    print(f"== 程序化配方 Description: {len(descs)} 条, 缺失 {len(miss)} 条 ==")
    for s, files in sorted(descs.items()):
        if s in table:
            print(f"  OK    {s[:78]}")
        else:
            print(f"  MISS  {s[:78]}   <- {','.join(files)}")

    if args.csv:
        rows = blocks_csv()
        m2 = [(n, c) for n, d, c in rows if n and n not in table]
        m3 = [(n, d, c) for n, d, c in rows if d and d not in table]
        print(f"\n== BlocksData.csv 方块名: {len(rows)} 行, 名称缺失 {len(m2)}, 描述缺失 {len(m3)} ==")
        print("-- 名称缺失 --")
        for n, c in m2:
            print(f"  {n!r:<28} <- {c}")
        print("-- 描述缺失 --")
        for n, d, c in m3:
            print(f"  [{n}] {d[:88]!r}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
