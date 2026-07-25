using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Interfaces.Model;
using Jint;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Wayfinder.Grains.Tests.Executables
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

        // WithArgument/WithContext bridge arbitrary CLR objects into Jint script scope via the
        // System.Text.Json.Nodes-based JsonObjectInstance (see Executables/JsonObjectInstance.cs,
        // JsonNodePropertyDescriptor.cs, Infrastructure/Extensions/JsonNodeExtensions.cs). Neither
        // method has a live production call site as of this migration (ExpressionGrain.BuildExecutable
        // has both commented out) and had no prior test coverage, so these pin the rewrite's behavior
        // directly rather than relying on incidental coverage elsewhere.

        [Fact]
        public void WithArgument__Given_ObjectWithScalarProperties__Then_PropertiesAccessibleCamelCased()
        {
            var result = CreateSubject("argName.count + ' ' + argName.label + ' ' + argName.isActive")
                .WithArgument(new TestArgument("argName", new { Count = 3, Label = "widgets", IsActive = true }))
                .ExecuteAsString();

            Assert.False(result.IsError);
            Assert.Equal("3 widgets true", result.Value);
        }

        [Fact]
        public void WithArgument__Given_ObjectWithNestedObject__Then_NestedPropertiesAccessible()
        {
            var result = CreateSubject("argName.parent.child.value")
                .WithArgument(new TestArgument("argName", new { Parent = new { Child = new { Value = "deep" } } }))
                .ExecuteAsString();

            Assert.False(result.IsError);
            Assert.Equal("deep", result.Value);
        }

        [Fact]
        public void WithArgument__Given_ObjectWithArrayProperty__Then_ArrayIndexableAndLengthCorrect()
        {
            var result = CreateSubject("argName.items.length + ':' + argName.items[0] + ',' + argName.items[1]")
                .WithArgument(new TestArgument("argName", new { Items = new[] { "a", "b" } }))
                .ExecuteAsString();

            Assert.False(result.IsError);
            Assert.Equal("2:a,b", result.Value);
        }

        [Fact]
        public void WithArgument__Given_ObjectWithNullProperty__Then_PropertyIsNull()
        {
            var result = CreateSubject("argName.maybe === null")
                .WithArgument(new TestArgument("argName", new { Maybe = (string)null }))
                .ExecuteAsBool();

            Assert.False(result.IsError);
            Assert.True(result.Value);
        }

        [Fact]
        public void WithArgument__Given_ObjectWithEnumProperty__Then_EnumSerializedAsPascalCaseMemberName()
        {
            // Matches the original Newtonsoft StringEnumConverter() default (no NamingStrategy):
            // enum members serialize using their raw C# name, independent of the camelCase property
            // naming policy that applies to property names.
            var result = CreateSubject("argName.state")
                .WithArgument(new TestArgument("argName", new { State = PlanItemState.Active }))
                .ExecuteAsString();

            Assert.False(result.IsError);
            Assert.Equal("Active", result.Value);
        }

        [Fact]
        public void WithArgument__Given_ScriptMutatesProperty__Then_DoesNotThrow()
        {
            // JsonNodePropertyDescriptor.SetValue writes back through the parent JsonObject's
            // indexer - this exercises that write path (Newtonsoft's equivalent mutated the
            // JProperty.Value in place).
            var result = CreateSubject("argName.count = argName.count + 1; '' + argName.count")
                .WithArgument(new TestArgument("argName", new { Count = 1 }))
                .ExecuteAsString();

            Assert.False(result.IsError, result.Message);
            Assert.Equal("2", result.Value);
        }

        [Fact]
        public void WithContext__Given_FlatObject__Then_EachPropertyBoundAsTopLevelVariable()
        {
            var result = CreateSubject("firstName + ' is ' + age")
                .WithContext(new { FirstName = "Ada", Age = 36 })
                .ExecuteAsString();

            Assert.False(result.IsError);
            Assert.Equal("Ada is 36", result.Value);
        }

        [Fact]
        public void WithContext__Given_ObjectWithNestedObjectProperty__Then_NestedPropertiesAccessible()
        {
            var result = CreateSubject("owner.name")
                .WithContext(new { Owner = new { Name = "case-owner" } })
                .ExecuteAsString();

            Assert.False(result.IsError);
            Assert.Equal("case-owner", result.Value);
        }

        [Fact]
        public void WithContext__Given_Null__Then_NoVariablesBoundAndExpressionStillEvaluates()
        {
            var result = CreateSubject("'unaffected'")
                .WithContext(null)
                .ExecuteAsString();

            Assert.False(result.IsError);
            Assert.Equal("unaffected", result.Value);
        }

        // WithJsonArgument binds an already-parsed JsonNode directly (via JsonNodeExtensions.AsJsValue),
        // without the CLR-object round-trip WithArgument/WithContext perform (JsonSerializer
        // .SerializeToNode(...) then a hard cast to JsonObject). That round-trip cannot carry a scalar
        // or array JsonNode - a CaseFileItem's Value (5.3.2) may legitimately be either - so this is
        // covered independently of WithArgument/WithContext's existing coverage above.

        [Fact]
        public void WithJsonArgument__Given_ObjectNode__Then_PropertiesAccessible()
        {
            var node = JsonNode.Parse("""{"amount": 150}""");

            var result = CreateSubject("argName.amount > 100")
                .WithJsonArgument("argName", node)
                .ExecuteAsBool();

            Assert.False(result.IsError, result.Message);
            Assert.True(result.Value);
        }

        [Fact]
        public void WithJsonArgument__Given_ScalarStringNode__Then_BoundAsScalar()
        {
            var node = JsonValue.Create("filed");

            var result = CreateSubject("argName")
                .WithJsonArgument("argName", node)
                .ExecuteAsString();

            Assert.False(result.IsError, result.Message);
            Assert.Equal("filed", result.Value);
        }

        [Fact]
        public void WithJsonArgument__Given_ArrayNode__Then_BoundAsIndexableArray()
        {
            var node = JsonNode.Parse("[1, 2, 3]");

            var result = CreateSubject("argName.length + ':' + argName[1]")
                .WithJsonArgument("argName", node)
                .ExecuteAsString();

            Assert.False(result.IsError, result.Message);
            Assert.Equal("3:2", result.Value);
        }

        [Fact]
        public void WithJsonArgument__Given_NullNode__Then_BoundAsJsNull()
        {
            var result = CreateSubject("argName === null")
                .WithJsonArgument("argName", null)
                .ExecuteAsBool();

            Assert.False(result.IsError, result.Message);
            Assert.True(result.Value);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public void WithJsonArgument__Given_NullOrEmptyName__Then_ArgNullEx(string name)
        {
            Assert.Throws<ArgumentNullException>(() => CreateSubject("true").WithJsonArgument(name, JsonValue.Create(1)));
        }

        private static Executable CreateSubject(string expression)
        {
            return new Executable(new Engine(), Mock.Of<ILogger<Executable>>(), expression);
        }

        private class TestArgument : IExpressionArgument
        {
            public TestArgument(string name, object value)
            {
                Name = name;
                Value = value;
            }

            public string Name { get; }
            public object Value { get; }
        }
    }
}
