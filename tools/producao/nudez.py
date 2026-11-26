"""Varre os quadros (1 por segundo) com o NudeNet e marca os segundos com exposição explícita.

Uso: python nudez.py <pasta com f%04d.jpg> <saida.json>
Saída: {"flags": [{"start", "end", "classes", "score"}], "seconds": {"t": [[classe, score], ...]}}
Gráficos de jogo enganam o detector nos dois sentidos: tudo o que ele marcar vai para a revisão visual, e a
ausência de marcas não dispensa a revisão das folhas de quadros.
"""
import glob
import json
import os
import sys

from nudenet import NudeDetector

EXPLICIT = {
    "FEMALE_BREAST_EXPOSED", "FEMALE_GENITALIA_EXPOSED", "MALE_GENITALIA_EXPOSED",
    "BUTTOCKS_EXPOSED", "ANUS_EXPOSED",
}
THRESHOLD = 0.45


def main():
    folder, out = sys.argv[1], sys.argv[2]
    detector = NudeDetector()
    frames = sorted(glob.glob(os.path.join(folder, "f*.jpg")))
    seconds = {}
    for i, path in enumerate(frames):
        hits = [(d["class"], round(float(d["score"]), 2)) for d in detector.detect(path)
                if d["class"] in EXPLICIT and d["score"] >= THRESHOLD]
        if hits:
            seconds[i] = hits

    flags = []
    for t in sorted(seconds):
        classes = sorted({c for c, _ in seconds[t]})
        score = max(s for _, s in seconds[t])
        if flags and t - flags[-1]["end"] <= 2:
            flags[-1]["end"] = t + 1
            flags[-1]["classes"] = sorted(set(flags[-1]["classes"]) | set(classes))
            flags[-1]["score"] = max(flags[-1]["score"], score)
        else:
            flags.append({"start": t, "end": t + 1, "classes": classes, "score": score})

    tmp = out + ".part"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump({"threshold": THRESHOLD, "frames": len(frames), "flags": flags,
                   "seconds": {str(k): v for k, v in seconds.items()}}, f, ensure_ascii=False)
    os.replace(tmp, out)
    print(f"{len(frames)} quadros; {len(flags)} trecho(s) marcado(s)")


if __name__ == "__main__":
    main()
