using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Hv86.Core
{
    /// <summary>
    /// Тонкая обёртка над System.Text.Json: везде ходим словарями и списками,
    /// как в мосте WebView2, а не типизированными моделями.
    ///
    /// Причина: протокол (pipe/WebView2) - это JSON, и держать рядом вторую
    /// типизированную копию каждого сообщения значит гарантированно их
    /// рассинхронизировать. Типизированы только vm.json и meta.json - они
    /// надолго, у них есть версия формата.
    /// </summary>
    public static class Json
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string Stringify(object value)
        {
            if (value == null) return "null";
            try { return JsonSerializer.Serialize(value, value.GetType(), Options); }
            catch (Exception ex) { Log.Warn("JSON", "сериализация: " + ex.Message); return "null"; }
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(text))
                {
                    return FromElement(doc.RootElement) as Dictionary<string, object>;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("JSON", "разбор: " + ex.Message);
                return null;
            }
        }

        public static Dictionary<string, object> ParseObjectSafe(string text)
        {
            return ParseObject(text);
        }

        public static object ParseAny(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(text))
                    return FromElement(doc.RootElement);
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- чтение

        public static string GetString(Dictionary<string, object> map, string key, string fallback)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null) return fallback;
            if (value is string s) return s;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static long GetLong(Dictionary<string, object> map, string key, long fallback)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null) return fallback;
            if (value is long l) return l;
            if (value is int i) return i;
            if (value is double d) return (long)d;
            if (value is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)) return parsed;
            return fallback;
        }

        public static int GetInt(Dictionary<string, object> map, string key, int fallback)
        {
            long v = GetLong(map, key, fallback);
            if (v > int.MaxValue) return int.MaxValue;
            if (v < int.MinValue) return int.MinValue;
            return (int)v;
        }

        public static bool GetBool(Dictionary<string, object> map, string key, bool fallback)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null) return fallback;
            if (value is bool b) return b;
            if (value is long l) return l != 0;
            if (value is string s)
            {
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(s, "1", StringComparison.Ordinal)) return true;
                if (string.Equals(s, "0", StringComparison.Ordinal)) return false;
            }
            return fallback;
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value)) return null;
            return value as Dictionary<string, object>;
        }

        public static List<object> GetList(Dictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value)) return null;
            return value as List<object>;
        }

        public static Dictionary<string, object> Obj(params object[] pairs)
        {
            Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                string key = Convert.ToString(pairs[i], CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(key)) map[key] = pairs[i + 1];
            }
            return map;
        }

        public static List<object> List(params object[] items)
        {
            return new List<object>(items ?? new object[0]);
        }

        // -------------------------------------------------------------- утилиты

        public static object FromElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (JsonProperty property in element.EnumerateObject())
                        map[property.Name] = FromElement(property.Value);
                    return map;
                }

                case JsonValueKind.Array:
                {
                    List<object> list = new List<object>();
                    foreach (JsonElement item in element.EnumerateArray())
                        list.Add(FromElement(item));
                    return list;
                }

                case JsonValueKind.String:
                    return element.GetString();

                case JsonValueKind.Number:
                {
                    long asLong;
                    if (element.TryGetInt64(out asLong)) return asLong;
                    return element.GetDouble();
                }

                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }

        public static string Pretty(object value)
        {
            try
            {
                return JsonSerializer.Serialize(value, value == null ? typeof(object) : value.GetType(),
                    new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }
            catch
            {
                return "{}";
            }
        }

        public static bool IsEmpty(IEnumerable collection)
        {
            if (collection == null) return true;
            IEnumerator e = collection.GetEnumerator();
            try { return !e.MoveNext(); }
            finally { (e as IDisposable)?.Dispose(); }
        }

        public static void WriteFile(string path, object value)
        {
            AtomicFile.WriteAllText(path, Pretty(value) + Environment.NewLine);
        }

        public static Dictionary<string, object> ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return ParseObject(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Log.Warn("JSON", "чтение " + path + ": " + ex.Message);
                return null;
            }
        }
    }
}
