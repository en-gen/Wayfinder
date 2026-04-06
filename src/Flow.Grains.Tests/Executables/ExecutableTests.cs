using System;
using Flow.Grains.Executables;
using Jint;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Flow.Grains.Tests.Executables
{
    public class ExecutableTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public void Ctor__When_NullOrEmptyExpression__Then_ArgNullEx(string exp)
        {
            Assert.Throws<ArgumentNullException>(() => CreateSubject(exp));
        }

        [Fact]
        public void ExecuteAsString__When_InvalidExpression__Then_FailureResult()
        {
            var result = CreateSubject("invalid").ExecuteAsString();
            Assert.True(result.IsError);
            Assert.Null(result.Value);
            Assert.NotNull(result.Message);
        }

        [Fact]
        public void ExecuteAsBool__When_InvalidExpression__Then_FailureResult()
        {
            var result = CreateSubject("invalid").ExecuteAsBool();
            Assert.True(result.IsError);
            Assert.NotNull(result.Message);
        }

        [Theory]
        [InlineData("'a'", "a")]
        [InlineData("'hello' + ' ' + 'world!'", "hello world!")]
        [InlineData("1 + 'b'", "1b")]
        public void ExecuteAsString__When_ValidExpression__Then_SuccessResult(string expression, string expected)
        {
            var result = CreateSubject(expression).ExecuteAsString();
            Assert.False(result.IsError);
            Assert.Equal(expected, result.Value);
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        [InlineData("1 == true", true)]
        [InlineData("1 === true", false)]
        public void ExecuteAsBool__When_ValidExpression__Then_SuccessResult(string expression, bool expected)
        {
            var result = CreateSubject(expression).ExecuteAsBool();
            Assert.False(result.IsError);
            Assert.Equal(expected, result.Value);
        }

        private static Executable CreateSubject(string expression)
        {
            return new Executable(new Engine(), Mock.Of<ILogger<Executable>>(), expression);
        }
    }
}
