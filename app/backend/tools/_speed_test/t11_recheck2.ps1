$ErrorActionPreference='Continue'
# T11 定点复验：切换后必须先说"仍在共享/下一个视频生效"，开下一集后再说"位完美"
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$tools='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools'
$elog="$bin\dsh_engine.log"; $out="$bin\speed_verify"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }
function Txt($id){ $e=FindId $id; if($e){ return $e.Current.Name } else { return '<不在 UIA 树里>' } }
function St($tag){ Write-Output ("[$tag] 按钮='" + (Txt 'ExclBtn') + "' | 小字='" + (Txt 'ExclStatusText') + "'") }
function Click($id){ $e=FindId $id; if($e){ $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } }

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\set_test_volume.ps1"
if(Test-Path $elog){ Move-Item $elog "$out\dsh_engine_t11c_prev.log" -Force -EA SilentlyContinue }
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 7
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_collections.ps1" 2>&1 | Select-Object -Last 1
Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_open_from_collection.ps1" 2>&1 | Select-Object -Last 1
Start-Sleep 5
Click 'SettingsTab'; Start-Sleep 1
St '1-默认共享'
Click 'ExclBtn'; Start-Sleep 2
St '2-切到独占（期望：仍在共享 → 切换后「下一个视频」生效）'
$items = $root.FindAll($desc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
foreach($it in $items){ try{ $n=$it.Current.Name; if(-not $n -or $n -notmatch 'CollItemVm'){ continue } }catch{ continue }
  try{ $it.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output '  已点下一个视频'; break }catch{ continue } }
Start-Sleep 8
St '3-下一个视频打开后（期望：独占已生效（位完美））'
Click 'ExclBtn'; Start-Sleep 2
St '4-切回共享（期望：仍在独占 → 下一个视频切回共享）'
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\set_test_volume.ps1" -Restore
Write-Output ('settings: ' + ((Get-Content 'F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json' -Raw -Encoding UTF8) -replace "`r?`n",' '))
Write-Output 'DONE'
