"""启动 Mahjong.App 并截图（调试用）。

用法: python tools/shot.py out.png [额外参数...]
"""
import ctypes
import ctypes.wintypes as wt
import subprocess
import sys
import time

from PIL import ImageGrab

user32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

out = sys.argv[1] if len(sys.argv) > 1 else "shot.png"
extra = sys.argv[2:]

exe = r"E:\cc_study\mj\src\Mahjong.App\bin\Debug\Mahjong.App.exe"
proc = subprocess.Popen([exe] + extra)
time.sleep(float(__import__("os").environ.get("SHOT_DELAY", "4")))

hwnd = user32.FindWindowW(None, "欢乐麻将")
if not hwnd:
    hwnd = proc._handle
# 置顶 + 显示，规避前台锁
user32.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040)
user32.ShowWindow(hwnd, 9)
time.sleep(0.8)

rect = wt.RECT()
user32.GetWindowRect(hwnd, ctypes.byref(rect))
img = ImageGrab.grab(bbox=(rect.left, rect.top, rect.right, rect.bottom))
img.save(out)
print("saved", out, img.size)
proc.terminate()
