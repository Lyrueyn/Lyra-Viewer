# Verify the player's quality-switch button end to end via UI Automation.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File uia_quality.ps1
# It finds the video window, invokes the quality button, then clicks the 480P item,
# and prints what it did. The app writes the switch result into dm_debug.log.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$auto = [System.Windows.Automation.AutomationElement]
$scopeChildren = [System.Windows.Automation.TreeScope]::Children
$scopeDesc = [System.Windows.Automation.TreeScope]::Descendants

function Find-ByAutomationId($root, $id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty, $id)
    return $root.FindFirst($scopeDesc, $cond)
}
function Find-ByName($root, $name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($auto::NameProperty, $name)
    return $root.FindFirst($scopeDesc, $cond)
}

$root = $auto::RootElement
$wins = $root.FindAll($scopeChildren, [System.Windows.Automation.Condition]::TrueCondition)
$videoWin = $null
foreach ($w in $wins) {
    try { if ($w.Current.Name -eq '视频') { $videoWin = $w; break } } catch { }
}
if (-not $videoWin) { Write-Output 'FAIL: video window not found'; exit 1 }
Write-Output ('window: ' + $videoWin.Current.Name)

$btn = Find-ByAutomationId $videoWin 'QualityBtn'
if (-not $btn) {
    # fall back: any button whose name contains a quality keyword
    $btns = $videoWin.FindAll($scopeDesc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    foreach ($b in $btns) {
        $n = $b.Current.Name
        if ($n -match 'P|画质') { $btn = $b; Write-Output ('fallback button: ' + $n); break }
    }
}
if (-not $btn) { Write-Output 'FAIL: quality button not found'; exit 1 }
Write-Output ('quality button name = "' + $btn.Current.Name + '"  enabled=' + $btn.Current.IsEnabled)

$inv = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$inv.Invoke()
Start-Sleep -Milliseconds 1200
Write-Output 'invoked quality button (flyout should be open)'

$item = Find-ByName $root '480P'
if (-not $item) { Write-Output 'FAIL: menu item 480P not found'; exit 2 }
Write-Output ('menu item: ' + $item.Current.Name + ' type=' + $item.Current.ControlType.ProgrammaticName)
$iinv = $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$iinv.Invoke()
Write-Output 'invoked 480P'
Start-Sleep -Seconds 4
Write-Output 'DONE'
