$ErrorActionPreference='Continue'
# T29 节点图画布实测：UIA 结构 + 真实鼠标拖动排序 + 滚轮缩放 + 应用只读 + 截图
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$recall='F:\DSH\LocalMediaPlayer-Workspace\app\data\downloads\【洛天依原创曲】再声 Recall【2024官方十二周年生贺曲】 [1080P+].mp4'
$shotDir="$bin\speed_verify"
$py='F:\DSH\LocalMediaPlayer-Workspace\app\backend\.venv\Scripts\python.exe'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
$sig = @'
using System; using System.Runtime.InteropServices;
public class M {
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int data, UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
'@
Add-Type -TypeDefinition $sig
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
$textType=[System.Windows.Automation.ControlType]::Text
$menuType=[System.Windows.Automation.ControlType]::MenuItem
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }
function Txt($id){ $e=FindId $id; if($e){ return $e.Current.Name } else { return '<不在树里>' } }
function ClickId($id){ $e=FindId $id; if($e){ try{ $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }catch{ Write-Output ("  Invoke失败 " + $id + ": " + $_.Exception.Message); return $false } } else { Write-Output ("  找不到 " + $id); return $false } }
function Vw(){ $p=Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1; if(-not $p){return $null}
  $cond=New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty,[int]$p.Id)
  foreach($w in $auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)){ try{ if($w.Current.Name -eq '视频'){return $w} }catch{} }; return $null }
function CardTexts($id){ $e=FindId $id; if(-not $e){ return '<无>' }
  $names=@(); foreach($t in $e.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$textType)))){ try{ $names += $t.Current.Name }catch{} }; return ($names -join ' | ') }
function Rect($id){ $e=FindId $id; if(-not $e){ return $null }; return $e.Current.BoundingRectangle }
function Shot($name, $rect){
  $w=Vw; if(-not $w){ return }
  $wr=$w.Current.BoundingRectangle
  $hwnd=[IntPtr]$w.Current.NativeWindowHandle
  $bmp=New-Object System.Drawing.Bitmap([int]$wr.Width,[int]$wr.Height)
  $g=[System.Drawing.Graphics]::FromImage($bmp); $hdc=$g.GetHdc()
  [M]::PrintWindow($hwnd,$hdc,2) | Out-Null
  $g.ReleaseHdc($hdc); $g.Dispose()
  if($rect){
    $crop=New-Object System.Drawing.Rectangle([int]($rect.X-$wr.X-10),[int]($rect.Y-$wr.Y-10),[int]($rect.Width+20),[int]($rect.Height+20))
    $crop.Intersect((New-Object System.Drawing.Rectangle(0,0,[int]$wr.Width,[int]$wr.Height)))
    if($crop.Width -gt 10 -and $crop.Height -gt 10){
      $c2=$bmp.Clone($crop,$bmp.PixelFormat); $out="$shotDir\$name"; $c2.Save($out,[System.Drawing.Imaging.ImageFormat]::Png); $c2.Dispose(); $bmp.Dispose()
      Write-Output ("  截图: " + $out + " （裁剪 " + $crop.Width + "x" + $crop.Height + "）"); return
    }
  }
  $out="$shotDir\$name"; $bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  Write-Output ("  截图: " + $out + " （整窗 " + [int]$wr.Width + "x" + [int]$wr.Height + "）")
}
function DragTo($fromRect,$toX,$toY){
  $fx=[int]($fromRect.X + $fromRect.Width/2); $fy=[int]($fromRect.Y + $fromRect.Height/2)
  [M]::SetCursorPos($fx,$fy) | Out-Null; Start-Sleep -Milliseconds 250
  [M]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 200   # left down
  for($i=1;$i -le 8;$i++){
    $x=[int]($fx + ($toX-$fx)*$i/8); $y=[int]($fy + ($toY-$fy)*$i/8)
    [M]::SetCursorPos($x,$y) | Out-Null; [M]::mouse_event(0x0001,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 60
  }
  [M]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 600     # left up
}
function ClickAt($rect){
  $x=[int]($rect.X + $rect.Width/2); $y=[int]($rect.Y + $rect.Height/2)
  [M]::SetCursorPos($x,$y) | Out-Null; Start-Sleep -Milliseconds 200
  [M]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 80
  [M]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 500
}
function WheelAt($rect,$delta){
  $x=[int]($rect.X + $rect.Width/2); $y=[int]($rect.Y + $rect.Height/2)
  [M]::SetCursorPos($x,$y) | Out-Null; Start-Sleep -Milliseconds 200
  [M]::mouse_event(0x0800,0,0,$delta,[UIntPtr]::Zero); Start-Sleep -Milliseconds 500
}

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
$env:DSH_NO_AUDIO='1'; $env:DSH_AUTO_OPEN=$recall
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
$w=$null; for($i=0;$i -lt 60;$i++){ Start-Sleep -Milliseconds 500; $w=Vw; if($w){ Write-Output ("视频窗就绪 " + [int](($i+1)*0.5) + "s"); break } }
if(-not $w){ Write-Output 'FAIL 窗口没起来'; exit 1 }
[M]::SetForegroundWindow([IntPtr]$w.Current.NativeWindowHandle) | Out-Null
Start-Sleep 2
ClickId 'EnhanceChainTab' | Out-Null
Start-Sleep 4

Write-Output '=== 1) 画布基础结构（初始：只有源+输出）==='
foreach($id in 'ChainCanvas','ChainCanvasSource','ChainCanvasOutput','ChainZoomText','ChainSourceTitle','ChainAddNodeBtn','ChainApplyBtn','ChainMsg','ChainAppliedHint','ChainRowList'){
  $e=FindId $id; Write-Output ("  " + $id.PadRight(20) + " 存在=" + [bool]$e + $(if($e){ " 文字='" + $e.Current.Name + "'" } else { "" }))
}
Write-Output ("  源节点卡片内容: " + (CardTexts 'ChainCanvasSource'))

Write-Output '=== 2) 加 2 个节点（UIA 点 Flyout）==='
foreach($label in '响度统一','高精度重采样'){
  ClickId 'ChainAddNodeBtn' | Out-Null; Start-Sleep 2
  $mi=$null
  foreach($e in $root.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$menuType)))){ try{ if($e.Current.Name -like ($label+'*')){ $mi=$e; break } }catch{} }
  if($mi){ $mi.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2; Write-Output ("  已加: " + $label) } else { Write-Output ("  ✗ 没找到菜单项 " + $label) }
}
Write-Output '=== 3) 画布节点/端口/连线（UIA 逐个列 id + 屏幕坐标）==='
foreach($id in 'ChainCanvasSource','ChainCanvasNode_1','ChainCanvasNode_2','ChainCanvasOutput'){
  $r=Rect $id; Write-Output ("  " + $id.PadRight(20) + " 内容='" + (CardTexts $id) + "' X=" + $(if($r){[int]$r.X}else{'n/a'}))
}
Write-Output ("  ChainCanvasStats = " + (Txt 'ChainCanvasStats') + "   ← 端口/连线数量的可读证据（Shapes 无 UIA peer）")
Write-Output ("  缩放文字 = " + (Txt 'ChainZoomText'))
$canvasRect=Rect 'ChainCanvas'
Shot 't29_canvas_before.png' $canvasRect

Write-Output '=== 4) 真实鼠标拖动：把「高精度重采样」(节点2) 拖到最左 ==='
$r2=Rect 'ChainCanvasNode_2'; $r1=Rect 'ChainCanvasNode_1'
Write-Output ("  拖动前: Node_1 X=" + [int]$r1.X + " 内容='" + (CardTexts 'ChainCanvasNode_1') + "' ; Node_2 X=" + [int]$r2.X + " 内容='" + (CardTexts 'ChainCanvasNode_2') + "'")
DragTo $r2 ([int]($r1.X - 70)) ([int]$r1.Y)
Start-Sleep 1
$r1b=Rect 'ChainCanvasNode_1'; $r2b=Rect 'ChainCanvasNode_2'
Write-Output ("  拖动后: Node_1 X=" + $(if($r1b){[int]$r1b.X}else{'n/a'}) + " 内容='" + (CardTexts 'ChainCanvasNode_1') + "' ; Node_2 X=" + $(if($r2b){[int]$r2b.X}else{'n/a'}) + " 内容='" + (CardTexts 'ChainCanvasNode_2') + "'")
Write-Output ("  ChainMsg = " + (Txt 'ChainMsg'))
Shot 't29_canvas_after_drag.png' (Rect 'ChainCanvas')

Write-Output '=== 5) 点击选中（亮蓝描边）+ 截图 ==='
$r1c=Rect 'ChainCanvasNode_1'
if($r1c){ ClickAt $r1c }
Start-Sleep 1
Write-Output ("  选中后 ChainMsg/日志见 dm_debug.log; 重画后 Node_1 内容='" + (CardTexts 'ChainCanvasNode_1') + "'")
Shot 't29_canvas_selected.png' (Rect 'ChainCanvas')

Write-Output '=== 6) 滚轮缩放 ==='
$before=(Txt 'ChainZoomText')
$cr=Rect 'ChainCanvas'
WheelAt $cr 240
$after1=(Txt 'ChainZoomText')
WheelAt $cr -480
$after2=(Txt 'ChainZoomText')
Write-Output ("  缩放: '" + $before + "' → 滚上 '" + $after1 + "' → 滚下 '" + $after2 + "'")

Write-Output '=== 7) 应用（真实 submit_chain）→ 只读 + 新建一条链 ==='
$maxBefore = & $py -c "import sqlite3;c=sqlite3.connect(r'F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db');print(c.execute('select max(id) from enhance_tasks').fetchone()[0] or 0)"
ClickId 'ChainApplyBtn' | Out-Null
$msg=''
for($i=0;$i -lt 15;$i++){ Start-Sleep 2; $msg=(Txt 'ChainMsg'); if($msg -match '已提交|失败|未就绪'){ break } }
Write-Output ("  ChainMsg = " + $msg)
Write-Output ("  应用后: ChainAddNodeBtn存在=" + [bool](FindId 'ChainAddNodeBtn') + " ChainApplyBtn存在=" + [bool](FindId 'ChainApplyBtn') + " ChainResetBtn存在=" + [bool](FindId 'ChainResetBtn') + " ChainAppliedHint='" + (Txt 'ChainAppliedHint') + "'")
$maxAfter = & $py -c "import sqlite3;c=sqlite3.connect(r'F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db');print(c.execute('select max(id) from enhance_tasks').fetchone()[0] or 0)"
Write-Output ("  新增任务: " + $maxBefore + " → " + $maxAfter)

Write-Output '=== 8) 下方产物列表 / 双击展开祖先 行为未变 ==='
$exp=FindId 'ChainExpand_46'
if($exp){ $exp.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 4
  Write-Output '  展开 46 后的文本:'
  foreach($t in (Vw).FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$textType)))){ try{ $n=$t.Current.Name; if($n -match '原始视频|节点1：|深度'){ Write-Output ("   · " + $n) } }catch{} }
} else { Write-Output '  ✗ 找不到 ChainExpand_46' }

Write-Output '=== 9) 收尾 ==='
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
Write-Output ('settings: ' + ((Get-Content 'F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json' -Raw -Encoding UTF8) -replace "`r?`n",' '))
Write-Output 'DONE'
