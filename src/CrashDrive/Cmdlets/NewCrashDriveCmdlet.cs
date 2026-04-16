using System.Management.Automation;
using CrashDrive.Store;

namespace CrashDrive.Cmdlets;

/// <summary>
/// Mount a post-mortem data file (execution trace, crash dump, or TTD trace)
/// as a PSDrive. Auto-detects the file kind and delegates to the appropriate
/// provider (<c>Trace</c>, <c>Dump</c>, or <c>Ttd</c>).
/// </summary>
[Cmdlet(VerbsCommon.New, "CrashDrive")]
public sealed class NewCrashDriveCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string Name { get; set; } = "";

    [Parameter(Mandatory = true, Position = 1)]
    [Alias("Path")]
    [ValidateNotNullOrEmpty]
    public string File { get; set; } = "";

    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? SymbolPath { get; set; }

    [Parameter]
    public SwitchParameter PassThru { get; set; }

    protected override void ProcessRecord()
    {
        var resolved = GetUnresolvedProviderPathFromPSPath(File);
        if (!System.IO.File.Exists(resolved))
        {
            WriteError(new ErrorRecord(
                new System.IO.FileNotFoundException($"File not found: {resolved}"),
                "FileNotFound", ErrorCategory.ObjectNotFound, resolved));
            return;
        }

        var kind = StoreFactory.DetectKind(resolved);
        var providerName = kind switch
        {
            StoreKind.Trace => "Trace",
            StoreKind.Dump => "Dump",
            StoreKind.Ttd => "Ttd",
            _ => throw new NotSupportedException($"Unknown file kind: {kind}"),
        };

        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        var cmd = ps.AddCommand("New-PSDrive")
            .AddParameter("PSProvider", providerName)
            .AddParameter("Name", Name)
            .AddParameter("Root", @"\")
            .AddParameter("File", resolved)
            .AddParameter("Scope", "Global");

        if (SymbolPath != null) cmd.AddParameter("SymbolPath", SymbolPath);

        var results = ps.Invoke();
        if (ps.HadErrors)
        {
            foreach (var err in ps.Streams.Error) WriteError(err);
            return;
        }
        if (PassThru.IsPresent)
            foreach (var r in results) WriteObject(r);
    }
}
