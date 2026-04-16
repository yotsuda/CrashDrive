using System.Globalization;
using System.Management.Automation;
using CrashDrive.Provider;

namespace CrashDrive.Cmdlets;

/// <summary>
/// Inspect local variables for a specific stack frame in a Dump or Ttd drive.
///
/// Uses dbgeng's data model: seeks (for TTD), switches thread, sets the frame
/// context, then reads <c>@$curframe.LocalVariables</c>. Quality of results
/// depends entirely on symbol availability — PDBs without local/param info
/// (typical for shipped Microsoft native modules) show nothing useful; for
/// source-built code with full PDBs, all names + types + values appear.
///
/// For managed frames, dbgeng only sees JIT-produced code, not the method
/// signature; SOS extension would be needed for proper managed locals, which
/// isn't wired up yet.
/// </summary>
[Cmdlet(VerbsCommon.Get, "CrashLocalVariable")]
[OutputType(typeof(string))]
public sealed class GetCrashLocalVariableCmdlet : PSCmdlet
{
    /// <summary>Thread ID. Hex form (0xa098 or a098) accepted. For Dump drives,
    /// pass the managed thread id (e.g. 20) and it's resolved to OS id automatically.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    public string ThreadId { get; set; } = "";

    /// <summary>Frame index (0 = innermost).</summary>
    [Parameter(Mandatory = true, Position = 1)]
    public int Frame { get; set; }

    [Parameter]
    public string? Drive { get; set; }

    /// <summary>TTD only: position to seek to. Accepts "8F_1DCE" path form,
    /// "8F:1DCE" native, or "start"/"end" aliases.</summary>
    [Parameter]
    public string? Position { get; set; }

    protected override void EndProcessing()
    {
        var drive = ResolveDrive();
        if (drive == null) return;

        // Build the dbgeng command. For reliable frame selection we emit:
        //   ~~[<tid>]s   — switch thread by OS id
        //   .frame <n>   — select frame
        //   dv /V /i /t  — locals with values, types, indirect addresses
        string cmd;
        if (drive is TtdDriveInfo ttd)
        {
            var osId = NormalizeHex(ThreadId);
            cmd = $"~~[0x{osId}]s;.frame 0n{Frame};dv /V /i /t";
            var native = Position != null ? DecodePosition(Position) : null;
            try
            {
                var output = ttd.Store.ExecuteDbgCommand(cmd, position: native);
                WriteObject(output);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "TtdLocalsFailed", ErrorCategory.NotSpecified, ThreadId));
            }
            return;
        }
        if (drive is DumpDriveInfo dmp)
        {
            // Accept either a hex OS id or a ClrMD managed thread id.
            var osId = ResolveDumpOsId(dmp);
            if (osId == null) return;
            cmd = $"~~[0x{osId:X}]s;.frame 0n{Frame};dv /V /i /t";
            try
            {
                var output = dmp.Store.ExecuteDbgCommand(cmd);
                WriteObject(output);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "DumpLocalsFailed", ErrorCategory.NotSpecified, ThreadId));
            }
            return;
        }
        WriteError(new ErrorRecord(
            new InvalidOperationException($"Drive '{drive.Name}' is not a CrashDrive Dump or Ttd drive."),
            "UnsupportedDrive", ErrorCategory.InvalidOperation, drive));
    }

    private uint? ResolveDumpOsId(DumpDriveInfo dmp)
    {
        var tidTrim = ThreadId.Trim();
        if (tidTrim.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(tidTrim[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                return hex;
        }
        // Try managed thread id lookup.
        if (int.TryParse(tidTrim, out var mgd))
        {
            var t = dmp.Store.Threads.FirstOrDefault(x => x.ManagedThreadId == mgd);
            if (t != null) return t.OSThreadId;
        }
        // Last resort: treat as hex OS id without 0x prefix.
        if (uint.TryParse(tidTrim, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h2))
            return h2;
        WriteError(new ErrorRecord(
            new ArgumentException($"Could not resolve thread id '{ThreadId}'."),
            "BadThreadId", ErrorCategory.InvalidArgument, ThreadId));
        return null;
    }

    private PSDriveInfo? ResolveDrive()
    {
        PSDriveInfo? d = !string.IsNullOrEmpty(Drive)
            ? SessionState.Drive.Get(Drive)
            : SessionState.Path.CurrentLocation.Drive;
        if (d == null)
        {
            WriteError(new ErrorRecord(
                new System.Management.Automation.DriveNotFoundException(Drive ?? "current"),
                "DriveNotFound", ErrorCategory.ObjectNotFound, Drive));
        }
        return d;
    }

    private static string NormalizeHex(string s)
    {
        s = s.Trim();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
    }

    private static string DecodePosition(string encoded)
    {
        if (encoded.Equals("start", StringComparison.OrdinalIgnoreCase)
            || encoded.Equals("end", StringComparison.OrdinalIgnoreCase))
            return encoded;
        return encoded.Replace('_', ':');
    }
}
