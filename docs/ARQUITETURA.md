# Arquitetura

## Visão geral

Um único serviço do Windows (`Bast.Service`) hospeda o motor de automação e a API HTTP local.
O painel (Etapa 3) será um SPA estático servido por esse serviço; fechar o navegador não afeta o motor.

```
src/
  Bast.Domain/          entidades, estados, regras puras (sem dependências)
  Bast.Application/     casos de uso: importação, e futuramente fila, agenda e retry
  Bast.Infrastructure/  EF Core/SQLite, ffprobe/ffmpeg, e futuramente segredos, S3, Telegram
  Bast.Service/         serviço Windows + API
tests/Bast.Tests/       testes automatizados
scripts/                teste de ponta a ponta e instalação do serviço
docs/                   este arquivo, ACOMPANHAMENTO.md, APIS.md
```

Projetos previstos: `Bast.Connectors.YouTube`, `.Meta`, `.Threads`, `.TikTok`, `.Simulator`, `Bast.Agent` (modo híbrido) e `dashboard/`.

## Decisões

- **O banco é a fonte oficial.** As pastas apenas refletem o estado; caminhos são gravados relativos à pasta principal.
- **Sem Quartz.NET.** A tabela `publications` guarda o horário e o estado; um laço busca tarefas vencidas e as reserva com lease transacional. Um segundo armazenamento de horários seria fonte de duplicidade após reinício.
- **Gravar antes de agir.** Toda operação com efeito externo (mover pasta, publicar) registra a intenção no banco antes e a confirmação depois. A reinicialização conclui o que ficou pela metade.
- **`UNKNOWN` exige reconciliação.** A máquina de estados não permite `UNKNOWN -> UPLOADING`.
- **Unicidade no banco.** Índices únicos em (hash, geração) e (vídeo, plataforma) impedem duplicidade mesmo com erro de lógica.
- **Segredos:** DPAPI atrás de uma interface, porque o Credential Manager é por usuário e não atende bem um serviço. Etapa 4.
- **Modo híbrido:** banco, armazenamento e segredos ficam atrás de interfaces; a troca para PostgreSQL e S3 é por configuração. Etapa 7.

## Publicação (Etapa 2)

A cada ciclo o motor: agenda vídeos sem horário, redistribui horários perdidos, consulta publicações em processamento ou incertas, executa as que venceram, fecha o estado dos vídeos e ajusta as pastas.

- O contexto do banco (`BastDbContext`) fica em `Bast.Application`; as migrações, em `Bast.Infrastructure`.
- Um conector devolve `Published`, `Processing`, `Transient`, `Permanent`, `Unknown` ou, em consultas, `NotFound`. Só `NotFound` autoriza repetir uma operação incerta.
- Sem conector para uma plataforma, nada é agendado para ela.

## Importação (Etapa 1)

1. Pasta aparece em `01 - ENVIAR VERTICAL`; aguarda a janela de estabilidade.
2. Inspeção: vídeo, legenda, thumbnail. Problema: registra em `import_issues` e mantém a pasta.
3. Validação do vídeo. Inválido: move para `05 - ERROS`.
4. SHA-256 e verificação de duplicidade.
5. Grava `videos` com estado `Importing` e o destino previsto.
6. Move a pasta para `02 - AGENDADOS`.
7. Confirma no banco: estado `Ready`.

Queda entre os passos 5 e 7 é resolvida na inicialização por `ImportService.RecoverAsync`.
