using System.Management.Automation;

namespace CrashDrive.Provider;

/// <summary>
/// Dynamic parameters for <c>New-PSDrive -PSProvider (Trace|Dump|Ttd) ...</c>
/// and used transitively by <c>New-CrashDrive</c>.
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
