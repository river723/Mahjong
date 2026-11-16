# -*- coding: utf-8 -*-
"""把外部牌面素材（tools/_sheet）转成本项目风格的贴图。

思路：外部素材只提供「形状」（干净的矢量图案），牌体材质、阴刻槽壁、
漆面渐变仍由 gen_tiles 的 build_body / engrave 生成。这样既拿到外部
素材准确的排布与配色，又不牺牲质感，也不怕素材分辨率不够（形状是纯
色块，二值化后重新渲染边缘）。

用法：
    py tools/import_tiles.py test tong1 r1_c1     单张试合成
    py tools/import_tiles.py all                  批量
"""
import os
import sys
import numpy as np
from PIL import Image, ImageChops, ImageFilter, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import gen_tiles as G

SHEET = os.path.join(ROOT, "tools", "_sheet")
OUT = os.path.join(ROOT, "tools", "_mixed")
os.makedirs(OUT, exist_ok=True)

W, H = G.W, G.H
FACE_H = G.FACE_H
CW, CH = G.CW, G.CH

# 图案在牌面上的可用区域（归一化），留出倒角与台面边距
PAD_X, PAD_TOP, PAD_BOT = 0.085, 0.060, 0.075


def split_colors(im):
    """把一张 RGB 牌面拆成 4 个漆色 mask（超采样尺寸）。

    返回 [(漆色, L 型 mask), ...]，顺序即 engrave 的图层顺序。
    """
    a = np.asarray(im.convert("RGB")).astype(np.int32)
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    mx = np.maximum(np.maximum(r, g), b)
    mn = np.minimum(np.minimum(r, g), b)
    sat = mx - mn

    # 白底 -> 0；彩/黑 -> 高值。阴影灰边靠 sat / 亮度区分
    alpha = np.clip(255 - mn, 0, 255)

    is_red = (r >= g) & (r >= b) & (sat > 45) & ((r - np.maximum(g, b)) > 25)
    is_green = (g >= r) & (g >= b) & (sat > 30) & ((g - np.minimum(r, b)) > 18)
    is_blue = (b >= r) & (b > g) & (sat > 35)
    is_ink = (~is_red) & (~is_green) & (~is_blue) & (mx < 165)

    groups = [
        ("ink", is_ink),
        ("green", is_green),
        ("blue", is_blue),
        ("red", is_red),
    ]
    color_of = {"red": G.RED, "green": G.GREEN, "blue": G.BLUE, "ink": G.INK}

    out = []
    for name, m in groups:
        v = np.where(m, alpha, 0).astype(np.uint8)
        if v.max() < 40:
            continue
        mk = Image.fromarray(v, "L").resize((CW, CH), Image.LANCZOS)
        # 软边：素材线条细，硬阈值 + 膨胀会产生锯齿；这里保留 alpha 渐变，
        # 只用 gamma 把 JPEG 的浅灰噪点压下去，再轻微模糊消除阶梯。
        mk = mk.point(lambda p: int(255 * (p / 255.0) ** 1.7))
        mk = mk.filter(ImageFilter.GaussianBlur(G.S(0.9)))
        out.append((color_of[name], mk))
    return out


def fit_face(im):
    """把素材牌面裁到「图案实际外接框」，再等比缩放到本项目牌面的图案区。

    直接按整张牌缩放会让图案偏小：素材的图案本身只占牌面约七成，
    因此必须先按非白像素求 bbox，否则合成出来的花纹会缩一圈。
    """
    w, h = im.size
    # 去掉素材自带的黑/灰外框与白边
    inset_x, inset_y = int(w * 0.055), int(h * 0.045)
    core = im.crop((inset_x, inset_y, w - inset_x, h - inset_y))

    a = np.asarray(core.convert("RGB")).astype(np.int32)
    mn = a.min(axis=2)
    ys, xs = np.where(mn < 215)
    if len(xs) > 40:
        pad = 2
        core = core.crop((
            max(0, xs.min() - pad), max(0, ys.min() - pad),
            min(core.width, xs.max() + 1 + pad), min(core.height, ys.max() + 1 + pad),
        ))

    x0, x1 = W * PAD_X, W * (1 - PAD_X)
    y0, y1 = FACE_H * PAD_TOP, FACE_H * (1 - PAD_BOT)
    avail_w, avail_h = x1 - x0, y1 - y0

    cw, chh = core.size
    k = min(avail_w / cw, avail_h / chh)
    tw, th = max(1, int(cw * k)), max(1, int(chh * k))
    core = core.resize((tw, th), Image.LANCZOS)

    canvas = Image.new("RGB", (W, FACE_H), (255, 255, 255))
    canvas.paste(core, (int(x0 + (avail_w - tw) / 2), int(y0 + (avail_h - th) / 2)))
    return canvas


def compose(src_png, seed=7, back_side=False):
    im = Image.open(src_png)
    face_img = fit_face(im)
    masks = split_colors(face_img)

    G.random.seed(seed)
    p = G.Painter()
    G.build_body(p, back_side=back_side)
    G.engrave(p, masks)
    return p.img.resize((W, H), Image.LANCZOS), masks, face_img


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "test"
    if mode == "test":
        name, src = sys.argv[2], sys.argv[3]
        img, masks, face = compose(os.path.join(SHEET, src + ".png"))
        img.save(os.path.join(OUT, "%s_mixed.png" % name))
        face.resize((W, H), Image.LANCZOS).save(os.path.join(OUT, "%s_src.png" % name))
        print("mask layers:", [(c, m.getbbox()) for c, m in masks])
        print("saved", name)


if __name__ == "__main__":
    main()
