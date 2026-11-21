<#
  Baixa a thumbnail exportada pelo Canva e grava na pasta do vídeo:
  - horizontais (pasta em 00 - THUMB): thumbnail.png, PNG 1280x720;
  - verticais (pasta em 00 - THUMB VERTICAL): thumbnail.jpg, JPG 1080x1920 (capa dos Reels e Shorts).
  Grava primeiro com outro nome e só renomeia no fim, para o BAST nunca ler um arquivo pela metade.
  Confere formato, tamanho e o limite de 2 MB do YouTube antes de colocar na pasta. Nunca sobrescreve.
#>
param(
    [Parameter(Mandatory)][string]$Url,
    [Parameter(Mandatory)][string]$Folder,
    [string]$Ffprobe
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ferramentas.ps1')
$Ffprobe = Resolve-Tool $Ffprobe 'FfprobePath' 'ffprobe'
$lane = Get-LaneOf (Resolve-Root '') (Get-Item -LiteralPath $Folder).FullName

$existing = Get-ChildItem -LiteralPath $Folder -File | Where-Object { [IO.Path]::GetFileNameWithoutExtension($_.Name) -ieq 'thumbnail' }
if ($existing) { throw "A pasta já tem $($existing[0].Name); nada foi alterado." }
$final = Join-Path $Folder $lane.File

$temp = Join-Path ([IO.Path]::GetTempPath()) ("thumb-" + [Guid]::NewGuid().ToString('N') + [IO.Path]::GetExtension($lane.File))
try {
    Invoke-WebRequest -Uri $Url -OutFile $temp -UseBasicParsing
    $size = & $Ffprobe -v error -select_streams v:0 -show_entries stream=codec_name,width,height -of csv=p=0 $temp
    $expected = "$($lane.Codec),$($lane.Width),$($lane.Height)"
    if ($size -notmatch "^$expected") { throw "O arquivo exportado não é $($lane.File) em $($lane.Width)x$($lane.Height) ($size)." }
    if ((Get-Item $temp).Length -gt 2MB) { throw "A thumbnail passou de 2 MB, o limite do YouTube." }
    Move-Item $temp $final
    $final
}
finally {
    if (Test-Path $temp) { Remove-Item $temp -Force }
}
