param([string]$OutputDirectory = "$PSScriptRoot/../.local/dist/TSKHook-UITranslations")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
dotnet publish (Join-Path $root 'app/Workbench') -c Release -r win-x64 --self-contained true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Workbench publish failed' }
Copy-Item (Join-Path $root 'LICENSE') $OutputDirectory
Copy-Item (Join-Path $root 'docs/workbench-user-guide.md') (Join-Path $OutputDirectory '使用说明.md')
Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath ($OutputDirectory + '-win-x64.zip') -Force
