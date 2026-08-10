#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\Tools\Ensure-Admin.ps1')
Invoke-EnsureAdmin -ScriptPath $PSCommandPath -BoundArguments $args

Set-Location -LiteralPath $PSScriptRoot
Write-Host $PWD.Path

$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$lubanDll = Join-Path $workspace 'Tools\Luban\Luban.dll'
$confRoot = $PSScriptRoot
$dataOut = Join-Path $workspace 'Server\GameConfig'
$codeOut = Join-Path $workspace 'Server\Hotfix\Config\GameConfig'

& dotnet $lubanDll `
  -t server `
  -c cs-bin `
  -d bin `
  --conf (Join-Path $confRoot 'luban.conf') `
  -x code.lineEnding=crlf `
  -x ("outputCodeDir=$codeOut") `
  -x ("outputDataDir=$dataOut")
$code = $LASTEXITCODE
if ($code -ne 0) { exit $code }
Wait-IfInteractive
exit 0
