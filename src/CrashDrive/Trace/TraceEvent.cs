using System.Text.Json.Serialization;

namespace CrashDrive.Trace;

/// <summary>
/// A single event in an execution trace. All tracers emit events in this shape.
/// <see cref="Type"/> determines which optional fields are populated.
/// </summary>
public sealed class TraceEvent
{
    [JsonPropertyName("seq")]
    public int Seq { get; set; }

    /// <summary>One of: call, return, exception, output, trace_start, trace_end, fatal.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    // Common location fields
    [JsonPropertyName("file")]
    public string? File { get; set; }

    [JsonPropertyName("line")]
    public int? Line { get; set; }

    [JsonPropertyName("function")]
    public string? Function { get; set; }

    [JsonPropertyName("depth")]
    public int? Depth { get; set; }

    // call / exception
    [JsonPropertyName("locals")]
    public Dictionary<string, string>? Locals { get; set; }

    [JsonPropertyName("globals")]
    public Dictionary<string, string>? Globals { get; set; }

    [JsonPropertyName("watch")]
    public Dictionary<string, string>? Watch { get; set; }

    // return
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    // exception / fatal
    [JsonPropertyName("exception")]
    public string? Exception { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("traceback")]
    public string? Traceback { get; set; }

    // trace_start
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("python_version")]
    public string? PythonVersion { get; set; }

    [JsonPropertyName("events")]
    public string[]? EventsRequested { get; set; }

    // trace_end
    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; set; }
}

public static class TraceJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}
