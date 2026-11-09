// Minimal JSON for the control protocol: an ordered object type, a writer (ASCII-only output, so
// PowerShell and any console code page read it unchanged) and a strict parser.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CctvOverlay
{
    internal sealed class JObj
    {
        readonly List<KeyValuePair<string, object>> members = new List<KeyValuePair<string, object>>();

        public JObj Set(string name, object value)
        {
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i].Key == name)
                {
                    members[i] = new KeyValuePair<string, object>(name, value);
                    return this;
                }
            }
            members.Add(new KeyValuePair<string, object>(name, value));
            return this;
        }

        public bool Has(string name)
        {
            foreach (KeyValuePair<string, object> kv in members)
                if (kv.Key == name)
                    return true;
            return false;
        }

        public object Get(string name)
        {
            foreach (KeyValuePair<string, object> kv in members)
                if (kv.Key == name)
                    return kv.Value;
            return null;
        }

        public string GetString(string name)
        {
            return Get(name) as string;
        }

        public bool GetBool(string name)
        {
            object v = Get(name);
            return v is bool && (bool)v;
        }

        public long GetInt(string name)
        {
            object v = Get(name);
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            if (v is double) return (long)(double)v;
            return 0;
        }

        public List<object> GetArray(string name)
        {
            return Get(name) as List<object>;
        }

        public IEnumerable<KeyValuePair<string, object>> Members { get { return members; } }
    }

    internal static class Json
    {
        public static string Write(object v)
        {
            var sb = new StringBuilder();
            WriteValue(sb, v);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null)
            {
                sb.Append("null");
            }
            else if (v is string)
            {
                WriteString(sb, (string)v);
            }
            else if (v is bool)
            {
                sb.Append((bool)v ? "true" : "false");
            }
            else if (v is int || v is long || v is uint)
            {
                sb.Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture));
            }
            else if (v is double)
            {
                sb.Append(((double)v).ToString("R", CultureInfo.InvariantCulture));
            }
            else if (v is JObj)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in ((JObj)v).Members)
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
            }
            else if (v is List<object>)
            {
                sb.Append('[');
                bool first = true;
                foreach (object o in (List<object>)v)
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    WriteValue(sb, o);
                }
                sb.Append(']');
            }
            else
            {
                WriteString(sb, v.ToString());
            }
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7e)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /* ---------------- parser ---------------- */

        public static object Parse(string text)
        {
            int i = 0;
            object v = ParseValue(text, ref i, 0);
            SkipWs(text, ref i);
            if (i != text.Length)
                throw new FormatException("trailing data");
            return v;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r'))
                i++;
        }

        static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > 32)
                throw new FormatException("nesting too deep");
            SkipWs(s, ref i);
            if (i >= s.Length)
                throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{')
            {
                i++;
                var o = new JObj();
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}')
                {
                    i++;
                    return o;
                }
                while (true)
                {
                    SkipWs(s, ref i);
                    if (i >= s.Length || s[i] != '"')
                        throw new FormatException("expected member name");
                    string name = ParseString(s, ref i);
                    SkipWs(s, ref i);
                    if (i >= s.Length || s[i] != ':')
                        throw new FormatException("expected ':'");
                    i++;
                    o.Set(name, ParseValue(s, ref i, depth + 1));
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',')
                    {
                        i++;
                        continue;
                    }
                    if (i < s.Length && s[i] == '}')
                    {
                        i++;
                        return o;
                    }
                    throw new FormatException("expected ',' or '}'");
                }
            }
            if (c == '[')
            {
                i++;
                var a = new List<object>();
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']')
                {
                    i++;
                    return a;
                }
                while (true)
                {
                    a.Add(ParseValue(s, ref i, depth + 1));
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',')
                    {
                        i++;
                        continue;
                    }
                    if (i < s.Length && s[i] == ']')
                    {
                        i++;
                        return a;
                    }
                    throw new FormatException("expected ',' or ']'");
                }
            }
            if (c == '"')
                return ParseString(s, ref i);
            if (Lit(s, ref i, "true")) return true;
            if (Lit(s, ref i, "false")) return false;
            if (Lit(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0)
                i++;
            string num = s.Substring(start, i - start);
            long l;
            if (long.TryParse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l))
                return l;
            double d;
            if (num.Length > 0 && double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return d;
            throw new FormatException("invalid value");
        }

        static bool Lit(string s, ref int i, string lit)
        {
            if (string.CompareOrdinal(s, i, lit, 0, lit.Length) == 0)
            {
                i += lit.Length;
                return true;
            }
            return false;
        }

        static string ParseString(string s, ref int i)
        {
            i++; /* opening quote */
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"')
                    return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length)
                        break;
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
                            if (i + 4 > s.Length)
                                throw new FormatException("bad escape");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: throw new FormatException("bad escape");
                    }
                    continue;
                }
                if (c < 0x20)
                    throw new FormatException("control character in string");
                sb.Append(c);
            }
            throw new FormatException("unterminated string");
        }
    }
}
