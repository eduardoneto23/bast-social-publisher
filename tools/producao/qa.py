"""Revisão técnica independente de um vídeo renderizado (analisa o ARQUIVO final, não o plano).

Uso: python qa.py <ffmpeg> <ffprobe> <video.mp4> <saida.json> [horizontal|vertical]
Confere: abre, duração, resolução, proporção, FPS, áudio presente, loudness integrado, true peak, clipping,
tela preta, quadro congelado, silêncio longo. Gera também uma folha de quadros do arquivo final
(<video>.folha.jpg, 1 quadro a cada N s) para a revisão visual.
"""
import json
import os
import re
import subprocess
import sys


def run(cmd):
    return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")


def main():
    ff, fp, video, out = sys.argv[1:5]
    mode = sys.argv[5] if len(sys.argv) > 5 else "horizontal"
    problems, warnings = [], []

    probe = run([fp, "-v", "error", "-print_format", "json", "-show_format", "-show_streams", video])
    if probe.returncode != 0:
        result = {"status": "REPROVADO", "problems": ["O arquivo não abre: " + probe.stderr.strip()[:300]]}
        json.dump(result, open(out, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
        print(result["status"])
        return
    info = json.loads(probe.stdout)
    v = next(s for s in info["streams"] if s["codec_type"] == "video")
    audio = [s for s in info["streams"] if s["codec_type"] == "audio"]
    duration = float(info["format"]["duration"])
    w, h = int(v["width"]), int(v["height"])
    num, den = (int(x) for x in v["avg_frame_rate"].split("/"))
    fps = num / den if den else 0
    size_mb = int(info["format"]["size"]) / 1048576
    expected = (1920, 1080) if mode == "horizontal" else (1080, 1920)
    if (w, h) != expected:
        problems.append(f"Resolução {w}x{h}; esperado {expected[0]}x{expected[1]}.")
    if not audio:
        problems.append("Sem faixa de áudio.")
    if fps < 23:
        problems.append(f"FPS baixo: {fps:.1f}.")
    kbps = size_mb * 8 * 1024 / max(duration, 1)
    if kbps < 1500:
        warnings.append(f"Taxa média baixa para {w}x{h}: {kbps:.0f} kb/s (arquivo pode estar degradado).")

    # Loudness, true peak, tela preta, congelamento e silêncio numa única leitura.
    an = run([ff, "-hide_banner", "-nostats", "-i", video,
              "-filter_complex",
              "[0:v]blackdetect=d=0.5:pix_th=0.06,freezedetect=n=0.002:d=3[vo];"
              "[0:a]ebur128=peak=true,silencedetect=n=-45dB:d=4,astats=metadata=0:reset=0[ao]",
              "-map", "[vo]", "-map", "[ao]", "-f", "null", "-"])
    log = an.stderr
    i_lufs = re.findall(r"I:\s+(-?[\d.]+) LUFS", log)
    tp = re.findall(r"Peak:\s+(-?[\d.]+) dBFS", log)
    integrated = float(i_lufs[-1]) if i_lufs else None
    true_peak = float(tp[-1]) if tp else None
    blacks = [(float(a), float(b)) for a, b in re.findall(r"black_start:([\d.]+) black_end:([\d.]+)", log)]
    freezes = re.findall(r"freeze_start: ([\d.]+)", log)
    silences = re.findall(r"silence_start: ([\d.]+)", log)
    clipped = re.findall(r"Number of samples clipped: (\d+)", log)

    target = -14
    if integrated is None:
        problems.append("Não foi possível medir o loudness.")
    elif abs(integrated - target) > 2:
        warnings.append(f"Loudness integrado {integrated} LUFS (alvo {target} ±2).")
    if true_peak is not None and true_peak > -0.5:
        problems.append(f"True peak {true_peak} dBTP: risco de distorção (limite -1).")
    if clipped and any(int(c) > 0 for c in clipped):
        warnings.append(f"Amostras no limite (clipping): {', '.join(clipped)}.")
    for a, b in blacks:
        problems.append(f"Tela preta de {a:.1f}s a {b:.1f}s.")
    for s in freezes:
        warnings.append(f"Quadro parado por 3 s ou mais a partir de {float(s):.1f}s (confira se é intencional).")
    for s in silences:
        warnings.append(f"Silêncio de 4 s ou mais a partir de {float(s):.1f}s.")

    # Folha de quadros do arquivo final para a revisão visual.
    step = max(2, round(duration / 30))
    sheet = os.path.splitext(video)[0] + ".folha.jpg"
    scale = "320:180" if mode == "horizontal" else "180:320"
    tile = "6x5" if mode == "horizontal" else "10x3"
    run([ff, "-hide_banner", "-loglevel", "error", "-y", "-i", video, "-vf",
         f"fps=1/{step},scale={scale},drawtext=fontfile='C\\:/Windows/Fonts/arialbd.ttf':text='%{{pts\\:gmtime\\:0\\:%M\\\\\\:%S}}':"
         f"x=4:y=4:fontsize=16:fontcolor=yellow:box=1:boxcolor=black@0.6,tile={tile}", "-frames:v", "1", "-q:v", "3", sheet])

    status = "REPROVADO" if problems else ("REVISAO" if warnings else "APROVADO_TECNICO")
    result = {
        "status": status,
        "file": video, "duration_s": round(duration, 2), "width": w, "height": h, "fps": round(fps, 2),
        "size_mb": round(size_mb, 1), "kbps": round(kbps), "audio_streams": len(audio),
        "loudness_lufs": integrated, "true_peak_dbtp": true_peak,
        "problems": problems, "warnings": warnings, "sheet": sheet, "sheet_step_s": step,
    }
    tmp = out + ".part"
    json.dump(result, open(tmp, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    os.replace(tmp, out)
    print(status, f"{duration:.1f}s {w}x{h} {fps:.0f}fps {integrated} LUFS TP {true_peak}", problems, warnings)


if __name__ == "__main__":
    main()
