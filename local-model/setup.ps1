param([string]$Python = 'py', [switch]$Cuda)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$venvPython = Join-Path $taskRoot '.venv/Scripts/python.exe'
$env:PIP_CACHE_DIR = Join-Path $taskRoot '.cache/pip'
$env:HF_HOME = Join-Path $taskRoot '.cache/huggingface'
$env:HF_HUB_DISABLE_TELEMETRY = '1'
$env:HF_HUB_DISABLE_SYMLINKS_WARNING = '1'
if (!(Test-Path -LiteralPath $venvPython)) {
    if ($Python -eq 'py') { & $Python -3.12 -m venv (Join-Path $taskRoot '.venv') }
    else { & $Python -m venv (Join-Path $taskRoot '.venv') }
    if ($LASTEXITCODE -ne 0) { throw 'Python 3.12 virtual environment creation failed.' }
}
& $venvPython -m pip install --disable-pip-version-check -r (Join-Path $PSScriptRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Local translation dependencies could not be installed.' }
$cpuHash = '31590ea000d5aff6f05f1e428048e72318a83709288159a5bd4dabec530080bb'
$cudaHash = 'df0ae292f4590b05896e656547ef0eda23dd64cfec2944561f8909784d9a7684'
$runtimeHash = & $venvPython -c "import importlib.metadata as m,json; d=next((d for d in m.distributions() if d.metadata['Name'].lower().replace('_','-')=='llama-cpp-python'),None); print(json.loads(d.read_text('direct_url.json') or '{}').get('archive_info',{}).get('hashes',{}).get('sha256','') if d and d.version=='0.3.35' else '')"
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the local model runtime.' }
if (($Cuda -and $runtimeHash -ne $cudaHash) -or (!$Cuda -and $runtimeHash -notin @($cpuHash, $cudaHash))) {
    $runtimeTag = if ($Cuda) { 'v0.3.35-cu125' } else { 'v0.3.35' }
    $runtimeSha = if ($Cuda) { $cudaHash } else { $cpuHash }
    $runtimeWheel = "https://github.com/abetlen/llama-cpp-python/releases/download/$runtimeTag/llama_cpp_python-0.3.35-py3-none-win_amd64.whl#sha256=$runtimeSha"
    & $venvPython -m pip install --disable-pip-version-check --force-reinstall --no-deps $runtimeWheel
    if ($LASTEXITCODE -ne 0) { throw 'The verified local model runtime could not be installed.' }
}
& $venvPython (Join-Path $PSScriptRoot 'provision.py')
if ($LASTEXITCODE -ne 0) { throw 'Local model provisioning failed.' }
& $venvPython (Join-Path $PSScriptRoot 'check.py')
if ($LASTEXITCODE -ne 0) { throw 'Offline worker check failed.' }
Write-Host "Ready. Python: $venvPython"
