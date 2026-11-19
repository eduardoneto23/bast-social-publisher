# Conectar o Threads

> Estado: publicação testada contra a API real em 17/11/2026 (contêiner aceito pelo Threads usando o link temporário). A primeira publicação real pelo serviço acontece no próximo vídeo.

## Como funciona

A API do Threads não recebe o arquivo de vídeo: ela só baixa o vídeo de um endereço público na internet. Para isso, no horário da publicação o programa abre um **link temporário** para o arquivo original, pelo túnel rápido do Cloudflare (`cloudflared`, sem conta e sem custo):

1. Um servidor local entrega só aquele arquivo, num caminho aleatório, apenas para o túnel.
2. O túnel gera um endereço `https://….trycloudflare.com` novo a cada publicação.
3. O programa manda esse endereço para o Threads, com a legenda. O Threads baixa o vídeo (no teste, uma única vez, em menos de 1 minuto).
4. Assim que o Threads termina de processar, o túnel é fechado. Se algo travar, ele fecha sozinho em 30 minutos.

O endereço não aparece em logs nem no painel. Quem tivesse o endereço só conseguiria baixar aquele vídeo, que vai ser publicado em seguida.

### Por que não usa o arquivo do Instagram

A primeira versão usava o arquivo do Reel publicado no Instagram. No teste real de 17/11/2026, o Threads recusou esse arquivo (`ERROR`, motivo `UNKNOWN`) três vezes, também pelo link do Reel do Facebook. O arquivo original pelo link temporário foi aceito. Com isso, o Threads não depende mais do Instagram e recebe o vídeo sem recompressão.

### Instalar o cloudflared

Baixe o executável oficial (assinado por Cloudflare, Inc.) e salve em `SYSTEM\tools\cloudflared.exe`, dentro da pasta principal:

```powershell
$dir = 'D:\BAST SOCIAL PUBLISHER\SYSTEM\tools'   # troque pela sua pasta principal
New-Item -ItemType Directory -Force $dir | Out-Null
Invoke-WebRequest 'https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe' -OutFile "$dir\cloudflared.exe"
(Get-AuthenticodeSignature "$dir\cloudflared.exe").SignerCertificate.Subject   # deve mostrar Cloudflare, Inc.
```

Para usar outro local, informe `"CloudflaredPath"` na seção `Bast` do `appsettings.Local.json`. Sem o cloudflared, o Threads fica como falha, com o motivo.

### Limites

- O túnel rápido do Cloudflare é feito para testes e não tem garantia de disponibilidade. Se ele não abrir, o Threads tenta de novo mais tarde; se o Threads não conseguir baixar, um link novo é aberto.
- O computador precisa estar ligado e com internet no horário, como para as outras redes.
- Legenda de até 500 caracteres (emojis contam como bytes UTF-8) e vídeo de até 5 minutos. Fora disso, o Threads fica como falha antes do envio.
## 1. Preparar o app

Os nomes de menu abaixo são traduções da documentação oficial em inglês e podem estar escritos de outro jeito no painel em português.

O app da Meta que você já usa para Facebook e Instagram tem um caso de uso do Threads, com **ID e chave próprios**.

1. Em https://developers.facebook.com/apps, abra o seu app.
2. Em **Casos de uso**, confira se **Acessar a API do Threads** está na lista. Em **Personalizar**, confira as permissões `threads_basic` e `threads_content_publish`.
3. Ainda em **Personalizar > Configurações**, em **URIs de redirecionamento OAuth válidos**, cadastre um endereço **https** seu. Pode ser a página inicial do site com a política de privacidade (a pasta [site/](../site/)), por exemplo `https://SEU-USUARIO.github.io/SEU-SITE/`. Depois de autorizar, o Threads abre esse endereço com um código no final; a página não precisa fazer nada.
4. Em **Funções do app > Funções**, adicione a sua conta do Threads como **Testador do Threads**. Aceite o convite no Threads: **Configurações > Conta > Permissões do site** (no site ou no aplicativo).
5. Em **Configurações do app > Básico**, anote o **ID do app do Threads** e a **chave secreta do app do Threads**. São diferentes do ID e da chave do app principal.

Sem análise do app pela Meta, só contas com função no app (você e os testadores) podem ser publicadas. É o suficiente para publicar na sua própria conta.

## 2. Conectar

```
powershell -ExecutionPolicy Bypass -File scripts\connect-threads.ps1
```

O script pede o ID, a chave secreta do Threads e o endereço de retorno, e abre a autorização no navegador. Depois de clicar em **Permitir**, copie o endereço inteiro da barra do navegador e cole no script.

Conferir: http://127.0.0.1:5080/api/connect/threads/status ou a aba Contas do painel.

## Token

- O token vale 60 dias e é renovado sozinho quando faltam menos de 50 dias (a cada ~10 dias). O serviço confere a cada 6 horas, mesmo sem publicações.
- A documentação diz que a permissão concedida dura 90 dias e que a renovação só a estende para **perfis públicos**. Com perfil privado, conte com conectar de novo a cada 90 dias.
- Se o Threads recusar o token, a conta aparece como "precisa reconectar", o aviso vai para o Telegram e as publicações do Threads esperam sem gastar tentativas.
- A documentação do Threads passa o token como parâmetro do endereço. O programa não registra endereços em log e nenhuma mensagem de erro mostra o endereço.

## O que falta confirmar com a API real

- O formato exato dos erros do Threads para token inválido (a documentação não traz).
- Se o painel da Meta aceita `http://localhost` como endereço de retorno; por isso o programa exige https.

## Desconectar

```powershell
Invoke-RestMethod http://127.0.0.1:5080/api/connect/threads -Method Delete
```
