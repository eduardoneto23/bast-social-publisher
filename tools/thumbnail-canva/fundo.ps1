<#
  Prepara o fundo da thumbnail a partir do quadro escolhido: 1280x720, um pouco mais de contraste
  e saturação, e escurecimento gradual na parte de baixo para o texto branco/amarelo do modelo
  ficar legível (o texto do modelo fica entre y=560 e y=720).
  -Remove "x,y,w,h" (em pixels do quadro 1280x720; várias áreas separadas por ";") cobre uma área
  com a cor de fundo escura (-RemoveColor, padrão preto) e bordas suaves nos lados que não encostam
  na borda da imagem. Use para tirar a webcam do usuário ou outro elemento sobreposto.
#>
param(
    [Parameter(Mandatory)][string]$Frame,
    [Parameter(Mandatory)][string]$Out,
    [string]$Remove,
    [string]$RemoveColor = 'black',
    [int]$Feather = 60,
    [string]$Ffmpeg
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ferramentas.ps1')
$Ffmpeg = Resolve-Tool $Ffmpeg 'FfmpegPath' 'ffmpeg'

$chain = '[0:v]scale=1280:720:force_original_aspect_ratio=increase,crop=1280:720,eq=contrast=1.08:saturation=1.15,format=rgb24[s0];'
$last = 's0'
$n = 0
if ($Remove) {
    foreach ($box in ($Remove -split ';' | Where-Object { $_.Trim() })) {
        $v = @($box -split ',' | ForEach-Object { [int]$_.Trim() })
        if ($v.Count -ne 4) { throw "Área inválida: $box (use x,y,w,h)" }
        if ($v[2] -lt 2 -or $v[3] -lt 2) { throw "Área inválida: $box" }
        # A área informada fica 100% coberta; a transição suave fica do lado de fora dela.
        $x = [Math]::Max(0, $v[0] - $Feather); $y = [Math]::Max(0, $v[1] - $Feather)
        $w = [Math]::Min($v[0] + $v[2] + $Feather, 1280) - $x; $h = [Math]::Min($v[1] + $v[3] + $Feather, 720) - $y
        if ($w -lt 2 -or $h -lt 2) { throw "Área fora do quadro: $box" }

        # Suaviza só os lados que ficam dentro da imagem; o lado que encosta na borda fica cheio.
        $terms = @()
        if ($x -gt 0) { $terms += "X/$Feather" }
        if ($x + $w -lt 1280) { $terms += "(W-1-X)/$Feather" }
        if ($y -gt 0) { $terms += "Y/$Feather" }
        if ($y + $h -lt 720) { $terms += "(H-1-Y)/$Feather" }
        $alpha = '255'
        if ($terms.Count -gt 0) {
            $m = $terms[0]
            for ($i = 1; $i -lt $terms.Count; $i++) { $m = "min($m,$($terms[$i]))" }
            $alpha = "255*clip($m,0,1)"
        }

        $n++
        $chain += "color=c=${RemoveColor}:s=${w}x${h},format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='$alpha'[m$n];[$last][m$n]overlay=x=${x}:y=${y}[s$n];"
        $last = "s$n"
    }
}

# Degradê contínuo (sem faixas visíveis): transparente até y=380, escurecendo até 90% em y=620 e mantido até o fim.
$chain += 'color=c=black:s=1280x720,format=rgba,geq=r=0:g=0:b=0:a=''if(lt(Y,380),0,min(230,(Y-380)*230/240))''[shade];' +
    "[$last][shade]overlay=format=auto,format=yuvj420p"

& $Ffmpeg -v error -y -i $Frame -filter_complex $chain -frames:v 1 -q:v 2 $Out
if (-not (Test-Path $Out)) { throw "O fundo não foi gerado." }
$Out
