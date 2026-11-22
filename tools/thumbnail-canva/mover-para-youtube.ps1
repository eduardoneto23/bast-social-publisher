<#
  Move para "01 - ENVIAR YOUTUBE" as pastas de "00 - THUMB" (com -Vertical: de "00 - THUMB VERTICAL" para
  "01 - ENVIAR VERTICAL") que já estão completas:
  um vídeo, um .txt e a imagem da thumbnail, sem THUMB-ERRO.txt e paradas há pelo menos
  $StableSeconds segundos. A pasta inteira vai de uma vez (mesmo disco = movimento instantâneo),
  então o BAST nunca vê a pasta sem a thumbnail. Nome repetido no destino ganha " (2)", " (3)"...
  Com -Folder, move só aquela pasta (usado logo depois de salvar a thumbnail).
  Saída: uma linha "origem -> destino" por pasta movida.
#>
param(
    [string]$Root,
    [string]$Folder,
    [int]$StableSeconds = 60,
    [switch]$Vertical
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ferramentas.ps1')
$Root = Resolve-Root $Root
$lane = if ($Folder) { Get-LaneOf $Root (Get-Item -LiteralPath $Folder).FullName } else { Get-Lane -Vertical:$Vertical }
$source = Join-Path $Root $lane.Source
$target = Join-Path $Root $lane.Target
$videoExt = '.mp4', '.mov', '.m4v', '.webm'
$imageExt = '.jpg', '.jpeg', '.png'
New-Item -ItemType Directory -Force $target | Out-Null

$candidates = if ($Folder) { @(Get-Item -LiteralPath $Folder) } elseif (Test-Path $source) { @(Get-ChildItem $source -Directory) } else { @() }

foreach ($dir in $candidates) {
    # Segurança: só pastas que estão diretamente dentro de 00 - THUMB.
    if ((Split-Path $dir.FullName -Parent).TrimEnd('\') -ine $source.TrimEnd('\')) { throw "A pasta não está em $($lane.Source): $($dir.FullName)" }

    $files = Get-ChildItem -LiteralPath $dir.FullName -File
    if ($files | Where-Object { $_.Name -ieq 'THUMB-ERRO.txt' }) { continue }
    $videos = @($files | Where-Object { $videoExt -contains $_.Extension.ToLower() })
    $texts = @($files | Where-Object { $_.Extension -ieq '.txt' })
    $images = @($files | Where-Object { $imageExt -contains $_.Extension.ToLower() -and -not (Test-Reference $_) })
    if ($videos.Count -ne 1 -or $texts.Count -lt 1 -or ($lane.NeedsThumbnail -and $images.Count -lt 1)) { continue }

    # Com a referência na pasta, o BAST só acha a thumbnail certa se ela se chamar thumbnail.*.
    $hasReference = @($files | Where-Object { $imageExt -contains $_.Extension.ToLower() -and (Test-Reference $_) }).Count -gt 0
    if ($hasReference -and $images.Count -gt 0 -and -not ($images | Where-Object { [IO.Path]::GetFileNameWithoutExtension($_.Name) -ieq 'thumbnail' })) {
        "AVISO: $($dir.Name) não foi movida: com referencia na pasta, a thumbnail precisa se chamar thumbnail.png ou thumbnail.jpg."
        continue
    }

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
