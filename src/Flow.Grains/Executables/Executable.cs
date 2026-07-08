using System;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Interfaces.Model;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Flow.Grains.Executables
{
    public class Executable : IExecutable
    {
        private Engine Engine { get; }
        private ILogger Logger { get; }
        private string Expression { get; }
        private JsonSerializer Serializer { get; }

        public Executable(
            Engine engine,
            ILogger<Executable> logger,
            string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) throw new ArgumentNullException(nameof(expression));

            Engine = engine;
            Logger = logger;
            Expression = expression;

            Serializer = new JsonSerializer
            {
                Formatting = Formatting.None,
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };

            Serializer.Converters.Add(new StringEnumConverter());
        }

        public IExecutable WithArgument<TArgument>(TArgument argument)
            where TArgument : IExpressionArgument
        {
            if (argument != null)
            {
                var instance = new JObjectInstance(Engine, JObject.FromObject(argument.Value, Serializer));
                Engine.SetValue(argument.Name, instance);
            }

            return this;
        }

        public IExecutable WithContext(object values)
        {
            if (values != null)
            {
                foreach (var property in JObject.FromObject(values, Serializer))
                {
                    Engine.SetValue(property.Key, property.Value.AsJsValue(Engine));
                }
            }

            return this;
        }

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
