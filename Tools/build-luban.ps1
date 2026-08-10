#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Ensure-Admin.ps1')
Invoke-EnsureAdmin -ScriptPath $PSCommandPath -BoundArguments $args

Set-Location -LiteralPath $PSScriptRoot
$outDir = Join-Path $PSScriptRoot 'Luban'
if (Test-Path -LiteralPath $outDir) {
  Remove-Item -LiteralPath $outDir -Recurse -Force
}

$csproj = Join-Path $PSScriptRoot '..\..\luban\src\Luban\Luban.csproj'
& dotnet build $csproj -c Release -o $outDir
$code = $LASTEXITCODE
Wait-IfInteractive
exit $code
