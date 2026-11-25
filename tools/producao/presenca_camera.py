"""Presença do criador na webcam, segundo a segundo, por detecção de rosto (OpenCV, frontal + perfil).

Uso: python presenca_camera.py <pasta com cam%04d.jpg> <saida.json>
Saída: {"seconds": [{"t", "face", "area"}], "absent": [{"start", "end"}]}
Ausência = 3 s ou mais seguidos sem rosto (um segundo isolado sem rosto é virar a cabeça, não sair).
"""
import glob
import json
import os
import sys

import cv2


def main() -> None:
    folder, out = sys.argv[1], sys.argv[2]
    base = cv2.data.haarcascades
    frontal = cv2.CascadeClassifier(os.path.join(base, "haarcascade_frontalface_default.xml"))
    profile = cv2.CascadeClassifier(os.path.join(base, "haarcascade_profileface.xml"))

    seconds = []
    for i, path in enumerate(sorted(glob.glob(os.path.join(folder, "cam*.jpg")))):
        img = cv2.imread(path)
        gray = cv2.equalizeHist(cv2.cvtColor(img, cv2.COLOR_BGR2GRAY))
        found = []
        for det in (frontal, profile):
            found += list(det.detectMultiScale(gray, scaleFactor=1.1, minNeighbors=5, minSize=(50, 50)))
        if not found:
            flipped = cv2.flip(gray, 1)
            found += list(profile.detectMultiScale(flipped, scaleFactor=1.1, minNeighbors=5, minSize=(50, 50)))
        area = max((w * h for (_, _, w, h) in found), default=0)
        seconds.append({"t": i, "face": bool(found), "area": int(area)})

    absent, run = [], None
    for s in seconds:
        if not s["face"]:
            run = run if run is not None else s["t"]
        else:
            if run is not None and s["t"] - run >= 3:
                absent.append({"start": run, "end": s["t"]})
            run = None
    if run is not None and len(seconds) - run >= 3:
        absent.append({"start": run, "end": len(seconds)})

    tmp = out + ".part"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump({"seconds": seconds, "absent": absent}, f)
    os.replace(tmp, out)
    present = sum(1 for s in seconds if s["face"])
    print(f"rosto em {present}/{len(seconds)} s; ausências de 3 s ou mais: {absent}")


if __name__ == "__main__":
    main()
