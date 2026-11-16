# Instala o BAST SOCIAL PUBLISHER como servico do Windows.
# Executar em um PowerShell aberto como Administrador:
#   powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1
#
# O servico inicia junto com o Windows (inicio automatico atrasado) e e reiniciado
# automaticamente pelo Windows se o processo cair.
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$name = 'BastSocialPublisher'
$repo = Split-Path $PSScriptRoot -Parent
$target = Join-Path $repo 'publish'
$local = Join-Path $repo 'src\Bast.Service\appsettings.Local.json'

if (-not (Test-Path $local)) {
    throw "Crie $local com Bast:RootFolder, Bast:FfprobePath e Bast:FfmpegPath antes de instalar (veja README.md)."
}

if (Get-Service $name -ErrorAction SilentlyContinue) {
    Stop-Service $name -ErrorAction SilentlyContinue
    sc.exe delete $name | Out-Null
    Start-Sleep -Seconds 2
}

dotnet publish (Join-Path $repo 'src\Bast.Service') -c Release -o $target --nologo
if ($LASTEXITCODE -ne 0) { throw 'Falha no dotnet publish.' }

$exe = Join-Path $target 'Bast.Service.exe'
sc.exe create $name binPath= "`"$exe`"" start= delayed-auto DisplayName= 'BAST Social Publisher' | Out-Null
sc.exe description $name 'Importa, agenda e publica videos nas redes sociais.' | Out-Null
# Reinicia apos 5 s, 30 s e 60 s; o contador de falhas zera depois de 1 dia.
sc.exe failure $name reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
sc.exe failureflag $name 1 | Out-Null
Start-Service $name
Get-Service $name | Format-List Name, Status, StartType
Write-Host 'Painel/API: http://127.0.0.1:5080/api/health'
