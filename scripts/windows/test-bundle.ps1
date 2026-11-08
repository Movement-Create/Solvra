# Exercise the delivered ZIP with the same host used by Install Solvra.cmd.
param([Parameter(Mandatory = $true)][string]$Archive)
$ErrorActionPreference = 'Stop'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('solvra-bundle-' + [guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $Archive -DestinationPath $temp
    $root = @(Get-ChildItem $temp -Directory)
    if ($root.Count -ne 1) { throw 'Expected one bundle directory' }
    $scripts = @(Get-ChildItem $root[0].FullName -Filter '*.ps1')
    if ($scripts.Count -lt 2) { throw 'Installer or telemetry helper missing' }
    foreach ($script in $scripts) {
        $bytes = [IO.File]::ReadAllBytes($script.FullName)
        if ($bytes.Length -lt 3 -or $bytes[0] -ne 239 -or $bytes[1] -ne 187 -or $bytes[2] -ne 191) {
            throw "Missing UTF-8 BOM: $($script.Name)"
        }
        $parseErrors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$null, [ref]$parseErrors) | Out-Null
        if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
    }
    $launcher = Get-Content (Join-Path $root[0].FullName 'Install Solvra.cmd') -Raw
    if ($launcher -notmatch 'powershell -NoProfile -ExecutionPolicy Bypass -File') {
        throw 'Launcher host changed; update the compatibility test'
    }
    # A child process preserves the helper test's exit code on both PowerShell hosts.
    $hostExe = (Get-Process -Id $PID).Path
    & $hostExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-install.ps1') (Join-Path $root[0].FullName 'install.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Packaged installer helper checks failed' }
    if ($PSVersionTable.PSVersion.Major -eq 5) {
        & $hostExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test-install-flow.ps1') (Join-Path $root[0].FullName 'install.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'Packaged installer flow checks failed' }
    }
    Write-Host "BUNDLE-CHECKS-OK PowerShell $($PSVersionTable.PSVersion)"
} finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}
