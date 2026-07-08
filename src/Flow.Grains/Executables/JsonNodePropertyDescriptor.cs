using System.Text.Json;
using System.Text.Json.Nodes;
using Flow.Grains.Infrastructure.Extensions;
using Jint;
using Jint.Native;
using Jint.Runtime.Descriptors;

namespace Flow.Grains.Executables
{
    // Jint 4.x made PropertyDescriptor.Value a plain get/set property (no longer virtual), so this
    // can no longer override Value to lazily convert + write back to the underlying node on every
    // access. Instead, convert once up front via the base Value setter, and use SetValue to keep the
    // parent JsonObject in sync whenever JS code assigns to this property (mirrors the original
    // two-way binding intent without needing a virtual override).
    //
    // Unlike Newtonsoft's JProperty (a standalone mutable name+value pair object with a settable
    // .Value), System.Text.Json.Nodes.JsonObject has no equivalent - a property is just a
    // KeyValuePair<string, JsonNode> entry, and mutating it means reassigning the parent JsonObject's
    // indexer (parent[name] = newNode), not writing through a property object. This descriptor holds
    // the parent JsonObject + the property name instead of a JProperty for that reason.
    public class JsonNodePropertyDescriptor : PropertyDescriptor
    {
        private Jint.Engine Engine { get; }
        private JsonObject Parent { get; }
        private string PropertyName { get; }

        public JsonNodePropertyDescriptor(Jint.Engine engine, JsonObject parent, string propertyName, JsonNode node)
        {
            Writable = true;
            Configurable = true;
            Enumerable = true;

            Engine = engine;
            Parent = parent;
            PropertyName = propertyName;

            // JsonNodeExtensions.AsJsValue already maps a null node reference to JsValue.Null (a JSON
            // null) rather than JsValue.Undefined - a JS-only concept with no JSON representation. Do
            // not special-case null here; let AsJsValue's own null-check handle it.
            Value = node.AsJsValue(Engine);
        }

        public void SetValue(JsValue value)
        {
            Value = value;

            if (value == null || value.IsUndefined())
            {
                Parent[PropertyName] = null;
            }
            else
            {
                var existingKind = Parent.TryGetPropertyValue(PropertyName, out var existing) && existing != null
                    ? existing.GetValueKind()
                    : JsonValueKind.Undefined;

                Parent[PropertyName] = JsonObjectInstance.Convert(existingKind, value);
            }
        }
    }
}
