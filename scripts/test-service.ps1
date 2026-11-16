# Instala o servico e verifica: inicio automatico, resposta da API e reinicio automatico apos queda.
# Executar como Administrador. O resultado fica em service-test.log na raiz do repositorio.
#Requires -RunAsAdministrator
$repo = Split-Path $PSScriptRoot -Parent
$log = Join-Path $repo 'service-test.log'
$name = 'BastSocialPublisher'
$url = 'http://127.0.0.1:5080/api/health'
$lines = New-Object System.Collections.Generic.List[string]

function Check($label, $condition) {
    $lines.Add(("{0} {1}" -f $(if ($condition) { 'OK   ' } else { 'FALHA' }), $label))
}

function Wait-Health($seconds) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        try { return Invoke-RestMethod $url -TimeoutSec 3 } catch { Start-Sleep -Milliseconds 500 }
    }
    return $null
}

function Service-Pid { (Get-CimInstance Win32_Service -Filter "Name='$name'").ProcessId }

try {
    $output = & (Join-Path $PSScriptRoot 'install-service.ps1') 2>&1 | Out-String
    $lines.Add('--- instalacao'); $lines.Add($output.Trim()); $lines.Add('--- verificacoes')

    $svc = Get-CimInstance Win32_Service -Filter "Name='$name'"
    Check 'servico instalado e em execucao' ($svc.State -eq 'Running')
    Check 'inicio automatico (atrasado)' ($svc.StartMode -eq 'Auto' -and $svc.DelayedAutoStart)
    $lines.Add("      conta do servico: $($svc.StartName)")

    $health = Wait-Health 30
    Check 'API responde' ($null -ne $health)
    Check 'health ok (banco, ffprobe, ffmpeg)' ($health.status -eq 'ok')
    $lines.Add("      pasta principal: $($health.rootFolder)")
    Check 'estrutura de pastas criada' ((Test-Path (Join-Path $health.rootFolder '01 - ENVIAR')) -and (Test-Path (Join-Path $health.rootFolder 'SYSTEM\bast.db')))

    # Queda: mata o processo sem aviso; o Windows deve reiniciar o servico em 5 segundos.
    $before = Service-Pid
    Stop-Process -Id $before -Force
    Start-Sleep -Seconds 2
    $down = $null; try { $down = Invoke-RestMethod $url -TimeoutSec 2 } catch { }
    Check 'processo derrubado (API fora do ar)' ($null -eq $down)
    $health = Wait-Health 40
    $after = Service-Pid
    Check 'Windows reiniciou o servico sozinho apos a queda' (($null -ne $health) -and ($after -gt 0) -and ($after -ne $before))
    Check 'health ok apos o reinicio' ($health.status -eq 'ok')
}
catch {
    $lines.Add("ERRO: $($_.Exception.Message)")
}

$lines.Add('--- fim')
[IO.File]::WriteAllLines($log, $lines)
