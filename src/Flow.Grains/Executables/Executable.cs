using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Interfaces.Model;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Microsoft.Extensions.Logging;

namespace Flow.Grains.Executables
{
    public class Executable : IExecutable
    {
        private Engine Engine { get; }
        private ILogger Logger { get; }
        private string Expression { get; }
        private JsonSerializerOptions SerializerOptions { get; }

        public Executable(
            Engine engine,
            ILogger<Executable> logger,
            string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) throw new ArgumentNullException(nameof(expression));

            Engine = engine;
            Logger = logger;
            Expression = expression;

            SerializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            // No naming policy passed here, matching the original `new StringEnumConverter()` (no
            // NamingStrategy/CamelCaseText): enum members serialize using their raw C# member name
            // (PascalCase), independent of PropertyNamingPolicy above (which only affects property
            // names, not enum values).
            SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        }

        public IExecutable WithArgument<TArgument>(TArgument argument)
            where TArgument : IExpressionArgument
        {
            if (argument != null)
            {
                var node = ToJsonObject(argument.Value);
                var instance = new JsonObjectInstance(Engine, node);
                Engine.SetValue(argument.Name, instance);
            }

            return this;
        }

        public IExecutable WithContext(object values)
        {
            if (values != null)
            {
                var node = ToJsonObject(values);
                foreach (var property in node)
                {
                    Engine.SetValue(property.Key, property.Value.AsJsValue(Engine));
                }
            }

            return this;
        }

        private JsonObject ToJsonObject(object value) =>
            (JsonObject)JsonSerializer.SerializeToNode(value, value.GetType(), SerializerOptions);

        public ExecutableResult<string> ExecuteAsString()
        {
            try
            {
                var result = Engine
                    .Evaluate(Expression)
                    .AsString();

                return ExecutableResult<string>.Success(result);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "unable to evaluate expression: {Expression} → {ErrorMessage}",
                    Expression,
                    e.Message);
                return ExecutableResult<string>.Failure(e.Message);
            }
        }

        public ExecutableResult<bool> ExecuteAsBool()
        {
            try
            {
                var jsValue = Engine.Evaluate(Expression);
                var result = TypeConverter.ToBoolean(jsValue);

                return ExecutableResult<bool>.Success(result);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "unable to evaluate expression: {Expression} → {ErrorMessage}",
                    Expression,
                    e.Message);
                return ExecutableResult<bool>.Failure(e.Message);
            }
        }
    }
}
