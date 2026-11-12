# -*- coding: utf-8 -*-
"""驱动应用 UI：按进程名找窗口，发鼠标点击 / Unicode 文本输入（ctypes，无第三方依赖）。

用法:
  python ui.py activate                 # 置前
  python ui.py click X Y                # 客户区坐标点击
  python ui.py type "文本"              # 发送 Unicode 字符
  python ui.py key 0x0D                 # 发送虚拟键（回车 0x0D 等）
  python ui.py paste "文本"             # 剪贴板粘贴
"""
import ctypes as ct
import subprocess
import sys
import time
from ctypes import wintypes

user32 = ct.windll.user32
kernel32 = ct.windll.kernel32
user32.SetProcessDPIAware()

PROC = "PixelPlayer.WinUI"


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


def activate(hwnd):
    if user32.IsIconic(hwnd):
        user32.ShowWindow(hwnd, 9)
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.2)


def _send_input(*inputs):
    arr = (INPUT * len(inputs))()
    for i, inp in enumerate(inputs):
        arr[i] = inp
    user32.SendInput(len(inputs), arr, ct.sizeof(INPUT))


class MOUSEINPUT(ct.Structure):
    _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG),
                ("mouseData", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ct.POINTER(ct.c_ulong))]


class KEYBDINPUT(ct.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", ct.POINTER(ct.c_ulong))]


class _INPUTUNION(ct.Union):
    _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT)]


class INPUT(ct.Structure):
    _fields_ = [("type", wintypes.DWORD), ("union", _INPUTUNION)]


MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
MOUSEEVENTF_MOVE = 0x0001
MOUSEEVENTF_ABSOLUTE = 0x8000
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_UNICODE = 0x0004


def click(hwnd, x, y):
    activate(hwnd)
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ct.byref(rect))
    # 客户区 -> 屏幕坐标
    pt = wintypes.POINT(x, y)
    user32.ClientToScreen(hwnd, ct.byref(pt))
    # DPI: client coords given in physical px; SetProcessDPIAware so 1:1
    sx, sy = pt.x, pt.y
    # MOUSEEVENTF_ABSOLUTE 需要归一化到 0..65535（主屏）
    sw = user32.GetSystemMetrics(0)
    sh = user32.GetSystemMetrics(1)
    mx = int(sx * 65536 / sw)
    my = int(sy * 65536 / sh)
    _send_input(
        INPUT(0, _INPUTUNION(mi=MOUSEINPUT(mx, my, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, 0, None))),
    )
    time.sleep(0.05)
    _send_input(
        INPUT(0, _INPUTUNION(mi=MOUSEINPUT(mx, my, 0, MOUSEEVENTF_LEFTDOWN, 0, None))),
        INPUT(0, _INPUTUNION(mi=MOUSEINPUT(mx, my, 0, MOUSEEVENTF_LEFTUP, 0, None))),
    )


def key(vk):
    _send_input(INPUT(1, _INPUTUNION(ki=KEYBDINPUT(vk, 0, 0, 0, None))),
                INPUT(1, _INPUTUNION(ki=KEYBDINPUT(vk, 0, KEYEVENTF_KEYUP, 0, None))))


def type_text(text):
    for ch in text:
        code = ord(ch)
        _send_input(INPUT(1, _INPUTUNION(ki=KEYBDINPUT(0, code, KEYEVENTF_UNICODE, 0, None))),
                    INPUT(1, _INPUTUNION(ki=KEYBDINPUT(0, code, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, 0, None))))
        time.sleep(0.01)


def paste_text(text):
    import win32clipboard  # noqa
