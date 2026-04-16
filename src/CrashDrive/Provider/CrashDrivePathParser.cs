namespace CrashDrive.Provider;

/// <summary>
/// Path grammar for CrashDrive provider.
///
/// <code>
/// &lt;drive&gt;:\
///   summary.json
///   stdout.txt
///   stderr.txt
///   events\
///     &lt;seq&gt;.json
///   by-type\
///     &lt;type&gt;\
///       &lt;seq&gt;.json
///   by-function\
///     &lt;function&gt;\
///       &lt;seq&gt;.json
///   exceptions\
///     &lt;index&gt;\
///       event.json
///       context.json
///       stack.txt
/// </code>
/// </summary>
public enum CrashPathType
{
    Root,

    // Top-level files
    SummaryFile,
    StdoutFile,
    StderrFile,

    // Top-level folders
    EventsFolder,
    ByTypeFolder,
    ByFunctionFolder,
    ExceptionsFolder,

    // Events
    EventFile,                  // events\<seq>.json

    // By type
    ByTypeCategoryFolder,       // by-type\<type>
    ByTypeEventFile,            // by-type\<type>\<seq>.json

    // By function
    ByFunctionCategoryFolder,   // by-function\<function>
    ByFunctionEventFile,        // by-function\<function>\<seq>.json

    // Exceptions
    ExceptionFolder,            // exceptions\<index>
    ExceptionEventFile,         // exceptions\<index>\event.json
    ExceptionContextFile,       // exceptions\<index>\context.json
    ExceptionStackFile,         // exceptions\<index>\stack.txt

    // TTD-specific
    TtdEventsFolder,            // ttd-events\
    TtdEventFile,               // ttd-events\<index>.json
    TtdTimelineFile,            // timeline.json

    // Dump-specific
    ThreadsFolder,              // threads\
    ThreadFolder,               // threads\<id>
    ThreadInfoFile,             // threads\<id>\info.json
    ThreadStackFile,            // threads\<id>\stack.txt
    ThreadFramesFolder,         // threads\<id>\frames
    FrameFile,                  // threads\<id>\frames\<n>.json
    ModulesFolder,              // modules\
    ModuleFile,                 // modules\<filename>.json
    DumpInfoFile,               // info.json (dump-side summary separate from summary.json)

    Invalid,
}

public sealed class CrashPathInfo
{
    public CrashPathType Type { get; init; }
    public string[] Segments { get; init; } = [];

    public int? Seq { get; init; }
    public string? Category { get; init; }     // for ByType/ByFunction category name
    public int? ExceptionIndex { get; init; }
    public int? ThreadId { get; init; }
    public int? FrameIndex { get; init; }
    public string? ModuleFile { get; init; }

    public bool IsContainer => Type switch
    {
        CrashPathType.Root => true,
        CrashPathType.EventsFolder => true,
        CrashPathType.ByTypeFolder => true,
        CrashPathType.ByFunctionFolder => true,
        CrashPathType.ExceptionsFolder => true,
        CrashPathType.ByTypeCategoryFolder => true,
        CrashPathType.ByFunctionCategoryFolder => true,
        CrashPathType.ExceptionFolder => true,
        CrashPathType.ThreadsFolder => true,
        CrashPathType.ThreadFolder => true,
        CrashPathType.ThreadFramesFolder => true,
        CrashPathType.ModulesFolder => true,
        CrashPathType.TtdEventsFolder => true,
        _ => false,
    };
}

public static class CrashPathParser
{
    public static CrashPathInfo Parse(string path)
    {
        var segments = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            return new CrashPathInfo { Type = CrashPathType.Root };

        var head = segments[0].ToLowerInvariant();

        // Top-level files
        if (segments.Length == 1)
        {
            return head switch
            {
                "summary.json" => new CrashPathInfo { Type = CrashPathType.SummaryFile, Segments = segments },
                "info.json" => new CrashPathInfo { Type = CrashPathType.DumpInfoFile, Segments = segments },
                "stdout.txt" => new CrashPathInfo { Type = CrashPathType.StdoutFile, Segments = segments },
                "stderr.txt" => new CrashPathInfo { Type = CrashPathType.StderrFile, Segments = segments },
                "events" => new CrashPathInfo { Type = CrashPathType.EventsFolder, Segments = segments },
                "by-type" => new CrashPathInfo { Type = CrashPathType.ByTypeFolder, Segments = segments },
                "by-function" => new CrashPathInfo { Type = CrashPathType.ByFunctionFolder, Segments = segments },
                "exceptions" => new CrashPathInfo { Type = CrashPathType.ExceptionsFolder, Segments = segments },
                "threads" => new CrashPathInfo { Type = CrashPathType.ThreadsFolder, Segments = segments },
                "modules" => new CrashPathInfo { Type = CrashPathType.ModulesFolder, Segments = segments },
                "ttd-events" => new CrashPathInfo { Type = CrashPathType.TtdEventsFolder, Segments = segments },
                "timeline.json" => new CrashPathInfo { Type = CrashPathType.TtdTimelineFile, Segments = segments },
                _ => new CrashPathInfo { Type = CrashPathType.Invalid, Segments = segments },
            };
        }

        return head switch
        {
            "events" => ParseEvents(segments),
            "by-type" => ParseByType(segments),
            "by-function" => ParseByFunction(segments),
            "exceptions" => ParseExceptions(segments),
            "threads" => ParseThreads(segments),
            "modules" => ParseModules(segments),
            "ttd-events" => ParseTtdEvents(segments),
            _ => new CrashPathInfo { Type = CrashPathType.Invalid, Segments = segments },
        };
    }

    private static CrashPathInfo ParseThreads(string[] segments)
    {
        // threads\<id>[\info.json | stack.txt | frames[\<n>.json]]
        if (segments.Length < 2) return Invalid(segments);
        if (!int.TryParse(segments[1], out var tid)) return Invalid(segments);

        if (segments.Length == 2)
            return new() { Type = CrashPathType.ThreadFolder, Segments = segments, ThreadId = tid };

        var sub = segments[2].ToLowerInvariant();
        if (segments.Length == 3)
        {
            return sub switch
            {
                "info.json" => new() { Type = CrashPathType.ThreadInfoFile, Segments = segments, ThreadId = tid },
                "stack.txt" => new() { Type = CrashPathType.ThreadStackFile, Segments = segments, ThreadId = tid },
                "frames" => new() { Type = CrashPathType.ThreadFramesFolder, Segments = segments, ThreadId = tid },
                _ => Invalid(segments),
            };
        }
        if (segments.Length == 4 && sub == "frames" && TryParseSeqFile(segments[3], out var frameN))
        {
            return new() { Type = CrashPathType.FrameFile, Segments = segments, ThreadId = tid, FrameIndex = frameN };
        }
        return Invalid(segments);
    }

    private static CrashPathInfo ParseTtdEvents(string[] segments)
    {
        if (segments.Length != 2) return Invalid(segments);
        if (!TryParseSeqFile(segments[1], out var seq)) return Invalid(segments);
        return new() { Type = CrashPathType.TtdEventFile, Segments = segments, Seq = seq };
    }

    private static CrashPathInfo ParseModules(string[] segments)
    {
        // modules\<filename>.json
        if (segments.Length != 2) return Invalid(segments);
        if (!segments[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return Invalid(segments);
        return new() { Type = CrashPathType.ModuleFile, Segments = segments, ModuleFile = segments[1][..^5] };
    }

    private static CrashPathInfo ParseEvents(string[] segments)
    {
        // events\<seq>.json
        if (segments.Length != 2) return Invalid(segments);
        if (!TryParseSeqFile(segments[1], out var seq)) return Invalid(segments);
        return new CrashPathInfo
        {
            Type = CrashPathType.EventFile,
            Segments = segments,
            Seq = seq,
        };
    }

    private static CrashPathInfo ParseByType(string[] segments)
    {
        // by-type\<type>[\<seq>.json]
        if (segments.Length == 2)
        {
            return new CrashPathInfo
            {
                Type = CrashPathType.ByTypeCategoryFolder,
                Segments = segments,
                Category = segments[1],
            };
        }
        if (segments.Length == 3 && TryParseSeqFile(segments[2], out var seq))
        {
            return new CrashPathInfo
            {
                Type = CrashPathType.ByTypeEventFile,
                Segments = segments,
                Category = segments[1],
                Seq = seq,
            };
        }
        return Invalid(segments);
    }

    private static CrashPathInfo ParseByFunction(string[] segments)
    {
        // by-function\<function>[\<seq>.json]
        if (segments.Length == 2)
        {
            return new CrashPathInfo
            {
                Type = CrashPathType.ByFunctionCategoryFolder,
                Segments = segments,
                Category = segments[1],
            };
        }
        if (segments.Length == 3 && TryParseSeqFile(segments[2], out var seq))
        {
            return new CrashPathInfo
            {
                Type = CrashPathType.ByFunctionEventFile,
                Segments = segments,
                Category = segments[1],
                Seq = seq,
            };
        }
        return Invalid(segments);
    }

    private static CrashPathInfo ParseExceptions(string[] segments)
    {
        // exceptions\<index>\{event.json | context.json | stack.txt}
        if (segments.Length == 2)
        {
            if (!int.TryParse(segments[1], out var idx)) return Invalid(segments);
            return new CrashPathInfo
            {
                Type = CrashPathType.ExceptionFolder,
                Segments = segments,
                ExceptionIndex = idx,
            };
        }
        if (segments.Length == 3)
        {
            if (!int.TryParse(segments[1], out var idx)) return Invalid(segments);
            var leaf = segments[2].ToLowerInvariant();
            var type = leaf switch
            {
                "event.json" => CrashPathType.ExceptionEventFile,
                "context.json" => CrashPathType.ExceptionContextFile,
                "stack.txt" => CrashPathType.ExceptionStackFile,
                _ => CrashPathType.Invalid,
            };
            return new CrashPathInfo
            {
                Type = type,
                Segments = segments,
                ExceptionIndex = idx,
            };
        }
        return Invalid(segments);
    }

    private static bool TryParseSeqFile(string segment, out int seq)
    {
        seq = 0;
        if (!segment.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
        var numStr = segment[..^5];
        return int.TryParse(numStr, out seq);
    }

    private static CrashPathInfo Invalid(string[] segments) =>
        new() { Type = CrashPathType.Invalid, Segments = segments };
}
