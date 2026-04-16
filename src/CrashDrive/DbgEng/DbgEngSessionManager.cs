namespace CrashDrive.DbgEng;

/// <summary>
/// Process-wide owner of the one live DbgEng session.
///
/// <para>
/// dbgeng binds a target to the *process*, not to any individual IDebugClient.
/// Creating two IDebugClients and calling OpenDumpFile on each doesn't give you
/// two independent targets — the second call quietly repoints global state. When
/// CrashDrive mounts both a Dump and a Ttd drive, whichever one opens second
/// steals the other's session (bare "|" on the Dump session reveals the TTD
/// target, etc).
/// </para>
///
/// <para>
/// The fix is structural: keep at most one live <see cref="DbgEngSession"/>;
/// if a caller needs a different file, dispose the current session and open
/// the new one, bumping a generation counter so callers can invalidate any
/// state that was bound to the previous session (TTD seek position, loaded
/// extensions, etc).
/// </para>
///
/// <para>
/// All access is serialized via a monitor taken by <see cref="AcquireFor"/> and
/// released by <see cref="Lease.Dispose"/>. This also serializes dbgeng
/// commands across drives, which is correct: dbgeng is single-threaded anyway.
/// The monitor is reentrant, so a caller holding a lease can call through
/// helpers that also acquire without deadlocking.
/// </para>
/// </summary>
public static class DbgEngSessionManager
{
    private static readonly object _lock = new();
    private static DbgEngSession? _current;
    private static string? _currentSymbolPath;
    private static long _generation;

    /// <summary>Generation counter, bumped every time a new session is opened.
    /// Callers that cache session-bound state should snapshot this alongside
    /// the state and invalidate on mismatch.</summary>
    public static long Generation
    {
        get { lock (_lock) return _generation; }
    }

    /// <summary>Acquire the session bound to <paramref name="filePath"/>.
    /// Holds the manager's monitor until the returned lease is disposed.
    /// If the live session is for a different file (or different sympath), it
    /// is torn down and replaced.</summary>
    public static Lease AcquireFor(string filePath, string? symbolPath = null)
    {
        Monitor.Enter(_lock);
        try
        {
            if (_current != null
                && string.Equals(_current.FilePath, filePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_currentSymbolPath, symbolPath, StringComparison.Ordinal))
            {
                return new Lease(_current, _generation);
            }

            if (_current != null)
            {
                try { _current.Dispose(); } catch { }
                _current = null;
            }

            _current = DbgEngSession.Open(filePath, symbolPath);
            _currentSymbolPath = symbolPath;
            unchecked { _generation++; }
            return new Lease(_current, _generation);
        }
        catch
        {
            Monitor.Exit(_lock);
            throw;
        }
    }

    /// <summary>Tear down the live session if it is bound to
    /// <paramref name="filePath"/>. Intended as a cleanup hook from store
    /// Dispose so the last drive removal releases the file.</summary>
    public static void CloseIf(string filePath)
    {
        lock (_lock)
        {
            if (_current == null) return;
            if (!string.Equals(_current.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                return;
            try { _current.Dispose(); } catch { }
            _current = null;
            _currentSymbolPath = null;
            unchecked { _generation++; }
        }
    }

    public sealed class Lease : IDisposable
    {
        private bool _disposed;

        public DbgEngSession Session { get; }
        public long Generation { get; }

        internal Lease(DbgEngSession session, long generation)
        {
            Session = session;
            Generation = generation;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Monitor.Exit(_lock);
        }
    }
}
