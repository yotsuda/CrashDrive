using System.Runtime.InteropServices;

namespace CrashDrive.DbgEng;

/// <summary>
/// A managed wrapper around a DbgEng debug session. Opens a dump or TTD trace
/// file, waits for the initial event, and allows executing commands (notably
/// Debugger Data Model "dx" queries) with output captured as strings.
///
/// <para>
/// This is the low-level substrate both for advanced dump inspection (v0.2+)
/// and for TTD support (v0.3).
/// </para>
/// </summary>
public sealed class DbgEngSession : IDisposable
{
    private readonly IDebugClient5 _client;
    private readonly IDebugControl7 _control;
    private readonly OutputCollector _output;
    private volatile bool _disposed;

    public string FilePath { get; }

    private DbgEngSession(IDebugClient5 client, IDebugControl7 control,
        OutputCollector output, string path)
    {
        _client = client;
        _control = control;
        _output = output;
        FilePath = path;
    }

    /// <summary>
    /// Open a dump (.dmp) or TTD trace (.run) file.
    /// Blocks until the initial event arrives (usually very fast for dumps,
    /// slightly slower for TTD traces while the engine maps the recording).
    /// </summary>
    public static DbgEngSession Open(string path, string? symbolPath = null)
    {
        // If opening a TTD trace, pre-load the WinDbg Preview dbgeng.dll
        // which supports TTD. The system32 dbgeng.dll does not.
        var isTtd = path.EndsWith(".run", StringComparison.OrdinalIgnoreCase);
        // Always prefer WinDbg Preview's dbgeng when available: it's required for
        // TTD, and for .dmp it brings the full extension set (ext.dll with !analyze
        // etc.) that system32's dbgeng ships without. Failure is only fatal for TTD.
        var haveWinDbg = DbgEngNative.TryLoadWinDbgDbgEng(out var loadDiag);
        if (isTtd && !haveWinDbg)
            throw new InvalidOperationException(
                "Opening .run (TTD) files requires WinDbg Preview (install via 'winget install Microsoft.WinDbg'). " +
                "Set CRASHDRIVE_DBGENG_DIR to override detection. Details: " + loadDiag);

        // Create the client as IDebugClient5 directly.
        var iid = DbgEngIIDs.IDebugClient5;
        var rawClient = DbgEngNative.DebugCreate(in iid);
        var client = (IDebugClient5)rawClient;

        // Wire up our output collector BEFORE doing anything else so engine
        // init messages and command output both flow through it.
        var collector = new OutputCollector();
        HResult.ThrowOnFailure(
            client.SetOutputCallbacks(collector),
            "SetOutputCallbacks");

        // Get the control interface on the same object.
        var control = (IDebugControl7)client;

        // Try OpenDumpFileWide first — the TTD-capable (WinDbg Preview) dbgeng.dll
        // accepts .run files directly. Fall back to ".opendump /ttd" command if that
        // returns E_INVALIDARG (older dbgeng that doesn't know about TTD via the
        // direct API but handles it via the command parser).
        var openHr = client.OpenDumpFileWide(path, 0);
        if (openHr < 0 && isTtd)
        {
            collector.Reset();
            var openCmd = $".opendump /ttd \"{path}\"";
            var hr = control.Execute(DebugOutctl.ThisClient, openCmd, DebugExecute.NotLogged);
            if (hr < 0)
            {
                var diag = collector.Drain();
                throw new COMException(
                    $"Neither OpenDumpFileWide (0x{openHr:X8}) nor '{openCmd}' (0x{hr:X8}) succeeded. " +
                    $"Engine output:\n{diag}",
                    hr);
            }
        }
        else if (openHr < 0)
        {
            HResult.ThrowOnFailure(openHr, $"OpenDumpFileWide({path})");
        }

        // Apply symbol path if explicit. Without one, keep dbgeng's default —
        // adding a sympath with a remote (msdl) component at session init makes
        // WaitForEvent and module-list bootstrap trigger network lookups, which
        // can block for minutes on a 150-module dump on the first run.
        if (!string.IsNullOrEmpty(symbolPath))
            control.Execute(DebugOutctl.ThisClient, $".sympath {symbolPath}", DebugExecute.NotLogged);

        // Wait for the engine to process the file and surface the initial event.
        HResult.ThrowOnFailure(
            control.WaitForEvent(DebugWait.Default, 60_000),
            "WaitForEvent");

        var session = new DbgEngSession(client, control, collector, path);
        collector.Reset();
        return session;
    }

    /// <summary>
    /// Execute a single DbgEng command (e.g. <c>dx @$cursession.TTD.Events</c>,
    /// <c>!analyze -v</c>, <c>lm</c>) and return the captured text output.
    /// </summary>
    public string Execute(string command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _output.Reset();
        var hr = _control.Execute(DebugOutctl.ThisClient, command, DebugExecute.NotLogged | DebugExecute.NoRepeat);
        HResult.ThrowOnFailure(hr, $"Execute({command})");
        return _output.Drain();
    }

    /// <summary>
    /// Evaluate a Data Model expression and return its rendered text.
    /// Equivalent to <c>dx -r0 &lt;expr&gt;</c> (no recursion — top-level only).
    /// </summary>
    public string Dx(string expression, int recursion = 0)
    {
        return Execute($"dx -r{recursion} {expression}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _client.SetOutputCallbacks(null); } catch { }
        try { _client.EndSession(DebugEnd.Active_Detach); } catch { }
        try { Marshal.FinalReleaseComObject(_control); } catch { }
        try { Marshal.FinalReleaseComObject(_client); } catch { }
    }
}

/// <summary>Captures text emitted by <see cref="IDebugControl7.Execute"/>.</summary>
internal sealed class OutputCollector : IDebugOutputCallbacks
{
    private readonly System.Text.StringBuilder _sb = new();
    private readonly object _lock = new();

    public int Output(DebugOutput Mask, string Text)
    {
        if (Text == null) return 0;
        lock (_lock) _sb.Append(Text);
        return 0;
    }

    public void Reset() { lock (_lock) _sb.Clear(); }
    public string Drain()
    {
        lock (_lock)
        {
            var s = _sb.ToString();
            _sb.Clear();
            return s;
        }
    }
}

internal static class HResult
{
    public static void ThrowOnFailure(int hr, string op)
    {
        if (hr < 0)
            throw new System.Runtime.InteropServices.COMException(
                $"DbgEng {op} failed (HRESULT 0x{hr:X8})", hr);
    }
}
