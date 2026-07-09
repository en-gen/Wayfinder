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
        private readonly Engine _engine;
        private readonly ILogger _logger;
        private readonly string _expression;
        private readonly JsonSerializerOptions _serializerOptions;

        public Executable(
            Engine engine,
            ILogger<Executable> logger,
            string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) throw new ArgumentNullException(nameof(expression));

            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _expression = expression;

            _serializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            // No naming policy passed here, matching the original `new StringEnumConverter()` (no
            // NamingStrategy/CamelCaseText): enum members serialize using their raw C# member name
            // (PascalCase), independent of PropertyNamingPolicy above (which only affects property
            // names, not enum values).
            _serializerOptions.Converters.Add(new JsonStringEnumConverter());
        }

        public IExecutable WithArgument<TArgument>(TArgument argument)
            where TArgument : IExpressionArgument
        {
            if (argument != null)
            {
                var node = ToJsonObject(argument.Value);
                var instance = new JsonObjectInstance(_engine, node);
                _engine.SetValue(argument.Name, instance);
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
                    _engine.SetValue(property.Key, property.Value.AsJsValue(_engine));
                }
            }

            return this;
        }

        // See IExecutable.WithJsonArgument for why this does not route through ToJsonObject.
        public IExecutable WithJsonArgument(string name, JsonNode value)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));

            _engine.SetValue(name, value.AsJsValue(_engine));

            return this;
        }

        private JsonObject ToJsonObject(object value) =>
            (JsonObject)JsonSerializer.SerializeToNode(value, value.GetType(), _serializerOptions);

        public ExecutableResult<string> ExecuteAsString()
        {
            try
            {
                var result = _engine
                    .Evaluate(_expression)
                    .AsString();

                return ExecutableResult<string>.Success(result);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "unable to evaluate expression: {Expression} → {ErrorMessage}",
                    _expression,
                    e.Message);
                return ExecutableResult<string>.Failure(e.Message);
            }
        }

        public ExecutableResult<bool> ExecuteAsBool()
        {
            try
            {
                var jsValue = _engine.Evaluate(_expression);
                var result = TypeConverter.ToBoolean(jsValue);

                return ExecutableResult<bool>.Success(result);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "unable to evaluate expression: {Expression} → {ErrorMessage}",
                    _expression,
                    e.Message);
                return ExecutableResult<bool>.Failure(e.Message);
            }
        }
    }
}
