// =============================================================================
// NextScan Studio - Minimal dependency-free JSON reader/writer
// Plan ref: MASTER_PLAN section 3.2 (zero user-visible prerequisites, no
// third-party assemblies). This is deliberately small: the IPC control channel
// carries flat objects only, so a full DOM library would be dead weight.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NextScan.Core
{
    /// <summary>A JSON value: null, bool, double, string, List&lt;object&gt; or JsonObj.</summary>
    public class JsonObj : Dictionary<string, object>
    {
        public JsonObj() : base(StringComparer.Ordinal) { }

        public string Str(string key, string def)
        {
            object v;
            if (!TryGetValue(key, out v) || v == null) return def;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public int Int(string key, int def)
        {
            object v;
            if (!TryGetValue(key, out v) || v == null) return def;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return def; }
        }

        public long Long(string key, long def)
        {
            object v;
            if (!TryGetValue(key, out v) || v == null) return def;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch { return def; }
        }

        public double Dbl(string key, double def)
        {
            object v;
            if (!TryGetValue(key, out v) || v == null) return def;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return def; }
        }

        public bool Bool(string key, bool def)
        {
            object v;
            if (!TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (s == null) return def;
            s = s.Trim().ToLowerInvariant();
            return s == "true" || s == "1" || s == "on" || s == "yes";
        }

        public JsonObj Obj(string key)
        {
            object v;
            if (TryGetValue(key, out v)) return v as JsonObj;
            return null;
        }

        public List<object> Arr(string key)
        {
            object v;
            if (TryGetValue(key, out v)) return v as List<object>;
            return null;
        }

        public JsonObj Set(string key, object value) { this[key] = value; return this; }

        public override string ToString() { return Json.Write(this); }
    }

    public static class Json
    {
        // ---------------------------------------------------------------- write
        public static string Write(object value)
        {
            StringBuilder sb = new StringBuilder(256);
            WriteValue(sb, value);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }

            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }

            if (v is float || v is double || v is decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("0"); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (v is byte || v is sbyte || v is short || v is ushort ||
                v is int || v is uint || v is long || v is ulong)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }

            if (v is Enum) { WriteString(sb, v.ToString()); return; }

            JsonObj o = v as JsonObj;
            if (o != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in o)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }

            System.Collections.IDictionary dict = v as System.Collections.IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (System.Collections.DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }

            System.Collections.IEnumerable list = v as System.Collections.IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // Control chars and anything non-ASCII is escaped, so the wire
                        // format stays pure ASCII regardless of pipe encoding settings.
                        if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ----------------------------------------------------------------- read
        public static JsonObj Parse(string text)
        {
            object v = ParseValue(text);
            JsonObj o = v as JsonObj;
            return o ?? new JsonObj();
        }

        public static object ParseValue(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int pos = 0;
            object result = ReadValue(text, ref pos);
            return result;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        static object ReadValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;

            char c = s[i];
            if (c == '{') return ReadObject(s, ref i);
            if (c == '[') return ReadArray(s, ref i);
            if (c == '"') return ReadString(s, ref i);

            if (c == 't' && i + 4 <= s.Length && s.Substring(i, 4) == "true") { i += 4; return true; }
            if (c == 'f' && i + 5 <= s.Length && s.Substring(i, 5) == "false") { i += 5; return false; }
            if (c == 'n' && i + 4 <= s.Length && s.Substring(i, 4) == "null") { i += 4; return null; }

            int start = i;
            while (i < s.Length && "+-.eE0123456789".IndexOf(s[i]) >= 0) i++;
            if (i == start) { i++; return null; }

            string num = s.Substring(start, i - start);
            double d;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return 0.0;
        }

        static JsonObj ReadObject(string s, ref int i)
        {
            JsonObj o = new JsonObj();
            i++; // '{'
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == '}') { i++; break; }
                if (s[i] == ',') { i++; continue; }

                if (s[i] != '"') { i++; continue; }
                string key = ReadString(s, ref i);

                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;

                object val = ReadValue(s, ref i);
                o[key] = val;
            }
            return o;
        }

        static List<object> ReadArray(string s, ref int i)
        {
            List<object> list = new List<object>();
            i++; // '['
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == ']') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                list.Add(ReadValue(s, ref i));
            }
            return list;
        }

        static string ReadString(string s, ref int i)
        {
            StringBuilder sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;

                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 <= s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber,
                                             CultureInfo.InvariantCulture, out code))
                                sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }
    }
}
