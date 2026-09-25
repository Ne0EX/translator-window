param([string]$Python = (Join-Path $PSScriptRoot '../.venv/Scripts/python.exe'), [switch]$Cuda)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (!(Test-Path -LiteralPath $Python)) { throw 'Run local-model/setup.ps1 first to create the local Python runtime.' }
& $Python -m pip install --disable-pip-version-check -r (Join-Path $PSScriptRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Local detector dependency installation failed.' }
if ($Cuda) {
    & $Python -m pip uninstall -y onnxruntime
    if ($LASTEXITCODE -ne 0) { throw 'Could not remove the CPU ONNX runtime.' }
    & $Python -m pip install --disable-pip-version-check torch==2.6.0+cu126 --index-url https://download.pytorch.org/whl/cu126
    if ($LASTEXITCODE -ne 0) { throw 'CUDA PyTorch installation failed.' }
    & $Python -m pip install --disable-pip-version-check onnxruntime-gpu==1.23.2
} else {
    & $Python -c "import importlib.util,sys; sys.exit(0 if importlib.util.find_spec('torch') else 1)"
    if ($LASTEXITCODE -ne 0) {
        & $Python -m pip install --disable-pip-version-check torch==2.6.0 --index-url https://download.pytorch.org/whl/cpu
        if ($LASTEXITCODE -ne 0) { throw 'CPU PyTorch installation failed.' }
    }
    & $Python -c "import importlib.metadata as m,sys; sys.exit(0 if any(d.metadata['Name']=='onnxruntime-gpu' for d in m.distributions()) else 1)"
    if ($LASTEXITCODE -ne 0) { & $Python -m pip install --disable-pip-version-check onnxruntime==1.23.2 }
}
if ($LASTEXITCODE -ne 0) { throw 'ONNX runtime installation failed.' }
& $Python (Join-Path $PSScriptRoot 'provision_manga.py')
if ($LASTEXITCODE -ne 0) { throw 'Japanese manga recognition model provisioning failed.' }
$mangaPath = Join-Path $PSScriptRoot '../models/manga-ocr'
& $Python (Join-Path $PSScriptRoot 'export_manga_onnx.py') --model $mangaPath --output $mangaPath
if ($LASTEXITCODE -ne 0) { throw 'Cached Japanese manga recognition graph export failed.' }
if ($Cuda) {
    & $Python (Join-Path $PSScriptRoot 'export_manga_onnx.py') --model $mangaPath --output $mangaPath --fp16
    if ($LASTEXITCODE -ne 0) { throw 'Cached FP16 Japanese manga recognition graph export failed.' }
}
$destination = Join-Path $PSScriptRoot '../models/comic-text-detector'
New-Item -ItemType Directory -Force $destination | Out-Null
$path = Join-Path $destination 'comictextdetector.onnx'
$sha256 = '1A86ACE74961413CBD650002E7BB4DCEC4980FFA21B2F19B86933372071D718F'
if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $sha256) {
    $partial = "$path.download"
    Invoke-WebRequest 'https://github.com/zyddnys/manga-image-translator/releases/download/beta-0.2.1/comictextdetector.pt.onnx' -OutFile $partial -UseBasicParsing
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $sha256) { throw 'Comic detector model failed SHA256 verification.' }
    Move-Item -LiteralPath $partial -Destination $path -Force
}
$blocksPath = Join-Path $destination 'comictextdetector-blocks.onnx'
& $Python (Join-Path $PSScriptRoot 'prune_detector.py') $path $blocksPath
if ($LASTEXITCODE -ne 0) { throw 'Block-only comic detector generation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE.comic-text-detector') -Destination $destination -Force
Write-Host 'Local comic text detector ready. Runtime inference is offline.'
