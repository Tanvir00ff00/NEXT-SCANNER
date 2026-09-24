// =============================================================================
// NextScan Studio - the small amount of JSON the tool layer writes by hand
// Plan ref: docs/AI_LAYER.md
//
// A tool's arguments arrive as JSON and its schema is written as JSON, and each
// provider wants them in a different wrapper. These helpers build and read the
// few shapes involved, with System.Text.Json, which the SDKs already bring.
// =============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NextScan.Ai
{
    public static class AiJson
    {
        /// <summary>Writes one JSON object with a writer, and returns it as a string.</summary>
        public static string Object(Action<Utf8JsonWriter> body)
        {
            using (var stream = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(stream))
                {
                    w.WriteStartObject();
                    body(w);
                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>Copies a JSON value, given as text, into a writer. Invalid JSON goes in as an empty object.</summary>
        public static void WriteRaw(Utf8JsonWriter w, string json)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json))
                    doc.RootElement.WriteTo(w);
            }
            catch (JsonException)
            {
                w.WriteStartObject();
                w.WriteEndObject();
            }
        }

        /// <summary>A parsed element that outlives its document, which the SDKs keep hold of.</summary>
        public static JsonElement Parse(string json)
        {
            using (JsonDocument doc = JsonDocument.Parse(json))
                return doc.RootElement.Clone();
        }

        /// <summary>A JSON object as a dictionary of plain .NET values, for Gemini's arguments.</summary>
        public static Dictionary<string, object> ToDictionary(string json)
        {
            var result = new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
                    foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                        result[p.Name] = Plain(p.Value);
                }
            }
            catch (JsonException) { }
            return result;
        }

        static object Plain(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: return e.GetString();
                case JsonValueKind.Number:
                    long whole;
                    if (e.TryGetInt64(out whole)) return whole;
                    return e.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (JsonElement item in e.EnumerateArray()) list.Add(Plain(item));
                    return list;
                case JsonValueKind.Object:
                    var map = new Dictionary<string, object>();
                    foreach (JsonProperty p in e.EnumerateObject()) map[p.Name] = Plain(p.Value);
                    return map;
                default: return null;
            }
        }

        /// <summary>Any .NET value -- a dictionary, a list, a JsonElement -- as JSON text.</summary>
        public static string Serialize(object value)
        {
            try { return JsonSerializer.Serialize(value); }
            catch { return "{}"; }
        }
    }
}
