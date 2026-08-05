// Minimal JSON for Editor (no Newtonsoft). Based on common MiniJSON patterns.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BinGames.EditorTools.CellArt
{
    public static class CellArtJson
    {
        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            return Parser.Parse(json);
        }

        public static string Serialize(object obj)
        {
            var sb = new StringBuilder(256);
            Serializer.Serialize(obj, sb, 0);
            return sb.ToString();
        }

        public static Dictionary<string, object> AsDict(object o) => o as Dictionary<string, object>;

        public static List<object> AsList(object o) => o as List<object>;

        public static string Str(object o, string fallback = null)
        {
            if (o == null)
            {
                return fallback;
            }

            return Convert.ToString(o);
        }

        public static bool Bool(object o, bool fallback = false)
        {
            if (o is bool b)
            {
                return b;
            }

            return fallback;
        }

        public static int? IntNullable(object o)
        {
            if (o == null)
            {
                return null;
            }

            if (o is long l)
            {
                return (int)l;
            }

            if (o is int i)
            {
                return i;
            }

            if (o is double d)
            {
                return (int)d;
            }

            return null;
        }

        sealed class Parser : IDisposable
        {
            readonly StringReader _r;

            Parser(string json) => _r = new StringReader(json);

            public static object Parse(string json)
            {
                using var p = new Parser(json);
                return p.ParseValue();
            }

            public void Dispose() => _r.Dispose();

            object ParseValue()
            {
                EatWs();
                return Peek() switch
                {
                    '{' => ParseObject(),
                    '[' => ParseArray(),
                    '"' => ParseString(),
                    't' => ParseLiteral("true", true),
                    'f' => ParseLiteral("false", false),
                    'n' => ParseLiteral("null", null),
                    _ => ParseNumber()
                };
            }

            Dictionary<string, object> ParseObject()
            {
                var table = new Dictionary<string, object>();
                _r.Read(); // {
                while (true)
                {
                    EatWs();
                    if (Peek() == '}')
                    {
                        _r.Read();
                        return table;
                    }

                    var key = ParseString();
                    EatWs();
                    if (_r.Read() != ':')
                    {
                        throw new Exception("json : expected");
                    }

                    table[key] = ParseValue();
                    EatWs();
                    var c = Peek();
                    if (c == ',')
                    {
                        _r.Read();
                        continue;
                    }

                    if (c == '}')
                    {
                        _r.Read();
                        return table;
                    }

                    throw new Exception("json object bad");
                }
            }

            List<object> ParseArray()
            {
                var list = new List<object>();
                _r.Read(); // [
                while (true)
                {
                    EatWs();
                    if (Peek() == ']')
                    {
                        _r.Read();
                        return list;
                    }

                    list.Add(ParseValue());
                    EatWs();
                    var c = Peek();
                    if (c == ',')
                    {
                        _r.Read();
                        continue;
                    }

                    if (c == ']')
                    {
                        _r.Read();
                        return list;
                    }

                    throw new Exception("json array bad");
                }
            }

            string ParseString()
            {
                var sb = new StringBuilder();
                _r.Read(); // "
                while (true)
                {
                    var c = (char)_r.Read();
                    if (c == '"')
                    {
                        return sb.ToString();
                    }

                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    c = (char)_r.Read();
                    switch (c)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            sb.Append(c);
                            break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            var hex = new char[4];
                            _r.Read(hex, 0, 4);
                            sb.Append((char)Convert.ToInt32(new string(hex), 16));
                            break;
                    }
                }
            }

            object ParseNumber()
            {
                var sb = new StringBuilder();
                while ("+-0123456789.eE".IndexOf(Peek()) >= 0)
                {
                    sb.Append((char)_r.Read());
                }

                var s = sb.ToString();
                if (s.IndexOf('.') >= 0 || s.IndexOf('e') >= 0 || s.IndexOf('E') >= 0)
                {
                    return double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
                }

                return long.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            }

            object ParseLiteral(string lit, object value)
            {
                for (var i = 0; i < lit.Length; i++)
                {
                    if (_r.Read() != lit[i])
                    {
                        throw new Exception("json literal");
                    }
                }

                return value;
            }

            void EatWs()
            {
                while (char.IsWhiteSpace(Peek()))
                {
                    _r.Read();
                }
            }

            char Peek()
            {
                var c = _r.Peek();
                return c < 0 ? '\0' : (char)c;
            }
        }

        static class Serializer
        {
            public static void Serialize(object obj, StringBuilder sb, int indent)
            {
                switch (obj)
                {
                    case null:
                        sb.Append("null");
                        break;
                    case string s:
                        sb.Append('"');
                        foreach (var c in s)
                        {
                            switch (c)
                            {
                                case '\\': sb.Append("\\\\"); break;
                                case '"': sb.Append("\\\""); break;
                                case '\n': sb.Append("\\n"); break;
                                case '\r': sb.Append("\\r"); break;
                                case '\t': sb.Append("\\t"); break;
                                default: sb.Append(c); break;
                            }
                        }

                        sb.Append('"');
                        break;
                    case bool b:
                        sb.Append(b ? "true" : "false");
                        break;
                    case int _:
                    case long _:
                    case float _:
                    case double _:
                    case decimal _:
                        sb.Append(Convert.ToString(obj, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case IDictionary dict:
                        sb.Append("{\n");
                        var first = true;
                        foreach (DictionaryEntry e in dict)
                        {
                            if (!first)
                            {
                                sb.Append(",\n");
                            }

                            first = false;
                            Indent(sb, indent + 1);
                            Serialize(e.Key.ToString(), sb, indent + 1);
                            sb.Append(": ");
                            Serialize(e.Value, sb, indent + 1);
                        }

                        sb.Append('\n');
                        Indent(sb, indent);
                        sb.Append('}');
                        break;
                    case IList list:
                        sb.Append("[\n");
                        for (var i = 0; i < list.Count; i++)
                        {
                            if (i > 0)
                            {
                                sb.Append(",\n");
                            }

                            Indent(sb, indent + 1);
                            Serialize(list[i], sb, indent + 1);
                        }

                        sb.Append('\n');
                        Indent(sb, indent);
                        sb.Append(']');
                        break;
                    default:
                        Serialize(obj.ToString(), sb, indent);
                        break;
                }
            }

            static void Indent(StringBuilder sb, int n)
            {
                for (var i = 0; i < n; i++)
                {
                    sb.Append("  ");
                }
            }
        }
    }
}
