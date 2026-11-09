Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
function F($id){ $c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id); $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c) }
if (F 'RelatedList') { Write-Output "相关推荐区块【仍存在】✗" } else { Write-Output "相关推荐区块【已消失】✔" }
if (F 'DescText') { Write-Output "简介区块【已显示】✔" } else { Write-Output "简介区块（当前无简介或未显示）" }
if (F 'SettingsTab') { Write-Output "设置 tab【在】✔" }