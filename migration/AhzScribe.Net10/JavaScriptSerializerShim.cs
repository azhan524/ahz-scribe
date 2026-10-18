using System;
using System.Collections.Generic;
using System.Text.Json;

// .NET 10 没有 System.Web.Extensions。旧源码统一用 JavaScriptSerializer，
// 这里保留同一个类型名/调用形状，内部换 System.Text.Json。
namespace System.Web.Script.Serialization
{
    public class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; }
        public int RecursionLimit { get; set; }

        static JsonSerializerOptions Options()
        {
            JsonSerializerOptions o = new JsonSerializerOptions();
            o.PropertyNameCaseInsensitive = true;
            o.MaxDepth = 200;
            o.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            o.WriteIndented = false;
            return o;
        }

        public string Serialize(object value)
        {
            return JsonSerializer.Serialize(value, Options());
        }

        public T Deserialize<T>(string text)
        {
            if (typeof(T) == typeof(Dictionary<string, object>))
            {
                using (JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 200 }))
                    return (T)(object)ToObject(doc.RootElement);
            }
            return JsonSerializer.Deserialize<T>(text, Options());
        }

        static object ToObject(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                foreach (JsonProperty p in e.EnumerateObject()) d[p.Name] = ToObject(p.Value);
                return d;
            }
            if (e.ValueKind == JsonValueKind.Array)
            {
                List<object> l = new List<object>();
                foreach (JsonElement x in e.EnumerateArray()) l.Add(ToObject(x));
                return l;
            }
            if (e.ValueKind == JsonValueKind.String) return e.GetString();
            if (e.ValueKind == JsonValueKind.True) return true;
            if (e.ValueKind == JsonValueKind.False) return false;
            if (e.ValueKind == JsonValueKind.Null || e.ValueKind == JsonValueKind.Undefined) return null;
            long lv;
            if (e.TryGetInt64(out lv)) return lv;
            decimal dv;
            if (e.TryGetDecimal(out dv)) return dv;
            double fv;
            if (e.TryGetDouble(out fv)) return fv;
            return e.GetRawText();
        }
    }
}
