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

# Filas: horizontal (00 - THUMB -> 01 - ENVIAR YOUTUBE, thumbnail PNG 1280x720) e vertical
# (00 - THUMB VERTICAL -> 01 - ENVIAR VERTICAL, capa JPG 1080x1920 para YouTube Shorts, Instagram e Facebook).
function Get-Lane([switch]$Vertical) {
    if ($Vertical) {
        return [pscustomobject]@{ Name = 'vertical'; Source = '00 - THUMB VERTICAL'; Target = '01 - ENVIAR VERTICAL'; Width = 1080; Height = 1920; File = 'thumbnail.jpg'; Codec = 'mjpeg' }
    }
    return [pscustomobject]@{ Name = 'horizontal'; Source = '00 - THUMB'; Target = '01 - ENVIAR YOUTUBE'; Width = 1280; Height = 720; File = 'thumbnail.png'; Codec = 'png' }
}

# Fila de uma pasta de vídeo pela pasta em que ela está (00 - THUMB ou 00 - THUMB VERTICAL).
function Get-LaneOf([string]$Root, [string]$Folder) {
    $parent = (Split-Path $Folder -Parent).TrimEnd('\')
    foreach ($vertical in $false, $true) {
        $lane = Get-Lane -Vertical:$vertical
        if ($parent -ieq (Join-Path $Root $lane.Source).TrimEnd('\')) { return $lane }
    }
    throw "A pasta não está em 00 - THUMB nem em 00 - THUMB VERTICAL: $Folder"
}
