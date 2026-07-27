using System;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Exceptions;

namespace Wayfinder.Grains.Tests.Integration.SiloFixture
{
    // Shared Serilog wiring for the in-memory TestCluster fixtures (ClusterFixture and
    // RepetitionGuardClusterFixture - kept identical between the two per their own "mirrors"
    // comments). Issue #153: both fixtures used to log at Debug and unconditionally
    // WriteTo.Seq("http://localhost:5341"), which is a good local-dev experience (a running Seq
    // instance to inspect) but on CI has no listener at all - every log statement pays a failing
    // HTTP POST, adding noise and latency that contributed to the RepetitionGuardFootgun flake.
    // Default (no env var set) now logs at Information with no Seq sink, which is what CI gets.
    // Local developers who want the previous Debug+Seq behaviour set WAYFINDER_TEST_SEQ (any
    // non-empty value) before running the tests.
    internal static class IntegrationTestLogging
    {
        private const string SeqOptInEnvVar = "WAYFINDER_TEST_SEQ";

        public static void Configure(ILoggingBuilder logging)
        {
            var seqEnabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SeqOptInEnvVar));

            var levelSwitch = new LoggingLevelSwitch
            {
                MinimumLevel = seqEnabled ? LogEventLevel.Debug : LogEventLevel.Information
            };

            var loggerConfiguration = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(levelSwitch)
                .Enrich.FromLogContext()
                .Enrich.WithExceptionDetails();

            if (seqEnabled)
            {
                loggerConfiguration = loggerConfiguration.WriteTo.Seq(
                    "http://localhost:5341",
                    controlLevelSwitch: levelSwitch);
            }

            logging.AddSerilog(loggerConfiguration.CreateLogger());
        }
    }
}
