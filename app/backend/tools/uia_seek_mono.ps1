Add-Type -AssemblyName UIAutomationClient
$root=[System.Windows.Automation.AutomationElement]::RootElement
function FindId($id){ $c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id); return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c) }
$sk=FindId 'SeekSlider'; $tt=FindId 'TimeText'
if(-not $sk){ Write-Output '✗ 找不到进度条'; exit }
$rv=$sk.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
$prev=[double]$rv.Current.Value; $back=0; $maxback=0.0; $n=0
# 远距离 seek：直接设到 60% 处（离当前位置很远，必触发旧规则的回跳条件）
$rv.SetValue([double]0.60)
for($i=0;$i -lt 45;$i++){
  Start-Sleep -Milliseconds 150
  try { $v=[double]$rv.Current.Value; $n++
        $d=($prev-$v)*100
        if($d -gt 0.05){ $back++; if($d -gt $maxback){$maxback=$d} }
        $prev=$v } catch {}
}
Write-Output ("采样 {0} 次 | 进度后退 {1} 次 | 最大后退 {2:N2}%（进度条刻度）" -f $n,$back,$maxback)
Write-Output ("最终 时间文字 = " + $(if($tt){$tt.Current.Name}else{'?'}))