# Acompanhamento do desenvolvimento

Atualizado em 2026-11-14. Este arquivo é o ponto de retomada entre sessões de trabalho.

## Situação por etapa

| Etapa | Situação |
|---|---|
| 1. Fundação | Concluída; falta só o teste de reinicialização real do Windows |
| 2. Gerenciamento (fila, legendas por rede, agendador, pastas) | Concluída |
| 3. Interface | Não iniciada |
| 4. YouTube, Instagram, Facebook | YouTube funcionando com a API real. Instagram e Facebook implementados, **sem teste com a API real** |
| 5. Threads e TikTok | Não iniciada |
| 6. Confiabilidade | Parte antecipada na Etapa 2 (ver abaixo) |
| 7. Nuvem | Não iniciada |
| 8. Distribuição | Não iniciada |

**O YouTube está conectado e publicando de verdade.** As demais redes não têm conector: um vídeo colocado em `01 - ENVIAR` é agendado e enviado só para o YouTube.

## Concluído na Etapa 1

- Solução .NET 10 em camadas: `Bast.Domain`, `Bast.Application`, `Bast.Infrastructure`, `Bast.Service`, `Bast.Tests`.
- Banco SQLite (EF Core, modo WAL), máquina de estados de publicação com os 10 estados.
- Monitor de `01 - ENVIAR`, espera pelo fim da cópia, inspeção da pasta, leitura integral da legenda.
- Validação do vídeo com ffprobe e decodificação do trecho final com ffmpeg.
- Duplicidade por SHA-256; reimportação intencional com `REIMPORTAR.txt`.
- Importação resistente a queda; ordenação numérica.
- Serviço do Windows instalado (`BastSocialPublisher`, LocalSystem, início automático atrasado, reinício após falha).

## Concluído na Etapa 2

- **Regras de legenda por rede** (`CaptionRules`): título do YouTube é a primeira linha; o texto nunca é alterado. Limites validados: Instagram (2.200 caracteres, 30 hashtags, 20 menções, 3 s a 15 min, 300 MB), Facebook Reels (3 a 90 s), Threads (500 caracteres com emoji contando em bytes, 5 min, 1 GB), TikTok (2.200), YouTube (título 100 caracteres, descrição 5.000 bytes, sem `<` e `>`).
- Incompatibilidade é sinalizada antes da publicação: a publicação daquela rede fica `FAILED` com o motivo, as outras redes seguem.
- **Agendador** (`SlotCalculator`, `SchedulingService`): horário inicial, intervalo, dias da semana, máximo por dia, último horário permitido, fuso, redes habilitadas. Tabela `schedule_settings`.
- Horários gravados no banco (`videos.ScheduledAtUtc` e `publications.ScheduledAtUtc`); reinício não recalcula nem duplica.
- **Horário perdido:** política configurável (próximo horário permitido ou próximo ciclo) e tolerância de atraso. Vídeos atrasados são redistribuídos na mesma ordem; dois vídeos nunca saem juntos.
- **Fila e motor** (`PublishingEngine`): cada publicação é independente; estado gravado antes da chamada à plataforma; tentativas registradas em `publication_attempts`.
- Recuperação: backoff exponencial (1, 2, 4, 8... minutos, teto de 1 hora), limite de tentativas, respeito ao tempo de espera informado pela plataforma, falha definitiva sem repetição.
- `UNKNOWN`: timeout, erro inesperado ou serviço interrompido durante o envio. Só sai por consulta à plataforma; nunca reenvia às cegas.
- **Movimentação de pastas** (`FolderSync`): `02 → 03 → 04`, `05` em falha, `06` quando cancelado. Pasta travada por outro programa não bloqueia a publicação e é movida depois.
- **Modo de simulação** (`Bast:SimulationMode`): percorre todo o fluxo sem enviar nada; publicações ficam marcadas como simuladas e sem link.
- **Interface dos conectores** (`IPlatformConnector`): publicar, consultar, gravar identificador externo no meio do envio.
- **API:** `/api/queue`, `/api/settings/schedule` (GET/PUT), `/api/queue/pause`, `/api/queue/resume`, `/api/videos/{id}/cancel`.

## Concluído na Etapa 4 (parcial: YouTube)

- **Armazenamento seguro** (`DpapiSecretStore`): credenciais criptografadas com DPAPI em `SYSTEM\secrets`, fora do banco e dos logs.
- **Contas** (`platform_accounts`, `AccountService`): situação de cada rede (conectada, precisa reconectar, desconectada).
- **OAuth do Google** (`GoogleAuth`): código de autorização com PKCE e redirecionamento para `127.0.0.1` em porta livre; renovação automática do access token; `invalid_grant` interrompe a integração e marca a conta como "precisa reconectar".
- **Conector do YouTube** (`YouTubeConnector`): upload retomável em blocos, sessão gravada antes do primeiro byte, retomada após queda de rede, erro 5xx tratado com consulta do que já foi recebido, cota diária esgotada aguardando a renovação, estado final confirmado por `uploadStatus`.
- **Motor:** novos resultados "sem internet" e "conta desconectada" esperam sem gastar tentativas.
- **API:** `/api/accounts`, `/api/connect/youtube/credentials`, `/start`, `/status` e `DELETE /api/connect/youtube`.
- `scripts/connect-youtube.ps1` para conectar a conta enquanto o painel não existe.

## Concluído na Etapa 4: Instagram e Facebook (sem teste real)

- **Autenticação da Meta** (`MetaAuth`): o usuário gera um token no Explorador da Graph API; o sistema troca por um de longa duração e guarda o token da Página (sem data de expiração) e o id do Instagram vinculado. O fluxo OAuth com redirecionamento local não foi usado porque a Meta exige endereço HTTPS registrado.
- Tokens e segredos vão sempre no cabeçalho ou no corpo do pedido, nunca no endereço. O log de pedidos HTTP foi desligado para os clientes da Meta e do Google.
- **Facebook** (`FacebookConnector`): Reels de Página em três fases, com retomada pelos bytes que a Meta guardou e confirmação por `publish_status = published`.
- **Instagram** (`InstagramConnector`): contêiner, upload direto, espera do processamento e `media_publish` feito uma única vez por contêiner.
- Erro 190/102 ou de permissão interrompe as duas integrações e marca as contas como "precisa reconectar".
- **API:** `/api/connect/meta/credentials`, `/token`, `/status` e `DELETE /api/connect/meta`. Script `scripts/connect-meta.ps1`. Guia em `docs/CONECTAR-META.md`.

## Como foi verificado

- `dotnet test`: 114 testes automatizados, todos passando. Os dos conectores usam servidores HTTP falsos; contra a API real só o YouTube foi exercitado.
- `scripts/e2e-etapa1.ps1`: 17 verificações de importação com o serviço real.
- `scripts/e2e-etapa2.ps1`: 22 verificações com o serviço real em modo de simulação: agenda de 1 em 1 minuto, pausa, legenda longa barrada no Threads, um vídeo por horário, encerramento forçado no meio da fila, 12 publicações com uma tentativa cada, falha parcial em `05 - ERROS`.
- `scripts/test-service.ps1`: serviço instalado, reinício automático após queda.

Cenários da seção 12 cobertos por teste automatizado: 1, 2, 3, 4, 8 (falha parcial), 9 (publicação duplicada), 10 (operação incerta), 12 (desligamento durante publicação), 13 (retomada). Parcial: 5 (reinício do processo, não do Windows). Também cobertos, só contra servidor falso: 6 (queda de internet durante o upload) e 7 (token expirado ou revogado). Pendentes: 11 (execução simultânea), 14 (backup).

## Pendências

- **Primeiro envio real no Facebook e no Instagram.** Depende de o usuário criar o aplicativo da Meta e conectar. Pontos a confirmar no teste: visibilidade pública com app em modo Ao vivo sem revisão, aceitação do token da Página nas chamadas do Instagram e formato do link do Reel do Facebook.
- Instagram: se a resposta do `media_publish` se perder e o contêiner nunca passar a `PUBLISHED`, a publicação fica em `UNKNOWN` até intervenção. Falta a reconciliação pela lista de mídias recentes.
- Instagram: uma publicação em `PROCESSING` é concluída mesmo com a fila pausada, porque o envio já tinha começado.
- Facebook: vídeos acima de 90 s são barrados; publicar como vídeo comum de Página ainda não existe.
- Serviço instalado atualizado em 2026-11-16 com os conectores da Meta (8 verificações passaram). Contas da Meta ainda não conectadas.

- **Primeiro upload real feito em 2026-11-16:** vídeo de teste de 6 s, privado. Percurso observado: `Scheduled` no horário, `Uploading`, sessão gravada, `Processing` com o id do vídeo, `Published` 24 s depois, pasta em `04 - ENVIADOS`, uma tentativa. Os escopos `youtube.upload` e `youtube.readonly` bastaram para enviar e consultar o estado.
- **Ainda não exercitado contra a API real:** retomada após queda de rede, upload em vários blocos (o teste coube em um bloco), cota esgotada, token recusado e thumbnail.
- YouTube configurado em 2026-11-16 como `PrivacyStatus: public` e `CategoryId: 20` (Jogos), em `appsettings.Local.json` (fonte e `publish/`).
- App OAuth publicado ("Em produção") em 2026-11-16, com página inicial e política hospedadas no GitHub Pages (modelos em `site/`). Conta reconectada depois da publicação.
- Serviço instalado atualizado em 2026-11-16 com o conector do YouTube (8 verificações passaram, migração `Accounts` aplicada). Conta ainda não conectada.
- Thumbnail: enviada logo após o upload; se o serviço cair exatamente nesse intervalo, o vídeo fica sem a thumbnail personalizada.

- Serviço instalado atualizado para a versão da Etapa 2 em 2026-11-14 (8 verificações de `scripts/test-service.ps1` passaram; migração `Scheduling` aplicada no banco real). Para atualizar de novo: o mesmo script, como Administrador.
- Reinicialização real do Windows ainda não foi testada.
- Publicações são executadas uma por vez; paralelismo entre redes fica para a Etapa 6.
- Ações de reagendar, publicar a próxima agora e tentar de novo uma publicação falhada: Etapa 3, junto com o painel.
- Vídeo já agendado não ganha publicação para uma rede conectada depois; também Etapa 3.
- Logs em arquivo, backup do banco, notificações: Etapa 6.
- Tabelas `platform_accounts`, `upload_sessions`, `storage_objects`, `notifications_outbox`, `audit_log`, `backups`: na etapa que as utiliza.

## Problemas conhecidos

- A verificação de truncamento decodifica só os 3 segundos finais do vídeo.
- A contagem de caracteres do Threads para emojis é uma aproximação por faixas Unicode; pode divergir da plataforma em poucos caracteres perto do limite de 500.
- O limite de 90 s do Facebook vale para Reels. Publicar vídeos mais longos como vídeo comum de Página será decidido na Etapa 4.
- O modo de simulação marca vídeos como publicados e move as pastas: usar com uma pasta principal separada.

## Decisões em aberto

- **TikTok:** a API oficial não permite publicação pública automática para ferramenta de uso próprio. Recomendação: modo assistido via Telegram. Vem desabilitado nas configurações. Decidir até a Etapa 5.
- **Threads:** exige URL pública do vídeo. Proposta: bucket Cloudflare R2 com URL assinada temporária.

## Próximos passos (Etapa 3)

1. Painel web (React + TypeScript) servido pelo próprio serviço, identidade visual Bast.
2. Visão geral, fila com estado por plataforma, histórico com tentativas e erros.
3. Tela de configurações da agenda e das redes.
4. Controles: pausar, retomar, reagendar, cancelar, tentar de novo, publicar a próxima, abrir pasta.
5. Problemas de importação com ação de dispensar.

## 2026-11-16 - Primeiro envio real na Meta

- Facebook (Pagina) e Instagram conectados pelo token do Explorador; app da Meta ainda em modo de desenvolvimento.
- Video de teste vertical de 6 s publicado nas duas redes, uma tentativa cada: Facebook confirmado em ~40 s, Instagram em ~1 min 40 s. Pasta foi para 04 - ENVIADOS.
- Confirmado: Instagram aceita o token da Pagina; link do Facebook vem como /reel/<id>/.
- Pendente: icone do app nao sobe no painel (erro generico da Meta); modo Ao vivo; conferir visibilidade dos Reels em modo de desenvolvimento; mensagem do connect-meta.ps1 diz "token recusado" quando o erro e da chave secreta.
- Novo: scripts/check-meta-secret.ps1; site/privacidade.html com Meta e exclusao de dados (copia publica ainda nao atualizada); site/icone-1024.*


## 2026-11-16 - Avisos pelo Telegram

- Fila de avisos no banco (tabela notifications, migracao Notifications): o aviso e gravado na mesma transacao do fato e entregue por outro ciclo (NotificationWorker, a cada 5 s).
- Avisos: video finalizado (um por video, com links e motivos), envio sem confirmacao, conta precisa reconectar, pasta com problema.
- Novo projeto Bast.Notifications.Telegram (Bot API oficial): token validado por getMe, conversa vinculada por codigo via getUpdates, sendMessage em texto puro.
- Sem canal conectado os avisos sao descartados; aviso nao entregue em 24 h e descartado; novas tentativas com espera crescente.
- Endpoints: /api/connect/telegram/status|token|link|test, DELETE /api/connect/telegram, /api/notifications. Script scripts/connect-telegram.ps1. Guia docs/CONECTAR-TELEGRAM.md.
- Mensagem da conexao da Meta agora distingue chave secreta recusada de token recusado.
- 140 testes passando. Nao exercitado contra o Telegram real ate a primeira conexao.

- Telegram real conectado em 2026-11-16: vinculo por codigo, aviso de teste e aviso de pasta com problema entregues. Servico instalado atualizado.


## 2026-11-16 - Painel (Etapa 3)

- Pagina estatica em src/Bast.Service/wwwroot (index.html, styles.css, app.js), sem bibliotecas externas, servida em http://127.0.0.1:5080/ e atualizada a cada 5 s.
- Secoes: Visao geral, Fila (cancelar), Historico, Problemas, Contas (teste do Telegram), Agenda (editar e salvar), Avisos. Botao de pausar/retomar no topo.
- Conexao de contas continua pelos scripts: credenciais nao passam pelo navegador.
- LocalRequestGuard: a API recusa pedidos de outras origens (pagina de terceiros no navegador) e de nomes de host que nao sejam 127.0.0.1/localhost. Cabecalhos CSP default-src 'self', sem iframe.
- Testado no navegador contra instancia isolada em simulacao: abas, cancelar, pausar, agenda com erro de validacao e com sucesso. 157 testes automaticos.
- Publicar agora: POST /api/videos/{id}/publish-now e botao na Fila. So adianta o horario do video; o envio continua sendo do motor (respeita pausa, nao duplica). Os demais videos mantem o horario. Usado em um envio real nas tres redes (uma tentativa em cada); o botao do painel em si nao foi clicado. 158 testes automaticos.
- Nao feito no painel: repetir manualmente uma publicacao que falhou, conectar contas, ver legenda/miniatura do video, paginacao do historico (mostra os 100 mais recentes).

