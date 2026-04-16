using System.Globalization;
using System.Management.Automation;
using CrashDrive.Dump;
using CrashDrive.Provider;
using CrashDrive.Ttd;

namespace CrashDrive.Cmdlets;

/// <summary>
/// Read bytes at a memory address in a Dump or TTD drive and render them as
/// hex / ASCII / Unicode / DWORDs / QWORDs / pointer chain.
///
/// For TTD, the position to observe is specified by -Position (encoded path
/// form, e.g. "8F_1DCE" or "start"); omitting it uses whatever position the
/// session was last seeked to (typically Lifetime end).
/// </summary>
[Cmdlet(VerbsCommunications.Read, "CrashMemory")]
[OutputType(typeof(string))]
public sealed class ReadCrashMemoryCmdlet : PSCmdlet
{
    /// <summary>Drive name (e.g. "dump" or "ttd"). If omitted, inferred from the
    /// current PS location.</summary>
    [Parameter(Position = 0)]
    public string? Drive { get; set; }

    /// <summary>TTD only: position to seek to before reading. Accepts native
    /// "8F:1DCE" or path-encoded "8F_1DCE", or the aliases "start" / "end".</summary>
    [Parameter]
    public string? Position { get; set; }

    /// <summary>Address to read. Accepts hex (0x...) or decimal.</summary>
    [Parameter(Mandatory = true, Position = 1)]
    public string Address { get; set; } = "";

    /// <summary>How many bytes to read. Default 128.</summary>
    [Parameter]
    public int Length { get; set; } = 128;

    /// <summary>Output format. Default Hex.</summary>
    [Parameter]
    [ValidateSet("Hex", "Ascii", "Unicode", "Dword", "Qword", "Pointers")]
    public string Format { get; set; } = "Hex";

    protected override void EndProcessing()
    {
        var addr = ParseAddress(Address);
        if (addr == null)
        {
            WriteError(new ErrorRecord(
                new ArgumentException($"Invalid address: {Address}"),
                "BadAddress", ErrorCategory.InvalidArgument, Address));
            return;
        }
        var drive = ResolveDrive();
        if (drive == null) return;

        var cmd = BuildCommand(addr.Value);
        try
        {
            string output;
            if (drive is TtdDriveInfo ttd)
            {
                var nativePos = Position != null ? DecodePosition(Position) : null;
                output = ttd.Store.ExecuteDbgCommand(cmd, position: nativePos);
            }
            else if (drive is DumpDriveInfo dmp)
            {
                output = dmp.Store.ExecuteDbgCommand(cmd);
            }
            else
            {
                WriteError(new ErrorRecord(
                    new InvalidOperationException($"Drive '{drive.Name}' is not a CrashDrive Dump or Ttd drive."),
                    "UnsupportedDrive", ErrorCategory.InvalidOperation, drive));
                return;
            }
            WriteObject(output);
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(ex, "ReadFailed", ErrorCategory.NotSpecified, Address));
        }
    }

    private string BuildCommand(ulong addr)
    {
        // dbgeng supports decimal count via "L0n<decimal>" or hex via "L<hex>".
        var count = $"L0n{Length}";
        return Format switch
        {
            "Hex"      => $"db 0x{addr:X} {count}",
            "Ascii"    => $"da 0x{addr:X} {count}",
            "Unicode"  => $"du 0x{addr:X} {count}",
            "Dword"    => $"dd 0x{addr:X} {count}",
            "Qword"    => $"dq 0x{addr:X} {count}",
            "Pointers" => $"dps 0x{addr:X} {count}",
            _          => $"db 0x{addr:X} {count}",
        };
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

    private static ulong? ParseAddress(string s)
    {
        s = s.Trim().Replace("`", "").Replace("_", "");
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return hex;
        if (ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
            return dec;
        return null;
    }

    private static string DecodePosition(string encoded)
    {
        // Path-form uses '_' as the major:minor separator (e.g. "8F_1DCE"); also
        // accept "start" / "end" aliases, which the TtdStore resolver handles.
        if (encoded.Equals("start", StringComparison.OrdinalIgnoreCase)
            || encoded.Equals("end", StringComparison.OrdinalIgnoreCase))
            return encoded;
        return encoded.Replace('_', ':');
    }
}
