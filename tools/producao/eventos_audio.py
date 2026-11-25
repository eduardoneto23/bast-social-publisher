"""Detecta eventos no microfone com o classificador AudioSet (AST): tosse, espirro, pigarro, fungada,
risada, grito... Janelas de 1 s a cada 0,5 s.

Uso: python eventos_audio.py <mic16.wav> <saida.json>
Saída: {"events": [{"start", "end", "label", "score"}], "loud": [...]}
"""
import json
import os
import sys
import wave

import numpy as np
import torch
from transformers import ASTFeatureExtractor, ASTForAudioClassification

MODEL = "MIT/ast-finetuned-audioset-10-10-0.4593"
WATCH = {
    "Cough": "tosse",
    "Sneeze": "espirro",
    "Throat clearing": "pigarro",
    "Sniff": "fungada",
    "Burping, eructation": "arroto",
    "Hiccup": "soluço",
    "Gasp": "suspiro",
    "Laughter": "risada",
    "Giggle": "risada",
    "Screaming": "grito",
    "Yell": "grito",
    "Shout": "grito",
}
THRESHOLD = {"tosse": 0.25, "espirro": 0.25, "pigarro": 0.25, "fungada": 0.3, "arroto": 0.3, "soluço": 0.3,
             "suspiro": 0.35, "risada": 0.3, "grito": 0.3}


def main() -> None:
    path, out = sys.argv[1], sys.argv[2]
    with wave.open(path, "rb") as w:
        sr = w.getframerate()
        x = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0

    device = "cuda" if torch.cuda.is_available() else "cpu"
    fe = ASTFeatureExtractor.from_pretrained(MODEL)
    model = ASTForAudioClassification.from_pretrained(MODEL).to(device).eval()
    labels = model.config.id2label
    watch_ids = {i: WATCH[name] for i, name in labels.items() if name in WATCH}

    win, hop = sr, sr // 2
    starts = list(range(0, max(1, len(x) - win), hop))
    raw = []
    batch = 32
    for b in range(0, len(starts), batch):
        chunk = [x[s:s + win] for s in starts[b:b + batch]]
        rms = [float(np.sqrt(np.mean(c ** 2)) + 1e-9) for c in chunk]
        feats = fe(chunk, sampling_rate=sr, return_tensors="pt").to(device)
        with torch.no_grad():
            probs = torch.sigmoid(model(**feats).logits).cpu().numpy()
        for k, s in enumerate(starts[b:b + batch]):
            for i, name in watch_ids.items():
                p = float(probs[k, i])
                if p >= THRESHOLD[name] and rms[k] > 0.005:
                    raw.append((s / sr, (s + win) / sr, name, p, 20 * np.log10(rms[k])))

    # Junta janelas seguidas do mesmo evento.
    events = []
    for start, end, name, p, db in sorted(raw):
        if events and events[-1]["label"] == name and start <= events[-1]["end"]:
            events[-1]["end"] = round(end, 2)
            events[-1]["score"] = max(events[-1]["score"], round(p, 2))
            events[-1]["db"] = max(events[-1]["db"], round(db, 1))
        else:
            events.append({"start": round(start, 2), "end": round(end, 2), "label": name, "score": round(p, 2), "db": round(db, 1)})

    tmp = out + ".part"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump({"device": device, "events": events}, f, ensure_ascii=False, indent=1)
    os.replace(tmp, out)
    counts = {}
    for e in events:
        counts[e["label"]] = counts.get(e["label"], 0) + 1
    print(device, counts)


if __name__ == "__main__":
    main()
