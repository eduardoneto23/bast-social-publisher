<#
  Folha de contato de um trecho do vídeo, com o tempo de cada quadro escrito em cima,
  para achar o segundo exato da melhor cena (ação, close do personagem).
  Ex.: -From 1258 -To 1282 -Step 1 (um quadro por segundo) e depois -Step 0.25 em volta do escolhido.
  O tempo escrito é contado a partir de -From. Saída: caminho da folha.
#>
param(
    [Parameter(Mandatory)][string]$Video,
    [Parameter(Mandatory)][double]$From,
    [Parameter(Mandatory)][double]$To,
    [double]$Step = 1,
    [Parameter(Mandatory)][string]$Out,
    [int]$Columns = 6,
    [string]$Ffmpeg
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ferramentas.ps1')
$Ffmpeg = Resolve-Tool $Ffmpeg 'FfmpegPath' 'ffmpeg'
if ($To -le $From -or $Step -le 0) { throw 'Use -To maior que -From e -Step positivo.' }

$count = [Math]::Min(48, [Math]::Max(1, [Math]::Floor(($To - $From) / $Step + 1e-9)))
$Columns = [Math]::Min($Columns, $count)
$rows = [Math]::Ceiling($count / $Columns)
$inv = [Globalization.CultureInfo]::InvariantCulture
$rate = (1 / $Step).ToString($inv)
$font = 'C\:/Windows/Fonts/arialbd.ttf'
$filter = "fps=$rate,scale=480:-1,drawtext=text='%{pts\:hms}':x=8:y=8:fontsize=26:fontcolor=yellow:box=1:boxcolor=black@0.6:fontfile='$font',tile=${Columns}x$rows"

New-Item -ItemType Directory -Force (Split-Path $Out -Parent) | Out-Null
& $Ffmpeg -v error -y -ss $From.ToString($inv) -t ($To - $From).ToString($inv) -i $Video -vf $filter -frames:v 1 $Out
if (-not (Test-Path -LiteralPath $Out)) { throw 'A folha não foi criada.' }
$Out
