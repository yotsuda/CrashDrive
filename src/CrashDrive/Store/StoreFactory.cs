using CrashDrive.Dump;
using CrashDrive.Trace;

namespace CrashDrive.Store;

/// <summary>
/// Opens a file as the appropriate <see cref="IStore"/>. Decides via
/// extension first, then magic-number sniffing as a fallback.
/// </summary>
public static class StoreFactory
{
    /// <summary>
    /// <para>MDMP</para> — 4-byte signature of a Windows minidump file.
    /// </summary>
    private static readonly byte[] s_minidumpMagic = [0x4D, 0x44, 0x4D, 0x50];

    public static IStore Open(string path, string? symbolPath = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"File not found: {path}");

        var kind = DetectKind(path);
        return kind switch
        {
            StoreKind.Trace => new TraceStore(path),
            StoreKind.Dump => new DumpStore(path, symbolPath),
            _ => throw new NotSupportedException($"Unsupported file kind: {kind}"),
        };
    }

    public static StoreKind DetectKind(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // Fast-path by extension
        if (ext is ".jsonl" or ".json" or ".ndjson") return StoreKind.Trace;
        if (ext is ".dmp" or ".mdmp" or ".hdmp") return StoreKind.Dump;

        // Fallback: peek at magic bytes
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            var read = fs.Read(header);
            if (read >= 4 && header.SequenceEqual(s_minidumpMagic))
                return StoreKind.Dump;
        }
        catch
        {
            // fall through to trace assumption
        }

        // Default: assume trace (JSONL is the most permissive format)
        return StoreKind.Trace;
    }
}
