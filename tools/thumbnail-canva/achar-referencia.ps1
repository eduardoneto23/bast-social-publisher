<#
  Acha no vídeo o momento que a imagem de referência mostra e extrai esse quadro em qualidade cheia.
  1. Varredura: compara a referência com o vídeo a cada 0,5 s (imagens reduzidas a 64x36, tons de cinza).
  2. Refino: em volta do melhor ponto (±1 s), compara quadro a quadro na taxa original do vídeo.
  3. Extrai o quadro escolhido na resolução do vídeo (original.png) e em 1280x720 (quadro-referencia.jpg),
     com o mesmo enquadramento 16:9 dos outros scripts.
  "diferenca" é a diferença média de brilho (0 = idêntico, 255 = oposto). "confiavel" = diferença até
  -Threshold: acima disso a referência provavelmente não é deste vídeo (ou foi recortada/editada) e
  a própria imagem de referência deve ser usada.
  Saída: JSON.
#>
param(
    [Parameter(Mandatory)][string]$Video,
    [Parameter(Mandatory)][string]$Reference,
    [Parameter(Mandatory)][string]$OutDir,
    [double]$Threshold = 14,
    [string]$Ffmpeg,
    [string]$Ffprobe
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ferramentas.ps1')
$Ffmpeg = Resolve-Tool $Ffmpeg 'FfmpegPath' 'ffmpeg'
$Ffprobe = Resolve-Tool $Ffprobe 'FfprobePath' 'ffprobe'
New-Item -ItemType Directory -Force $OutDir | Out-Null

$W = 64; $H = 36; $size = $W * $H
$frame = 'scale=1280:720:force_original_aspect_ratio=increase,crop=1280:720'
$small = "$frame,scale=${W}:${H}:flags=area,format=gray"
$inv = [Globalization.CultureInfo]::InvariantCulture

function Read-Raw([string]$path) { if (Test-Path $path) { [IO.File]::ReadAllBytes($path) } else { [byte[]]@() } }

if (-not ('BastFrameMatch' -as [type])) {
    Add-Type -TypeDefinition @'
public static class BastFrameMatch {
    // Diferença média absoluta entre a referência e cada quadro (todos do mesmo tamanho, em sequência).
    public static double[] Scores(byte[] all, byte[] reference) {
        int size = reference.Length, count = all.Length / size;
        var scores = new double[count];
        for (int f = 0; f < count; f++) {
            long sum = 0; int b = f * size;
            for (int i = 0; i < size; i++) { int d = all[b + i] - reference[i]; sum += d < 0 ? -d : d; }
            scores[f] = (double)sum / size;
        }
        return scores;
    }
}
'@
}

function Compare-Frames([byte[]]$all, [byte[]]$ref) { return ,([BastFrameMatch]::Scores($all, $ref)) }

# Referência reduzida.
$refRaw = Join-Path $OutDir 'ref.raw'
& $Ffmpeg -v error -y -i $Reference -vf $small -frames:v 1 -f rawvideo $refRaw
$ref = Read-Raw $refRaw
if ($ref.Length -ne $size) { throw "Não foi possível ler a imagem de referência." }

# 1. Varredura a cada 0,5 s.
$coarseRaw = Join-Path $OutDir 'varredura.raw'
& $Ffmpeg -v error -y -i $Video -an -vf "fps=2,$small" -f rawvideo $coarseRaw
$coarse = Compare-Frames (Read-Raw $coarseRaw) $ref
if ($coarse.Length -eq 0) { throw "Não foi possível ler os quadros do vídeo." }
$bestCoarse = 0
for ($f = 1; $f -lt $coarse.Length; $f++) { if ($coarse[$f] -lt $coarse[$bestCoarse]) { $bestCoarse = $f } }
$tCoarse = $bestCoarse / 2.0

# 2. Refino quadro a quadro em volta do melhor ponto.
$rate = (& $Ffprobe -v error -select_streams v:0 -show_entries stream=avg_frame_rate -of csv=p=0 $Video).Trim()
$fps = 30.0
if ($rate -match '^(\d+)/(\d+)$' -and [double]$Matches[2] -gt 0) { $fps = [double]$Matches[1] / [double]$Matches[2] }
$start = [Math]::Max(0, $tCoarse - 1.0)
$fineRaw = Join-Path $OutDir 'refino.raw'
& $Ffmpeg -v error -y -ss $start.ToString($inv) -i $Video -t 2 -an -vf $small -f rawvideo $fineRaw
$fine = Compare-Frames (Read-Raw $fineRaw) $ref
$best = $tCoarse; $bestScore = $coarse[$bestCoarse]
for ($f = 0; $f -lt $fine.Length; $f++) {
    if ($fine[$f] -lt $bestScore) { $bestScore = $fine[$f]; $best = $start + $f / $fps }
}

# 3. Quadro em qualidade cheia.
$original = Join-Path $OutDir 'original.png'
$out = Join-Path $OutDir 'quadro-referencia.jpg'
& $Ffmpeg -v error -y -ss $best.ToString($inv) -i $Video -frames:v 1 $original
& $Ffmpeg -v error -y -i $original -vf $frame -q:v 2 $out
Remove-Item $refRaw, $coarseRaw, $fineRaw -ErrorAction SilentlyContinue

[pscustomobject]@{
    segundos   = [Math]::Round($best, 2)
    diferenca  = [Math]::Round($bestScore, 1)
    confiavel  = $bestScore -le $Threshold
    quadro     = $out
    original   = $original
} | ConvertTo-Json
