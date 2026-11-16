# APIs das plataformas

Levantamento feito na documentação oficial em 2026-11-14. Reverificar cada item antes de implementar o conector correspondente.

| Rede | Publicação pública automática | Envio do arquivo | Limites | Aprovação |
|---|---|---|---|---|
| YouTube | Sim | Upload direto (`videos.insert`) | 100 uploads/dia; até 256 GB | Sem auditoria desde 2026-11-02 |
| Instagram (Reels) | Sim | Upload direto resumable, só com Facebook Login for Business | Legenda 2.200 caracteres, 30 hashtags, 20 menções; 3 s a 15 min; 300 MB | Conta profissional ligada a Página |
| Facebook (Reels de Página) | Sim, só Páginas | Upload direto em 3 fases | 30 Reels/24 h; 3 a 90 s; 9:16 | `pages_show_list`, `pages_read_engagement`, `pages_manage_posts` |
| Threads | Sim | Somente URL pública (`video_url`) | Texto 500 caracteres (emoji conta em bytes UTF-8); 250 posts/24 h; 5 min; 1 GB | Sem acesso avançado, só "testers" do app |
| TikTok | Não para uso próprio | Upload em blocos ou URL de domínio verificado | ~15 posts/dia por conta; legenda 2.200 | Auditoria obrigatória |

## YouTube

- Revision history, 2026-11-02: uploads de projetos não verificados não ficam mais restritos a privado.
- `privacyStatus` omitido assume `public`; o conector deve enviar o valor explicitamente.
- App OAuth em "Testing" tem refresh token que expira em 7 dias. Colocar em "In production".
- Escopo: `youtube.upload`.
- Título: até 100 caracteres. Descrição: até 5.000 bytes. Nenhum dos dois aceita `<` ou `>`.
- As políticas proíbem o cliente de truncar ou alterar o texto informado pelo usuário sem consentimento.

- Upload retomável: `POST /upload/youtube/v3/videos?uploadType=resumable` devolve o endereço da sessão em `Location`; blocos em múltiplos de 256 KB com `Content-Range`; `308` indica bloco recebido; consulta com `Content-Range: bytes */total`; `404` significa sessão expirada.
- OAuth para app instalado: PKCE obrigatório, redirecionamento para `http://127.0.0.1:porta`, refresh token sempre devolvido; `invalid_grant` exige nova autorização.

## Instagram

- Fluxo: criar contêiner (`media_type=REELS`, `upload_type=resumable`), enviar bytes para `rupload.facebook.com`, consultar `status_code` até `FINISHED`, chamar `media_publish`.
- `status_code`: `EXPIRED`, `ERROR`, `FINISHED`, `IN_PROGRESS`, `PUBLISHED`. Contêiner expira em 24 h.
- A documentação cita 100 posts/24 h em um trecho e 50 em outro: consultar `content_publishing_limit`.
- `cover_url` (thumbnail) exige URL pública; `thumb_offset` não.

## Facebook

- `POST /{page-id}/video_reels` com `upload_phase=start`, upload, depois `upload_phase=finish` com `video_state=PUBLISHED`.
- Só Páginas; perfis pessoais não são suportados.

## Meta: autenticação e modos do app

- Redirecionamento OAuth precisa ser HTTPS e estar registrado; "Strict Mode" é obrigatório. Por isso o sistema usa um token gerado no Explorador da Graph API.
- Token de usuário de longa duração: cerca de 60 dias. Token de Página obtido a partir dele: "do not have an expiration date", podendo ser invalidado em certas condições.
- Modo de desenvolvimento: "Any data generated while an app is in Development mode, such as test posts, can only be seen by role users."
- Erros: 190 e 102 (token), 10 e 200 a 299 (permissão), 4, 17, 341 e 613 (limite), 1 e 2 (temporários).
- Reels do Facebook: retomada do upload pelo `uploading_phase.bytes_transfered` do estado do vídeo.
- Instagram: consultar o contêiner uma vez por minuto, por até 5 minutos; contêiner expira em 24 h.

## Threads

- `POST /{user-id}/threads` com `media_type=VIDEO` e `video_url`, aguardar processamento, `POST /{user-id}/threads_publish`.
- Não existe upload direto: mesmo no modo local é necessário armazenamento com URL temporária.

## TikTok

- Sem auditoria: posts apenas `SELF_ONLY` e a conta precisa estar privada.
- As diretrizes listam como não aceitável: "A utility tool to help upload contents to the account(s) you or your team manages".
- A interface exigida pede escolha manual da privacidade, sem valor padrão, e consentimento antes de cada envio.
- Conclusão: publicação pública automática pela API oficial não é viável para este projeto.

## Não verificado

- Validade e renovação dos tokens do Threads e do TikTok.
- Se um app da Meta em modo Ao vivo, só com acesso padrão, publica conteúdo público para contas de quem tem função no app.
- Qual token as chamadas do Instagram exigem (a documentação cita Página e usuário).
- Endpoint e valores de status do contêiner do Threads.

## Fontes

- https://developers.google.com/youtube/v3/revision_history
- https://developers.google.com/youtube/v3/docs/videos/insert
- https://developers.google.com/youtube/v3/docs/videos
- https://developers.google.com/youtube/terms/developer-policies
- https://support.google.com/cloud/answer/15549945
- https://developers.facebook.com/docs/instagram-platform/content-publishing/
- https://developers.facebook.com/docs/instagram-platform/instagram-graph-api/reference/ig-user/media/
- https://developers.facebook.com/documentation/video-api/guides/reels-publishing
- https://developers.facebook.com/documentation/threads/posts
- https://developers.tiktok.com/docs/en/content-sharing-guidelines
- https://developers.tiktok.com/docs/en/content-posting-api-get-started
- https://developers.tiktok.com/docs/en/content-posting-api-reference-direct-post
