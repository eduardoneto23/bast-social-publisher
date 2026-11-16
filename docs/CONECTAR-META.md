# Conectar Facebook e Instagram

Os dois usam o mesmo aplicativo da Meta e a mesma conexão. O sistema publica **Reels** em uma **Página do Facebook** e na **conta profissional do Instagram** vinculada a essa Página.

> Estado: primeiro envio real feito em 16/11/2026, com um vídeo de teste de 6 segundos: Reel publicado no Facebook em cerca de 40 segundos e no Instagram em cerca de 1 minuto e 40, uma tentativa em cada rede, com o app em modo de desenvolvimento. Ainda não exercitados contra a API real: retomada de upload interrompido, limites de uso e token recusado. Os nomes de menus do painel da Meta abaixo foram escritos de memória e podem estar diferentes.

## Antes de começar

- O Instagram precisa ser uma conta **profissional** (Criador de conteúdo ou Empresa).
- Ela precisa estar **vinculada a uma Página do Facebook** que você administra. Perfil pessoal do Facebook não é aceito pela API.
- Se a Página tiver "Autorização de publicação da Página" pendente, conclua antes.

## 1. Criar o aplicativo

1. Acesse https://developers.facebook.com, registre-se como desenvolvedor e crie um aplicativo do tipo **Empresa**.
2. Em **Configurações do app > Básico**, anote o **ID do aplicativo** e a **Chave secreta**, e preencha a **URL da Política de Privacidade**.
3. Coloque o aplicativo em modo **Ao vivo**. Em modo de desenvolvimento, o que o app publica só é visto por quem tem função no app.

## 2. Gerar o token

1. Abra o **Explorador da Graph API**: https://developers.facebook.com/tools/explorer
2. Selecione o seu aplicativo e escolha **Token do usuário**.
3. Adicione as permissões:
   - `pages_show_list`
   - `pages_read_engagement`
   - `pages_manage_posts`
   - `instagram_basic`
   - `instagram_content_publish`
   - `business_management` (necessária quando a Página está em um portfólio empresarial)
4. Clique em **Gerar token de acesso**, escolha a Página e a conta do Instagram na janela de autorização e copie o token.

Esse token dura cerca de uma hora. O sistema o troca na hora por um token da Página, que não tem data de expiração.

## 3. Conectar

```
powershell -ExecutionPolicy Bypass -File scripts\connect-meta.ps1
```

O script pede o ID do aplicativo, a chave secreta e o token. Se a conta administrar mais de uma Página, ele lista as Páginas e pergunta qual usar.

Conferir: http://127.0.0.1:5080/api/accounts

## Como a publicação funciona

**Facebook:** abre o envio do Reel, envia o arquivo, pede a publicação e consulta o estado até a Meta informar `published`. Se a internet cair no meio, o envio continua dos bytes que a Meta já guardou.

**Instagram:** cria um contêiner, envia o arquivo, espera o processamento (consulta a cada minuto) e então pede a publicação, uma única vez. Se a resposta desse pedido se perder, o sistema não pede de novo: fica consultando até a Meta confirmar, para não publicar em dobro.

## Limites

| | Facebook (Reels) | Instagram (Reels) |
|---|---|---|
| Duração | 3 a 90 segundos | 3 segundos a 15 minutos |
| Formato | vertical 9:16, mínimo 540x960, 24 a 60 fps | vertical 9:16 recomendado |
| Tamanho | não informado na documentação | até 300 MB |
| Legenda | sem limite conferido | 2.200 caracteres, 30 hashtags, 20 menções |
| Por dia | 30 Reels | 50 ou 100 (a documentação se contradiz) |

Vídeo fora desses limites é barrado antes do horário só para aquela rede; as outras seguem.

## Quando a conexão cai

Troca de senha, remoção do app ou perda de função na Página invalidam o token. O sistema marca Facebook e Instagram como "precisa reconectar", segura as publicações sem gastar tentativas e retoma quando você rodar o script de novo com um token novo.

## Dicas do painel da Meta

- Na criação do app, marque só os casos de uso **Gerenciar tudo na sua Página** e **Gerenciar mensagens e conteúdo no Instagram**. Marcar os outros faz o painel pedir verificação de empresa e análise do app, que não são necessárias para publicar nas próprias contas.
- `pages_manage_posts` e `instagram_content_publish` só aparecem no Explorador depois de adicionadas dentro de cada caso de uso (**Casos de uso > Personalizar**).
- A chave secreta tem 32 caracteres (números e letras de a a f) e fica em **Configurações do app > Básico > Mostrar**. Não confunda com a chave do app do Threads, na mesma tela. Para conferir sem conectar: `scripts\check-meta-secret.ps1`.
- A autenticação de dois fatores exigida pela Meta é ativada pela Central de Contas (accountscenter.facebook.com); a tela antiga aberta pelo painel pode dar erro.

## Confirmado no primeiro uso real

- As chamadas do Instagram aceitam o token da Página.
- O Facebook devolve o link no formato `https://www.facebook.com/reel/<id>/`.
- Em modo de desenvolvimento, o administrador do app consegue conectar e publicar.

## O que falta confirmar

- Quem enxerga os Reels publicados com o app em modo de desenvolvimento.
- Se o modo Ao vivo é liberado sem verificação de empresa.

## Desconectar

```
Invoke-RestMethod http://127.0.0.1:5080/api/connect/meta -Method Delete
```
