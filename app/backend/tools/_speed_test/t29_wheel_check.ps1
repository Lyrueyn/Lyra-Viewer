$ErrorActionPreference='Continue'
# T29b 收尾验证：小窗滚轮缩放 + 全屏滚轮缩放 + 小窗截图
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$recall='F:\DSH\LocalMediaPlayer-Workspace\app\data\downloads\【洛天依原创曲】再声 Recall【2024官方十二周年生贺曲】 [1080P+].mp4'
$shotDir="$bin\speed_verify"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public class M31 {
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int data, UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
'@
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
$menuType=[System.Windows.Automation.ControlType]::MenuItem
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }
function Txt($id){ $e=FindId $id; if($e){ return $e.Current.Name } else { return '<不在树里>' } }
function ClickId($id){ $e=FindId $id; if($e){ try{ $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }catch{ return $false } } else { Write-Output ("  找不到 " + $id); return $false } }
function Vw(){ $p=Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1; if(-not $p){return $null}
  $cond=New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty,[int]$p.Id)
  foreach($w in $auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)){ try{ if($w.Current.Name -eq '视频'){return $w} }catch{} }; return $null }
function Rect($id){ $e=FindId $id; if(-not $e){ return $null }; return $e.Current.BoundingRectangle }
function AbsMove($x,$y){ $sw=[M31]::GetSystemMetrics(0); $sh=[M31]::GetSystemMetrics(1)
  [M31]::mouse_event(0x8001,[uint32]([math]::Round($x*65535.0/$sw)),[uint32]([math]::Round($y*65535.0/$sh)),0,[UIntPtr]::Zero) }
function WheelAt($rect,$delta){ AbsMove ([int]($rect.X+80)) ([int]($rect.Y+80)); Start-Sleep -Milliseconds 400
  [M31]::mouse_event(0x0800,0,0,$delta,[UIntPtr]::Zero); Start-Sleep -Milliseconds 900 }
function ShotFull($name){ $w=Vw; if(-not $w){return}
  $wr=$w.Current.BoundingRectangle; $hwnd=[IntPtr]$w.Current.NativeWindowHandle
  $bmp=New-Object System.Drawing.Bitmap([int]$wr.Width,[int]$wr.Height)
  $g=[System.Drawing.Graphics]::FromImage($bmp); $hdc=$g.GetHdc()
  [M31]::PrintWindow($hwnd,$hdc,2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
  $out="$shotDir\$name"; $bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); Write-Output ("  截图: " + $out) }

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$env:DSH_NO_AUDIO='1'; $env:DSH_AUTO_OPEN=$recall
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
$w=$null; for($i=0;$i -lt 60;$i++){ Start-Sleep -Milliseconds 500; $w=Vw; if($w){ break } }
if(-not $w){ Write-Output 'FAIL'; exit 1 }
[M31]::SetForegroundWindow([IntPtr]$w.Current.NativeWindowHandle) | Out-Null; Start-Sleep 2
ClickId 'EnhanceChainTab' | Out-Null; Start-Sleep 4
foreach($label in '响度统一','高精度重采样'){
  ClickId 'ChainAddNodeBtn' | Out-Null; Start-Sleep 2
  $mi=$null
  foreach($e in $root.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$menuType)))){ try{ if($e.Current.Name -like ($label+'*')){ $mi=$e; break } }catch{} }
  if($mi){ $mi.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2 }
}
Write-Output ("  加 2 节点: " + (Txt 'ChainCanvasStats'))
Write-Output '=== A) 小窗滚轮缩放 ==='
$cr=Rect 'ChainCanvas'
$z0=(Txt 'ChainZoomText'); WheelAt $cr 360; $z1=(Txt 'ChainZoomText'); WheelAt $cr -720; $z2=(Txt 'ChainZoomText')
Write-Output ("  缩放: '" + $z0 + "' → 上 '" + $z1 + "' → 下 '" + $z2 + "'")
ShotFull 't29_smallwindow_canvas.png'
Write-Output '=== B) 全屏滚轮缩放 ==='
ClickId 'ChainFullscreenBtn' | Out-Null; Start-Sleep 3
$cr2=Rect 'ChainCanvas'
$f0=(Txt 'ChainZoomText'); WheelAt $cr2 240; $f1=(Txt 'ChainZoomText'); WheelAt $cr2 -240; $f2=(Txt 'ChainZoomText')
Write-Output ("  全屏缩放: '" + $f0 + "' → 上 '" + $f1 + "' → 下 '" + $f2 + "'")
ShotFull 't29_fullscreen_canvas.png'
Write-Output '=== C) 退出全屏 ==='
ClickId 'ChainFullscreenBtn' | Out-Null; Start-Sleep 2
Write-Output ("  " + (Txt 'ChainCanvasStats') + " ; 按钮='" + (Txt 'ChainFullscreenBtn') + "'")
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Write-Output 'DONE'
