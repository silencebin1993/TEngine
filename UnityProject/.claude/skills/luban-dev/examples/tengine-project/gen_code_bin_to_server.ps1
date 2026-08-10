#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$teRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..\..\..')).Path
. (Join-Path $teRoot 'Tools\Ensure-Admin.ps1')
Invoke-EnsureAdmin -ScriptPath $PSCommandPath -BoundArguments $args

Set-Location -LiteralPath $PSScriptRoot
Write-Host $PWD.Path

$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$lubanDll = Join-Path $workspace 'Tools\Luban\Luban.dll'
$confRoot = $PSScriptRoot
$dataOut = Join-Path $workspace 'Configs\Server\Binary'
$codeOut = Join-Path $workspace 'Server\Entity\Generate\GameConfig'

& dotnet $lubanDll `
  -t server `
  -c cs-bin `
  -d bin `
  --conf (Join-Path $confRoot 'luban.conf') `
  -x code.lineEnding=crlf `
  -x ("outputCodeDir=$codeOut") `
  -x ("outputDataDir=$dataOut")
$code = $LASTEXITCODE
Wait-IfInteractive
exit $code
