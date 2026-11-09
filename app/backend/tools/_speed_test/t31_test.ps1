$ErrorActionPreference='Continue'
# T30/T31 实测：增强tab合并 / 节点1=A（用用户真实产物 #78 复现bug）/ 双击节点1展开血统 / 单节点应用 / 画布平移
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$shotDir="$bin\speed_verify"
$py='F:\DSH\LocalMediaPlayer-Workspace\app\backend\.venv\Scripts\python.exe'
$db='F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public class M32 {
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
function AbsMove($x,$y){ $sw=[M32]::GetSystemMetrics(0); $sh=[M32]::GetSystemMetrics(1)
  [M32]::mouse_event(0x8001,[uint32]([math]::Round($x*65535.0/$sw)),[uint32]([math]::Round($y*65535.0/$sh)),0,[UIntPtr]::Zero) }
function DragBtn($fromRect,$toX,$toY,$down,$up,$holdMs){
  $fx=[int]($fromRect.X + $fromRect.Width/2); $fy=[int]($fromRect.Y + $fromRect.Height/2)
  AbsMove $fx $fy; Start-Sleep -Milliseconds 350
  [M32]::mouse_event($down,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 250
  for($i=1;$i -le 12;$i++){ AbsMove ([int]($fx+($toX-$fx)*$i/12)) ([int]($fy+($toY-$fy)*$i/12)); Start-Sleep -Milliseconds 70 }
  Start-Sleep -Milliseconds $holdMs
  [M32]::mouse_event($up,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 800
}
function DoubleClickAt($rect){
  $x=[int]($rect.X+$rect.Width/2); $y=[int]($rect.Y+$rect.Height/2)
  AbsMove $x $y; Start-Sleep -Milliseconds 400
  for($k=0;$k -lt 2;$k++){
    [M32]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 45
    [M32]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 60
  }
  Start-Sleep -Milliseconds 1200
}
function ShotFull($name){ $w=Vw; if(-not $w){return}
  $wr=$w.Current.BoundingRectangle; $hwnd=[IntPtr]$w.Current.NativeWindowHandle
  $bmp=New-Object System.Drawing.Bitmap([int]$wr.Width,[int]$wr.Height)
  $g=[System.Drawing.Graphics]::FromImage($bmp); $hdc=$g.GetHdc()
  [M32]::PrintWindow($hwnd,$hdc,2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
  $out="$shotDir\$name"; $bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); Write-Output ("  截图: " + $out) }

# 拿用户产物 #78 的 output_path（★ 不能删 78/79）
$p78 = & $py -c "import sqlite3;c=sqlite3.connect(r'$db');r=c.execute('select output_path from enhance_tasks where id=78').fetchone();print(r[0] if r else '')"
Write-Output ("  测试用产物 #78 = " + $p78)
if(-not (Test-Path $p78)){ Write-Output 'FAIL: #78 产物不存在'; exit 1 }
$o81 = & $py -c "import sqlite3;c=sqlite3.connect(r'$db');print(c.execute('select count(*) from enhance_tasks').fetchone()[0])"
Write-Output ("  当前 enhance_tasks = " + $o81)

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$env:DSH_NO_AUDIO='1'; $env:DSH_AUTO_OPEN=$p78
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
$w=$null; for($i=0;$i -lt 60;$i++){ Start-Sleep -Milliseconds 500; $w=Vw; if($w){ break } }
if(-not $w){ Write-Output 'FAIL 窗口没起来'; exit 1 }
[M32]::SetForegroundWindow([IntPtr]$w.Current.NativeWindowHandle) | Out-Null; Start-Sleep 2

Write-Output '=== ① 增强 tab 已合并（没有增强器选择）==='
ClickId 'EnhanceTab' | Out-Null; Start-Sleep 4
foreach($id in 'EnhanceTab','EnhanceTargetText','EnhanceMsg','EnhanceFullList','EnhanceTaskList','EnhanceVersionList','EnhanceAllList'){
  $e=FindId $id; Write-Output ("  " + $id.PadRight(20) + " 存在=" + [bool]$e + $(if($e -and $e.Current.Name){" 文字='" + $e.Current.Name + "'"}else{""}))
}
Write-Output '  「增强」tab 可见文本（应无 响度统一/高精度重采样 等增强器按钮）:'
$enhTexts=@()
foreach($t in (Vw).FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$textType)))){ try{ $n=$t.Current.Name; if($n){ $enhTexts += $n } }catch{} }
$hasEnh = $enhTexts | Where-Object { $_ -match '^(响度统一|高精度重采样|FFT 重采样|AI 补高频|语音降噪|视频超分|补帧)$' }
Write-Output ("  增强器按钮文字命中数 = " + @($hasEnh).Count + "  （0 = 已无增强器入口 ✔）")
$enhTexts | Where-Object { $_ -match '增强统一|任务进度|产物|已提交|排队|完成' } | Select-Object -First 8 | ForEach-Object { "   · " + $_ }
ShotFull 't31_enhance_task_tab.png'

Write-Output '=== ⑤ 增强链 tab：播放产物 #78 → 节点1 应显示 78 的产物名（不是原视频）==='
ClickId 'EnhanceChainTab' | Out-Null; Start-Sleep 4
Write-Output ("  ChainCanvasSource(卡片标题) = " + (Txt 'ChainCanvasSource'))
Write-Output ("  ChainSourceTitle(节点1 显示名) = " + (Txt 'ChainSourceTitle'))
Write-Output ("  ChainEditHint = " + (Txt 'ChainEditHint'))
Write-Output ("  " + (Txt 'ChainCanvasStats'))

Write-Output '=== ⑤b 双击画布上的节点1 → 下方列表展开血统 O→…→A ==='
$srcRect=Rect 'ChainCanvasSource'
if($srcRect){ DoubleClickAt $srcRect } else { Write-Output '  ✗ 找不到源节点卡' }
Write-Output ("  ChainMsg = " + (Txt 'ChainMsg'))
Write-Output '  下方列表可见文本（应出现祖先链）:'
foreach($t in (Vw).FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$textType)))){ try{ $n=$t.Current.Name; if($n -match '原始视频|节点1：|节点：|深度'){ Write-Output ("   · " + $n) } }catch{} }
ShotFull 't31_node1_is_product_expanded.png'

Write-Output '=== ② 单节点也能跑：加 1 个节点 → 应用（source_path 应为 #78 的产物）==='
ClickId 'ChainAddNodeBtn' | Out-Null; Start-Sleep 2
$mi=$null
foreach($e in $root.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$menuType)))){ try{ if($e.Current.Name -like '响度统一*'){ $mi=$e; break } }catch{} }
if($mi){ $mi.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2; Write-Output '  已加 1 个节点: 响度统一' }
Write-Output ("  画布: " + (Txt 'ChainCanvasStats'))
ClickId 'ChainApplyBtn' | Out-Null
$msg=''; for($i=0;$i -lt 20;$i++){ Start-Sleep 2; $msg=(Txt 'ChainMsg'); if($msg -match '已提交|失败|未就绪'){ break } }
Write-Output ("  ChainMsg = " + $msg)
$newId = & $py -c @"
import sqlite3
c=sqlite3.connect(r'$db')
r=c.execute('select id,parent_id,media_id,enhancer,status,substr(source_path,1,120) from enhance_tasks order by id desc limit 1').fetchone()
print('%s|%s|%s|%s|%s|%s' % r)
"@
Write-Output ("  新任务(id|parent|media|enhancer|status|source) = " + $newId)
ShotFull 't31_single_node_applied.png'

Write-Output '=== ⑥ 画布平移：中键拖 + 空白处左键拖 ==='
$before=(Txt 'ChainCanvasStats')
$cr=Rect 'ChainCanvas'
$p0 = $cr
# 中键拖：从画布中部往右拖
$from=New-Object System.Drawing.RectangleF([float]($cr.X+150),[float]($cr.Y+120),[float]40,[float]40)
DragBtn $from ([int]($cr.X+330)) ([int]($cr.Y+120)) 0x0020 0x0040 150
$afterMid=(Txt 'ChainCanvasStats')
# 空白处左键拖：画布右下角空白
$from2=New-Object System.Drawing.RectangleF([float]($cr.X+$cr.Width-90),[float]($cr.Y+$cr.Height-70),[float]30,[float]30)
DragBtn $from2 ([int]($cr.X+$cr.Width-260)) ([int]($cr.Y+$cr.Height-70)) 0x0002 0x0004 150
$afterLeft=(Txt 'ChainCanvasStats')
Write-Output ("  平移前: " + $before)
Write-Output ("  中键拖后: " + $afterMid)
Write-Output ("  空白左键拖后: " + $afterLeft)
Write-Output '  dm 平移日志:'
Select-String -Path "$bin\dm_debug.log" -Pattern '开始平移|结束平移|双击节点1|节点1 探测' -Encoding UTF8 | Select-Object -Last 6 | ForEach-Object { "   " + $_.Line }

Write-Output '=== ⑦ 保留的 AutomationId 逐个检查 ==='
$ids='EnhanceTab','EnhanceChainTab','EnhanceMsg','EnhanceTaskList','EnhanceVersionList','EnhanceAllList','ChainSourceTitle','ChainAddNodeBtn','ChainApplyBtn','ChainResetBtn','ChainMsg','ChainAppliedHint','ChainRowList','ChainCanvas','ChainCanvasSource','ChainCanvasOutput','ChainZoomText','ChainFullscreenBtn','ChainCanvasStats'
$missing=@()
foreach($id in $ids){ $e=FindId $id; if(-not $e){ $missing += $id } }
Write-Output ("  检查 " + $ids.Count + " 个 → 缺失: " + $(if($missing.Count){ $missing -join ',' } else { '无 ✔' }))

Write-Output '=== ⑧ 收尾：只删我造的 id，保留 46/47/51/52/78/79 ==='
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 2
& $py -c @"
import sqlite3, os
db=r'$db'
keep = {46,47,51,52,78,79}
c=sqlite3.connect(db)
rows=[r[0] for r in c.execute('select id from enhance_tasks order by id')]
extra=[i for i in rows if i not in keep]
print('  删除我造的:', extra)
for tid in extra:
    r=c.execute('select output_path from enhance_tasks where id=?',(tid,)).fetchone()
    if r and r[0]:
        try: os.remove(r[0]); print('   删文件', r[0])
        except Exception as e: print('   删文件失败', e)
    c.execute('delete from enhance_tasks where id=?',(tid,))
c.commit()
n=c.execute('select count(*) from enhance_tasks').fetchone()[0]
print('  enhance_tasks =', n, [r[0] for r in c.execute('select id from enhance_tasks order by id')])
for t,want in [('videos',30),('download_tasks',17),('playlists',2),('playlist_items',22)]:
    print('  %-14s = %d (基线 %d)' % (t,c.execute('select count(*) from '+t).fetchone()[0],want))
"@
Write-Output ('  settings: ' + ((Get-Content 'F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json' -Raw -Encoding UTF8) -replace "`r?`n",' '))
$bin2='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
Write-Output ("  引擎 md5 = " + (Get-FileHash "$bin2\dsh_engine.dll" -Algorithm MD5).Hash.Substring(0,8))
Write-Output 'DONE'
