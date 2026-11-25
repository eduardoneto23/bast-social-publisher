"""Transcreve a faixa do microfone com faster-whisper (GPU), com tempo por palavra.

Uso: python transcrever.py <mic16.wav> <saida.json> [modelo]
Saída: {"segments": [{"start", "end", "text", "words": [{"start", "end", "word", "p"}]}]}
"""
import json
import sys

from faster_whisper import WhisperModel


def main() -> None:
    audio, out = sys.argv[1], sys.argv[2]
    model_name = sys.argv[3] if len(sys.argv) > 3 else "base"
    device = sys.argv[4] if len(sys.argv) > 4 else "cpu"
    # GPU só com as bibliotecas CUDA 12 (cublas/cudnn) instaladas para o CTranslate2; sem elas, CPU.
    model = (WhisperModel(model_name, device="cuda", compute_type="float16") if device == "cuda"
             else WhisperModel(model_name, device="cpu", compute_type="int8"))

    # Lê o WAV (16 kHz mono PCM) direto: o leitor do faster-whisper depende de uma versão do PyAV
    # que nem sempre está instalada.
    import wave

    import numpy as np

    with wave.open(audio, "rb") as w:
        if w.getframerate() != 16000 or w.getnchannels() != 1 or w.getsampwidth() != 2:
            raise SystemExit("O áudio precisa ser WAV 16 kHz mono 16 bits.")
        samples = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0

    segments, info = model.transcribe(
        samples,
        language="pt",
        word_timestamps=True,
        vad_filter=True,
        vad_parameters={"min_silence_duration_ms": 400},
        condition_on_previous_text=False,
    )
    result = {"language": info.language, "duration": info.duration, "segments": []}
    for seg in segments:
        result["segments"].append({
            "start": round(seg.start, 2),
            "end": round(seg.end, 2),
            "text": seg.text.strip(),
            "words": [
                {"start": round(w.start, 2), "end": round(w.end, 2), "word": w.word.strip(), "p": round(w.probability, 2)}
                for w in (seg.words or [])
            ],
        })
    tmp = out + ".part"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=1)
    import os
    os.replace(tmp, out)
    print(f"{len(result['segments'])} trechos de fala")


if __name__ == "__main__":
    main()
