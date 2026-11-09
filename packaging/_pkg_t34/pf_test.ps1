$r = @()
try { New-Item -ItemType Directory -Path 'C:\Program Files\_lmptest' -ErrorAction Stop | Out-Null; $r += 'PF_DIR=OK'; Remove-Item 'C:\Program Files\_lmptest' -Recurse -Force } catch { $r += 'PF_DIR=FAIL ' + $_.Exception.Message }
try { New-Item -ItemType Directory -Path 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\_lmptest' -ErrorAction Stop | Out-Null; $r += 'SM_DIR=OK'; Remove-Item 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\_lmptest' -Recurse -Force } catch { $r += 'SM_DIR=FAIL ' + $_.Exception.Message }
try { $p = 'C:\Users\Administrator\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\_lmptest'; New-Item -ItemType Directory -Path $p -ErrorAction Stop | Out-Null; $r += 'USER_SM=OK'; Remove-Item $p -Recurse -Force } catch { $r += 'USER_SM=FAIL ' + $_.Exception.Message }
# 组属性
$r += '---GROUPS---'
& whoami /groups 2>&1 | Select-String 'Administrators' | ForEach-Object { $r += $_.Line.Trim() }
$r | Out-File -FilePath 'F:\DSH\_pkg_t34\pf_test.txt' -Encoding UTF8