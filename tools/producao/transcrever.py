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
    device = sys.argv[4] if len(sys.argv) > 4 else "auto"
    # GPU só com as bibliotecas CUDA 12 (cublas/cudnn) disponíveis para o CTranslate2; sem elas, CPU.
    if device == "auto":
        try:
            import os as _os
            import site
            for base in site.getsitepackages():
                for sub in ("nvidia/cublas/bin", "nvidia/cudnn/bin"):
                    path = _os.path.join(base, sub)
                    if _os.path.isdir(path):
                        _os.add_dll_directory(path)
                        _os.environ["PATH"] = path + _os.pathsep + _os.environ.get("PATH", "")
            import ctranslate2
            device = "cuda" if ctranslate2.get_cuda_device_count() > 0 else "cpu"
        except Exception:
            device = "cpu"
    model = (WhisperModel(model_name, device="cuda", compute_type="float16") if device == "cuda"
             else WhisperModel(model_name, device="cpu", compute_type="int8"))
    print(f"dispositivo: {device}", file=sys.stderr)

    # Lê o WAV (16 kHz mono PCM) direto: o leitor do faster-whisper depende de uma versão do PyAV
    # que nem sempre está instalada.
    import wave

    import numpy as np

    with wave.open(audio, "rb") as w:
        if w.getframerate() != 16000 or w.getnchannels() != 1 or w.getsampwidth() != 2:
            raise SystemExit("O áudio precisa ser WAV 16 kHz mono 16 bits.")
        samples = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0

    def run(m):
        segments, info = m.transcribe(
            samples,
            language="pt",
            word_timestamps=True,
            vad_filter=True,
            vad_parameters={"min_silence_duration_ms": 400},
            condition_on_previous_text=False,
        )
        res = {"language": info.language, "duration": info.duration, "device": device, "model": model_name, "segments": []}
        for seg in segments:
            res["segments"].append({
                "start": round(seg.start, 2),
                "end": round(seg.end, 2),
                "text": seg.text.strip(),
                "words": [
                    {"start": round(w.start, 2), "end": round(w.end, 2), "word": w.word.strip(), "p": round(w.probability, 2)}
                    for w in (seg.words or [])
                ],
            })
        return res

    try:
        result = run(model)
    except RuntimeError as error:
        # A placa falhou no meio (biblioteca CUDA ausente): refaz na CPU.
        if device != "cuda":
            raise
        print(f"GPU indisponível ({error}); usando a CPU.", file=sys.stderr)
        device = "cpu"
        result = run(WhisperModel(model_name, device="cpu", compute_type="int8"))
    tmp = out + ".part"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=1)
    import os
    os.replace(tmp, out)
    print(f"{len(result['segments'])} trechos de fala")


if __name__ == "__main__":
    main()
