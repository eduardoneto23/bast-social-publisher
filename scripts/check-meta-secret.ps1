# Confere o ID e a chave secreta do aplicativo da Meta direto na Meta, sem passar pelo servico.
# Nao guarda nada e nao mostra a chave: so informa o tamanho, o formato e se a Meta aceitou.
# Uso: powershell -ExecutionPolicy Bypass -File scripts\check-meta-secret.ps1
$ErrorActionPreference = 'Stop'

function Read-Secret($prompt) {
    $secure = Read-Host $prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

$appId = (Read-Host 'ID do aplicativo').Trim()
$secret = (Read-Secret 'Chave secreta do aplicativo').Trim()

Write-Host ''
Write-Host "ID: $($appId.Length) caracteres; so numeros: $($appId -match '^\d+$')"
Write-Host "Chave: $($secret.Length) caracteres (o esperado e 32); so letras a-f e numeros: $($secret -match '^[0-9a-f]+$')"

try {
    $body = @{ client_id = $appId; client_secret = $secret; grant_type = 'client_credentials' }
    $result = Invoke-RestMethod 'https://graph.facebook.com/oauth/access_token' -Method Post -Body $body
    if ($result.access_token) { Write-Host 'Resultado: a Meta ACEITOU o ID e a chave.' -ForegroundColor Green }
    else { Write-Host 'Resultado: resposta inesperada da Meta.' -ForegroundColor Yellow }
}
catch {
    $detail = $_.ErrorDetails.Message
    $message = try { ($detail | ConvertFrom-Json).error.message } catch { $_.Exception.Message }
    Write-Host "Resultado: a Meta RECUSOU: $message" -ForegroundColor Red
}
finally {
    $secret = $null; $body = $null; $result = $null
}
