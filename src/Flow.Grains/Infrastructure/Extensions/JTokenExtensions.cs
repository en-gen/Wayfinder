using System;
using System.Linq;
using Flow.Grains.Executables;
using Jint.Native;
using Newtonsoft.Json.Linq;

namespace Flow.Grains.Infrastructure.Extensions
{
    public static class JTokenExtensions
    {
        public static JsValue AsJsValue(this JToken token, Jint.Engine engine)
        {
            switch (token.Type)
            {
                case JTokenType.Array:
                    return engine.Array.Construct(token.Select(x => x.AsJsValue(engine)).ToArray());

                case JTokenType.Boolean:
                    return new JsValue(token.Value<bool>());

                case JTokenType.Bytes:
                    throw new NotSupportedException();

                case JTokenType.Comment:
                    throw new NotSupportedException();

                case JTokenType.Constructor:
                    throw new NotSupportedException();

                case JTokenType.Date:
                    return engine.Date.Construct(DateTime.Parse(token.ToString()));

                case JTokenType.Float:
                    return new JsValue(token.Value<float>());

                case JTokenType.Guid:
                    return new JsValue(token.Value<Guid>().ToString());

                case JTokenType.Integer:
                    return new JsValue(token.Value<int>());

                case JTokenType.None:
                    throw new NotSupportedException();

                case JTokenType.Null:
                    return JsValue.Null;

                case JTokenType.Object:
                    return new JObjectInstance(engine, (JObject)token);

                case JTokenType.Property:
                    throw new NotSupportedException();

                case JTokenType.Raw:
                    throw new NotSupportedException();

                case JTokenType.String:
                    return new JsValue(token.Value<string>());

                case JTokenType.TimeSpan:
                    throw new NotSupportedException();

                case JTokenType.Undefined:
                    return JsValue.Undefined;

                case JTokenType.Uri:
                    throw new NotSupportedException();

                default:
                    throw new NotSupportedException();
            }
        }
    }
}
