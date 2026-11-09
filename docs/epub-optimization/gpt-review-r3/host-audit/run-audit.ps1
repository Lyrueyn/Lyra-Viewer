param([string]$RunId=('recheck-'+(Get-Date -Format 'yyyyMMdd-HHmmss')))
$ErrorActionPreference='Stop'
function Save-AuditLog([string]$Path) { process { $_ | Out-String | Add-Content -Path $Path -Encoding utf8; Write-Output $_ } }
if($RunId -notmatch '^[A-Za-z0-9_-]+$'){throw 'RunId must be a simple new directory name'}
$base=$PSScriptRoot
$run=Join-Path $base ('runs/'+$RunId)
if(Test-Path $run){throw 'Choose a new RunId; do not overwrite previous evidence'}
New-Item -ItemType Directory $run | Out-Null
$env:DOTNET_CLI_HOME=Join-Path $base '.dotnet'
$env:DOTNET_NOLOGO='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:LYRA_READER_DEBUG='0'
$inputs=@('../candidate/reader-state.js','../candidate/reader.js','../candidate/extension.json','../host-snapshot/ReaderStateStore.cs','../host-snapshot/ReaderProtocol.cs','../host-snapshot/ExtensionHostWindow.xaml','../host-snapshot/ExtensionHostWindow.xaml.cs')
$hashes=@($inputs | ForEach-Object {Get-FileHash (Join-Path $base $_) -Algorithm SHA256 | Select-Object Path,Hash})
$hashes | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $run 'inputs-sha256-before.json') -Encoding utf8
& dotnet build (Join-Path $base 'HostAudit.csproj') -o (Join-Path $run 'bin') --nologo -v minimal 2>&1 | Save-AuditLog (Join-Path $run 'build.log')
if($LASTEXITCODE -ne 0){throw 'R3 audit compile failed'}
& dotnet build (Join-Path $base 'r2-bridge/Bridge.csproj') -o (Join-Path $run 'r2bin') --nologo -v minimal 2>&1 | Save-AuditLog (Join-Path $run 'r2-build.log')
if($LASTEXITCODE -ne 0){throw 'R2 compatibility bridge compile failed'}
& dotnet (Join-Path $run 'bin/HostAudit.dll') --audit 2>&1 | Save-AuditLog (Join-Path $run 'host-console.txt')
if($LASTEXITCODE -ne 0){throw 'C# diagnostic execution failed'}
& node (Join-Path $base 'client-audit.cjs') $run 2>&1 | Save-AuditLog (Join-Path $run 'client-console.txt')
if($LASTEXITCODE -ne 0){throw 'JS diagnostic execution failed'}
$after=@($inputs | ForEach-Object {Get-FileHash (Join-Path $base $_) -Algorithm SHA256 | Select-Object Path,Hash})
$after | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $run 'inputs-sha256-after.json') -Encoding utf8
if((Compare-Object $hashes $after -Property Path,Hash)){throw 'Frozen input changed during audit'}
Write-Output "Completed independent diagnostic run: $run. Inspect pass:false in JSON; process exit0 only means probes completed."
