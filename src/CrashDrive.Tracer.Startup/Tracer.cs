namespace CrashDrive.Tracer.Startup;

/// <summary>
/// Singleton bootstrap for the tracer. Called from <c>StartupHook.Initialize</c>
/// once the target process's runtime is up but before user Main runs.
/// </summary>
internal static class Tracer
{
    internal static TraceEmitter? Emitter { get; private set; }

    public static void Start(string outputPath)
    {
        Emitter = new TraceEmitter(outputPath);

        var include = ParseGlob(Environment.GetEnvironmentVariable("CRASHDRIVE_TRACE_INCLUDE"));
        var exclude = ParseGlob(Environment.GetEnvironmentVariable("CRASHDRIVE_TRACE_EXCLUDE"));

        var patches = new HarmonyPatches(Emitter, include, exclude);
        patches.Apply();

        if (Environment.GetEnvironmentVariable("CRASHDRIVE_TRACE_DEBUG") == "1")
            Console.Error.WriteLine(patches.DebugReport());

        // Flush JSONL on process exit. trace_end is written here so the file
        // is well-formed even on clean termination.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Emitter?.Close(exitCode: 0);
    }

    private static string[]? ParseGlob(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw!
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
    }
}
