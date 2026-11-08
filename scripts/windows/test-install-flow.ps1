# Run the installer under Windows PowerShell 5.1 with disposable native prerequisites.
# This exercises control flow without installing packages or using a real model account.
param([Parameter(Mandatory = $true)][string]$Installer)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Run this test with Windows PowerShell 5.1' }
. $Installer
$temp = Join-Path ([IO.Path]::GetTempPath()) ('solvra flow ' + [guid]::NewGuid().ToString('N'))
$keys = @('LOCALAPPDATA', 'USERPROFILE', 'DOTNET_ROOT', 'CODEX_HOME', 'SOLVRA_CODEX_BIN', 'SOLVRA_INSTALL_TEST_EXIT', 'SOLVRA_INSTALL_TEST_VERSION_FAIL', 'SOLVRA_INSTALL_TEST_MODEL_MARKER', 'SOLVRA_INSTALL_TEST_TELEMETRY_MARKER', 'Path')
$saved = @{}
foreach ($key in $keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
try {
  $bundle = Join-Path $temp 'bundle'
  $runtime = Join-Path $temp 'native runtime'
  New-Item -ItemType Directory -Force $bundle, $runtime, (Join-Path $bundle 'backend') | Out-Null
  Copy-Item $Installer (Join-Path $bundle 'install.ps1')
  Set-Content (Join-Path $bundle 'VERSION') 'fixture'
  Set-Content (Join-Path $bundle 'backend\Solvra.dll') 'disposable native process fixture'
  # The worker itself is tested separately; this boundary records whether installation reached it.
  @'
param($ConfigFile, $Dotnet, $Dll)
[IO.File]::WriteAllText($env:SOLVRA_INSTALL_TEST_TELEMETRY_MARKER, 'configured')
Write-Host 'FIXTURE-TELEMETRY-CONFIGURED'
'@ | Set-Content (Join-Path $bundle 'configure-telemetry.ps1')
  $native = Join-Path $runtime 'dotnet.exe'
  Add-Type -OutputAssembly $native -OutputType ConsoleApplication -TypeDefinition @'
using System;
using System.IO;
public class NativeFixture {
  public static int Main(string[] args) {
    if (Array.IndexOf(args, "--list-runtimes") >= 0) {
      Console.WriteLine("Microsoft.NETCore.App 8.0.0 [fixture]"); return 0;
    }
    if (Array.IndexOf(args, "--version") >= 0) {
      if (Environment.GetEnvironmentVariable("SOLVRA_INSTALL_TEST_VERSION_FAIL") == "1") {
        Console.Error.WriteLine("fixture backend startup failure"); return 22;
      }
      Console.WriteLine("fixture"); return 0;
    }
    if (args.Length >= 3 && args[1] == "run") {
      if (args[2] != "Reply with exactly the word OK and nothing else.") return 42;
      File.WriteAllText(Environment.GetEnvironmentVariable("SOLVRA_INSTALL_TEST_MODEL_MARKER"), "called");
      Console.Error.WriteLine("[session] Starting agent session");
      Console.WriteLine("OK");
      return int.Parse(Environment.GetEnvironmentVariable("SOLVRA_INSTALL_TEST_EXIT"));
    }
    return 43;
  }
}
'@
  $env:LOCALAPPDATA = Join-Path $temp 'local app data'
  $env:USERPROFILE = Join-Path $temp 'profile'
  $env:DOTNET_ROOT = $runtime
  [Environment]::SetEnvironmentVariable('CODEX_HOME', $null, 'Process')
  $env:SOLVRA_CODEX_BIN = $native
  New-Item -ItemType Directory -Force (Join-Path $env:USERPROFILE '.codex') | Out-Null
  Set-Content (Join-Path $env:USERPROFILE '.codex\auth.json') '{}'
  $env:SOLVRA_INSTALL_TEST_MODEL_MARKER = Join-Path $temp 'model.called'
  $env:SOLVRA_INSTALL_TEST_TELEMETRY_MARKER = Join-Path $temp 'telemetry.called'
  $hostExe = (Get-Process -Id $PID).Path
  foreach ($case in @('success', 'model-failure', 'skip-model', 'backend-failure')) {
    Remove-Item $env:SOLVRA_INSTALL_TEST_MODEL_MARKER, $env:SOLVRA_INSTALL_TEST_TELEMETRY_MARKER -ErrorAction SilentlyContinue
    $env:SOLVRA_INSTALL_TEST_EXIT = if ($case -eq 'model-failure') { '17' } else { '0' }
    $env:SOLVRA_INSTALL_TEST_VERSION_FAIL = if ($case -eq 'backend-failure') { '1' } else { '0' }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $bundle 'install.ps1'), '-NoVSCode', '-NoGit', '-TelemetryConfig', (Join-Path $temp 'device.json'))
    if ($case -eq 'skip-model') { $arguments += '-NoTest' }
    $result = Invoke-NativeCapture $hostExe $arguments
    $telemetryCalled = Test-Path $env:SOLVRA_INSTALL_TEST_TELEMETRY_MARKER
    $modelCalled = Test-Path $env:SOLVRA_INSTALL_TEST_MODEL_MARKER
    if ($case -eq 'backend-failure') {
      if ($result.ExitCode -eq 0 -or $telemetryCalled -or $result.Output -match '==> Done\.') { throw 'Backend failure did not stop installation' }
    } else {
      if ($result.ExitCode -ne 0 -or -not $telemetryCalled) { throw "$case failed to reach telemetry: $($result.Output)" }
      if ($case -eq 'skip-model' -and $modelCalled) { throw '-NoTest still called the model' }
      if ($case -ne 'skip-model' -and -not $modelCalled) { throw 'Model test was not executed' }
      if ($case -eq 'success' -and $result.Output -notmatch 'Model answered: OK') { throw 'Successful model output was rejected' }
      if ($case -eq 'model-failure' -and ($result.Output -notmatch 'exit 17' -or $result.Output -match 'Model answered: OK')) { throw 'Nonzero exit code was reported as success' }
      if ($result.Output.IndexOf('FIXTURE-TELEMETRY-CONFIGURED') -ge $result.Output.IndexOf('==> Done.')) { throw 'Done was printed before telemetry setup' }
    }
    Write-Host "INSTALL-FLOW-OK $case"
  }
} finally {
  [Environment]::SetEnvironmentVariable('Path', $userPath, 'User')
  foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
  Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}
exit 0
