using System;
using System.Linq;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;
using Jint.Runtime.Descriptors;
using Newtonsoft.Json.Linq;

namespace Flow.Grains.Executables
{
    public class JObjectInstance : ObjectInstance
    {
        private JObject Value { get; }

        public JObjectInstance(Jint.Engine engine, JObject value) : base(engine)
        {
            Value = value;
        }

        public override PropertyDescriptor GetOwnProperty(string propertyName)
        {
            var descriptor = base.GetOwnProperty(propertyName);
            if (descriptor == PropertyDescriptor.Undefined)
            {
                var property = Value.Properties().FirstOrDefault(p => p.Name == propertyName);
                if (property != null)
                {
                    descriptor = new JObjectPropertyDescriptor(Engine, this, property);
                    Properties[propertyName] = descriptor;
                }
            }

            return descriptor;
        }

        public static JToken Convert(JsValue value)
        {
            switch (value.Type)
            {
                case Jint.Runtime.Types.None:
                    throw new NotSupportedException();

                case Jint.Runtime.Types.Undefined:
                    return JValue.CreateUndefined();

                case Jint.Runtime.Types.Null:
                    return JValue.CreateNull();

                case Jint.Runtime.Types.Boolean:
                    return new JValue(value.AsBoolean());

                case Jint.Runtime.Types.String:
                    return JValue.CreateString(value.AsString());

                case Jint.Runtime.Types.Number:
                    return new JValue(value.AsNumber());

                case Jint.Runtime.Types.Object:
                    if (value.AsObject() is JObjectInstance instance)
                    {
                        return instance.Value;
                    }

                    return new JObject(
                        value.AsObject().GetOwnProperties()
                            .Where(kvp => !kvp.Value.Enumerable.HasValue || kvp.Value.Enumerable.Value)
                            .Select(kvp => new JProperty(kvp.Key, Convert(kvp.Value.Value ?? JsValue.Undefined))));

                default:
                    throw new NotSupportedException();
            }
        }

        public static JToken Convert(JTokenType type, JsValue value)
        {
            switch (type)
            {
                case JTokenType.Array:
                    if (value.IsArray())
                    {
                        var array = value.AsArray();
                        return new JArray(
                            array.GetOwnProperties()
                                .Where(k => ArrayInstance.IsArrayIndex(new JsValue(k.Key), out _))
                                .Select(kvp => Convert(kvp.Value.Value ?? JsValue.Null)));
                    }

                    break;

                case JTokenType.Boolean:
                    if (value.IsBoolean()) return new JValue(value.AsBoolean());
                    break;

                case JTokenType.Date:
                    if (value.IsDate()) return new JValue(value.AsDate());
                    break;

                case JTokenType.Float:
                    if (value.IsNumber()) return new JValue((float)value.AsNumber());
                    break;

                case JTokenType.Integer:
                    if (value.IsNumber()) return new JValue((int)value.AsNumber());
                    break;

                case JTokenType.String:
                    if (value.IsString()) return JValue.CreateString(value.AsString());
                    break;
            }

            return Convert(value);
        }
    }
}
