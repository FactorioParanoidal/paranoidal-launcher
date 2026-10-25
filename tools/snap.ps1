param([int]$ProcessId, [string]$OutFile, [string]$Title = '')
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
$found = New-Object System.Collections.Generic.List[IntPtr]
[W]::EnumWindows({ param($h, $l) $p = 0; [void][W]::GetWindowThreadProcessId($h, [ref]$p); if ($p -eq $ProcessId -and [W]::IsWindowVisible($h)) { $found.Add($h) }; $true }, [IntPtr]::Zero) | Out-Null
$i = 0
foreach ($h in $found) {
  $sb = New-Object Text.StringBuilder 256; [void][W]::GetWindowText($h, $sb, 256)
  if ($Title -and -not $sb.ToString().Contains($Title)) { continue }
  $r = New-Object W+RECT; [void][W]::GetWindowRect($h, [ref]$r)
  $w = $r.R - $r.L; $hh = $r.B - $r.T
  if ($w -lt 50) { continue }
  $bmp = New-Object Drawing.Bitmap $w, $hh
  $g = [Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  [void][W]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
  $f = if ($i -eq 0) { $OutFile } else { $OutFile -replace '\.png$', "-$i.png" }
  $bmp.Save($f); $bmp.Dispose()
  "{0} '{1}' {2}x{3} -> {4}" -f $h, $sb, $w, $hh, $f
  $i++
}
