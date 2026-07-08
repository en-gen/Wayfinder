using Flow.Grains.Infrastructure.Extensions;
using Jint;
using Jint.Native;
using Jint.Runtime.Descriptors;
using Newtonsoft.Json.Linq;

namespace Flow.Grains.Executables
{
    // Jint 4.x made PropertyDescriptor.Value a plain get/set property (no longer virtual), so this
    // can no longer override Value to lazily convert + write back to the underlying JProperty on
    // every access. Instead, convert once up front via the base Value setter, and use SetValue to
    // keep the JProperty in sync whenever JS code assigns to this property (mirrors the original
    // two-way binding intent without needing a virtual override).
    public class JObjectPropertyDescriptor : PropertyDescriptor
    {
        private Jint.Engine Engine { get; }
        private JProperty Property { get; }
        private JObjectInstance Parent { get; }

        public JObjectPropertyDescriptor(Jint.Engine engine, JObjectInstance parent, JProperty property)
        {
            Writable = true;
            Configurable = true;
            Enumerable = true;

            Engine = engine;
            Parent = parent;
            Property = property;

            Value = Property.Value != null ? Property.Value.AsJsValue(Engine) : JsValue.Undefined;
        }

        public void SetValue(JsValue value)
        {
            Value = value;

            if (value == null || value.IsUndefined())
            {
                Property.Value = null;
            }
            else
            {
                Property.Value = JObjectInstance.Convert(Property.Value?.Type ?? JTokenType.Undefined, value);
            }
        }
    }
}
