// =============================================================================
// NextScan Studio - a tool's JSON Schema, as Gemini's Schema object
// Plan ref: docs/AI_LAYER.md
//
// Claude and OpenAI take a tool's parameters as JSON Schema text. Google's SDK
// (1.21) takes its own Schema type and has no raw-JSON form, so the one schema
// every tool is written in is translated here: type, description, properties,
// required, items, enum. That is all the tools use; anything else in a schema
// is left out rather than guessed at.
// =============================================================================
using System.Collections.Generic;
using System.Text.Json;

namespace NextScan.Ai
{
    static class GeminiSchema
    {
        public static Google.GenAI.Types.Schema From(string json)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json))
                    return Convert(doc.RootElement);
            }
            catch (JsonException)
            {
                return new Google.GenAI.Types.Schema { Type = Google.GenAI.Types.Type.Object };
            }
        }

        static Google.GenAI.Types.Schema Convert(JsonElement e)
        {
            var s = new Google.GenAI.Types.Schema();
            JsonElement v;

            if (e.TryGetProperty("type", out v) && v.ValueKind == JsonValueKind.String)
                s.Type = TypeOf(v.GetString());
            if (e.TryGetProperty("description", out v) && v.ValueKind == JsonValueKind.String)
                s.Description = v.GetString();

            if (e.TryGetProperty("enum", out v) && v.ValueKind == JsonValueKind.Array)
            {
                s.Enum = new List<string>();
                foreach (JsonElement item in v.EnumerateArray()) s.Enum.Add(item.ToString());
            }

            if (e.TryGetProperty("properties", out v) && v.ValueKind == JsonValueKind.Object)
            {
                s.Properties = new Dictionary<string, Google.GenAI.Types.Schema>();
                foreach (JsonProperty p in v.EnumerateObject()) s.Properties[p.Name] = Convert(p.Value);
            }

            if (e.TryGetProperty("required", out v) && v.ValueKind == JsonValueKind.Array)
            {
                s.Required = new List<string>();
                foreach (JsonElement item in v.EnumerateArray()) s.Required.Add(item.GetString());
            }

            if (e.TryGetProperty("items", out v) && v.ValueKind == JsonValueKind.Object)
                s.Items = Convert(v);

            return s;
        }

        static Google.GenAI.Types.Type TypeOf(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "string": return Google.GenAI.Types.Type.String;
                case "number": return Google.GenAI.Types.Type.Number;
                case "integer": return Google.GenAI.Types.Type.Integer;
                case "boolean": return Google.GenAI.Types.Type.Boolean;
                case "array": return Google.GenAI.Types.Type.Array;
                default: return Google.GenAI.Types.Type.Object;
            }
        }
    }
}
