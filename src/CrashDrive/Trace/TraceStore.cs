using System.Text.Json;
using CrashDrive.Store;

namespace CrashDrive.Trace;

/// <summary>
/// Lazy JSONL trace loader. Constructor only stats the file — actual parsing
/// happens on first access to indexed properties. This keeps mount (New-CrashDrive)
/// fast even for GB-scale trace files; the heavy index build only fires when
/// the user actually navigates to events/, exceptions/, by-function/, etc.
/// </summary>
public sealed class TraceStore : IStore
{
    private readonly Lazy<IndexedData> _index;

    public string FilePath { get; }
    public long FileSizeBytes { get; }
    public DateTime LastWriteTime { get; }
    public StoreKind Kind => StoreKind.Trace;

    public void Dispose() { /* in-memory only, nothing to release */ }

    public TraceStore(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Trace file not found: {path}");

        FilePath = path;
        var info = new FileInfo(path);
        FileSizeBytes = info.Length;
        LastWriteTime = info.LastWriteTimeUtc;

        _index = new Lazy<IndexedData>(() => IndexedData.Build(path),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Whether the heavy index has already been built.</summary>
    public bool IsIndexed => _index.IsValueCreated;

    bool IStore.IsLoaded => _index.IsValueCreated;

    // All of the below force lazy evaluation.
    public IReadOnlyList<TraceEvent> Events => _index.Value.Events;
    public IReadOnlyDictionary<int, TraceEvent> BySeq => _index.Value.BySeq;
    public IReadOnlyDictionary<string, IReadOnlyList<TraceEvent>> ByType => _index.Value.ByType;
    public IReadOnlyDictionary<string, IReadOnlyList<TraceEvent>> ByFunction => _index.Value.ByFunction;
    public IReadOnlyList<ExceptionRecord> Exceptions => _index.Value.Exceptions;
    public TraceSummary Summary => _index.Value.Summary;

    /// <summary>
    /// Cheap summary not requiring full parse — useful for Get-CrashDrive listings.
    /// </summary>
    public CheapSummary CheapInfo => new()
    {
        FilePath = FilePath,
        FileSizeBytes = FileSizeBytes,
        LastWriteTime = LastWriteTime,
        IsLoaded = IsIndexed,
    };
}

public sealed class CheapSummary
{
    public string FilePath { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public DateTime LastWriteTime { get; set; }
    public bool IsLoaded { get; set; }
}

/// <summary>All the parsed/indexed data. Built once, cached behind the Lazy.</summary>
internal sealed class IndexedData
{
    public IReadOnlyList<TraceEvent> Events { get; init; } = [];
    public IReadOnlyDictionary<int, TraceEvent> BySeq { get; init; } = new Dictionary<int, TraceEvent>();
    public IReadOnlyDictionary<string, IReadOnlyList<TraceEvent>> ByType { get; init; }
        = new Dictionary<string, IReadOnlyList<TraceEvent>>();
    public IReadOnlyDictionary<string, IReadOnlyList<TraceEvent>> ByFunction { get; init; }
        = new Dictionary<string, IReadOnlyList<TraceEvent>>();
    public IReadOnlyList<ExceptionRecord> Exceptions { get; init; } = [];
    public TraceSummary Summary { get; init; } = new();

    public static IndexedData Build(string path)
    {
        var events = new List<TraceEvent>();
        using (var fs = File.OpenRead(path))
        using (var reader = new StreamReader(fs, System.Text.Encoding.UTF8))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var ev = JsonSerializer.Deserialize<TraceEvent>(line, TraceJson.Options);
                    if (ev != null) events.Add(ev);
                }
                catch (JsonException)
                {
                    // Skip malformed lines
                }
            }
        }

        var bySeq = events.ToDictionary(e => e.Seq);
        var byType = events
            .GroupBy(e => e.Type)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TraceEvent>)g.ToList());
        var byFunction = events
            .Where(e => !string.IsNullOrEmpty(e.Function))
            .GroupBy(e => e.Function!)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TraceEvent>)g.ToList());

        const int contextRadius = 10;
        var exceptions = new List<ExceptionRecord>();
        int exceptionIdx = 1;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Type != "exception") continue;
            var start = Math.Max(0, i - contextRadius);
            var end = Math.Min(events.Count, i + contextRadius + 1);
            var context = events.Skip(start).Take(end - start).ToList();
            exceptions.Add(new ExceptionRecord(exceptionIdx++, events[i], context));
        }

        var summary = new TraceSummary
        {
            FilePath = path,
            TotalEvents = events.Count,
            EventsByType = byType.ToDictionary(kv => kv.Key, kv => kv.Value.Count),
            ExceptionCount = exceptions.Count,
            UniqueFunctions = byFunction.Count,
            StartEvent = events.FirstOrDefault(e => e.Type == "trace_start"),
            EndEvent = events.FirstOrDefault(e => e.Type == "trace_end"),
        };

        return new IndexedData
        {
            Events = events,
            BySeq = bySeq,
            ByType = byType,
            ByFunction = byFunction,
            Exceptions = exceptions,
            Summary = summary,
        };
    }
}

/// <summary>Exception occurrence with surrounding context for post-mortem analysis.</summary>
public sealed record ExceptionRecord(
    int Index,
    TraceEvent Event,
    IReadOnlyList<TraceEvent> Context);

/// <summary>Aggregate stats for a trace file.</summary>
public sealed class TraceSummary
{
    public string FilePath { get; set; } = "";
    public int TotalEvents { get; set; }
    public Dictionary<string, int> EventsByType { get; set; } = new();
    public int ExceptionCount { get; set; }
    public int UniqueFunctions { get; set; }
    public TraceEvent? StartEvent { get; set; }
    public TraceEvent? EndEvent { get; set; }
}
