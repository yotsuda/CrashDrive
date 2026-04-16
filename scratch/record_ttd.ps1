#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Record a TTD trace of a target program. Requires admin privileges.
.EXAMPLE
    # Run from an elevated PowerShell:
    .\record_ttd.ps1 -Program python -ArgumentList 'sample_target.py'
#>
param(
    [Parameter(Mandatory)]
    [string]$Program,

    [string[]]$ArgumentList = @(),

    [string]$OutDir = 'C:\MyProj\CrashDrive\scratch\ttd'
)

$ErrorActionPreference = 'Stop'
New-Item -Path $OutDir -ItemType Directory -Force | Out-Null

Write-Host "Recording TTD to $OutDir" -ForegroundColor Cyan
$allArgs = @('-out', $OutDir, $Program) + $ArgumentList
& tttracer.exe @allArgs
if ($LASTEXITCODE -ne 0) { throw "tttracer failed with exit code $LASTEXITCODE" }

Write-Host "`nFiles produced:" -ForegroundColor Green
Get-ChildItem $OutDir -Recurse | Format-Table Name, Length -AutoSize
