param([double]$Volume = 0.02, [switch]$Restore, [string]$SettingsPath = "F:\DSH\LocalMediaPlayer-Workspace\app\data\player_settings.json")
# 测试音量约定（用户 2026-10-16 明确要求）：自动化测试会在他用电脑时外放声音，必须压低。
#   用法：测试前  set_test_volume.ps1            （音量→2%）
#         测试后  set_test_volume.ps1 -Restore  （恢复测试前的值）
$saveFile = "F:\DSH\LocalMediaPlayer-Workspace\app\backend\tools\_test_volume_saved.txt"
if ($Restore) {
  if (Test-Path $saveFile) {
    $v = (Get-Content $saveFile -Raw).Trim()
  # ★ 防坑：若"原值"本身 <0.05，说明上一轮把测试值(2%)当成原值存了下来 —— 拒绝恢复并告警
  if ([double]$v -lt 0.05) { Write-Output ("⚠ 保存的原值 {0} 疑似测试值(2%)，拒绝 -Restore；已改为恢复 1.0" -f $v); $v = '1' }
    $o = Get-Content $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $o.volume = [double]$v
    ($o | ConvertTo-Json -Depth 6) | Set-Content $SettingsPath -Encoding UTF8
    Write-Output ("测试音量已恢复为 {0}" -f $v)
  } else { Write-Output '没有保存过测试前音量，未改动' }
  exit
}
$o = Get-Content $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$script:saved = $o.volume
Set-Content $saveFile -Value $script:saved -Encoding UTF8
$o.volume = $Volume
($o | ConvertTo-Json -Depth 6) | Set-Content $SettingsPath -Encoding UTF8
Write-Output ("测试音量已设为 {0:P0}（原值 {1} 已保存，用完请 -Restore）" -f $Volume, $script:saved)