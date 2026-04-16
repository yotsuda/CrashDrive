#Requires -Modules Microsoft.PowerShell.PlatyPS
<#
.SYNOPSIS
    Update PlatyPS markdown from loaded module (reflects parameter changes).
.DESCRIPTION
    CrashDrive module must be imported before running this script.
    Parameter metadata (Type, Position, Default value, etc.) is updated.
    Hand-written descriptions and parameter order are preserved.
#>
$ErrorActionPreference = 'Stop'

if (-not (Get-Module CrashDrive)) {
    throw 'CrashDrive module is not loaded. Run Import-Module CrashDrive first.'
}

$mdPath = "$PSScriptRoot\md"

# --- Update command help markdown ---
$mdFiles = Measure-PlatyPSMarkdown -Path "$mdPath\*.md"

$cmdFiles = $mdFiles | Where-Object Filetype -match 'CommandHelp'
if ($cmdFiles) {
    $cmdFiles | Update-MarkdownCommandHelp -Path { $_.FilePath } -NoBackup
    Write-Host "Updated: $($cmdFiles.Count) command help file(s)" -ForegroundColor Cyan
}

# --- Workarounds for PlatyPS v1.0.1 bugs ---
$cmdMdFiles = Get-ChildItem $mdPath -Filter '*.md' |
    Where-Object { $_.Name -notlike 'about_*' -and $_.Name -ne 'CrashDrive.md' }

# Remove duplicate __AllParameterSets SYNTAX block
$syntaxPattern = '(?s)\r?\n### __AllParameterSets\r?\n\r?\n```\r?\n.*?```\r?\n'
# Remove placeholder OUTPUTS System.Object section
$outputPattern = '(?s)### System\.Object\r?\n\r?\n\{\{ Fill in the Description \}\}\r?\n\r?\n'

foreach ($file in $cmdMdFiles) {
    $content = [System.IO.File]::ReadAllText($file.FullName)
    $cleaned = $content

    # Remove BOM if present
    if ($cleaned.Length -gt 0 -and $cleaned[0] -eq [char]0xFEFF) {
        $cleaned = $cleaned.Substring(1)
    }

    $cleaned = [regex]::Replace($cleaned, $syntaxPattern, '')
    $cleaned = [regex]::Replace($cleaned, $outputPattern, '')

    # Normalize line endings to CRLF (Update-MarkdownCommandHelp mixes LF and CRLF)
    $cleaned = $cleaned -replace "(?<!\r)\n", "`r`n"

    if ($cleaned -ne $content) {
        $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllText($file.FullName, $cleaned, $utf8NoBom)
        Write-Host "Cleaned: $($file.Name)" -ForegroundColor Yellow
    }
}

Write-Host 'Markdown updated. Review changes, then run build.ps1.' -ForegroundColor Green
