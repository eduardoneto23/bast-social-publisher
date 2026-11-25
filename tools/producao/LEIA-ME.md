# Ferramentas de produção (gravação do OBS → vídeos)

Scripts usados pelas etapas de produção do BAST (fases 3 a 7). Só leem a gravação original; tudo é gravado na
pasta de trabalho do job (disco de produção). Python: o ambiente com faster-whisper/torch/transformers.

| Script | O que faz |
|---|---|
| `transcrever.py` | Fala do microfone com tempo por palavra (faster-whisper). Sem as bibliotecas CUDA 12 do CTranslate2, roda na CPU. |
| `eventos_audio.py` | Tosse, espirro, pigarro, fungada, risada, grito no microfone (classificador AudioSet, GPU). |
| `presenca_camera.py` | Rosto na webcam segundo a segundo; ausências de 3 s ou mais (confira visualmente: perfil engana). |
| `render.py` | Renderiza a partir de um plano JSON: cortes, webcam oculta, voz abaixada nas tosses, ducking do jogo, -14 LUFS / -1 dBTP, NVENC. `--previa <segundo>` gera a prévia da composição vertical (com guias) antes do render. |
| `qa.py` | Revisão técnica independente do arquivo final: abre, resolução, FPS, loudness, true peak, clipping, tela preta, congelamento, silêncio; folha de quadros para a revisão visual. |
| `thumb_tratar.py` | Tratamento da thumbnail (contraste, nitidez, vinheta leve, gamma até a luminância alvo) e medição de luminância. |

## Perfis (`perfis/`)

- **BAST_VERTICAL_V1** – vertical 1080x1920: webcam pequena no topo (15%, centralizada, borda amarela, só com o criador
  presente), respiro (3%), gameplay no centro (57%, recorte acompanhando a ação, sem passar de 1,5x de ampliação),
  faixa preta embaixo (25%) sem nada desenhado. Legenda na base da gameplay; gancho no topo dela. Sem o criador na
  câmera, a janela some e a gameplay ocupa a faixa de cima. Padrão do `render.py` (`"profile"` no plano troca;
  `"profile_overrides"` ajusta chaves).
- **BAST_THUMBNAIL_V1** – thumbnail 1280x720: uma ideia; elemento principal com 40-62% da largura; rosto real com
  30-45% da altura (até 55% no conceito de reação), no canto oposto ao texto; texto de 2 a 5 palavras com uma em
  amarelo (#ffde59), sem dominar a imagem; tratamento de exposição/contraste/nitidez; até 3 conceitos (acontecimento,
  reação, curiosidade) e validação em 246x138 e 168x94. Montagem no Canva sobre o modelo "BAST - Modelo Thumbnail".
