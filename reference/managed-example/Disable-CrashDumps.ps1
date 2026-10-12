# Removes only this application's override. Does not delete dumps or alter global WER settings.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script manually in an elevated PowerShell (Run as administrator).'
}
$keyPath = 'SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\HMM_FPGA_UI_Example.Managed.exe'
$baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
try { $baseKey.DeleteSubKeyTree($keyPath, $false) } finally { $baseKey.Dispose() }
Write-Host 'Removed HMM_FPGA_UI_Example.Managed.exe LocalDumps override only. Existing dumps and global WER settings remain.'
