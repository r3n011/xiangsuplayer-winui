# -*- coding: utf-8 -*-
"""自包含验证驱动：启动应用 → 等待 → 截图 → 可选 UI 操作 → 截图。
在同一进程内完成，避免工具会话结束时回收应用进程导致'闪退'假象。
用法: python tools/drive.py <out1.png> [steps.json]
steps.json 每项: {"click": [x,y]} / {"type": "text"} / {"key": "RETURN"} / {"wait": ms} / {"shot": "out.png"}
"""
import json
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, "bin", "x64", "Debug", "net10.0-windows10.0.26100.0", "PixelPlayer.WinUI.exe")
sys.path.insert(0, os.path.join(ROOT, "tools"))

import shot  # noqa: E402
import ui    # noqa: E402

# 常用虚拟键
KEYS = {
    "RETURN": 0x0D, "ENTER": 0x0D, "ESC": 0x1B, "TAB": 0x09,
    "BACK": 0x08, "SPACE": 0x20, "DOWN": 0x28, "UP": 0x26,
    "LEFT": 0x25, "RIGHT": 0x27, "F6": 0x75,
}


def kill_stale():
    """清理同名残留实例，避免点击/截图串扰到别的窗口。"""
    for pid in shot.pids_of("PixelPlayer.WinUI"):
        try:
            import ctypes
            h = ctypes.windll.kernel32.OpenProcess(0x0001, False, pid)
            if h:
                ctypes.windll.kernel32.TerminateProcess(h, 1)
                ctypes.windll.kernel32.CloseHandle(h)
                print("killed stale pid", pid)
        except Exception as ex:
            print("kill failed", pid, ex)


def main():
    out1 = sys.argv[1] if len(sys.argv) > 1 else "shot_home.png"
    steps_file = sys.argv[2] if len(sys.argv) > 2 else None

    kill_stale()
    proc = subprocess.Popen([EXE], cwd=ROOT)
    time.sleep(10)
    rc = proc.poll()
    print("pid:", proc.pid, "alive:", rc is None, "exitcode:", rc)
    if rc is not None:
        sys.exit(1)

    pids = {proc.pid}
    hwnd = shot.find_window(pids)
    if not hwnd:
        print("FAIL: window not found for pid", proc.pid)
        sys.exit(1)
    print("window found:", hwnd)
    shot.capture(hwnd, out1)
    print("shot:", out1)

    if steps_file:
        with open(steps_file, encoding="utf-8") as f:
            steps = json.load(f)
        for step in steps:
            if "click" in step:
                x, y = step["click"]
                ui.click(hwnd, x, y)
                print("click", x, y)
            elif "type" in step:
                ui.type_text(step["type"])
                print("type", step["type"])
            elif "key" in step:
                name = str(step["key"]).upper()
                vk = int(KEYS.get(name, 0), 0) if name not in KEYS else KEYS[name]
                ui.key(vk)
                print("key", name)
            elif "wait" in step:
                time.sleep(step["wait"] / 1000.0)
                print("wait", step["wait"])
            elif "shot" in step:
                shot.capture(hwnd, step["shot"])
                print("shot:", step["shot"])
            time.sleep(0.6)
    print("DONE")


if __name__ == "__main__":
    main()
