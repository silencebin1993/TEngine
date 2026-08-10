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
$dataOut = Join-Path $workspace 'UnityProject\Assets\AssetRaw\Configs\bytes'
$codeOut = Join-Path $workspace 'UnityProject\Assets\GameScripts\HotFix\GameProto\GameConfig'
$protoDir = Join-Path $workspace 'UnityProject\Assets\GameScripts\HotFix\GameProto'
$customTpl = Join-Path $confRoot 'CustomTemplate\CustomTemplate_Client_LazyLoad'

$destCfg = Join-Path $protoDir 'ConfigSystem.cs'
$destExt = Join-Path $protoDir 'ExternalTypeUtil.cs'
New-Item -ItemType Directory -Force -Path $protoDir | Out-Null
Copy-Item -LiteralPath (Join-Path $confRoot 'CustomTemplate\ConfigSystem.cs') -Destination $destCfg -Force
Copy-Item -LiteralPath (Join-Path $confRoot 'CustomTemplate\ExternalTypeUtil.cs') -Destination $destExt -Force

& dotnet $lubanDll `
  -t client `
  -c cs-bin `
  -d bin `
  --conf (Join-Path $confRoot 'luban.conf') `
  --customTemplateDir $customTpl `
  -x code.lineEnding=crlf `
  -x ("outputCodeDir=$codeOut") `
  -x ("outputDataDir=$dataOut")
$code = $LASTEXITCODE
Wait-IfInteractive
exit $code
