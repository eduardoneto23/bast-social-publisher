# BAST SOCIAL PUBLISHER

Publicação automática de vídeos nas redes sociais, rodando no seu próprio computador com Windows.

Você prepara o vídeo, escreve a legenda em um arquivo de texto, coloca tudo em uma pasta e o programa agenda e publica sozinho, mesmo com a janela fechada.

## Situação atual

| Rede | Situação |
|---|---|
| YouTube | Funcionando (vídeos e Shorts), pela API oficial |
| Instagram | Funcionando (Reels), pela API oficial. Ver [docs/CONECTAR-META.md](docs/CONECTAR-META.md) |
| Facebook | Funcionando (Reels de Página), pela API oficial |
| Threads | Planejado |
| TikTok | A API oficial não permite publicação pública automática para ferramenta de uso próprio; ver [docs/APIS.md](docs/APIS.md) |

Avisos de publicação, falha e reconexão podem chegar no seu celular por um bot do Telegram: ver [docs/CONECTAR-TELEGRAM.md](docs/CONECTAR-TELEGRAM.md).

Ainda não existe painel visual: o acompanhamento é feito por endereços no navegador e por scripts, descritos abaixo. O andamento do projeto está em [docs/ACOMPANHAMENTO.md](docs/ACOMPANHAMENTO.md).

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
01 - ENVIAR/       coloque aqui as pastas dos vídeos
02 - AGENDADOS/    validados, aguardando o horário
03 - PUBLICANDO/   em envio
04 - ENVIADOS/     publicados
05 - ERROS/        com falha
06 - ARQUIVADOS/   cancelados
SYSTEM/            banco de dados e credenciais criptografadas
```

Monte a pasta do vídeo fora de `01 - ENVIAR` e mova para lá quando estiver pronta:

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
- A legenda nunca é alterada pelo programa.
- Vídeo vertical de até 3 minutos é publicado pelo YouTube como Short.
- As pastas são publicadas em ordem numérica: `Video 2` antes de `Video 10`.

### 7. Acompanhar

Abra no navegador:

| Endereço | O que mostra |
|---|---|
| http://127.0.0.1:5080/api/queue | Fila, horários e estado por rede, com erros e links |
| http://127.0.0.1:5080/api/import-issues | Pastas que não puderam ser importadas e o motivo |
| http://127.0.0.1:5080/api/accounts | Redes conectadas |
| http://127.0.0.1:5080/api/health | Situação geral do serviço |

## Agenda

O padrão é publicar às 10:00, 14:00, 18:00 e 22:00 (início às 10:00, intervalo de 4 horas), todos os dias, no fuso de São Paulo. Para mudar:

```powershell
$agenda = @{
  startTime = '09:00'; endTime = '23:59'; intervalMinutes = 180; maxPerDay = 5
  days = @('Monday','Tuesday','Wednesday','Thursday','Friday')
  timeZoneId = 'America/Sao_Paulo'
  platforms = @('YouTube')
  missedPolicy = 'NextAllowedSlot'; missedGraceMinutes = 30; maxAttempts = 5
} | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:5080/api/settings/schedule -Method Put -ContentType 'application/json' -Body $agenda
```

- `missedPolicy`: o que fazer se o computador estava desligado no horário. `NextAllowedSlot` publica no próximo horário; `NextCycle` espera o dia seguinte. Vídeos atrasados nunca são publicados todos de uma vez.
- Os vídeos já agendados mantêm o horário; a nova agenda vale para os próximos.

## Controles

```powershell
# Pausar e retomar a fila
Invoke-RestMethod http://127.0.0.1:5080/api/queue/pause -Method Post
Invoke-RestMethod http://127.0.0.1:5080/api/queue/resume -Method Post

# Cancelar um vídeo ainda não publicado (o id aparece em /api/queue)
Invoke-RestMethod http://127.0.0.1:5080/api/videos/ID-DO-VIDEO/cancel -Method Post
```

## Regras que o programa segue

- Uma pasta só é importada quando para de mudar por 10 segundos, para não pegar cópia pela metade.
- Pasta sem vídeo, sem TXT ou com TXT vazio fica em `01 - ENVIAR` e aparece em `/api/import-issues`.
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
