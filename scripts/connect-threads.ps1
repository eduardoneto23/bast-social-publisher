# Conecta a conta do Threads pelo login oficial (OAuth).
# Pede o ID e a chave secreta do app do Threads (Configuracoes do app > Basico, campos "Threads")
# e o endereco de retorno cadastrado no app. Abre a autorizacao no navegador; depois de autorizar,
# cole o endereco que aparecer na barra do navegador.
# Uso: powershell -ExecutionPolicy Bypass -File scripts\connect-threads.ps1
param([string]$Url = 'http://127.0.0.1:5080')
$ErrorActionPreference = 'Stop'

function Read-Secret($prompt) {
    $secure = Read-Host $prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

$status = Invoke-RestMethod "$Url/api/connect/threads/status"
if ($status.connected) {
    Write-Host "Ja conectado ao Threads: @$($status.username)."
    if ((Read-Host 'Conectar de novo? (s/N)') -ne 's') { exit 0 }
}

if (-not $status.hasAppCredentials -or (Read-Host 'Informar novas credenciais do app do Threads? (s/N)') -eq 's') {
    Write-Host 'Use o ID e a chave do THREADS (nao os do app principal da Meta).'
    $appId = Read-Host 'ID do app do Threads'
    $secret = Read-Secret 'Chave secreta do app do Threads'
    $secret = $secret.Trim()
    if ($secret -notmatch '^[0-9a-fA-F]{32}$') {
        $secret = $null
        Write-Host 'A chave secreta tem 32 caracteres (numeros e letras de a a f). Clique em "Mostrar" no painel antes de copiar; os pontinhos nao servem.'
        exit 1
    }
    # Confere a chave direto com a Meta (pedido de token do app, documentado pelo Threads) antes de guardar.
    try {
        Invoke-RestMethod ("https://graph.threads.net/oauth/access_token?grant_type=client_credentials&client_id=" +
            [uri]::EscapeDataString($appId.Trim()) + "&client_secret=" + [uri]::EscapeDataString($secret)) | Out-Null
    } catch {
        $secret = $null
        Write-Host 'A Meta recusou o ID ou a chave do app do Threads. Confira se sao os campos "ID do app do Threads" e "Chave secreta do app do Threads".'
        exit 1
    }
    Write-Host 'Chave do Threads conferida com a Meta.'
    $redirect = Read-Host 'Endereco de retorno (https, igual ao cadastrado no app)'
    $body = @{ appId = $appId; appSecret = $secret; redirectUri = $redirect } | ConvertTo-Json
    $secret = $null
    try {
        Invoke-RestMethod "$Url/api/connect/threads/credentials" -Method Put -ContentType 'application/json' -Body $body | Out-Null
    } catch {
        $body = $null
        Write-Host "Credenciais recusadas: $($_.ErrorDetails.Message)"
        exit 1
    }
    $body = $null
    Write-Host 'Credenciais do app do Threads guardadas.'
}

$start = Invoke-RestMethod "$Url/api/connect/threads/start" -Method Post
if (-not $start.ok) { Write-Host $start.message; exit 1 }

Write-Host ''
Write-Host 'Abrindo a autorizacao do Threads no navegador. Entre com a sua conta e clique em Permitir.'
Write-Host 'O navegador vai abrir a sua pagina de retorno com um codigo no endereco.'
Start-Process $start.authorizeUrl
Write-Host ''
$redirected = Read-Host 'Cole aqui o endereco completo da barra do navegador'

$body = @{ redirectedUrl = $redirected } | ConvertTo-Json
$result = Invoke-RestMethod "$Url/api/connect/threads/code" -Method Post -ContentType 'application/json' -Body $body
$body = $null
$redirected = $null
Write-Host $result.message
if (-not $result.ok) { exit 1 }
