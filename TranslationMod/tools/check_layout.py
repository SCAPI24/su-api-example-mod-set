#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""确认 Content/zh_CN.xml 的行结构与组内排序，决定能否做逐行安全编辑。"""

import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
CONTENT = os.path.abspath(os.path.join(HERE, "..", "Content", "zh_CN.xml"))

text = open(CONTENT, encoding="utf-8").read()
lines = text.split("\n")
print(f"总行数 {len(lines)}")

entry_lines = [i for i, l in enumerate(lines) if l.startswith("    <Entry ")]
screen_lines = [i for i, l in enumerate(lines) if l.startswith("  <Screen ")]
other = [i for i, l in enumerate(lines)
         if not l.startswith("    <Entry ") and not l.startswith("  <Screen ")
         and l.strip() not in ("<Translations>", "</Translations>", "</Screen>", "<Translations/>")]
print(f"Entry 行 {len(entry_lines)}  Screen 行 {len(screen_lines)}  其它非空行 {len(other)}")
for i in other[:10]:
    print(f"   其它: {i}: {lines[i][:100]!r}")

# 每条 Entry 必须一行内闭合
bad = [i for i in entry_lines if not lines[i].rstrip().endswith("/>")]
print(f"未在单行闭合的 Entry: {len(bad)} {bad[:5]}")

# 逐屏提取 Original，检查是否 ordinal 有序
pat = re.compile(r'^    <Entry Original="(.*?)" Translation="(.*?)" />$')
order = []
curr = None
screens = {}
for i, l in enumerate(lines):
    if l.startswith("  <Screen "):
        curr = re.match(r'^  <Screen Name="(.*?)">$', l).group(1)
        screens[curr] = []
        order.append(curr)
    elif l.startswith("    <Entry "):
        m = pat.match(l)
        if not m:
            print(f"  解析失败 line {i+1}: {l[:120]!r}")
            continue
        screens[curr].append((i, m.group(1), m.group(2)))

print(f"\nScreen 顺序: {order}")
unsorted_screens = []
for name in order:
    keys = [o for _, o, _ in screens[name]]
    srt = sorted(keys)
    if keys != srt:
        unsorted_screens.append(name)
        # 找出第一处逆序
        for a, b in zip(keys, keys[1:]):
            if a > b:
                print(f"  [{name}] 逆序: {a[:40]!r} > {b[:40]!r}")
                break
print(f"组内非有序的 Screen: {len(unsorted_screens)} {unsorted_screens}")
