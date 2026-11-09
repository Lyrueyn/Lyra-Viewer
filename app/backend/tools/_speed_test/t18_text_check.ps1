$ErrorActionPreference='Continue'
# 文案复核（不出声：DSH_NO_AUDIO=1）：只读 7 个增强器的中文名 + 说明
$bin='F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui\bin\Debug\net10.0-windows10.0.26100.0\win-x64'
$recall='F:\DSH\LocalMediaPlayer-Workspace\app\data\downloads\【洛天依原创曲】再声 Recall【2024官方十二周年生贺曲】 [1080P+].mp4'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto=[System.Windows.Automation.AutomationElement]; $root=$auto::RootElement; $desc=[System.Windows.Automation.TreeScope]::Descendants
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty,$id); return $root.FindFirst($desc,$c) }

Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$env:DSH_NO_AUDIO='1'          # 静音：本步不需要出声
$env:DSH_AUTO_OPEN=$recall
Start-Process -FilePath "$bin\WinUIGui.exe" -WorkingDirectory $bin | Out-Null
Start-Sleep 12
$btn = FindId 'EnhanceTab'
if(-not $btn){ Write-Output 'FAIL: 找不到增强 tab'; Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force; exit 1 }
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep 4
Write-Output '=== 7 个增强器的按钮文字（UIA 读到的）==='
foreach($k in 'loudness','soxr_hires','fft_hires','audio_sr','denoise','realesrgan','rife'){
  $b = FindId ("Enhancer_" + $k)
  if($b){ Write-Output ("  " + $k.PadRight(11) + " → '" + $b.Current.Name + "'") } else { Write-Output ("  " + $k + " → <缺失>") }
}
Write-Output '=== 界面上每个按钮下面那句说明（UIA Text 读到的，与增强器一一对应）==='
$p=Get-Process WinUIGui -EA SilentlyContinue | Select-Object -First 1
$vw=$null
if($p){ $cond=New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty,[int]$p.Id)
  foreach($w in $auto::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)){ try{ if($w.Current.Name -eq '视频'){ $vw=$w } }catch{} } }
if($vw){
  foreach($t in $vw.FindAll($desc,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)))){
    try{ $n=$t.Current.Name; if($n -match 'LUFS|96kHz|补高频|语音|降音量|GPU|未实测|最轻|听感几乎'){ Write-Output ("  · " + $n) } }catch{}
  }
}
Write-Host "=== 未出声确认：设置文件（本步没动音量）==="
Get-Content 'F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json' -Raw -Encoding UTF8
Get-Process WinUIGui -EA SilentlyContinue | Stop-Process -Force
Write-Output 'DONE'
