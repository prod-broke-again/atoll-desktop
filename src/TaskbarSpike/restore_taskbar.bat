@echo off
setlocal EnableExtensions
if /I "%~1"=="/restart" goto :restart

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "iex ((Get-Content -LiteralPath '%~f0' -Raw) -replace '(?s)^.*#POWERSHELL','')"
if errorlevel 2 goto :restart
exit /b 0

:restart
echo Tray windows were not found, or /restart was requested. Restarting Explorer...
powershell.exe -NoProfile -Command "Get-Process explorer | Stop-Process -Force"
exit /b 0

#POWERSHELL
$ErrorActionPreference = 'Stop'
$sig = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class TrayRestore {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")]
  public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
  [DllImport("user32.dll")]
  public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  public static int Restore() {
    int n = 0;
    EnumWindows((h, l) => {
      var sb = new StringBuilder(256);
      GetClassName(h, sb, sb.Capacity);
      var name = sb.ToString();
      if (name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") {
        ShowWindow(h, 5);
        n++;
      }
      return true;
    }, IntPtr.Zero);
    return n;
  }
}
'@
Add-Type -TypeDefinition $sig -Language CSharp
$n = [TrayRestore]::Restore()
if ($n -lt 1) { exit 2 }
Write-Host "Restored $n taskbar window(s)."
