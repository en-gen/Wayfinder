using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wayfinder.Grains.Executables;
using Jint.Native;

namespace Wayfinder.Grains.Infrastructure.Extensions
{
    public static class JsonNodeExtensions
    {
        // A JSON null has no JsonNode instance in the System.Text.Json.Nodes object model (unlike
        // Newtonsoft, where JTokenType.Null is a real JValue) - a null CLR reference IS the
        // representation. That's handled here, up front, rather than via a case in the switch below.
        //
        // Newtonsoft's JTokenType additionally distinguished JTokenType.Date/.Guid/.Float/.Integer -
        // CLR-type fidelity JObject.FromObject preserved internally when built from a strongly-typed
        // object graph. JsonValueKind (System.Text.Json's equivalent discriminator) only reflects the
        // JSON *wire* shape: Object/Array/String/Number/True/False/Null/Undefined - once a DateTime or
        // Guid is serialized to JSON, it's indistinguishable from any other string. This is not a
        // fidelity regression to work around; it's the honest shape of the JSON model this bridge
        // hands to Jint now carries. Any case expression that referenced a value by its original CLR
        // type (script code checking whether an argument "looks like" a date) was never something the
        // JSON wire format actually preserved - only Newtonsoft's in-memory JToken did, and nothing in
        // the current codebase exercises that distinction (WithArgument/WithContext, the only callers
        // of this bridge, have no live production call sites or tests as of this migration).
        public static JsValue AsJsValue(this JsonNode node, Jint.Engine engine)
        {
            if (node is null)
            {
                return JsValue.Null;
            }

            switch (node.GetValueKind())
            {
                case JsonValueKind.Array:
                    return engine.Intrinsics.Array.Construct(node.AsArray().Select(x => x.AsJsValue(engine)).ToArray());

                case JsonValueKind.False:
                case JsonValueKind.True:
                    return node.GetValue<bool>();

                case JsonValueKind.Null:
                    return JsValue.Null;

                case JsonValueKind.Number:
                    return node.GetValue<double>();

                case JsonValueKind.Object:
                    return new JsonObjectInstance(engine, node.AsObject());

                case JsonValueKind.String:
                    return node.GetValue<string>();

                case JsonValueKind.Undefined:
                    return JsValue.Undefined;

                default:
                    throw new NotSupportedException();
            }
        }
    }
}
