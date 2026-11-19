<#
  Prepara o fundo da thumbnail a partir do quadro escolhido: 1280x720, um pouco mais de contraste
  e saturação, e escurecimento gradual na parte de baixo para o texto branco/amarelo do modelo
  ficar legível (o texto do modelo fica entre y=560 e y=720).
#>
param(
    [Parameter(Mandatory)][string]$Frame,
    [Parameter(Mandatory)][string]$Out,
    [string]$Ffmpeg
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ferramentas.ps1')
$Ffmpeg = Resolve-Tool $Ffmpeg 'FfmpegPath' 'ffmpeg'

$filter = @(
    'scale=1280:720:force_original_aspect_ratio=increase,crop=1280:720',
    'eq=contrast=1.08:saturation=1.15',
    # Faixa escura em degraus: suave em cima, mais forte onde fica o texto.
    'drawbox=x=0:y=440:w=1280:h=40:color=black@0.12:t=fill',
    'drawbox=x=0:y=480:w=1280:h=40:color=black@0.24:t=fill',
    'drawbox=x=0:y=520:w=1280:h=40:color=black@0.38:t=fill',
    'drawbox=x=0:y=560:w=1280:h=160:color=black@0.55:t=fill'
) -join ','

& $Ffmpeg -v error -y -i $Frame -vf $filter -frames:v 1 -q:v 2 $Out
if (-not (Test-Path $Out)) { throw "O fundo não foi gerado." }
$Out
