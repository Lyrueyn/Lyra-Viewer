$ErrorActionPreference='Continue'
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$tools='F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]
$root=$auto::RootElement
$desc=[System.Windows.Automation.TreeScope]::Descendants

function FindId($id){
  $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id)
  return $root.FindFirst($desc,$c)
}
function State(){
  $t=FindId 'GpuDeadBannerTitle'
  $banner = if($t){ '显示:"' + $t.Current.Name + '"' } else { '隐藏' }
  $rows=@()
  foreach($id in 'PlayBtn','SeekSlider','SpeedBtn','MuteBtn','VolumeSlider'){
    $e=FindId $id
    if($e){ $rows += ("$id=" + $(if($e.Current.IsEnabled){'可用'}else{'置灰'})) } else { $rows += ("$id=找不�到") }
  }
  return ("提示条=" + $banner + " | " + ($rows -join ' '))
}

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$env:DSH_TEST_DEVICE_LOST='1'
$env:DSH_SPEED_UI='1'
Remove-Item Env:DSH_AUDIO_DUMP -EA SilentlyContinue
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 7
Write-Output '--- 打开合集第一个视频 ---'
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_collections.ps1" 2>&1 | Select-Object -Last 1
Start-Sleep 2
& powershell -NoProfile -ExecutionPolicy Bypass -File "$tools\uia_open_from_collection.ps1" 2>&1 | Select-Object -Last 1
Write-Output '--- 每秒读一次 UIA（等判死）---'
$t0=Get-Date
for($i=1;$i -le 26;$i++){
  Start-Sleep -Seconds 1
  $el=[int]((Get-Date)-$t0).TotalSeconds
  Write-Output ("t=" + $el + "s  " + (State))
  if($i -ge 12 -and (State) -match '提示条=显示'){ if($i -ge 14){ break } }
}
Write-Output '--- 再确认一次（判死后 3 秒）---'
Start-Sleep 3
Write-Output (State)
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Write-Output 'DONE'
