<#
  Extrai quadros candidatos do vídeo, em 1280x720, espalhados entre 8% e 92% da duração,
  e monta uma folha de contato numerada (4 colunas) para escolher o melhor quadro de uma vez.
  Saída: JSON com a duração, os quadros e a folha de contato.
#>
param(
    [Parameter(Mandatory)][string]$Video,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$Count = 12,
    [string]$Ffmpeg,
    [string]$Ffprobe
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ferramentas.ps1')
$Ffmpeg = Resolve-Tool $Ffmpeg 'FfmpegPath' 'ffmpeg'
$Ffprobe = Resolve-Tool $Ffprobe 'FfprobePath' 'ffprobe'

New-Item -ItemType Directory -Force $OutDir | Out-Null
$duration = [double](& $Ffprobe -v error -show_entries format=duration -of csv=p=0 $Video)
if ($duration -le 0) { throw "Não foi possível ler a duração do vídeo." }

$frames = @()
for ($i = 0; $i -lt $Count; $i++) {
    $t = $duration * (0.08 + 0.84 * $i / [Math]::Max(1, $Count - 1))
    $out = Join-Path $OutDir ('quadro-{0:D2}.jpg' -f ($i + 1))
    # Corta para 16:9 sem distorcer e grava em 1280x720.
    & $Ffmpeg -v error -y -ss $t.ToString([Globalization.CultureInfo]::InvariantCulture) -i $Video -frames:v 1 `
        -vf 'scale=1280:720:force_original_aspect_ratio=increase,crop=1280:720' -q:v 2 $out
    if (Test-Path $out) { $frames += [pscustomobject]@{ numero = $i + 1; segundos = [Math]::Round($t, 1); arquivo = $out } }
}

# Folha de contato com o número de cada quadro.
$sheet = Join-Path $OutDir 'folha.jpg'
$list = Join-Path $OutDir 'lista.txt'
$frames | ForEach-Object { "file '$($_.arquivo -replace '\\', '/')'" } | Set-Content -Encoding ascii $list
$cols = 4
$rows = [Math]::Ceiling($frames.Count / $cols)
& $Ffmpeg -v error -y -f concat -safe 0 -i $list `
    -vf "scale=480:270,drawtext=text='%{eif\:n+1\:d}':x=12:y=8:fontsize=44:fontcolor=yellow:box=1:boxcolor=black@0.6,tile=${cols}x${rows}:padding=6" `
    -frames:v 1 -q:v 3 $sheet

[pscustomobject]@{ duracao = [Math]::Round($duration, 1); quadros = $frames; folha = $sheet } | ConvertTo-Json -Depth 4
