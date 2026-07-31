using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Wayfinder.Grains.Tests.Utils.Helpers
{
    // Issue #194 - a single captured log call: level, event id, the rendered message (matches what
    // Serilog/the real sinks would show, rather than a raw unformatted template + args pair a test
    // would have to reassemble itself), and the exception if one was passed. Category is stamped by
    // whoever owns this FakeLogger instance (see FakeLoggerProvider, which stamps its own key; a
    // directly-constructed FakeLogger used from a unit test - see BaseBehaviorTests - leaves it
    // blank since there's only ever one logger in play there).
    //
    // Fields carries the structured name/value pairs behind a templated call (e.g.
    // logger.LogError("... {RuleError}", message) captures a "RuleError" -> message entry here),
    // when the logged state is the IReadOnlyList<KeyValuePair<string, object>> shape every
    // LoggerExtensions.LogXxx(string, object[]) template call produces - null for a call that logs
    // a plain object instead of a template (Fields.TryGetValue-style lookups just get nothing back
    // then, not an exception). Lets a test assert on a specific field's value directly instead of
    // substring-matching the rendered Message, which is fragile against a message reword that
    // doesn't change behavior at all.
    public sealed record CapturedLogEntry(
        string Category,
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception Exception,
        IReadOnlyList<KeyValuePair<string, object>> Fields);

    // A minimal ILogger fake: records every Log<TState> call into an in-memory, thread-safe list a
    // test can read back. Not a general-purpose logging framework - no filtering options, no scope
    // capture (BeginScope is a no-op so production code that wraps calls in Host.LogWithContext's
    // `using (_logger.BeginScope(...))` doesn't blow up against this fake).
    //
    // Two ways this gets used (see the issue #194 write-up):
    //  - Unit tests (Wayfinder.Grains.Tests) that mock IBehaviorHost construct a FakeLogger
    //    directly and hand it to whatever seam accepts an ILogger - e.g. intercepting a mocked
    //    IBehaviorHost.LogWithContext(Action<ILogger>) callback and invoking the callback against
    //    this instance instead of a real one.
    //  - Integration tests (Wayfinder.Grains.Tests.Integration) register a FakeLoggerProvider (see
    //    that class) into a TestCluster silo's ConfigureLogging alongside the existing Serilog
    //    wiring, so the real DI-resolved ILogger<T> for a grain routes here too.
    //
    // IMPORTANT ceiling on the integration usage: Microsoft.Extensions.Logging's global MinLevel
    // defaults to Information, and IntegrationTestLogging's AddSerilog call only ever raises that
    // for SerilogLoggerProvider (via its own LoggingLevelSwitch), never for any other provider -
    // so a real DI-resolved ILogger routed through FakeLoggerProvider NEVER delivers Debug/Trace
    // calls here at all; they're filtered out by the framework before this class's Log is ever
    // invoked. IsEnabled/Log below are kept consistent with that reality (Information and above
    // only) rather than each unconditionally claiming "everything", including for the standalone
    // unit-test usage above, which is not filtered by anything else - so behavior does not depend
    // on which of the two ways a test wires this up. See ClusterFixture's remarks for why that
    // ceiling was measured and kept rather than widened (Debug/Trace capture of a full integration
    // run is ~12.5k entries / ~8 MB of Orleans' own chatter, not a "small in-memory capture").
    public sealed class FakeLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<CapturedLogEntry> _entries = new ConcurrentQueue<CapturedLogEntry>();

        public FakeLogger(string category = "")
        {
            _category = category ?? "";
        }

        public IReadOnlyList<CapturedLogEntry> Entries => new List<CapturedLogEntry>(_entries);

        // Explicit reset seam rather than a fresh instance per test: integration tests share one
        // FakeLoggerProvider (and therefore one FakeLogger per grain category) for the lifetime of
        // the whole TestCluster fixture (see ClusterFixture), so a test asserting "this call logged
        // an error" needs to clear whatever earlier tests already left behind immediately before it
        // acts, not construct a new capture target it has no way to wire into the running silo.
        public void Clear() => _entries.Clear();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception exception,
            Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter != null ? formatter(state, exception) : state?.ToString();
            var fields = state as IReadOnlyList<KeyValuePair<string, object>>;
            _entries.Enqueue(new CapturedLogEntry(_category, logLevel, eventId, message, exception, fields));
        }

        private sealed class NoopScope : IDisposable
        {
            public static readonly NoopScope Instance = new NoopScope();
            public void Dispose()
            {
            }
        }
    }
}
