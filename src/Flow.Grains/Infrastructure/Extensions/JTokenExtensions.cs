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
                    return engine.Intrinsics.Array.Construct(token.Select(x => x.AsJsValue(engine)).ToArray());

                case JTokenType.Boolean:
                    return token.Value<bool>();

                case JTokenType.Bytes:
                    throw new NotSupportedException();

                case JTokenType.Comment:
                    throw new NotSupportedException();

                case JTokenType.Constructor:
                    throw new NotSupportedException();

                case JTokenType.Date:
                    // Intrinsics has no Date constructor accessor in Jint 4.x; go through the JS
                    // engine itself, which always exposes the Date constructor.
                    var epochMillis = new DateTimeOffset(DateTime.Parse(token.ToString())).ToUnixTimeMilliseconds();
                    return engine.Evaluate($"new Date({epochMillis})");

                case JTokenType.Float:
                    return (double)token.Value<float>();

                case JTokenType.Guid:
                    return token.Value<Guid>().ToString();

                case JTokenType.Integer:
                    return token.Value<int>();

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
                    return token.Value<string>();

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
