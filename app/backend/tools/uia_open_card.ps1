# 点开首页第 N 张卡片（默认第 1 张），用于验证播放器里的弹幕渲染。
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File uia_open_card.ps1 [-Index 0]
param([int]$Index = 0)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$sig = @'
using System; using System.Runtime.InteropServices;
public class Mouse {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
  public static void Click(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(120);
    mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(60);
    mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero);
  }
}
'@
Add-Type -TypeDefinition $sig -Language CSharp

$auto = [System.Windows.Automation.AutomationElement]
$desc = [System.Windows.Automation.TreeScope]::Descendants
$children = [System.Windows.Automation.TreeScope]::Children
$proc = Get-Process WinUIGui -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output 'FAIL: app not running'; exit 1 }
$cond = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, [int]$proc.Id)
$wins = $auto::RootElement.FindAll($children, $cond)
$mainWin = $null
foreach ($w in $wins) { try { if ($w.Current.Name -ne '视频') { $mainWin = $w; break } } catch { } }
if (-not $mainWin) { Write-Output 'FAIL: main window'; exit 1 }

$items = $mainWin.FindAll($desc, (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
Write-Output ('cards found: ' + $items.Count)
if ($items.Count -le $Index) { Write-Output 'FAIL: card index out of range'; exit 2 }
# 按（行, 列）排序，稳妥取第 N 张
$sorted = $items | Sort-Object { [int]($_.Current.BoundingRectangle.Top / 50) }, { $_.Current.BoundingRectangle.Left }
$card = $sorted[$Index]
$r = $card.Current.BoundingRectangle
# UIA 坐标是虚拟化的，物理坐标要 ×1.5（本机 150% 缩放）
$x = [int](($r.Left + $r.Width / 2) * 1.5)
$y = [int](($r.Top + $r.Height * 0.35) * 1.5)
Write-Output ("clicking card #$Index at physical ($x,$y)  (rect $([int]$r.Left),$([int]$r.Top) $([int]$r.Width)x$([int]$r.Height))")
[Mouse]::Click($x, $y)
Start-Sleep -Seconds 8
Write-Output 'DONE'
