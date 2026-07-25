using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;
using Jint.Runtime.Descriptors;

namespace Flow.Grains.Executables
{
    public class JsonObjectInstance : ObjectInstance
    {
        private readonly JsonObject _value;

        public JsonObjectInstance(Jint.Engine engine, JsonObject value) : base(engine)
        {
            _value = value ?? throw new ArgumentNullException(nameof(value));
        }

        public override PropertyDescriptor GetOwnProperty(JsValue property)
        {
            var descriptor = base.GetOwnProperty(property);
            if (descriptor == PropertyDescriptor.Undefined)
            {
                var propertyName = property.AsString();
                // TryGetPropertyValue's bool return tracks key presence, not null-ness of the value -
                // it correctly returns true (with node set to null) for a key whose value is a JSON
                // null, distinguishing that from the key genuinely not existing.
                if (_value.TryGetPropertyValue(propertyName, out var node))
                {
                    descriptor = new JsonNodePropertyDescriptor(Engine, _value, propertyName, node);
                    FastSetProperty(propertyName, descriptor);
                }
            }

            return descriptor;
        }

        // A JSON null has no JsonNode instance (see JsonNodeExtensions) - the return type here is
        // nullable to reflect that honestly rather than pretending there is always a node.
        public static JsonNode Convert(JsValue value)
        {
            switch (value.Type)
            {
                case Jint.Runtime.Types.Empty:
                    throw new NotSupportedException();

                // JSON has no "undefined" - it's a JS-only concept. System.Text.Json.Nodes has no way
                // to construct a node that serializes as "undefined" (JsonValueKind.Undefined is the
                // default/uninitialized state of a node reference, not a constructible value). The
                // standard, expected translation - the same one System.Text.Json.JsonSerializer itself
                // performs - is to treat it the same as absent/null.
                case Jint.Runtime.Types.Undefined:
                    return null;

                case Jint.Runtime.Types.Null:
                    return null;

                case Jint.Runtime.Types.Boolean:
                    return JsonValue.Create(value.AsBoolean());

                case Jint.Runtime.Types.String:
                    return JsonValue.Create(value.AsString());

                case Jint.Runtime.Types.Number:
                    return JsonValue.Create(value.AsNumber());

                case Jint.Runtime.Types.Object:
                    if (value.AsObject() is JsonObjectInstance instance)
                    {
                        return instance._value.DeepClone();
                    }

                    var result = new JsonObject();
                    foreach (var kvp in value.AsObject().GetOwnProperties().Where(kvp => kvp.Value.Enumerable))
                    {
                        result[kvp.Key.ToString()] = Convert(kvp.Value.Value ?? JsValue.Undefined);
                    }

                    return result;

                default:
                    throw new NotSupportedException();
            }
        }

        // A JS array index is any own property key that is a non-negative integer string
        // (ECMA-262 6.1.7 "array index"). Jint 4.x no longer exposes ArrayInstance.IsArrayIndex
        // publicly, so this replicates the check directly against the property key.
        private static bool IsArrayIndex(JsValue key) =>
            key.IsString() && uint.TryParse(key.AsString(), out _);

        // Mirrors the existing node's JsonValueKind when coercing a JS value back into JSON, the same
        // way the Newtonsoft-era bridge mirrored the existing JTokenType. Newtonsoft additionally
        // distinguished JTokenType.Integer/.Float/.Date/.Guid by preserving the originating CLR type;
        // JsonValueKind only reflects the JSON wire shape (there is one Number kind, and dates/guids
        // are just strings once serialized), so those distinctions collapse here - see
        // JsonNodeExtensions.AsJsValue for the fuller rationale. A JS Date value with no applicable
        // existing-node kind still converts through DateTime, since that's the natural JSON
        // representation for a date (an ISO-8601 string) with no richer target type to defer to.
        public static JsonNode Convert(JsonValueKind kind, JsValue value)
        {
            switch (kind)
            {
                case JsonValueKind.Array:
                    if (value.IsArray())
                    {
                        var array = value.AsArray();
                        var result = new JsonArray();
                        foreach (var kvp in array.GetOwnProperties().Where(kvp => IsArrayIndex(kvp.Key)))
                        {
                            result.Add(Convert(kvp.Value.Value ?? JsValue.Null));
                        }

                        return result;
                    }

                    break;

                case JsonValueKind.False:
                case JsonValueKind.True:
                    if (value.IsBoolean()) return JsonValue.Create(value.AsBoolean());
                    break;

                case JsonValueKind.Number:
                    if (value.IsNumber()) return JsonValue.Create(value.AsNumber());
                    break;

                case JsonValueKind.String:
                    if (value.IsString()) return JsonValue.Create(value.AsString());
                    if (value.IsDate()) return JsonValue.Create(value.AsDate());
                    break;
            }

            return Convert(value);
        }
    }
}
