$ErrorActionPreference='Continue'
# T11 复验（改完文案后）：切换 → "仍在共享/下一个视频生效" → 开下一集 → "位完美"；并核对启动无重复下发
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$tools='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools'
$out="$bin\speed_verify"; $elog="$bin\dsh_engine.log"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }
function Txt($id){ $e=FindId $id; if($e){ return $e.Current.Name } else { return '<不在 UIA 树里>' } }
function St($tag){ Write-Output ("[$tag] 按钮='" + (Txt 'ExclBtn') + "' | 小字='" + (Txt 'ExclStatusText') + "'") }
function Click($id){ $e=FindId $id; if($e){ $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } else { return $false } }
function InvokeSecondVideo(){
  $items = $root.FindAll($desc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
  foreach($it in $items){
    try{ $n=$it.Current.Name; if(-not $n -or $n -notmatch 'CollItemVm'){ continue } }catch{ continue }
    try{ $it.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $n }catch{ continue }
  }
  return $null
}

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\set_test_volume.ps1"
if(Test-Path $elog){ Move-Item $elog "$out\dsh_engine_t11b_prev.log" -Force -EA SilentlyContinue }
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 7
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_collections.ps1" 2>&1 | Select-Object -Last 1
Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_open_from_collection.ps1" 2>&1 | Select-Object -Last 1
Start-Sleep 5
Click 'SettingsTab' | Out-Null; Start-Sleep 1
St '1-默认(共享)'
Write-Output ('  提示小字全文 = ' + (Txt 'ExclHint'))
Write-Output '--- 点一下（共享→独占）---'
Click 'ExclBtn' | Out-Null; Start-Sleep 2
St '2-切换后（应说"仍在共享/下一个视频生效"，不能说设备拒绝）'
Write-Output '--- 开"下一个视频"（同会话内点合集项）---'
$n = InvokeSecondVideo; Write-Output ("  点到: " + $n)
Start-Sleep 8
St '3-下一个视频打开后'
Write-Output '=== 引擎日志（独占相关）==='
Select-String -Path $elog -Pattern '独占模式|音频: 48000|音频: 44100' -Encoding UTF8 | ForEach-Object { $_.Line }
Write-Output '--- 收尾：切回共享 ---'
Click 'ExclBtn' | Out-Null; Start-Sleep 1
St '4-切回共享后'
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\set_test_volume.ps1" -Restore
Write-Output ('settings: ' + ((Get-Content 'F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json' -Raw -Encoding UTF8) -replace "`r?`n",' '))
Write-Output 'DONE'
