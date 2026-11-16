# Conecta a conta do YouTube ao BAST SOCIAL PUBLISHER.
# Pede o Client ID e o Client Secret do Google Cloud, abre o navegador para a autorizacao
# e aguarda a conclusao. As credenciais vao direto para o servico local, que as guarda criptografadas.
# Uso: powershell -ExecutionPolicy Bypass -File scripts\connect-youtube.ps1
#      ou, com o arquivo JSON baixado do Google Cloud (nao precisa digitar nada):
#      powershell -ExecutionPolicy Bypass -File scripts\connect-youtube.ps1 -ClientSecretFile "C:\caminho\client_secret_....json"
param([string]$Url = 'http://127.0.0.1:5080', [string]$ClientSecretFile)
$ErrorActionPreference = 'Stop'

if ($ClientSecretFile) {
    $json = (Get-Content -Raw -LiteralPath $ClientSecretFile | ConvertFrom-Json).installed
    if (-not $json.client_id -or -not $json.client_secret) {
        throw 'O arquivo nao e de um cliente OAuth do tipo "App para computador" (falta a secao "installed").'
    }
    $body = @{ clientId = $json.client_id; clientSecret = $json.client_secret } | ConvertTo-Json
    Invoke-RestMethod "$Url/api/connect/youtube/credentials" -Method Put -ContentType 'application/json' -Body $body | Out-Null
    $body = $null; $json = $null
    Write-Host 'Credenciais do arquivo guardadas (criptografadas) no servico.'
}

$status = Invoke-RestMethod "$Url/api/connect/youtube/status"
if ($status.connected) {
    Write-Host 'O YouTube ja esta conectado. Para trocar de conta, desconecte antes:'
    Write-Host "  Invoke-RestMethod $Url/api/connect/youtube -Method Delete"
    exit 0
}

if (-not $ClientSecretFile -and (-not $status.hasClientCredentials -or (Read-Host 'Informar novas credenciais do Google Cloud? (s/N)') -eq 's')) {
    $clientId = Read-Host 'Client ID'
    $secure = Read-Host 'Client Secret' -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $body = @{ clientId = $clientId; clientSecret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) } | ConvertTo-Json
        Invoke-RestMethod "$Url/api/connect/youtube/credentials" -Method Put -ContentType 'application/json' -Body $body | Out-Null
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
        $body = $null
    }
    Write-Host 'Credenciais guardadas.'
}

$start = Invoke-RestMethod "$Url/api/connect/youtube/start" -Method Post
Write-Host 'Abrindo o navegador para a autorizacao do Google...'
Start-Process $start.authorizationUrl

for ($i = 0; $i -lt 300; $i++) {
    Start-Sleep -Seconds 2
    $status = Invoke-RestMethod "$Url/api/connect/youtube/status"
    if ($status.connected -or $status.flow -notmatch '^Aguardando') { break }
}

Write-Host $status.flow
if (-not $status.connected) { exit 1 }
