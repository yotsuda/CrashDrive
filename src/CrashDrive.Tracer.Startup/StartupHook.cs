using System.Reflection;
using CrashDrive.Tracer.Startup;

// The .NET runtime loads this class by well-known name. It MUST be in the
// global namespace, MUST be named `StartupHook`, and MUST expose a static
// method `Initialize()` with the signature below. See:
//   https://github.com/dotnet/runtime/blob/main/docs/design/features/host-startup-hook.md
//
// Activated by the target process when DOTNET_STARTUP_HOOKS points at this
// assembly. New-CrashDrive sets that env var (and CRASHDRIVE_TRACE_OUT) when
// launching a program under -Language dotnet capture.
internal class StartupHook
{
    public static void Initialize()
    {
        var outputPath = Environment.GetEnvironmentVariable("CRASHDRIVE_TRACE_OUT");
        if (string.IsNullOrEmpty(outputPath))
        {
            // Not under CrashDrive capture — remain dormant. Matters if
            // DOTNET_STARTUP_HOOKS is set persistently in someone's env.
            return;
        }

        // The runtime's default probe paths are the target app's directory
        // and the shared framework — NOT the startup hook's own directory.
        // Register a resolver so Harmony (and any other sibling DLL) loads
        // from wherever this hook DLL sits. Must be hooked before any code
        // path that references Harmony types runs through the JIT.
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromHookDirectory;

        try
        {
            Tracer.Start(outputPath!);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[CrashDrive.Tracer] Failed to start: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Assembly? ResolveFromHookDirectory(object? sender, ResolveEventArgs args)
    {
        var simpleName = new AssemblyName(args.Name).Name;
        if (string.IsNullOrEmpty(simpleName)) return null;

        var hookDir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);
        if (string.IsNullOrEmpty(hookDir)) return null;

        var candidate = Path.Combine(hookDir!, simpleName + ".dll");
        if (!File.Exists(candidate)) return null;

        try { return Assembly.LoadFrom(candidate); } catch { return null; }
    }
}
