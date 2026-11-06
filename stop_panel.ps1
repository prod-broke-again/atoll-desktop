Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class PanelStop {
  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  public static extern IntPtr FindWindow(IntPtr cls, string title);
  [DllImport("user32.dll")]
  public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
"@

$deadline = (Get-Date).AddSeconds(8)
$posted = $false
while ((Get-Date) -lt $deadline) {
  $hwnd = [PanelStop]::FindWindow([IntPtr]::Zero, "TaskbarSpike")
  if ($hwnd -eq [IntPtr]::Zero) { break }
  if (-not $posted) {
    [void][PanelStop]::PostMessage($hwnd, 0x0312, [IntPtr]1, [IntPtr]::Zero)
    $posted = $true
  }
  Start-Sleep -Milliseconds 300
}
