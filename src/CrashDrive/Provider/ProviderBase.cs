using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Provider;

namespace CrashDrive.Provider;

/// <summary>
/// Shared infrastructure for the Trace / Dump / Ttd providers. Each concrete
/// provider handles its own hierarchy and store opening; the base provides
/// path normalization, content reader plumbing, and write helpers.
/// </summary>
public abstract class ProviderBase : NavigationCmdletProvider, IContentCmdletProvider
{
    // === Path normalization (shared) ===

    protected override string MakePath(string parent, string child)
    {
        var result = base.MakePath(parent, child);
        if (result.EndsWith('\\') && result.Length > 1 && result[^2] != ':')
            result = result[..^1];
        return result;
    }

    protected override string NormalizeRelativePath(string path, string basePath)
    {
        var result = base.NormalizeRelativePath(path, basePath);
        if (result.StartsWith('\\') && result.Length > 1)
            result = result[1..];
        return result;
    }

    protected string NormalizePath(string path)
    {
        var colonIdx = path.IndexOf(':');
        if (colonIdx > 0 && colonIdx + 1 < path.Length && path[colonIdx + 1] is '\\' or '/')
            path = path[(colonIdx + 1)..];
        return path.Replace('\\', '/').Trim('/');
    }

    protected string EnsureDrivePrefix(string path)
    {
        if (PSDriveInfo == null) return path;
        var prefix = $"{PSDriveInfo.Name}:";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path : prefix + path;
    }

    protected override bool IsValidPath(string path) => true;

    // === Content reader (read-only; returns plain text for any file node) ===

    public IContentReader GetContentReader(string path)
        => new StringContentReader(GetFileText(NormalizePath(path)));

    public object? GetContentReaderDynamicParameters(string path) => null;
    public IContentWriter GetContentWriter(string path) => throw new NotSupportedException("Read-only provider.");
    public object? GetContentWriterDynamicParameters(string path) => null;
    public void ClearContent(string path) => throw new NotSupportedException("Read-only provider.");
    public object? ClearContentDynamicParameters(string path) => null;

    /// <summary>
    /// Subclass returns the text content of a file path. Return empty string
    /// if the path is not a file or doesn't exist.
    /// </summary>
    protected abstract string GetFileText(string normalizedPath);

    protected sealed class StringContentReader : IContentReader
    {
        private readonly string _text;
        private bool _read;

        public StringContentReader(string text) => _text = text;

        public IList Read(long readCount)
        {
            if (_read) return new System.Collections.ArrayList();
            _read = true;
            return new System.Collections.ArrayList { _text };
        }

        public void Seek(long offset, SeekOrigin origin) { /* no-op */ }
        public void Close() { }
        public void Dispose() { }
    }
}
