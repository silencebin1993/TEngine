#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Tools\Ensure-Admin.ps1')
Invoke-EnsureAdmin -ScriptPath $PSCommandPath -BoundArguments $args

$RepoUrl = 'https://gitee.com/game-for-all_0/TEngine.git'
$ProjectDir = $PSScriptRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$TempDir = Join-Path $env:TEMP ("TEngine_tmp_{0}" -f (Get-Random))

function Fail([string]$msg) {
  Write-Host $msg -ForegroundColor Red
  Write-Host ''
  Write-Host '[ABORTED] No changes were made.' -ForegroundColor Yellow
  Wait-IfInteractive
  exit 1
}

Write-Host '========================================'
Write-Host ' TEngine Updater'
Write-Host '========================================'
Write-Host ''
Write-Host "Source : $RepoUrl"
Write-Host "Target : $ProjectDir"
Write-Host ''

Write-Host '[0/3] Checking project layout ...'
if (-not (Test-Path -LiteralPath (Join-Path $ProjectDir 'UnityProject'))) {
  Fail "[ERROR] 'UnityProject' not found under $ProjectDir`n        Please run this script from the TEngine project root."
}
if (-not (Test-Path -LiteralPath (Join-Path $ProjectDir 'UnityProject\Packages\YooAsset'))) {
  Fail "[ERROR] 'UnityProject\Packages\YooAsset' not found.`n        Unexpected project structure."
}
if (-not (Test-Path -LiteralPath (Join-Path $ProjectDir 'UnityProject\Assets\TEngine'))) {
  Fail "[ERROR] 'UnityProject\Assets\TEngine' not found.`n        Unexpected project structure."
}
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
  Fail "[ERROR] 'git' not found in PATH. Please install Git for Windows."
}
Write-Host '[OK] Project layout verified.'
Write-Host ''

try {
  Write-Host '[1/3] Cloning (sparse) ...'
  & git clone --no-checkout --depth=1 --filter=blob:none $RepoUrl $TempDir
  if ($LASTEXITCODE -ne 0) { throw 'git clone failed.' }

  Push-Location -LiteralPath $TempDir
  try {
    & git sparse-checkout init --cone
    if ($LASTEXITCODE -ne 0) { throw 'git sparse-checkout init failed.' }
    & git sparse-checkout set UnityProject/Packages/YooAsset UnityProject/Assets/TEngine
    if ($LASTEXITCODE -ne 0) { throw 'git sparse-checkout set failed.' }
    & git checkout
    if ($LASTEXITCODE -ne 0) { throw 'git checkout failed.' }
  } finally {
    Pop-Location
  }

  Write-Host ''
  Write-Host '[2/3] Syncing UnityProject\Packages\YooAsset ...'
  $srcYoo = Join-Path $TempDir 'UnityProject\Packages\YooAsset'
  $dstYoo = Join-Path $ProjectDir 'UnityProject\Packages\YooAsset'
  & robocopy $srcYoo $dstYoo /MIR /NFL /NDL /NJH /NJS /NC /NS
  if ($LASTEXITCODE -ge 8) { throw 'robocopy failed for YooAsset.' }

  Write-Host ''
  Write-Host '[3/3] Syncing UnityProject\Assets\TEngine ...'
  $srcTe = Join-Path $TempDir 'UnityProject\Assets\TEngine'
  $dstTe = Join-Path $ProjectDir 'UnityProject\Assets\TEngine'
  & robocopy $srcTe $dstTe /MIR /NFL /NDL /NJH /NJS /NC /NS
  if ($LASTEXITCODE -ge 8) { throw 'robocopy failed for TEngine.' }

  Write-Host ''
  Write-Host '[OK] Update complete!' -ForegroundColor Green
} catch {
  Write-Host ("[ERROR] {0}" -f $_.Exception.Message) -ForegroundColor Red
} finally {
  Set-Location -LiteralPath $ProjectDir
  if (Test-Path -LiteralPath $TempDir) {
    Remove-Item -LiteralPath $TempDir -Recurse -Force -ErrorAction SilentlyContinue
  }
}

Write-Host ''
Wait-IfInteractive
exit 0
