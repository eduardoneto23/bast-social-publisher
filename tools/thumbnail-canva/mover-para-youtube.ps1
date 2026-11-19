<#
  Move para "01 - ENVIAR YOUTUBE" as pastas de "00 - THUMB" que já estão completas:
  um vídeo, um .txt e a imagem da thumbnail, sem THUMB-ERRO.txt e paradas há pelo menos
  $StableSeconds segundos. A pasta inteira vai de uma vez (mesmo disco = movimento instantâneo),
  então o BAST nunca vê a pasta sem a thumbnail. Nome repetido no destino ganha " (2)", " (3)"...
  Com -Folder, move só aquela pasta (usado logo depois de salvar a thumbnail).
  Saída: uma linha "origem -> destino" por pasta movida.
#>
param(
    [string]$Root,
    [string]$Folder,
    [int]$StableSeconds = 60
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ferramentas.ps1')
$Root = Resolve-Root $Root
$source = Join-Path $Root '00 - THUMB'
$target = Join-Path $Root '01 - ENVIAR YOUTUBE'
$videoExt = '.mp4', '.mov', '.m4v', '.webm'
$imageExt = '.jpg', '.jpeg', '.png'
New-Item -ItemType Directory -Force $target | Out-Null

$candidates = if ($Folder) { @(Get-Item -LiteralPath $Folder) } elseif (Test-Path $source) { @(Get-ChildItem $source -Directory) } else { @() }

foreach ($dir in $candidates) {
    # Segurança: só pastas que estão diretamente dentro de 00 - THUMB.
    if ((Split-Path $dir.FullName -Parent).TrimEnd('\') -ine $source.TrimEnd('\')) { throw "A pasta não está em 00 - THUMB: $($dir.FullName)" }

    $files = Get-ChildItem -LiteralPath $dir.FullName -File
    if ($files | Where-Object { $_.Name -ieq 'THUMB-ERRO.txt' }) { continue }
    $videos = @($files | Where-Object { $videoExt -contains $_.Extension.ToLower() })
    $texts = @($files | Where-Object { $_.Extension -ieq '.txt' })
    $images = @($files | Where-Object { $imageExt -contains $_.Extension.ToLower() })
    if ($videos.Count -ne 1 -or $texts.Count -lt 1 -or $images.Count -lt 1) { continue }

    if (-not $Folder) {
        $newest = ($files | Measure-Object LastWriteTimeUtc -Maximum).Maximum
        if (((Get-Date).ToUniversalTime() - $newest).TotalSeconds -lt $StableSeconds) { continue }
    }
    if (Test-InUse $files) { continue }

    $destination = Join-Path $target $dir.Name
    for ($i = 2; Test-Path -LiteralPath $destination; $i++) { $destination = Join-Path $target "$($dir.Name) ($i)" }

    Move-Item -LiteralPath $dir.FullName -Destination $destination
    "$($dir.FullName) -> $destination"
}
