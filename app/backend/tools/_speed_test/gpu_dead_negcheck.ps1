$ErrorActionPreference='Continue'
# 反向验证：不注入设备丢失时，提示条必须始终不出现、控件必须全程可用（防"误报白屏提示"）
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$tools='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
Remove-Item Env:DSH_TEST_DEVICE_LOST -EA SilentlyContinue
$env:DSH_SPEED_UI='1'
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 7
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_collections.ps1" 2>&1 | Select-Object -Last 1
Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_open_from_collection.ps1" 2>&1 | Select-Object -Last 1
$bad=0
for($i=1;$i -le 14;$i++){
  Start-Sleep -Seconds 1
  $t=FindId 'GpuDeadBannerTitle'
  $p=FindId 'PlayBtn'; $s=FindId 'SeekSlider'
  $banner = if($t){'显示'}else{'隐藏'}
  $pen = if($p -and $p.Current.IsEnabled){'可用'}else{'置灰'}
  $sen = if($s -and $s.Current.IsEnabled){'可用'}else{'置灰'}
  Write-Output ("t=" + $i + "s 提示条=" + $banner + " PlayBtn=" + $pen + " SeekSlider=" + $sen)
  if($t -or $pen -ne '可用'){ $bad++ }
}
if($bad -eq 0){ Write-Output '✔ 正常播放 14 秒：提示条始终隐藏、控件始终可用（无误报）' } else { Write-Output ("✗ 有 " + $bad + " 次异常状态") }
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Write-Output 'DONE'
