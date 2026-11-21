# Avisos pelo Telegram

O sistema não publica no Telegram. Ele usa um bot **seu** para avisar, no seu celular, o que aconteceu com as publicações. Os vídeos não passam pelo Telegram e só a sua conversa recebe as mensagens.

> Estado: conectado e testado com o Telegram real em 16/11/2026 (vínculo por código, aviso de teste e aviso de pasta com problema entregue em 5 segundos). Ainda não exercitados contra o Telegram real: limite de envio e bot bloqueado.

## O que é avisado

| Aviso | Quando |
|---|---|
| ✅ Publicado | Todas as redes de um vídeo terminaram com sucesso. Traz o link de cada rede. |
| ⚠️ Publicado com falhas / ❌ Falhou | Todas as redes terminaram e pelo menos uma falhou de vez. Traz o motivo informado pela plataforma. |
| ❓ Sem confirmação | Um envio ficou sem resposta da plataforma. O sistema continua consultando e não envia de novo sem certeza. |
| 🔑 Reconectar | Uma conta teve o acesso recusado (token vencido, senha trocada, permissão removida). |
| 📁 Pasta com problema | Uma pasta em `01 - ENVIAR VERTICAL` foi recusada (sem legenda, vídeo corrompido, duplicada). |

Há um aviso por vídeo, quando todas as redes terminam, e não um por rede. Novas tentativas automáticas não geram aviso.

## 1. Criar o bot

1. No Telegram, abra a conversa com **@BotFather** (o oficial, com selo de verificado).
2. Envie `/newbot`, escolha um nome e um nome de usuário terminado em `bot`.
3. O BotFather responde com o **token** do bot, no formato `123456789:AA...`. Trate como senha.

## 2. Conectar

```
powershell -ExecutionPolicy Bypass -File scripts\connect-telegram.ps1
```

O script pede o token, mostra um código de 6 dígitos e pede que você envie esse código para o seu bot no Telegram. O sistema só aceita a conversa privada que enviou o código: outra pessoa que encontre o bot não recebe nada.

Ao final chega uma mensagem de confirmação no Telegram.

Conferir: http://127.0.0.1:5080/api/connect/telegram/status

## Testar e acompanhar

```
Invoke-RestMethod http://127.0.0.1:5080/api/connect/telegram/test -Method Post
Invoke-RestMethod http://127.0.0.1:5080/api/notifications
```

O segundo comando lista os últimos avisos e a situação de cada um (enviado, aguardando nova tentativa, descartado).

## Como a entrega funciona

- O aviso é gravado no banco junto com o fato que o causou e enviado em seguida por outro ciclo. Se o computador desligar no meio, o aviso não se perde nem é enviado em dobro.
- Sem internet ou com limite de envio do Telegram, o sistema tenta de novo com espera crescente (1, 2, 4... minutos, até 1 hora).
- Aviso que não pôde ser entregue em 24 horas é descartado.
- Enquanto o Telegram não está conectado, os avisos são descartados. Conectar depois não despeja avisos antigos.
- Uma falha nos avisos nunca atrasa nem impede uma publicação.

## Segurança

- O token fica criptografado em `SYSTEM\secrets`, como os demais.
- A Bot API exige o token no endereço do pedido. Por isso este módulo não registra pedidos em log e as mensagens de erro não incluem o endereço.
- Se o token vazar, envie `/revoke` ao @BotFather e rode o script de novo com o token novo.

## Desconectar

```
Invoke-RestMethod http://127.0.0.1:5080/api/connect/telegram -Method Delete
```
