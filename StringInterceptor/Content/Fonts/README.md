# 中文字体生成指南

## 格式要求

字体 PNG 使用 **Magenta 分隔线格式**（与游戏 Pericles 字体相同）：
- 每个 glyph 之间有 1px Magenta (#FF00FF) 分隔线
- 水平分隔线：glyph 之间的垂直列是 Magenta
- 垂直分隔线：每行 glyph 之间有 Magenta 横线
- 检测算法：`pixel != Magenta && left pixel == Magenta && top pixel == Magenta`

## 生成方法

### 方法 A：BMFont + 后处理（推荐）

1. 安装 AngelCode BMFont (https://www.angelcode.com/products/bmfont/)
2. 配置：
   - Font Settings → 选择中文字体（如 Microsoft YaHei）
   - Font Settings → Size: 32/24/18/12（对应 Pericles 各字号）
   - Export Options → 位深: 32bit (A8R8G8B8)
   - Export Options → 纹理: 选择 PNG
   - Export Options → 间距: 1px (用 Magenta 替换)
3. 选择字符集 → GB2312 一级字（3755字）+ ASCII（32-126）
4. Options → Font Settings → 确认 Charset: Unicode
5. 导出 → 得到 .fnt + .png

### 方法 B：Python 脚本直接生成

```bash
pip install Pillow
python tools/generate_font.py --ttf "C:/Windows/Fonts/msyh.ttc" --size 32 --charset gb2312
```

## 文件命名

生成后放入 `Content/Fonts/`：
- ChineseFont12.png + ChineseFont12.chars
- ChineseFont18.png + ChineseFont18.chars
- ChineseFont24.png + ChineseFont24.chars
- ChineseFont32.png + ChineseFont32.chars

## .chars 文件格式

每行一个十进制字符码点，顺序与 PNG 中 glyph 排列一致：

```
32
33
34
...
19968
19969
...
```

（空行和 # 开头的行为注释忽略）