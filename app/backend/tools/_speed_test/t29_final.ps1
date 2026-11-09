$ErrorActionPreference='Continue'
# T29b 最终实测：全屏编辑 + 节点图结构 + 真实鼠标拖动排序 + 滚轮缩放 + 应用只读 + 截图
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$recall='F:\DSH\LocalMediaPlayer-Workspace\app\data\downloads\【洛天依原创曲】再声 Recall【2024官方十二周年生贺曲】 [1080P+].mp4'
$shotDir="$bin\speed_verify"
$dm="$bin\dm_debug.log"
$py='F:\DSH\LocalMediaPlayer-Workspace\app\backend\.venv\Scripts\python.exe'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public class M29 {
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int data, UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
}
'@
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
$textType=[System.Windows.Automation.ControlType]::Text
$menuType=[System.Windows.Automation.ControlType]::MenuItem
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }
function Txt($id){ $e=FindId $id; if($e){ return $e.Current.Name } else { return '<不在树里>' } }
function ClickId($id){ $e=FindId $id; if($e){ try{ $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }catch{ Write-Output ("  Invoke失败 " + $id + ": " + $_.Exception.Message); return $false } } else { Write-Output ("  找不到 " + $id); return $false } }
function Vw(){ $p=Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1; if(-not $p){return $null}
  $cond=New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty,[int]$p.Id)
  foreach($w in $auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)){ try{ if($w.Current.Name -eq '视频'){return $w} }catch{} }; return $null }
function Rect($id){ $e=FindId $id; if(-not $e){ return $null }; return $e.Current.BoundingRectangle }
function ShotFull($name){
  $w=Vw; if(-not $w){ return }
  $wr=$w.Current.BoundingRectangle; $hwnd=[IntPtr]$w.Current.NativeWindowHandle
  $bmp=New-Object System.Drawing.Bitmap([int]$wr.Width,[int]$wr.Height)
  $g=[System.Drawing.Graphics]::FromImage($bmp); $hdc=$g.GetHdc()
  $ok=[M29]::PrintWindow($hwnd,$hdc,2); $g.ReleaseHdc($hdc); $g.Dispose()
  $out="$shotDir\$name"; $bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  Write-Output ("  截图: " + $out + "（" + [int]$wr.Width + "x" + [int]$wr.Height + " PrintWindow=" + $ok + "）")
}
function ShotCrop($name,$rect){
  $w=Vw; if(-not $w){ return }
  $wr=$w.Current.BoundingRectangle; $hwnd=[IntPtr]$w.Current.NativeWindowHandle
  $bmp=New-Object System.Drawing.Bitmap([int]$wr.Width,[int]$wr.Height)
  $g=[System.Drawing.Graphics]::FromImage($bmp); $hdc=$g.GetHdc()
  [M29]::PrintWindow($hwnd,$hdc,2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
  $crop=New-Object System.Drawing.Rectangle([int]($rect.X-$wr.X-8),[int]($rect.Y-$wr.Y-8),[int]($rect.Width+16),[int]($rect.Height+16))
  $crop.Intersect((New-Object System.Drawing.Rectangle(0,0,[int]$wr.Width,[int]$wr.Height)))
  if($crop.Width -gt 20){ $c2=$bmp.Clone($crop,$bmp.PixelFormat); $out="$shotDir\$name"; $c2.Save($out,[System.Drawing.Imaging.ImageFormat]::Png); $c2.Dispose() }
  else { $out="$shotDir\$name"; $bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png) }
  $bmp.Dispose(); Write-Output ("  截图: " + $out)
}
function AbsMove($x,$y){
  $sw=[M29]::GetSystemMetrics(0); $sh=[M29]::GetSystemMetrics(1)
  $nx=[uint32]([math]::Round($x*65535.0/$sw)); $ny=[uint32]([math]::Round($y*65535.0/$sh))
  [M29]::mouse_event(0x8001,$nx,$ny,0,[UIntPtr]::Zero)
}
function DragTo($fromRect,$toX,$toY){
  $fx=[int]($fromRect.X + $fromRect.Width/2); $fy=[int]($fromRect.Y + $fromRect.Height/2)
  AbsMove $fx $fy; Start-Sleep -Milliseconds 350
  [M29]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 300
  for($i=1;$i -le 12;$i++){
    AbsMove ([int]($fx+($toX-$fx)*$i/12)) ([int]($fy+($toY-$fy)*$i/12)); Start-Sleep -Milliseconds 80
  }
  Start-Sleep -Milliseconds 200
  [M29]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 900
}
function ClickAt($rect){
  AbsMove ([int]($rect.X+$rect.Width/2)) ([int]($rect.Y+$rect.Height/2)); Start-Sleep -Milliseconds 300
  [M29]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 100
  [M29]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 700
}
function WheelOver($rect,$delta){
  AbsMove ([int]($rect.X+60)) ([int]($rect.Y+60)); Start-Sleep -Milliseconds 300
  [M29]::mouse_event(0x0800,0,0,$delta,[UIntPtr]::Zero); Start-Sleep -Milliseconds 700
}

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
if(Test-Path $dm){ Move-Item $dm "$shotDir\dm_debug_t29_prev.log" -Force -EA SilentlyContinue }
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
$env:DSH_NO_AUDIO='1'; $env:DSH_AUTO_OPEN=$recall
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
$w=$null; for($i=0;$i -lt 60;$i++){ Start-Sleep -Milliseconds 500; $w=Vw; if($w){ break } }
if(-not $w){ Write-Output 'FAIL 窗口没起来'; exit 1 }
[M29]::SetForegroundWindow([IntPtr]$w.Current.NativeWindowHandle) | Out-Null
Start-Sleep 2
ClickId 'EnhanceChainTab' | Out-Null; Start-Sleep 4

Write-Output '=== 1) 小窗结构 ==='
foreach($id in 'ChainCanvas','ChainCanvasSource','ChainCanvasOutput','ChainCanvasStats','ChainZoomText','ChainFullscreenBtn','ChainRowList'){
  $e=FindId $id; Write-Output ("  " + $id.PadRight(20) + " 存在=" + [bool]$e + $(if($e){" 文字='" + $e.Current.Name + "'"}else{""}))
}
Write-Output '=== 2) 加 2 个节点 ==='
foreach($label in '响度统一','高精度重采样'){
  ClickId 'ChainAddNodeBtn' | Out-Null; Start-Sleep 2
  $mi=$null
  foreach($e in $root.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$menuType)))){ try{ if($e.Current.Name -like ($label+'*')){ $mi=$e; break } }catch{} }
  if($mi){ $mi.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2; Write-Output ("  已加: " + $label) } else { Write-Output ("  ✗ 未找到 " + $label) }
}
foreach($id in 'ChainCanvasSource','ChainCanvasNode_1','ChainCanvasNode_2','ChainCanvasOutput'){ Write-Output ("  " + $id.PadRight(20) + " 内容='" + (Txt $id) + "'") }
Write-Output ("  " + (Txt 'ChainCanvasStats'))

Write-Output '=== 3) ★ 点「全屏编辑」→ 画布应占满整窗 ==='
$smallW = 0; $r=Rect 'ChainCanvas'; if($r){ $smallW=[int]$r.Width }
ClickId 'ChainFullscreenBtn' | Out-Null; Start-Sleep 3
$r2=Rect 'ChainCanvas'
Write-Output ("  画布(ScrollViewer) 宽度: 小窗 " + $smallW + " → 全屏 " + $(if($r2){[int]$r2.Width}else{'n/a'}))
Write-Output ("  ChainFullscreenBtn 文字 = " + (Txt 'ChainFullscreenBtn') + " ; " + (Txt 'ChainCanvasStats'))
ShotFull 't29_fullscreen_canvas.png'
Write-Output '=== 4) 全屏下真实鼠标拖动：把节点2 拖到最左 ==='
$n1=Rect 'ChainCanvasNode_1'; $n2=Rect 'ChainCanvasNode_2'
Write-Output ("  拖动前: Node_1(='" + (Txt 'ChainCanvasNode_1') + "') X=" + $(if($n1){[int]$n1.X}else{'n/a'}) + " ; Node_2(='" + (Txt 'ChainCanvasNode_2') + "') X=" + $(if($n2){[int]$n2.X}else{'n/a'}))
if($n1 -and $n2){ DragTo $n2 ([int]($n1.X - 100)) ([int]$n1.Y) }
Start-Sleep 1
Write-Output ("  拖动后: Node_1(='" + (Txt 'ChainCanvasNode_1') + "') ; Node_2(='" + (Txt 'ChainCanvasNode_2') + "')")
Write-Output ("  ChainMsg = " + (Txt 'ChainMsg'))
Select-String -Path $dm -Pattern '拖动重排|选中节点|自动排列' -Encoding UTF8 | Select-Object -Last 3 | ForEach-Object { "  dm: " + $_.Line }
ShotFull 't29_fullscreen_after_drag.png'
Write-Output '=== 5) 全屏下点击选中 + 截图 ==='
$n1b=Rect 'ChainCanvasNode_1'
if($n1b){ ClickAt $n1b; Start-Sleep 1 }
Select-String -Path $dm -Pattern '选中节点' -Encoding UTF8 | Select-Object -Last 2 | ForEach-Object { "  dm: " + $_.Line }
ShotFull 't29_fullscreen_selected.png'
Write-Output '=== 6) 滚轮缩放 ==='
$cr=Rect 'ChainCanvas'; $z0=(Txt 'ChainZoomText')
WheelOver $cr 240; $z1=(Txt 'ChainZoomText')
WheelOver $cr -480; $z2=(Txt 'ChainZoomText')
Write-Output ("  缩放: '" + $z0 + "' → 滚上 '" + $z1 + "' → 滚下 '" + $z2 + "'")
Write-Output '=== 7) 退出全屏 → 恢复小窗 ==='
ClickId 'ChainFullscreenBtn' | Out-Null; Start-Sleep 3
$r3=Rect 'ChainCanvas'
Write-Output ("  退出后画布宽度 = " + $(if($r3){[int]$r3.Width}else{'n/a'}) + " ; 按钮文字 = " + (Txt 'ChainFullscreenBtn') + " ; " + (Txt 'ChainCanvasStats'))
Write-Output '=== 8) 应用 → 只读 ==='
$maxBefore = & $py -c "import sqlite3;c=sqlite3.connect(r'F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db');print(c.execute('select max(id) from enhance_tasks').fetchone()[0] or 0)"
ClickId 'ChainApplyBtn' | Out-Null
$msg=''; for($i=0;$i -lt 15;$i++){ Start-Sleep 2; $msg=(Txt 'ChainMsg'); if($msg -match '已提交|失败|未就绪'){ break } }
Write-Output ("  ChainMsg = " + $msg)
Write-Output ("  应用后: 加节点存在=" + [bool](FindId 'ChainAddNodeBtn') + " 应用存在=" + [bool](FindId 'ChainApplyBtn') + " 新建一条链存在=" + [bool](FindId 'ChainResetBtn') + " Hint='" + (Txt 'ChainAppliedHint') + "'")
$maxAfter = & $py -c "import sqlite3;c=sqlite3.connect(r'F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db');print(c.execute('select max(id) from enhance_tasks').fetchone()[0] or 0)"
Write-Output ("  新增任务: " + $maxBefore + " → " + $maxAfter)
Write-Output '=== 9) 下方列表 / 双击展开 未变 ==='
$exp=FindId 'ChainExpand_46'
if($exp){ $exp.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 4
  foreach($t in (Vw).FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$textType)))){ try{ $n=$t.Current.Name; if($n -match '原始视频'){ Write-Output ("   · " + $n) } }catch{} }
} else { Write-Output '  ✗ 找不到 ChainExpand_46' }
Write-Output '=== 10) 收尾 ==='
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 2
if([int]$maxAfter -gt [int]$maxBefore){
  & $py -c @"
import sqlite3, os
db=r'F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db'; c=sqlite3.connect(db)
for tid in range($maxBefore+1, $maxAfter+1):
    r=c.execute('select output_path from enhance_tasks where id=?',(tid,)).fetchone()
    if r and r[0]:
        try: os.remove(r[0])
        except Exception as e: print('  删文件失败', e)
    c.execute('delete from enhance_tasks where id=?',(tid,))
c.commit()
print('  enhance_tasks =', c.execute('select count(*) from enhance_tasks').fetchone()[0], [r[0] for r in c.execute('select id from enhance_tasks order by id')])
"@
}
Write-Output ('  settings: ' + ((Get-Content 'F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json' -Raw -Encoding UTF8) -replace "`r?`n",' '))
Write-Output 'DONE'
