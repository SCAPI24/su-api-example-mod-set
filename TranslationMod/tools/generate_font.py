#!/usr/bin/env python3
"""
生成 Survivalcraft 2 兼容的 Magenta 分隔线位图字体 PNG。

格式要求（与游戏 Pericles 字体一致）：
- 每个 glyph 之间用 1px Magenta (#FF00FF) 分隔线
- 水平分隔：glyph 之间的垂直像素列为 Magenta
- 垂直分隔：每行 glyph 之间有 Magenta 横像素行
- 检测算法：pixel != Magenta && left pixel == Magenta && top pixel == Magenta

输出：
- ChineseFont{N}.png  — Magenta 分隔线位图
- ChineseFont{N}.chars — 字符码点映射（每行十进制 Unicode）
"""

import argparse
import os
import sys
from PIL import Image, ImageDraw, ImageFont

MAGENTA = (255, 0, 255)  # #FF00FF


def get_gb2312_level1():
    """返回 GB2312 一级汉字（3755 个）的 Unicode 码点列表"""
    chars = []
    # GB2312 一级汉字：区 16-55，位 01-94
    for qu in range(16, 56):  # 区号 16-55
        for wei in range(1, 95):  # 位号 01-94
            code = 0xA0 + qu << 8 | (0xA0 + wei)
            try:
                c = code.to_bytes(2, 'big').decode('gb2312')
                chars.append(c)
            except (UnicodeDecodeError, UnicodeEncodeError):
                pass
    return chars


def get_ascii():
    """返回 ASCII 可打印字符（32-126）"""
    return [chr(i) for i in range(32, 127)]


def get_charset(name):
    """根据名称返回字符集"""
    if name == 'ascii':
        return get_ascii()
    elif name == 'gb2312' or name == 'gb2312-l1':
        return get_gb2312_level1()
    elif name == 'gb2312-full':
        # GB2312 全部 6763 汉字 + ASCII
        ascii_chars = get_ascii()
        gb_chars = get_gb2312_level1()
        # 二级汉字（区 56-87）
        for qu in range(56, 88):
            for wei in range(1, 95):
                code = 0xA0 + qu << 8 | (0xA0 + wei)
                try:
                    c = code.to_bytes(2, 'big').decode('gb2312')
                    gb_chars.append(c)
                except (UnicodeDecodeError, UnicodeEncodeError):
                    pass
        return ascii_chars + gb_chars
    elif name == 'test':
        return [chr(i) for i in range(32, 127)] + list("中文字体测试一二三四五六七八九十")
    else:
        raise ValueError(f"Unknown charset: {name}")


def render_glyph(draw, font, char, size, ascent, descent):
    """渲染单个字符到 Image，返回 Image 和 left bearing"""
    bbox = draw.textbbox((0, 0), char, font=font)
    if bbox is None or bbox[2] - bbox[0] <= 0:
        w, h = size // 3, size
        img = Image.new('RGBA', (w, h), (255, 255, 255, 255))
        draw2 = ImageDraw.Draw(img)
        draw2.text((0, ascent), char, font=font, fill=(0, 0, 0, 255))
        return binarize_glyph(img), 0

    w, h = bbox[2] - bbox[0], bbox[3] - bbox[1]
    if w <= 0 or h <= 0:
        w, h = size // 3, size
    img = Image.new('RGBA', (w + 2, h + 2), (255, 255, 255, 255))
    draw2 = ImageDraw.Draw(img)
    draw2.text((1 - bbox[0], 1 - bbox[1]), char, font=font, fill=(0, 0, 0, 255))
    return binarize_glyph(img), -bbox[0]


def binarize_glyph(img):
    """二值化：白色(>50%亮度)→Magenta, 暗色→白色(文字)。消除抗锯齿边缘"""
    pix = img.load()
    for y in range(img.height):
        for x in range(img.width):
            p = pix[x, y]
            # 亮度 > 128 视为背景 → Magenta
            if (p[0] + p[1] + p[2]) / 3 > 128:
                pix[x, y] = MAGENTA + (255,)
            else:
                pix[x, y] = (255, 255, 255, 255)
    return img


def crop_to_glyph(img):
    """找到 glyph 的实际非 Magenta 像素矩形边界。返回 (left, top, right, bottom) 或 None"""
    pix = img.load()
    left, top, right, bottom = img.width, img.height, -1, -1
    for y in range(img.height):
        for x in range(img.width):
            if pix[x, y][:3] != MAGENTA:
                left = min(left, x)
                top = min(top, y)
                right = max(right, x)
                bottom = max(bottom, y)
    if left == img.width:
        return None  # 全 Magenta
    return (left, top, right, bottom)


def generate_font(ttf_path, size, charset_name, output_prefix, glyphs_per_row=32):
    """生成 Magenta 分隔线格式的位图字体"""
    font = ImageFont.truetype(ttf_path, size)
    chars = get_charset(charset_name)

    # 获取字体度量
    temp_img = Image.new('RGBA', (1, 1), (0, 0, 0, 0))
    temp_draw = ImageDraw.Draw(temp_img)
    ascent, descent = font.getmetrics()

    print(f"Font: {ttf_path}, Size: {size}px, Chars: {len(chars)}")
    print(f"Metrics: ascent={ascent}, descent={descent}")

    # 渲染所有字符
    glyph_images = []
    advances = []
    for char in chars:
        img, adv = render_glyph(temp_draw, font, char, size, ascent, descent)
        glyph_images.append(img)
        advances.append(adv)

    # 计算每列的固定宽度和每行的固定高度（取最大值，确保对齐）
    col_widths = []
    row_heights = []
    num_cols = min(glyphs_per_row, len(chars))
    num_rows = (len(chars) + num_cols - 1) // num_cols

    for col in range(num_cols):
        max_w = 0
        for row in range(num_rows):
            idx = row * num_cols + col
            if idx < len(glyph_images):
                max_w = max(max_w, glyph_images[idx].width)
        col_widths.append(max_w)

    for row in range(num_rows):
        max_h = 0
        for col in range(num_cols):
            idx = row * num_cols + col
            if idx < len(glyph_images):
                max_h = max(max_h, glyph_images[idx].height)
        row_heights.append(max_h)

    # 计算总画布尺寸（每个格子宽/高 + 1px 分隔线，首行/首列有额外 1px）
    total_w = 1 + sum(w + 1 for w in col_widths)
    total_h = 1 + sum(h + 1 for h in row_heights)

    print(f"Canvas: {total_w}x{total_h}, Grid: {num_cols}x{num_rows}")

    # 创建画布
    canvas = Image.new('RGBA', (total_w, total_h), MAGENTA + (255,))

    # 放置 glyph
    y = 1
    for row in range(num_rows):
        x = 1
        for col in range(num_cols):
            idx = row * num_cols + col
            if idx < len(glyph_images):
                glyph = glyph_images[idx]
                cell_w = col_widths[col]
                cell_h = row_heights[row]
                # 居中放置
                ox = (cell_w - glyph.width) // 2
                oy = (cell_h - glyph.height) // 2
                canvas.paste(glyph, (x + ox, y + oy), glyph)
            x += col_widths[col] + 1  # +1 for separator
        y += row_heights[row] + 1

    # 保存 PNG
    png_path = f"{output_prefix}.png"
    canvas.save(png_path)
    print(f"Saved: {png_path} ({os.path.getsize(png_path)} bytes)")

    # 保存 .glyph.csv 纹理坐标映射
    csv_path = f"{output_prefix}.glyph.csv"
    with open(csv_path, 'w', encoding='utf-8') as f:
        f.write(f"# Font: {ttf_path}\n")
        f.write(f"# Size: {size}px\n")
        f.write(f"# Charset: {charset_name}\n")
        f.write(f"# Canvas: {total_w}x{total_h}\n")
        f.write(f"# Columns: {num_cols}, Rows: {num_rows}\n")
        f.write("code,cell_left,cell_top,cell_width,cell_height,glyph_width,glyph_height,crop_left,crop_top,crop_right,crop_bottom\n")

        y = 1
        for row in range(num_rows):
            x = 1
            for col in range(num_cols):
                idx = row * num_cols + col
                if idx < len(glyph_images):
                    glyph = glyph_images[idx]
                    cell_w = col_widths[col]
                    cell_h = row_heights[row]
                    # 裁剪 glyph 找到实际非 Magenta 像素边界
                    ox = (cell_w - glyph.width) // 2
                    oy = (cell_h - glyph.height) // 2
                    crop = crop_to_glyph(glyph)
                    if crop is not None:
                        cl, ct, cr, cb = crop
                        gw = cr - cl + 1
                        gh = cb - ct + 1
                    else:
                        # 空格等空白字符
                        cl, ct = ox, oy
                        gw, gh = cell_w // 3, cell_h // 2
                        cr, cb = cl + gw - 1, ct + gh - 1
                    code = ord(chars[idx])
                    f.write(f"{code},{x},{y},{cell_w},{cell_h},{gw},{gh},{cl},{ct},{cr},{cb}\n")
                x += col_widths[col] + 1
            y += row_heights[row] + 1

    print(f"Saved: {csv_path}")

    # 验证：检查 Magenta 分隔线
    img = Image.open(png_path)
    pix = img.load()
    sep_ok = all(pix[0, py][:3] == MAGENTA for py in range(img.height)) and \
              all(pix[px, 0][:3] == MAGENTA for px in range(img.width))
    print(f"Magenta border: {'OK' if sep_ok else 'FAIL'}")
    print(f"Total: {len(chars)} glyphs, Grid: {num_cols}x{num_rows}")


def main():
    parser = argparse.ArgumentParser(
        description="Generate Magenta-separated bitmap font PNG for Survivalcraft 2"
    )
    parser.add_argument('--ttf', required=True, help='TTF/OTF font file path')
    parser.add_argument('--size', type=int, required=True,
                        help='Font size in pixels (32/24/18/12)')
    parser.add_argument('--charset', default='gb2312',
                        choices=['ascii', 'gb2312', 'gb2312-full', 'test'],
                        help='Character set')
    parser.add_argument('--output', required=True, help='Output prefix (e.g. ChineseFont32)')
    parser.add_argument('--glyphs-per-row', type=int, default=32,
                        help='Maximum glyphs per row')
    args = parser.parse_args()

    if not os.path.exists(args.ttf):
        print(f"ERROR: Font file not found: {args.ttf}")
        sys.exit(1)

    generate_font(args.ttf, args.size, args.charset, args.output, args.glyphs_per_row)


if __name__ == '__main__':
    main()