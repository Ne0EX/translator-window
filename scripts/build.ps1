param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
& $dotnet publish (Join-Path $taskRoot 'src/Translumo/Translumo.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $taskRoot 'artifacts/app') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host 'Ready: double-click Start Translator.cmd in the project folder.'
