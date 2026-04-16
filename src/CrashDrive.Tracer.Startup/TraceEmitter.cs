using System.Text.Json;

namespace CrashDrive.Tracer.Startup;

/// <summary>
/// Writes CrashDrive-compatible JSONL to a file. Thread-safe writes.
/// One file per process run. Schema matches <c>CrashDrive.Trace.TraceEvent</c>
/// so the Trace provider can mount the output directly.
/// </summary>
internal sealed class TraceEmitter
{
    private readonly StreamWriter _writer;
    private readonly object _writeLock = new();
    // Per-thread call depth (propagates across async boundaries via AsyncLocal).
    private readonly AsyncLocal<int> _depth = new();
    // Recursion guard so the emitter doesn't observe itself when patched
    // BCL methods happen to be called during serialization.
    [ThreadStatic] private static bool s_inEmit;

    private long _seq;
    private bool _closed;

    public TraceEmitter(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        _writer = new StreamWriter(new FileStream(
            outputPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            // Flush after every line. The ProcessExit handler closes cleanly,
            // but a crash (access violation, abort) bypasses it — without
            // AutoFlush the buffered JSONL would be lost on abnormal exit.
            AutoFlush = true,
        };

        WriteRaw(new Dictionary<string, object?>
        {
            ["seq"] = NextSeq(),
            ["type"] = "trace_start",
            ["target"] = Environment.GetCommandLineArgs().FirstOrDefault() ?? "",
            ["dotnet_version"] = Environment.Version.ToString(),
            ["events"] = new[] { "call", "return", "exception" },
        });
    }

    public bool IsSuppressed => s_inEmit;

    public void EmitCall(string file, int line, string function,
        Dictionary<string, string>? locals)
    {
        if (s_inEmit) return;
        var depth = _depth.Value;
        WriteRaw(new Dictionary<string, object?>
        {
            ["seq"] = NextSeq(),
            ["type"] = "call",
            ["file"] = file,
            ["line"] = line,
            ["function"] = function,
            ["depth"] = depth,
            ["locals"] = locals,
        });
        _depth.Value = depth + 1;
    }

    public void EmitReturn(string file, int line, string function, string? value)
    {
        if (s_inEmit) return;
        var depth = Math.Max(0, _depth.Value - 1);
        _depth.Value = depth;
        WriteRaw(new Dictionary<string, object?>
        {
            ["seq"] = NextSeq(),
            ["type"] = "return",
            ["file"] = file,
            ["line"] = line,
            ["function"] = function,
            ["depth"] = depth,
            ["value"] = value,
        });
    }

    public void EmitException(string file, int line, string function, Exception ex)
    {
        if (s_inEmit) return;
        var depth = Math.Max(0, _depth.Value - 1);
        _depth.Value = depth;
        WriteRaw(new Dictionary<string, object?>
        {
            ["seq"] = NextSeq(),
            ["type"] = "exception",
            ["file"] = file,
            ["line"] = line,
            ["function"] = function,
            ["depth"] = depth,
            ["exception"] = ex.GetType().FullName,
            ["message"] = ex.Message,
            ["traceback"] = ex.StackTrace,
        });
    }

    public void Close(int exitCode)
    {
        // Emit trace_end BEFORE marking closed, otherwise WriteRaw's
        // early-return on _closed swallows our own final record.
        lock (_writeLock)
        {
            if (_closed) return;
        }
        WriteRaw(new Dictionary<string, object?>
        {
            ["seq"] = NextSeq(),
            ["type"] = "trace_end",
            ["exit_code"] = exitCode,
        });
        lock (_writeLock)
        {
            _closed = true;
            try { _writer.Flush(); _writer.Dispose(); } catch { }
        }
    }

    private long NextSeq() => Interlocked.Increment(ref _seq);

    private void WriteRaw(Dictionary<string, object?> payload)
    {
        s_inEmit = true;
        try
        {
            var json = JsonSerializer.Serialize(payload);
            lock (_writeLock)
            {
                if (_closed) return;
                _writer.WriteLine(json);
            }
        }
        catch
        {
            // Swallow: an emit failure must not escape into user code. The
            // resulting JSONL may be truncated but the target keeps running.
        }
        finally
        {
            s_inEmit = false;
        }
    }
}
