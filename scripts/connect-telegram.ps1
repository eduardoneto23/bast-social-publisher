# Conecta os avisos a um bot do Telegram criado por voce no @BotFather.
# Pede o token do bot e vincula a sua conversa por um codigo. O token vai direto para o servico
# local, que o guarda criptografado.
# Uso: powershell -ExecutionPolicy Bypass -File scripts\connect-telegram.ps1
param([string]$Url = 'http://127.0.0.1:5080')
$ErrorActionPreference = 'Stop'

function Read-Secret($prompt) {
    $secure = Read-Host $prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

$status = Invoke-RestMethod "$Url/api/connect/telegram/status"
if ($status.connected) {
    Write-Host "Ja conectado: bot @$($status.bot), conversa de $($status.chat)."
    if ((Read-Host 'Conectar de novo? (s/N)') -ne 's') { exit 0 }
}

if (-not $status.hasToken -or (Read-Host 'Informar um novo token do bot? (s/N)') -eq 's') {
    $body = @{ token = (Read-Secret 'Token do bot (recebido do @BotFather)') } | ConvertTo-Json
    $result = Invoke-RestMethod "$Url/api/connect/telegram/token" -Method Put -ContentType 'application/json' -Body $body
    $body = $null
    Write-Host $result.message
    if (-not $result.ok) { exit 1 }
    $status = Invoke-RestMethod "$Url/api/connect/telegram/status"
}

$code = Get-Random -Minimum 100000 -Maximum 999999
Write-Host ''
Write-Host "1. Abra o Telegram e a conversa com o seu bot: @$($status.bot)"
Write-Host "2. Envie para ele esta mensagem: $code"
Write-Host ''

while ($true) {
    Read-Host 'Depois de enviar, aperte Enter' | Out-Null
    $body = @{ code = "$code" } | ConvertTo-Json
    $result = Invoke-RestMethod "$Url/api/connect/telegram/link" -Method Post -ContentType 'application/json' -Body $body
    Write-Host $result.message
    if ($result.ok) { break }
    if ((Read-Host 'Tentar de novo? (S/n)') -eq 'n') { exit 1 }
}

Write-Host 'Uma mensagem de confirmacao deve ter chegado no Telegram.'
