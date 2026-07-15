using System;
using Jint;

namespace Flow.Grains.Executables
{
    // Factory for the Jint engine used by all rule/ifPart/timer expression evaluation.
    // Expressions are model-authored (ultimately tenant-supplied) code executing inside
    // the silo, so the engine runs with hard resource budgets: without them a single
    // `while(true){}` in an ifPart condition blocks a thread-pool thread indefinitely
    // (Jint evaluation is synchronous). Budget breaches surface as engine exceptions,
    // which Executable converts to ExecutableResult.Failure - a hostile expression
    // fails its evaluation instead of degrading the host.
    //
    // #86: recursion depth was unbounded (Jint's own default is -1/unlimited), so deep
    // model-authored recursion would overflow the CLR stack. A StackOverflowException is
    // uncatchable in .NET - it tears down the whole process, not just the evaluation - which
    // defeats every other budget here (they all rely on throwing a catchable exception that
    // Executable's try/catch turns into a Failure result). LimitRecursion makes Jint track its
    // own call-stack depth and throw a catchable RecursionDepthOverflowException before the
    // real CLR stack is at risk. Interop is pinned off explicitly (Interop.Enabled defaults to
    // false already, since nothing here ever calls AllowClr) so a future Jint upgrade or option
    // change can't silently open a path from a hostile expression to arbitrary CLR objects.
    public static class SandboxedJintEngine
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
        public const int MaxStatements = 100_000;
        public const long MemoryLimitBytes = 16_000_000;
        public const int MaxRecursionDepth = 64;

        public static Engine Create() =>
            new(options =>
            {
                options.TimeoutInterval(Timeout);
                options.MaxStatements(MaxStatements);
                options.LimitMemory(MemoryLimitBytes);
                options.LimitRecursion(MaxRecursionDepth);
                options.Interop.Enabled = false;
            });
    }
}
