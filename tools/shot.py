# -*- coding: utf-8 -*-
"""按进程名截图：自动恢复最小化窗口，PrintWindow 后台截图（尽量不抢焦点）。"""
import ctypes as ct
import subprocess
import sys
from ctypes import wintypes

from PIL import Image

user32 = ct.windll.user32
gdi32 = ct.windll.gdi32
user32.SetProcessDPIAware()

PROC = sys.argv[1] if len(sys.argv) > 1 else "PixelPlayer.WinUI"
OUT = sys.argv[2] if len(sys.argv) > 2 else "shot.png"
WIDTH = int(sys.argv[3]) if len(sys.argv) > 3 else 0
HEIGHT = int(sys.argv[4]) if len(sys.argv) > 4 else 0


def pids_of(name):
    out = subprocess.run(
        ["tasklist", "/FI", f"IMAGENAME eq {name}.exe", "/FO", "CSV", "/NH"],
        capture_output=True, text=True, encoding="gbk", errors="ignore").stdout
    pids = set()
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) >= 2 and parts[0].lower() == (name + ".exe").lower():
            try:
                pids.add(int(parts[1]))
            except ValueError:
                pass
    return pids


def find_window(pids):
    best = None
    best_area = 0

    def enum_proc(hwnd, _):
        nonlocal best, best_area
        if user32.IsWindowVisible(hwnd):
            pid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ct.byref(pid))
            if pid.value in pids:
                rect = wintypes.RECT()
                user32.GetWindowRect(hwnd, ct.byref(rect))
                area = max(0, rect.right - rect.left) * max(0, rect.bottom - rect.top)
                if area > best_area:
                    best, best_area = hwnd, area
        return True

    EnumProc = ct.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user32.EnumWindows(EnumProc(enum_proc), 0)
    return best


def capture(hwnd, out_path):
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ct.byref(rect))
    w, h = rect.right - rect.left, rect.bottom - rect.top

    hdc_window = user32.GetWindowDC(hwnd)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_window)
    bitmap = gdi32.CreateCompatibleBitmap(hdc_window, w, h)
    gdi32.SelectObject(hdc_mem, bitmap)
    ok = user32.PrintWindow(hwnd, hdc_mem, 2)

    class BITMAPINFOHEADER(ct.Structure):
        _fields_ = [
            ("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG),
            ("biHeight", wintypes.LONG), ("biPlanes", wintypes.WORD),
            ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
            ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
            ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD),
            ("biClrImportant", wintypes.DWORD),
        ]

    class BITMAPINFO(ct.Structure):
        _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]

    bmi = BITMAPINFO()
    bmi.bmiHeader.biSize = ct.sizeof(BITMAPINFOHEADER)
    bmi.bmiHeader.biWidth = w
    bmi.bmiHeader.biHeight = -h
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    bmi.bmiHeader.biCompression = 0

    buf = ct.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(hdc_mem, bitmap, 0, h, buf, ct.byref(bmi), 0)

    img = Image.frombuffer("RGBA", (w, h), buf.raw, "raw", "BGRA", 0, 1)
    img.convert("RGB").save(out_path)

    gdi32.DeleteObject(bitmap)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(hwnd, hdc_window)
    return ok, (w, h)


pids = pids_of(PROC)
hwnd = find_window(pids) if pids else None
if not hwnd:
    print("window not found", pids)
    sys.exit(1)

if user32.IsIconic(hwnd):
    user32.ShowWindow(hwnd, 9)  # SW_RESTORE
    print("restored minimized window")

if WIDTH > 0 and HEIGHT > 0:
    # SWP_NOACTIVATE | SWP_NOZORDER
    user32.SetWindowPos(hwnd, 0, 80, 60, WIDTH, HEIGHT, 0x0010 | 0x0004)

ok, size = capture(hwnd, OUT)
print(f"PrintWindow ok={ok} hwnd={hwnd} {size} -> {OUT}")
