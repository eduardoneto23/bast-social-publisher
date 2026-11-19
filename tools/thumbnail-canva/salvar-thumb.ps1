<#
  Baixa a thumbnail exportada pelo Canva e grava como thumbnail.png na pasta do vídeo.
  Grava primeiro com outro nome e só renomeia no fim, para o BAST nunca ler um arquivo pela metade.
  Confere se é PNG de 1280x720 antes de colocar na pasta.
#>
param(
    [Parameter(Mandatory)][string]$Url,
    [Parameter(Mandatory)][string]$Folder,
    [string]$Ffprobe
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ferramentas.ps1')
$Ffprobe = Resolve-Tool $Ffprobe 'FfprobePath' 'ffprobe'

$final = Join-Path $Folder 'thumbnail.png'
if (Test-Path $final) { throw "A pasta já tem thumbnail.png; nada foi alterado." }

$temp = Join-Path ([IO.Path]::GetTempPath()) ("thumb-" + [Guid]::NewGuid().ToString('N') + '.png')
try {
    Invoke-WebRequest -Uri $Url -OutFile $temp -UseBasicParsing
    $size = & $Ffprobe -v error -select_streams v:0 -show_entries stream=codec_name,width,height -of csv=p=0 $temp
    if ($size -notmatch '^png,1280,720') { throw "O arquivo exportado não é um PNG 1280x720 ($size)." }
    if ((Get-Item $temp).Length -gt 2MB) { throw "A thumbnail passou de 2 MB, o limite do YouTube." }
    Move-Item $temp $final
    $final
}
finally {
    if (Test-Path $temp) { Remove-Item $temp -Force }
}
