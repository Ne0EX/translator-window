param([string]$Python = 'py', [switch]$Cuda)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) {
    New-Item -ItemType Directory -Force (Join-Path $taskRoot '.tools') | Out-Null
    $installer = Join-Path $taskRoot '.tools/dotnet-install.ps1'
    Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $installer -UseBasicParsing
    & $installer -Version '8.0.425' -InstallDir (Join-Path $taskRoot '.tools/dotnet') -NoPath
    if ($LASTEXITCODE -ne 0) { throw 'Workspace .NET SDK installation failed.' }
}
& (Join-Path $taskRoot 'local-model/setup.ps1') -Python $Python -Cuda:$Cuda
& (Join-Path $PSScriptRoot 'setup-ocr.ps1')
& (Join-Path $taskRoot 'local-ocr/setup.ps1') -Cuda:$Cuda
& (Join-Path $PSScriptRoot 'build.ps1')
