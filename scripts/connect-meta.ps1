# Conecta a Pagina do Facebook e o Instagram profissional vinculado a ela.
# Pede o ID e a chave secreta do aplicativo da Meta e o token gerado no Explorador da Graph API.
# Tudo vai direto para o servico local, que guarda as credenciais criptografadas.
# Uso: powershell -ExecutionPolicy Bypass -File scripts\connect-meta.ps1
param([string]$Url = 'http://127.0.0.1:5080')
$ErrorActionPreference = 'Stop'

function Read-Secret($prompt) {
    $secure = Read-Host $prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

$status = Invoke-RestMethod "$Url/api/connect/meta/status"
if ($status.facebookConnected) {
    Write-Host "Ja conectado: Pagina `"$($status.page)`"; Instagram: $(if ($status.instagram) { '@' + $status.instagram } else { 'nao vinculado' })."
    if ((Read-Host 'Conectar de novo com outro token? (s/N)') -ne 's') { exit 0 }
}

if (-not $status.hasAppCredentials -or (Read-Host 'Informar novas credenciais do aplicativo da Meta? (s/N)') -eq 's') {
    $appId = Read-Host 'ID do aplicativo'
    $body = @{ appId = $appId; appSecret = (Read-Secret 'Chave secreta do aplicativo') } | ConvertTo-Json
    Invoke-RestMethod "$Url/api/connect/meta/credentials" -Method Put -ContentType 'application/json' -Body $body | Out-Null
    $body = $null
    Write-Host 'Credenciais do aplicativo guardadas.'
}

$token = Read-Secret 'Token de acesso do usuario (Explorador da Graph API)'
$pageId = $null
while ($true) {
    $body = @{ userToken = $token; pageId = $pageId } | ConvertTo-Json
    $result = Invoke-RestMethod "$Url/api/connect/meta/token" -Method Post -ContentType 'application/json' -Body $body
    $body = $null
    Write-Host $result.message
    if ($result.connected) { break }
    if (-not $result.pages -or @($result.pages).Count -eq 0) { $token = $null; exit 1 }

    Write-Host ''
    foreach ($page in $result.pages) {
        $ig = if ($page.hasInstagram) { "Instagram @$($page.instagramUsername)" } else { 'sem Instagram vinculado' }
        Write-Host "  $($page.id)  $($page.name)  ($ig)"
    }
    $pageId = Read-Host 'ID da Pagina a usar'
}
$token = $null
