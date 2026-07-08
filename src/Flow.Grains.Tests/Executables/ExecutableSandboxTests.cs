using System.Diagnostics;
using Flow.Grains.Executables;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Flow.Grains.Tests.Executables
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
    }
}
