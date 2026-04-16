using System.Management.Automation;
using CrashDrive.Provider;

namespace CrashDrive.Cmdlets;

/// <summary>
/// Convenience wrapper around <c>New-PSDrive -PSProvider CrashDrive</c>.
/// Mount a trace file or crash dump as a new PSDrive.
/// </summary>
[Cmdlet(VerbsCommon.New, "CrashDrive")]
[OutputType(typeof(CrashDriveInfo))]
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

        // Build the command: New-PSDrive -PSProvider CrashDrive -Name X -Root \ -File Y [-SymbolPath Z]
        // We invoke via the runtime so dynamic parameters are honored.
        var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        try
        {
            var cmd = ps.AddCommand("New-PSDrive")
                .AddParameter("PSProvider", "CrashDrive")
                .AddParameter("Name", Name)
                .AddParameter("Root", @"\")
                .AddParameter("File", resolved)
                .AddParameter("Scope", "Global");

            if (SymbolPath != null) cmd.AddParameter("SymbolPath", SymbolPath);

            var results = ps.Invoke();
            if (ps.HadErrors)
            {
                foreach (var err in ps.Streams.Error)
                    WriteError(err);
                return;
            }

            if (PassThru.IsPresent)
            {
                foreach (var r in results)
                    WriteObject(r);
            }
        }
        finally
        {
            ps.Dispose();
        }
    }
}
