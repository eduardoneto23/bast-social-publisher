<#
  Confere e grava a legenda criada para o vídeo como legenda.txt (UTF-8) na pasta.
  O rascunho vem de um arquivo (-Draft), para não perder acentos, emojis e quebras de linha.
  Regras: 1ª linha = título (até 55 caracteres, sem #, sem lista de assuntos); a última linha não vazia tem exatamente
  5 hashtags (#palavra, sem espaços, sem repetir); total até 5000 bytes (limite da descrição do YouTube) e,
  nos verticais (pasta em 00 - THUMB VERTICAL, que vão também para o Threads), até 500 caracteres.
  Nunca sobrescreve uma legenda que já existe na pasta.
#>
param(
    [Parameter(Mandatory)][string]$Folder,
    [Parameter(Mandatory)][string]$Draft
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ferramentas.ps1')
$root = (Resolve-Root '').TrimEnd('\')
$lane = Get-LaneOf $root (Get-Item -LiteralPath $Folder).FullName

if (@(Get-ChildItem -LiteralPath $Folder -File -Filter *.txt | Where-Object { $_.Name -ine 'REIMPORTAR.txt' -and $_.Name -ine 'THUMB-ERRO.txt' }).Count -gt 0) {
    throw "A pasta já tem legenda; nada foi alterado."
}

$text = [IO.File]::ReadAllText($Draft, [Text.Encoding]::UTF8).Replace("`r`n", "`n").Trim()
$lines = $text -split "`n"
$title = $lines[0].Trim()
$problems = @()
if ($title.Length -eq 0) { $problems += 'A 1ª linha (título) está vazia.' }
if ($title.Length -gt 55) { $problems += "O título tem $($title.Length) caracteres; o máximo é 55 (título curto, gancho e não resumo)." }
if (($title.Split(',').Count - 1) -ge 1 -and $title -match '\se\s') { $problems += 'O título lista vários assuntos; fale de UMA coisa só.' }
if (($title.Split(':').Count - 1) -gt 1) { $problems += 'O título tem mais de um dois-pontos.' }
if ($title.Contains('#')) { $problems += 'O título não deve ter hashtags.' }
if ($title -cmatch '^[^a-záéíóúâêôãõç]*$' -and $title.Length -gt 12) { $problems += 'O título está todo em MAIÚSCULAS.' }

$last = ($lines | Where-Object { $_.Trim() } | Select-Object -Last 1).Trim()
$tags = @($last -split '\s+' | Where-Object { $_ })
if ($tags.Count -ne 5) { $problems += "A última linha deve ter exatamente 5 hashtags (tem $($tags.Count) itens)." }
foreach ($tag in $tags) { if ($tag -notmatch '^#[\p{L}\p{N}_]+$') { $problems += "Hashtag inválida: $tag" } }
if (($tags | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique).Count -ne $tags.Count) { $problems += 'Há hashtag repetida.' }
if ([Text.Encoding]::UTF8.GetByteCount($text) -gt 5000) { $problems += 'A legenda passa de 5000 bytes.' }
# Verticais vão também para o Threads, que aceita no máximo 500 caracteres.
if ($lane.Name -eq 'vertical' -and $text.Length -gt 500) { $problems += "A legenda tem $($text.Length) caracteres; nos verticais o máximo é 500 (limite do Threads)." }
if ($problems.Count -gt 0) { throw ($problems -join ' ') }

$final = Join-Path $Folder 'legenda.txt'
[IO.File]::WriteAllText($final, ($text -replace "`n", "`r`n") + "`r`n", [Text.UTF8Encoding]::new($false))
$final
