# -*- coding: utf-8 -*-
"""点击『设置』标签页并重新截图采样。"""
import ctypes
import ctypes.wintypes as wt
import time
from PIL import ImageGrab
from collections import Counter

user32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

hwnd = user32.FindWindowW(None, "DDL拯救器")
if not hwnd:
    raise SystemExit("WINDOW NOT FOUND")
user32.SetForegroundWindow(hwnd)
time.sleep(0.8)

rect = wt.RECT()
user32.GetWindowRect(hwnd, ctypes.byref(rect))
x, y = rect.left, rect.top
w, h = rect.right - x, rect.bottom - y
print("rect:", x, y, w, h)

# 『设置』标签：窗口内相对 (w*0.155, h*0.155)
cx = x + int(w * 0.155)
cy = y + int(h * 0.155)
user32.SetCursorPos(cx, cy)
time.sleep(0.2)
user32.mouse_event(0x0002, 0, 0, 0, 0)  # LEFTDOWN
user32.mouse_event(0x0004, 0, 0, 0, 0)  # LEFTUP
time.sleep(1.0)
print("clicked at", cx, cy)

img = ImageGrab.grab(bbox=(x, y, x + w, y + h))
img.save(r"D:\DDLaunch\tools\smoke_settings.png")

data = list(img.getdata())
cnt = Counter(data)
total = len(data)


def near(a, bb, tol=4):
    return all(abs(p - q) <= tol for p, q in zip(a, bb))


targets = {
    "CardBg.dark #26272B": (0x26, 0x27, 0x2B),
    "Toolbar.dark #212226": (0x21, 0x22, 0x26),
    "AppBg.dark #1B1C1E": (0x1B, 0x1C, 0x1E),
    "InputBg.dark #1E1F23": (0x1E, 0x1F, 0x23),
    "WHITE": (0xFF, 0xFF, 0xFF),
    "Aero2灰 #F0F0F0": (0xF0, 0xF0, 0xF0),
}
for name, rgb in targets.items():
    c = sum(v for k, v in cnt.items() if near(k, rgb))
    print(f"  {name:24s} {c*100.0/total:6.2f}%")
