using Wayfinder.Grains.Executables;
using Xunit;

namespace Wayfinder.Grains.Tests.Executables
{
    // #158 follow-up - IsError used to be DERIVED from Message
    // (!string.IsNullOrWhiteSpace(Message)), which made a Failure(...) constructed with a blank
    // message (a real scenario - see ExecutableTests' blank-message Jint throws) silently report
    // IsError == false, indistinguishable from success. IsError is now a flag stored directly by
    // the Success/Failure factories, independent of what Message happens to contain.
    public class ExecutableResultTests
    {
        [Fact]
        public void Success__Given_AnyValue__Then_IsErrorFalse()
        {
            var result = ExecutableResult<bool>.Success(false);

            Assert.False(result.IsError);
            Assert.False(result.Value);
        }

        [Theory]
        [InlineData("a real error message")]
        [InlineData("")]
        [InlineData(null)]
        public void Failure__Given_AnyMessage_IncludingBlank__Then_IsErrorTrue(string message)
        {
            var result = ExecutableResult<bool>.Failure(message);

            Assert.True(result.IsError);
            Assert.Equal(message, result.Message);
        }

        [Fact]
        public void ValueOr__Given_SuccessResult__Then_ReturnsValue()
        {
            var result = ExecutableResult<bool>.Success(true);

            Assert.True(result.ValueOr(false));
        }

        [Theory]
        [InlineData("boom")]
        [InlineData("")]
        public void ValueOr__Given_FailureResult_IncludingBlankMessage__Then_ReturnsFallbackNotDefaultValue(string message)
        {
            var result = ExecutableResult<bool>.Failure(message);

            // Value is default(bool) == false on a Failure result (see the ctor) - ValueOr must
            // return the caller-supplied fallback, never that default(TResult), regardless of
            // whether Message happens to be blank.
            Assert.True(result.ValueOr(true));
        }
    }
}
