# Checks for install.ps1 helpers; runs anywhere PowerShell 7 runs:  pwsh scripts/windows/test-install.ps1 scripts/windows/install.ps1
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile($args[0], [ref]$null, [ref]$errors) | Out-Null
if ($errors.Count) { $errors | ForEach-Object { Write-Host "PARSE: $($_.Message) line $($_.Extent.StartLineNumber)" }; exit 1 }
. $args[0]  # helpers only (the installer stops when dot-sourced)
$w = Get-WrapperText 'C:\dn\dotnet.exe' 'C:\S\current\Solvra.dll' 'chatgpt' 'chatgpt:gpt-5.6-sol' 'C:\npm\codex.cmd'
if ($w -notmatch 'if not defined SOLVRA_PROVIDER set "SOLVRA_PROVIDER=chatgpt"' -or $w -notmatch '"C:\\dn\\dotnet.exe" "C:\\S\\current\\Solvra.dll" %\*') { Write-Host "wrapper bad:`n$w"; exit 1 }
$tmp = Join-Path ([IO.Path]::GetTempPath()) "svps-settings-$PID.json"
'{ "editor.fontSize": 14, "solvra.model": "old" }' | Set-Content $tmp
if (-not (Update-VSCodeSettings $tmp @{ 'solvra.model' = 'chatgpt:gpt-5.6-sol'; 'solvra.executablePath' = 'C:\S\current\Solvra.dll' })) { Write-Host 'merge failed'; exit 1 }
$o = Get-Content $tmp -Raw | ConvertFrom-Json
if ($o.'editor.fontSize' -ne 14 -or $o.'solvra.model' -ne 'chatgpt:gpt-5.6-sol' -or $o.'solvra.executablePath' -ne 'C:\S\current\Solvra.dll') { Write-Host "merged wrong: $(Get-Content $tmp -Raw)"; exit 1 }
if (-not (Test-Path "$tmp.solvra-backup")) { Write-Host 'no backup'; exit 1 }
"{`n // comment`n `"a`": 1 }" | Set-Content $tmp
$before = Get-Content $tmp -Raw
$r = Update-VSCodeSettings $tmp @{ 'x' = 1 }
# PowerShell 7 accepts comments in JSON; 5.1 refuses. Either way the file must stay valid JSON(C).
if ($r -or (Get-Content $tmp -Raw) -ne $before) { Write-Host "comments were rewritten"; exit 1 }; Write-Host "comments: left alone"
$new = Join-Path ([IO.Path]::GetTempPath()) "svps-new-$PID\settings.json"
if (-not (Update-VSCodeSettings $new @{ 'solvra.model' = 'chatgpt:x' })) { Write-Host 'new file failed'; exit 1 }
Write-Host "new file: $((Get-Content $new -Raw) -replace '\s+',' ')"
Remove-Item $tmp, "$tmp.solvra-backup" -ErrorAction SilentlyContinue
Write-Host 'PS-CHECKS-OK'
