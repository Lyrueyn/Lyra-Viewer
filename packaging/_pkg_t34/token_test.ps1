$lines = @()
$lines += 'Identity=' + [Security.Principal.WindowsIdentity]::GetCurrent().Name
$lines += 'IsAdmin=' + (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$lines += 'TokenType=' + [Security.Principal.WindowsIdentity]::GetCurrent().Token
$lines += 'Groups:'
[Security.Principal.WindowsIdentity]::GetCurrent().Groups | ForEach-Object {
  try { $lines += '  ' + $_.Translate([System.Security.Principal.NTAccount]).Value + '  ' + $_.Value } catch { $lines += '  ' + $_.Value }
}
$lines | Out-File -FilePath 'F:\DSH\_pkg_t34\child_token.txt' -Encoding UTF8