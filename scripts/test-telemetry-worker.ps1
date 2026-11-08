# Native worker registration check with disposable credentials; never contacts a real collector.
$ErrorActionPreference='Stop'
$tmp=Join-Path ([IO.Path]::GetTempPath()) ('solvra-worker-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$rsa=[Security.Cryptography.RSA]::Create(2048)
$config=Join-Path $tmp 'device.json'
@{endpoint='https://collector.invalid';device_id=[guid]::NewGuid().ToString('N');token=('x'*48);public_key=$rsa.ExportSubjectPublicKeyInfoPem();logs_directory=(Join-Path $tmp 'logs');sessions_directory=(Join-Path $tmp 'sessions')} | ConvertTo-Json | Set-Content $config
$env:SOLVRA_TELEMETRY_CONFIG=Join-Path $tmp 'installed.json'
$dotnet=(Get-Command dotnet).Source
$dll=(Resolve-Path 'src/Solvra/bin/Debug/net8.0/Solvra.dll').Path
if ($IsWindows) {
  $name='Solvra Telemetry '+[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
  try {
    # Match the Windows installer's host, even though key generation above requires PS7.
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/windows/test-telemetry-acl.ps1 -ConfigFile $config -Dotnet $dotnet -Dll $dll -Profile (Join-Path $tmp 'profile')
    if ($LASTEXITCODE -ne 0) { throw 'Windows PowerShell telemetry configuration failed' }
    $env:SOLVRA_TELEMETRY_CONFIG=Join-Path $tmp 'profile/.solvra/telemetry.json'
    $task=Get-ScheduledTask -TaskName $name
    if ($task.Actions.Execute -ne $dotnet -or $task.Actions.Arguments -notmatch 'telemetry watch') { throw 'Invalid task action' }
  } finally {
    if ($name) { Unregister-ScheduledTask -TaskName $name -Confirm:$false }
  }
} elseif ($IsMacOS) {
  & bash ./scripts/macos/configure-telemetry.sh $config $dotnet $dll --no-start
  if ($LASTEXITCODE -ne 0) { throw 'LaunchAgent definition invalid' }
  Remove-Item "$HOME/Library/LaunchAgents/com.solvra.telemetry.plist"
}
& $dotnet $dll telemetry status
if ($LASTEXITCODE -ne 0) { throw 'Status failed' }
Remove-Item -Recurse $tmp
