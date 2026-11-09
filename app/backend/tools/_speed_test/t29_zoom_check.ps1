$ErrorActionPreference='Continue'
# T29b 补充：小窗滚轮缩放 + 自动排列 + 拖动后序号（快速验证）
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$recall='F:\DSH\LocalMediaPlayer-Workspace\app\data\downloads\【洛天依原创曲】再声 Recall【2024官方十二周年生贺曲】 [1080P+].mp4'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public class M30 {
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int data, UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
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
function AbsMove($x,$y){ $sw=[M30]::GetSystemMetrics(0); $sh=[M30]::GetSystemMetrics(1)
  [M30]::mouse_event(0x8001,[uint32]([math]::Round($x*65535.0/$sw)),[uint32]([math]::Round($y*65535.0/$sh)),0,[UIntPtr]::Zero) }

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$env:DSH_NO_AUDIO='1'; $env:DSH_AUTO_OPEN=$recall
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
$w=$null; for($i=0;$i -lt 60;$i++){ Start-Sleep -Milliseconds 500; $w=Vw; if($w){ break } }
if(-not $w){ Write-Output 'FAIL 窗口没起来'; exit 1 }
[M30]::SetForegroundWindow([IntPtr]$w.Current.NativeWindowHandle) | Out-Null
Start-Sleep 2
ClickId 'EnhanceChainTab' | Out-Null; Start-Sleep 4
foreach($label in '响度统一','高精度重采样','FFT 重采样'){
  ClickId 'ChainAddNodeBtn' | Out-Null; Start-Sleep 2
  $mi=$null
  foreach($e in $root.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$menuType)))){ try{ if($e.Current.Name -like ($label+'*')){ $mi=$e; break } }catch{} }
  if($mi){ $mi.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2; Write-Output ("  已加: " + $label) }
}
Write-Output ("  加 3 节点后: " + (Txt 'ChainCanvasStats'))
Write-Output '=== 小窗滚轮缩放 ==='
$cr=Rect 'ChainCanvas'
$z0=(Txt 'ChainZoomText')
AbsMove ([int]($cr.X+120)) ([int]($cr.Y+120)); Start-Sleep -Milliseconds 400
[M30]::mouse_event(0x0800,0,0,720,[UIntPtr]::Zero); Start-Sleep -Milliseconds 900    # 滚上 6 格
$z1=(Txt 'ChainZoomText')
[M30]::mouse_event(0x0800,0,0,-1440,[UIntPtr]::Zero); Start-Sleep -Milliseconds 900  # 滚下 12 格
$z2=(Txt 'ChainZoomText')
Write-Output ("  缩放: '" + $z0 + "' → 滚上 '" + $z1 + "' → 滚下 '" + $z2 + "'")
Write-Output '=== 自动排列 ==='
$n1=Rect 'ChainCanvasNode_1'; $n2=Rect 'ChainCanvasNode_2'
Write-Output ("  排列前: Node_1 X=" + $(if($n1){[int]$n1.X}else{'n/a'}) + " Node_2 X=" + $(if($n2){[int]$n2.X}else{'n/a'}) + " Node_3 X=" + $(if((Rect 'ChainCanvasNode_3')){[int](Rect 'ChainCanvasNode_3').X}else{'n/a'}))
ClickId 'ChainAutoLayoutBtn' | Out-Null; Start-Sleep 2
$n1b=Rect 'ChainCanvasNode_1'; $n2b=Rect 'ChainCanvasNode_2'; $n3b=Rect 'ChainCanvasNode_3'
Write-Output ("  排列后: Node_1 X=" + $(if($n1b){[int]$n1b.X}else{'n/a'}) + " Node_2 X=" + $(if($n2b){[int]$n2b.X}else{'n/a'}) + " Node_3 X=" + $(if($n3b){[int]$n3b.X}else{'n/a'}))
Write-Output ("  自动排列后 ChainMsg = " + (Txt 'ChainMsg'))
Write-Output ("  节点顺序: " + (Txt 'ChainCanvasNode_1') + " → " + (Txt 'ChainCanvasNode_2') + " → " + (Txt 'ChainCanvasNode_3'))
Write-Output '=== 应用前小窗截图 ==='
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Write-Output 'DONE'
