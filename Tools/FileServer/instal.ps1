#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\Ensure-Admin.ps1')
Invoke-EnsureAdmin -ScriptPath $PSCommandPath -BoundArguments $args

Set-Location -LiteralPath $PSScriptRoot
& npm install yumu-static-server -g
$code = $LASTEXITCODE
Wait-IfInteractive
exit $code
