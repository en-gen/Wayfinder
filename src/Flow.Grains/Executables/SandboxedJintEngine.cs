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
    public static class SandboxedJintEngine
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
        public const int MaxStatements = 100_000;
        public const long MemoryLimitBytes = 16_000_000;

        public static Engine Create() =>
            new(options =>
            {
                options.TimeoutInterval(Timeout);
                options.MaxStatements(MaxStatements);
                options.LimitMemory(MemoryLimitBytes);
            });
    }
}
