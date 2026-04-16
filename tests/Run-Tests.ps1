#Requires -Version 7.4
<#
.SYNOPSIS
    Deploy CrashDrive locally and run the Pester smoke suite.

.NOTES
    Runs deploy.ps1 first so the tests exercise the installed module (not the
    built-tree one), which is how end users see it. Pass -SkipDeploy to reuse
    an already-installed build.
#>
param(
    [switch]$SkipDeploy,
    [string[]]$Tag,
    [string[]]$ExcludeTag
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not $SkipDeploy) {
    & (Join-Path $repo 'deploy.ps1')
}

if (-not (Get-Module -ListAvailable Pester | Where-Object Version -ge '5.3.0')) {
    Write-Host 'Installing Pester 5.x (CurrentUser)...' -ForegroundColor Cyan
    Install-Module -Name Pester -MinimumVersion 5.3.0 -Scope CurrentUser -Force -SkipPublisherCheck
}

Import-Module Pester -MinimumVersion 5.3.0 -Force

$cfg = New-PesterConfiguration
$cfg.Run.Path = Join-Path $PSScriptRoot 'CrashDrive.Tests.ps1'
$cfg.Output.Verbosity = 'Detailed'
$cfg.Run.Exit = $true
if ($Tag)        { $cfg.Filter.Tag = $Tag }
if ($ExcludeTag) { $cfg.Filter.ExcludeTag = $ExcludeTag }

Invoke-Pester -Configuration $cfg
