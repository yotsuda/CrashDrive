using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace CrashDrive.Tracer.Startup;

/// <summary>
/// Walks loaded assemblies, picks the user-authored ones (filtered by
/// include/exclude), and patches every concrete method with prefix
/// (call), postfix (return), and finalizer (exception) hooks.
///
/// Harmony handles the hard parts of IL weaving (async state machines,
/// generics, value types) — we just wire up the callbacks.
/// </summary>
internal sealed class HarmonyPatches
{
    private static TraceEmitter? s_emitter;  // prefix/postfix/finalizer must be static

    private readonly Harmony _harmony;
    private readonly string[]? _include;
    private readonly string[]? _exclude;

    public HarmonyPatches(TraceEmitter emitter, string[]? include, string[]? exclude)
    {
        s_emitter = emitter;
        _harmony = new Harmony("crashdrive.tracer");
        _include = include;
        _exclude = exclude;
    }

    public void Apply()
    {
        // Patch assemblies already loaded.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            TryPatchAssembly(asm);

        // And any that load later (lazy / dynamic).
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => TryPatchAssembly(e.LoadedAssembly);
    }

    // Diagnostic counter for verification. Surfaced via env var
    // CRASHDRIVE_TRACE_DEBUG=1 so we don't noise up normal runs.
    private int _patchedMethods;
    private int _skippedAssemblies;
    private int _patchedAssemblies;

    internal string DebugReport() =>
        $"[CrashDrive.Tracer] patched={_patchedMethods} methods across " +
        $"{_patchedAssemblies} assemblies, skipped {_skippedAssemblies} out-of-scope assemblies";

    private void TryPatchAssembly(Assembly asm)
    {
        if (asm.IsDynamic) return;
        var name = asm.GetName().Name ?? "";

        // Include filter: if provided, assembly must match. Otherwise default
        // to IsUserAssembly heuristic (skips BCL/runtime/CrashDrive itself).
        var inScope = _include != null && _include.Length > 0
            ? MatchesAny(name, _include)
            : IsUserAssembly(asm);
        if (!inScope) { Interlocked.Increment(ref _skippedAssemblies); return; }

        if (_exclude != null && _exclude.Length > 0 && MatchesAny(name, _exclude))
        { Interlocked.Increment(ref _skippedAssemblies); return; }

        Debug($"patching assembly: {name}");
        Interlocked.Increment(ref _patchedAssemblies);

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException rtle) { types = rtle.Types.OfType<Type>().ToArray(); }
        catch { return; }

        var prefix    = new HarmonyMethod(typeof(HarmonyPatches), nameof(Prefix));
        var postfix   = new HarmonyMethod(typeof(HarmonyPatches), nameof(Postfix));
        var finalizer = new HarmonyMethod(typeof(HarmonyPatches), nameof(Finalizer));

        foreach (var type in types)
        {
            if (type == null) continue;
            if (!type.IsClass) continue;
            // Don't filter on IsAbstract: static classes satisfy
            // (IsAbstract && IsSealed) but their methods are perfectly
            // patchable. Rely on the per-method IsPatchable check instead.
            if (type.ContainsGenericParameters) continue;   // open generic type

            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.DeclaredOnly);
            }
            catch { continue; }

            foreach (var method in methods)
            {
                if (!IsPatchable(method)) continue;
                try
                {
                    _harmony.Patch(method, prefix: prefix, postfix: postfix, finalizer: finalizer);
                    Interlocked.Increment(ref _patchedMethods);
                }
                catch (Exception ex)
                {
                    // Harmony can legitimately refuse (e.g. invalid IL targets).
                    // Skip; don't abort whole-assembly patching for one failure.
                    Debug($"  patch failed: {type.FullName}.{method.Name}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static bool IsPatchable(MethodInfo method)
    {
        if (method.IsAbstract) return false;
        if (method.ContainsGenericParameters) return false;
        if (method.IsGenericMethodDefinition) return false;
        if ((method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0) return false;
        if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0) return false;
        try
        {
            if (method.GetMethodBody() == null) return false;
        }
        catch { return false; }
        return true;
    }

    private static bool IsUserAssembly(Assembly asm)
    {
        var name = asm.GetName().Name ?? "";

        // Framework / runtime assemblies: out.
        if (name.Equals("mscorlib", StringComparison.Ordinal) ||
            name.Equals("netstandard", StringComparison.Ordinal) ||
            name.Equals("WindowsBase", StringComparison.Ordinal) ||
            name.Equals("0Harmony", StringComparison.Ordinal) ||
            name.StartsWith("System.", StringComparison.Ordinal) ||
            name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
            name.StartsWith("CrashDrive.Tracer", StringComparison.Ordinal))
            return false;

        // Assemblies loaded from shared framework paths: out.
        var loc = SafeLocation(asm);
        if (!string.IsNullOrEmpty(loc))
        {
            if (loc.Contains("\\dotnet\\shared\\", StringComparison.OrdinalIgnoreCase)) return false;
            if (loc.Contains("\\dotnet\\sdk\\", StringComparison.OrdinalIgnoreCase)) return false;
            if (loc.Contains("/dotnet/shared/", StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    private static string SafeLocation(Assembly asm)
    {
        try { return asm.Location; } catch { return ""; }
    }

    private static bool MatchesAny(string name, string[] globs)
    {
        foreach (var g in globs)
        {
            var pattern = "^" + Regex.Escape(g).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            if (Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    // ── Harmony callbacks (must be static) ────────────────────────────

    private static void Prefix(MethodBase __originalMethod, object[] __args)
    {
        var emitter = s_emitter;
        if (emitter == null || emitter.IsSuppressed) return;

        Dictionary<string, string>? locals = null;
        try
        {
            var parameters = __originalMethod.GetParameters();
            if (parameters.Length > 0 && __args != null)
            {
                locals = new Dictionary<string, string>(parameters.Length);
                for (int i = 0; i < parameters.Length && i < __args.Length; i++)
                {
                    locals[parameters[i].Name ?? $"arg{i}"] = SafeRepr(__args[i]);
                }
            }
        }
        catch { }

        var (file, line) = ResolveSource(__originalMethod);
        emitter.EmitCall(
            file: file,
            line: line,
            function: FormatMethodName(__originalMethod),
            locals: locals);
    }

    private static void Postfix(MethodBase __originalMethod, object? __result)
    {
        var emitter = s_emitter;
        if (emitter == null || emitter.IsSuppressed) return;

        string? value = null;
        if (__originalMethod is MethodInfo mi && mi.ReturnType != typeof(void))
        {
            value = SafeRepr(__result);
        }

        var (file, line) = ResolveSource(__originalMethod);
        emitter.EmitReturn(
            file: file,
            line: line,
            function: FormatMethodName(__originalMethod),
            value: value);
    }

    // Harmony convention: if the finalizer's return type is Exception and it
    // returns null, the exception propagates unchanged. Return the received
    // __exception to signal "do not alter behavior".
    private static Exception? Finalizer(MethodBase __originalMethod, Exception? __exception)
    {
        if (__exception == null) return null;
        var emitter = s_emitter;
        if (emitter == null || emitter.IsSuppressed) return __exception;

        var (file, line) = ResolveSource(__originalMethod);
        emitter.EmitException(
            file: file,
            line: line,
            function: FormatMethodName(__originalMethod),
            ex: __exception);

        return __exception;
    }

    private static string FormatMethodName(MethodBase m)
    {
        var type = m.DeclaringType?.FullName ?? "?";
        var parms = string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name));
        return $"{type}.{m.Name}({parms})";
    }

    private static readonly bool s_debug =
        Environment.GetEnvironmentVariable("CRASHDRIVE_TRACE_DEBUG") == "1";

    private static void Debug(string msg)
    {
        if (s_debug) Console.Error.WriteLine($"[CrashDrive.Tracer] {msg}");
    }

    // ── Portable-PDB sequence-point lookup ────────────────────────────
    //
    // Gives trace events real (file, line) coordinates instead of
    // (<assembly-name>, 0). Works only for portable PDBs — Windows PDB
    // (MSF container) is not readable via System.Reflection.Metadata.
    // Microsoft BCL ships public Windows PDBs, so framework methods stay
    // unresolved; user-authored assemblies typically ship portable PDBs.
    //
    // Per-method cache is keyed by MetadataToken because two MethodBase
    // instances can share a token across reflection callers. Per-assembly
    // reader is kept alive for the process lifetime; the tracer's .pdb
    // fetch is irreversible anyway and we never hot-reload PDBs in a
    // target process.

    private static readonly ConcurrentDictionary<Assembly, MetadataReaderProvider?> s_pdbProviders
        = new();
    private static readonly ConcurrentDictionary<int, (string File, int Line)?> s_sourceCache
        = new();

    private static (string File, int Line) ResolveSource(MethodBase m)
    {
        var assemblyFallback = m.DeclaringType?.Assembly.GetName().Name ?? "";
        var resolved = TryResolveSource(m);
        return resolved is (string file, int line)
            ? (file, line)
            : (assemblyFallback, 0);
    }

    private static (string File, int Line)? TryResolveSource(MethodBase m)
    {
        if (s_sourceCache.TryGetValue(m.MetadataToken, out var cached))
            return cached;

        var result = ComputeSource(m);
        s_sourceCache[m.MetadataToken] = result;
        return result;
    }

    private static (string File, int Line)? ComputeSource(MethodBase m)
    {
        var asm = m.DeclaringType?.Assembly;
        if (asm == null || asm.IsDynamic) return null;

        var provider = s_pdbProviders.GetOrAdd(asm, TryOpenPortablePdb);
        var reader = provider?.GetMetadataReader();
        if (reader == null) return null;

        // MethodDebugInformation is indexed by the MethodDef row (low 24 bits
        // of the MetadataToken; top 8 bits are the 0x06 table id for MethodDef).
        var row = m.MetadataToken & 0x00FFFFFF;
        if (row == 0) return null;

        MethodDebugInformation mdi;
        try { mdi = reader.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(row)); }
        catch { return null; }

        try
        {
            foreach (var sp in mdi.GetSequencePoints())
            {
                if (sp.IsHidden) continue;
                var doc = reader.GetDocument(sp.Document);
                var file = reader.GetString(doc.Name);
                return (file, sp.StartLine);
            }
        }
        catch { }
        return null;
    }

    private static MetadataReaderProvider? TryOpenPortablePdb(Assembly asm)
    {
        var loc = SafeLocation(asm);
        if (string.IsNullOrEmpty(loc)) return null;
        var pdb = Path.ChangeExtension(loc, ".pdb");
        if (!File.Exists(pdb)) return null;

        try
        {
            // Portable PDB begins with the ECMA-335 metadata header "BSJB"
            // (0x424A5342). Windows PDB starts with the MSF header
            // "Microsoft C/C++ MSF 7.00\r\n\u001aDS". Only the former is
            // readable by System.Reflection.Metadata; reject the latter
            // before trying to open it (MetadataReaderProvider would throw).
            using (var peek = File.OpenRead(pdb))
            {
                Span<byte> header = stackalloc byte[4];
                if (peek.Read(header) != 4) return null;
                if (header[0] != 0x42 || header[1] != 0x53
                    || header[2] != 0x4A || header[3] != 0x42)
                    return null;
            }
            var stream = File.OpenRead(pdb);
            return MetadataReaderProvider.FromPortablePdbStream(stream);
        }
        catch { return null; }
    }

    private static string SafeRepr(object? v)
    {
        if (v == null) return "null";
        try
        {
            var s = v.ToString() ?? "";
            if (s.Length > 200) s = s.Substring(0, 197) + "...";
            return s;
        }
        catch
        {
            return $"<{v.GetType().Name}>";
        }
    }
}
