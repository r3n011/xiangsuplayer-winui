# -*- coding: utf-8 -*-
"""生成示例音乐库：8 首合成 WAV（含 ID3 标签）+ 同步 .lrc 歌词。"""
import math
import os
import struct
import wave

from mutagen.id3 import ID3, TALB, TIT2, TPE1, TRCK, TDRC, APIC
from mutagen.flac import Picture

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "sample-music")
os.makedirs(OUT, exist_ok=True)

SR = 44100

SONGS = [
    # (文件名, 标题, 艺术家, 专辑, 曲号, 年份, 基频Hz, 时长s, BPM, 歌词)
    ("01 - 夜色霓虹.wav", "夜色霓虹", "星轨乐队", "城市光谱", 1, 2024, 220.0, 42, 96, [
        "[00:00.50]霓虹落进夜里",
        "[00:04.00]街灯慢慢呼吸",
        "[00:07.50]我们走过十字路口",
        "[00:11.00]把沉默留在雨里",
        "[00:15.00]夜色霓虹 照亮来时的路",
        "[00:19.50]每一步都算数",
        "[00:23.00]夜色霓虹 别问去处",
        "[00:27.00]风会带我们回去",
        "[00:31.50]城市在低声歌唱",
        "[00:35.00]唱给没睡的人听",
        "[00:38.50]夜色霓虹 一直亮到天明",
    ]),
    ("02 - 光谱.wav", "光谱", "星轨乐队", "城市光谱", 2, 2024, 261.6, 38, 108, [
        "[00:00.50]把白昼拆成七种颜色",
        "[00:04.50]挑一种最接近心情的",
        "[00:08.50]光谱在指尖流转",
        "[00:12.50]像时间温柔的针脚",
        "[00:17.00]我们都是光的旅人",
        "[00:21.00]在波长里寻找共鸣",
        "[00:25.50]光谱 照见彼此",
        "[00:29.50]也照见明天的自己",
        "[00:34.00]光谱 一直延伸",
    ]),
    ("03 - 通勤线.wav", "通勤线", "星轨乐队", "城市光谱", 3, 2024, 196.0, 35, 120, [
        "[00:00.50]七点十五的站台",
        "[00:04.00]人群像潮水漫过来",
        "[00:08.00]耳机里放着旧歌单",
        "[00:12.00]把疲惫调成静音",
        "[00:16.50]通勤线 连接两个梦",
        "[00:20.50]一头是家 一头是远方",
        "[00:25.00]通勤线 别太快靠站",
        "[00:29.00]让我多听一段旋律",
        "[00:33.00]到站之前 再听一遍",
    ]),
    ("04 - 午夜电台.wav", "午夜电台", "星轨乐队", "城市光谱", 4, 2024, 233.1, 40, 88, [
        "[00:00.50]午夜电台 调频 FM 98.5",
        "[00:05.00]主播声音像旧毛衣",
        "[00:09.50]温暖 有点起球",
        "[00:14.00]点歌的人说 给十年后的自己",
        "[00:19.00]午夜电台 别关掉",
        "[00:23.50]让音乐陪我说晚安",
        "[00:28.00]午夜电台 还在放",
        "[00:32.50]那首我们都会唱的歌",
        "[00:37.00]天亮之前 再放一遍",
    ]),
    ("05 - 山谷回信.wav", "山谷回信", "林间笔记", "自然备忘录", 1, 2023, 174.6, 44, 76, [
        "[00:00.50]对着山谷喊你的名字",
        "[00:05.00]回音绕了三圈才回来",
        "[00:09.50]溪水把消息捎给下游",
        "[00:14.00]风把答案写在叶子上",
        "[00:19.00]山谷回信 迟到但不缺席",
        "[00:24.00]就像你总会在黄昏想起我",
        "[00:29.00]山谷回信 一字一句",
        "[00:34.00]都是大自然的封面故事",
        "[00:39.00]等雾散了 再念一遍",
    ]),
    ("06 - 松林漫步.wav", "松林漫步", "林间笔记", "自然备忘录", 2, 2023, 164.8, 36, 72, [
        "[00:00.50]踩着松针走进下午",
        "[00:04.50]阳光被筛成碎金",
        "[00:09.00]松果落地的声音",
        "[00:13.00]是森林的标点符号",
        "[00:17.50]松林漫步 慢一点再慢一点",
        "[00:22.50]时间在这里学会散步",
        "[00:27.50]松林漫步 深呼吸",
        "[00:32.00]把城市留在很远的地方",
    ]),
    ("07 - 湖面涟漪.wav", "湖面涟漪", "林间笔记", "自然备忘录", 3, 2023, 207.7, 39, 84, [
        "[00:00.50]扔一颗石子进湖里",
        "[00:04.50]涟漪一圈圈扩散",
        "[00:09.00]像某句话在心里",
        "[00:13.00]荡了很久都没停",
        "[00:17.50]湖面涟漪 倒映云的形状",
        "[00:22.50]倒映我没说出口的话",
        "[00:27.50]湖面涟漪 慢慢平静",
        "[00:32.00]平静也是一种回答",
        "[00:36.00]风来了 又起一圈",
    ]),
    ("08 - 星空下.wav", "星空下", "林间笔记", "自然备忘录", 4, 2023, 146.8, 46, 68, [
        "[00:00.50]把毯子铺在草地上",
        "[00:05.00]躺下来数星星",
        "[00:10.00]银河像一条旧围巾",
        "[00:15.00]搭在夜的肩膀上",
        "[00:20.00]星空下 说什么都轻",
        "[00:25.50]连心跳都怕吵醒萤火虫",
        "[00:31.00]星空下 什么都不说",
        "[00:36.50" "]就让宇宙替我们聊天",
        "[00:42.00]流星划过 许愿要快",
    ]),
]

# 简易封面：纯色渐变 PNG（用 Pillow 画，程序内置颜色）
COVERS = {
    "城市光谱": (38, 79, 158),
    "自然备忘录": (34, 110, 74),
}


def synth(freq, seconds, bpm):
    """简单和弦琶音 + 低音 + 打拍噪声，纯合成不需要素材。"""
    import array
    total = int(SR * seconds)
    buf = array.array("h", [0]) * 0
    beat = 60.0 / bpm
    noise_level = 900

    chords = [0, 4, 7, 12]  # 大三和弦音程
    for i in range(total):
        t = i / SR
        # 旋律：五声音阶琶音，每 0.5 拍换音
        step = int(t / (beat / 2)) % 8
        scale = [0, 2, 4, 7, 9, 12, 7, 4]
        mel_f = freq * (2 ** (scale[step] / 12.0))
        mel = 0.22 * math.sin(2 * math.pi * mel_f * t) * math.exp(-((t % (beat / 2)) * 6))

        # 低音：每拍一个音
        bass_step = int(t / beat) % 4
        bass_f = freq / 2 * (2 ** ([0, 0, 5, 7][bass_step] / 12.0))
        bass = 0.30 * math.sin(2 * math.pi * bass_f * t)

        # 和弦铺底
        chord = sum(math.sin(2 * math.pi * (freq / 2) * (2 ** (p / 12.0)) * t) for p in chords) * 0.045

        # 打拍：每拍起一个小噪声包络
        beat_pos = t % beat
        noise = 0.0
        if beat_pos < 0.03:
            seed = int(t * SR)
            noise = noise_level * (1 - beat_pos / 0.03) * ((seed * 2654435761) % 65536 / 65536 - 0.5)

        # 首尾淡入淡出
        env = min(1.0, t / 1.5, (seconds - t) / 2.0)
        v = (mel + bass + chord + noise) * env * 0.8
        buf.append(max(-32767, min(32767, int(v * 32767))))
    return buf


def make_cover(rgb, out_path):
    from PIL import Image, ImageDraw
    img = Image.new("RGB", (512, 512))
    d = ImageDraw.Draw(img)
    for y in range(512):
        k = y / 511
        c = tuple(int(a + (b - a) * k) for a, b in zip(rgb, tuple(min(255, x + 70) for x in rgb)))
        d.line([(0, y), (511, y)], fill=c)
    for i in range(24):
        x = 40 + i * 18
        h = 90 + (i * 37) % 220
        d.rectangle([x, 512 - h - 40, x + 10, 512 - 40], fill=(255, 255, 255, 40))
    img.save(out_path)


def main():
    cover_paths = {}
    for album, rgb in COVERS.items():
        p = os.path.join(OUT, f"cover_{album}.png")
        make_cover(rgb, p)
        cover_paths[album] = p

    for fname, title, artist, album, no, year, freq, dur, bpm, lyrics in SONGS:
        path = os.path.join(OUT, fname)
        buf = synth(freq, dur, bpm)
        with wave.open(path, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(SR)
            w.writeframes(buf.tobytes())

        # 写 ID3（WAV 支持 ID3 标签，TagLib 能读）
        try:
            tags = ID3()
            tags.add(TIT2(encoding=3, text=title))
            tags.add(TPE1(encoding=3, text=artist))
            tags.add(TALB(encoding=3, text=album))
            tags.add(TRCK(encoding=3, text=str(no)))
            tags.add(TDRC(encoding=3, text=str(year)))
            with open(cover_paths[album], "rb") as f:
                tags.add(APIC(encoding=3, mime="image/png", type=3, desc="Cover", data=f.read()))
            tags.save(path, v2_version=3)
        except Exception as e:
            print("tag fail", fname, e)

        # 同步歌词
        lrc = os.path.splitext(path)[0] + ".lrc"
        with open(lrc, "w", encoding="utf-8") as f:
            f.write(f"[ti:{title}]\n[ar:{artist}]\n[al:{album}]\n")
            f.write("\n".join(lyrics) + "\n")

        print("ok", fname, f"{dur}s")


if __name__ == "__main__":
    main()
