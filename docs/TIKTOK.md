# TikTok (modo assistido)

## Por que não é automático

Documentação oficial do TikTok conferida em 17/11/2026 (Content Posting API, regras de compartilhamento e de análise de apps):

- App sem análise do TikTok só publica como **privado** ("só eu"), e a conta precisa estar privada na hora do post.
- A análise lista como **não aceitável** uma ferramenta para enviar conteúdo às contas que você ou a sua equipe administram, e exige que o app não seja de uso pessoal ou privado.
- Cada post exige escolha manual de privacidade, sem valor pré-selecionado, prévia e consentimento do usuário.

Fazer o app passar na análise exigiria descrevê-lo como algo que ele não é. Por isso o programa não usa a API do TikTok.

## Como funciona

1. Conecte o Telegram ([CONECTAR-TELEGRAM.md](CONECTAR-TELEGRAM.md)).
2. No painel, aba **Agenda**, marque o **TikTok** nas redes.
3. No horário do vídeo, o bot manda:
   - o arquivo original do vídeo, como documento (sem recompressão);
   - a legenda pronta para copiar, com os botões **✅ Publiquei** e **⏭️ Não vou publicar**.
4. Publique no app do TikTok e toque em **Publiquei**. A mesma resposta pode ser dada no painel, aba **Fila**.

Enquanto você não responde, o TikTok aparece como **Aguardando você** e a pasta do vídeo fica em `03 - PUBLICANDO`. Responder **Não vou publicar** marca só o TikTok como cancelado; as outras redes não mudam.

## Limites

- O Telegram aceita arquivos de até 50 MB vindos de bots. Acima disso, a mensagem traz só a legenda e diz em qual pasta está o vídeo.
- O programa não tem como saber se o post foi mesmo feito, nem o link dele: vale a sua resposta.
- Só a conversa vinculada ao bot pode responder pelos botões.
- O cancelamento pela fila não cancela um TikTok que já foi entregue; use o botão **Não vou publicar**.
