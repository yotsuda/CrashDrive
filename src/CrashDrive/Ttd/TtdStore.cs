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
    private readonly Lazy<IReadOnlyList<TtdEvent>> _events;
    private readonly Lazy<TtdSummary> _summary;
    private readonly string? _symbolPath;

    // Tracks which manager generation we have state tied to. -1 means never
    // acquired. When the generation changes we know the live session was
    // swapped out and back: cached position is gone, TTDAnalyze isn't loaded
    // on the new session. See DbgEngSessionManager for the swap rationale.
    private long _dbgGeneration = -1;
    private bool _ttdAnalyzeLoaded;
    private bool _everLoaded;

    public string FilePath { get; }
    public long FileSizeBytes { get; }
    public DateTime LastWriteTime { get; }
    public StoreKind Kind => StoreKind.Ttd;

    public bool IsLoaded => _everLoaded;

    public TtdStore(string path, string? symbolPath = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"TTD trace file not found: {path}");

        FilePath = path;
        _symbolPath = symbolPath;
        var info = new FileInfo(path);
        FileSizeBytes = info.Length;
        LastWriteTime = info.LastWriteTimeUtc;

        _events = new Lazy<IReadOnlyList<TtdEvent>>(LoadEvents,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _summary = new Lazy<TtdSummary>(LoadSummary,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Acquire the shared dbgeng session for the duration of
    /// <paramref name="body"/>. Before calling the body, invalidates
    /// per-generation state (seek position) and re-loads TTDAnalyze if the
    /// session was swapped since we last ran. The manager's monitor is
    /// reentrant, so <paramref name="body"/> may itself call public methods
    /// that acquire again without deadlocking.</summary>
    private T WithSession<T>(Func<DbgEngSession, T> body)
    {
        using var lease = DbgEngSessionManager.AcquireFor(FilePath, _symbolPath);
        if (_dbgGeneration != lease.Generation)
        {
            _dbgGeneration = lease.Generation;
            _currentPosition = null;
            _ttdAnalyzeLoaded = false;
        }
        if (!_ttdAnalyzeLoaded)
        {
            try { lease.Session.Execute(".load ttd\\TTDAnalyze"); } catch { }
            _ttdAnalyzeLoaded = true;
        }
        _everLoaded = true;
        return body(lease.Session);
    }

    public IReadOnlyList<TtdEvent> Events => _events.Value;
    public TtdSummary Summary => _summary.Value;

    // ─── Event views (derived, cached) ───────────────────────────────
    //
    // Timeline/ folder wants reordered + filtered event slices. Each carries
    // the original event index so users can still cross-reference ttd-events.

    private IReadOnlyList<TtdEventWithIndex>? _eventsByPosition;
    private IReadOnlyList<TtdEventWithIndex>? _exceptionEvents;
    private IReadOnlyList<TtdEventWithIndex>? _significantEvents;
    private readonly object _eventViewLock = new();

    public IReadOnlyList<TtdEventWithIndex> EventsByPosition
    {
        get
        {
            if (_eventsByPosition != null) return _eventsByPosition;
            lock (_eventViewLock)
            {
                return _eventsByPosition ??= Events
                    .Select((e, i) => new TtdEventWithIndex(i, e))
                    .OrderBy(x => x.Event.Position, PositionComparer.Instance)
                    .ToList();
            }
        }
    }

    public IReadOnlyList<TtdEventWithIndex> ExceptionEvents
    {
        get
        {
            if (_exceptionEvents != null) return _exceptionEvents;
            lock (_eventViewLock)
            {
                return _exceptionEvents ??= EventsByPosition
                    .Where(x => x.Event.Type.StartsWith("Exception", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }
    }

    public IReadOnlyList<TtdEventWithIndex> SignificantEvents
    {
        get
        {
            if (_significantEvents != null) return _significantEvents;
            lock (_eventViewLock)
            {
                return _significantEvents ??= EventsByPosition
                    .Where(x => IsSignificant(x.Event.Type))
                    .ToList();
            }
        }
    }

    private static bool IsSignificant(string type) =>
        type.Equals("ModuleLoaded", StringComparison.OrdinalIgnoreCase)
        || type.Equals("ModuleUnloaded", StringComparison.OrdinalIgnoreCase)
        || type.Equals("ThreadCreated", StringComparison.OrdinalIgnoreCase)
        || type.Equals("ThreadTerminated", StringComparison.OrdinalIgnoreCase);

    // ─── Position navigation ───────────────────────────────────────
    //
    // DbgEng maintains a single "current position" per session. Cache it so
    // we only issue !tt when moving. Callers pass native "major:minor" strings.
    // Invalidated automatically by WithSession on generation change.

    private string? _currentPosition;

    /// <summary>Navigate to a specific TTD position (native "major:minor" string).</summary>
    public void SeekTo(string position) => WithSession(session =>
    {
        SeekToUnderLease(session, position);
        return 0;
    });

    private void SeekToUnderLease(DbgEngSession session, string position)
    {
        if (_currentPosition == position) return;
        session.Execute($"!tt {position}");
        _currentPosition = position;
    }

    /// <summary>Run an arbitrary dbgeng command, optionally after seeking to a
    /// position and/or switching to a thread. Intended for raw debugger access
    /// from cmdlets (db/u/x/dx/!dumpobj etc.). Position may be "start"/"end"
    /// aliases or a native major:minor string.</summary>
    public string ExecuteDbgCommand(string command, string? position = null, string? threadId = null)
        => WithSession(session =>
        {
            if (position != null)
            {
                var native = position switch
                {
                    "start" => Summary.LifetimeStart,
                    "end" => Summary.LifetimeEnd,
                    _ => position,
                };
                SeekToUnderLease(session, native);
            }
            if (threadId != null)
            {
                var tid = threadId.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? threadId[2..] : threadId;
                return session.Execute($"~~[0x{tid}]s;{command}");
            }
            return session.Execute(command);
        });

    /// <summary>Dump register state for the given thread at the given TTD position.
    /// Seeks to the position, switches to the thread, and runs `r`.</summary>
    public string RenderRegistersAtPosition(string position, string threadId) => WithSession(session =>
    {
        try
        {
            SeekToUnderLease(session, position);
            // TTD thread ids are hex like "0xa098"; dbgeng's ~~[ID]s selector
            // takes a hex OS thread id. Strip the 0x prefix.
            var tid = threadId.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? threadId[2..] : threadId;
            return session.Execute($"~~[0x{tid}]s;r");
        }
        catch (Exception ex)
        {
            return $"Failed to read registers at position {position} for thread {threadId}: " +
                $"{ex.GetType().Name}: {ex.Message}\n";
        }
    });

    /// <summary>
    /// Return threads visible at the current (last-seeked) position.
    /// Note: the Threads data-model collection is keyed by thread ID, not a
    /// numeric index. We enumerate via .Select() and then fetch details per ID.
    /// </summary>
    public IReadOnlyList<TtdThreadAtPosition> GetThreadsAtCurrentPosition() => WithSession(session =>
    {
        var ids = ParseIdList(session.Dx("@$curprocess.Threads.Select(t => t.Id)", recursion: 1));

        var result = new List<TtdThreadAtPosition>();
        foreach (var id in ids)
        {
            int frameCount = 0;
            try { frameCount = ParseInt(ExtractScalar(session.Dx($"@$curprocess.Threads[{id}].Stack.Frames.Count()"))); }
            catch { }
            result.Add(new TtdThreadAtPosition { Id = id, FrameCount = frameCount });
        }
        return (IReadOnlyList<TtdThreadAtPosition>)result;
    });

    /// <summary>
    /// Query memory access history in an address range.
    /// <paramref name="mode"/> is "r", "w", or "rw".
    /// </summary>
    public IReadOnlyList<TtdMemoryAccess> GetMemoryAccesses(
        string startAddrHex, string endAddrHex, string mode, int maxRecords = 200) => WithSession(session =>
    {
        // dx parses unprefixed numeric literals as decimal. Path-form addresses
        // are conventionally hex ("1a373fa3d50_1a373fa3e50"), so a missing 0x
        // used to silently return zero records. Normalize here.
        startAddrHex = EnsureHexPrefix(startAddrHex);
        endAddrHex = EnsureHexPrefix(endAddrHex);
        var result = new List<TtdMemoryAccess>();
        var baseExpr = $"@$cursession.TTD.Memory({startAddrHex}, {endAddrHex}, \"{mode}\")";

        // Single dx call using Select() projection — orders of magnitude faster
        // than querying each field individually.
        var proj = $"{baseExpr}.Take({maxRecords}).Select(m => new {{ " +
                   "T = m.ThreadId, P = m.TimeStart, K = m.AccessType, " +
                   "A = m.Address, S = m.Size, V = m.Value, O = m.OverwrittenValue, I = m.IP })";

        string text;
        try { text = session.Dx(proj, recursion: 2); }
        catch { return (IReadOnlyList<TtdMemoryAccess>)result; }

        foreach (var record in ParseProjectedRecords(text))
        {
            result.Add(new TtdMemoryAccess
            {
                Index = result.Count,
                ThreadId = record.GetValueOrDefault("T", ""),
                TimeStart = record.GetValueOrDefault("P", ""),
                AccessType = record.GetValueOrDefault("K", ""),
                Address = record.GetValueOrDefault("A", ""),
                Size = record.GetValueOrDefault("S", ""),
                Value = record.GetValueOrDefault("V", ""),
                OverwrittenValue = record.GetValueOrDefault("O", ""),
                IP = record.GetValueOrDefault("I", ""),
            });
        }
        return (IReadOnlyList<TtdMemoryAccess>)result;
    });

    // ─── Single-record convenience picks ─────────────────────────────
    //
    // "first write" / "last write before pos" are the reverse-lookup primitives
    // that make TTD worth its weight: "where was this pointer set?" / "what
    // was the last thing that stomped on this field before the crash?" Both
    // are thin wrappers over GetMemoryAccesses — we pull a larger window and
    // pick client-side by TimeStart using the position comparer.

    private const int PickMaxRecords = 10_000;

    public TtdMemoryAccess? GetFirstWrite(string startAddrHex, string endAddrHex)
    {
        var recs = GetMemoryAccesses(startAddrHex, endAddrHex, "w", PickMaxRecords);
        return recs.Count > 0 ? recs[0] : null;
    }

    public TtdMemoryAccess? GetLastWriteBefore(string startAddrHex, string endAddrHex, string position)
    {
        var recs = GetMemoryAccesses(startAddrHex, endAddrHex, "w", PickMaxRecords);
        TtdMemoryAccess? best = null;
        foreach (var r in recs)
        {
            if (PositionComparer.Instance.Compare(r.TimeStart, position) >= 0) break;
            best = r;
        }
        return best;
    }

    /// <summary>
    /// Parse "dx -r2 expr.Select(...)" output where each element is a block of
    /// <c>    [0xN]</c> followed by indented <c>        Field : Value</c> lines.
    /// </summary>
    private static IEnumerable<Dictionary<string, string>> ParseProjectedRecords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        Dictionary<string, string>? current = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            // New record: "    [0xN]" or "    [...]"
            if (line.StartsWith("    [") && !line.StartsWith("        "))
            {
                if (current != null) yield return current;
                current = new Dictionary<string, string>();
                continue;
            }

            if (current == null) continue;

            // Field line: "        Field            : Value"
            if (!line.StartsWith("        ")) continue;
            var trimmed = line.TrimStart();
            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx <= 0) continue;
            var key = trimmed[..colonIdx].Trim();
            var value = trimmed[(colonIdx + 1)..].Trim();
            if (!string.IsNullOrEmpty(key) && !current.ContainsKey(key))
                current[key] = value;
        }
        if (current != null) yield return current;
    }

    /// <summary>
    /// Execute TTD.Calls("module!function") and return structured call records.
    /// Each record spans a time range (entry → exit) and carries argument registers
    /// plus the return value.
    /// </summary>
    public IReadOnlyList<TtdCall> GetCalls(string module, string function, int maxRecords = 500) => WithSession(session =>
    {
        var result = new List<TtdCall>();
        var functionFullName = $"{module}!{function}";
        var baseExpr = $"@$cursession.TTD.Calls(\"{functionFullName}\")";

        // Projection gives all core fields in one dx call.
        var proj = $"{baseExpr}.Take({maxRecords}).Select(c => new {{ " +
                   "T = c.ThreadId, S = c.TimeStart, E = c.TimeEnd, " +
                   "FA = c.FunctionAddress, RA = c.ReturnAddress, RV = c.ReturnValue })";

        string text;
        try { text = session.Dx(proj, recursion: 2); }
        catch { return (IReadOnlyList<TtdCall>)result; }

        var idx = 0;
        foreach (var record in ParseProjectedRecords(text))
        {
            var call = new TtdCall
            {
                Index = idx++,
                Function = functionFullName,
                ThreadId = record.GetValueOrDefault("T", ""),
                TimeStart = record.GetValueOrDefault("S", ""),
                TimeEnd = record.GetValueOrDefault("E", ""),
                FunctionAddress = record.GetValueOrDefault("FA", ""),
                ReturnAddress = record.GetValueOrDefault("RA", ""),
                ReturnValue = record.GetValueOrDefault("RV", ""),
            };
            // Fetch parameters lazily only for the requested index (when file is read)
            // For folder enumeration we skip — avoids N×M queries.
            result.Add(call);
        }
        return (IReadOnlyList<TtdCall>)result;
    });

    /// <summary>Fetch the 4 register parameters for a specific call record.</summary>
    public IReadOnlyList<string> GetCallParameters(string module, string function, int index) => WithSession(session =>
    {
        var functionFullName = $"{module}!{function}";
        try
        {
            return ParseIdList(session.Dx(
                $"@$cursession.TTD.Calls(\"{functionFullName}\")[{index}].Parameters.Select(p => p)",
                recursion: 1));
        }
        catch { return (IReadOnlyList<string>)Array.Empty<string>(); }
    });

    /// <summary>Return stack frames for a thread id at the current position.</summary>
    public IReadOnlyList<TtdFrame> GetFramesAtCurrentPosition(string threadId) => WithSession(session =>
    {
        var result = new List<TtdFrame>();
        int frameCount;
        try { frameCount = ParseInt(ExtractScalar(session.Dx($"@$curprocess.Threads[{threadId}].Stack.Frames.Count()"))); }
        catch { return (IReadOnlyList<TtdFrame>)result; }

        for (int i = 0; i < Math.Min(frameCount, 128); i++)
        {
            try
            {
                var text = ExtractScalar(session.Dx($"@$curprocess.Threads[{threadId}].Stack.Frames[{i}]"));
                ulong ip = 0;
                try
                {
                    var ipText = ExtractScalar(session.Dx($"@$curprocess.Threads[{threadId}].Stack.Frames[{i}].Attributes.InstructionOffset"));
                    ip = ParseUlong(ipText);
                }
                catch { }
                result.Add(new TtdFrame { Index = i, Description = text, InstructionPointer = ip });
            }
            catch { }
        }
        return (IReadOnlyList<TtdFrame>)result;
    });

    // ─── Source location lookup ──────────────────────────────────────
    //
    // Same contract as DumpStore.GetSourceLocation — routed through the
    // shared dbgeng session. TTD recordings are immutable like dumps, so
    // caching by IP is safe across session swaps.

    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, CrashDrive.Dump.SourceLocation?>
        _sourceCache = new();

    public CrashDrive.Dump.SourceLocation? GetSourceLocation(ulong ip)
    {
        if (ip == 0) return null;
        if (_sourceCache.TryGetValue(ip, out var cached)) return cached;
        CrashDrive.Dump.SourceLocation? result = null;
        try
        {
            result = WithSession(session =>
            {
                var hit = session.GetSourceLocation(ip);
                return hit is { } v ? new CrashDrive.Dump.SourceLocation(v.File, v.Line) : null;
            });
        }
        catch { }
        _sourceCache[ip] = result;
        return result;
    }

    private static ulong ParseUlong(string s)
    {
        s = s.Trim().Replace("`", "").Replace("_", "");
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.Parse(s[2..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
        return ulong.TryParse(s, out var v) ? v : 0;
    }

    private static string EnsureHexPrefix(string addr) =>
        addr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? addr : "0x" + addr;

    /// <summary>
    /// Parse the multi-line "dx .Select(t => t.Id)" output, extracting the
    /// right-hand side value of each "    [key] : value" line.
    /// </summary>
    private static IReadOnlyList<string> ParseIdList(string dxOutput)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(dxOutput)) return ids;
        foreach (var rawLine in dxOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            // Child rows are typically indented; they look like:
            //   "    [0xa098]         : 0xa098"
            if (!line.StartsWith("    ")) continue;
            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0) continue;
            var value = line[(colonIdx + 1)..].Trim();
            if (!string.IsNullOrEmpty(value) && !value.StartsWith("Error"))
                ids.Add(value);
        }
        return ids;
    }

    private IReadOnlyList<TtdEvent> LoadEvents() => WithSession(session =>
    {
        // Get count first, then query each event individually. This avoids the
        // brittle "dx -g" grid-output parsing and works uniformly for any count.
        int count;
        try { count = ParseInt(ExtractScalar(session.Dx("@$curprocess.TTD.Events.Count()"))); }
        catch { return (IReadOnlyList<TtdEvent>)Array.Empty<TtdEvent>(); }

        var list = new List<TtdEvent>(count);
        for (int i = 0; i < count; i++)
        {
            var ev = new TtdEvent();
            try
            {
                ev.Position = ExtractScalar(session.Dx($"@$curprocess.TTD.Events[{i}].Position"));
                ev.Type = ExtractScalar(session.Dx($"@$curprocess.TTD.Events[{i}].Type"));
                // Module field may not exist on all event types — tolerate failure.
                try { ev.Module = ExtractScalar(session.Dx($"@$curprocess.TTD.Events[{i}].Module")); } catch { }
            }
            catch { /* skip malformed event */ }
            list.Add(ev);
        }
        return (IReadOnlyList<TtdEvent>)list;
    });

    private TtdSummary LoadSummary() => WithSession(session =>
    {
        var summary = new TtdSummary
        {
            FilePath = FilePath,
            FileSizeBytes = FileSizeBytes,
        };
        try { summary.LifetimeStart = ExtractScalar(session.Dx("@$curprocess.TTD.Lifetime.MinPosition")); } catch { }
        try { summary.LifetimeEnd = ExtractScalar(session.Dx("@$curprocess.TTD.Lifetime.MaxPosition")); } catch { }
        try { summary.ThreadCount = ParseInt(ExtractScalar(session.Dx("@$curprocess.Threads.Count()"))); } catch { }
        try { summary.ModuleCount = ParseInt(ExtractScalar(session.Dx("@$curprocess.Modules.Count()"))); } catch { }
        try { summary.EventCount = ParseInt(ExtractScalar(session.Dx("@$curprocess.TTD.Events.Count()"))); } catch { }
        return summary;
    });

    public void Dispose()
    {
        DbgEngSessionManager.CloseIf(FilePath);
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

/// <summary>Pairs a <see cref="TtdEvent"/> with its index in the original
/// <see cref="TtdStore.Events"/> list. Timeline views reorder/filter the
/// underlying list but retain this back-pointer so users can cross-reference
/// with ttd-events\.</summary>
public readonly record struct TtdEventWithIndex(int OriginalIndex, TtdEvent Event);

/// <summary>Orders "major:minor" hex position strings numerically (not
/// lexically — "1CBF" must compare before "8F" only by value of the major
/// part). Handles "start"/"end" aliases by treating them as the extremes.</summary>
public sealed class PositionComparer : IComparer<string>
{
    public static readonly PositionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x == y) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        var (xMaj, xMin) = Parse(x);
        var (yMaj, yMin) = Parse(y);
        var c = xMaj.CompareTo(yMaj);
        return c != 0 ? c : xMin.CompareTo(yMin);
    }

    private static (ulong Major, ulong Minor) Parse(string s)
    {
        if (string.IsNullOrEmpty(s)) return (0, 0);
        var colon = s.IndexOf(':');
        if (colon < 0) return (ParseHex(s), 0);
        return (ParseHex(s[..colon]), ParseHex(s[(colon + 1)..]));
    }

    private static ulong ParseHex(string s)
    {
        s = s.Trim();
        return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}

public sealed class TtdThreadAtPosition
{
    public string Id { get; set; } = "";
    public int FrameCount { get; set; }
}

public sealed class TtdFrame
{
    public int Index { get; set; }
    public string Description { get; set; } = "";
    public ulong InstructionPointer { get; set; }
}

public sealed class TtdMemoryAccess
{
    public int Index { get; set; }
    public string ThreadId { get; set; } = "";
    public string TimeStart { get; set; } = "";
    public string AccessType { get; set; } = "";
    public string Address { get; set; } = "";
    public string Size { get; set; } = "";
    public string Value { get; set; } = "";
    public string OverwrittenValue { get; set; } = "";
    public string IP { get; set; } = "";
}

public sealed class TtdCall
{
    public int Index { get; set; }
    public string Function { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string TimeStart { get; set; } = "";
    public string TimeEnd { get; set; } = "";
    public string FunctionAddress { get; set; } = "";
    public string ReturnAddress { get; set; } = "";
    public string ReturnValue { get; set; } = "";
    public IReadOnlyList<string> Parameters { get; set; } = [];
}

/// <summary>PS path-safe encoding of a TTD position. "1CBF:8C1" ↔ "1CBF_8C1".</summary>
public static class TtdPosition
{
    public static string Encode(string nativePosition) => nativePosition.Replace(':', '_');
    public static string Decode(string encodedPosition) => encodedPosition.Replace('_', ':');

    public static bool IsValid(string encodedPosition)
    {
        // Accept "<hex>_<hex>" or "start"/"end" symbolic names.
        if (encodedPosition is "start" or "end") return true;
        var idx = encodedPosition.IndexOf('_');
        if (idx <= 0 || idx >= encodedPosition.Length - 1) return false;
        return IsHex(encodedPosition[..idx]) && IsHex(encodedPosition[(idx + 1)..]);
    }

    private static bool IsHex(string s)
    {
        if (s.Length == 0) return false;
        foreach (var ch in s)
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F')))
                return false;
        return true;
    }
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
