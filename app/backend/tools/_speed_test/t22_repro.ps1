param([string]$Mode='fixed', [int]$Sec=60)
$ErrorActionPreference='Continue'
# T22 复现/验证：合集连播 [A,B,B]（B 出现两次，指向同一文件）
#   -Mode legacy → DSH_TEST_EOF_LEGACY=1（老行为，复现连环）
#   -Mode fixed  → 修复后行为
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$tools='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools'
$out="$bin\speed_verify"
$dm="$bin\dm_debug.log"; $el="$bin\dsh_engine.log"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
function MainWin(){
  $p=Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1
  if(-not $p){ return $null }
  $cond=New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty,[int]$p.Id)
  foreach($w in $auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)){ try{ if($w.Current.Name -ne '视频'){ return $w } }catch{} }
  return $null
}
function GoCollections($w){
  $els=$w.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
  $left=$w.Current.BoundingRectangle.Left; $navs=@()
  foreach($b in $els){ try{ $r=$b.Current.BoundingRectangle; if(($r.Left-$left) -lt 130 -and $r.Width -lt 130 -and $r.Height -lt 130){ $navs += $b } }catch{} }
  $navs=$navs | Sort-Object { $_.Current.BoundingRectangle.Top }
  if($navs.Count -ge 3){ $navs[2].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
  return $false
}
function CountIn($file,$pat){ if(-not (Test-Path $file)){ return 0 }; return (Select-String -Path $file -Pattern $pat -Encoding UTF8 -EA SilentlyContinue | Measure-Object).Count }
function Report($tag){
  $eof = CountIn $dm '播放结束:' ; $chain = CountIn $dm '合集连播 → 第'
  $init = CountIn $el '\]\s*Init'; $shut = CountIn $el '\]\s*Shutdown'; $lost = CountIn $el '设备丢失'; $load = CountIn $el 'LoadMedia ok'
  Write-Output ("[$tag] dm_debug: 播放结束=$eof 合集连播=$chain | dsh_engine: Init=$init Shutdown=$shut LoadMedia=$load 设备丢失=$lost")
  return @{eof=$eof; chain=$chain; init=$init; shut=$shut; lost=$lost; load=$load}
}

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
if(Test-Path $dm){ Move-Item $dm "$out\dm_debug_t22_$Mode`_prev.log" -Force -EA SilentlyContinue }
if(Test-Path $el){ Move-Item $el "$out\dsh_engine_t22_$Mode`_prev.log" -Force -EA SilentlyContinue }
Remove-Item Env:DSH_TEST_EOF_LEGACY -EA SilentlyContinue
$env:DSH_NO_AUDIO='1'                       # 静音跑（连播逻辑与音频无关，且不打扰用户）
if($Mode -eq 'legacy'){ $env:DSH_TEST_EOF_LEGACY='1'; Write-Output '★ legacy 模式：已关掉 T22 加固（复现老行为）' } else { Write-Output '★ fixed 模式：T22 加固生效' }

Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 8
$w=MainWin
if(-not $w){ Write-Output 'FAIL: 主窗口没起来'; exit 1 }
GoCollections $w | Out-Null
Start-Sleep 3
# 选第一个合集（= 我的 【T22自测】合集，id=8）
$items=$w.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)))
Write-Output ("  合集页 ListItem 数 = " + $items.Count)
foreach($it in $items){ try{ $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Write-Output '  已选中第一个合集'; break }catch{} }
Start-Sleep 3
# 点第一个视频（第 1 集 clipA）→ 带上来源合集 → 连播生效
$w=MainWin
$items=$w.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)))
$clicked=$false
foreach($it in $items){ try{ $n=$it.Current.Name; if($n -and $n.Length -gt 4){ $it.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output ("  已点视频项: " + $n); $clicked=$true; break } }catch{} }
if(-not $clicked){ Write-Output '  ✗ 没点到视频项' }

Report 't0(开播)'
for($i=1;$i -le $Sec;$i+=10){
  Start-Sleep -Seconds 10
  Report ("t=" + $i + "s")
}
Write-Output '=== UIA：自动连播停机提示条 ==='
function FindId2($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }
$bar = FindId2 'AutoStopBar'
$txt = FindId2 'AutoStopText'
if($txt){ Write-Output ("  AutoStopText 可见=" + (-not $txt.Current.IsOffscreen) + " 文字=" + $txt.Current.Name) }
elseif($bar){ Write-Output '  AutoStopBar 在树里但文字读不到' }
else { Write-Output '  AutoStopBar/Text 不在 UIA 树里（= 未触发停机，或还没显示）' }
Write-Output '=== dm_debug.log 里 播放结束/连播/闸门 相关行（最近 30 条）==='
Select-String -Path $dm -Pattern '播放结束|合集连播|闸门|换片被拒|自动连播|结束判定成立' -Encoding UTF8 | Select-Object -Last 30 | ForEach-Object { $_.Line }
Write-Output '=== dsh_engine.log 里 Init/Shutdown/LoadMedia/设备丢失（最近 15 条）==='
Select-String -Path $el -Pattern 'Init / 交换链|Shutdown|LoadMedia ok|设备丢失' -Encoding UTF8 | Select-Object -Last 15 | ForEach-Object { $_.Line }
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Write-Output 'DONE'
