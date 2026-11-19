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

# Verdadeiro quando algum arquivo da lista ainda está aberto por outro programa (cópia em andamento).
function Test-InUse($files) {
    foreach ($file in $files) {
        try { $stream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'None'); $stream.Dispose() }
        catch { return $true }
    }
    return $false
}

# Imagem de referência que o usuário põe na pasta (referencia.png/.jpg): não é a thumbnail.
function Test-Reference($file) { return [IO.Path]::GetFileNameWithoutExtension($file.Name) -match '^refer[eê]ncia$' }

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
