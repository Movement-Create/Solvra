# Run after install.ps1, or pass -TelemetryConfig to install.ps1.
# Private device file stays outside the bundle/repository. No credentials in the scheduled task.
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$ConfigFile,
  [Parameter(Mandatory=$true)][string]$Dotnet,
  [Parameter(Mandatory=$true)][string]$Dll,
  [switch]$NoStart
)
$ErrorActionPreference = 'Stop'
$configDir = Join-Path $env:USERPROFILE '.solvra'
New-Item -ItemType Directory -Force $configDir | Out-Null
# Restrict the credential file to this user and SYSTEM before the backend writes it.
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$acl = Get-Acl $configDir
$acl.SetAccessRuleProtection($true, $false)
foreach ($identity in @($sid, 'S-1-5-18')) {
  $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    [System.Security.Principal.SecurityIdentifier]::new($identity), 'FullControl',
    'ContainerInherit,ObjectInherit', 'None', 'Allow')
  $acl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $configDir -AclObject $acl
& $Dotnet $Dll telemetry configure --file $ConfigFile
if ($LASTEXITCODE -ne 0) { throw 'Telemetry configuration failed.' }
$taskName = "Solvra Telemetry $sid"
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
  Stop-ScheduledTask -TaskName $taskName -ErrorAction Stop
}
$action = New-ScheduledTaskAction -Execute $Dotnet -Argument ('"' + $Dll + '" telemetry watch')
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $sid
$principal = New-ScheduledTaskPrincipal -UserId $sid -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew `
  -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
  -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
if (-not $NoStart) {
  # Configure command verifies syntax; sync verifies credentials and actual cloud acknowledgment.
  & $Dotnet $Dll telemetry test
  if ($LASTEXITCODE -ne 0) { Write-Warning 'Initial delivery failed; records remain queued and the background worker will retry.' }
  Start-ScheduledTask -TaskName $taskName
}
Write-Host 'Telemetry worker installed for this user. Check delivery with: solvra telemetry status'
