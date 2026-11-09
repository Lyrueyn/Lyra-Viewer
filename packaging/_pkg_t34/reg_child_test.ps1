$k = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\_lmptest_child'
try {
  New-Item -Path $k -Force -ErrorAction Stop | Out-Null
  Set-ItemProperty -Path $k -Name DisplayName -Value 'test' -ErrorAction Stop
  'CHILD_OK' | Out-File -FilePath 'F:\DSH\_pkg_t34\child_reg_result.txt' -Encoding UTF8
  Remove-Item $k -Recurse -Force -ErrorAction SilentlyContinue
} catch {
  ('CHILD_FAIL: ' + $_.Exception.Message) | Out-File -FilePath 'F:\DSH\_pkg_t34\child_reg_result.txt' -Encoding UTF8
}