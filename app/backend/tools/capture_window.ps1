# Capture the main window content directly (works even when covered by another window).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File capture_window.ps1 -Title 本地播放器 -Out D:\...\out.png
param([string]$Title = '本地播放器', [string]$Out = 'F:\DSH\LocalMediaPlayer-Workspace\backups\local-media-player-backup-20261014\pw_home.png')
Add-Type -AssemblyName System.Drawing
$sig = @'
using System; using System.Text; using System.Runtime.InteropServices;
public class PWCap {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  public struct RECT { public int L,T,R,B; }
  public static IntPtr Find(uint target, string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h,p)=>{ uint pid; GetWindowThreadProcessId(h,out pid);
      if(pid==target){ var sb=new StringBuilder(512); GetWindowTextW(h,sb,512); if(sb.ToString()==title) found=h; } return true; }, IntPtr.Zero);
    return found; }
  public static string Cap(IntPtr h, string outPath) {
    RECT r; GetWindowRect(h, out r);
    int w = r.R-r.L, hh = r.B-r.T;
    using (var bmp = new System.Drawing.Bitmap(w, hh))
    using (var g = System.Drawing.Graphics.FromImage(bmp)) {
      IntPtr hdc = g.GetHdc();
      bool ok = PrintWindow(h, hdc, 2);
      g.ReleaseHdc(hdc);
      bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
      return "PrintWindow=" + ok + " size=" + w + "x" + hh;
    } }
}
'@
Add-Type -TypeDefinition $sig -Language CSharp -ReferencedAssemblies System.Drawing

$proc = Get-Process WinUIGui -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output 'app not running'; exit 1 }
$h = [PWCap]::Find([uint32]$proc.Id, $Title)
if ($h -eq [IntPtr]::Zero) { Write-Output ('window not found: ' + $Title); exit 1 }
Write-Output ([PWCap]::Cap($h, $Out))
