# -*- coding: utf-8 -*-
"""
金币图标生成器（离线预渲染，与 tools/gen_tiles.py 同一套工艺）

产出（均为带 alpha 的 PNG）：
  coin.png / coin_dim.png        256px 单枚金币展示用大图（¥ 造型，用户最初认可的图）
  stack_1.png ... stack_5.png    80px 一摞金币，层数 1~5：底层固定、向上生长
  stack_bust.png                 破产用的灰色矮摞

用途：桌面每人显示「一摞金币 + 精确数字」，摞的高度按余额分档粗略变化。
  XAML 里 Image 固定 40px，摞图统一 80px 画布、底部对齐，正好 2x 整数倍缩放：
  非整数采样会把硬边抹软；尺寸恒定也让徽章高度不随余额抖动，只有金币向上长。

摞图来源：tools/coin_stack_reference.png（用户提供的金币摞矢量插画）。
  早期版本用代码手绘（渐变底色 + 实心亮金弧带 + 手画滚花 / ¥），反复调参
  仍与参考图差一口气：参考图是「三四十条细密立体滚花棱 + 奶白色艺术化 $
  符号 + 中心橙到边缘亮黄的径向渐变 + 左右两端镜面高光」，这些细节在
  80px 画布上物理上画不出来，手绘只能退化成粗栅栏，怎么调都不像。
  因此改成**直接取参考图的像素**：
    1) 参考图 433x433 虽是 RGBA，但棋盘格背景是画上去的（alpha 全 255）。
       用「从图像边缘连通的浅中性色」做 flood fill 抠成透明；金币内部的
       奶白符号与白色镜面高光不与边缘连通，得以完整保留。
    2) 中央最高摞 x=104..238 段干净无遮挡，层距用逐行自相关测得精确 20px；
       取它顶部一枚币作「上层币」贴片（顶面 + 亮金边 + 滚花段）。
    3) 画面底部那枚独立单币无遮挡、自带圆弧底缘，作「最底层币」贴片。
    4) 按 20px 层距从下往上叠贴片：上层盖住下层上部，每层只露出 20px 的
       滚花段 —— 这正是参考图里一摞币的构成方式，所以各档与参考图同源。
  好处：符号、渐变、滚花密度、镜面高光、配色全部来自原图，不再是近似；
  代价是 40px 显示尺寸下细滚花与符号会被插值成质感纹理（参考图缩小亦然）。

工艺要点（256px 单枚大图仍由代码绘制）：
  1) 以「256 基准尺寸」写线宽，3x 超采样后 LANCZOS 缩小
  2) 单枚币：径向渐变金属底 + 方向光 + 阴刻「¥」；轮廓是单一正圆
  3) 大图专属齿纹 / 噪点 / 镜面高光弧；UI 小图不画这些高频细节
  4) 破产摞去色压暗 + 半透明（小尺寸上画裂纹只会变成脏点，故省略）

运行： C:/Python312/python.exe tools/gen_coins.py
依赖： Pillow + numpy（本机 Python 3.12 已装）
"""
import math
import os
from collections import deque

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFont, ImageOps, ImageFilter

try:
    import sys
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "Mahjong.App", "Assets", "Coins")
os.makedirs(OUT, exist_ok=True)

LARGE, SMALL = 256, 48


def rgb(hexstr):
    h = hexstr.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def gradient_face(mask, S, with_noise):
    """币面金属底：径向渐变 + 方向光（小图不加噪点，噪点在 20px 下就是脏点）。"""
    radial = Image.radial_gradient("L").resize((S, S), Image.LANCZOS)
    face = ImageOps.colorize(
        radial,
        black=rgb("#FFF7D2"), mid=rgb("#F6C545"), white=rgb("#B96B08"),
        blackpoint=0, midpoint=118, whitepoint=255,
    ).convert("RGBA")

    linear = Image.linear_gradient("L").resize((S, S), Image.LANCZOS).rotate(-38, resample=Image.BICUBIC)
    shade = Image.new("RGBA", (S, S), (74, 38, 0, 0))
    shade.putalpha(linear.point(lambda v: int(v * 0.34)))
    face = Image.alpha_composite(face, shade)

    lit = Image.new("RGBA", (S, S), (255, 255, 235, 0))
    lit.putalpha(linear.point(lambda v: int((255 - v) * 0.20)))
    face = Image.alpha_composite(face, lit)

    if with_noise:
        noise = Image.effect_noise((S, S), 26).convert("L")
        grain = Image.new("RGBA", (S, S), (255, 246, 205, 0))
        grain.putalpha(noise.point(lambda v: int(max(0, v - 128) * 0.32)))
        face = Image.alpha_composite(face, grain)

    face.putalpha(ImageChops.multiply(face.getchannel("A"), mask))
    return face


def draw_coin(size, ss, rich):
    """size: 输出边长；ss: 超采样倍数；rich: 是否画大图专属的高频细节。"""
    S = size * ss
    U = S / 256.0                 # 以 256 为基准的线宽换算系数
    C = S / 2.0
    R = S * 0.455

    # 轮廓永远是单一正圆：不再把「主圆 ∪ 下移侧壁圆」当外轮廓，
    # 那个并集在左右两侧有尖角，缩小后正是「边缘很毛」的来源。
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).ellipse([C - R, C - R, C + R, C + R], fill=255)
    mask_soft = mask.filter(ImageFilter.GaussianBlur(0.6 * ss))

    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    canvas = Image.alpha_composite(canvas, gradient_face(mask, S, rich))
    d = ImageDraw.Draw(canvas)

    # 厚度：在圆内底部画一道暗弧，外轮廓仍是干净的圆
    inset = 5 * U
    d.arc([C - R + inset, C - R + inset, C + R - inset, C + R - inset],
          start=6, end=174, fill=rgb("#8A5209") + (255,), width=max(1, int(7 * U)))

    # 外圈暗槽环 + 亮金环（小图把环加粗，缩到 20px 才不会断成虚线）
    ring_w = 7 * U if rich else 9 * U
    ring_out = R - 4 * U
    d.ellipse([C - ring_out, C - ring_out, C + ring_out, C + ring_out],
              outline=rgb("#8A5209") + (255,), width=max(1, int(ring_w)))
    ring_in = R - 13 * U
    d.ellipse([C - ring_in, C - ring_in, C + ring_in, C + ring_in],
              outline=rgb("#FFE9A6") + (255,), width=max(1, int((4 if rich else 6) * U)))

    # 齿纹只属于大图：小图上 36 条刻线会糊成一圈白毛刺
    if rich:
        for i in range(36):
            a = math.radians(i * 10)
            r1, r2 = R - 15 * U, R - 22 * U
            d.line([C + r1 * math.cos(a), C + r1 * math.sin(a),
                    C + r2 * math.cos(a), C + r2 * math.sin(a)],
                   fill=rgb("#8E5A0D") + (170,), width=max(1, int(3 * U)))

    # 内圈细环，框住中心字面
    inner = R - 38 * U
    d.ellipse([C - inner, C - inner, C + inner, C + inner],
              outline=rgb("#A96A0C" if not rich else "#B4770E") + (255,),
              width=max(1, int((9 if not rich else 6) * U)))
    d.ellipse([C - inner - 4 * U, C - inner - 4 * U, C + inner + 4 * U, C + inner + 4 * U],
              outline=rgb("#FFEBAE") + (220,), width=max(1, int(3 * U)))

    # 中心「¥」：阴刻填漆（亮边 / 暗槽 / 漆面三层）。
    font = None
    for path in ("C:/Windows/Fonts/msyhbd.ttc", "C:/Windows/Fonts/arialbd.ttf",
                 "C:/Windows/Fonts/msyh.ttc"):
        if os.path.exists(path):
            try:
                font = ImageFont.truetype(path, int(126 * U))
                break
            except Exception:
                continue
    text = "¥"
    if not rich:
        # 小图不用字体：字体笔画带抗锯齿，48→22px 后细笔画会糊成一团。
        px = float(ss)

        def draw_yen(dx, dy, color):
            w = max(2, int(3 * px))
            d.line([(C - 7 * px + dx, C - 9 * px + dy), (C + dx, C - px + dy)],
                   fill=color, width=w)
            d.line([(C + 7 * px + dx, C - 9 * px + dy), (C + dx, C - px + dy)],
                   fill=color, width=w)
            d.line([(C + dx, C - 2 * px + dy), (C + dx, C + 9 * px + dy)],
                   fill=color, width=w)
            for yy in (1, 5):
                d.line([(C - 7 * px + dx, C + yy * px + dy),
                        (C + 7 * px + dx, C + yy * px + dy)],
                       fill=color, width=max(2, int(2.4 * px)))

        draw_yen(1.0 * px, 1.0 * px, rgb("#FFE9A0") + (230,))
        draw_yen(0, 0, rgb("#8E5208") + (255,))
    elif font is not None:
        box = d.textbbox((0, 0), text, font=font)
        tw, th = box[2] - box[0], box[3] - box[1]
        x = C - tw / 2.0 - box[0]
        y = C - th / 2.0 - box[1] - 3 * U
        off = 3 * U
        d.text((x + off, y + off), text, font=font, fill=rgb("#FFF3C0") + (235,))
        d.text((x - 2 * U, y - 2 * U), text, font=font, fill=rgb("#6B3C05") + (255,))
        d.text((x, y), text, font=font, fill=rgb("#B9760D") + (255,))
    else:
        q = 34 * U
        d.rectangle([C - q, C - q, C + q, C + q], fill=rgb("#8E5A0D") + (255,))
        d.rectangle([C - q + 4 * U, C - q + 4 * U, C + q - 4 * U, C + q - 4 * U],
                    fill=rgb("#5A3606") + (255,))

    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    g = ImageDraw.Draw(glow)
    if rich:
        hl = R - 16 * U
        g.arc([C - hl, C - hl, C + hl, C + hl], start=196, end=292,
              fill=(255, 255, 245, 150), width=max(1, int(20 * U)))
        g.arc([C - hl - 9 * U, C - hl - 9 * U, C + hl + 9 * U, C + hl + 9 * U],
              start=208, end=276, fill=(255, 255, 245, 70), width=max(1, int(9 * U)))
        blur = 3.0 * ss
    else:
        g.ellipse([C - R * 0.92, C - R * 0.95, C - R * 0.10, C - R * 0.30],
                  fill=(255, 255, 240, 46))
        blur = 9 * U
    glow = glow.filter(ImageFilter.GaussianBlur(blur))
    canvas = Image.alpha_composite(canvas, glow)

    canvas.putalpha(ImageChops.multiply(canvas.getchannel("A"), mask_soft))
    return canvas.resize((size, size), Image.LANCZOS)


def make_dim(coin, size):
    """破产版：去色压暗 + 裂纹 + 半透明。裂纹坐标按输出尺寸等比缩放。"""
    k = size / 256.0
    gray = ImageOps.grayscale(coin.convert("RGB"))
    dim = ImageOps.colorize(gray, black=rgb("#3B3B3F"), white=rgb("#9A9AA2")).convert("RGBA")
    dim.putalpha(coin.getchannel("A").point(lambda v: int(v * 0.72)))
    d = ImageDraw.Draw(dim)
    w = max(1, int(5 * k))
    d.line([(int(x * k), int(y * k)) for x, y in
            [(84, 26), (102, 86), (88, 132), (116, 182), (104, 228)]],
           fill=(40, 40, 44, 220), width=w, joint="curve")
    if size >= 128:
        d.line([(int(x * k), int(y * k)) for x, y in [(102, 86), (146, 104), (182, 90)]],
               fill=(40, 40, 44, 180), width=max(1, int(4 * k)), joint="curve")
        d.line([(int(x * k), int(y * k)) for x, y in [(88, 132), (52, 148), (34, 186)]],
               fill=(40, 40, 44, 180), width=max(1, int(4 * k)), joint="curve")
    return dim


# ==================== 摞图：直接取参考插画的像素 ====================
REF_IMAGE = os.path.join(ROOT, "tools", "coin_stack_reference.png")
MAX_LAYERS = 5
STACK_SIZE = 80          # XAML 显示 40px：2:1 整数倍缩放，避免非整数采样把硬边抹软
SS = 4                   # 画布超采样倍率
COIN_W_OUT = 66          # 输出画布里的币宽
BOTTOM_OUT = 76          # 底缘固定在输出 y=76，向上生长

# 参考图 433x433 里的两块干净素材（原图像素坐标）
TOP_BOX = (103, 21, 239, 71)      # 最高摞顶部一枚币：顶面 + 亮金边 + 滚花
FULL_BOX = (251, 362, 390, 413)   # 底部独立单币：完整、自带圆弧底缘
STEP_SRC = 20                     # 层距（逐行自相关测得，差值最小）
HC_SRC = 50                       # 上层币贴片高度

_TILES = None


def load_tiles():
    """抠掉画上去的棋盘格背景，返回缩放好的两张透明币贴片。"""
    im = Image.open(REF_IMAGE).convert("RGBA")
    arr = np.array(im)
    c = arr[:, :, :3].astype(int)
    # 棋盘格灰(238) / 白(255) 与浅色描边：低饱和 + 高亮度
    neutral = (c.max(2) - c.min(2) < 48) & (c.min(2) > 168)
    h, w = neutral.shape
    seen = np.zeros((h, w), bool)
    q = deque()
    for x in range(w):
        for y in (0, h - 1):
            if neutral[y, x] and not seen[y, x]:
                seen[y, x] = True
                q.append((y, x))
    for y in range(h):
        for x in (0, w - 1):
            if neutral[y, x] and not seen[y, x]:
                seen[y, x] = True
                q.append((y, x))
    while q:
        y, x = q.popleft()
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ny, nx = y + dy, x + dx
            if 0 <= ny < h and 0 <= nx < w and not seen[ny, nx] and neutral[ny, nx]:
                seen[ny, nx] = True
                q.append((ny, nx))
    # 只删「连通到画面边缘」的浅色区域：币面上的奶白 $ 符号与镜面高光
    # 被金色包围、不与边缘连通，所以完整保留。
    arr[seen, 3] = 0
    cut = Image.fromarray(arr)

    S = STACK_SIZE * SS
    BW = int(COIN_W_OUT * SS)
    scale = BW / float(TOP_BOX[2] - TOP_BOX[0])
    step = int(round(STEP_SRC * scale))
    top = cut.crop(TOP_BOX).resize((BW, int(round(HC_SRC * scale))), Image.LANCZOS)
    full = cut.crop(FULL_BOX).resize(
        (BW, int(round((FULL_BOX[3] - FULL_BOX[1]) * scale))), Image.LANCZOS)
    return top, full, step, S, BW


def tiles():
    global _TILES
    if _TILES is None:
        _TILES = load_tiles()
    return _TILES


def draw_stack(n):
    """用参考图的一枚币按 20px 层距叠出 n 层摞，底层固定、向上生长。

    上层币贴片会盖住下层币的上部，每层只露出滚花段与亮金前缘，这正是
    参考图里一摞币的构成方式。最底层换成那枚独立单币，得到圆弧底收尾。
    """
    top, full, step, S, BW = tiles()
    bottom = int(BOTTOM_OUT * SS)
    x0 = (S - BW) // 2
    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    for k in range(n):
        tile = full if k == 0 else top
        y = bottom - tile.height - k * step
        canvas.alpha_composite(tile, (x0, y))
    return canvas.resize((STACK_SIZE, STACK_SIZE), Image.LANCZOS)


def gray_out(img):
    """破产摞：去色压暗 + 半透明，不再画裂纹（小尺寸上裂纹只会变成脏点）。"""
    g = ImageOps.grayscale(img.convert("RGB"))
    out = ImageOps.colorize(g, black=rgb("#3B3B3F"), white=rgb("#9C9CA4")).convert("RGBA")
    out.putalpha(img.getchannel("A").point(lambda v: int(v * 0.75)))
    return out


if __name__ == "__main__":
    large = draw_coin(LARGE, 3, rich=True)
    large.save(os.path.join(OUT, "coin.png"))
    make_dim(large, LARGE).save(os.path.join(OUT, "coin_dim.png"))

    # 桌面上「一摞金币 + 数字」用这组：层数 1~5 对应余额档位，破产用灰色矮摞
    for n in range(1, MAX_LAYERS + 1):
        draw_stack(n).save(os.path.join(OUT, "stack_%d.png" % n))
    gray_out(draw_stack(1)).save(os.path.join(OUT, "stack_bust.png"))
    print("saved coin PNGs + %d coin stacks into %s" % (MAX_LAYERS + 1, OUT))
