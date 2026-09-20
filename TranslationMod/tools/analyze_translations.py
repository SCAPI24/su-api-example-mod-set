#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
TranslationMod 翻译表体检工具。

用法:
    python Mod/TranslationMod/tools/analyze_translations.py
    python Mod/TranslationMod/tools/analyze_translations.py --export <Logs/zh_CN.xml>

检查项:
  1. Content/zh_CN.xml 结构统计（Screen / Entry 数）
  2. 全局重复 Original（运行时按 Original 全局去重，重复会静默覆盖）
  3. 跨 Screen 同 Original 但译文冲突（后者覆盖前者 → 真实 bug）
  4. Translation 为空 / 等于 Original（漏译）
  5. 与运行时导出表 Logs/zh_CN.xml 对比：导出表中仍未翻译的条目 = 待补词表
"""

import argparse
import os
import sys
import xml.etree.ElementTree as ET
from collections import OrderedDict, defaultdict

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONTENT = os.path.join(ROOT, "Content", "zh_CN.xml")
DEFAULT_EXPORTS = [
    os.path.join(ROOT, "..", "..", "Survivalcraft", "bin", "Release", "net8.0", "Logs", "zh_CN.xml"),
    os.path.join(ROOT, "..", "..", "publish", "Windows", "Logs", "zh_CN.xml"),
]


def load(path):
    """返回 (screen_order, {screen: [(original, translation), ...]})。"""
    tree = ET.parse(path)
    root = tree.getroot()
    order = []
    screens = OrderedDict()
    for screen in root.findall("Screen"):
        name = screen.get("Name") or ""
        entries = []
        for e in screen.findall("Entry"):
            entries.append((e.get("Original") or "", e.get("Translation") or ""))
        if name not in screens:
            screens[name] = []
            order.append(name)
        screens[name].extend(entries)
    # 兼容旧格式：根下直接 Entry
    loose = [(e.get("Original") or "", e.get("Translation") or "") for e in root.findall("Entry")]
    if loose:
        if "__root__" not in screens:
            screens["__root__"] = []
            order.append("__root__")
        screens["__root__"].extend(loose)
    return order, screens


def global_map(screens):
    """复刻 C# LoadTranslations: Translations[original] = translation（后写覆盖）。"""
    m = OrderedDict()
    for name in screens:
        for orig, trans in screens[name]:
            if orig and trans is not None:
                m[orig] = trans
    return m


def is_untranslated(orig, trans):
    return trans == "" or trans == orig


def report(path, label):
    order, screens = load(path)
    total = sum(len(screens[n]) for n in order)
    print(f"\n{'=' * 72}")
    print(f"{label}: {path}")
    print(f"{'=' * 72}")
    print(f"Screen 数: {len(order)}   Entry 数: {total}")

    # 1. 每屏统计
    print("\n[每屏条目数]")
    for name in order:
        rows = screens[name]
        untr = sum(1 for o, t in rows if is_untranslated(o, t))
        flag = f"   漏译 {untr}" if untr else ""
        print(f"  {name:<34} {len(rows):>5}{flag}")

    # 2. 组内重复 Original（同屏重复 = 冗余）
    print("\n[同屏内重复 Original]")
    dup_in_screen = 0
    for name in order:
        seen = defaultdict(list)
        for i, (o, t) in enumerate(screens[name]):
            seen[o].append(t)
        for o, ts in seen.items():
            if len(ts) > 1:
                dup_in_screen += 1
                same = "译文一致" if len(set(ts)) == 1 else f"译文冲突 {sorted(set(ts))}"
                print(f"  {name}: {o[:60]!r} x{len(ts)}  {same}")
    if not dup_in_screen:
        print("  无")

    # 3. 跨屏同 Original 译文冲突（严重：全局字典后者覆盖）
    print("\n[跨屏同 Original 译文冲突]  ← 运行时全局字典会静默覆盖")
    owner = defaultdict(list)
    for name in order:
        for o, t in screens[name]:
            owner[o].append((name, t))
    conflicts = 0
    for o, lst in owner.items():
        vals = {t for _, t in lst}
        if len(vals) > 1:
            conflicts += 1
            if conflicts <= 40:
                print(f"  {o[:60]!r}")
                for name, t in lst:
                    print(f"      [{name}] {t[:60]!r}")
    if not conflicts:
        print("  无")
    elif conflicts > 40:
        print(f"  ... 另有 {conflicts - 40} 条，见写入的报告")

    # 4. 漏译
    untr = [(name, o) for name in order for o, t in screens[name] if is_untranslated(o, t)]
    print(f"\n[漏译 Translation == Original 或为空] 共 {len(untr)} 条")
    for name, o in untr[:25]:
        print(f"  [{name}] {o[:90]!r}")
    if len(untr) > 25:
        print(f"  ... 另有 {len(untr) - 25} 条")

    return order, screens


def compare(export_path, content_path):
    print(f"\n{'=' * 72}")
    print("导出表 vs Content 表 差异")
    print(f"{'=' * 72}")
    _, ex = load(export_path)
    _, ct = load(content_path)
    ex_map = global_map(ex)
    ct_map = global_map(ct)

    only_export = [o for o in ex_map if o not in ct_map]
    only_content = [o for o in ct_map if o not in ex_map]
    changed = [o for o in ex_map if o in ct_map and ex_map[o] != ct_map[o]]

    print(f"导出表条目: {len(ex_map)}   Content 表条目: {len(ct_map)}")
    print(f"仅在导出表（新收集、待补）: {len(only_export)}")
    print(f"仅在 Content 表: {len(only_content)}")
    print(f"两边都有但译文不同: {len(changed)}")

    if changed:
        print("\n[同 Original 译文不一致（导出表可能是运行期原文占位）]")
        for o in changed[:20]:
            print(f"  {o[:60]!r}\n      content: {ct_map[o][:60]!r}\n      export : {ex_map[o][:60]!r}")

    if only_export:
        print("\n[仅在导出表中的原文 —— 需要翻译后并入 Content/zh_CN.xml]")
        for o in only_export[:40]:
            print(f"  {o[:100]!r}")
        if len(only_export) > 40:
            print(f"  ... 另有 {len(only_export) - 40} 条")

    return only_export, only_content, changed


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--content", default=CONTENT)
    ap.add_argument("--export", default=None, help="运行时导出的 Logs/zh_CN.xml")
    args = ap.parse_args()

    if not os.path.isfile(args.content):
        print(f"找不到 Content 表: {args.content}")
        return 1

    report(args.content, "Content 翻译表（随 Mod 发布）")

    export = args.export
    if export is None:
        for cand in DEFAULT_EXPORTS:
            if os.path.isfile(cand):
                export = cand
                break
    if export and os.path.isfile(export):
        compare(export, args.content)
    else:
        print("\n未找到运行时导出表 Logs/zh_CN.xml，跳过对比。")
        print("提示: 运行一次游戏后导出表生成在 <运行根目录>/Logs/zh_CN.xml")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
