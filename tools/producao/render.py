"""Renderiza um vídeo a partir de um plano de edição (JSON), só com ffmpeg. Nunca altera o original.

Uso: python render.py <plano.json>

Plano (todos os tempos em segundos do ORIGINAL):
{
  "ffmpeg": "caminho do ffmpeg",
  "source": "gravação original",
  "output": "arquivo final .mp4",
  "mode": "horizontal" | "vertical",
  "segments": [[ini, fim], ...],              # trechos mantidos, na ordem de montagem
  "game_track": 1, "mic_track": 2,             # índices das faixas de áudio (a:N)
  "cam": {"x": 0, "y": 0, "w": 372, "h": 268}, # área da webcam no quadro 1920x1080
  "cam_hide": [[ini, fim], ...],               # webcam fora (ausente, outra pessoa, constrangedor)
  "mic_duck": [[ini, fim], ...],               # tosse/pigarro: voz abaixada nesse instante, com fade
  "mic_gain_db": 12,                           # ganho da voz antes da compressão
  "loudness": -14,                             # LUFS integrado alvo
  "fps": 60,
  # só vertical:
  "focus_x": 0.5,                              # centro horizontal da ação (0 = esquerda, 1 = direita)
  "hook": "FRASE DO GANCHO",                   # texto na tela nos primeiros segundos
  "captions": [{"start": s, "end": s, "text": "..."}],  # tempos do ORIGINAL; viram tempos do corte
  "fonts_dir": "C:/Windows/Fonts"
}

Áudio: voz com passa-alta, compressão moderada e limitador; jogo com ducking (sidechain da voz);
mixagem normalizada (loudnorm em duas passadas) a -14 LUFS e -1 dBTP.
"""
import json
import os
import re
import subprocess
import sys


def overlaps(a, b, c, d):
    return max(a, c) < min(b, d)


def split_by(segments, intervals):
    """Quebra os trechos nas bordas dos intervalos; devolve (ini, fim, dentro_do_intervalo)."""
    out = []
    for s, e in segments:
        cuts = sorted({s, e, *[p for i in intervals for p in i if s < p < e]})
        for a, b in zip(cuts, cuts[1:]):
            if b - a < 0.05:
                continue
            inside = any(overlaps(a, b, i0, i1) for i0, i1 in intervals)
            out.append((a, b, inside))
    return out


def ts(t):
    h, rem = divmod(t, 3600)
    m, s = divmod(rem, 60)
    return f"{int(h)}:{int(m):02d}:{s:05.2f}"


def ass_escape(text):
    return text.replace("\\", "").replace("{", "(").replace("}", ")")


def build_ass(plan, pieces, path):
    """Legendas palavra a palavra (2-4 palavras por linha) + gancho, nos tempos do corte."""
    # Mapa original -> tempo do corte.
    offsets, acc = [], 0.0
    for a, b, _ in pieces:
        offsets.append((a, b, acc))
        acc += b - a

    def to_cut(t):
        for a, b, off in offsets:
            if a <= t <= b:
                return off + (t - a)
        return None

    p = load_profile(plan)
    H = p["canvas"]["h"]
    cap, hk = p["captions"], p["hook"]
    # Legenda: base na parte de baixo da área da gameplay (acima da faixa preta); gancho no topo dessa área.
    cap_margin = H - cap["baseline_y"]
    if cap["baseline_y"] > p["safe_bottom"]["y"]:
        raise SystemExit("O perfil põe a legenda dentro da faixa preta reservada; corrija captions.baseline_y.")
    lines = [
        "[Script Info]", "ScriptType: v4.00+", f"PlayResX: {p['canvas']['w']}", f"PlayResY: {H}", "WrapStyle: 0", "",
        "[V4+ Styles]",
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding",
        f"Style: Cap,{cap['font']},{cap['size']},&H00FFFFFF,&H0059DEFF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,{cap['outline']},2,2,70,70,{cap_margin},1",
        f"Style: Hook,{hk['font']},{hk['size']},&H0059DEFF,&H00FFFFFF,&H00000000,&H96000000,0,0,0,0,100,100,0,0,3,4,0,8,70,70,{hk['top_y']},1",
        "", "[Events]", "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text",
    ]
    if plan.get("hook"):
        # Gancho em amarelo; a palavra marcada com *asterisco* fica branca para destacar.
        hook = re.sub(r"\*(.+?)\*", r"{\\c&H00FFFFFF&}\1{\\c&H0059DEFF&}", ass_escape(plan["hook"]))
        lines.append(f"Dialogue: 1,{ts(0)},{ts(min(hk.get('seconds', 3.0), acc))},Hook,,0,0,0,,{hook}")
    for cap in plan.get("captions", []):
        s, e = to_cut(cap["start"]), to_cut(cap["end"])
        if s is None or e is None or e <= s:
            continue
        text = ass_escape(cap["text"]).upper()
        # Destaque amarelo para palavras marcadas com *asterisco*.
        text = re.sub(r"\*(.+?)\*", r"{\\c&H0059DEFF&}\1{\\c&H00FFFFFF&}", text)
        lines.append(f"Dialogue: 0,{ts(s)},{ts(e)},Cap,,0,0,0,,{text}")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def filter_path(p):
    return p.replace("\\", "/").replace(":", "\\:")


PROFILES_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "perfis")


def load_profile(plan):
    """Perfil de composição (padrão BAST_VERTICAL_V1). O plano pode sobrescrever chaves com "profile_overrides"."""
    name = plan.get("profile", "BAST_VERTICAL_V1")
    with open(os.path.join(PROFILES_DIR, name + ".json"), encoding="utf-8") as fh:
        profile = json.load(fh)
    for key, value in plan.get("profile_overrides", {}).items():
        if isinstance(value, dict) and isinstance(profile.get(key), dict):
            profile[key].update(value)
        else:
            profile[key] = value
    return profile


def focus_expr(plan, a, b, crop_w, x_min, x_max):
    """
    Posição x do recorte da gameplay acompanhando a ação. "focus" = [[segundo do original, x 0..1], ...]
    (interpolado em linha reta); sem keyframes, "focus_x" fixo. Sempre dentro de [x_min, x_max].
    """
    keys = sorted(plan.get("focus") or [[a, plan.get("focus_x", 0.5)]])

    def px(xn):
        return xn * 1920 - crop_w / 2

    local = [(t - a, px(x)) for t, x in keys]
    if len(local) == 1:
        expr = f"{local[0][1]:.1f}"
    else:
        expr = f"{local[-1][1]:.1f}"
        for (t0, x0), (t1, x1) in reversed(list(zip(local, local[1:]))):
            seg = f"{x0:.1f}+({x1 - x0:.1f})*(t-{t0:.3f})/{max(t1 - t0, 0.001):.3f}"
            expr = f"if(lt(t,{t1:.3f}),{seg},{expr})"
        expr = f"if(lt(t,{local[0][0]:.3f}),{local[0][1]:.1f},{expr})"
    return f"max({x_min},min({x_max},{expr}))"


def vertical_chain(plan, i, a, b, hide, cam, fps):
    """
    Composição BAST_VERTICAL_V1 (1080x1920): webcam pequena no topo (só com o criador presente), gameplay no
    centro como protagonista, faixa preta embaixo sem nada desenhado (zona da interface das redes).
    """
    p = load_profile(plan)
    W, H = p["canvas"]["w"], p["canvas"]["h"]
    game_y, game_h = p["gameplay"]["y"], p["gameplay"]["h"]
    safe_y = p["safe_bottom"]["y"]
    if hide and p.get("absent_webcam") == "expand":
        game_y, game_h = 0, safe_y  # sem webcam: a gameplay sobe até o topo

    # Recorte da gameplay no quadro 1920x1080 com a proporção da área, sem passar da ampliação máxima.
    crop_h = 1080
    crop_w = int(round(crop_h * W / game_h / 2)) * 2
    if W / crop_w > p.get("max_upscale", 1.5) + 1e-6:
        raise SystemExit(f"Ampliação de {W / crop_w:.2f}x passa do limite do perfil; use outra composição.")
    x_min = cam["x"] + cam["w"] if p.get("exclude_source_cam") else 0
    x_max = 1920 - crop_w
    if x_min > x_max:
        x_min = x_max
    xexpr = focus_expr(plan, a, b, crop_w, x_min, x_max)

    out = [f"[vs{i}]null,split=2[ga{i}][ca{i}]",
           f"color=c=black:s={W}x{H}:r={fps}:d={b - a:.3f}[bg{i}]",
           f"[ga{i}]crop={crop_w}:{crop_h}:'{xexpr}':0,scale={W}:{game_h}:flags=lanczos,setsar=1[game{i}]",
           f"[bg{i}][game{i}]overlay=0:{game_y}:shortest=1[t{i}]"]
    if hide:
        out.append(f"[ca{i}]nullsink")
        out.append(f"[t{i}]setsar=1,fps={fps}[v{i}]")
    else:
        win = p["webcam_window"]
        border = win.get("border", 0)
        inner_h = win["h"] - 2 * border
        inner_w = int(round(inner_h * cam["w"] / cam["h"] / 2)) * 2
        out.append(f"[ca{i}]crop={cam['w']}:{cam['h']}:{cam['x']}:{cam['y']},scale={inner_w}:{inner_h}:flags=lanczos,"
                   f"pad={inner_w + 2 * border}:{inner_h + 2 * border}:{border}:{border}:color={win.get('border_color', 'black')}[cam{i}]")
        out.append(f"[t{i}][cam{i}]overlay=(W-w)/2:{win['top']},setsar=1,fps={fps}[v{i}]")
    return out


def inputs_for(plan, pieces):
    """Uma entrada por trecho (-ss/-to no original): nada fica guardado na memória esperando a montagem."""
    args = []
    for a, b, _ in pieces:
        args += ["-ss", f"{a:.3f}", "-to", f"{b:.3f}", "-i", plan["source"]]
    return args


def build(plan, measured=None):
    mode = plan["mode"]
    cam = plan.get("cam", {"x": 0, "y": 0, "w": 372, "h": 268})
    fps = plan.get("fps", 60)
    pieces = split_by(plan["segments"], plan.get("cam_hide", []))
    game, mic = plan.get("game_track", 1), plan.get("mic_track", 2)
    gain = plan.get("mic_gain_db", 12)
    duck = plan.get("mic_duck", [])

    f = []
    n = len(pieces)
    concat_in = []
    for i, (a, b, hide) in enumerate(pieces):
        # Tosse/pigarro: voz abaixada nesse instante (tempo local do trecho = original - início do trecho).
        local = [(max(d0 - a, 0), d1 - a) for d0, d1 in duck if overlaps(a, b, d0, d1)]
        duck_expr = "+".join(f"between(t,{x:.2f},{y:.2f})" for x, y in local) or "0"
        f.append(f"[{i}:a:{mic}]aresample=48000,pan=mono|c0=0.5*c0+0.5*c1,highpass=f=80,"
                 f"volume='if({duck_expr},0.06,1)':eval=frame,volume={gain}dB,"
                 f"acompressor=threshold=-20dB:ratio=3:attack=10:release=150:makeup=1,"
                 f"alimiter=limit=0.9:level=disabled,asetpts=PTS-STARTPTS[m{i}]")
        f.append(f"[{i}:a:{game}]aresample=48000,asetpts=PTS-STARTPTS[g{i}]")
        trim = "setpts=PTS-STARTPTS"
        f.append(f"[{i}:v]{trim}[vs{i}]")
        trim = "null"
        if mode == "horizontal":
            if hide:
                # Webcam fora: amplia a gameplay só na área sem a webcam (16:9, sem barras, sem esticar).
                ch = 1080 - cam["h"]
                cw = int(ch * 16 / 9) // 2 * 2
                cx = 1920 - cw
                v = f"[vs{i}]{trim},crop={cw}:{ch}:{cx}:{cam['h']},scale=1920:1080:flags=lanczos,setsar=1,fps={fps}[v{i}]"
            else:
                v = f"[vs{i}]{trim},scale=1920:1080,setsar=1,fps={fps}[v{i}]"
            f.append(v)
        else:
            f.extend(vertical_chain(plan, i, a, b, hide, cam, fps))
        concat_in.append(f"[v{i}][m{i}][g{i}]")

    f.append("".join(concat_in) + f"concat=n={n}:v=1:a=2[vcat][mcat][gcat]")
    # Fades curtos nas emendas já vêm do corte limpo; o jogo abaixa quando a voz fala (ducking).
    f.append("[mcat]asplit=2[mkey][mmix]")
    f.append("[gcat][mkey]sidechaincompress=threshold=0.02:ratio=8:attack=20:release=400:makeup=1[gduck]")
    f.append("[mmix][gduck]amix=inputs=2:weights=1 0.8:normalize=0,afade=t=in:d=0.15[mixed]")

    target = plan.get("loudness", -14)
    if measured:
        f.append(f"[mixed]loudnorm=I={target}:TP=-1:LRA=11:measured_I={measured['input_i']}:measured_TP={measured['input_tp']}:"
                 f"measured_LRA={measured['input_lra']}:measured_thresh={measured['input_thresh']}:offset={measured['target_offset']}:"
                 f"linear=true,aresample=48000[aout]")
    else:
        f.append(f"[mixed]loudnorm=I={target}:TP=-1:LRA=11:print_format=json[aout]")

    vout = "[vcat]"
    if measured is None:
        # 1ª passada só mede o áudio: o vídeo é descartado.
        f.append("[vcat]nullsink")
        return ";".join(f), None, pieces
    if mode == "vertical" and (plan.get("captions") or plan.get("hook")):
        ass = os.path.splitext(plan["output"])[0] + ".ass"
        build_ass(plan, pieces, ass)
        fonts = plan.get("fonts_dir", "C:/Windows/Fonts")
        f.append(f"[vcat]subtitles=filename='{filter_path(ass)}':fontsdir='{filter_path(fonts)}'[vsub]")
        vout = "[vsub]"
    return ";".join(f), vout, pieces


def run(cmd):
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if p.returncode != 0:
        raise SystemExit("ffmpeg falhou:\n" + p.stderr[-3000:])
    return p


def preview(plan, second, out_png):
    """
    Prévia da composição antes do render: um quadro real no segundo pedido com o layout do perfil, gancho e
    uma legenda de exemplo; e uma cópia com as regiões desenhadas (webcam, gameplay, faixa reservada).
    """
    p = load_profile(plan)
    pieces = split_by([[second, second + 0.5]], plan.get("cam_hide", []))
    a, b, hide = pieces[0]
    cam = plan.get("cam", {"x": 0, "y": 0, "w": 372, "h": 268})
    f = ["[0:v]setpts=PTS-STARTPTS[vs0]", *vertical_chain(plan, 0, a, b, hide, cam, plan.get("fps", 60))]
    sample = dict(plan)
    sample["captions"] = [{"start": a, "end": b, "text": "LEGENDA DE *EXEMPLO*"}]
    ass = os.path.splitext(out_png)[0] + ".ass"
    build_ass(sample, pieces, ass)
    f.append(f"[v0]subtitles=filename='{filter_path(ass)}':fontsdir='{filter_path(plan.get('fonts_dir', 'C:/Windows/Fonts'))}'[comp]")
    graph = ";".join(f)
    run([plan["ffmpeg"], "-hide_banner", "-loglevel", "error", "-y", "-ss", f"{a:.3f}", "-to", f"{b:.3f}", "-i", plan["source"],
         "-filter_complex", graph, "-map", "[comp]", "-frames:v", "1", out_png])

    W = p["canvas"]["w"]
    gy, gh = (0, p["safe_bottom"]["y"]) if hide and p.get("absent_webcam") == "expand" else (p["gameplay"]["y"], p["gameplay"]["h"])
    guides = os.path.splitext(out_png)[0] + "-guias.png"
    boxes = [
        f"drawbox=x=0:y={p['webcam_band']['y']}:w={W}:h={p['webcam_band']['h']}:color=cyan@0.9:t=6",
        f"drawbox=x=0:y={gy}:w={W}:h={gh}:color=lime@0.9:t=6",
        f"drawbox=x=0:y={p['safe_bottom']['y']}:w={W}:h={p['safe_bottom']['h']}:color=red@0.9:t=6",
    ]
    run([plan["ffmpeg"], "-hide_banner", "-loglevel", "error", "-y", "-i", out_png, "-vf", ",".join(boxes), guides])
    os.remove(ass)
    print(f"prévia: {out_png} | guias: {guides} | webcam {'oculta' if hide else 'visível'}")


def main():
    plan = json.load(open(sys.argv[1], encoding="utf-8"))
    if len(sys.argv) > 3 and sys.argv[2] == "--previa":
        preview(plan, float(sys.argv[3]), sys.argv[4] if len(sys.argv) > 4 else os.path.splitext(plan["output"])[0] + ".previa.png")
        return
    ff = plan["ffmpeg"]
    out = plan["output"]
    os.makedirs(os.path.dirname(out), exist_ok=True)

    # 1ª passada: só mede o loudness da mixagem.
    graph, vout, pieces = build(plan)
    graph_file = out + ".filtro1.txt"
    open(graph_file, "w", encoding="utf-8").write(graph)
    p = run([ff, "-hide_banner", "-nostats", *inputs_for(plan, pieces), "-/filter_complex", graph_file,
             "-map", "[aout]", "-f", "null", "-"])
    m = re.search(r"\{[^{}]*\"input_i\"[^{}]*\}", p.stderr, re.S)
    measured = json.loads(m.group(0))

    # 2ª passada: render final com o loudness corrigido.
    graph, vout, pieces = build(plan, measured)
    open(graph_file, "w", encoding="utf-8").write(graph)
    partial = out + ".part.mp4"
    run([ff, "-hide_banner", "-nostats", "-y", *inputs_for(plan, pieces), "-/filter_complex", graph_file,
         "-map", vout, "-map", "[aout]",
         "-c:v", "h264_nvenc", "-preset", "p6", "-tune", "hq", "-rc", "vbr", "-cq", "19", "-b:v", "0",
         "-profile:v", "high", "-pix_fmt", "yuv420p",
         "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", partial])
    os.replace(partial, out)
    os.remove(graph_file)

    edl = {"source": plan["source"], "pieces": [{"inicio": a, "fim": b, "webcam_oculta": h} for a, b, h in pieces],
           "duracao_s": round(sum(b - a for a, b, _ in pieces), 2), "loudness_medido": measured}
    json.dump(edl, open(os.path.splitext(out)[0] + ".edl.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print(f"ok: {out} ({edl['duracao_s']} s, {len(pieces)} trechos)")


if __name__ == "__main__":
    main()
