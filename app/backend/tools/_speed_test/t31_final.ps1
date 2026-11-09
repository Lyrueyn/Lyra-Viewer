$ErrorActionPreference='Continue'
# T31 终验：①单节点应用跑通（产物文件）②应用后 ChainSourceTitle/ChainMsg/ChainRowList 仍在 ③平移偏移前后 ④节点1=产物名 ⑤增强任务 tab
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$shotDir="$bin\speed_verify"
$py='F:\DSH\LocalMediaPlayer-Workspace\app\backend\.venv\Scripts\python.exe'
$db='F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db'
$recall='F:\DSH\LocalMediaPlayer-Workspace\app\data\downloads\【洛天依原创曲】再声 Recall【2024官方十二周年生贺曲】 [1080P+].mp4'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public class M34 {
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int data, UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
'@
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
$textType=[System.Windows.Automation.ControlType]::Text
$menuType=[System.Windows.Automation.ControlType]::MenuItem
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }
function Txt($id){ $e=FindId $id; if($e){ return $e.Current.Name } else { return '<不在树里>' } }
function ClickId($id){ $e=FindId $id; if($e){ try{ $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }catch{ return $false } } else { Write-Output ("  找不到 " + $id); return $false } }
function Vw(){ $p=Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1; if(-not $p){return $null}
  $cond=New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty,[int]$p.Id)
  foreach($w in $auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)){ try{ if($w.Current.Name -eq '视频'){return $w} }catch{} }; return $null }
function Rect($id){ $e=FindId $id; if(-not $e){ return $null }; return $e.Current.BoundingRectangle }
function AbsMove($x,$y){ $sw=[M34]::GetSystemMetrics(0); $sh=[M34]::GetSystemMetrics(1)
  [M34]::mouse_event(0x8001,[uint32]([math]::Round($x*65535.0/$sw)),[uint32]([math]::Round($y*65535.0/$sh)),0,[UIntPtr]::Zero) }
function DragBtn($fromRect,$toX,$toY,$down,$up){
  $fx=[int]($fromRect.X + $fromRect.Width/2); $fy=[int]($fromRect.Y + $fromRect.Height/2)
  AbsMove $fx $fy; Start-Sleep -Milliseconds 300
  [M34]::mouse_event($down,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 200
  for($i=1;$i -le 10;$i++){ AbsMove ([int]($fx+($toX-$fx)*$i/10)) ([int]($fy+($toY-$fy)*$i/10)); Start-Sleep -Milliseconds 60 }
  Start-Sleep -Milliseconds 120
  [M34]::mouse_event($up,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 700
}
function ShotFull($name){ $w=Vw; if(-not $w){return}
  $wr=$w.Current.BoundingRectangle; $hwnd=[IntPtr]$w.Current.NativeWindowHandle
  $bmp=New-Object System.Drawing.Bitmap([int]$wr.Width,[int]$wr.Height)
  $g=[System.Drawing.Graphics]::FromImage($bmp); $hdc=$g.GetHdc()
  [M34]::PrintWindow($hwnd,$hdc,2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
  $out="$shotDir\$name"; $bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); Write-Output ("  截图: " + $out) }
function StartApp($open){
  Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
  if($open){ $env:DSH_AUTO_OPEN=$open } else { Remove-Item Env:DSH_AUTO_OPEN -EA SilentlyContinue }
  $env:DSH_NO_AUDIO='1'
  Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
  $w=$null; for($i=0;$i -lt 60;$i++){ Start-Sleep -Milliseconds 500; $w=Vw; if($w){ break } }
  if($w){ [M34]::SetForegroundWindow([IntPtr]$w.Current.NativeWindowHandle) | Out-Null; Start-Sleep 3 }
  return $w
}

$baseIds = & $py -c "import sqlite3;c=sqlite3.connect(r'$db');print(','.join(str(r[0]) for r in c.execute('select id from enhance_tasks order by id')))"
Write-Output ("  基线 enhance ids = " + $baseIds)

Write-Output '=== A) 普通视频：加 1 个节点 → 应用（证明单节点也能跑）==='
$w = StartApp $recall
if(-not $w){ Write-Output 'FAIL 窗口没起来'; exit 1 }
ClickId 'EnhanceChainTab' | Out-Null; Start-Sleep 4
Write-Output ("  节点1 = " + (Txt 'ChainSourceTitle'))
Write-Output ("  画布 = " + (Txt 'ChainCanvasStats'))
ClickId 'ChainAddNodeBtn' | Out-Null; Start-Sleep 2
$mi=$null
foreach($e in $root.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$menuType)))){ try{ if($e.Current.Name -like '响度统一*'){ $mi=$e; break } }catch{} }
if($mi){ $mi.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2; Write-Output '  已加 1 个节点（响度统一）' }
Write-Output ("  画布 = " + (Txt 'ChainCanvasStats'))
ClickId 'ChainApplyBtn' | Out-Null
$msg=''; for($i=0;$i -lt 15;$i++){ Start-Sleep 2; $msg=(Txt 'ChainMsg'); if($msg -match '已提交|失败|未就绪'){ break } }
Write-Output ("  ChainMsg = " + $msg)
$newId = & $py -c "import sqlite3;c=sqlite3.connect(r'$db');r=c.execute('select id from enhance_tasks order by id desc limit 1').fetchone();print(r[0] if r else 0)"
Write-Output ("  新任务 id = " + $newId)
$status=''; for($i=0;$i -lt 90;$i++){
  Start-Sleep 3
  $row = & $py -c "import sqlite3,os;c=sqlite3.connect(r'$db');r=c.execute('select status,output_path,error from enhance_tasks where id=?',($newId,)).fetchone();print((r[0] if r else 'gone')+'|'+(r[1] or '')+'|'+((r[2] or '').replace('|',' ')))"
  $status = ($row -split '\|')[0]
  if($status -match 'done|failed'){ break }
}
$parts = $row -split '\|'
Write-Output ("  新任务状态 = " + $parts[0])
$outPath = $parts[1]
if($outPath -and (Test-Path $outPath)){
  $fi=Get-Item $outPath; Write-Output ("  ★ 产物文件 = " + $fi.Name + "  大小 = " + [math]::Round($fi.Length/1MB,2) + " MB  （单节点也能跑通 ✔）")
} else { Write-Output ("  ✗ 产物文件不存在: " + $outPath) }

Write-Output '=== B) 应用后（只读态）关键 id 仍在（Lead 关注的 ②）==='
foreach($id in 'ChainSourceTitle','ChainMsg','ChainRowList','ChainAppliedHint','ChainResetBtn','ChainCanvasSource','ChainCanvasStats'){
  $e=FindId $id; Write-Output ("  " + $id.PadRight(18) + " 存在=" + [bool]$e + $(if($e -and $e.Current.Name){" 文字='" + $e.Current.Name + "'"}else{""}))
}
Write-Output '  下方列表文本（应有新产物行）:'
foreach($t in (Vw).FindAll($desc,(New-Object System.Management.Automation.PropertyCondition([System.Management.Automation.AutomationProperty]::null,$null)))) { } # no-op
$w2=Vw
foreach($t in $w2.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$textType)))){ try{ $n=$t.Current.Name; if($n -match '响度统一|节点：'){ Write-Output ("   · " + $n) } }catch{} }
ShotFull 't31_single_node_applied.png'

Write-Output '=== C) 画布平移（中键拖 / 空白处左键拖）偏移前后 ==='
$p0=(Txt 'ChainCanvasStats')
$cr=Rect 'ChainCanvas'
$f1=New-Object System.Drawing.RectangleF([float]($cr.X+160),[float]($cr.Y+100),[float]30,[float]30)
DragBtn $f1 ([int]($cr.X+340)) ([int]($cr.Y+100)) 0x0020 0x0040
$p1=(Txt 'ChainCanvasStats')
$f2=New-Object System.Drawing.RectangleF([float]($cr.X+$cr.Width-80),[float]($cr.Y+$cr.Height-60),[float]20,[float]20)
DragBtn $f2 ([int]($cr.X+$cr.Width-280)) ([int]($cr.Y+$cr.Height-60)) 0x0002 0x0004
$p2=(Txt 'ChainCanvasStats')
Write-Output ("  平移前:     " + $p0)
Write-Output ("  中键拖后:   " + $p1)
Write-Output ("  空白左键拖: " + $p2)

Write-Output '=== D) 清理我造的任务（保留 46/47/51/52/78/79）==='
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
& $py -c @"
import sqlite3, os
db=r'$db'; keep={46,47,51,52,78,79}
c=sqlite3.connect(db)
extra=[r[0] for r in c.execute('select id from enhance_tasks order by id') if r[0] not in keep]
print('  删除我造的:', extra)
for tid in extra:
    r=c.execute('select output_path from enhance_tasks where id=?',(tid,)).fetchone()
    if r and r[0] and os.path.exists(r[0]):
        try: os.remove(r[0]); print('   删产物文件:', os.path.basename(r[0]))
        except Exception as e: print('   删文件失败', e)
    c.execute('delete from enhance_tasks where id=?',(tid,))
c.commit()
print('  enhance_tasks =', c.execute('select count(*) from enhance_tasks').fetchone()[0], [r[0] for r in c.execute('select id from enhance_tasks order by id')])
"@

Write-Output '=== E) 播放产物 #78 → 节点1 显示产物名（新构建再确认）==='
$p78 = & $py -c "import sqlite3;c=sqlite3.connect(r'$db');r=c.execute('select output_path from enhance_tasks where id=78').fetchone();print(r[0] if r else '')"
$w3 = StartApp $p78
if($w3){
  ClickId 'EnhanceChainTab' | Out-Null; Start-Sleep 5
  Write-Output ("  ChainCanvasSource = " + (Txt 'ChainCanvasSource'))
  Write-Output ("  ChainSourceTitle  = " + (Txt 'ChainSourceTitle'))
  Write-Output ("  ChainEditHint     = " + (Txt 'ChainEditHint'))
  ClickId 'ChainSourceExpandBtn' | Out-Null
  for($i=0;$i -lt 8;$i++){ Start-Sleep 1; if((Txt 'ChainMsg') -match '已展开'){ break } }
  Write-Output ("  ChainMsg = " + (Txt 'ChainMsg'))
  $hasSrc=$false
  foreach($n in (Vw).FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$textType)))){ try{ if($n.Current.Name -match '原始视频'){ Write-Output ("   · 血统行: " + $n.Current.Name); $hasSrc=$true } }catch{} }
  ShotFull 't31_node1_is_product_expanded.png'
  Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
}
Write-Output ("  最终 enhance_tasks = " + (& $py -c "import sqlite3;c=sqlite3.connect(r'$db');print(c.execute('select count(*) from enhance_tasks').fetchone()[0], [r[0] for r in c.execute('select id from enhance_tasks order by id')])"))
Write-Output ('  settings: ' + ((Get-Content 'F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json' -Raw -Encoding UTF8) -replace "`r?`n",' '))
Write-Output ("  引擎 md5 = " + (Get-FileHash "$bin\dsh_engine.dll" -Algorithm MD5).Hash.Substring(0,8) + " / dll = " + (Get-FileHash "$bin\WinUIGui.dll" -Algorithm MD5).Hash.Substring(0,8))
Write-Output 'DONE'
