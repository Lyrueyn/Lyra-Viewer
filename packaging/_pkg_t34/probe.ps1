$o = @()
$o += 'APPDATA=' + $env:APPDATA
$o += 'USERNAME=' + $env:USERNAME
$o += 'USERPROFILE=' + $env:USERPROFILE
$o += 'SESSIONNAME=' + $env:SESSIONNAME
$o += 'CurrentUser=' + [Security.Principal.WindowsIdentity]::GetCurrent().Name
$o += 'IsAdmin=' + (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$o += 'Desktop=' + [Environment]::GetFolderPath('Desktop')
$o += 'StartMenu=' + [Environment]::GetFolderPath('Programs')
$o += 'PWD=' + (Get-Location).Path
# 试写文件到桌面
try { $f = Join-Path ([Environment]::GetFolderPath('Desktop')) 'probe-test.txt'; Set-Content -LiteralPath $f -Value 'x' -EA Stop; $o += 'WriteDesktop=OK'; Remove-Item -LiteralPath $f -Force } catch { $o += 'WriteDesktop=FAIL ' + $_.Exception.Message }
# 试写注册表
try { New-Item -Path 'HKCU:\Software\_lmp_probe' -Force -EA Stop | Out-Null; $o += 'WriteReg=OK'; Remove-Item 'HKCU:\Software\_lmp_probe' -Force } catch { $o += 'WriteReg=FAIL ' + $_.Exception.Message }
$o | Set-Content -LiteralPath 'F:\DSH\_pkg_t34\probe.txt' -Encoding UTF8