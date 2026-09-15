#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
向 Mod/TranslationMod/Content/zh_CN.xml 写入本轮补译。

改动分两类：
  * UPDATE —— 已存在的条目且 Translation == Original（扫描器把英文原文当译文收回），就地改成中文
  * INSERT —— 表中完全不存在的新条目，按 string.CompareOrdinal 插入对应 Screen

只动被点名的行，其余字节保持不变；写入后重新解析并自检。

用法:
    python apply_translations.py            # 干跑，打印将发生的改动
    python apply_translations.py --apply    # 实际写入
"""

import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET

sys.stdout.reconfigure(encoding="utf-8")
HERE = os.path.dirname(os.path.abspath(__file__))
CONTENT = os.path.abspath(os.path.join(HERE, "..", "Content", "zh_CN.xml"))

ENTRY_RE = re.compile(r'^    <Entry Original="(.*?)" Translation="(.*?)" />$')
SCREEN_RE = re.compile(r'^  <Screen Name="(.*?)">$')

# ---------------------------------------------------------------- 触屏帮助长段落
TOUCH_PARAS = [
    ("Use the touchpad at the bottom left side of the screen to move. Tap it once to jump. "
     "This can also be configured to use directional buttons instead of the pad.",
     "使用屏幕左下方的触控板移动。轻点一次可跳跃。也可以将其配置为使用方向按钮代替触控板。"),
    ("Use the touchpad at the bottom right side of the screen to look around. Tap it once to jump. "
     "You can also drag your finger across any area of the screen to look.",
     "使用屏幕右下方的触控板环顾四周。轻点一次可跳跃。也可以在屏幕任意区域拖动手指来观察。"),
    ("You can tweak control scheme of the game in the Controls and Sensitivity settings. Experiment "
     "with various options, for example you may find you prefer buttons to touchpads, or split touch "
     "with crosshair to regular controls. Controlling a first person game on a small touchscreen may "
     "look difficult, but once you get the hang of it, it is no different than using a gamepad or a "
     "mouse. Because you can touch any part of the screen at any time, some actions like block digging "
     "or placement may even be easier.",
     "你可以在“控制”和“灵敏度”设置中调整游戏的操作方案。多尝试各种选项，例如你可能会发现自己更喜欢按钮而不是触控板，"
     "或是喜欢带准星的分离触控而不是常规控制。在小触摸屏上操作第一人称游戏看起来可能很难，但一旦上手，它与使用手柄或"
     "鼠标并无区别。由于你可以随时触摸屏幕的任何部分，挖掘或放置方块之类的操作甚至可能更容易。"),
    ("Some cheap or old devices have low quality touch screens or buggy touch drivers. They might "
     "intermittently drop active touch points, or mix them up if touched in more than one point at "
     "once. These are best avoided as playing on them is extremely frustrating.",
     "一些廉价或老旧设备的触摸屏质量低劣，或者触摸驱动存在缺陷。它们可能会间歇性地丢失活动触摸点，或者在同时触摸多个点时"
     "将它们混淆。最好避开这类设备，因为在上面游玩会非常令人沮丧。"),
]

# ---------------------------------------------------------------- 就地补译 (UPDATE)
UPDATES = {
    "HelpScreen": {
        # Pak/Dialogs/GamepadHelpDialog.xml 左列：手柄按键名
        "Left Stick": "左摇杆",
        "Right Stick": "右摇杆",
        "Left Trigger": "左扳机",
        "Right Trigger": "右扳机",
        "Left Thumb Click": "左摇杆按下",
        "Right Thumb Click": "右摇杆按下",
        "Left Shoulder": "左肩键",
        "Right Shoulder": "右肩键",
        "D-Pad Up": "方向键上",
        "D-Pad Down": "方向键下",
        "D-Pad Left/Right": "方向键左/右",
        "D-Pad Up/Down": "方向键上/下",
        "Back/Select/Pause": "返回/选择/暂停",
        "Left Trigger + A": "左扳机 + A",
        "A/Right Shoulder": "A/右肩键",
        # 同文件右列：动作说明
        "jump, drag items": "跳跃、拖动物品",
        "drop active item, cancel": "丢弃当前物品，取消",
        "change active slot": "切换当前快捷栏",
        "scroll, prev/next page": "滚动、上一页/下一页",
    },
}

# ---------------------------------------------------------------- 新增条目 (INSERT)
INSERTS = {
    "RecipaediaRecipesScreen": {
        # Survivalcraft/Game/*Block.cs 程序化合成配方 CraftingRecipe.Description
        "Make colored LEDs from copper, glass and paint": "用铜、玻璃和油漆制作彩色LED",
        "Make colored 1-LEDs from copper, glass and paint": "用铜、玻璃和油漆制作彩色1-LED",
        "Make colored 4-LEDs from copper, glass and paint": "用铜、玻璃和油漆制作彩色4-LED",
        "Make 7-segment displays from copper, glass and paint": "用铜、玻璃和油漆制作七段数码管",
        "Make multicolored LEDs from copper, glass and wire": "用铜、玻璃和电线制作多彩LED",
        "Make fireworks": "制作烟花",
        "Combine furniture into interactive design": "将家具组合为可交互设计",
        "Cook an egg to increase its nutritional value": "烹饪鸡蛋以提高其营养价值",
        "Cook pumpkin soup": "烹饪南瓜汤",
        "Undye carpet": "去除地毯染色",
        "Undye clothing": "去除衣物染色",
    },
    "HelpScreen": {
        # Pak/Dialogs/GamepadHelpDialog.xml 右列：原表完全缺失的动作说明
        "movement": "移动",
        "looking": "视角",
        "place block, use, interact": "放置方块、使用、交互",
        "dig, attack, aim": "挖掘、攻击、瞄准",
        "mount creature or vehicle": "骑乘生物或载具",
        "change camera mode": "切换相机模式",
        "edit block or item": "编辑方块或物品",
        "toggle sneaking": "切换潜行",
        "open inventory panel": "打开物品栏",
        "open body panel": "打开身体/食物面板",
        "show game menu": "显示游戏菜单",
        "auto move inventory item": "自动移动物品栏物品",
        "toggle fly mode (creative)": "切换飞行模式（创造模式）",
        "fly up/down (creative)": "上升/下降（创造模式）",
        "OK": "确定",
    },
    "RecipaediaScreen": {
        # Pak/BlocksData.csv DefaultDisplayName（这些方块无 GetDisplayName 重写，直接显示）
        "Oak Leaves": "橡树叶",
        "Birch Leaves": "白桦树叶",
        "Mimosa Leaves": "合欢树叶",
        "Poplar Leaves": "杨树叶",
        # Pak/BlocksData.csv DefaultDescription（ClothingBlock creativeData=0，百科里可达）
        "Clothing and armor are used to protect the wearer from environment conditions and attacks. "
        "Various types of clothes are available and can be put on or removed through clothes interface. "
        "Can be dyed by bathing in a paint bucket in a heated furnace.":
            "衣物和护甲用于保护穿戴者免受环境状况和攻击的伤害。有多种衣物可供选择，可以通过衣物界面穿上或脱下。"
            "在加热的熔炉中浸泡油漆桶即可染色。",
    },
}


def esc(v):
    """复刻 .NET XDocument 的属性值转义。"""
    return (v.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
             .replace('"', "&quot;").replace("\n", "&#xA;")
             .replace("\r", "&#xD;").replace("\t", "&#x9;"))


def make_line(original, translation):
    return f'    <Entry Original="{esc(original)}" Translation="{esc(translation)}" />'


def build_touch_translation(actual_key):
    """按原串精确的空白结构拼出译文，保证 key 端空白不被改动。"""
    parts = actual_key.split("\n\n")
    if len(parts) != len(TOUCH_PARAS):
        raise SystemExit(f"触屏段落数不符: {len(parts)} != {len(TOUCH_PARAS)}")
    out = []
    for part, (en_core, cn_core) in zip(parts, TOUCH_PARAS):
        lead = part[:len(part) - len(part.lstrip(" "))]
        trail = part[len(part.rstrip(" ")):]
        core = part.strip(" ")
        if core != en_core:
            raise SystemExit(f"段落原文不匹配:\n  实际={core[:80]!r}\n  预期={en_core[:80]!r}")
        out.append(lead + cn_core + trail)
    return "\n\n".join(out)


def scan(lines):
    """扫描行列表，返回 screen -> [(行号, 原文, 译文)]（保持文档顺序）。"""
    screens, curr = {}, None
    for i, l in enumerate(lines):
        m = SCREEN_RE.match(l)
        if m:
            curr = m.group(1)
            screens[curr] = []
            continue
        if curr is not None and ENTRY_RE.match(l):
            el = ET.fromstring(l.strip())
            screens[curr].append((i, el.get("Original"), el.get("Translation")))
    return screens


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true")
    args = ap.parse_args()

    text = open(CONTENT, encoding="utf-8", newline="").read()
    raw_len = len(text.encode("utf-8"))
    had_trailing_nl = text.endswith("\n")
    lines = text.split("\n")

    screens = scan(lines)

    all_keys = {o for rows in screens.values() for _, o, _ in rows}
    print(f"当前表: {len(all_keys)} 条 / {len(screens)} 屏\n")

    # 触屏长段落：就地改
    touch_key = next((o for o in all_keys
                      if o.startswith(" Use the touchpad at the bottom left")), None)
    if touch_key is None:
        raise SystemExit("未找到触屏帮助长段落")
    touch_cn = build_touch_translation(touch_key)
    UPDATES.setdefault("HelpTopicScreen", {})[touch_key] = touch_cn

    # 校验：UPDATE 的目标必须存在且当前为原文自指
    print("== UPDATE 校验 ==")
    for name, mapping in UPDATES.items():
        for o in mapping:
            row = next((r for r in screens.get(name, []) if r[1] == o), None)
            if row is None:
                print(f"  [跳过] [{name}] 未找到 {o[:60]!r}")
            else:
                flag = "自指" if row[2] == o else f"已译({row[2][:20]!r})"
                print(f"  [OK]   [{name}] {o[:52]!r}  ({flag})")

    print("\n== INSERT 校验 ==")
    for name, mapping in INSERTS.items():
        for o in mapping:
            if o in all_keys:
                print(f"  [冲突] [{name}] 已存在 {o[:60]!r}")
            else:
                print(f"  [新增] [{name}] {o[:60]!r} -> {mapping[o][:40]!r}")

    n_upd = sum(len(v) for v in UPDATES.values())
    n_ins = sum(len(v) for v in INSERTS.values())
    print(f"\n合计: 就地补译 {n_upd} 条 + 新增 {n_ins} 条 = {n_upd + n_ins} 条")

    if not args.apply:
        print("\n[干跑] 未写入。加 --apply 实际写入。")
        return 0

    # ---- 就地替换（不改变行数）
    replaced = 0
    for name, mapping in UPDATES.items():
        for i, o, t in screens.get(name, []):
            if o in mapping:
                lines[i] = make_line(o, mapping[o])
                replaced += 1

    # ---- 插入（按 CompareOrdinal，组内有序）；每次插入后整档重扫，避免行号错位
    inserted = 0
    for name, mapping in INSERTS.items():
        for o in sorted(mapping):
            fresh = scan(lines)
            if name not in fresh:
                raise SystemExit(f"未找到 Screen {name!r}")
            rows = fresh[name]
            if any(r[1] == o for r in rows):
                continue
            pos = rows[-1][0] + 1              # 默认追加到该屏末尾
            for r, orig, _ in rows:
                if orig > o:                   # CompareOrdinal：BMP 下等价于码点比较
                    pos = r
                    break
            lines.insert(pos, make_line(o, mapping[o]))
            inserted += 1

    out = "\n".join(lines)
    if not had_trailing_nl and out.endswith("\n"):
        out = out[:-1]
    with open(CONTENT, "wb") as f:
        f.write(out.encode("utf-8"))
    print(f"\n已写入: 替换 {replaced} 条, 插入 {inserted} 条, "
          f"字节 {raw_len} -> {len(out.encode('utf-8'))}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
