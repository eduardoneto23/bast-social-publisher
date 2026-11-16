# Conectar Facebook e Instagram

Os dois usam o mesmo aplicativo da Meta e a mesma conexão. O sistema publica **Reels** em uma **Página do Facebook** e na **conta profissional do Instagram** vinculada a essa Página.

> Estado: os conectores estão implementados e testados contra um servidor falso que segue a documentação oficial. **Ainda não foram exercitados contra a API real.** Os nomes de menus do painel da Meta abaixo foram escritos de memória e podem estar diferentes.

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

## O que falta confirmar no primeiro uso real

- Se um app em modo Ao vivo, sem revisão da Meta, publica para as suas próprias contas com conteúdo visível ao público.
- Se as chamadas do Instagram aceitam o token da Página (a documentação cita token de Página em um trecho e de usuário em outro).
- O formato real do link (`permalink_url`) devolvido para Reels do Facebook.

## Desconectar

```
Invoke-RestMethod http://127.0.0.1:5080/api/connect/meta -Method Delete
```
