<#
  Tira um quadro em resolução cheia, recorta só a área útil (sem a webcam e sem o HUD do jogo) e
  melhora cor, contraste e nitidez. Recortar em vez de tapar: nada de caixa preta.

  Origem: -Video + -Second (um momento do vídeo) ou -Image (um print do jogo/OBS em boa qualidade,
  como a referencia.png: rende mais que qualquer quadro do vídeo comprimido).

  Quadro mais nítido: no vídeo, o script não usa o quadro exato de -Second. Ele mede a nitidez
  (variância do laplaciano, só dentro do recorte) de todos os quadros em -Second ± -Window segundos
  e usa o mais nítido, que é o que tem menos borrão de movimento e de compressão. Só aceita quadros
  que mostram a mesma cena dentro do recorte (diferença média até -MaxShift em 255 em relação ao
  quadro de -Second), para o personagem não sair do enquadramento. -Window 0 desliga.

  -Crop "w:h:x:y" em pixels da origem (ex.: 1920x1080).
  -Fundo: o recorte vira o fundo da thumbnail (1280x720, deve ser 16:9) com a faixa escura embaixo
          para o texto. Com -Vertical, o fundo da capa dos verticais (1080x1920, recorte 9:16).
          Sem -Fundo: sai o recorte em tamanho original (para o personagem, que depois tem o fundo
          removido no Canva). Informe -TargetHeight (altura que o personagem vai ter na thumbnail)
          para o script calcular a ampliação.
  Ampliação com IA: se o recorte precisa crescer e o Real-ESRGAN está em SYSTEM\tools\realesrgan, ele
  é ampliado pela IA (rosto, barba, texto e bordas ficam nítidos), misturado com a ampliação comum
  (-AiStrength, padrão 0,5: a IA pura inventa textura em padrões finos) e o personagem já sai no
  tamanho de -TargetHeight. -NoAi desliga. Limite de ampliação: 2,5x com a IA, 1,2x sem ela; acima disso o JSON
  traz "aviso" e a tarefa deve escolher um recorte maior (um close, o personagem mais perto).

  Saída: JSON com arquivo, segundo usado, ganho de nitidez, tamanho do recorte, ampliação, ia e aviso.
#>
param(
    [string]$Video,
    [double]$Second,
    [string]$Image,
    [Parameter(Mandatory)][string]$Crop,
    [Parameter(Mandatory)][string]$Out,
    [switch]$Fundo,
    [switch]$Vertical,
    [double]$Window = 0.25,
    [double]$MaxShift = 6,
    [int]$TargetHeight = 0,
    [switch]$NoAi,
    [ValidateRange(0.0, 1.0)][double]$AiStrength = 0.5,
    [string]$Ffmpeg,
    [string]$Ffprobe
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ferramentas.ps1')
$Ffmpeg = Resolve-Tool $Ffmpeg 'FfmpegPath' 'ffmpeg'
if ($Crop -notmatch '^\d+:\d+:\d+:\d+$') { throw 'Use -Crop "largura:altura:x:y".' }
if (-not $Video -and -not $Image) { throw 'Informe -Video (com -Second) ou -Image.' }
$w, $h = $Crop.Split(':')[0..1] | ForEach-Object { [double]$_ }
$inv = [Globalization.CultureInfo]::InvariantCulture
New-Item -ItemType Directory -Force (Split-Path $Out -Parent) | Out-Null

if (-not ('BastSharpness2' -as [type])) {
    Add-Type -TypeDefinition @'
public static class BastSharpness2 {
    // Diferença média de brilho entre cada quadro e o quadro de referência (0 = idêntico).
    public static double[] Distance(byte[] all, int size, int reference) {
        int count = all.Length / size; var d = new double[count]; int r = reference * size;
        for (int f = 0; f < count; f++) {
            long sum = 0; int b = f * size;
            for (int i = 0; i < size; i++) { int x = all[b + i] - all[r + i]; sum += x < 0 ? -x : x; }
            d[f] = (double)sum / size;
        }
        return d;
    }

    // Variância do laplaciano de cada quadro (tons de cinza, todos w x h, em sequência).
    // Quanto maior, mais detalhe nítido; borrão de movimento e de compressão derrubam o valor.
    public static double[] Scores(byte[] all, int w, int h) {
        int size = w * h, count = all.Length / size;
        var scores = new double[count];
        for (int f = 0; f < count; f++) {
            int b = f * size; double sum = 0, sumSq = 0; long n = 0;
            for (int y = 1; y < h - 1; y++) {
                int row = b + y * w;
                for (int x = 1; x < w - 1; x++) {
                    int i = row + x;
                    double lap = all[i - w] + all[i + w] + all[i - 1] + all[i + 1] - 4.0 * all[i];
                    sum += lap; sumSq += lap * lap; n++;
                }
            }
            double mean = sum / n;
            scores[f] = sumSq / n - mean * mean;
        }
        return scores;
    }
}
'@
}

# 1. Escolhe o quadro mais nítido em volta do momento pedido (só no vídeo).
$used = $Second; $gain = 1.0
if ($Video -and $Window -gt 0) {
    $Ffprobe = Resolve-Tool $Ffprobe 'FfprobePath' 'ffprobe'
    $rate = (& $Ffprobe -v error -select_streams v:0 -show_entries stream=avg_frame_rate -of csv=p=0 $Video).Trim()
    $fps = 30.0
    if ($rate -match '^(\d+)/(\d+)$' -and [double]$Matches[2] -gt 0) { $fps = [double]$Matches[1] / [double]$Matches[2] }

    # Mede numa cópia reduzida (até 540 px de altura): rápido e com a mesma ordem de nitidez.
    $scale = [Math]::Min(1.0, 540.0 / $h)
    $sw = [int]([Math]::Round($w * $scale / 2) * 2); $sh = [int]([Math]::Round($h * $scale / 2) * 2)
    $start = [Math]::Max(0, $Second - $Window)
    $raw = Join-Path ([IO.Path]::GetTempPath()) ("nitidez-" + [Guid]::NewGuid().ToString('N') + '.raw')
    try {
        & $Ffmpeg -v error -y -ss $start.ToString($inv) -i $Video -t ($Second - $start + $Window).ToString($inv) -an `
            -vf "crop=$Crop,scale=${sw}:${sh}:flags=area,format=gray" -f rawvideo $raw
        $bytes = if (Test-Path $raw) { [IO.File]::ReadAllBytes($raw) } else { [byte[]]@() }
    }
    finally {
        Remove-Item $raw -ErrorAction SilentlyContinue
    }

    $scores = [BastSharpness2]::Scores($bytes, $sw, $sh)
    if ($scores.Length -gt 0) {
        $center = [Math]::Min($scores.Length - 1, [int][Math]::Round(($Second - $start) * $fps))
        # Só vale um quadro que mostra a mesma cena dentro do recorte: se o personagem ou a câmera já se
        # mexeram, o recorte planejado sairia errado (ex.: pegaria só o boné).
        $distance = [BastSharpness2]::Distance($bytes, $sw * $sh, $center)
        $best = $center
        for ($i = 0; $i -lt $scores.Length; $i++) {
            if ($distance[$i] -le $MaxShift -and $scores[$i] -gt $scores[$best]) { $best = $i }
        }
        $used = $start + $best / $fps
        if ($scores[$center] -gt 0) { $gain = $scores[$best] / $scores[$center] }
    }
}

# 2. Recorte final em qualidade cheia, a partir do quadro escolhido (ou da imagem).
$look = 'eq=contrast=1.12:saturation=1.3:brightness=0.01,unsharp=5:5:0.7'
# Personagem: tira um pouco do ruído de compressão antes de afiar, para o recorte do Canva ficar limpo.
$lookCutout = 'hqdn3d=1.2:1.2:0:0,eq=contrast=1.1:saturation=1.25:brightness=0.01,unsharp=5:5:0.6'
# Depois da IA a imagem já está nítida e limpa: só cor e contraste.
$lookAi = 'eq=contrast=1.1:saturation=1.25:brightness=0.01'
$source = if ($Image) { @('-i', $Image) } else { @('-ss', $used.ToString($inv), '-i', $Video) }

if ($Fundo -and $Vertical) {
    if ([Math]::Abs($w / $h - 9 / 16) -gt 0.02) { throw "O recorte do fundo vertical precisa ser 9:16 (está ${w}x$h)." }
    $zoom = 1920.0 / $h
} elseif ($Fundo) {
    if ([Math]::Abs($w / $h - 16 / 9) -gt 0.03) { throw "O recorte do fundo precisa ser 16:9 (está ${w}x$h)." }
    $zoom = 720.0 / $h
} else {
    $zoom = if ($TargetHeight -gt 0) { $TargetHeight / $h } else { $null }
}

# Ampliação com IA: quando o recorte precisa crescer e o Real-ESRGAN está instalado (e não foi desligado
# com -NoAi), o recorte é ampliado 4x pela IA e só depois reduzido ao tamanho final.
$upscaler = if ($NoAi) { $null } else { Resolve-Upscaler }
$useAi = $upscaler -and $zoom -and $zoom -gt 1.05
if ($useAi) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("recorte-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $tmp | Out-Null
    try {
        $plain = Join-Path $tmp 'recorte.png'; $big = Join-Path $tmp 'ia.png'
        & $Ffmpeg -v error -y @source -frames:v 1 -vf "crop=$Crop" $plain
        $run = Start-Process -FilePath $upscaler -ArgumentList @('-i', "`"$plain`"", '-o', "`"$big`"", '-n', 'realesrgan-x4plus', '-s', '4') `
            -WorkingDirectory (Split-Path $upscaler) -NoNewWindow -Wait -PassThru `
            -RedirectStandardError (Join-Path $tmp 'ia.log') -RedirectStandardOutput (Join-Path $tmp 'ia.out')
        if ($run.ExitCode -ne 0 -or -not (Test-Path $big)) { $useAi = $false }
        else { $source = @('-i', $big, '-i', $plain) }
    }
    catch { $useAi = $false }
}

# Imagem base no tamanho final ("[b]"). Com a IA: mistura a versão da IA com a ampliação comum
# (-AiStrength = peso da IA). A IA sozinha inventa textura em padrões finos (telas, grama); a mistura
# mantém a nitidez de olhos, rostos e bordas sem esse aspecto artificial.
function Base([string]$size, [string]$finish) {
    if ($useAi) {
        $mix = $AiStrength.ToString($inv)
        return "[0:v]scale=${size}:flags=lanczos[ia];[1:v]scale=${size}:flags=lanczos[co];[ia][co]blend=all_mode=normal:all_opacity=$mix,$lookAi[b]"
    }
    return "[0:v]crop=$Crop,scale=${size}:flags=lanczos,$finish[b]"
}

if ($Fundo -and $Vertical) {
    # Faixa escura na parte de baixo (a partir de ~62% da altura), onde fica o texto da capa.
    $gradient = "color=black:s=1080x1920,format=rgba,geq=r=0:g=0:b=0:a='if(lt(Y,1180),0,min(235,(Y-1180)*235/420))'"
    & $Ffmpeg -v error -y @source -frames:v 1 -filter_complex "$(Base '1080:1920' $look);$gradient[g];[b][g]overlay" $Out
} elseif ($Fundo) {
    $gradient = "color=black:s=1280x720,format=rgba,geq=r=0:g=0:b=0:a='if(lt(Y,420),0,min(235,(Y-420)*235/200))'"
    & $Ffmpeg -v error -y @source -frames:v 1 -filter_complex "$(Base '1280:720' $look);$gradient[g];[b][g]overlay" $Out
} elseif ($useAi) {
    # Personagem já no tamanho final da thumbnail (o Canva usa 1:1, sem ampliar de novo).
    & $Ffmpeg -v error -y @source -frames:v 1 -filter_complex (Base "-2:$TargetHeight" $lookAi) -map '[b]' $Out
} else {
    & $Ffmpeg -v error -y @source -frames:v 1 -vf "crop=$Crop,$lookCutout" $Out
}
if ($tmp) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
if (-not (Test-Path -LiteralPath $Out)) { throw 'O recorte não foi criado.' }

# Sem IA, acima de 1,2x a imagem fica mole; com a IA, até 2,5x fica bom.
$limit = if ($useAi) { 2.5 } else { 1.2 }
$warning = $null
if ($zoom -and $zoom -gt $limit) {
    $warning = "O recorte vai ser ampliado $([Math]::Round($zoom, 2))x (limite $limit) e vai perder nitidez. Escolha um recorte maior (personagem mais perto, um close) ou use um print do jogo em -Image."
}

[pscustomobject]@{
    arquivo   = $Out
    segundo   = if ($Image) { $null } else { [Math]::Round($used, 3) }
    ganho     = [Math]::Round($gain, 2)
    largura   = [int]$w
    altura    = [int]$h
    ampliacao = if ($zoom) { [Math]::Round($zoom, 2) } else { $null }
    ia        = [bool]$useAi
    aviso     = $warning
} | ConvertTo-Json
