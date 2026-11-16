# Teste de ponta a ponta da Etapa 2, em MODO DE SIMULACAO (nada e enviado a nenhuma rede).
# Sobe o servico real em uma pasta temporaria, agenda um video por minuto e acompanha a fila.
# Dura cerca de 6 minutos. Uso: powershell -ExecutionPolicy Bypass -File scripts\e2e-etapa2.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path $env:TEMP ("bast-e2e2-" + [guid]::NewGuid().ToString('N'))
$url = 'http://127.0.0.1:5082'
$dll = Join-Path $repo 'src\Bast.Service\bin\Debug\net10.0\Bast.Service.dll'
$failures = @()

function Check($name, $condition) {
    if ($condition) { Write-Host "OK    $name" } else { Write-Host "FALHA $name"; $script:failures += $name }
}

function Api($path, $method = 'Get', $body = $null) {
    if ($null -ne $body) { return @(Invoke-RestMethod "$url$path" -Method $method -Body ($body | ConvertTo-Json) -ContentType 'application/json') }
    $raw = (Invoke-WebRequest "$url$path" -Method $method -UseBasicParsing).Content
    return @($raw | ConvertFrom-Json | ForEach-Object { $_ })
}

function Start-Bast {
    $env:Bast__RootFolder = $root
    $env:Bast__StabilitySeconds = '3'
    $env:Bast__ScanIntervalSeconds = '2'
    $env:Bast__EngineIntervalSeconds = '2'
    $env:Bast__SimulationMode = 'true'
    $env:Urls = $url
    $p = Start-Process dotnet -ArgumentList "`"$dll`"" -WorkingDirectory (Split-Path $dll) -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $root "service-$([guid]::NewGuid().ToString('N')).log") -RedirectStandardError (Join-Path $root 'service.err.log')
    for ($i = 0; $i -lt 40; $i++) {
        try { Invoke-RestMethod "$url/api/health" | Out-Null; return $p } catch { Start-Sleep -Milliseconds 500 }
    }
    throw 'Servico nao respondeu.'
}

function New-VideoFolder($name, $seed, $caption) {
    $d = New-Item -ItemType Directory -Force (Join-Path $stage $name)
    ffmpeg -nostdin -v error -y -f lavfi -i "testsrc=size=540x960:rate=30:duration=6" -f lavfi -i "sine=frequency=${seed}:duration=6" `
        -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest -movflags +faststart (Join-Path $d 'video.mp4')
    [IO.File]::WriteAllText((Join-Path $d 'legenda.txt'), $caption, (New-Object Text.UTF8Encoding $false))
}

function Wait-Until($condition, $seconds) {
    for ($i = 0; $i -lt $seconds; $i++) { if (& $condition) { return $true }; Start-Sleep -Seconds 1 }
    return $false
}

function Published-Count { @((Api '/api/queue') | Where-Object { $_.state -eq 'Published' }).Count }

New-Item -ItemType Directory -Force $root | Out-Null
dotnet build (Join-Path $repo 'src\Bast.Service') -v q --nologo | Out-Null
$service = Start-Bast
try {
    $health = (Api '/api/health')[0]
    Check 'modo de simulacao ativo' ($health.simulationMode -eq $true)

    # Um horario por minuto, o dia todo, para o teste caber em poucos minutos.
    $settings = @{ startTime = '00:00'; endTime = '23:59'; intervalMinutes = 1; maxPerDay = 1440
        days = @('Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'); timeZoneId = 'America/Sao_Paulo'
        platforms = @('Instagram', 'Facebook', 'Threads', 'YouTube'); missedPolicy = 'NextAllowedSlot'; missedGraceMinutes = 30; maxAttempts = 5 }
    $saved = (Api '/api/settings/schedule' 'Put' $settings)[0]
    Check 'configuracao de agenda salva' ($saved.intervalMinutes -eq 1 -and $saved.platforms.Count -eq 4)
    $bad = $null
    try { Invoke-RestMethod "$url/api/settings/schedule" -Method Put -ContentType 'application/json' -Body (($settings + @{}) | ForEach-Object { $_.intervalMinutes = 0; $_ } | ConvertTo-Json) | Out-Null } catch { $bad = $_.Exception.Response.StatusCode.value__ }
    Check 'configuracao invalida recusada (HTTP 400)' ($bad -eq 400)
    $settings.intervalMinutes = 1

    Api '/api/queue/pause' 'Post' | Out-Null
    $stage = Join-Path $root 'stage'
    $special = "Descri" + [char]0xE7 + [char]0xE3 + "o " + [char]::ConvertFromUtf32(0x1F3AE)
    New-VideoFolder 'Video 10' 410 "TITULO DEZ`r`n`r`n$special`r`n#Bast"
    New-VideoFolder 'Video 2' 402 "TITULO DOIS`r`n`r`n$special`r`n#Bast"
    New-VideoFolder 'Video 001' 401 "TITULO UM`r`n`r`n$special`r`n#Bast"
    New-VideoFolder 'Video 30 longo' 430 ("TITULO LONGO`r`n" + ('a' * 600))
    Get-ChildItem $stage -Directory | Move-Item -Destination (Join-Path $root '01 - ENVIAR')

    Check 'quatro videos importados' (Wait-Until { (Api '/api/videos').Count -eq 4 } 40)
    Start-Sleep -Seconds 6
    Check 'fila pausada: nada agendado nem publicado' (@((Api '/api/queue') | Where-Object { $_.scheduledAtUtc }).Count -eq 0)

    Api '/api/queue/resume' 'Post' | Out-Null
    Check 'videos agendados apos retomar' (Wait-Until { @((Api '/api/queue') | Where-Object { $_.scheduledAtUtc }).Count -eq 4 } 20)
    $queue = Api '/api/queue'
    Check 'ordem numerica da fila (001, 2, 10, 30)' (($queue.folderName -join '|') -eq 'Video 001|Video 2|Video 10|Video 30 longo')
    $slots = $queue | ForEach-Object { [datetime]::Parse($_.scheduledAtUtc).ToUniversalTime() }
    $gaps = 1..3 | ForEach-Object { ($slots[$_] - $slots[$_ - 1]).TotalMinutes }
    Check 'horarios consecutivos de 1 em 1 minuto' (($gaps | Where-Object { $_ -ne 1 }).Count -eq 0)
    Check 'titulo do YouTube e a primeira linha' ($queue[0].youTubeTitle -eq 'TITULO UM')
    $long = $queue | Where-Object folderName -eq 'Video 30 longo'
    $threads = $long.publications | Where-Object platform -eq 'Threads'
    Check 'legenda longa barrada no Threads antes de publicar' ($threads.state -eq 'Failed' -and $threads.lastError -match '500' -and $threads.attemptCount -eq 0)
    Check 'mesma legenda aceita nas outras redes' (@($long.publications | Where-Object { $_.platform -ne 'Threads' -and $_.state -eq 'Scheduled' }).Count -eq 3)

    Check 'primeiro video publicado no horario' (Wait-Until { (Published-Count) -ge 1 } 90)
    Check 'apenas um video publicado por horario' ((Published-Count) -eq 1)

    # Queda no meio da fila: derruba o processo e sobe de novo.
    $slotsBefore = ((Api '/api/queue') | Sort-Object folderName | ForEach-Object { $_.scheduledAtUtc }) -join ','
    Stop-Process -Id $service.Id -Force
    Start-Sleep -Seconds 2
    $service = Start-Bast
    $slotsAfter = ((Api '/api/queue') | Sort-Object folderName | ForEach-Object { $_.scheduledAtUtc }) -join ','
    Check 'reinicio manteve os mesmos horarios' ($slotsBefore -eq $slotsAfter)

    Check 'fila concluida' (Wait-Until { @((Api '/api/queue') | Where-Object { $_.state -in 'Published', 'Error' }).Count -eq 4 } 240)
    Start-Sleep -Seconds 5
    $final = Api '/api/queue'
    $ok = $final | Where-Object { $_.folderName -ne 'Video 30 longo' }
    Check 'tres videos Published em ENVIADOS' (@($ok | Where-Object { $_.state -eq 'Published' -and $_.folder -eq 'Sent' }).Count -eq 3)
    Check 'pastas em 04 - ENVIADOS' ((Get-ChildItem (Join-Path $root '04 - ENVIADOS') -Directory).Count -eq 3)
    $pubs = $ok | ForEach-Object { $_.publications }
    Check 'doze publicacoes, uma tentativa cada, sem duplicar' ($pubs.Count -eq 12 -and @($pubs | Where-Object { $_.state -ne 'Published' -or $_.attemptCount -ne 1 }).Count -eq 0)
    Check 'todas marcadas como simuladas e sem link' (@($pubs | Where-Object { -not $_.simulated -or $_.externalUrl }).Count -eq 0)
    $starts = $ok | ForEach-Object { [datetime]::Parse(($_.publications | Select-Object -First 1).startedAtUtc) } | Sort-Object
    Check 'videos publicados em minutos diferentes' ((($starts[1] - $starts[0]).TotalSeconds -ge 30) -and (($starts[2] - $starts[1]).TotalSeconds -ge 30))
    $long = $final | Where-Object folderName -eq 'Video 30 longo'
    Check 'falha parcial: 3 redes publicadas, Threads falhou, video em ERROS' ($long.state -eq 'Error' -and $long.folder -eq 'Errors' -and @($long.publications | Where-Object state -eq 'Published').Count -eq 3)
    $caption = [IO.File]::ReadAllText((Join-Path $root '04 - ENVIADOS\Video 2\legenda.txt'))
    Check 'legenda original intacta' ($caption -eq "TITULO DOIS`r`n`r`n$special`r`n#Bast")
}
finally {
    Stop-Process -Id $service.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    if ($failures.Count -gt 0) { Write-Host '--- log do servico'; Get-ChildItem $root -Filter 'service-*.log' | ForEach-Object { Get-Content $_.FullName -Tail 40 } }
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) { Write-Host "`n$($failures.Count) verificacoes falharam."; exit 1 }
Write-Host "`nTodas as verificacoes passaram."
