# Solvra installer for Windows 10/11 (x64 and Arm64). Per user, no administrator rights needed.
#
# Double-click "Install Solvra.cmd", or from PowerShell in the extracted folder:
#   .\install.ps1                      # everything: .NET 8, the solvra command, the VS Code
#                                      # extension, and ChatGPT through your Codex sign-in
#   .\install.ps1 -Model gpt-5.6-luna  # another ChatGPT model (default gpt-5.6-sol)
#   .\install.ps1 -NoChatGPT           # skip the ChatGPT setup (add an API key in the chat instead)
#   .\install.ps1 -NoVSCode            # command line only
#   .\install.ps1 -NoGit               # do not offer to install Git for Windows
#
# Installs:
#   %LOCALAPPDATA%\Solvra\releases\<version>   the backend
#   %LOCALAPPDATA%\Solvra\current              junction to the active release
#   %LOCALAPPDATA%\Solvra\bin\solvra.cmd       the solvra command (added to your user PATH)
#   VS Code extension solvra.solvra-vscode, with its settings pointing at the above
# Missing prerequisites are installed per user: .NET 8 (Microsoft's dotnet-install.ps1),
# Node.js + the Codex CLI (winget + npm, for ChatGPT), and Git for Windows (winget; it provides
# the bash that Solvra uses to run commands).
[CmdletBinding()]
param(
  [string]$Model = 'gpt-5.6-sol',
  [switch]$NoChatGPT,
  [switch]$NoVSCode,
  [switch]$NoCli,
  [switch]$NoGit,
  [switch]$NoTest,
  [string]$TelemetryConfig
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Say([string]$m)  { Write-Host "==> $m" -ForegroundColor Cyan }
function Warn([string]$m) { Write-Host "warning: $m" -ForegroundColor Yellow }
function Die([string]$m)  { Write-Host "error: $m" -ForegroundColor Red; throw $m }

# ------------------------------------------------------------------ helpers (also used by tests)

function Invoke-NativeCapture([string]$FilePath, [string[]]$ArgumentList) {
  # In Windows PowerShell 5.1, redirected stderr becomes ErrorRecord objects.
  # Scope Continue to this invocation; process failure is determined by exit code.
  $ErrorActionPreference = 'Continue'
  $global:LASTEXITCODE = $null
  $lines = @(& $FilePath @ArgumentList 2>&1)
  $code = $global:LASTEXITCODE
  if ($null -eq $code) { throw "Could not run native command: $FilePath" }
  [pscustomobject]@{
    ExitCode = $code
    Output = (($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()
  }
}

function Test-Net8([string]$dotnet) {
  if (-not $dotnet -or -not (Test-Path -LiteralPath $dotnet)) { return $false }
  try { $result = Invoke-NativeCapture $dotnet @('--list-runtimes') } catch { return $false }
  return ($result.ExitCode -eq 0 -and $result.Output -match '(?m)^Microsoft\.NETCore\.App 8\.')
}

function Find-Dotnet {
  $candidates = @()
  if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
  $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
  if ($cmd) { $candidates += $cmd.Source }
  if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') }
  if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe') }
  foreach ($c in $candidates) { if (Test-Net8 $c) { return $c } }
  return $null
}

function Find-Codex {
  $candidates = @()
  if ($env:SOLVRA_CODEX_BIN) { $candidates += $env:SOLVRA_CODEX_BIN }
  foreach ($name in 'codex.exe', 'codex.cmd') {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
  }
  if ($env:APPDATA) { $candidates += (Join-Path $env:APPDATA 'npm\codex.cmd') }
  foreach ($c in $candidates) { if ($c -and (Test-Path -LiteralPath $c)) { return $c } }
  return $null
}

function Find-GitBash {
  $roots = @($env:ProgramFiles, $env:ProgramW6432, ${env:ProgramFiles(x86)})
  if ($env:LOCALAPPDATA) { $roots += (Join-Path $env:LOCALAPPDATA 'Programs') }
  foreach ($r in $roots) {
    if (-not $r) { continue }
    $p = Join-Path $r 'Git\bin\bash.exe'
    if (Test-Path -LiteralPath $p) { return $p }
  }
  return $null
}

function Find-Code {
  $candidates = @()
  if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\Microsoft VS Code\bin\code.cmd') }
  if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'Microsoft VS Code\bin\code.cmd') }
  $cmd = Get-Command code.cmd -ErrorAction SilentlyContinue
  if ($cmd) { $candidates += $cmd.Source }
  foreach ($c in $candidates) { if ($c -and (Test-Path -LiteralPath $c)) { return $c } }
  return $null
}

function Add-UserPath([string]$dir) {
  $current = [Environment]::GetEnvironmentVariable('Path', 'User')
  $parts = @()
  if ($current) { $parts = $current.Split(';') | Where-Object { $_ } }
  if ($parts -notcontains $dir) {
    [Environment]::SetEnvironmentVariable('Path', (($parts + $dir) -join ';'), 'User')
    return $true
  }
  return $false
}

# Text of %LOCALAPPDATA%\Solvra\bin\solvra.cmd. Provider settings are defaults only: variables
# already set by the user win.
function Get-WrapperText([string]$dotnet, [string]$dll, [string]$provider, [string]$model, [string]$codex) {
  $lines = @('@echo off', 'rem solvra-windows-installer: starts the Solvra backend with .NET 8.', 'setlocal')
  if ($provider) { $lines += "if not defined SOLVRA_PROVIDER set `"SOLVRA_PROVIDER=$provider`"" }
  if ($model) { $lines += "if not defined SOLVRA_MODEL set `"SOLVRA_MODEL=$model`"" }
  if ($codex) { $lines += "if not defined SOLVRA_CODEX_BIN set `"SOLVRA_CODEX_BIN=$codex`"" }
  $lines += "`"$dotnet`" `"$dll`" %*"
  return ($lines -join "`r`n") + "`r`n"
}

# Merge Solvra's settings into VS Code's user settings.json. Returns $false (and leaves the file
# alone) when it cannot be parsed, e.g. because it has comments.
function Update-VSCodeSettings([string]$path, [hashtable]$values) {
  $obj = New-Object PSObject
  if (Test-Path -LiteralPath $path) {
    $text = [IO.File]::ReadAllText($path)
    if ($text.Trim()) {
      # Never rewrite a file with comments (PowerShell 7 would parse it and drop them).
      if ($text -match '(?m)^\s*//' -or $text -match '/\*') { return $false }
      try { $obj = $text | ConvertFrom-Json } catch { return $false }
      if ($null -eq $obj -or $obj -is [array] -or $obj -isnot [psobject]) { return $false }
    }
    Copy-Item -LiteralPath $path -Destination "$path.solvra-backup" -Force
  } else {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
  }
  foreach ($k in $values.Keys) { $obj | Add-Member -NotePropertyName $k -NotePropertyValue $values[$k] -Force }
  $json = $obj | ConvertTo-Json -Depth 50
  [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding $false))
  return $true
}

function Invoke-Winget([string]$id, [string]$what) {
  $winget = Get-Command winget -ErrorAction SilentlyContinue
  if (-not $winget) { Warn "winget is not available, so $what cannot be installed automatically."; return $false }
  Say "Installing $what with winget ($id)..."
  & $winget.Source install --id $id -e --silent --accept-source-agreements --accept-package-agreements | Out-Host
  return ($LASTEXITCODE -eq 0)
}

function Update-SessionPath {
  $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

# ------------------------------------------------------------------ main
if ($MyInvocation.InvocationName -eq '.') { return }  # dot-sourced by tests: helpers only

$Bundle = Split-Path -Parent $MyInvocation.MyCommand.Path
$Version = 'unknown'
if (Test-Path (Join-Path $Bundle 'VERSION')) { $Version = (Get-Content (Join-Path $Bundle 'VERSION') -Raw).Trim() }
$BackendSrc = Join-Path $Bundle 'backend'
$Vsix = Get-ChildItem -Path $Bundle -Filter 'solvra-vscode-*.vsix' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not (Test-Path (Join-Path $BackendSrc 'Solvra.dll'))) { Die 'backend\Solvra.dll not found next to install.ps1. Run it from the extracted folder.' }

$Root = Join-Path $env:LOCALAPPDATA 'Solvra'
$Bin = Join-Path $Root 'bin'
$Current = Join-Path $Root 'current'
$Dll = Join-Path $Current 'Solvra.dll'

Write-Host ''
Write-Host "Solvra $Version for Windows" -ForegroundColor White
Write-Host ''

# Files from a downloaded zip carry "mark of the web"; unblock them so .NET loads the libraries.
Get-ChildItem -LiteralPath $Bundle -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

# ---- .NET 8 runtime
$Dotnet = Find-Dotnet
if (-not $Dotnet) {
  Say '.NET 8 runtime not found; installing it for your user with Microsoft''s dotnet-install.ps1...'
  $tmp = Join-Path ([IO.Path]::GetTempPath()) "dotnet-install-$([Guid]::NewGuid().ToString('N')).ps1"
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $tmp
  & $tmp -Runtime dotnet -Channel 8.0 -InstallDir (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet') -NoPath | Out-Host
  Remove-Item $tmp -Force -ErrorAction SilentlyContinue
  $Dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
  if (-not (Test-Net8 $Dotnet)) { Die ".NET 8 install did not produce a working runtime at $Dotnet. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run this again." }
}
Say "Using .NET: $Dotnet"

# ---- backend + solvra command
$Release = Join-Path (Join-Path $Root 'releases') $Version
Say "Installing Solvra backend $Version to $Release"
New-Item -ItemType Directory -Force -Path (Join-Path $Root 'releases'), $Bin | Out-Null
if (Test-Path -LiteralPath $Release) { Remove-Item -LiteralPath $Release -Recurse -Force }
Copy-Item -LiteralPath $BackendSrc -Destination $Release -Recurse
# Replace the "current" junction (rmdir removes only the link, never the release it points to).
if (Test-Path -LiteralPath $Current) { cmd /c rmdir "`"$Current`"" | Out-Null }
New-Item -ItemType Junction -Path $Current -Target $Release | Out-Null

$versionResult = Invoke-NativeCapture $Dotnet @($Dll, '--version')
$BackendVersion = $versionResult.Output
if ($versionResult.ExitCode -ne 0) { Die "Solvra failed to start (exit $($versionResult.ExitCode)): $BackendVersion" }
Say "Solvra backend: $BackendVersion"

# ---- Git Bash (Solvra runs commands with bash)
$GitBash = Find-GitBash
if (-not $GitBash -and -not $NoGit) {
  Say 'Git for Windows is not installed. Solvra needs its bash to run commands (tests, builds, git).'
  if (Invoke-Winget 'Git.Git' 'Git for Windows') { $GitBash = Find-GitBash }
}
if ($GitBash) { Say "Commands will run with: $GitBash" }
else { Warn 'No Git Bash: Solvra can read and edit files, but running commands will fail until you install Git for Windows (https://git-scm.com/download/win).' }

# ---- ChatGPT through the Codex sign-in
$Codex = $null
$ChatGPTOk = $false
if (-not $NoChatGPT) {
  Say 'Setting up ChatGPT through your Codex sign-in (no API key needed)...'
  $Codex = Find-Codex
  if (-not $Codex) {
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
      Say 'The Codex CLI needs Node.js.'
      if (Invoke-Winget 'OpenJS.NodeJS.LTS' 'Node.js LTS') { Update-SessionPath }
    }
    if (Get-Command npm -ErrorAction SilentlyContinue) {
      Say 'Installing the Codex CLI (npm install -g @openai/codex)...'
      & npm install -g @openai/codex | Out-Host
      $Codex = Find-Codex
    }
  }
  if (-not $Codex) {
    Warn 'Could not install the Codex CLI. Install Node.js (https://nodejs.org), run "npm install -g @openai/codex", then run this installer again.'
  } else {
    Say "Codex CLI: $Codex"
    $codexHome = $env:CODEX_HOME
    if (-not $codexHome) { $codexHome = Join-Path $env:USERPROFILE '.codex' }
    if (-not (Test-Path (Join-Path $codexHome 'auth.json'))) {
      Say 'Signing in to ChatGPT with Codex (your browser will open)...'
      & $Codex login | Out-Host
    }
    if (Test-Path (Join-Path $codexHome 'auth.json')) { $ChatGPTOk = $true; Say 'Codex sign-in found.' }
    else { Warn 'No Codex sign-in yet. Run "codex login", or use "Sign in with Codex" in the Solvra chat.' }
  }
}

$Provider = $null; $QualifiedModel = $null
if (-not $NoChatGPT) { $Provider = 'chatgpt'; $QualifiedModel = "chatgpt:$Model" }

if (-not $NoCli) {
  $wrapper = Join-Path $Bin 'solvra.cmd'
  [IO.File]::WriteAllText($wrapper, (Get-WrapperText $Dotnet $Dll $Provider $QualifiedModel $Codex), (New-Object Text.ASCIIEncoding))
  if (Add-UserPath $Bin) { Say "Added $Bin to your PATH (new terminals will have the solvra command)." }
}

# ---- VS Code extension + settings
$VSCodeOk = $false
if (-not $NoVSCode) {
  $Code = Find-Code
  if (-not $Vsix) { Warn 'No solvra-vscode-*.vsix in this folder; skipping VS Code.' }
  elseif (-not $Code) {
    Warn "VS Code not found. Install it from https://code.visualstudio.com, then run this installer again (or install $($Vsix.Name) from the Extensions view: ... -> Install from VSIX)."
  } else {
    Say "Installing the VS Code extension $($Vsix.Name)"
    & $Code --install-extension $Vsix.FullName --force | Out-Host
    $VSCodeOk = ($LASTEXITCODE -eq 0)
    $settings = @{ 'solvra.executablePath' = $Dll; 'solvra.dotnetPath' = $Dotnet }
    if ($QualifiedModel) { $settings['solvra.model'] = $QualifiedModel }
    $settingsPath = Join-Path $env:APPDATA 'Code\User\settings.json'
    if (Update-VSCodeSettings $settingsPath $settings) {
      Say 'VS Code settings updated (previous file kept as settings.json.solvra-backup).'
    } else {
      Warn 'Your VS Code settings.json has comments, so it was left alone. Add these settings yourself:'
      foreach ($k in $settings.Keys) { Write-Host ('    "{0}": {1},' -f $k, ($settings[$k] | ConvertTo-Json)) }
    }
  }
}

# ---- one tiny test request
if ($ChatGPTOk -and -not $NoTest) {
  Say "Testing $Model with a one-line request..."
  try {
    $test = Invoke-NativeCapture $Dotnet @($Dll, 'run', 'Reply with exactly the word OK and nothing else.', '--provider', 'chatgpt', '--model', $Model, '--no-tools', '--no-session', '--ephemeral', '--max-turns', '1', '--time-limit-seconds', '90')
    if ($test.ExitCode -eq 0 -and $test.Output -match '(?m)^OK\r?$') { Say 'Model answered: OK' }
    else { Warn "Model test did not succeed (exit $($test.ExitCode)); installation will continue. Try Solvra in VS Code:`n$($test.Output)" }
  } catch {
    Warn "Model test could not run; installation will continue: $($_.Exception.Message)"
  }
}

# Telemetry credentials are supplied privately, never included in the public zip.
if ($TelemetryConfig) {
  if ($NoCli) { throw '-TelemetryConfig requires the CLI backend; omit -NoCli.' }
  & (Join-Path $PSScriptRoot 'configure-telemetry.ps1') -ConfigFile $TelemetryConfig -Dotnet $Dotnet -Dll $Dll
  if (-not $?) { throw 'Telemetry worker setup failed.' }
} elseif (Test-Path (Join-Path $env:USERPROFILE '.solvra\telemetry.json')) {
  & (Join-Path $PSScriptRoot 'configure-telemetry.ps1') -ConfigFile (Join-Path $env:USERPROFILE '.solvra\telemetry.json') -Dotnet $Dotnet -Dll $Dll
  if (-not $?) { throw 'Telemetry worker setup failed.' }
} else {
  Write-Host 'Telemetry is not connected yet. Re-run with -TelemetryConfig <private-device.json> to enable cloud collection.'
}

Write-Host ''
Say 'Done.'
if (-not $NoCli) { Write-Host '  Command:  open a new terminal, then: solvra chat' }
if ($VSCodeOk) { Write-Host '  VS Code:  close and reopen VS Code, open a folder, then click the Solvra icon in the Activity Bar.' }
if ($ChatGPTOk) { Write-Host "  Model:    ChatGPT $Model via your Codex sign-in. Change it from the model menu in the chat." }
elseif ($NoChatGPT) { Write-Host '  Provider: add an API key in the chat (Settings -> API keys), or run .\install.ps1 again without -NoChatGPT.' }
Write-Host '  Update:   run the next version''s installer; it replaces this one and keeps your settings.'
Write-Host '  Remove:   code --uninstall-extension solvra.solvra-vscode; delete %LOCALAPPDATA%\Solvra; remove it from your user PATH.'
