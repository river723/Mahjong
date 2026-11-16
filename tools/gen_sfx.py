"""合成麻将音效 wav（纯标准库，无第三方依赖）。

与语音包一样采用「离线预制短音频」方案：运行时只做播放，不做合成。
输出 16kHz / 16bit / mono PCM，与 Assets/Voice 下语音格式一致。

用法: python tools/gen_sfx.py [输出目录]
"""
import math
import os
import random
import struct
import sys
import wave

SR = 16000
PEAK = 0.72


def add_wood_hit(buf, start, amp, freqs, decay, noise, seed):
    """一记麻将牌拍桌声：滤波噪声瞬态 + 几条快速衰减的木质谐振。"""
    rnd = random.Random(seed)
    lp = 0.0
    n = int(decay * 6 * SR)
    start_i = int(start * SR)
    for i in range(n):
        idx = start_i + i
        if idx >= len(buf):
            break
        t = i / float(SR)
        env = math.exp(-t / decay)
        lp = lp * 0.55 + (rnd.random() * 2 - 1) * 0.45
        transient = lp * noise * math.exp(-t / (decay * 0.22))
        body = 0.0
        for j, f in enumerate(freqs):
            # 木头受击时音高会略微下滑
            f2 = f * (1.0 - 0.10 * min(1.0, t / decay))
            body += math.sin(2 * math.pi * f2 * t) / (j + 1) ** 1.4
        buf[idx] += amp * env * (body * 0.55 + transient)


def add_tone(buf, start, freq, dur, amp, decay, harmonics=(1.0, 0.35, 0.12)):
    """柔和铃音（用于胡牌/提示），基频 + 泛音。"""
    start_i = int(start * SR)
    n = int(dur * SR)
    for i in range(n):
        idx = start_i + i
        if idx >= len(buf):
            break
        t = i / float(SR)
        env = math.exp(-t / decay) * min(1.0, t / 0.008)
        v = sum(h * math.sin(2 * math.pi * freq * (k + 1) * t)
                for k, h in enumerate(harmonics))
        buf[idx] += amp * env * v / sum(harmonics)


def add_rub(buf, start, dur, amp, seed):
    """摸牌时的轻微摩擦声（低音量）。"""
    rnd = random.Random(seed)
    lp = 0.0
    start_i = int(start * SR)
    for i in range(int(dur * SR)):
        idx = start_i + i
        if idx >= len(buf):
            break
        t = i / float(SR)
        lp = lp * 0.82 + (rnd.random() * 2 - 1) * 0.18
        env = math.sin(math.pi * i / (dur * SR)) ** 1.5
        buf[idx] += amp * env * lp


def write_wav(path, buf):
    peak = max(1e-6, max(abs(v) for v in buf))
    gain = PEAK * 32767.0 / peak
    fade = int(0.003 * SR)
    frames = []
    for i, v in enumerate(buf):
        f = 1.0
        if i < fade:
            f = i / float(fade)
        if i > len(buf) - fade - 1:
            f = min(f, (len(buf) - 1 - i) / float(fade))
        s = max(-32768, min(32767, int(v * gain * f)))
        frames.append(struct.pack('<h', s))
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(b''.join(frames))


def make(dur):
    return [0.0] * int(dur * SR)


def build(out_dir):
    os.makedirs(out_dir, exist_ok=True)

    # 出牌：一记干脆的「嗒」
    b = make(0.14)
    add_wood_hit(b, 0.0, 1.0, (190, 430, 980), 0.030, 0.60, 11)
    write_wav(os.path.join(out_dir, 'discard.wav'), b)

    # 摸牌：轻摩擦（备用，默认不播，避免太吵）
    b = make(0.11)
    add_rub(b, 0.0, 0.10, 0.85, 21)
    write_wav(os.path.join(out_dir, 'draw.wav'), b)

    # 碰：两声叠在一起的重击
    b = make(0.24)
    add_wood_hit(b, 0.0, 1.0, (160, 370, 850), 0.042, 0.65, 31)
    add_wood_hit(b, 0.045, 0.65, (210, 470, 1020), 0.030, 0.45, 32)
    write_wav(os.path.join(out_dir, 'pong.wav'), b)

    # 杠：三连击，比碰更有分量
    b = make(0.32)
    add_wood_hit(b, 0.0, 1.0, (145, 340, 790), 0.045, 0.65, 41)
    add_wood_hit(b, 0.068, 0.78, (175, 390, 900), 0.040, 0.60, 42)
    add_wood_hit(b, 0.140, 0.92, (155, 360, 830), 0.050, 0.65, 43)
    write_wav(os.path.join(out_dir, 'kong.wav'), b)

    # 胡：上行三音铃 + 收尾长音
    b = make(1.15)
    add_tone(b, 0.00, 523.25, 0.55, 0.85, 0.30)   # C5
    add_tone(b, 0.13, 659.25, 0.55, 0.85, 0.30)   # E5
    add_tone(b, 0.26, 783.99, 0.85, 1.00, 0.42)   # G5
    add_tone(b, 0.26, 1046.5, 0.85, 0.45, 0.38)   # C6 泛音层
    write_wav(os.path.join(out_dir, 'win.wav'), b)

    # 流局：低沉的下行两音
    b = make(0.62)
    add_tone(b, 0.00, 329.63, 0.30, 0.80, 0.20, (1.0, 0.18, 0.05))
    add_tone(b, 0.20, 246.94, 0.42, 0.80, 0.28, (1.0, 0.18, 0.05))
    write_wav(os.path.join(out_dir, 'drawgame.wav'), b)

    # 需要玩家操作时的轻提示（自摸询问 / 可碰杠胡）
    b = make(0.26)
    add_tone(b, 0.00, 880.0, 0.09, 0.70, 0.045, (1.0, 0.2, 0.0))
    add_tone(b, 0.10, 1174.7, 0.12, 0.70, 0.055, (1.0, 0.2, 0.0))
    write_wav(os.path.join(out_dir, 'prompt.wav'), b)

    print('generated 7 sfx ->', out_dir)


if __name__ == '__main__':
    default = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                           'src', 'Mahjong.App', 'Assets', 'Sounds')
    build(sys.argv[1] if len(sys.argv) > 1 else default)
