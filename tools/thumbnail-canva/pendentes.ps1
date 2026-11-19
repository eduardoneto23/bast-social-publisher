<#
  Lista as pastas de "00 - THUMB" que ainda precisam de algo antes de ir para o YouTube:
  um vídeo sem thumbnail, ou sem .txt (temThumbnail = true: o usuário já pôs a imagem, falta só a legenda), nada alterado nos últimos 60 segundos (cópia terminada).
  Se a pasta não tem .txt, "legenda" vem nula e "tituloTrabalho" traz o título que o usuário deu
  (nome do arquivo de vídeo ou, se for genérico como "video", o nome da pasta): a legenda deve ser criada.
  Pastas com THUMB-ERRO.txt são puladas até o usuário apagar esse arquivo.
  Saída: JSON com a lista.
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
$generic = '^(video|vídeo|final|export|render|untitled|sem t[ií]tulo|novo)[\s_\-]*\d*$'
$result = @()

if (Test-Path $inbox) {
    foreach ($dir in Get-ChildItem $inbox -Directory) {
        $files = Get-ChildItem -LiteralPath $dir.FullName -File | Where-Object { -not ($_.Attributes -band [IO.FileAttributes]::Hidden) }
        if ($files | Where-Object { $_.Name -ieq 'THUMB-ERRO.txt' }) { continue }
        $videos = @($files | Where-Object { $videoExt -contains $_.Extension.ToLower() })
        $texts = @($files | Where-Object { $_.Extension -ieq '.txt' -and $_.Name -ine 'REIMPORTAR.txt' })
        $images = @($files | Where-Object { $imageExt -contains $_.Extension.ToLower() })
        # Com imagem e com .txt já está completa (o mover-para-youtube.ps1 cuida). Com imagem e sem .txt falta só a legenda.
        if ($videos.Count -ne 1 -or ($images.Count -gt 0 -and $texts.Count -gt 0)) { continue }

        $newest = ($files | Measure-Object LastWriteTimeUtc -Maximum).Maximum
        if (((Get-Date).ToUniversalTime() - $newest).TotalSeconds -lt $StableSeconds) { continue }
        # A cópia do Windows mantém a data do original: um arquivo ainda em uso indica cópia em andamento.
        if (Test-InUse $files) { continue }

        $caption = $null
        if ($texts.Count -gt 0) {
            $caption = $texts | Where-Object { $_.Name -ieq 'legenda.txt' } | Select-Object -First 1
            if (-not $caption) { $caption = $texts[0] }
        }

        # Título de trabalho: nome da pasta e do arquivo juntos, sem repetir o que um já contém.
        $fileTitle = [IO.Path]::GetFileNameWithoutExtension($videos[0].Name).Trim()
        $folderTitle = $dir.Name.Trim()
        if ($fileTitle -match $generic -or $folderTitle.ToLower().Contains($fileTitle.ToLower())) { $working = $folderTitle }
        elseif ($fileTitle.ToLower().Contains($folderTitle.ToLower())) { $working = $fileTitle }
        else { $working = "$folderTitle - $fileTitle" }

        $result += [pscustomobject]@{
            pasta          = $dir.FullName
            nome           = $dir.Name
            video          = $videos[0].FullName
            legenda        = if ($caption) { $caption.FullName } else { $null }
            tituloTrabalho = $working
            temThumbnail   = $images.Count -gt 0
        }
    }
}

ConvertTo-Json -InputObject @($result) -Depth 3
