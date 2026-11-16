# -*- coding: utf-8 -*-
"""把素材牌面批量合成为本项目的贴图（先输出到 tools/_mixed，不覆盖 Assets）。

映射依据：对 tools/_sheet 的网格切分结果逐张目视核对。
  tong1-9  <- r1c1..r1c5, r2c1..r2c4
  pin1-9   <- 按实际条数归类（素材条子有重号/缺号，见 PIN 注释）
  man1-9   <- r6c1..r6c5, r7c1..r7c4
  east..north <- r5c3..r5c6
  zhong/fa <- r5c1/r5c2   bai 缺
  back     <- r5c7
"""
import os
import sys
import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import gen_tiles as G
import import_tiles as I

SHEET = os.path.join(ROOT, "tools", "_sheet")
OUT = os.path.join(ROOT, "tools", "_mixed")
os.makedirs(OUT, exist_ok=True)

MAP = {}
for i in range(1, 10):
    MAP["tong%d" % i] = ("r1_c%d" % i) if i <= 5 else ("r2_c%d" % (i - 5))
# 条子：逐张目视核对（投影法在密集排布上会把贯通列合并，不能只信投影）
#   r3_c1=9(3x3)  r3_c2=8(M/M)  r3_c3=2  r3_c4=4(2x2)  r3_c5=3(斜品字)
#   r4_c1=5(四角绿+中心红)  r4_c2=6(3x2)  r4_c3=7(1红+3+3)  r4_c4=幺鸡
PIN = {
    1: "r4_c4",   # 幺鸡
    2: "r3_c3",   # 2 根
    3: "r3_c5",   # 3 根斜品字
    4: "r3_c4",   # 4 根 2x2
    5: "r4_c1",   # 5 根 四角 + 中心红
    6: "r4_c2",   # 6 根 3x2
    7: "r4_c3",   # 7 根 1+3+3
    8: "r3_c2",   # 8 根 M/M
    9: "r3_c1",   # 9 根 3x3
}
for i, s in PIN.items():
    if s:
        MAP["pin%d" % i] = s
for i in range(1, 10):
    MAP["man%d" % i] = ("r6_c%d" % i) if i <= 5 else ("r7_c%d" % (i - 5))
MAP.update({
    "east": "r5_c3", "south": "r5_c4", "west": "r5_c5", "north": "r5_c6",
    "zhong": "r5_c1", "fa": "r5_c2", "bai": "r5_c7",
})
# 素材无牌背：牌背仍由 gen_tiles.make_back 生成（Assets 里已有，无需覆盖）


def to_layer(src_png, gamma=1.35):
    """素材牌面 -> 白底转透明的 RGBA 图层（超采样尺寸）。"""
    face = I.fit_face(Image.open(src_png))
    a = np.asarray(face).astype(np.float32)
    mn = a.min(axis=2)
    area = np.clip(255.0 - mn, 0, 255)
    af = np.clip(area / 255.0, 1e-6, 1.0)
    C = np.clip((a - (1 - af)[..., None] * 255.0) / af[..., None], 0, 255)
    rgb = Image.fromarray(C.astype(np.uint8), "RGB")
    al = Image.fromarray(area.astype(np.uint8), "L").point(
        lambda p: int(255 * (p / 255.0) ** gamma))
    layer = rgb.convert("RGBA")
    layer.putalpha(al)
    return layer.resize((G.CW, G.CH), Image.LANCZOS)


def main():
    G.random.seed(7)
    base = G.Painter()
    G.build_body(base)

    done, missing = [], []
    for name, cell in MAP.items():
        src = os.path.join(SHEET, cell + ".png")
        if not os.path.exists(src):
            missing.append(name)
            continue
        layer = to_layer(src)
        p = G.Painter()
        p.img = base.img.copy()
        p.img = Image.alpha_composite(p.img, layer)
        p.img.resize((G.W, G.H), Image.LANCZOS).save(
            os.path.join(OUT, "%s.png" % name))
        done.append(name)

    print("done %d" % len(done))
    print("missing:", missing)



if __name__ == "__main__":
    main()
