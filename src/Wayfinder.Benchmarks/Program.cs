using System.Reflection;
using BenchmarkDotNet.Running;

namespace Wayfinder.Benchmarks
{
    // Exploratory benchmark harness (#224). Run everything:
    //
    //     dotnet run -c Release --project src/Wayfinder.Benchmarks -- --filter *
    //
    // ...or one class:
    //
    //     dotnet run -c Release --project src/Wayfinder.Benchmarks -- --filter *ExpressionEvaluation*
    //
    // Release is mandatory - BenchmarkDotNet refuses a Debug build, because a non-optimized
    // assembly measures the JIT's unoptimized output rather than anything that ships.
    public static class Program
    {
        public static void Main(string[] args) =>
            BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
    }
}
