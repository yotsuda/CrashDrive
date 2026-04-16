using System.Management.Automation;
using CrashDrive.Provider;

namespace CrashDrive.Cmdlets;

/// <summary>
/// Run an arbitrary dbgeng command against a Dump or Ttd drive and emit the
/// captured text output. Shares the drive's session via
/// <c>DbgEngSessionManager.AcquireFor</c>, so commands run in the same engine
/// instance the provider uses for path resolution — no duplicate target load,
/// no cross-drive steal.
///
/// Paths first: reach for this cmdlet when a dbgeng extension (<c>!locks</c>,
/// <c>!syncblk</c>, a custom <c>.ecxr</c> workflow, etc.) cannot reasonably be
/// expressed as a filesystem path. If a query pattern recurs often enough to
/// justify paths, promote it to the provider.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "CrashCommand")]
[OutputType(typeof(string))]
public sealed class InvokeCrashCommandCmdlet : PSCmdlet
{
    /// <summary>The dbgeng command to execute (e.g. <c>!analyze -v</c>,
    /// <c>lm</c>, <c>dx @$cursession.TTD.Events.Count()</c>).</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    public string Command { get; set; } = "";

    /// <summary>Drive name. If omitted, inferred from the current PS location.</summary>
    [Parameter]
    public string? Drive { get; set; }

    /// <summary>TTD only: position to seek to before running the command.
    /// Accepts native <c>major:minor</c>, path-form <c>major_minor</c>, or the
    /// aliases <c>start</c> / <c>end</c>. Ignored on Dump drives.</summary>
    [Parameter]
    public string? Position { get; set; }

    /// <summary>TTD only: thread to switch to (<c>~~[0x&lt;tid&gt;]s</c>) before
    /// running the command. Hex with or without <c>0x</c> prefix.</summary>
    [Parameter]
    public string? ThreadId { get; set; }

    protected override void ProcessRecord()
    {
        if (string.IsNullOrWhiteSpace(Command))
        {
            WriteError(new ErrorRecord(
                new ArgumentException("Command is empty."),
                "EmptyCommand", ErrorCategory.InvalidArgument, Command));
            return;
        }

        var drive = ResolveDrive();
        if (drive == null) return;

        try
        {
            if (drive is TtdDriveInfo ttd)
            {
                var native = Position != null ? DecodePosition(Position) : null;
                var output = ttd.Store.ExecuteDbgCommand(Command, position: native, threadId: ThreadId);
                WriteObject(output);
                return;
            }
            if (drive is DumpDriveInfo dmp)
            {
                if (Position != null)
                    WriteWarning("Position is ignored on Dump drives.");
                if (ThreadId != null)
                    WriteWarning("ThreadId is ignored on Dump drives; prefix the command with '~~[0x<tid>]s;' if you need a thread switch.");
                var output = dmp.Store.ExecuteDbgCommand(Command);
                WriteObject(output);
                return;
            }
            WriteError(new ErrorRecord(
                new InvalidOperationException(
                    $"Drive '{drive.Name}' is not a CrashDrive Dump or Ttd drive. " +
                    "Invoke-CrashCommand needs a dbgeng-backed session."),
                "UnsupportedDrive", ErrorCategory.InvalidOperation, drive));
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "InvokeFailed", ErrorCategory.NotSpecified, Command));
        }
    }

    private PSDriveInfo? ResolveDrive()
    {
        if (!string.IsNullOrEmpty(Drive))
        {
            var d = SessionState.Drive.Get(Drive);
            if (d == null)
            {
                WriteError(new ErrorRecord(
                    new System.Management.Automation.DriveNotFoundException(Drive),
                    "DriveNotFound", ErrorCategory.ObjectNotFound, Drive));
                return null;
            }
            return d;
        }
        return SessionState.Path.CurrentLocation.Drive;
    }

    private static string DecodePosition(string encoded)
    {
        if (encoded.Equals("start", StringComparison.OrdinalIgnoreCase)
            || encoded.Equals("end", StringComparison.OrdinalIgnoreCase))
            return encoded;
        return encoded.Replace('_', ':');
    }
}
