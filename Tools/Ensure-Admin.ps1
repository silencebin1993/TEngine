#Requires -Version 5.1
<#
.SYNOPSIS
  Dot-source from TEngine tool scripts, then call Invoke-EnsureAdmin.
.EXAMPLE
  . (Join-Path $PSScriptRoot '..\Tools\Ensure-Admin.ps1')
  Invoke-EnsureAdmin -ScriptPath $PSCommandPath -BoundArguments $args
#>

function Test-IsAdministrator {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = New-Object Security.Principal.WindowsPrincipal($id)
  return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Hide-CurrentConsoleWindow {
  # When re-launching elevated, hide this non-admin console so only the worker window stays visible.
  try {
    if (-not ('BinGames.NativeConsole' -as [type])) {
      Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace BinGames {
  public static class NativeConsole {
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  }
}
"@
    }
    $hwnd = [BinGames.NativeConsole]::GetConsoleWindow()
    if ($hwnd -ne [IntPtr]::Zero) {
      [void][BinGames.NativeConsole]::ShowWindow($hwnd, 0) # SW_HIDE
    }
  } catch {}
}

function Invoke-EnsureAdmin {
  param(
    [Parameter(Mandatory = $true)]
    [string] $ScriptPath,

    [string[]] $BoundArguments = @()
  )

  if (Test-IsAdministrator) { return }

  $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
  $argList = [System.Collections.Generic.List[string]]::new()
  [void]$argList.Add('-NoProfile')
  [void]$argList.Add('-ExecutionPolicy')
  [void]$argList.Add('Bypass')
  [void]$argList.Add('-File')
  [void]$argList.Add($ScriptPath)
  foreach ($a in $BoundArguments) {
    if ($null -ne $a) { [void]$argList.Add([string]$a) }
  }

  Hide-CurrentConsoleWindow

  # Prefer Windows Terminal when interactive (paste / Ctrl+V). wt.exe is a broker
  # so -Wait is unreliable; only use it when not in AI_MODE automation.
  $wt = $null
  if (-not $env:AI_MODE) {
    $cmd = Get-Command wt.exe -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) { $wt = $cmd.Source }
    else {
      $fallback = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\wt.exe'
      if (Test-Path -LiteralPath $fallback) { $wt = $fallback }
    }
  }
  if ($wt) {
    $workDir = Split-Path -Parent $ScriptPath
    $wtArgs = [System.Collections.Generic.List[string]]::new()
    [void]$wtArgs.Add('-d')
    [void]$wtArgs.Add($workDir)
    [void]$wtArgs.Add('--')
    [void]$wtArgs.Add($powershell)
    foreach ($a in $argList) { [void]$wtArgs.Add($a) }
    try {
      Start-Process -FilePath $wt -Verb RunAs -ArgumentList $wtArgs.ToArray() | Out-Null
      exit 0
    } catch {
      # Fall through to classic elevated console.
    }
  }

  # Classic path (also opens in WT if OS default terminal is Windows Terminal).
  $proc = Start-Process -FilePath $powershell -Verb RunAs -ArgumentList $argList.ToArray() -Wait -PassThru
  $code = 0
  if ($null -ne $proc) { $code = [int]$proc.ExitCode }
  exit $code
}

function Wait-IfInteractive {
  if ($env:AI_MODE) { return }
  if ($Host.Name -eq 'ConsoleHost') {
    Read-Host 'Press Enter to close' | Out-Null
  }
}
