# Show/hide regression for the tray + global-hotkey shell (v0.3.1).
#
# What it pins: after the main window has been hidden and then brought back by a
# SECOND INSTANCE, pressing the show/hide hotkey must still work. That is the exact
# scenario that silently broke in 0.3.0: the wake path only called Activate(), while
# the toggle consulted a manually mirrored "is visible" field, so the two went out of
# phase forever and the hotkey became a one-way no-op.
#
# Usage (from the repo root):
#   powershell -File tools\verify-showhide.ps1 -Exe <path to WinCanvas.App.exe>
#
# Requires a real desktop session (it drives the window and reads its visibility),
# so it is not part of `dotnet test`; run it by hand after touching the shell code.

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Injector = '',
    [int]$StartupSeconds = 14
)
$ErrorActionPreference = 'Continue'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Injector) {
    $Injector = Join-Path $repoRoot 'tools\HotkeyInjector\bin\Release\net10.0-windows10.0.19041.0\HotkeyInjector.exe'
    if (-not (Test-Path $Injector)) {
        Write-Output 'injector missing -> building tools/HotkeyInjector'
        dotnet build (Join-Path $repoRoot 'tools\HotkeyInjector\HotkeyInjector.csproj') -c Release -v q --nologo | Out-Null
    }
}
if (-not (Test-Path $Injector)) { Write-Output 'FATAL: HotkeyInjector not available'; exit 2 }

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class VisibilityProbe {
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool EnumWindows(Cb c, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

  public delegate bool Cb(IntPtr h, IntPtr l);

  public static bool Visible(uint pidTarget) {
    bool visible = false; bool found = false;
    // Do NOT pre-filter on IsWindowVisible: that would turn "window is hidden" into
    // "window not found", and the script could not tell a closed window from a dead
    // process -- which is exactly the assertion this regression has to make.
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == pidTarget) {
        StringBuilder s = new StringBuilder(256); GetClassNameW(h, s, 256);
        if (s.ToString() == "WinUIDesktopWin32WindowClass") { found = true; visible = IsWindowVisible(h); return false; }
      }
      return true;
    }, IntPtr.Zero);
    if (!found) { throw new Exception("main window not found for pid " + pidTarget + " (process gone?)"); }
    return visible;
  }
}
'@

function Show-Hide([string]$hexVk) {
    & $Injector inject $hexVk 1 300 | Out-Null
}

$ctrlAltC = '43'   # default ShowHideMainWindow binding

Get-Process -Name 'WinCanvas.App' -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Start-Sleep -Seconds 2

$p = Start-Process -FilePath $Exe -PassThru
$pid32 = [uint32]$p.Id
Start-Sleep -Seconds $StartupSeconds

$fail = 0

Write-Output ('STEP1 launched            visible=' + [VisibilityProbe]::Visible($pid32) + ' expect=True')

Show-Hide $ctrlAltC
Start-Sleep -Seconds 3
$s = [VisibilityProbe]::Visible($pid32)
Write-Output ('STEP2 after hide hotkey   visible=' + $s + ' expect=False')
if ($s) { $fail++ }

# Second instance: it should wake the existing window and exit by itself.
$q = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 6
Write-Output ('STEP3 second instance exited=' + ((Get-Process -Id $q.Id -ErrorAction SilentlyContinue) -eq $null))
$s = [VisibilityProbe]::Visible($pid32)
Write-Output ('STEP3 after wake          visible=' + $s + ' expect=True')
if (-not $s) { $fail++ }

# The step that used to fail: the toggle must still be in phase after the wake.
Show-Hide $ctrlAltC
Start-Sleep -Seconds 3
$s = [VisibilityProbe]::Visible($pid32)
Write-Output ('STEP4 wake then hotkey    visible=' + $s + ' expect=False   <-- regression lives here')
if ($s) { $fail++ }

Show-Hide $ctrlAltC
Start-Sleep -Seconds 3
$s = [VisibilityProbe]::Visible($pid32)
Write-Output ('STEP5 hotkey again        visible=' + $s + ' expect=True')
if (-not $s) { $fail++ }

Get-Process -Id $p.Id -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Write-Output ('RESULT failures=' + $fail)
exit $fail
