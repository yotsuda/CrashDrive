#Requires -Version 7.4
# Self-dump in DumpType.Full so ClrMD's GetMethodByInstructionPointer can
# resolve arbitrary JIT'd IPs — required for managed source resolution.
param(
    [string]$Out = (Join-Path $PSScriptRoot 'self-full.dmp')
)

Add-Type -Path 'C:\Program Files\PowerShell\7\Modules\CrashDrive\Microsoft.Diagnostics.NETCore.Client.dll'
Import-Module CrashDrive -Force

# Touch a drive so provider/store code gets JIT'd and has managed methods
# live on the heap. This maximizes what the dump can demonstrate.
if (-not (Get-PSDrive warm -ErrorAction SilentlyContinue)) {
    New-CrashDrive -Name warm -Path (Join-Path $PSScriptRoot 'self.dmp') | Out-Null
}
(Get-PSDrive warm).Store.Modules | Out-Null
(Get-PSDrive warm).Store.Threads | Out-Null

Remove-Item $Out -ErrorAction SilentlyContinue
$client = [Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient]::new($PID)
$client.WriteDump([Microsoft.Diagnostics.NETCore.Client.DumpType]::Full, $Out)
Write-Host "Wrote: $Out ($([long]((Get-Item $Out).Length / 1MB)) MB)"
