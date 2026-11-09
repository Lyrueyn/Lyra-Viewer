param([int]$WaitPlaySec=12)
$ErrorActionPreference='Continue'
# T18 就地验证（已部署构建）：增强 tab / 7 个增强器 / 版本列表 / 播放增强版 / 失败提示如实显示
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$tools='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools'
$recall='F:\DSH\LocalMediaPlayer-Workspace\app\data\downloads\【洛天依原创曲】再声 Recall【2024官方十二周年生贺曲】 [1080P+].mp4'
$dm="$bin\dm_debug.log"; $el="$bin\dsh_engine.log"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }
function Txt($id){ $e=FindId $id; if($e){ return $e.Current.Name } else { return '<不在 UIA 树里>' } }
function ClickId($id){ $e=FindId $id; if($e){ try{ $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }catch{ Write-Output ("  Invoke 失败 " + $id + ": " + $_.Exception.Message); return $false } } else { Write-Output ("  找不到 " + $id); return $false } }
function AllButtons($w){ $w.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button))) }
function AllTexts($w){ $w.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text))) }

Write-Output '=== 0) 压测试音量到 2% ==='
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\set_test_volume.ps1"
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
if(Test-Path $dm){ Move-Item $dm "$bin\speed_verify\dm_debug_t18_prev.log" -Force -EA SilentlyContinue }
if(Test-Path $el){ Move-Item $el "$bin\speed_verify\dsh_engine_t18_prev.log" -Force -EA SilentlyContinue }

Write-Output '=== 1) 启动并直接打开"再声 Recall"（media_id=58，有两条历史增强产物）==='
$env:DSH_AUTO_OPEN=$recall
Remove-Item Env:DSH_TEST_EOF_LEGACY -EA SilentlyContinue
Remove-Item Env:DSH_NO_AUDIO -EA SilentlyContinue
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 12
$vw=$null
$p=Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1
if($p){ $cond=New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty,[int]$p.Id)
  foreach($w in $auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)){ try{ if($w.Current.Name -eq '视频'){ $vw=$w } }catch{} } }
if(-not $vw){ Write-Output 'FAIL: 视频窗口没起来'; Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; exit 1 }
Write-Output ("  视频窗口 OK: '" + $vw.Current.Name + "'")

Write-Output '=== 2) 点"增强" tab → 读 7 个增强器（中文名 + 说明）==='
ClickId 'EnhanceTab' | Out-Null
Start-Sleep 4
$ids = 'loudness','soxr_hires','fft_hires','audio_sr','denoise','realesrgan','rife'
foreach($k in $ids){
  $b = FindId ("Enhancer_" + $k)
  if($b){ Write-Output ("  [OK] " + $k.PadRight(11) + " 按钮='" + $b.Current.Name + "' 可用=" + $b.Current.IsEnabled + " 说明见下") }
  else { Write-Output ("  [缺失] " + $k) }
}
Write-Output '  --- 增强器说明文字（界面上真正显示的）---'
$texts = AllTexts $vw
foreach($t in $texts){ try{ $n=$t.Current.Name; if($n -and ($n -match 'LUFS|96kHz|libsoxr|补高频|降噪|GPU|超分|补帧|重采样|响度|吃 GPU')){ Write-Output ("   · " + $n) } }catch{} }
Write-Output ("  EnhanceTarget = " + (Txt 'EnhanceTargetText'))

Write-Output '=== 3) 版本列表：这个视频的增强版本 + 全部增强产物 ==='
$btns = AllButtons $vw
$playBtns=@(); foreach($b in $btns){ try{ if($b.Current.Name -eq '播放'){ $playBtns += $b } }catch{} }
Write-Output ("  界面上「播放」按钮数 = " + $playBtns.Count)
foreach($t in (AllTexts $vw)){ try{ $n=$t.Current.Name; if($n -match '重采样#|enhancer|血缘深度|再声'){ Write-Output ("   · " + $n) } }catch{} }

Write-Output '=== 4) 点第一个「播放」（应播放增强版文件）==='
if($playBtns.Count -gt 0){
  $before = (Select-String -Path $el -Pattern 'LoadMedia ok' -Encoding UTF8 -EA SilentlyContinue | Measure-Object).Count
  $playBtns[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep $WaitPlaySec
  $after = (Select-String -Path $el -Pattern 'LoadMedia ok' -Encoding UTF8 -EA SilentlyContinue | Measure-Object).Count
  Write-Output ("  LoadMedia 次数 前=$before 后=$after")
  Select-String -Path $dm -Pattern '增强: 播放增强版' -Encoding UTF8 | Select-Object -Last 2 | ForEach-Object { "  dm: " + $_.Line }
  Write-Output ("  EnhanceMsg = " + (Txt 'EnhanceMsg'))
  Select-String -Path $el -Pattern 'LoadMedia ok|音频:' -Encoding UTF8 | Select-Object -Last 4 | ForEach-Object { "  engine: " + $_.Line }
} else { Write-Output '  ✗ 没有"播放"按钮' }

Write-Output '=== 5) 失败提示是否如实显示后端 error（把后端停掉再点一次增强器）==='
Get-CimInstance Win32_Process -Filter "Name like '%python%'" -EA SilentlyContinue | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
Start-Sleep 2
Write-Output ("  已停后端；现在点增强器「" + (FindId 'Enhancer_loudness').Current.Name + "」")
ClickId 'Enhancer_loudness' | Out-Null
Start-Sleep 6
Write-Output ("  EnhanceMsg = " + (Txt 'EnhanceMsg'))
Select-String -Path $dm -Pattern '增强: 提交' -Encoding UTF8 | Select-Object -Last 3 | ForEach-Object { "  dm: " + $_.Line }

Write-Output '=== 6) 收尾 ==='
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\set_test_volume.ps1" -Restore
Write-Output ('settings: ' + ((Get-Content 'F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json' -Raw -Encoding UTF8) -replace "`r?`n",' '))
Write-Output 'DONE'
