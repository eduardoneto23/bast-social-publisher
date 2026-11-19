# Pasta principal do BAST: parâmetro, senão Bast:RootFolder do appsettings.Local.json do serviço.
function Resolve-Root([string]$Given) {
    if ($Given) { return $Given }
    $settings = Join-Path $PSScriptRoot '..\..\src\Bast.Service\appsettings.Local.json'
    if (Test-Path $settings) {
        $value = (Get-Content $settings -Raw | ConvertFrom-Json).Bast.RootFolder
        if ($value) { return $value }
    }
    throw "Pasta principal não encontrada. Informe -Root ou configure Bast:RootFolder no appsettings.Local.json."
}

# Localiza ffmpeg/ffprobe: parâmetro, depois appsettings.Local.json do serviço, depois o PATH.
function Resolve-Tool([string]$Given, [string]$SettingName, [string]$Command) {
    if ($Given) { return $Given }
    $settings = Join-Path $PSScriptRoot '..\..\src\Bast.Service\appsettings.Local.json'
    if (Test-Path $settings) {
        $value = (Get-Content $settings -Raw | ConvertFrom-Json).Bast.$SettingName
        if ($value -and (Test-Path $value)) { return $value }
    }
    $found = Get-Command $Command -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    throw "$Command não encontrado. Informe o caminho com -$Command."
}
