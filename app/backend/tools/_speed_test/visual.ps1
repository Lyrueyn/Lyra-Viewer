$ErrorActionPreference='Continue'
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$tools='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools'
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$env:DSH_SPEED_UI='1'; Remove-Item Env:DSH_AUDIO_DUMP -EA SilentlyContinue
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 8
Write-Output '--- uia_collections ---'; & powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_collections.ps1" 2>&1 | Select-Object -Last 3
Start-Sleep 2
Write-Output '--- uia_open_from_collection ---'; & powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_open_from_collection.ps1" 2>&1 | Select-Object -Last 3
Start-Sleep 6
Write-Output '--- uia_speed_set 1.5 ---'; & powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_speed_set.ps1" -Speed 1.5 2>&1
Add-Type -AssemblyName UIAutomationClient
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement
$ci=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,'PlayBtn')
if(-not $ci){ }
$pb=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$ci)
if($pb){ $pb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output '已暂停（让控制条常驻，便于截图）' } else { Write-Output '找不到 PlayBtn' }
Start-Sleep 2
Write-Output '--- capture ---'
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\capture_window.ps1" -Title '视频' -Out "$bin\speed_verify\speed_btn_1.5x.png" 2>&1
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Write-Output 'DONE'
