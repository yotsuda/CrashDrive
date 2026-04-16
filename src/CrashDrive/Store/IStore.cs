namespace CrashDrive.Store;

/// <summary>
/// What kind of post-mortem data a store represents.
/// </summary>
public enum StoreKind
{
    /// <summary>JSONL execution trace from a language tracer (Python sys.monitoring, etc.)</summary>
    Trace,

    /// <summary>Windows minidump / .NET crash dump (parsed via ClrMD).</summary>
    Dump,
}

/// <summary>
/// Common metadata for any file CrashDrive can mount. Heavy data access
/// happens through kind-specific downcasts in the provider.
/// </summary>
public interface IStore : IDisposable
{
    string FilePath { get; }
    long FileSizeBytes { get; }
    DateTime LastWriteTime { get; }
    bool IsLoaded { get; }
    StoreKind Kind { get; }
}
