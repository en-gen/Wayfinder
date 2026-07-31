using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Wayfinder.Grains.Tests.Utils.Helpers
{
    // Issue #194 - the integration-test half of the FakeLogger pair (see that file's remarks): an
    // ILoggerProvider a TestCluster silo's ConfigureLogging can register ALONGSIDE the existing
    // Serilog wiring (IntegrationTestLogging.Configure), so the real DI-resolved ILogger<T> for a
    // grain is captured here too without disturbing Serilog/Seq at all. One FakeLogger per category
    // name, exactly matching how the real Microsoft.Extensions.Logging LoggerFactory caches
    // ILogger<T> instances per category - so repeated CreateLogger calls for the same grain type
    // keep accumulating into the same capture rather than losing history to a fresh instance.
    //
    // CAPTURE CEILING - Information and above ONLY, deliberately: Microsoft.Extensions.Logging's
    // global MinLevel defaults to Information, and IntegrationTestLogging's AddSerilog call only
    // raises that for SerilogLoggerProvider itself, never for this provider - so Debug/Trace log
    // calls never even reach a FakeLogger created here (see FakeLogger's own remarks for detail,
    // and IsEnabled there, which is kept honest about this rather than claiming otherwise). This
    // was widened once via `logging.AddFilter<FakeLoggerProvider>(null, LogLevel.Trace)` in
    // ClusterFixture and measured: it turned a full integration run's capture into ~12.5k entries
    // (~8 MB) of Orleans' own chatty Debug logging. Reverted - not a "small in-memory capture" at
    // that point, and nothing today needs anything below Information.
    public sealed class FakeLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentDictionary<string, FakeLogger> _loggers =
            new ConcurrentDictionary<string, FakeLogger>();

        public IReadOnlyList<CapturedLogEntry> Entries => _loggers.Values.SelectMany(l => l.Entries).ToList();

        // See FakeLogger.Clear's remarks - this fixture-lifetime provider is shared across every
        // test that runs against the cluster it's wired into, so a test asserting on captured
        // entries must clear immediately before it acts, not rely on a fresh provider per test.
        public void Clear()
        {
            foreach (var logger in _loggers.Values)
            {
                logger.Clear();
            }
        }

        public ILogger CreateLogger(string categoryName) =>
            _loggers.GetOrAdd(categoryName, name => new FakeLogger(name));

        public void Dispose()
        {
        }
    }
}
