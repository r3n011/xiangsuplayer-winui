# -*- coding: utf-8 -*-
"""修复示例 WAV：把 mutagen 前置的 ID3v2 标签挪进 RIFF 'ID3 ' chunk，恢复合法 WAV 结构。"""
import os
import struct

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "sample-music")


def fix(path):
    with open(path, "rb") as f:
        data = f.read()

    riff_pos = data.find(b"RIFF")
    if riff_pos <= 0:
        if riff_pos == 0:
            print("skip (already ok)", os.path.basename(path))
            return
        raise SystemExit("no RIFF: " + path)
    if data[:3] != b"ID3":
        raise SystemExit("unexpected head: " + path)

    id3 = data[:riff_pos]
    body = data[riff_pos:]

    # ID3v2 尺寸为 syncsafe int（去掉尾部 padding 也可以，保留完整亦可）
    size = (id3[6] << 21) | (id3[7] << 14) | (id3[8] << 7) | id3[9]
    id3 = id3[: 10 + size]
    if len(id3) % 2:
        id3 += b"\x00"

    # 新 RIFF：body 去掉旧 size 头，追加 ID3 chunk，并更新 RIFF size
    new_size = (len(body) - 8) + 8 + len(id3)
    out = b"RIFF" + struct.pack("<I", new_size) + body[8:] + b"ID3 " + struct.pack("<I", len(id3)) + id3

    with open(path, "wb") as f:
        f.write(out)
    print("fixed", os.path.basename(path), f"id3={len(id3)}B")


for name in sorted(os.listdir(OUT)):
    if name.lower().endswith(".wav"):
        fix(os.path.join(OUT, name))
