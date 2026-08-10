#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\Tools\Ensure-Admin.ps1')
Invoke-EnsureAdmin -ScriptPath $PSCommandPath -BoundArguments $args

Set-Location -LiteralPath $PSScriptRoot
. (Join-Path $PSScriptRoot 'path_define.ps1')

$unity = Join-Path $UNITYEDITOR_PATH 'Unity.exe'
$argsUnity = @(
  $WORKSPACE,
  '-logFile', $BUILD_LOGFILE,
  '-executeMethod', 'TEngine.ReleaseTools.AutomationBuildAndroid',
  '-quit',
  '-batchmode',
  '-CustomArgs:Language=en_US;',
  $WORKSPACE
)
& $unity @argsUnity
$code = $LASTEXITCODE
Wait-IfInteractive
exit $code
