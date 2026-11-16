"""用微软 Edge 神经语音重生成配音包（音质更好的可选方案）。

与项目自带的 SAPI 生成器共用同一份词条表（Assets/Voice/voice_manifest.txt），
输出的 wav 文件名/格式完全一致，运行时 AudioService 无需改动。

依赖：Python 3.12 + edge-tts（pip install edge-tts）+ ffmpeg
用法：C:/Python312/python.exe tools/gen_voice_edge.py [音色名]

任一文件合成失败时保留原来的 wav（SAPI 版本），不会留下半成品语音包。
"""
import asyncio
import math
import os
import re
import struct
import subprocess
import sys
import tempfile
import wave

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VOICE_DIR = os.path.join(ROOT, 'src', 'Mahjong.App', 'Assets', 'Voice')
MANIFEST = os.path.join(VOICE_DIR, 'voice_manifest.txt')
DEFAULT_VOICE = 'zh-CN-XiaoxiaoNeural'
RATE = '+10%'          # 麻将播报要干脆
SR = 16000
TARGET_PEAK = 24000.0  # 与 C# SAPI 生成器一致

EXCLAMATION = {'act_pong', 'act_kong', 'act_ckong', 'act_win',
               'act_selfdraw', 'act_kongwin'}


def read_entries():
    """从 SAPI 生成器产出的 manifest 读取 key=文本，保持词条单一来源。"""
    entries = []
    with open(MANIFEST, encoding='utf-8-sig') as f:
        for line in f:
            m = re.match(r'^([A-Za-z0-9_]+)=(.+?)(?:\s*\(\d+ms\))?\s*$', line.strip())
            if m and (m.group(1).startswith('tile_') or
                      m.group(1).startswith('act_') or
                      m.group(1).startswith('seat_') or
                      m.group(1).startswith('hint_')):
                entries.append((m.group(1), m.group(2)))
    if not entries:
        raise RuntimeError('manifest 里没有词条，请先跑一次 --genvoice')
    return entries


def postprocess(src_wav, dest_wav):
    """裁首尾静音 + 响度归一化 + 淡入淡出，输出 16k/16bit/mono PCM。"""
    with wave.open(src_wav, 'rb') as w:
        n = w.getnframes()
        raw = w.readframes(n)
    samples = struct.unpack('<%dh' % n, raw)
    peak = max(1, max(abs(s) for s in samples))
    threshold = max(120, peak / 25)

    first, last = 0, n - 1
    while first < n and abs(samples[first]) < threshold:
        first += 1
    while last > first and abs(samples[last]) < threshold:
        last -= 1

    pad = SR // 25
    first = max(0, first - pad)
    last = min(n - 1, last + pad)
    length = last - first + 1
    if length < SR // 20:
        first, length = 0, min(n, SR // 20)

    gain = min(6.0, TARGET_PEAK / peak)
    fade = min(SR // 200, length // 4)
    out = []
    for i in range(length):
        v = samples[first + i] * gain
        v = max(-32768.0, min(32767.0, v))
        if fade:
            if i < fade:
                v *= i / float(fade)
            from_end = length - 1 - i
            if from_end < fade:
                v *= from_end / float(fade)
        out.append(int(v))
    with wave.open(dest_wav, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(struct.pack('<%dh' % len(out), *out))
    return length * 1000 // SR


async def synth_one(sem, key, text, voice, tmp_dir, results):
    async with sem:
        mp3 = os.path.join(tmp_dir, key + '.mp3')
        raw_wav = os.path.join(tmp_dir, key + '_raw.wav')
        dest = os.path.join(VOICE_DIR, key + '.wav')
        # 动作词加感叹号，语气更像真人打牌喊出来的
        spoken = text + '！' if key in EXCLAMATION else text
        try:
            import edge_tts
            comm = edge_tts.Communicate(spoken, voice, rate=RATE)
            await comm.save(mp3)
            subprocess.run(
                ['ffmpeg', '-y', '-loglevel', 'error', '-i', mp3,
                 '-ar', str(SR), '-ac', '1', '-c:a', 'pcm_s16le', raw_wav],
                check=True, capture_output=True)
            ms = postprocess(raw_wav, dest)
            results[key] = ('OK', text, ms)
        except Exception as exc:  # 单个失败保留原 wav
            results[key] = ('FAIL', text, str(exc)[:120])
        finally:
            for p in (mp3, raw_wav):
                try:
                    os.remove(p)
                except OSError:
                    pass


async def main(voice):
    entries = read_entries()
    print('voice=%s  entries=%d' % (voice, len(entries)))
    with tempfile.TemporaryDirectory(prefix='mj_edge_') as tmp:
        sem = asyncio.Semaphore(4)
        results = {}
        await asyncio.gather(*(synth_one(sem, k, t, voice, tmp, results)
                               for k, t in entries))

    ok = [k for k, v in results.items() if v[0] == 'OK']
    fail = [k for k, v in results.items() if v[0] == 'FAIL']
    print('generated=%d  failed=%d' % (len(ok), len(fail)))
    for k in fail:
        print('  FAIL', k, results[k][2])

    if ok:
        lines = ['voice=%s (edge-tts, offline bundle)' % voice,
                 'format=16000Hz/16bit/mono PCM',
                 'rate=%s' % RATE,
                 'source=tools/gen_voice_edge.py',
                 'count=%d' % len(entries)]
        for k, t in entries:
            v = results.get(k)
            ms = v[2] if v and v[0] == 'OK' else 0
            lines.append('%s=%s (%dms)' % (k, t, ms))
        with open(MANIFEST, 'w', encoding='utf-8') as f:
            f.write('\n'.join(lines) + '\n')
    if fail:
        print('失败条目保留了上一次生成的 wav。')
    return 0 if not fail else 1


if __name__ == '__main__':
    v = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_VOICE
    sys.exit(asyncio.run(main(v)))
