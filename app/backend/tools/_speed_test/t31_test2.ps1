$ErrorActionPreference='Continue'
# T31 补充验证：增强任务 tab（重试点击）/ 节点1=产物名 / 血统展开按钮
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$shotDir="$bin\speed_verify"
$py='F:\DSH\LocalMediaPlayer-Workspace\app\backend\.venv\Scripts\python.exe'
$db='F:\DSH\LocalMediaPlayer-Workspace\app\data\local_media.db'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public class M33 {
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int data, UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
'@
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
$textType=[System.Windows.Automation.ControlType]::Text
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }
function Txt($id){ $e=FindId $id; if($e){ return $e.Current.Name } else { return '<不在树里>' } }
function ClickId($id){ $e=FindId $id; if($e){ try{ $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }catch{ return $false } } else { Write-Output ("  找不到 " + $id); return $false } }
function Vw(){ $p=Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1; if(-not $p){return $null}
  $cond=New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty,[int]$p.Id)
  foreach($w in $auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)){ try{ if($w.Current.Name -eq '视频'){return $w} }catch{} }; return $null }
function Rect($id){ $e=FindId $id; if(-not $e){ return $null }; return $e.Current.BoundingRectangle }
function ShotFull($name){ $w=Vw; if(-not $w){return}
  $wr=$w.Current.BoundingRectangle; $hwnd=[IntPtr]$w.Current.NativeWindowHandle
  $bmp=New-Object System.Drawing.Bitmap([int]$wr.Width,[int]$wr.Height)
  $g=[System.Drawing.Graphics]::FromImage($bmp); $hdc=$g.GetHdc()
  [M33]::PrintWindow($hwnd,$hdc,2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose()
  $out="$shotDir\$name"; $bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); Write-Output ("  截图: " + $out) }
function AllTexts(){ $w=Vw; if(-not $w){ return @() }; $r=@()
  foreach($t in $w.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,$textType)))){ try{ if($t.Current.Name){ $r += $t.Current.Name } }catch{} }; return $r }

$p78 = & $py -c "import sqlite3;c=sqlite3.connect(r'$db');r=c.execute('select output_path from enhance_tasks where id=78').fetchone();print(r[0] if r else '')"
Write-Output ("  播放产物 #78 = " + (Split-Path $p78 -Leaf))
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$env:DSH_NO_AUDIO='1'; $env:DSH_AUTO_OPEN=$p78
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
$w=$null; for($i=0;$i -lt 60;$i++){ Start-Sleep -Milliseconds 500; $w=Vw; if($w){ break } }
if(-not $w){ Write-Output 'FAIL 窗口没起来'; exit 1 }
[M33]::SetForegroundWindow([IntPtr]$w.Current.NativeWindowHandle) | Out-Null; Start-Sleep 3

Write-Output '=== ① 增强任务 tab（带重试：第一次点击可能被窗口激活吞掉）==='
ClickId 'EnhanceTab' | Out-Null; Start-Sleep 3
if(-not (FindId 'EnhanceMsg')){ Write-Output '  第一次点击未生效 → 再点一次'; ClickId 'EnhanceTab' | Out-Null; Start-Sleep 3 }
foreach($id in 'EnhanceTab','EnhanceTargetText','EnhanceMsg','EnhanceTaskList','EnhanceVersionList','EnhanceAllList'){
  $e=FindId $id; Write-Output ("  " + $id.PadRight(20) + " 存在=" + [bool]$e + $(if($e -and $e.Current.Name){" 文字='" + $e.Current.Name + "'"}else{""}))
}
$texts=AllTexts
$enhHit = $texts | Where-Object { $_ -match '^(响度统一|高精度重采样|FFT 重采样|AI 补高频|语音降噪|视频超分|补帧)$' }
Write-Output ("  增强器按钮文字命中 = " + @($enhHit).Count + " （0 ✔ 已无增强器入口）")
Write-Output '  增强任务 tab 可见文本（前 12 条）:'
$texts | Where-Object { $_ -match '增强|任务|产物|统一|完成|排队|运行|失败|选择' } | Select-Object -First 12 | ForEach-Object { "   · " + $_ }
ShotFull 't31_enhance_task_tab.png'

Write-Output '=== ⑤ 增强链：节点1 应显示产物名 ==='
ClickId 'EnhanceChainTab' | Out-Null; Start-Sleep 4
Write-Output ("  ChainCanvasSource = " + (Txt 'ChainCanvasSource'))
Write-Output ("  ChainSourceTitle  = " + (Txt 'ChainSourceTitle'))
Write-Output ("  ChainEditHint     = " + (Txt 'ChainEditHint'))
Write-Output ("  ChainCanvasStats  = " + (Txt 'ChainCanvasStats'))
Write-Output ("  产物列表行数（ChainExpand_* 按钮）: " + @((Vw).FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,'ChainExpand_78')))).Count)

Write-Output '=== ⑤b 点节点1 上的「血统 ↗」按钮 → 下方列表展开 O→…→A ==='
ClickId 'ChainSourceExpandBtn' | Out-Null
for($i=0;$i -lt 8;$i++){ Start-Sleep 1; $m=Txt 'ChainMsg'; if($m -match '已展开|没有更上一层|没找到'){ break } }
Write-Output ("  ChainMsg = " + (Txt 'ChainMsg'))
Write-Output '  下方列表可见文本（祖先链）:'
foreach($n in (AllTexts)){ if($n -match '原始视频|节点1：|节点：|深度'){ Write-Output ("   · " + $n) } }
ShotFull 't31_node1_is_product_expanded.png'
Write-Output ("  dm: ")
Select-String -Path "$bin\dm_debug.log" -Pattern '节点1 探测|展开节点1|祖先链' -Encoding UTF8 | Select-Object -Last 4 | ForEach-Object { "   " + $_.Line }

Write-Output '=== ⑦ 保留 id 检查（此时在增强链 tab，按钮未应用所以都在）==='
$ids='EnhanceTab','EnhanceChainTab','EnhanceMsg','EnhanceTaskList','EnhanceVersionList','EnhanceAllList','ChainSourceTitle','ChainAddNodeBtn','ChainApplyBtn','ChainMsg','ChainRowList','ChainCanvas','ChainCanvasSource','ChainCanvasOutput','ChainZoomText','ChainFullscreenBtn','ChainCanvasStats','ChainSourceExpandBtn'
$missing=@(); foreach($id in $ids){ if(-not (FindId $id)){ $missing += $id } }
Write-Output ("  检查 " + $ids.Count + " 个 → 缺失: " + $(if($missing.Count){ $missing -join ',' } else { '无 ✔' }))

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Write-Output ("  enhance_tasks = " + (& $py -c "import sqlite3;c=sqlite3.connect(r'$db');print(c.execute('select count(*) from enhance_tasks').fetchone()[0], [r[0] for r in c.execute('select id from enhance_tasks order by id')])"))
Write-Output ('  settings: ' + ((Get-Content 'F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json' -Raw -Encoding UTF8) -replace "`r?`n",' '))
Write-Output 'DONE'
