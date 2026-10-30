# -*- coding: utf-8 -*-
"""冒烟验证：截取 DDLSaver 主窗口，统计关键主题色占比。"""
import ctypes
import ctypes.wintypes as wt
from PIL import ImageGrab
from collections import Counter

user32 = ctypes.windll.user32
# 关键：让本进程 DPI-aware，保证 GetWindowRect 与 ImageGrab 同一坐标系
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    user32.SetProcessDPIAware()

hwnd = user32.FindWindowW(None, "DDL拯救器")
if not hwnd:
    raise SystemExit("WINDOW NOT FOUND")

# 确认窗口可见
if not user32.IsWindowVisible(hwnd):
    raise SystemExit("WINDOW NOT VISIBLE")

# 拉到前台再截屏（否则截到的是盖在上面的其他窗口）
user32.ShowWindow(hwnd, 9)  # SW_RESTORE
user32.SetForegroundWindow(hwnd)
import time
time.sleep(1.2)

rect = wt.RECT()
user32.GetWindowRect(hwnd, ctypes.byref(rect))
x, y, r, b = rect.left, rect.top, rect.right, rect.bottom
print("window rect:", x, y, r, b, " size:", r - x, b - y)

img = ImageGrab.grab(bbox=(x, y, r, b))
img.save(r"D:\DDLaunch\tools\smoke_dark.png")
data = list(img.getdata())
total = len(data)
cnt = Counter(data)


def near(a, bb, tol=4):
    return all(abs(p - q) <= tol for p, q in zip(a, bb))


targets = {
    "CardBg.dark #26272B": (0x26, 0x27, 0x2B),
    "Toolbar.dark #212226": (0x21, 0x22, 0x26),
    "AppBg.dark #1B1C1E": (0x1B, 0x1C, 0x1E),
    "InputBg.dark #1E1F23": (0x1E, 0x1F, 0x23),
    "WHITE": (0xFF, 0xFF, 0xFF),
    "Aero2默认灰 #F0F0F0系": (0xF0, 0xF0, 0xF0),
    "选中蓝(默认)#CBE8F6": (0xCB, 0xE8, 0xF6),
    "ListSel.dark #324253": (0x32, 0x42, 0x53),
}
print(f"窗口内 {total} 像素：")
for name, rgb in targets.items():
    c = sum(v for k, v in cnt.items() if near(k, rgb))
    print(f"  {name:26s} {c*100.0/total:6.2f}%")
