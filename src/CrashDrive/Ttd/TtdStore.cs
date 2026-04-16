using CrashDrive.DbgEng;
using CrashDrive.Store;

namespace CrashDrive.Ttd;

/// <summary>
/// Opens a Time Travel Debugging (.run) trace via DbgEng + Data Model queries.
/// Everything is lazy — mount just opens the session and waits for the
/// initial event; event/thread/position data is fetched on demand via "dx".
/// </summary>
public sealed class TtdStore : IStore
{
    private readonly Lazy<DbgEngSession> _session;
    private readonly Lazy<IReadOnlyList<TtdEvent>> _events;
    private readonly Lazy<TtdSummary> _summary;
    private readonly string? _symbolPath;

    public string FilePath { get; }
    public long FileSizeBytes { get; }
    public DateTime LastWriteTime { get; }
    public StoreKind Kind => StoreKind.Ttd;

    public bool IsLoaded => _session.IsValueCreated;

    public TtdStore(string path, string? symbolPath = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"TTD trace file not found: {path}");

        FilePath = path;
        _symbolPath = symbolPath;
        var info = new FileInfo(path);
        FileSizeBytes = info.Length;
        LastWriteTime = info.LastWriteTimeUtc;

        _session = new Lazy<DbgEngSession>(() => DbgEngSession.Open(path, symbolPath),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _events = new Lazy<IReadOnlyList<TtdEvent>>(LoadEvents,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _summary = new Lazy<TtdSummary>(LoadSummary,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public DbgEngSession Session
    {
        get
        {
            var s = _session.Value;
            // TTD Data Model extension must be loaded before Events/Lifetime bind.
            EnsureTtdAnalyzeLoaded(s);
            return s;
        }
    }

    public IReadOnlyList<TtdEvent> Events => _events.Value;
    public TtdSummary Summary => _summary.Value;

    private static readonly object s_loadLock = new();
    private static readonly HashSet<DbgEngSession> s_loadedSessions = new();

    private static void EnsureTtdAnalyzeLoaded(DbgEngSession session)
    {
        lock (s_loadLock)
        {
            if (s_loadedSessions.Contains(session)) return;
            try { session.Execute(".load ttd\\TTDAnalyze"); } catch { }
            s_loadedSessions.Add(session);
        }
    }

    private IReadOnlyList<TtdEvent> LoadEvents()
    {
        // Get count first, then query each event individually. This avoids the
        // brittle "dx -g" grid-output parsing and works uniformly for any count.
        int count;
        try { count = ParseInt(ExtractScalar(Session.Dx("@$curprocess.TTD.Events.Count()"))); }
        catch { return []; }

        var list = new List<TtdEvent>(count);
        for (int i = 0; i < count; i++)
        {
            var ev = new TtdEvent();
            try
            {
                ev.Position = ExtractScalar(Session.Dx($"@$curprocess.TTD.Events[{i}].Position"));
                ev.Type = ExtractScalar(Session.Dx($"@$curprocess.TTD.Events[{i}].Type"));
                // Module field may not exist on all event types — tolerate failure.
                try { ev.Module = ExtractScalar(Session.Dx($"@$curprocess.TTD.Events[{i}].Module")); } catch { }
            }
            catch { /* skip malformed event */ }
            list.Add(ev);
        }
        return list;
    }

    private TtdSummary LoadSummary()
    {
        var summary = new TtdSummary
        {
            FilePath = FilePath,
            FileSizeBytes = FileSizeBytes,
        };
        try { summary.LifetimeStart = ExtractScalar(Session.Dx("@$curprocess.TTD.Lifetime.MinPosition")); } catch { }
        try { summary.LifetimeEnd = ExtractScalar(Session.Dx("@$curprocess.TTD.Lifetime.MaxPosition")); } catch { }
        try { summary.ThreadCount = ParseInt(ExtractScalar(Session.Dx("@$curprocess.Threads.Count()"))); } catch { }
        try { summary.ModuleCount = ParseInt(ExtractScalar(Session.Dx("@$curprocess.Modules.Count()"))); } catch { }
        try { summary.EventCount = ParseInt(ExtractScalar(Session.Dx("@$curprocess.TTD.Events.Count()"))); } catch { }
        return summary;
    }

    public void Dispose()
    {
        if (_session.IsValueCreated)
        {
            try { _session.Value.Dispose(); } catch { }
        }
    }

    // ─── dx output parsing helpers ─────────────────────────────────

    /// <summary>
    /// Extract the scalar value from output like "expr : value".
    /// </summary>
    private static string ExtractScalar(string dxOutput)
    {
        if (string.IsNullOrWhiteSpace(dxOutput)) return "";
        // dx single-value output: "<expr> : <value>"
        var colonIdx = dxOutput.IndexOf(':');
        if (colonIdx > 0 && colonIdx + 1 < dxOutput.Length)
            return dxOutput[(colonIdx + 1)..].Trim();
        return dxOutput.Trim();
    }

    private static int ParseInt(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.Parse(s[2..], System.Globalization.NumberStyles.HexNumber);
        return int.TryParse(s, out var v) ? v : 0;
    }

    /// <summary>
    /// Parse the output of <c>dx -g</c> into rows. Grid output looks like:
    /// <code>
    /// =======================================================
    /// =            = (+) Pos      = (+) Type = (+) Module    =
    /// =======================================================
    /// = [0x0]      - "12:0"       - CreateThread - ntdll.dll  =
    /// = [0x1]      - ...
    /// </code>
    /// </summary>
    private static List<TtdEvent> ParseGridOutput(string text)
    {
        var results = new List<TtdEvent>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        string[]? headers = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith("===") || line.StartsWith("---")) continue;

            // Column rows use '=' boundaries; header looks like:
            //   = [...] = (+) Col1 = (+) Col2 = ...
            // Data rows use '-' or '=' as separators, inconsistent.
            // Split on '=' OR '-' and trim.
            var parts = line.Split('=', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
            if (parts.Length == 0) continue;

            if (headers == null)
            {
                // First real content line is the header row — strip the "(+)" markers.
                headers = parts.Select(p => p.TrimStart('+', ' ', '(', ')').Trim()).ToArray();
                continue;
            }

            // Data row: drop the leading "[index]" column.
            if (parts.Length < headers.Length) continue;
            var ev = new TtdEvent();
            // Find the column values - skip any parts that look like "[0xN]"
            var dataCols = parts.Where(p => !(p.StartsWith("[") && p.EndsWith("]"))).ToArray();
            if (dataCols.Length == 0) continue;

            // Best-effort: map by header name
            for (int i = 0; i < dataCols.Length && i < headers.Length - 1; i++)
            {
                var header = headers[i + 1]; // skip "[index]"
                var val = dataCols[i].Trim('-', ' ', '"');
                switch (header)
                {
                    case "Pos": ev.Position = val; break;
                    case "Type": ev.Type = val; break;
                    case "Module": ev.Module = val; break;
                }
            }
            if (!string.IsNullOrEmpty(ev.Position) || !string.IsNullOrEmpty(ev.Type))
                results.Add(ev);
        }
        return results;
    }
}

public sealed class TtdEvent
{
    public string Position { get; set; } = "";
    public string Type { get; set; } = "";
    public string Module { get; set; } = "";
}

public sealed class TtdSummary
{
    public string FilePath { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string LifetimeStart { get; set; } = "";
    public string LifetimeEnd { get; set; } = "";
    public int ThreadCount { get; set; }
    public int ModuleCount { get; set; }
    public int EventCount { get; set; }
}
