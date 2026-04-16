using System.Management.Automation;

namespace CrashDrive.Provider;

/// <summary>
/// Dynamic parameters for <c>New-PSDrive -PSProvider CrashDrive ...</c>.
/// Exposed via the <c>New-CrashDrive</c> convenience cmdlet and directly on
/// <c>New-PSDrive</c>.
/// </summary>
public sealed class NewCrashDriveDynamicParameters
{
    [Parameter(Mandatory = true)]
    [Alias("Path")]
    [ValidateNotNullOrEmpty]
    public string File { get; set; } = "";

    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? SymbolPath { get; set; }
}
