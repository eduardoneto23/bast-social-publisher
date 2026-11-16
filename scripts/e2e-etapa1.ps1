# Teste de ponta a ponta da Etapa 1: sobe o serviço real em uma pasta temporária,
# coloca pastas em 01 - ENVIAR e confere o resultado pela API e pelo disco.
# Uso: pwsh scripts/e2e-etapa1.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path $env:TEMP ("bast-e2e-" + [guid]::NewGuid().ToString('N'))
$url = 'http://127.0.0.1:5081'
$dll = Join-Path $repo 'src\Bast.Service\bin\Debug\net10.0\Bast.Service.dll'
$failures = @()
# "Descricao" com cedilha, til e um emoji, montados por codigo para o script nao depender da codificacao do arquivo.
$special = "Descri" + [char]0xE7 + [char]0xE3 + "o " + [char]::ConvertFromUtf32(0x1F3AE)

function Check($name, $condition) {
    if ($condition) { Write-Host "OK    $name" } else { Write-Host "FALHA $name"; $script:failures += $name }
}

function Start-Bast {
    $env:Bast__RootFolder = $root
    $env:Bast__StabilitySeconds = '3'
    $env:Bast__ScanIntervalSeconds = '2'
    $env:Urls = $url
    $p = Start-Process dotnet -ArgumentList "`"$dll`"" -WorkingDirectory (Split-Path $dll) -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $root 'service.log') -RedirectStandardError (Join-Path $root 'service.err.log')
    for ($i = 0; $i -lt 40; $i++) {
        try { Invoke-RestMethod "$url/api/health" | Out-Null; return $p } catch { Start-Sleep -Milliseconds 500 }
    }
    throw 'Servico nao respondeu.'
}

function New-Video($path, $seed) {
    ffmpeg -nostdin -v error -y -f lavfi -i "testsrc=size=540x960:rate=30:duration=6" -f lavfi -i "sine=frequency=${seed}:duration=6" `
        -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest -movflags +faststart $path
}

function Wait-Until($condition, $seconds = 40) {
    for ($i = 0; $i -lt $seconds * 2; $i++) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 500 }
    return $false
}

New-Item -ItemType Directory -Force $root | Out-Null
dotnet build (Join-Path $repo 'src\Bast.Service') -v q --nologo | Out-Null
$service = Start-Bast
try {
    $inbox = Join-Path $root '01 - ENVIAR'
    $health = Invoke-RestMethod "$url/api/health"
    Check 'health ok (banco, ffprobe, ffmpeg)' ($health.status -eq 'ok')
    Check 'estrutura de pastas criada' ((Get-ChildItem $root -Directory).Count -eq 7)

    # Monta as pastas fora de ENVIAR e move de uma vez, como o usuário faria.
    $stage = Join-Path $root 'stage'
    foreach ($n in 'Video 10', 'Video 2', 'Video 001') {
        $d = New-Item -ItemType Directory -Force (Join-Path $stage $n)
        New-Video (Join-Path $d 'video.mp4') (400 + [int]($n -replace '\D'))
        Set-Content (Join-Path $d 'legenda.txt') "TITULO $n`r`n`r`n$special`r`n#Bast" -Encoding utf8
    }
    $noTxt = New-Item -ItemType Directory -Force (Join-Path $stage 'Video 4 sem txt')
    New-Video (Join-Path $noTxt 'video.mp4') 900
    $corrupt = New-Item -ItemType Directory -Force (Join-Path $stage 'Video 5 corrompido')
    $bytes = [IO.File]::ReadAllBytes((Join-Path $stage 'Video 2\video.mp4'))
    [IO.File]::WriteAllBytes((Join-Path $corrupt 'video.mp4'), $bytes[0..([int]($bytes.Length * 0.6))])
    Set-Content (Join-Path $corrupt 'legenda.txt') 'legenda' -Encoding utf8
    $dup = New-Item -ItemType Directory -Force (Join-Path $stage 'Video 30 copia do 2')
    Copy-Item (Join-Path $stage 'Video 2\*') $dup
    Get-ChildItem $stage -Directory | Move-Item -Destination $inbox

    $done = Wait-Until { (Invoke-RestMethod "$url/api/videos").Count -eq 3 -and (Invoke-RestMethod "$url/api/import-issues").Count -ge 3 }
    Check 'três vídeos válidos importados' $done
    $videos = Invoke-RestMethod "$url/api/videos"
    Check 'ordem numérica (001, 2, 10)' (($videos.folderName -join '|') -eq 'Video 001|Video 2|Video 10')
    Check 'todos Ready em AGENDADOS' (@($videos | Where-Object { $_.state -ne 'Ready' -or $_.folder -ne 'Scheduled' }).Count -eq 0)
    Check 'metadados lidos (540x960 h264/aac)' (@($videos | Where-Object { $_.width -ne 540 -or $_.height -ne 960 -or $_.videoCodec -ne 'h264' -or $_.audioCodec -ne 'aac' }).Count -eq 0)
    Check 'pastas movidas para 02 - AGENDADOS' ((Get-ChildItem (Join-Path $root '02 - AGENDADOS') -Directory).Count -eq 3)
    $issues = Invoke-RestMethod "$url/api/import-issues"
    Check 'pasta sem TXT sinalizada e mantida em ENVIAR' ((@($issues | Where-Object code -eq 'MissingCaption').Count -eq 1) -and (Test-Path (Join-Path $inbox 'Video 4 sem txt')))
    Check 'vídeo corrompido encaminhado para 05 - ERROS' ((@($issues | Where-Object code -eq 'CorruptVideo').Count -eq 1) -and (Test-Path (Join-Path $root '05 - ERROS\Video 5 corrompido')))
    Check 'cópia com outro nome detectada como duplicata' ((@($issues | Where-Object code -eq 'Duplicate').Count -eq 1) -and (Test-Path (Join-Path $inbox 'Video 30 copia do 2')))
    $hashOnDisk = (Get-FileHash (Join-Path $root '02 - AGENDADOS\Video 2\video.mp4') -Algorithm SHA256).Hash.ToLower()
    Check 'SHA-256 do banco confere com o arquivo' (($videos | Where-Object folderName -eq 'Video 2').contentHash -eq $hashOnDisk)
    $caption = [IO.File]::ReadAllText((Join-Path $root '02 - AGENDADOS\Video 2\legenda.txt'))
    Check 'legenda original intacta' ($caption.Contains("$special`r`n#Bast"))

    # Reinício: derruba o processo sem aviso e sobe de novo.
    Stop-Process -Id $service.Id -Force
    Start-Sleep -Seconds 1
    $service = Start-Bast
    Start-Sleep -Seconds 8
    $after = Invoke-RestMethod "$url/api/videos"
    Check 'após reinício: mesmos 3 vídeos, sem duplicar' (($after.Count -eq 3) -and (($after.id | Sort-Object) -join ',') -eq (($videos.id | Sort-Object) -join ','))

    # Correção da pasta sem TXT e reimportação intencional.
    Set-Content (Join-Path $inbox 'Video 4 sem txt\legenda.txt') 'Agora tem legenda' -Encoding utf8
    Set-Content (Join-Path $inbox 'Video 30 copia do 2\REIMPORTAR.txt') '' -Encoding utf8
    $done = Wait-Until { (Invoke-RestMethod "$url/api/videos").Count -eq 5 }
    Check 'pasta corrigida e reimportação intencional importadas' $done
    $final = Invoke-RestMethod "$url/api/videos"
    Check 'reimportação registrada como geração 2' (($final | Where-Object folderName -eq 'Video 30 copia do 2').generation -eq 2)
    Check 'ENVIAR vazia ao final' ((Get-ChildItem $inbox -Directory).Count -eq 0)
    Check 'unico problema em aberto e o video corrompido em ERROS' (@(@(Invoke-RestMethod "$url/api/import-issues") | Where-Object { $_.code -ne 'CorruptVideo' }).Count -eq 0)
}
finally {
    Stop-Process -Id $service.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    if ($failures.Count -gt 0) { Write-Host "--- log do servico"; Get-Content (Join-Path $root 'service.log') -Tail 40 }
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) { Write-Host "`n$($failures.Count) verificacoes falharam."; exit 1 }
Write-Host "`nTodas as verificacoes passaram."
