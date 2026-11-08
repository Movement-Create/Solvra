# Runs in its own Windows PowerShell process: privilege removal is permanent for that process only.
param([string]$ConfigFile, [string]$Dotnet, [string]$Dll, [string]$Profile)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class RemoveAclPrivileges {
  [StructLayout(LayoutKind.Sequential)] public struct Luid { public uint Low; public int High; }
  [StructLayout(LayoutKind.Sequential)] public struct Privileges { public uint Count; public Luid Id; public uint Attributes; }
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool LookupPrivilegeValue(string system, string name, out Luid id);
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref Privileges state, uint length, IntPtr previous, IntPtr returned);
  [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  public static void Remove(string name) {
    IntPtr token;
    if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token)) throw new Win32Exception();
    try {
      Luid id;
      if (!LookupPrivilegeValue(null, name, out id)) throw new Win32Exception();
      var state = new Privileges { Count=1, Id=id, Attributes=4 };
      if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
      int error = Marshal.GetLastWin32Error();
      if (error != 0 && error != 1300) throw new Win32Exception(error);
    } finally { CloseHandle(token); }
  }
}
'@
foreach ($name in @('SeSecurityPrivilege', 'SeBackupPrivilege', 'SeRestorePrivilege', 'SeTakeOwnershipPrivilege')) {
  [RemoveAclPrivileges]::Remove($name)
}
$privileges = (& whoami.exe /priv | Out-String)
if ($LASTEXITCODE -ne 0 -or $privileges -match 'SeSecurityPrivilege|SeBackupPrivilege|SeRestorePrivilege|SeTakeOwnershipPrivilege') {
  throw 'ACL test still has privileged security access'
}
$env:USERPROFILE = $Profile
$directory = Join-Path $Profile '.solvra'
New-Item -ItemType Directory -Force $directory | Out-Null
$env:SOLVRA_TELEMETRY_CONFIG = Join-Path $directory 'telemetry.json'
$sidType = [System.Security.Principal.SecurityIdentifier]
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$info = [IO.DirectoryInfo]::new($directory)
$before = $info.GetAccessControl()
$owner = $before.GetOwner($sidType).Value
# Confirm that previously explicit broad grants are removed as well as inheritance.
$before.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
  [System.Security.Principal.SecurityIdentifier]::new('S-1-1-0'), 'Read', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
$info.SetAccessControl($before)
foreach ($attempt in @(1, 2)) {
  & (Join-Path $PSScriptRoot 'configure-telemetry.ps1') -ConfigFile $ConfigFile -Dotnet $Dotnet -Dll $Dll -NoStart
  if ($LASTEXITCODE -ne 0) { throw 'Telemetry import failed' }
  $actual = $info.GetAccessControl()
  $rules = @($actual.GetAccessRules($true, $true, $sidType))
  if (-not $actual.AreAccessRulesProtected -or $rules.Count -ne 2 -or $actual.GetOwner($sidType).Value -ne $owner) {
    throw 'Directory protection or ownership changed incorrectly'
  }
  foreach ($rule in $rules) {
    if ($rule.IdentityReference.Value -notin @($sid, 'S-1-5-18') -or $rule.AccessControlType -ne 'Allow' -or $rule.FileSystemRights -ne 'FullControl') {
      throw 'Unexpected directory access grant'
    }
  }
  $fileRules = @(([IO.FileInfo]::new($env:SOLVRA_TELEMETRY_CONFIG)).GetAccessControl().GetAccessRules($true, $true, $sidType))
  if ($fileRules.Count -ne 2 -or @($fileRules | Where-Object { $_.IdentityReference.Value -notin @($sid, 'S-1-5-18') }).Count) {
    throw 'Device credential file has unexpected access grants'
  }
  Write-Host "TELEMETRY-ACL-OK attempt=$attempt without security/backup/restore/ownership privileges"
}
exit 0
