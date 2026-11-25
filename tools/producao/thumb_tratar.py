"""Tratamento de imagem do perfil BAST_THUMBNAIL_V1 (exposição, contraste, nitidez, vinheta) e medição de luminância.

Uso:
  python thumb_tratar.py <ffmpeg> fundo <entrada.png> <saida.png>   # fundo/cena: ajuste adaptativo à luminância
  python thumb_tratar.py <ffmpeg> rosto <entrada.png> <saida.png>   # recorte do rosto: realce leve, natural
  python thumb_tratar.py <ffmpeg> medir <imagem.png>                # imprime a luminância média (0..1)
"""
import json
import os
import re
import subprocess
import sys

PROFILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "perfis", "BAST_THUMBNAIL_V1.json")


def luma(ff, path):
    p = subprocess.run([ff, "-hide_banner", "-i", path, "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG", "-f", "null", "-"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    values = re.findall(r"YAVG=([\d.]+)", p.stderr)
    return float(values[-1]) / 255 if values else None


def main():
    ff, mode = sys.argv[1], sys.argv[2]
    t = json.load(open(PROFILE, encoding="utf-8"))["treatment"]
    if mode == "medir":
        print(f"{luma(ff, sys.argv[3]):.3f}")
        return

    src, out = sys.argv[3], sys.argv[4]
    before = luma(ff, src)
    target = t["target_luma"][0] if mode == "fundo" else t.get("face_target_luma", 0.28)

    # Sobe o gamma em passos até a imagem tratada chegar ao alvo de luminância (máx. 1,9: além disso fica lavada).
    gamma, after, chain = 1.0, None, []
    while True:
        lift = f"eq=gamma={gamma:.2f}" if gamma > 1.0 else None
        if mode == "fundo":
            chain = [c for c in (t["base"], lift, t["sharpen"], t["vignette"]) if c]
        else:
            chain = [c for c in (lift, t["face"]) if c]
        p = subprocess.run([ff, "-hide_banner", "-loglevel", "error", "-y", "-i", src, "-vf", ",".join(chain), out],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        if p.returncode != 0:
            raise SystemExit(p.stderr)
        after = luma(ff, out)
        if after is None or after >= target or gamma >= 1.9:
            break
        gamma = round(gamma + 0.1, 2)
    print(json.dumps({"modo": mode, "luma_antes": round(before or 0, 3), "luma_depois": round(after or 0, 3), "alvo": target,
                      "filtros": ",".join(chain)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
