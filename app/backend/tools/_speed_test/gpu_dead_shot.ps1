$ErrorActionPreference='Continue'
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$tools='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools'
$out="$bin\speed_verify\gpu_dead_banner.png"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public class Fg { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }'
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$env:DSH_TEST_DEVICE_LOST='1'; $env:DSH_SPEED_UI='1'
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 7
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_collections.ps1" 2>&1 | Select-Object -Last 1
Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_open_from_collection.ps1" 2>&1 | Select-Object -Last 1

$ok=$false
for($i=1;$i -le 30;$i++){
  Start-Sleep -Seconds 1
  if(FindId 'GpuDeadBannerTitle'){ Write-Output ("提示条在 t=" + $i + "s 出现"); $ok=$true; break }
}
if(-not $ok){
  Write-Output '提示条未出现'
  Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
  exit 1
}
Start-Sleep 2
$proc=Get-Process WinUIGui | Select-Object -First 1
$cond=New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty,[int]$proc.Id)
$wins=$root.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)
foreach($w in $wins){
  try{
    if($w.Current.Name -eq '视频'){
      $r=$w.Current.BoundingRectangle
      $h=[IntPtr]$w.Current.NativeWindowHandle
      [Fg]::SetForegroundWindow($h) | Out-Null
      Start-Sleep -Seconds 1
      $bmp=New-Object System.Drawing.Bitmap([int]$r.Width,[int]$r.Height)
      $g=[System.Drawing.Graphics]::FromImage($bmp)
      $g.CopyFromScreen([int]$r.X,[int]$r.Y,0,0,(New-Object System.Drawing.Size([int]$r.Width,[int]$r.Height)))
      $bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
      Write-Output ("截图已保存 " + $out + "  尺寸 " + [int]$r.Width + "x" + [int]$r.Height)
    }
  }catch{}
}
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Write-Output 'DONE'
