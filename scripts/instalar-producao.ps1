<#
  Monta o ambiente Python da produção (análise de áudio, fala, câmera e conteúdo sensível), usado pelo
  serviço do BAST. Fica dentro da pasta principal, em SYSTEM\tools\python-producao, com os modelos em
  SYSTEM\tools\modelos, para o serviço (conta do sistema) enxergar tudo sem depender do perfil de usuário.

  Uso: powershell -ExecutionPolicy Bypass -File scripts\instalar-producao.ps1 [-Python C:\Python314\python.exe] [-Whisper small]
  Pode rodar de novo: só instala o que falta.
#>
param(
    [string]$Python = 'C:\Python314\python.exe',
    [string]$Whisper = 'small',
    [string]$Root = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\tools\thumbnail-canva\ferramentas.ps1')
$Root = Resolve-Root $Root

$tools = Join-Path $Root 'SYSTEM\tools'
$venv = Join-Path $tools 'python-producao'
$models = Join-Path $tools 'modelos'
New-Item -ItemType Directory -Force $tools, $models | Out-Null

if (-not (Test-Path (Join-Path $venv 'Scripts\python.exe'))) {
    Write-Host "Criando o ambiente em $venv"
    & $Python -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao criar o ambiente Python.' }
}
$py = Join-Path $venv 'Scripts\python.exe'

& $py -m pip install --upgrade pip --quiet
# torch com CUDA (placa NVIDIA) direto do índice do PyTorch; se já houver um torch só de CPU, ele é trocado.
$torch = & $py -c "import torch; print(torch.__version__)" 2>$null
if (-not $torch -or $torch -notmatch '\+cu') {
    & $py -m pip install --quiet --force-reinstall torch --index-url https://download.pytorch.org/whl/cu128
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao instalar o torch com CUDA.' }
}
& $py -m pip install --quiet -r (Join-Path $PSScriptRoot '..\tools\producao\requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao instalar os pacotes.' }

# Modelos: o cache do Hugging Face fica dentro da pasta do BAST (HF_HOME), não no perfil do usuário.
$env:HF_HOME = $models
& $py -I -c @"
import sys
from huggingface_hub import snapshot_download
for repo in ['MIT/ast-finetuned-audioset-10-10-0.4593', 'Systran/faster-whisper-$Whisper']:
    path = snapshot_download(repo)
    print('modelo ok:', repo)
"@
if ($LASTEXITCODE -ne 0) { throw 'Falha ao baixar os modelos.' }

& $py -I -c "import torch, cv2, faster_whisper, transformers, nudenet; print('torch', torch.__version__, 'cuda', torch.cuda.is_available()); print('opencv', cv2.__version__)"
Write-Host "Ambiente da produção pronto: $py"
