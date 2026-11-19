<#
  Lista as pastas de "00 - THUMB" que precisam de thumbnail:
  um vídeo, um .txt, nenhuma imagem, e nada alterado nos últimos 60 segundos (cópia terminada).
  Pastas com THUMB-ERRO.txt são puladas até o usuário apagar esse arquivo.
  Saída: JSON com a lista (pasta, vídeo, legenda).
#>
param(
    [string]$Root,
    [int]$StableSeconds = 60
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ferramentas.ps1')
$Root = Resolve-Root $Root
$inbox = Join-Path $Root '00 - THUMB'
$videoExt = '.mp4', '.mov', '.m4v', '.webm'
$imageExt = '.jpg', '.jpeg', '.png'
$result = @()

if (Test-Path $inbox) {
    foreach ($dir in Get-ChildItem $inbox -Directory) {
        $files = Get-ChildItem $dir.FullName -File | Where-Object { -not ($_.Attributes -band [IO.FileAttributes]::Hidden) }
        if ($files | Where-Object { $_.Name -ieq 'THUMB-ERRO.txt' }) { continue }
        $videos = @($files | Where-Object { $videoExt -contains $_.Extension.ToLower() })
        $texts = @($files | Where-Object { $_.Extension -ieq '.txt' -and $_.Name -ine 'REIMPORTAR.txt' })
        $images = @($files | Where-Object { $imageExt -contains $_.Extension.ToLower() })
        if ($videos.Count -ne 1 -or $texts.Count -lt 1 -or $images.Count -gt 0) { continue }

        $newest = ($files | Measure-Object LastWriteTimeUtc -Maximum).Maximum
        if (((Get-Date).ToUniversalTime() - $newest).TotalSeconds -lt $StableSeconds) { continue }

        $caption = $texts | Where-Object { $_.Name -ieq 'legenda.txt' } | Select-Object -First 1
        if (-not $caption) { $caption = $texts[0] }
        $result += [pscustomobject]@{
            pasta   = $dir.FullName
            nome    = $dir.Name
            video   = $videos[0].FullName
            legenda = $caption.FullName
        }
    }
}

ConvertTo-Json -InputObject @($result) -Depth 3
