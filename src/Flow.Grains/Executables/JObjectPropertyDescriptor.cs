using Flow.Grains.Infrastructure.Extensions;
using Jint.Native;
using Jint.Runtime.Descriptors;
using Newtonsoft.Json.Linq;

namespace Flow.Grains.Executables
{
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
        }

        private JsValue _converted = null;

        public override JsValue Value
        {
            get
            {
                if (_converted == null && Property.Value != null)
                {
                    _converted = Property.Value.AsJsValue(Engine);
                }

                return _converted;
            }
            set
            {
                _converted = value;
                if (value == null)
                {
                    Property.Value = null;
                }
                else
                {
                    Property.Value = JObjectInstance.Convert(Property.Value.Type, value);
                }
            }
        }
    }
}
