using Azure;
using FluentAssertions;
using Xunit;

namespace Flow.Grains.Tests.Integration.Storage
{
    // Pins AzuriteProbe's classification of the Azurite API-version rejection (see the probe's
    // remarks for the incident). Plain [Fact], NOT [AzuriteFact]: the classifier is pure, so
    // these must run - and pass - on machines with no Azurite at all.
    public class AzuriteProbeTests
    {
        // Verbatim rejection observed 2026-07 from Azurite-Blob/3.35.0 running without
        // --skipApiVersionCheck (HTTP 400, x-ms-error-code InvalidHeaderValue), captured with:
        // curl -H "x-ms-version: 2026-02-06" "http://127.0.0.1:10000/devstoreaccount1?comp=properties&restype=service"
        private const string AzuriteApiVersionRejectionMessage =
            "The API version 2026-02-06 is not supported by Azurite. Please upgrade Azurite to " +
            "latest version and retry. If you are using Azurite in Visual Studio, please check " +
            "you have installed latest Visual Studio patch. Azurite command line parameter " +
            "\"--skipApiVersionCheck\" or Visual Studio Code configuration \"Skip Api Version " +
            "Check\" can skip this error.";

        [Fact]
        public void IsApiVersionRejection__Given_AzuriteApiVersionRejection__Then_True()
        {
            var exception = new RequestFailedException(
                400, AzuriteApiVersionRejectionMessage, "InvalidHeaderValue", null);

            AzuriteProbe.IsApiVersionRejection(exception).Should().BeTrue();
        }

        [Fact]
        public void IsApiVersionRejection__Given_OtherInvalidHeaderBadRequest__Then_False()
        {
            // Same status and error code, different cause: a genuinely malformed header must not
            // be classified as the Azurite version rejection - the message is the discriminator.
            var exception = new RequestFailedException(
                400, "The value for one of the HTTP headers is not in the correct format.", "InvalidHeaderValue", null);

            AzuriteProbe.IsApiVersionRejection(exception).Should().BeFalse();
        }

        [Fact]
        public void IsApiVersionRejection__Given_NonBadRequestStatus__Then_False()
        {
            var exception = new RequestFailedException(
                403, "Server failed to authenticate the request.", "AuthenticationFailed", null);

            AzuriteProbe.IsApiVersionRejection(exception).Should().BeFalse();
        }

        [Fact]
        public void ApiVersionRejectedReason__Then_NamesTheFixAndTheComposeStack()
        {
            // The skip reason is the only diagnostic a developer sees in the test summary - it
            // must say what is wrong (version check) and both ways out (flag, or repo stack).
            AzuriteProbe.ApiVersionRejectedReason.Should()
                .Contain("--skipApiVersionCheck").And
                .Contain("docker compose -f devops/infrastructure/docker-compose.yml up -d");
        }
    }
}
