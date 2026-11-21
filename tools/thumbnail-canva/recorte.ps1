<#
  Tira um quadro do vídeo em resolução cheia, recorta só a área útil (sem a webcam e sem o HUD
  do jogo) e melhora cor, contraste e nitidez. Recortar em vez de tapar: nada de caixa preta.
  -Crop "w:h:x:y" em pixels do vídeo original (ex.: 1920x1080).
  -Fundo: o recorte vira o fundo da thumbnail (1280x720, deve ser 16:9) com a faixa escura embaixo
          para o texto. Com -Vertical, o fundo da capa dos verticais (1080x1920, recorte 9:16).
          Sem -Fundo: sai o recorte em tamanho original (para o personagem, que depois
          tem o fundo removido no Canva).
  Saída: caminho do PNG.
#>
param(
    [Parameter(Mandatory)][string]$Video,
    [Parameter(Mandatory)][double]$Second,
    [Parameter(Mandatory)][string]$Crop,
    [Parameter(Mandatory)][string]$Out,
    [switch]$Fundo,
    [switch]$Vertical,
    [string]$Ffmpeg
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ferramentas.ps1')
$Ffmpeg = Resolve-Tool $Ffmpeg 'FfmpegPath' 'ffmpeg'
if ($Crop -notmatch '^\d+:\d+:\d+:\d+$') { throw 'Use -Crop "largura:altura:x:y".' }
$w, $h = $Crop.Split(':')[0..1] | ForEach-Object { [double]$_ }
$inv = [Globalization.CultureInfo]::InvariantCulture
$look = 'eq=contrast=1.12:saturation=1.3:brightness=0.01,unsharp=5:5:0.7'

New-Item -ItemType Directory -Force (Split-Path $Out -Parent) | Out-Null
if ($Fundo -and $Vertical) {
    if ([Math]::Abs($w / $h - 9 / 16) -gt 0.02) { throw "O recorte do fundo vertical precisa ser 9:16 (está ${w}x$h)." }
    # Faixa escura na parte de baixo (a partir de ~62% da altura), onde fica o texto da capa.
    $gradient = "color=black:s=1080x1920,format=rgba,geq=r=0:g=0:b=0:a='if(lt(Y,1180),0,min(235,(Y-1180)*235/420))'"
    & $Ffmpeg -v error -y -ss $Second.ToString($inv) -i $Video -frames:v 1 -filter_complex "[0:v]crop=$Crop,scale=1080:1920:flags=lanczos,$look[b];$gradient[g];[b][g]overlay" $Out
} elseif ($Fundo) {
    if ([Math]::Abs($w / $h - 16 / 9) -gt 0.03) { throw "O recorte do fundo precisa ser 16:9 (está ${w}x$h)." }
    $gradient = "color=black:s=1280x720,format=rgba,geq=r=0:g=0:b=0:a='if(lt(Y,420),0,min(235,(Y-420)*235/200))'"
    & $Ffmpeg -v error -y -ss $Second.ToString($inv) -i $Video -frames:v 1 -filter_complex "[0:v]crop=$Crop,scale=1280:720:flags=lanczos,$look[b];$gradient[g];[b][g]overlay" $Out
} else {
    & $Ffmpeg -v error -y -ss $Second.ToString($inv) -i $Video -frames:v 1 -vf "crop=$Crop,$look" $Out
}
if (-not (Test-Path -LiteralPath $Out)) { throw 'O recorte não foi criado.' }
$Out
