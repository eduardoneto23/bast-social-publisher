# Thumbnail automática no Canva (vídeos horizontais do YouTube)

Automação **fora do serviço BAST**: uma tarefa agendada do Claude (app Claude, aba Code) olha a pasta `00 - THUMB`, cria a thumbnail no estilo do seu modelo do Canva, grava `thumbnail.png` na pasta do vídeo e só então move a pasta inteira para `01 - ENVIAR YOUTUBE`. O BAST não olha `00 - THUMB`, então nada é publicado enquanto a thumbnail está sendo feita.

```
00 - THUMB/             você coloca aqui: vídeo + legenda.txt
   (thumbnail criada)
01 - ENVIAR YOUTUBE/    a pasta chega aqui já com thumbnail.png e o BAST agenda
```

## Como funciona

1. `pendentes.ps1` acha as pastas com um vídeo e um .txt, sem imagem, paradas há pelo menos 60 s (cópia terminada).
2. `quadros.ps1` tira 12 quadros do vídeo e monta uma folha de contato; o Claude escolhe o melhor.
3. `fundo.ps1` prepara o quadro em 1280x720 com a faixa escura embaixo, igual ao modelo.
4. O Claude envia o fundo ao Canva (`enviar-canva.ps1`), copia o design "BAST - Modelo Thumbnail", troca a imagem e a frase (parte branca + palavra amarela), confere a prévia e exporta em PNG.
5. `salvar-thumb.ps1` confere o PNG (1280x720, até 2 MB, o limite do YouTube) e grava `thumbnail.png` na pasta.
6. `mover-para-youtube.ps1` move a pasta completa para `01 - ENVIAR YOUTUBE` de uma vez (nome repetido ganha " (2)").

O modelo original nunca é alterado: cada vídeo ganha uma cópia chamada "BAST thumb - <nome da pasta>" no Canva, que dá para ajustar à mão depois.

## Legenda automática

Se a pasta tiver só o vídeo, o nome do arquivo vira o título de trabalho (ex.: `gta vi o inicio.mp4`; se o nome for genérico, como `video.mp4`, vale o nome da pasta) e a tarefa cria o `legenda.txt`:

- **Título:** não é resumo, é o motivo para clicar. A tarefa pesquisa títulos reais do YouTube sobre o jogo e o momento, cria 5 opções nesse estilo e escolhe pelo teste do fã ("um fã de GTA clicaria nisso?"). Formato curto: jogo + gancho de uma coisa só (ex.: "GTA 6 – Invadi o PÁTIO DA POLÍCIA"), 25 a 55 caracteres. Listas de assuntos, frases vazias de impacto ("que ninguém esperava", "CHOCANTE"), título todo em maiúsculas e promessas que o vídeo não cumpre são proibidos.
- **Descrição:** 1 ou 2 frases objetivas.
- **Hashtags:** exatamente 5, todas do jogo e do vídeo. As 3 primeiras (mostradas pelo YouTube acima do título) são os termos mais fortes do jogo e as 2 últimas as mais específicas do vídeo, com uma pesquisa rápida do que está em alta.
- **Validação:** `salvar-legenda.ps1` confere tudo isso antes de gravar e nunca sobrescreve um `.txt` que você colocou.
- **Sem revisão:** a legenda vai para o ar sem revisão. Se quiser controlar o texto, coloque o seu `legenda.txt` na pasta.

## Imagem de referência

Para escolher você mesmo o momento da thumbnail, coloque na pasta uma imagem chamada `referencia.png` (ou `.jpg`). Pode ser um print do vídeo, mesmo pequeno ou comprimido.

- **Como funciona:** `achar-referencia.ps1` procura esse momento no vídeo e tira o quadro em qualidade cheia.
- **Precisão:** no teste, um print de 640x360 em baixa qualidade foi achado no segundo exato de um vídeo de 6 minutos, em cerca de 20 s.
- **Se não achar:** quando a imagem não é do vídeo, ou foi recortada ou editada, a tarefa usa a própria imagem.
- **Webcam:** é coberta do mesmo jeito.
- **A referência continua na pasta:** ela vai junto para `01 - ENVIAR YOUTUBE`, e o BAST usa a `thumbnail.png`. Se a pasta tiver a referência e uma thumbnail sua, a sua precisa se chamar `thumbnail.png` ou `thumbnail.jpg`.

## Se der errado

- Quando uma pasta não dá certo, ela fica em `00 - THUMB` com um `THUMB-ERRO.txt` explicando o motivo e não é tentada de novo. Corrija e apague esse arquivo para tentar outra vez.
- Para usar uma thumbnail feita por você, coloque a imagem na pasta: ela é só movida para `01 - ENVIAR YOUTUBE`, sem passar pelo Canva. Também dá para colocar a pasta completa direto em `01 - ENVIAR YOUTUBE`.
- A tarefa só roda com o app do Claude aberto. Se ele estiver fechado, roda quando abrir.

## Criar a tarefa

A instrução completa está em [tarefa-agendada.md](tarefa-agendada.md). No app do Claude, crie uma tarefa agendada com esse texto (trocando `<PASTA PRINCIPAL>` e `<PROJETO>` pelos seus caminhos), a cada 15 minutos (`*/15 * * * *`). Os scripts acham a pasta principal sozinhos pelo `appsettings.Local.json` do serviço.
