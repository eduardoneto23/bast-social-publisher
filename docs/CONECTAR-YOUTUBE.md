# Conectar o YouTube

O sistema publica pela YouTube Data API v3 com um aplicativo OAuth seu, criado no Google Cloud. A criação é feita uma única vez e é gratuita.

## 1. Criar o aplicativo no Google Cloud

1. Acesse https://console.cloud.google.com/ com a conta dona do canal e crie um projeto (por exemplo, "Bast Social Publisher").
2. Em **APIs e serviços > Biblioteca**, ative **YouTube Data API v3**.
3. Em **Google Auth Platform > Branding**, preencha nome do app e e-mails.
4. Em **Público-alvo**, escolha **Externo** e depois clique em **Publicar app** para sair do modo de teste. No modo de teste a autorização expira a cada 7 dias.
5. Em **Clientes**, crie um cliente OAuth do tipo **App para computador**. Guarde o **Client ID** e o **Client Secret**.

Como o app não passou pela verificação do Google, a tela de autorização mostra o aviso "O Google não verificou este app". Para uso próprio isso é esperado: clique em **Avançado** e continue.

## 2. Conectar

Com o serviço em execução, rode no PowerShell:

```
powershell -ExecutionPolicy Bypass -File scripts\connect-youtube.ps1
```

O script pede o Client ID e o Client Secret, abre o navegador e aguarda. Autorize com a conta do canal. Ao final aparece o nome do canal conectado.

- A autorização precisa ser feita no navegador deste computador, porque o Google devolve a resposta para `127.0.0.1`.
- O Client Secret e o token de acesso ficam criptografados (DPAPI do Windows) em `SYSTEM\secrets`, fora do banco e dos logs.

Conferir a situação: `http://127.0.0.1:5080/api/accounts`.

## 3. Configurações

Em `appsettings.Local.json`:

```json
{
  "YouTube": {
    "PrivacyStatus": "public",
    "CategoryId": "20"
  }
}
```

- `PrivacyStatus`: `public`, `unlisted` ou `private`. Para o primeiro teste real, use `private`.
- `CategoryId`: `22` (Pessoas e blogs, padrão) ou `20` (Jogos).

## Como a publicação funciona

1. O sistema abre uma sessão de upload e grava o endereço dela no banco antes de enviar o primeiro byte.
2. O vídeo é enviado em blocos de 8 MB. Se a internet cair, o envio continua do ponto em que parou, na mesma sessão.
3. Terminado o upload, a publicação fica em `PROCESSING` até a API informar `uploadStatus = processed`. Só então vira `PUBLISHED`, com o link.
4. Vídeo rejeitado ou com falha de processamento vira `FAILED` com o motivo informado pelo YouTube.

## Limites

- 100 uploads por dia por projeto. Ao esgotar, a publicação aguarda a renovação da cota (meia-noite do horário do Pacífico) sem contar como falha definitiva.
- Título: 100 caracteres. Descrição: 5.000 bytes. Sem `<` e `>`.
- Thumbnail personalizada exige canal com recursos avançados habilitados; se o YouTube recusar, o vídeo é publicado sem ela e o motivo fica no log.

## Desconectar

```
Invoke-RestMethod http://127.0.0.1:5080/api/connect/youtube -Method Delete
```

Apaga o token local e pede a revogação ao Google.
