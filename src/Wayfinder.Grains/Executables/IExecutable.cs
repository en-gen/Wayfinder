using System.Text.Json.Nodes;
using Wayfinder.Grains.Interfaces.Model;

namespace Wayfinder.Grains.Executables
{
    public interface IExecutable
    {
        IExecutable WithArgument<TArgument>(TArgument argument)
            where TArgument : IExpressionArgument;

        IExecutable WithContext(object values);

        // Binds an already-parsed JsonNode under `name` without the CLR-object round-trip that
        // WithArgument/WithContext perform (JsonSerializer.SerializeToNode(...) then a hard cast to
        // JsonObject - see Executable.ToJsonObject). That round-trip assumes the argument's runtime
        // shape serializes to a JSON *object*; it throws InvalidCastException for a scalar or array
        // node. A CaseFileItem's Value (5.3.2) is a bare JsonNode of any shape - object, array, or
        // scalar (e.g. JsonValue.Create("filed")) - so binding it through WithArgument would let a
        // legitimately scalar-valued CaseFileItem crash expression evaluation before Jint ever runs,
        // which is exactly the "hostile/broken expression must not wedge the sentry" failure mode
        // this engine is meant to avoid. This method reuses the same JsonNode->JsValue primitive
        // (JsonNodeExtensions.AsJsValue) that WithArgument/WithContext are themselves built on, just
        // without the object-shape assumption.
        IExecutable WithJsonArgument(string name, JsonNode value);

        ExecutableResult<string> ExecuteAsString();
        ExecutableResult<bool> ExecuteAsBool();
    }
}
