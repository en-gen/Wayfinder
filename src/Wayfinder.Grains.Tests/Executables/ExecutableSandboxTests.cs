using System;
using System.Diagnostics;
using Wayfinder.Grains.Executables;
using FluentAssertions;
using Jint.Runtime;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Wayfinder.Grains.Tests.Executables
{
    // Pins the sandbox budgets on the shared Jint engine factory: hostile or runaway
    // expressions must terminate with a Failure result within the configured budgets
    // instead of blocking the evaluating thread indefinitely.
    public class ExecutableSandboxTests
    {
        private static Executable Create(string expression) =>
            new(SandboxedJintEngine.Create(), Mock.Of<ILogger<Executable>>(), expression);

        [Fact]
        public void ExecuteAsBool__Given_InfiniteLoop__Then_FailsWithinBudget()
        {
            var executable = Create("while(true){}");
            var stopwatch = Stopwatch.StartNew();

            var result = executable.ExecuteAsBool();

            stopwatch.Stop();
            result.IsError.Should().BeTrue();
            stopwatch.Elapsed.Should().BeLessThan(SandboxedJintEngine.Timeout * 5);
        }

        [Fact]
        public void ExecuteAsBool__Given_StatementBomb__Then_Fails()
        {
            var executable = Create("let x = 0; for (let i = 0; i < 100000000; i++) { x++; } x > 0");

            var result = executable.ExecuteAsBool();

            result.IsError.Should().BeTrue();
        }

        [Fact]
        public void ExecuteAsString__Given_MemoryBomb__Then_Fails()
        {
            var executable = Create("let a = []; while (true) { a.push('xxxxxxxxxx'.repeat(10000)); }");

            var result = executable.ExecuteAsString();

            result.IsError.Should().BeTrue();
        }

        [Fact]
        public void ExecuteAsString__Given_OrdinaryExpression__Then_SucceedsThroughSandboxedEngine()
        {
            var executable = Create("(1 + 1).toString()");

            var result = executable.ExecuteAsString();

            result.IsError.Should().BeFalse();
            result.Value.Should().Be("2");
        }

        // #86: recursion depth was unbounded before this (Jint's own default is -1), so
        // self-recursive model-authored script could overflow the real CLR stack. A
        // StackOverflowException is uncatchable in .NET and tears down the whole process - not
        // just this evaluation - which would defeat every budget in this file, all of which rely
        // on throwing a catchable exception that Executable's try/catch turns into a Failure
        // result. Does NOT actually drive the CLR stack anywhere near overflow: LimitRecursion
        // makes Jint track its own call-stack depth and throw once that count (64, per
        // SandboxedJintEngine.MaxRecursionDepth) is exceeded, long before the real stack is at
        // risk - proving the guard converts what would be an uncatchable crash into a handled,
        // catchable eval failure.
        [Fact]
        public void Create__Given_UnboundedRecursion__Then_ThrowsCatchableRecursionDepthOverflowException()
        {
            var engine = SandboxedJintEngine.Create();

            Action act = () => engine.Evaluate("function recurse(n) { return recurse(n + 1); } recurse(0);");

            act.Should().Throw<RecursionDepthOverflowException>(
                "LimitRecursion must convert unbounded model-authored recursion into a catchable " +
                "Jint exception instead of letting it overflow the real CLR stack with an " +
                "uncatchable StackOverflowException");
        }

        [Fact]
        public void ExecuteAsBool__Given_UnboundedRecursion__Then_FailsWithinRecursionBudget()
        {
            var executable = Create("function recurse(n) { return recurse(n + 1) || true; } recurse(0);");

            var result = executable.ExecuteAsBool();

            result.IsError.Should().BeTrue();
        }
    }
}
