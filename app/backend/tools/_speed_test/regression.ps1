$ErrorActionPreference='Continue'
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$tools='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools'
Remove-Item Env:DSH_SPEED_UI -EA SilentlyContinue; Remove-Item Env:DSH_AUDIO_DUMP -EA SilentlyContinue
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 7
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_collections.ps1" 2>&1 | Select-Object -Last 1
Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_open_from_collection.ps1" 2>&1 | Select-Object -Last 1
Start-Sleep 6
Add-Type -AssemblyName UIAutomationClient
$auto=[System.Windows.Automation.AutomationElement]
$c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,'SpeedBtn')
$b=$auto::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
if($b){ Write-Output ("[回归] 未设环境变量却找到 SpeedBtn → 灰度默认没生效 ✗ 文字=" + $b.Current.Name) } else { Write-Output '[回归] 未设环境变量 → 找不到 SpeedBtn ✔ 默认仍然隐藏' }
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Write-Output 'DONE'
