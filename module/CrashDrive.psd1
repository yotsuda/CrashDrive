@{
    RootModule           = 'CrashDrive.dll'
    ModuleVersion        = '0.4.0'
    GUID                 = 'b6f1e4d2-5a9c-4e83-91f4-7a3b6e2d8c4f'
    Author               = 'Yoshifumi Tsuda'
    Copyright            = '(c) Yoshifumi Tsuda. All rights reserved.'
    Description          = @'
Mount Windows post-mortem artifacts (crash dumps, Time-Travel Debugging recordings, execution traces) as PSDrives. Navigate them with ls, cd, cat - the filesystem metaphor humans already know.

Combined with splash (ConPTY-based shell MCP server), AI agents can browse and reason about post-mortem state through the same filesystem idioms, enabling AI-driven post-mortem debugging without specialized debugger vocabulary.

Related: splash - https://github.com/yotsuda/splash (npm: @ytsuda/splash)
'@
    PowerShellVersion    = '7.4'
    CompatiblePSEditions = @('Core')

    # Pre-loaded before RootModule so CrashDrive.dll's refs to these resolve
    # from bin\ rather than needing them in the module root.
    RequiredAssemblies   = @(
        'bin\Microsoft.Diagnostics.Runtime.dll'
        'bin\Microsoft.Diagnostics.NETCore.Client.dll'
    )

    CmdletsToExport      = @(
        'New-CrashDrive'
        'Enable-CrashEditorFollow'
        'Disable-CrashEditorFollow'
        'Read-CrashMemory'
        'Get-CrashObject'
        'Get-CrashLocalVariable'
        'Invoke-CrashCommand'
    )
    FunctionsToExport    = @()
    AliasesToExport      = @()
    VariablesToExport    = @()

    FormatsToProcess     = @('CrashDrive.Format.ps1xml')

    PrivateData          = @{
        PSData = @{
            Tags       = @('Debug', 'Trace', 'CrashDump', 'Provider', 'PSDrive', 'Forensics', 'TTD', 'PostMortem', 'AI', 'MCP')
            LicenseUri = 'https://github.com/yotsuda/CrashDrive/blob/master/LICENSE'
            ProjectUri = 'https://github.com/yotsuda/CrashDrive'
        }
    }
}
