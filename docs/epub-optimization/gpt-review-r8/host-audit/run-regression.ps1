$ErrorActionPreference='Stop'
$env:DOTNET_CLI_HOME="$PSScriptRoot\.dotnet"
$env:NUGET_PACKAGES="$PSScriptRoot\.nuget"
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:TEMP="$PSScriptRoot\.temp"
$env:TMP="$PSScriptRoot\.temp"
Remove-Item Env:AUDIT_RUN -ErrorAction SilentlyContinue
foreach($probe in @('audit','supplement','regression','extended','close-boundary','protocol-boundary','boundary')) {
  & node "$PSScriptRoot\$probe.cjs" *> "$PSScriptRoot\$probe-console.log"
  if($LASTEXITCODE -ne 0){throw "Probe $probe exited $LASTEXITCODE"}
}
& node "$PSScriptRoot\capacity.cjs" ack *> "$PSScriptRoot\capacity-ack-console.log"
if($LASTEXITCODE -ne 0){throw 'capacity ack failed'}
& node "$PSScriptRoot\capacity.cjs" burst *> "$PSScriptRoot\capacity-burst-console.log"
if($LASTEXITCODE -ne 0){throw 'capacity burst failed'}
& node "$PSScriptRoot\completion-boundary.cjs" mixed *> "$PSScriptRoot\completion-mixed-console.log"
if($LASTEXITCODE -ne 0){throw 'mixed failed'}
Write-Output 'R5 applicable regression execution completed; inspect individual results for product verdict.'
