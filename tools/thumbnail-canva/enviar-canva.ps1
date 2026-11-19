<#
  Envia um arquivo para o Canva pelo endereço de upload de uso único devolvido pela ferramenta
  create-upload-url do Canva (corpo = bytes crus, application/octet-stream). Imprime a resposta,
  que traz o identificador do arquivo enviado.
#>
param(
    [Parameter(Mandatory)][string]$UploadUrl,
    [Parameter(Mandatory)][string]$File
)

$ErrorActionPreference = 'Stop'
$response = Invoke-WebRequest -Uri $UploadUrl -Method Post -InFile $File -ContentType 'application/octet-stream' -UseBasicParsing
$response.Content
