#Requires -Version 7.4
<#
.SYNOPSIS
    Build and deploy the CrashDrive module to the local PowerShell Modules directory.
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$ModulePath = "$env:ProgramFiles\PowerShell\7\Modules\CrashDrive"
)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$srcProject = Join-Path $projectDir 'src\CrashDrive\CrashDrive.csproj'
$moduleSource = Join-Path $projectDir 'module'

Write-Host "Building CrashDrive ($Configuration)..." -ForegroundColor Cyan
dotnet build $srcProject -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if (Test-Path $ModulePath) {
    Remove-Item "$ModulePath\*" -Recurse -Force
} else {
    New-Item -Path $ModulePath -ItemType Directory -Force | Out-Null
}

Copy-Item (Join-Path $moduleSource 'CrashDrive.psd1')          $ModulePath
Copy-Item (Join-Path $moduleSource 'CrashDrive.Format.ps1xml') $ModulePath

$buildOutput = Join-Path $projectDir "src\CrashDrive\bin\$Configuration\net8.0"
Copy-Item (Join-Path $buildOutput 'CrashDrive.dll') $ModulePath
# PDB too: managed-frame source resolution needs it next to the DLL when
# the compile-time path is gone (self-dump from another machine, etc.).
$pdb = Join-Path $buildOutput 'CrashDrive.pdb'
if (Test-Path $pdb) { Copy-Item $pdb $ModulePath }
# Dependencies not in the PowerShell host: copy them alongside the module DLL.
@(
    'Microsoft.Diagnostics.Runtime.dll',
    'Microsoft.Diagnostics.NETCore.Client.dll'
) | ForEach-Object {
    $src = Join-Path $buildOutput $_
    if (Test-Path $src) { Copy-Item $src $ModulePath }
}

Write-Host "Deployed to $ModulePath" -ForegroundColor Green
Get-ChildItem $ModulePath | Format-Table Name, Length -AutoSize
