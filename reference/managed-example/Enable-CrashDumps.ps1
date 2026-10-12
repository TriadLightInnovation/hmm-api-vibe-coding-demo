# Run manually from an elevated PowerShell. Never invoked by build or application.
[CmdletBinding()]
param([string]$ExeDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script manually in an elevated PowerShell (Run as administrator).'
}
$exeRoot = (Resolve-Path -LiteralPath $ExeDirectory).Path
$exeName = 'HMM_FPGA_UI_Example.Managed.exe'
if (-not (Test-Path -LiteralPath (Join-Path $exeRoot $exeName) -PathType Leaf)) {
    throw "Expected $exeName in -ExeDirectory. Use the script beside the built EXE or supply that directory."
}
$dumpFolder = Join-Path $exeRoot 'crash-dumps'
New-Item -ItemType Directory -Path $dumpFolder -Force | Out-Null
$keyPath = 'SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\HMM_FPGA_UI_Example.Managed.exe'
$baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
try {
    $appKey = $baseKey.CreateSubKey($keyPath)
    try {
        $appKey.SetValue('DumpFolder', $dumpFolder, [Microsoft.Win32.RegistryValueKind]::ExpandString)
        $appKey.SetValue('DumpType', 2, [Microsoft.Win32.RegistryValueKind]::DWord)
        $appKey.SetValue('DumpCount', 5, [Microsoft.Win32.RegistryValueKind]::DWord)
        $appKey.Flush()
    } finally { $appKey.Dispose() }
} finally { $baseKey.Dispose() }
Write-Host "Enabled per-application full dumps (DumpType=2, DumpCount=5) for $exeName"
Write-Host "Dump directory: $dumpFolder"
Write-Host 'The normal application user must have write access to this directory. No application has been launched.'
