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
