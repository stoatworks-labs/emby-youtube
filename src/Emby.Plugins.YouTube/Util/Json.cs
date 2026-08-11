using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Emby.Plugins.YouTube.Util
{
    /// <summary>
    /// A small, dependency-free JSON reader.
    ///
    /// Emby ships its own <c>IJsonSerializer</c>, but its property-name matching rules have varied
    /// between server versions, and binding YouTube's deeply nested responses to POCOs would mean
    /// either a pile of DTOs or a NuGet dependency the plugin cannot ship (Emby loads a bare .dll
    /// and does not resolve NuGet assemblies at runtime). We only ever pluck a handful of fields
    /// out of each response, so a reader that never guesses is both smaller and safer.
    /// </summary>
    public static class Json
    {
        public static JsonValue Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new JsonValue(null);
            var index = 0;
            var value = ParseValue(text, ref index);
            return new JsonValue(value);
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON.");

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            i++; // '{'
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return result; }

            while (i < s.Length)
            {
                SkipWhitespace(s, ref i);
                var key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' in JSON object.");
                i++;
                result[key] = ParseValue(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                throw new FormatException("Expected ',' or '}' in JSON object.");
            }
            throw new FormatException("Unterminated JSON object.");
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var result = new List<object>();
            i++; // '['
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return result; }

            while (i < s.Length)
            {
                result.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                throw new FormatException("Expected ',' or ']' in JSON array.");
            }
            throw new FormatException("Unterminated JSON array.");
        }

        private static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected '\"' at start of JSON string.");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) break;
                var esc = s[i++];
                switch (esc)
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
                        if (i + 4 > s.Length) throw new FormatException("Truncated \\u escape.");
                        sb.Append((char)ushort.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("Invalid escape '\\" + esc + "'.");
                }
            }
            throw new FormatException("Unterminated JSON string.");
        }

        private static object ParseNumber(string s, ref int i)
        {
            var start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;
            var raw = s.Substring(start, i - start);
            if (raw.Length == 0) throw new FormatException("Expected a JSON number.");

            // Prefer an exact integer so ids and durations never pick up float rounding.
            if (raw.IndexOf('.') < 0 && raw.IndexOf('e') < 0 && raw.IndexOf('E') < 0
                && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                return l;
            }
            return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new FormatException("Expected '" + literal + "' in JSON.");
            i += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
    }

    /// <summary>
    /// Null-safe accessor over parsed JSON. Missing keys and type mismatches yield an empty value
    /// rather than throwing, so callers can walk a path like <c>j["a"]["b"][0]["c"].AsString</c>
    /// without guarding every hop — YouTube omits fields freely depending on the video.
    /// </summary>
    public sealed class JsonValue
    {
        private readonly object _raw;

        public JsonValue(object raw) { _raw = raw; }

        public bool Exists => _raw != null;

        public JsonValue this[string key]
        {
            get
            {
                if (_raw is Dictionary<string, object> map && map.TryGetValue(key, out var v))
                    return new JsonValue(v);
                return new JsonValue(null);
            }
        }

        public JsonValue this[int index]
        {
            get
            {
                if (_raw is List<object> list && index >= 0 && index < list.Count)
                    return new JsonValue(list[index]);
                return new JsonValue(null);
            }
        }

        public IEnumerable<JsonValue> Array
        {
            get
            {
                if (_raw is List<object> list)
                {
                    foreach (var item in list) yield return new JsonValue(item);
                }
            }
        }

        public int Count => _raw is List<object> list ? list.Count : 0;

        public string AsString
        {
            get
            {
                if (_raw == null) return null;
                if (_raw is string s) return s;
                if (_raw is bool b) return b ? "true" : "false";
                if (_raw is long l) return l.ToString(CultureInfo.InvariantCulture);
                if (_raw is double d) return d.ToString(CultureInfo.InvariantCulture);
                return null;
            }
        }

        public long? AsLong
        {
            get
            {
                if (_raw is long l) return l;
                if (_raw is double d) return (long)d;
                if (_raw is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) return p;
                return null;
            }
        }

        public int? AsInt => (int?)AsLong;

        public double? AsDouble
        {
            get
            {
                if (_raw is double d) return d;
                if (_raw is long l) return l;
                if (_raw is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
                return null;
            }
        }

        public bool AsBool => _raw is bool b && b;
    }
}
