@{
    RootModule           = 'CrashDrive.dll'
    ModuleVersion        = '0.9.0'
    GUID                 = 'b6f1e4d2-5a9c-4e83-91f4-7a3b6e2d8c4f'
    Author               = 'Yoshifumi Tsuda'
    Copyright            = '(c) Yoshifumi Tsuda. All rights reserved.'
    Description          = 'PowerShell provider that mounts execution trace files and crash dumps as PSDrives. Post-mortem inspection via the filesystem metaphor.'
    PowerShellVersion    = '7.4'
    CompatiblePSEditions = @('Core')

    CmdletsToExport      = @(
        'New-CrashDrive'
        'Invoke-CrashCapture'
        'Enable-CrashEditorFollow'
        'Disable-CrashEditorFollow'
    )
    FunctionsToExport    = @()
    AliasesToExport      = @()
    VariablesToExport    = @()

    FormatsToProcess     = @('CrashDrive.Format.ps1xml')

    PrivateData          = @{
        PSData = @{
            Tags       = @('Debug', 'Trace', 'CrashDump', 'Provider', 'PSDrive', 'Forensics')
            LicenseUri = 'https://github.com/yotsuda/CrashDrive/blob/main/LICENSE'
            ProjectUri = 'https://github.com/yotsuda/CrashDrive'
        }
    }
}
