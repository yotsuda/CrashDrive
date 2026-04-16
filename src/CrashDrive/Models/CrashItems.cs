using CrashDrive.Trace;

namespace CrashDrive.Models;

/// <summary>Folder entry shown in Get-ChildItem output.</summary>
public class FolderItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Directory { get; set; } = "";
    public string Description { get; set; } = "";
    public int? Count { get; set; }
}

/// <summary>File entry shown in Get-ChildItem output.</summary>
public class FileItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Directory { get; set; } = "";
    public long Length { get; set; }
    public string Preview { get; set; } = "";
}

/// <summary>
/// PowerShell-facing representation of a single TraceEvent.
/// Shown as a file in the PSDrive tree; file content (via Get-Content) is the JSON.
/// </summary>
public class EventItem
{
    public int Seq { get; set; }
    public string Type { get; set; } = "";
    public string? Function { get; set; }
    public int? Line { get; set; }
    public int? Depth { get; set; }
    public string? Summary { get; set; }   // one-line description
    public string Path { get; set; } = "";
    public string Directory { get; set; } = "";

    public static EventItem From(TraceEvent ev, string path, string directory)
    {
        return new EventItem
        {
            Seq = ev.Seq,
            Type = ev.Type,
            Function = ev.Function,
            Line = ev.Line,
            Depth = ev.Depth,
            Summary = SummaryFor(ev),
            Path = path,
            Directory = directory,
        };
    }

    private static string SummaryFor(TraceEvent ev) => ev.Type switch
    {
        "call" => $"{ev.Function}({LocalsShort(ev.Locals)})",
        "return" => $"{ev.Function} => {ev.Value}",
        "exception" => $"{ev.Exception}: {ev.Message}",
        "trace_start" => $"start target={ev.Target}",
        "trace_end" => $"end exit_code={ev.ExitCode}",
        _ => ev.Type,
    };

    private static string LocalsShort(Dictionary<string, string>? locals)
    {
        if (locals == null || locals.Count == 0) return "";
        var first = locals.Take(3).Select(kv => $"{kv.Key}={kv.Value}");
        var s = string.Join(", ", first);
        if (locals.Count > 3) s += $", ... (+{locals.Count - 3})";
        return s;
    }
}

/// <summary>Top-level exception summary shown in exceptions/ folder.</summary>
public class ExceptionItem
{
    public int Index { get; set; }
    public int Seq { get; set; }
    public string ExceptionType { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Function { get; set; }
    public int? Line { get; set; }
    public string Path { get; set; } = "";
    public string Directory { get; set; } = "";
}
