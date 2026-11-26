"""Sinais técnicos da gravação: tela preta, quadro congelado, trocas de cena e os picos de volume da voz
(candidatos a reação: grito, risada, susto).

Uso: python sinais.py <ffmpeg> <gravação original> <mic16.wav> <saida.json>
"""
import json
import os
import re
import subprocess
import sys


def run(cmd):
    return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace").stderr


def main():
    ff, source, mic, out = sys.argv[1:5]

    # Vídeo em baixa resolução: tela preta, congelamento e trocas de cena numa leitura só.
    log = run([ff, "-hide_banner", "-nostats", "-i", source, "-an", "-vf",
               "fps=5,scale=320:180,blackdetect=d=1:pix_th=0.08,freezedetect=n=0.003:d=4,scdet=threshold=8",
               "-f", "null", "-"])
    blacks = [{"start": float(a), "end": float(b)} for a, b in re.findall(r"black_start:([\d.]+) black_end:([\d.]+)", log)]
    freeze_starts = [float(x) for x in re.findall(r"freeze_start: ([\d.]+)", log)]
    freeze_ends = [float(x) for x in re.findall(r"freeze_end: ([\d.]+)", log)]
    freezes = [{"start": s, "end": e} for s, e in zip(freeze_starts, freeze_ends + [None] * len(freeze_starts))]
    scenes = [round(float(t), 2) for t in re.findall(r"lavfi\.scd\.time: ([\d.]+)", log)]

    # Volume momentâneo da voz (janela de 400 ms): os picos bem acima da fala normal.
    log = run([ff, "-hide_banner", "-nostats", "-i", mic, "-af", "ebur128=metadata=1,ametadata=print:key=lavfi.r128.M", "-f", "null", "-"])
    times = [float(t) for t in re.findall(r"pts_time:([\d.]+)", log)]
    values = [float(v) for v in re.findall(r"lavfi\.r128\.M=(-?[\d.]+)", log)]
    series = [(t, v) for t, v in zip(times, values) if v > -70]
    peaks = []
    if series:
        loud = sorted(v for _, v in series)
        p90 = loud[int(len(loud) * 0.90)]
        threshold = max(p90 + 4, -24)
        last = -10
        for t, v in series:
            if v >= threshold and t - last > 3:
                peaks.append({"t": round(t, 1), "lufs": round(v, 1)})
                last = t
        peaks = sorted(peaks, key=lambda p: -p["lufs"])[:40]
        peaks.sort(key=lambda p: p["t"])

    result = {"black": blacks, "freeze": freezes, "scene_changes": scenes, "voice_peaks": peaks}
    tmp = out + ".part"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False)
    os.replace(tmp, out)
    print(f"tela preta {len(blacks)}, congelado {len(freezes)}, trocas de cena {len(scenes)}, picos de voz {len(peaks)}")


if __name__ == "__main__":
    main()
