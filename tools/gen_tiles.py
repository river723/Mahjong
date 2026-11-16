# -*- coding: utf-8 -*-
"""
麻将牌贴图生成器（离线预渲染，路线 A）

产出 34 种牌面 + 1 张牌背 + 1 张总览图，均为带 alpha 的 PNG，
供 Mahjong.App 作为 Resource 直接使用。

真实感要点（对照欢乐麻将 / 传统手雕麻将工艺）：
  1. 白面 + 墨绿背壳双层结构，侧面可见分层线与倒角
  2. 象牙白材质含高频细噪点 + 低频斑驳，不是纯色渐变
  3. 倒角高光带方向性（左上亮、右下暗），顶部弧形镜面柔光
  4. 图案为「阴刻填漆」：凹槽左上内壁暗、右下内壁亮、槽内彩色漆面，
     与「凸起浮雕」的明暗方向相反
  5. 万/字牌用楷体（笔锋），筒/条按传统排布分门布局并分色填漆
     （筒：蓝/红/绿同心圆；条：绿竹节，五条中心、七条顶端、九条中列为红）
  6. 3x 超采样后 LANCZOS 缩小，边缘干净

运行： C:/Python312/python.exe tools/gen_tiles.py
依赖： Pillow（本机 Python 3.12 已装）
"""
import os
import sys
import random
from PIL import Image, ImageDraw, ImageFont, ImageFilter, ImageChops

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "Mahjong.App", "Assets", "Tiles")
os.makedirs(OUT, exist_ok=True)

W, H = 256, 352
SS = 3
CW, CH = W * SS, H * SS
FACE_H = H - 20
DEPTH = H - FACE_H
R = 20

# 刻槽笔画加粗系数：实物手雕牌的填漆槽比屏幕矢量线条更宽，且阴刻合成时
# 的 MinFilter 会腐蚀细笔，故筒/条/鸟的线条统一放宽约 38%
INK_GAIN = 1.38
# 楷体文字的描边宽度（逻辑像素，作用于超采样画布，保留毛笔笔锋）
TEXT_STROKE = 2.4

FONT_PATH = "C:/Windows/Fonts/STKAITI.TTF"
if not os.path.exists(FONT_PATH):
    FONT_PATH = "C:/Windows/Fonts/simsun.ttc"

# 漆色取样自参考牌面（tools/fZuoSx1Bur.jpg）：红偏朱、蓝偏靛、绿为亮翠绿。
# 原先的绿 (22,116,60) 太墨，缩到牌面上比参考暗一档，是「条子不像」的
# 主因之一——颜色先对了，形态才读得出来。
RED = (190, 34, 50)
BLUE = (42, 50, 148)
GREEN = (50, 152, 88)
INK = (44, 46, 44)

IVORY_TOP = (251, 249, 242)
IVORY_BOT = (233, 229, 216)
EDGE_DARK = (166, 161, 143)
BACK_GREEN_TOP = (36, 100, 82)
BACK_GREEN_BOT = (17, 62, 51)


def S(v):
    return int(round(v * SS))


def SW(v):
    return max(1, int(round(v * SS)))


def font(sz):
    return ImageFont.truetype(FONT_PATH, max(1, S(sz)))


def new_rgba(color=(0, 0, 0, 0)):
    return Image.new("RGBA", (CW, CH), color)


def new_l(v=0):
    return Image.new("L", (CW, CH), v)


def box_px(b):
    return [S(b[0]), S(b[1]), S(b[2]), S(b[3])]


def mask_round(b, r):
    m = new_l(0)
    ImageDraw.Draw(m).rounded_rectangle(box_px(b), radius=S(r), fill=255)
    return m


def vgrad(c1, c2):
    strip = Image.new("RGBA", (1, CH))
    px = strip.load()
    for y in range(CH):
        t = y / float(CH - 1)
        px[0, y] = tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(3)) + (255,)
    return strip.resize((CW, CH))


def dir_alpha():
    """左上亮、右下暗的方向性 alpha，用来给倒角高光定光源。"""
    v = ImageChops.invert(Image.linear_gradient("L").resize((CW, CH)))
    h = ImageChops.invert(
        Image.linear_gradient("L").transpose(Image.Transpose.ROTATE_90).resize((CW, CH))
    )
    d = Image.blend(v, h, 0.5)
    return d.point(lambda p: int(45 + p * 0.82))


class Painter:
    def __init__(self):
        self.img = new_rgba()

    def fill(self, b, r, color):
        m = mask_round(b, r)
        layer = color if isinstance(color, Image.Image) else new_rgba(tuple(color) + (255,))
        self.img.paste(layer, (0, 0), m)

    def over(self, color, mask):
        layer = new_rgba(tuple(color) + (255,))
        layer.putalpha(mask)
        self.img = Image.alpha_composite(self.img, layer)

    def stroke(self, b, r, color, width, alpha=None):
        outer = mask_round(b, r)
        inner = mask_round(
            (b[0] + width, b[1] + width, b[2] - width, b[3] - width),
            max(1, r - width),
        )
        ring = ImageChops.subtract(outer, inner)
        if alpha is not None:
            ring = ImageChops.multiply(ring, alpha)
        self.over(color, ring)

    def line(self, y, color, width, alpha=255):
        m = new_l(0)
        ImageDraw.Draw(m).rectangle([0, S(y), CW, S(y + width)], fill=alpha)
        self.over(color, m)

    def composite(self, layer):
        self.img = Image.alpha_composite(self.img, layer)


def add_grain(p, mask, seed, strength=0.40):
    """高频细噪点 + 低频斑驳，模拟密胺/骨料的材质不均匀。"""
    random.seed(seed)
    grain = Image.effect_noise((CW, CH), 24).convert("RGB")
    low = Image.effect_noise((CW // 36, CH // 36), 42).resize((CW, CH), Image.BICUBIC).convert("RGB")
    mixed = Image.blend(grain, low, 0.42)
    cur = p.img.convert("RGB")
    out = Image.blend(cur, ImageChops.overlay(cur, mixed), strength)
    rgba = out.convert("RGBA")
    old_alpha = p.img.getchannel("A")
    p.img = Image.composite(rgba, p.img, mask)
    p.img.putalpha(old_alpha)


def build_body(p, back_side=False):
    """牌体：厚度层 + 正面层 + 倒角/高光/材质。返回牌面 mask。"""
    # ---- 1. 厚度层（白面 + 绿背壳的下沿） ----
    tb = (0, DEPTH * 0.30, W, H)
    p.fill(tb, R * 0.82, vgrad((241, 237, 226), (198, 193, 177)))
    thick_mask = mask_round(tb, R * 0.82)
    # 墨绿背壳只在最底缘露出很细一条，不做成明显的绿色底座
    green = vgrad((38, 92, 76), (20, 62, 50))
    gm = new_l(0)
    ImageDraw.Draw(gm).rectangle([S(3), S(H - DEPTH * 0.26), S(W - 3), S(H - 1)], fill=255)
    gm = ImageChops.multiply(gm, thick_mask)
    gl = green.copy()
    gl.putalpha(gm)
    p.composite(gl)
    p.line(H - DEPTH * 0.26, (126, 118, 100), 0.9, 110)

    # ---- 2. 正面 / 背面主体 ----
    fb = (0, 0, W, FACE_H)
    if back_side:
        base = vgrad(BACK_GREEN_TOP, BACK_GREEN_BOT)
    else:
        base = vgrad(IVORY_TOP, IVORY_BOT)
    p.fill(fb, R, base)
    face = mask_round(fb, R)

    add_grain(p, face, 1000 if back_side else 7, 0.20 if back_side else 0.40)

    # ---- 3. 外轮廓与多段倒角 ----
    p.stroke(fb, R, EDGE_DARK if not back_side else (10, 42, 34), 2.0)
    p.stroke((2, 2, W - 2, FACE_H - 2), R - 2,
             (255, 255, 252) if not back_side else (150, 205, 178),
             2.6, dir_alpha())
    p.stroke((7, 7, W - 7, FACE_H - 7), R - 7,
             (120, 114, 96) if not back_side else (8, 40, 32), 1.0,
             new_l(255).point(lambda v: int(v * 0.10)))

    # ---- 4. 顶部弧形镜面柔光（随倒角收敛，不是一条硬白带） ----
    gloss = new_l(0)
    gd = ImageDraw.Draw(gloss)
    gd.ellipse([S(-W * 0.30), S(-FACE_H * 0.62), S(W * 1.22), S(FACE_H * 0.30)], fill=120)
    gloss = gloss.filter(ImageFilter.GaussianBlur(S(13)))
    gloss = ImageChops.multiply(gloss, face)
    gloss = gloss.point(lambda v: int(v * (0.55 if back_side else 0.85)))
    p.over((255, 255, 255), gloss)

    # 右下环境反光（冷色，极淡）
    amb = new_l(0)
    ImageDraw.Draw(amb).ellipse(
        [S(W * 0.45), S(FACE_H * 0.55), S(W * 1.25), S(FACE_H * 1.22)], fill=70
    )
    amb = amb.filter(ImageFilter.GaussianBlur(S(18)))
    amb = ImageChops.multiply(amb, face)
    p.over((206, 220, 226), amb.point(lambda v: int(v * 0.35)))

    # 底边内阴影，让牌面有「铣出台面」的厚度
    shade = new_l(0)
    ImageDraw.Draw(shade).rectangle([0, S(FACE_H - 12), CW, S(FACE_H)], fill=90)
    shade = shade.filter(ImageFilter.GaussianBlur(S(5)))
    shade = ImageChops.multiply(shade, face)
    p.over((120, 114, 96) if not back_side else (6, 34, 27), shade)
    return face


def engrave(p, masks):
    """阴刻填漆合成：masks = [(漆色, L型刻槽mask), ...]"""
    for color, M in masks:
        # 槽口周围的极淡暗晕
        halo = ImageChops.subtract(M.filter(ImageFilter.GaussianBlur(S(2.4))), M)
        p.over((78, 72, 58), halo.point(lambda v: int(v * 0.34)))

        # 光从左上：凹槽的左上内壁背光（暗）、右下内壁受光（亮）
        d = S(1.7)
        dark = ImageChops.subtract(M, ImageChops.offset(M, d, d))
        p.over((64, 57, 45), dark.point(lambda v: int(v * 0.66)))
        light = ImageChops.subtract(M, ImageChops.offset(M, -d, -d))
        p.over((255, 254, 246), light.point(lambda v: int(v * 0.72)))

        # 槽内彩色漆面（略低于牌面，上暗下亮，带细微内阴影）
        core = M.filter(ImageFilter.MinFilter(5))
        cd = tuple(max(0, int(c * 0.76)) for c in color)
        cl = tuple(min(255, int(c * 1.10 + 10)) for c in color)
        layer = vgrad(cd, cl)
        layer.putalpha(core)
        p.composite(layer)


def layout_tong(n):
    """筒子 2-9 的传统排布，每项 (x, y, scale, 漆色)。

    依据维基百科「麻雀牌」筒子条目：二筒上绿下蓝、三筒蓝红绿斜线、
    四筒两蓝两绿对角、五筒中心红、六筒上二绿下四红、
    七筒上三绿斜线下四红、八筒 2x4 全蓝、九筒三行蓝红绿。
    坐标归一化到牌面（x 乘 W，y 乘 FACE_H）。
    """
    B, R, G = BLUE, RED, GREEN
    if n == 2:
        return [(0.50, 0.30, 0.96, G), (0.50, 0.70, 0.96, B)]
    if n == 3:
        return [(0.30, 0.27, 0.86, B), (0.50, 0.50, 0.86, R),
                (0.70, 0.73, 0.86, G)]
    if n == 4:
        # 左上绿、右上蓝；左下蓝、右下绿（对角同色，方向与实物牌一致）
        return [(0.32, 0.31, 0.90, G), (0.68, 0.31, 0.90, B),
                (0.32, 0.69, 0.90, B), (0.68, 0.69, 0.90, G)]
    if n == 5:
        return [(0.30, 0.27, 0.82, G), (0.70, 0.27, 0.82, B),
                (0.50, 0.50, 0.82, R),
                (0.30, 0.73, 0.82, B), (0.70, 0.73, 0.82, G)]
    if n == 6:
        # 2 列 x 3 行的等距方阵（参照 Unicode U+1F01E 牌形）：
        # 六个圆上下对齐、行距相等，上排两绿、下四红
        return ([(x, 0.23, 0.80, G) for x in (0.33, 0.67)]
                + [(x, y, 0.80, R) for y in (0.50, 0.77)
                   for x in (0.33, 0.67)])
    if n == 7:
        # 关键：上三颗绿色沿左上→右下斜线，下四颗红色 2x2 方阵
        return ([(0.28, 0.19, 0.74, G), (0.50, 0.31, 0.74, G),
                 (0.72, 0.43, 0.74, G)]
                + [(x, y, 0.74, R) for y in (0.63, 0.83)
                   for x in (0.32, 0.68)])
    if n == 8:
        return [(x, y, 0.70, B) for y in (0.20, 0.40, 0.60, 0.80)
                for x in (0.37, 0.63)]
    out = []
    for y, c in zip((0.22, 0.50, 0.78), (B, R, G)):
        out += [(x, y, 0.74, c) for x in (0.27, 0.50, 0.73)]
    return out


def layout_suo(n):
    """条子 2-9 的传统排布，每项 (x, y, scale, 漆色, 倾角)。

    二/四/五/九条与同名筒子相似；三条排成「上一下二」；
    六条「上三下三」；七条上一下三下三；八条上下各四根呈 W/M 形。
    """
    G, R = GREEN, RED
    if n == 2:
        return [(0.50, 0.360, 0.98, G, 0), (0.50, 0.640, 0.98, G, 0)]
    if n == 3:
        # 关键：品字形，不是斜线三连
        return [(0.50, 0.290, 0.88, G, 0),
                (0.33, 0.680, 0.88, G, 0), (0.67, 0.680, 0.88, G, 0)]
    if n == 4:
        return [(x, y, 0.92, G, 0) for y in (0.355, 0.645)
                for x in (0.35, 0.65)]
    if n == 5:
        return [(0.35, 0.290, 0.84, G, 0), (0.65, 0.290, 0.84, G, 0),
                (0.50, 0.50, 0.84, R, 0),
                (0.35, 0.710, 0.84, G, 0), (0.65, 0.710, 0.84, G, 0)]
    if n == 6:
        return [(x, y, 0.80, G, 0) for y in (0.360, 0.640)
                for x in (0.22, 0.50, 0.78)]
    if n == 7:
        return ([(0.50, 0.220, 0.78, R, 0)]
                + [(x, y, 0.76, G, 0) for y in (0.560, 0.790)
                   for x in (0.22, 0.50, 0.78)])
    if n == 8:
        # 标准 M/W 形（实物牌「|∧|」+「|∨|」）：这里只排四根竖直的边竹，
        # 中间那段折线斜竹由 draw_chevron 在绘制阶段单独补上，因为用两根
        # 45° 竹节相叠会在顶点切出两个小角、下缘还会长出「小脚」。
        return [(x, y, 0.92, G, 0) for y in (0.205, 0.795) for x in (0.235, 0.765)]
    out = []
    for y in (0.225, 0.50, 0.775):
        for x in (0.22, 0.50, 0.78):
            out.append((x, y, 0.76, R if x == 0.50 else G, 0))
    return out


def bamboo_geom(u):
    """条子的几何参数：节半宽 we、半长 Lh、单节腔心偏移、腔半高。"""
    g = INK_GAIN / 1.38
    we = u * 0.230 * (1 + (g - 1) * 0.45)   # 节半宽（最宽处）
    Lh = u * 0.700                          # 半长（两端圆头）
    return we, Lh


def punch_slot(d, cx, cy, u):
    """挖出上下两节的内部空腔（细长椭圆）。

    八条画完斜线后要重挖一次，把斜线在腔内的那截抹掉，斜线才会「从管壁
    开始」而不是糊在管口外面。
    """
    we, Lh = bamboo_geom(u)
    wi = we * 0.22
    ai = u * 0.190
    for sy in (-1, 1):
        yc = cy + sy * u * 0.415
        # 方端竖缝（不是椭圆）：参考图里腔宽恒为 3px，从第 5 行直贯到第 35 行
        d.rectangle([S(cx - wi), S(yc - ai), S(cx + wi), S(yc + ai)], fill=0)


def draw_bamboo(d, cx, cy, u):
    """一根条子（索）：上下两节空心环，腰部收窄。

    形态来自参考图的像素点阵：一根条总高约 78px、宽 11px，由**上下两个
    空心节**构成，每节内腔约 3px 宽 × 30px 高，腰部收到约 0.42 倍且是实心
    的（上下两腔不连通）。别处常误画成「直筒 + 一道横缝」——那会读成被
    切两截的短棒。

    轮廓用数值函数一次画成：半宽 = 鼓形 × 端部圆头弧，腰部的高斯凹在
    s=0 处收到 0.42，节与腰之间是连续凹弧，不会像「两椭圆相叠」那样在
    交点掐出尖角。
    """
    import math
    we, Lh = bamboo_geom(u)
    N = 72
    right, left = [], []
    for i in range(2 * N + 1):
        t = (i - N) / N                      # -1 → 1，沿条子轴向
        s = abs(t)
        # 腰部高斯凹：s=0 收到 0.42，sigma 控制凹的宽度
        wa = 1.0 - 0.32 * math.exp(-(s / 0.22) ** 2)
        # 端部圆角：只占最后 16%，接近参考的「圆角矩形」而不是椭圆——
        # 参考一节 39px 高、11px 宽，端部圆弧只有 3 行
        if s > 0.75:
            q = (s - 0.75) / 0.25
            wa *= math.sqrt(max(0.0, 1.0 - q * q))
        right.append((cx + we * wa, cy + t * Lh))
        left.append((cx - we * wa, cy + t * Lh))
    d.polygon([(S(x), S(y)) for x, y in right + left[::-1]], fill=255)
    punch_slot(d, cx, cy, u)


def draw_chevron(d, xl, xr, y_end, up, shaft):
    """八条中间那段折线斜竹。

    参考实物牌：上组是「|∧|」、下组是「|∨|」，斜竹两端正好落在两侧竖竹的
    端部，顶点是一个圆润的单尖。用一条圆角折线画出来，而不是两根 45° 竹节
    互相叠——叠出来的顶点会切出两个小角，下缘还会长了「小脚」戳穿竖竹。
    """
    xm = (xl + xr) * 0.5
    run = abs(xr - xl) * 0.5
    y_apex = y_end - run * 0.80 if up else y_end + run * 0.80
    d.line([(S(xl), S(y_end)), (S(xm), S(y_apex)), (S(xr), S(y_end))],
           fill=255, width=max(1, S(shaft)), joint="curve")
    r = shaft * 0.5
    for px, py in ((xl, y_end), (xr, y_end)):
        d.ellipse([S(px - r), S(py - r), S(px + r), S(py + r)], fill=255)


def draw_bamboo_rot(mask, cx, cy, u, angle):
    """画一根可倾斜的竹节：先画竖竹，再绕自身中心旋转后原地合并到漆色层。"""
    tmp = new_l(0)
    draw_bamboo(ImageDraw.Draw(tmp), cx, cy, u)
    if angle:
        tmp = tmp.rotate(angle, resample=Image.BICUBIC, center=(S(cx), S(cy)))
    # paste 是原地写入，已取出的 ImageDraw 仍指向同一张图
    mask.paste(ImageChops.lighter(mask, tmp))


def draw_bird(dg, dr, cx, cy, u):
    """一条（幺鸡）：站在竹枝上的孔雀。

    参考实物牌：孔雀侧身朝右站在斜竹枝上，头顶一小簇红冠、细长红喙，
    三片长尾羽从身后向左下方甩出、羽尖下垂。关键是头颈必须和身体连成
    一个剪影，否则会散成「球 + 棍」。
    """
    import math
    g = INK_GAIN

    def P(x, y):
        return S(cx + u * x), S(cy + u * y)

    # 竹枝（先画，压在鸟身后）
    dg.line([P(-1.02, 0.82)[0], P(-1.02, 0.82)[1],
             P(0.96, 0.66)[0], P(0.96, 0.66)[1]],
            fill=255, width=SW(u * 0.085 * g))

    # 三片长尾羽：从身体后下方向左下甩出，叶形收尖
    bx, by = -0.26, 0.16
    for tx, ty, wd in ((-1.02, 0.14, 0.115), (-0.94, 0.44, 0.125), (-0.76, 0.70, 0.110)):
        dx, dy = tx - bx, ty - by
        L = math.hypot(dx, dy) or 1.0
        px, py = -dy / L, dx / L
        mx, my = bx + dx * 0.52, by + dy * 0.52
        dg.polygon([P(bx + px * wd * 0.35, by + py * wd * 0.35),
                    P(mx + px * wd, my + py * wd),
                    P(tx, ty),
                    P(mx - px * wd, my - py * wd),
                    P(bx - px * wd * 0.35, by - py * wd * 0.35)], fill=255)

    # 身体：略向前倾的水滴，尾部收窄
    dg.ellipse([P(-0.50, -0.24)[0], P(-0.50, -0.24)[1],
                P(0.34, 0.48)[0], P(0.34, 0.48)[1]], fill=255)
    dg.polygon([P(-0.46, 0.02), P(-0.20, -0.22), P(0.16, 0.30), P(-0.30, 0.40)],
               fill=255)

    # 脖子：圆头粗线把身体和头连成一个剪影，避免出现尖角
    dg.line([P(0.10, 0.00)[0], P(0.10, 0.00)[1],
             P(0.44, -0.56)[0], P(0.44, -0.56)[1]],
            fill=255, width=SW(u * 0.26), joint="curve")

    # 头
    dg.ellipse([P(0.26, -0.86)[0], P(0.26, -0.86)[1],
                P(0.70, -0.42)[0], P(0.70, -0.42)[1]], fill=255)

    # 红喙（朝右）
    dr.polygon([P(0.62, -0.70), P(1.02, -0.60), P(0.62, -0.50)], fill=255)
    # 红冠：头顶三小撮
    for gx, gy, gr in ((0.34, -0.90, 0.055), (0.44, -0.95, 0.055), (0.54, -0.92, 0.050)):
        dr.ellipse([P(gx - gr, gy - gr)[0], P(gx - gr, gy - gr)[1],
                    P(gx + gr, gy + gr)[0], P(gx + gr, gy + gr)[1]], fill=255)

    # 眼睛（挖空）+ 翅膀轮廓（挖一道弧）
    r = u * 0.052
    dg.ellipse([S(cx + u * 0.50) - r, S(cy - u * 0.66) - r,
                S(cx + u * 0.50) + r, S(cy - u * 0.66) + r], fill=0)

    # 两只脚：短竖线踩在竹枝上
    for fx in (-0.06, 0.14):
        dg.line([P(fx, 0.38)[0], P(fx, 0.38)[1], P(fx, 0.74)[0], P(fx, 0.74)[1]],
                fill=255, width=SW(u * 0.055 * g))


def draw_dot(d, cx, cy, u, big=False, inner=None, green=None):
    """筒子单色同心圆（实物每颗圆只填一种漆色）。
    big=True 为一筒的大花纹圆：外蓝细圈 + 绿花瓣环 + 红盘上压绿十字。
    """
    g = INK_GAIN
    if big:
        import math
        # 1) 外圈细蓝环
        d.ellipse([S(cx - u * 0.80), S(cy - u * 0.80),
                   S(cx + u * 0.80), S(cy + u * 0.80)],
                  outline=255, width=SW(4.6 * g))
        # 2) 绿花瓣环：沿半径 0.60u 排一圈小圆，半径取 0.145u 使相邻花瓣
        #    互相叠合，边缘连成扇贝状花瓣环（散点会读成「珍珠项链」）
        for i in range(16):
            a = i * math.pi / 8
            px, py = cx + math.cos(a) * u * 0.58, cy + math.sin(a) * u * 0.58
            rp = u * 0.128
            green.ellipse([S(px - rp), S(py - rp), S(px + rp), S(py + rp)], fill=255)
        # 3) 花瓣内圈细绿环 + 红盘
        green.ellipse([S(cx - u * 0.44), S(cy - u * 0.44),
                       S(cx + u * 0.44), S(cy + u * 0.44)],
                      outline=255, width=SW(4.4 * g))
        inner.ellipse([S(cx - u * 0.34), S(cy - u * 0.34),
                       S(cx + u * 0.34), S(cy + u * 0.34)], fill=255)
        # 4) 红盘上的绿十字（绿层在红层之后绘制，压在上面）
        cw_ = SW(7.2 * g)
        green.line([S(cx - u * 0.30), S(cy), S(cx + u * 0.30), S(cy)],
                   fill=255, width=cw_)
        green.line([S(cx), S(cy - u * 0.30), S(cx), S(cy + u * 0.30)],
                   fill=255, width=cw_)
        return
    r = u * 0.62
    d.ellipse([S(cx - r), S(cy - r), S(cx + r), S(cy + r)], outline=255, width=SW(4.6 * g))
    r2 = r * 0.62
    d.ellipse([S(cx - r2), S(cy - r2), S(cx + r2), S(cy + r2)], outline=255, width=SW(4.2 * g))
    r3 = r * 0.23 * g
    d.ellipse([S(cx - r3), S(cy - r3), S(cx + r3), S(cy + r3)], fill=255)


def glyph_masks(index):
    """返回 [(漆色, 刻槽mask)]。"""
    layers = {}

    def layer(color):
        if color not in layers:
            m = new_l(0)
            layers[color] = (m, ImageDraw.Draw(m))
        return layers[color][1]

    def mask_of(color):
        layer(color)                 # 确保该漆色层已建立
        return layers[color][0]

    if index < 9:
        digits = "一二三四五六七八九"
        d_blue = layer(BLUE)
        d_blue.text((S(W * 0.5), S(FACE_H * 0.30)), digits[index],
                    font=font(112), fill=255, anchor="mm",
                    stroke_width=S(TEXT_STROKE), stroke_fill=255)
        d_red = layer(RED)
        d_red.text((S(W * 0.5), S(FACE_H * 0.685)), "萬",
                   font=font(120), fill=255, anchor="mm",
                   stroke_width=S(TEXT_STROKE), stroke_fill=255)
    elif index < 18:
        n = index - 8
        if n == 1:
            draw_bird(layer(GREEN), layer(RED), W * 0.48, FACE_H * 0.47, W * 0.38)
        else:
            for nx, ny, sc, color, ang in layout_suo(n):
                draw_bamboo_rot(mask_of(color), nx * W, ny * FACE_H,
                                W * 0.225 * sc, ang)
            if n == 8:
                u8 = W * 0.225 * 0.92
                dg8 = ImageDraw.Draw(mask_of(GREEN))
                # 上组是 V（谷、斜线向下汇合）、下组是 ∧（尖、斜线向上
                # 汇合）—— 与「上∧下∨」正好相反，这是参考图实测的朝向
                for cy_n, up in ((0.205, False), (0.795, True)):
                    cy8 = cy_n * FACE_H
                    # 斜线端点落在管腔中段，超出管壁的部分稍后会被重挖
                    # 的内缝抹掉，所以不必精确对齐管口
                    # 端点落在靠中间那一节的中心，而不是条的腰部——落在腰部
                    # 会把「8」字中间那道细腰填成一块绿，形态就废了
                    y_end = cy8 + (-0.42 * u8 if up else 0.42 * u8)
                    # 斜线比竖管细：参考图里约是管宽的 0.4 倍
                    draw_chevron(dg8, 0.235 * W, 0.765 * W, y_end, up,
                                 2 * u8 * 0.112)
                # 重挖四根竖管的内缝：斜线伸进管腔的那一截被抹掉，
                # 视觉上斜线正好从管壁内侧起笔，不会在管口外留绿角
                for ny in (0.205, 0.795):
                    for nx in (0.235, 0.765):
                        punch_slot(dg8, nx * W, ny * FACE_H, u8)
    elif index < 27:
        n = index - 17
        if n == 1:
            draw_dot(layer(BLUE), W * 0.5, FACE_H * 0.5, W * 0.50,
                     big=True, inner=layer(RED), green=layer(GREEN))
        else:
            for nx, ny, sc, color in layout_tong(n):
                draw_dot(layer(color), nx * W, ny * FACE_H, W * 0.225 * sc)
    else:
        ch = ["東", "南", "西", "北", "中", "發", "白"][index - 27]
        color = RED if index == 31 else (GREEN if index == 32 else BLUE)
        d = layer(color)
        if index == 33:
            # 白板：双框，内外线粗细接近、间距收紧（参考实物牌）
            d.rounded_rectangle([S(W * 0.20), S(FACE_H * 0.14),
                                 S(W * 0.80), S(FACE_H * 0.86)],
                                radius=S(10), outline=255, width=SW(4.0 * INK_GAIN))
            d.rounded_rectangle([S(W * 0.245), S(FACE_H * 0.185),
                                 S(W * 0.755), S(FACE_H * 0.815)],
                                radius=S(7), outline=255, width=SW(2.8 * INK_GAIN))
        else:
            d.text((S(W * 0.5), S(FACE_H * 0.5)), ch,
                   font=font(178), fill=255, anchor="mm",
                   stroke_width=S(TEXT_STROKE * 1.35), stroke_fill=255)
    return [(c, m) for c, (m, _) in layers.items()]


def make_tile(index):
    random.seed(100 + index)
    p = Painter()
    build_body(p)
    engrave(p, glyph_masks(index))
    return p.img.resize((W, H), Image.LANCZOS)


def make_back():
    random.seed(999)
    p = Painter()
    face = build_body(p, back_side=True)
    # 背壳中央的浅凹台（真实牌背常有一块内凹方框）
    inset = (W * 0.145, FACE_H * 0.10, W * 0.855, FACE_H * 0.90)
    p.stroke(inset, 12, (8, 38, 30), 3.2)
    p.stroke((inset[0] + 3, inset[1] + 3, inset[2] - 3, inset[3] - 3),
             10, (120, 186, 158), 2.0, dir_alpha())
    # 四角凹点
    dm = new_l(0)
    dd = ImageDraw.Draw(dm)
    for cx, cy in ((0.27, 0.22), (0.73, 0.22), (0.27, 0.78), (0.73, 0.78)):
        x, y = cx * W, cy * FACE_H
        dd.ellipse([S(x - 5), S(y - 5), S(x + 5), S(y + 5)], fill=255)
    p.over((10, 44, 35), ImageChops.multiply(dm, face))
    return p.img.resize((W, H), Image.LANCZOS)


NAMES = (
    ["man%d" % i for i in range(1, 10)]
    + ["pin%d" % i for i in range(1, 10)]
    + ["tong%d" % i for i in range(1, 10)]
    + ["east", "south", "west", "north", "zhong", "fa", "bai"]
)


def make_ui_preview(tiles, back):
    """静态示意图：贴图接入后的牌桌观感（非程序实机截图）。"""
    PW, PH = 1180, 700
    canvas = Image.new("RGBA", (PW, PH), (0, 0, 0, 255))

    # 绿绒布桌面：垂直渐变 + 中心柔光 + 细噪点
    strip = Image.new("RGB", (1, PH))
    sp = strip.load()
    for y in range(PH):
        t = y / float(PH - 1)
        sp[0, y] = (int(10 + 8 * t), int(62 - 22 * t), int(38 - 14 * t))
    canvas.paste(strip.resize((PW, PH)))
    glow = Image.new("RGBA", (PW, PH), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse([PW * 0.18, -PH * 0.28, PW * 0.82, PH * 0.72],
                                 fill=(60, 170, 110, 46))
    canvas.alpha_composite(glow.filter(ImageFilter.GaussianBlur(90)))
    noise = Image.effect_noise((PW, PH), 12).convert("RGB")
    canvas.paste(Image.blend(canvas.convert("RGB"),
                             ImageChops.overlay(canvas.convert("RGB"), noise), 0.18).convert("RGBA"))

    d = ImageDraw.Draw(canvas)
    d.rounded_rectangle([12, 12, PW - 12, PH - 12], radius=28,
                        outline=(214, 165, 62, 255), width=7)

    def paste(tile, x, y, w, h, angle=0.0, shadow=True):
        small = tile.resize((w, h), Image.LANCZOS)
        if shadow:
            sh = Image.new("RGBA", (PW, PH), (0, 0, 0, 0))
            ImageDraw.Draw(sh).ellipse(
                [x + w * 0.06, y + h * 0.88, x + w * 0.94, y + h * 1.10],
                fill=(0, 0, 0, 120))
            canvas.alpha_composite(sh.filter(ImageFilter.GaussianBlur(7)))
        rot = small.rotate(angle, resample=Image.BICUBIC, expand=True)
        canvas.alpha_composite(rot, (int(x - (rot.width - w) / 2),
                                     int(y - (rot.height - h) / 2)))

    # 对家立牌（牌背），重叠排列
    bx = (PW - 13 * 24) / 2
    for i in range(13):
        paste(back, bx + i * 24, 42, 32, 44, shadow=False)

    # 北家 / 南家牌河
    north = [tiles[i] for i in (2, 13, 24, 27, 31, 33)]
    south = [tiles[i] for i in (0, 9, 18, 20, 29, 32)]
    for k, t in enumerate(north):
        paste(t, 350 + k * 82, 108, 54, 74)
    for k, t in enumerate(south):
        paste(t, 350 + k * 82, 344, 54, 74)

    # 左右两家的几张弃牌
    for k, idx in enumerate((4, 15, 22)):
        paste(tiles[idx], 66, 190 + k * 66, 40, 55)
        paste(tiles[idx + 3], PW - 106, 190 + k * 66, 40, 55)

    # 己方手牌：14 张，轻微倾斜与高低不齐
    hand = [0, 1, 2, 3, 4, 10, 14, 18, 21, 26, 27, 31, 32, 33]
    hx = (PW - 14 * 78) / 2
    for k, idx in enumerate(hand):
        ang = ((k * 37) % 101) / 100.0 * 3.2 - 1.6
        jy = ((k * 61 + 17) % 101) / 100.0 * 2.6 - 1.3
        paste(tiles[idx], hx + k * 78, 520 + jy, 72, 99, ang)

    d = ImageDraw.Draw(canvas)
    label = ImageFont.truetype(FONT_PATH, 20)
    d.text((PW / 2, 258), "欢乐麻将 · 贴图化效果预览", font=label,
           fill=(255, 231, 160, 230), anchor="mm")
    d.text((PW / 2, 655), "静态示意图（非实机截图） · 贴图由 tools/gen_tiles.py 生成",
           font=ImageFont.truetype(FONT_PATH, 15), fill=(210, 225, 210, 170), anchor="mm")

    preview = os.path.join(ROOT, "tools", "ui_preview.png")
    canvas.convert("RGB").save(preview)
    print("  ui preview ok -> %s" % preview)


def main():
    tiles = []
    for i, name in enumerate(NAMES):
        img = make_tile(i)
        img.save(os.path.join(OUT, name + ".png"))
        tiles.append(img)
        print("  %-8s ok" % name)
    back = make_back()
    back.save(os.path.join(OUT, "back.png"))
    tiles.append(back)
    print("  back     ok")

    # 总览图：7 列 x 5 行，深绿绒布底
    tw, th = 128, 176
    cols, gap, pad = 7, 12, 20
    rows = (len(tiles) + cols - 1) // cols
    sheet = Image.new("RGB", (pad * 2 + cols * tw + (cols - 1) * gap,
                              pad * 2 + rows * th + (rows - 1) * gap), (11, 52, 34))
    for i, t in enumerate(tiles):
        x = pad + (i % cols) * (tw + gap)
        y = pad + (i // cols) * (th + gap)
        small = t.resize((tw, th), Image.LANCZOS)
        sheet.paste(small, (x, y), small.getchannel("A"))
    preview = os.path.join(ROOT, "tools", "tile_preview.png")
    sheet.save(preview)
    print("  preview ok -> %s" % preview)
    make_ui_preview(tiles, back)


if __name__ == "__main__":
    main()
