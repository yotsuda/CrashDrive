using System.Runtime.CompilerServices;

namespace TinyApp;

// Methods are annotated [MethodImpl(NoInlining)] because Harmony cannot
// intercept calls that the JIT has inlined at the call site. In real apps
// most methods are too large to inline; for this microbenchmark the
// methods are trivial and would otherwise all be inlined into Main.
internal static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Main(string[] args)
    {
        Console.WriteLine("tinyapp: starting");
        var result = Add(3, Multiply(4, 5));
        Console.WriteLine($"tinyapp: result = {result}");

        try { Divide(10, 0); }
        catch (DivideByZeroException ex)
        {
            Console.WriteLine($"tinyapp: caught {ex.GetType().Name}");
        }

        Console.WriteLine("tinyapp: done");
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Add(int a, int b) => a + b;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Multiply(int a, int b)
    {
        var prod = 0;
        for (var i = 0; i < b; i++) prod = Add(prod, a);
        return prod;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Divide(int a, int b) => a / b;
}
