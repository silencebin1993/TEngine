#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\Ensure-Admin.ps1')
Invoke-EnsureAdmin -ScriptPath $PSCommandPath -BoundArguments $args

Set-Location -LiteralPath $PSScriptRoot
& server -p 8081 -cors
exit $LASTEXITCODE
