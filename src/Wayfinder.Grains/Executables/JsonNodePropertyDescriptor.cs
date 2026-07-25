using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wayfinder.Grains.Infrastructure.Extensions;
using Jint;
using Jint.Native;
using Jint.Runtime.Descriptors;

namespace Wayfinder.Grains.Executables
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
        private readonly Jint.Engine _engine;
        private readonly JsonObject _parent;
        private readonly string _propertyName;

        public JsonNodePropertyDescriptor(Jint.Engine engine, JsonObject parent, string propertyName, JsonNode node)
        {
            Writable = true;
            Configurable = true;
            Enumerable = true;

            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            _propertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));

            // JsonNodeExtensions.AsJsValue already maps a null node reference to JsValue.Null (a JSON
            // null) rather than JsValue.Undefined - a JS-only concept with no JSON representation. Do
            // not special-case null here; let AsJsValue's own null-check handle it.
            Value = node.AsJsValue(_engine);
        }

        public void SetValue(JsValue value)
        {
            Value = value;

            if (value == null || value.IsUndefined())
            {
                _parent[_propertyName] = null;
            }
            else
            {
                var existingKind = _parent.TryGetPropertyValue(_propertyName, out var existing) && existing != null
                    ? existing.GetValueKind()
                    : JsonValueKind.Undefined;

                _parent[_propertyName] = JsonObjectInstance.Convert(existingKind, value);
            }
        }
    }
}
