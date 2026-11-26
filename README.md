# BAST SOCIAL PUBLISHER

Publicação automática de vídeos nas redes sociais, rodando no seu próprio computador com Windows.

Você prepara o vídeo, escreve a legenda em um arquivo de texto, coloca tudo em uma pasta e o programa agenda e publica sozinho, mesmo com a janela fechada.

## Situação atual

| Rede | Situação |
|---|---|
| YouTube | Funcionando (vídeos e Shorts), pela API oficial |
| Instagram | Funcionando (Reels), pela API oficial. Ver [docs/CONECTAR-META.md](docs/CONECTAR-META.md) |
| Facebook | Funcionando (Reels de Página), pela API oficial |
| Threads | Implementado e testado com a API real (contêiner aceito). O vídeo vai por um link temporário do Cloudflare, aberto só durante o envio; precisa do `cloudflared`. Ver [docs/CONECTAR-THREADS.md](docs/CONECTAR-THREADS.md) |
| TikTok | Modo assistido: a API oficial não permite publicação pública automática para ferramenta de uso próprio, então no horário o vídeo e a legenda chegam pelo Telegram e você publica no app. Ver [docs/TIKTOK.md](docs/TIKTOK.md) |

Avisos de publicação, falha e reconexão podem chegar no seu celular por um bot do Telegram: ver [docs/CONECTAR-TELEGRAM.md](docs/CONECTAR-TELEGRAM.md).

O painel fica em http://127.0.0.1:5080/ e mostra a fila, o histórico, os problemas, as contas, a agenda e os avisos. Dá para pausar a fila, cancelar um vídeo, publicar um vídeo agendado na hora e mudar a agenda por ele. A conexão das contas continua sendo feita pelos scripts, para que senhas e tokens nunca passem pelo navegador. O andamento do projeto está em [docs/ACOMPANHAMENTO.md](docs/ACOMPANHAMENTO.md).

## O que você precisa

- Windows 10 ou 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- ffmpeg e ffprobe
- Git
- Uma conta Google com um canal do YouTube

## Passo a passo

### 1. Instalar as ferramentas

No PowerShell:

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Gyan.FFmpeg
winget install Git.Git
```

Feche e abra o PowerShell de novo. Descubra onde o ffmpeg foi instalado e guarde os dois caminhos, que serão usados no passo 3:

```powershell
(Get-Command ffmpeg).Source
(Get-Command ffprobe).Source
```

### 2. Baixar o projeto

```powershell
git clone https://github.com/eduardoneto23/bast-social-publisher.git
cd bast-social-publisher
dotnet test
```

O `dotnet test` deve terminar com todos os testes aprovados.

### 3. Configurar

Crie o arquivo `src\Bast.Service\appsettings.Local.json`. Ele fica só no seu computador e não vai para o Git.

```json
{
  "Bast": {
    "RootFolder": "D:\\BAST SOCIAL PUBLISHER",
    "FfprobePath": "C:\\caminho\\completo\\ffprobe.exe",
    "FfmpegPath": "C:\\caminho\\completo\\ffmpeg.exe"
  },
  "YouTube": {
    "PrivacyStatus": "private",
    "CategoryId": "22"
  }
}
```

- `RootFolder`: onde ficarão os vídeos e o banco de dados. Use um disco com espaço. Barras invertidas são duplicadas (`\\`).
- `FfprobePath` e `FfmpegPath`: os caminhos completos do passo 1. São obrigatórios porque o serviço do Windows não enxerga o PATH do seu usuário.
- `PrivacyStatus`: comece com `private` para testar. Depois troque para `public` ou `unlisted`.
- `CategoryId`: `22` é Pessoas e blogs, `20` é Jogos.

### 4. Instalar como serviço do Windows

Abra o PowerShell **como Administrador**, entre na pasta do projeto e rode:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\test-service.ps1
```

O script compila, instala o serviço `BastSocialPublisher`, confere se ele responde e testa o reinício automático. O resultado fica em `service-test.log`.

O serviço inicia junto com o Windows e continua rodando com a janela fechada. Confira no navegador: http://127.0.0.1:5080/api/health

Sempre que mudar o `appsettings.Local.json` ou atualizar o código, rode esse script de novo.

### 5. Conectar o YouTube

Siga [docs/CONECTAR-YOUTUBE.md](docs/CONECTAR-YOUTUBE.md). Em resumo:

1. Crie um projeto no Google Cloud, ative a YouTube Data API v3 e crie um cliente OAuth do tipo "App para computador".
2. Baixe o arquivo JSON do cliente e rode:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\connect-youtube.ps1 -ClientSecretFile "C:\caminho\client_secret_....json"
```

3. Autorize no navegador com a conta do canal.
4. Apague o arquivo JSON depois de conectar.

Em modo de teste do Google a autorização expira a cada 7 dias. Para evitar isso é preciso publicar o app OAuth, o que exige uma página inicial e uma política de privacidade públicas; há modelos na pasta [site/](site/) para você adaptar e hospedar.

### 6. Publicar o primeiro vídeo

Dentro de `RootFolder` o programa cria esta estrutura:

```
01 - ENVIAR VERTICAL/    coloque aqui as pastas dos vídeos verticais (redes marcadas na Agenda)
01 - ENVIAR YOUTUBE/     vídeos horizontais, só para o YouTube (ver abaixo)
02 - AGENDADOS/          validados, aguardando o horário
03 - PUBLICANDO/         em envio
04 - ENVIADOS/           publicados
05 - ERROS/              com falha
06 - ARQUIVADOS/         cancelados
07 - PENDENTE MANUAL/    verticais já publicados no automático, esperando você postar à mão nas outras redes
SYSTEM/                  banco de dados e credenciais criptografadas
```

**Envio manual:** com a opção "Envio manual" ligada na Agenda, o vertical que terminou o envio automático vai para `07 - PENDENTE MANUAL`. Ele aparece no card **Pendente manual** da Visão geral; depois de postar à mão nas redes que não estão no automático, clique em **Já postei** e a pasta vai para `04 - ENVIADOS`. Os horizontais vão direto para `04 - ENVIADOS`.

Monte a pasta do vídeo fora de `01 - ENVIAR VERTICAL` e mova para lá quando estiver pronta:

```
Video 001/
  video.mp4
  legenda.txt
  thumbnail.jpg     (opcional)
```

Exemplo de `legenda.txt`:

```
GTA 6 VAI TER POSTER NA VERSÃO FÍSICA!

A Rockstar confirmou que a versão física terá um pôster especial.

E você, curtiu essa novidade?

#GTA6 #Games
```

- A **primeira linha** vira o título no YouTube (até 100 caracteres). O texto inteiro vira a descrição.
- A imagem opcional (`thumbnail.jpg`, de preferência 1080x1920) vira a capa no YouTube Shorts e no Facebook. No Instagram o Reel sai com a capa automática: a capa por link temporário fica desligada (`Instagram:UseCover`), porque o Instagram muitas vezes não consegue buscar a imagem e recusa o Reel. Uma `referencia.png` nunca é usada como capa.
- Instagram: vídeo acima de 90 MB sobe como uma cópia mais leve (H.264 até 6 Mbps, 30 qps), feita em `SYSTEM\cache\compact` e apagada depois de 3 dias (`Instagram:CompactAboveMb`, 0 desliga). O arquivo original não muda.
- Vídeo em erro: o botão **Tentar de novo** refaz só as redes que falharam no envio; **Postar manual** leva o vídeo que já saiu em alguma rede para `07 - PENDENTE MANUAL`.
- A legenda nunca é alterada pelo programa.
- Vídeo vertical de até 3 minutos é publicado pelo YouTube como Short.
- As pastas são publicadas em ordem numérica: `Video 2` antes de `Video 10`.

### Vídeos horizontais (só YouTube)

Vídeos longos na horizontal vão em `01 - ENVIAR YOUTUBE`, cada um na sua pasta:

```
Video 001/
  video.mp4         (horizontal: mais largo que alto)
  legenda.txt       (mesmo padrão: 1ª linha é o título)
  thumbnail.jpg     (obrigatória; .jpg ou .png)
```

- Vão **só para o YouTube**, com a thumbnail.
- São **5 por dia**, nos horários **08:00, 11:00, 14:00, 17:00 e 20:00**, todos os dias. A lista pode ser mudada na aba Agenda.
- Cada vídeo pega o **próximo horário livre**: colocou um, ele sai no próximo horário; colocou outro, sai no seguinte, e assim por diante. Com os horários do dia ocupados, vai para o dia seguinte. Um horário nunca tem dois vídeos, e um horário que coincide com o de um vídeo vertical é pulado.
- Pasta sem thumbnail ou com vídeo vertical fica em `01 - ENVIAR YOUTUBE` e aparece em Problemas com o motivo.
- Se o computador estava desligado nas janelas, os vídeos passam para as próximas janelas livres, nunca todos de uma vez.
- O YouTube só aceita thumbnail personalizada em canal verificado (youtube.com/verify). Se ele recusar, o vídeo sai com a imagem automática e o aviso aparece no painel e no Telegram.
- Esses vídeos seguem a sua própria agenda: o modo da agenda (horários fixos, manual ou intervalo) vale só para `01 - ENVIAR VERTICAL`.
- Legenda e thumbnail podem ser criadas automaticamente no Canva antes do envio: ver [tools/thumbnail-canva/LEIA-ME.md](tools/thumbnail-canva/LEIA-ME.md) (pastas `00 - THUMB` e `00 - THUMB VERTICAL`).

### 7. Acompanhar

Abra no navegador:

| Endereço | O que mostra |
|---|---|
| http://127.0.0.1:5080/api/queue | Fila, horários e estado por rede, com erros e links |
| http://127.0.0.1:5080/api/import-issues | Pastas que não puderam ser importadas e o motivo |
| http://127.0.0.1:5080/api/accounts | Redes conectadas |
| http://127.0.0.1:5080/api/health | Situação geral do serviço |

## Agenda

Os vídeos verticais (`01 - ENVIAR VERTICAL`) recebem horário de um destes modos, escolhidos no painel, aba **Agenda**:

- **Horários fixos** (padrão): cada vídeo novo pega o próximo horário livre da lista, um por horário. O padrão é **10:00, 16:00 e 18:00**: com os 3 do dia ocupados (contando os já publicados e os marcados à mão), o vídeo vai para o dia seguinte. A lista é editável na Agenda.
- **Intervalo**: cada vídeo novo recebe o próximo horário livre da agenda de intervalo (ver abaixo).
- **Manual**: o vídeo importado fica **aguardando horário** e só é publicado quando você escolhe a data e a hora no painel, aba **Fila**. O limite padrão é de 3 vídeos por dia (no fuso da agenda), contando os já publicados naquele dia. Dois vídeos não podem ficar no mesmo minuto. Se chegar um vídeo novo, o aviso vai para o Telegram.

Em qualquer modo dá para mudar o horário de um vídeo que ainda não começou a publicar (botão **Mudar horário**), ou publicar na hora (**Publicar agora**). Pela API:

```powershell
$corpo = @{ localTime = '2026-11-20T14:30' } | ConvertTo-Json   # data e hora no fuso da agenda
Invoke-RestMethod http://127.0.0.1:5080/api/videos/ID-DO-VIDEO/time -Method Post -ContentType 'application/json' -Body $corpo
```

No modo manual, se o computador estiver desligado no horário escolhido e passar da tolerância de atraso, o vídeo **não** é publicado depois: ele volta a aguardar horário e você recebe um aviso.

### Mudar a agenda pela API

No modo de intervalo, o padrão é publicar às 10:00, 14:00, 18:00 e 22:00 (início às 10:00, intervalo de 4 horas), todos os dias, no fuso de São Paulo. Para mudar:

```powershell
$agenda = @{
  startTime = '09:00'; endTime = '23:59'; intervalMinutes = 180; maxPerDay = 5
  days = @('Monday','Tuesday','Wednesday','Thursday','Friday')
  timeZoneId = 'America/Sao_Paulo'
  platforms = @('YouTube')
  missedPolicy = 'NextAllowedSlot'; missedGraceMinutes = 30; maxAttempts = 5
  mode = 'FixedTimes'; manualMaxPerDay = 3   # opcionais: sem eles, o que está salvo é mantido
  verticalSlots = '10:00, 16:00, 18:00'; horizontalSlots = '08:00, 11:00, 14:00, 17:00, 20:00'
} | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:5080/api/settings/schedule -Method Put -ContentType 'application/json' -Body $agenda
```

- `missedPolicy`: o que fazer se o computador estava desligado no horário. `NextAllowedSlot` publica no próximo horário; `NextCycle` espera o dia seguinte. Vídeos atrasados nunca são publicados todos de uma vez.
- Os vídeos já agendados mantêm o horário; a nova agenda vale para os próximos.
- Rede desmarcada em `platforms` não recebe mais nada: as publicações dela que ainda não começaram são canceladas no ciclo seguinte (as que já estão em envio terminam).
- `manualPosting` (opcional): `true` deixa os verticais publicados em `07 - PENDENTE MANUAL` até a confirmação no painel.

## Controles

```powershell
# Pausar e retomar a fila
Invoke-RestMethod http://127.0.0.1:5080/api/queue/pause -Method Post
Invoke-RestMethod http://127.0.0.1:5080/api/queue/resume -Method Post

# Cancelar um vídeo ainda não publicado (o id aparece em /api/queue)
Invoke-RestMethod http://127.0.0.1:5080/api/videos/ID-DO-VIDEO/cancel -Method Post

# Publicar agora um vídeo agendado, sem esperar o horário
Invoke-RestMethod http://127.0.0.1:5080/api/videos/ID-DO-VIDEO/publish-now -Method Post
```

Os mesmos controles estão no painel, na aba Fila.

## Produção (gravações do OBS viram vídeos)

Em implementação por fases. **Hoje (fases 2 e 3):** recebimento das gravações, jobs, etapas com retomada, parada de emergência, aba **Produção** no painel e a **análise automática** (etapa `analise-midia`): faixas de jogo e microfone separadas, quadros e folhas de contato, recorte da webcam, transcrição (Whisper na placa de vídeo), tosse/pigarro/risada/grito, presença na câmera, conteúdo explícito (NudeNet), tela preta, congelamento, trocas de cena e picos de voz, tudo resumido em `1 - analise\analise.json` com alertas. Os detectores apontam suspeitas; a revisão visual vem nas próximas fases. A seleção de momentos, a edição, as thumbnails e a revisão chegam nas fases 4 a 7; até lá o job para, sem simular nada, na primeira etapa que ainda não existe.
- **Fase 4 – seleção e edição horizontal:** a etapa `selecao` é feita pelo Claude (tarefa agendada "BAST – Produção", prompt em `tools\producao\tarefa-producao.md`, a cada 15 min; precisa do app do Claude aberto). Ele lê a análise inteira, grava `1 - analise\plano_edicao.json` (teaser, trechos, webcam oculta, voz abaixada, cenas sensíveis, verticais, thumbnail, título, descrição, hashtags) e entrega pela API (`GET /api/production/claude`, `POST /api/production/jobs/{id}/steps/selecao/complete`). O BAST confere o plano: tempos dentro da gravação, mínimo de 60 s, e cena sensível dentro do que será usado sem corte para o job em **Precisa de revisão humana** (o blur automático ainda não existe); o botão **Revisei, continuar** libera. Depois, a etapa `edicao-horizontal` renderiza sozinha (ffmpeg/NVENC: teaser + trechos, webcam oculta com zoom, voz abaixada nas tosses, ducking do jogo, -14 LUFS/-1 dBTP) e grava em `2 - horizontal` o MP4, a EDL e o `.txt` com título/descrição/hashtags, e em `5 - projetos` o projeto do DaVinci (FCPXML 1.10, cortes limpos apontando para o original, marcadores onde a webcam foi oculta). A importação do FCPXML no DaVinci ainda não foi testada.
- **Ambiente da análise:** `scripts\instalar-producao.ps1` cria `SYSTEM\tools\python-producao` (torch com CUDA, faster-whisper, transformers, OpenCV 4, NudeNet) e baixa os modelos para `SYSTEM\tools\modelos`; os jobs rodam sem internet. Opções: `ProductionGameTrack`/`ProductionMicTrack` (faixas do OBS, padrão 1 e 2), `ProductionWebcam` (área da webcam, padrão `0,0,372,268`), `ProductionWhisperModel` (padrão `small`). Os scripts vão junto com o serviço na pasta `producao`.

- **Pasta do OBS:** `00 - GRAVACOES` (configure o caminho de gravação do OBS para ela). O BAST só lê: o original nunca é movido, renomeado, alterado ou apagado.
- **Gravação pronta** quando, ao mesmo tempo: o nome não é de Replay Buffer nem captura (`Bast:ProductionIgnorePatterns`, padrão `^Replay` e `^Screenshot`); o OBS largou o arquivo; tamanho e data não mudam há 2 min (`ProductionStableSeconds`); o ffprobe lê vídeo e áudio; o final decodifica sem erro; e dura mais de 5 min (`ProductionMinDurationSeconds`, as mais curtas são testes). A mesma gravação nunca gera dois jobs.
- **Autorização:** cada job só roda depois do botão **Autorizar**, que vale só para as etapas daquele arquivo (nunca publica nada). Depois disso ele roda sozinho, inclusive de madrugada, se o serviço e o computador estiverem ligados.
- **Disco de trabalho:** `Bast:ProductionWorkFolder` (ex.: `G:\BAST PRODUCAO`), uma pasta por job com `1 - analise`, `2 - horizontal`, `3 - verticais`, `4 - thumbnails`, `5 - projetos`, `6 - relatorio` e `temp`. Antes de começar, o job confere o espaço: precisa de ~1/4 do tamanho da gravação (mínimo 5 GB) e sempre sobram `ProductionReserveGb` (50 GB). Sem espaço o job espera, com o motivo no painel e um aviso; nada é apagado para abrir espaço.
- **Etapas:** uma por vez, um job por vez. Etapa concluída nunca é refeita numa retomada; etapa interrompida (pausa, parada, reinício do computador) recomeça do zero e o resultado parcial nunca vira entregável. Falha comum tenta de novo até 3 vezes; falha que não se resolve tentando (original alterado, por exemplo) para o job em **Falhou**.
- **Parada de emergência:** botão **PARAR AGORA** (ou `POST /api/production/stop`): nenhuma etapa nova começa e a atual é interrompida, sem apagar nada. **Liberar produção** (`POST /api/production/release`) continua de onde parou.
- **Publicação:** interruptor **Publicar automaticamente o que for APROVADO** na Agenda, desligado por padrão. Desligado, cada vídeo produzido só entra na agenda pelo botão **Liberar** (fase 8).
- **API:** `GET /api/production`, `GET /api/production/jobs/{id}/logs`, `POST /api/production/jobs/{id}/authorize|pause|resume|cancel`.

## Radar de Conteúdo e Estúdio de Ideias

Fluxo separado da edição das lives (filas, pastas, configurações e aprovação próprias).

- **Pesquisa diária:** feita pela tarefa agendada do Claude (`tools\radar\tarefa-radar.md`, a cada 30 min só confere se está na hora). Roda no horário da aba **Radar** (padrão 08:00, fuso da Agenda), quando você clica em **Atualizar pesquisa agora**, ou assim que possível se o PC/app estava desligado no horário. Precisa do app do Claude aberto e do PC ligado.
- **Fontes:** primeiro internacionais (inglês), depois Brasil. Métricas (views, curtidas, comentários, data, views/hora e crescimento entre consultas) só da API oficial do YouTube (`GET /api/radar/youtube`, com a conta já conectada; escopo `youtube.readonly`); o que a API não informa aparece como "não disponível". Cada busca gasta 100 unidades da cota diária do YouTube, compartilhada com os envios: o Radar tem um limite de buscas por dia (padrão 12).
- **Tags:** cada tag ativa tem a própria pesquisa; sinônimos evitam duplicação (GTA VI = GTA 6 = Grand Theft Auto VI = GTA6). Jogo sem tag ativa não é pesquisado.
- **Sugestões:** até 5 horizontais e 5 verticais por tag, cada uma com fonte verificável (link), análise e plano de produção (ideia, gancho, roteiro, o que gravar...). Sem oportunidades reais suficientes, o Radar registra a falta em vez de completar. Pesquisa que falha é avisada no painel e no Telegram, sem reaproveitar sugestões antigas.
- **Telegram:** o mesmo bot recebe 3 mensagens (resumo, horizontais, verticais) com links para o painel (`Bast:PanelUrl`, padrão `http://127.0.0.1:5080/`; só abrem neste computador).
- **Estúdio de Ideias** (`Bast:StudioFolder`, ex.: `G:\BAST\ESTUDIO_DE_IDEIAS`, com as pastas `00 - GRAVACOES_PENDENTES` a `08 - PROJETOS_E_ASSETS`): **Iniciar produção** cria o código `BAST-AAAAMMDD-NNN`, a pasta da produção com `roteiro.json` e `ROTEIRO.txt`. A pasta é reconhecida pelo código do `roteiro.json` (renomear não quebra). A gravação conta como terminada quando você clica em **Terminei de gravar** (ou cria `PRONTO.txt`) e os arquivos param de mudar por 2 minutos; os vídeos são conferidos e a pasta vai para `01 - EM_ANALISE`. Nada é apagado. A edição guiada pelo roteiro chega com as fases 4 a 8 da produção.
- **Publicação:** nenhum vídeo do Estúdio sai sem o botão **Liberar publicação** dele (e só depois de aprovado). A publicação automática da Agenda não vale para o Estúdio.
- **API:** `GET /api/radar`, `GET /api/radar/due`, `POST /api/radar/runs/begin`, `POST /api/radar/runs/{id}/submit|fail`, `POST /api/radar/refresh`, `PUT /api/radar/settings`, `POST|PUT|DELETE /api/radar/tags`, `POST /api/radar/suggestions/{id}/save|ignore|start`, `POST /api/studio/{id}/finished|release`.

## Regras que o programa segue

- Uma pasta só é importada quando para de mudar por 10 segundos, para não pegar cópia pela metade.
- Pasta sem vídeo, sem TXT ou com TXT vazio fica em `01 - ENVIAR VERTICAL` e aparece em `/api/import-issues`.
- Vídeo corrompido ou truncado vai para `05 - ERROS`.
- O mesmo vídeo não é importado duas vezes, mesmo com outro nome. Para repetir de propósito, crie um arquivo vazio `REIMPORTAR.txt` dentro da pasta.
- Cada rede é controlada separadamente: a falha de uma não repete nem desfaz a publicação das outras.
- Se não der para confirmar que uma publicação foi concluída, o programa consulta a plataforma antes de tentar de novo, para não publicar em dobro.
- Os arquivos originais nunca são alterados nem apagados; as pastas são apenas movidas.

## Modo de simulação

Para ver o fluxo completo sem publicar nada, use uma **pasta principal separada** e adicione `"SimulationMode": true` na seção `Bast`. As publicações ficam marcadas como simuladas.

## Segurança

- Tokens e segredos ficam criptografados pela DPAPI do Windows em `SYSTEM\secrets`, fora do banco e dos logs.
- A API escuta apenas em `127.0.0.1`, ou seja, só é acessível neste computador.
- `appsettings.Local.json` e os arquivos `client_secret*.json` estão no `.gitignore`. Nunca os envie para um repositório.

## Remover

PowerShell como Administrador:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall-service.ps1
```

Remove só o serviço. Vídeos, banco e configurações continuam no lugar.

## Para quem vai mexer no código

```powershell
dotnet test
powershell -ExecutionPolicy Bypass -File scripts\e2e-etapa1.ps1
powershell -ExecutionPolicy Bypass -File scripts\e2e-etapa2.ps1
```

A arquitetura está em [docs/ARQUITETURA.md](docs/ARQUITETURA.md) e o levantamento das APIs em [docs/APIS.md](docs/APIS.md).
