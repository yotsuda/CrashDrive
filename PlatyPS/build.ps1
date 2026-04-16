#Requires -Modules Microsoft.PowerShell.PlatyPS
<#
.SYNOPSIS
    Build MAML XML help from PlatyPS markdown and deploy to module folder.
.DESCRIPTION
    Generates XML help files from markdown sources and copies them to:
      - module/en-US/  (for $PSUICulture = en-US)
      - module/ja-JP/  (for $PSUICulture = ja-JP)
#>
$ErrorActionPreference = 'Stop'

$mdPath = "$PSScriptRoot\md"
$xmlPath = "$PSScriptRoot\xml"
$moduleDir = Join-Path $PSScriptRoot '..\module'

# Clean previous build output
if (Test-Path $xmlPath) {
    Remove-Item "$xmlPath\*" -Recurse -Force
}

# Build XML from markdown
Write-Host 'Building XML help...' -ForegroundColor Cyan
Measure-PlatyPSMarkdown -Path "$mdPath\*.md" |
    Where-Object Filetype -match 'CommandHelp' |
    Import-MarkdownCommandHelp -Path { $_.FilePath } |
    Export-MamlCommandHelp -OutputFolder $xmlPath -Force

# Workaround: PlatyPS v1 emits &#x80; (invalid XML char) between example paragraphs
foreach ($xml in Get-ChildItem $xmlPath -Filter '*.xml' -Recurse) {
    $content = [System.IO.File]::ReadAllText($xml.FullName)
    $cleaned = $content -replace '\s*<maml:para>&#x80;</maml:para>\r?\n', ''
    if ($cleaned -ne $content) {
        [System.IO.File]::WriteAllText($xml.FullName, $cleaned)
        Write-Host "Cleaned invalid XML chars: $($xml.Name)" -ForegroundColor Yellow
    }
}

# Export-MamlCommandHelp creates a module-named subfolder
$xmlSourceDir = Join-Path $xmlPath 'CrashDrive'

# Deploy to locale folders (ja-JP and en-US)
foreach ($dir in @("$moduleDir\en-US", "$moduleDir\ja-JP")) {
    if (-not (Test-Path $dir)) {
        New-Item -Path $dir -ItemType Directory -Force | Out-Null
    }
    Copy-Item "$xmlSourceDir\*" $dir -Force
}

# Deploy about_ topics to locale folders
$aboutFiles = Get-ChildItem "$moduleDir\ja-JP" -Filter 'about_*.help.txt' -ErrorAction SilentlyContinue
foreach ($f in $aboutFiles) {
    Copy-Item $f.FullName "$moduleDir\en-US" -Force
}

Write-Host 'Help files deployed to module/en-US/ and module/ja-JP/.' -ForegroundColor Green
Get-ChildItem $xmlSourceDir -Filter *.xml | Format-Table Name, Length -AutoSize
if ($aboutFiles) {
    Write-Host "About topics: $($aboutFiles.Name -join ', ')" -ForegroundColor Green
}
