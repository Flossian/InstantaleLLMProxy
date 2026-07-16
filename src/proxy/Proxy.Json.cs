// ----------------------------------------------------------------------------
// Proxy.Json.cs (LlmProxy partial: 最小限のJSONパーサ/シリアライザ (外部依存なし))
// ----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

static partial class LlmProxy
{
    // JSONとPythonリテラル (dict/list/tuple/str/数値/True/False/None) の両方を
    // 同じ文法のスーパーセットとして解析する。dictはOrderedDictionary (キー順保持)。
    internal static object ParseLiteral(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) throw new FormatException("入力の末尾に到達");
        char c = s[i];
        if (c == '{')
        {
            var d = new OrderedDictionary();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                string key = ParseLiteral(s, ref i) as string;
                if (key == null) throw new FormatException("dictのキーが文字列でない");
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("':'が無い");
                i++;
                d[key] = ParseLiteral(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("'}'が無い");
                if (s[i] == ',')
                {
                    i++;
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == '}') { i++; return d; } // 末尾カンマ許容
                    continue;
                }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("dictの区切りが不正: pos=" + i);
            }
        }
        if (c == '[' || c == '(')
        {
            char close = c == '[' ? ']' : ')';
            var list = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == close) { i++; return list; }
            while (true)
            {
                list.Add(ParseLiteral(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("リストが閉じない");
                if (s[i] == ',')
                {
                    i++;
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == close) { i++; return list; }
                    continue;
                }
                if (s[i] == close) { i++; return list; }
                throw new FormatException("リストの区切りが不正: pos=" + i);
            }
        }
        if (c == '\'' || c == '"') return ParseQuotedString(s, ref i);
        if (MatchWord(s, i, "True")) { i += 4; return true; }
        if (MatchWord(s, i, "False")) { i += 5; return false; }
        if (MatchWord(s, i, "None")) { i += 4; return null; }
        if (MatchWord(s, i, "true")) { i += 4; return true; }
        if (MatchWord(s, i, "false")) { i += 5; return false; }
        if (MatchWord(s, i, "null")) { i += 4; return null; }
        if (c == '-' || c == '+' || (c >= '0' && c <= '9')) return ParseNumber(s, ref i);
        throw new FormatException("不明なリテラル: pos=" + i);
    }

    static void SkipWs(string s, ref int i)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
            else break;
        }
    }

    // 位置iがちょうど単語wか。直後が英数字/_ なら別の識別子の一部とみなして不一致にする
    // (例: "None" と "NoneOfThem" を取り違えない)
    static bool MatchWord(string s, int i, string w)
    {
        if (i + w.Length > s.Length) return false;
        if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false;
        int e = i + w.Length;
        if (e < s.Length)
        {
            char n = s[e];
            if (char.IsLetterOrDigit(n) || n == '_') return false;
        }
        return true;
    }

    // 引用符はシングル/ダブルの両方を受け付ける (Python表記のdictは 'key' を使う)。
    // エスケープもJSONの範囲に加えてPython固有の \x41 や \' まで解釈する
    static string ParseQuotedString(string s, ref int i)
    {
        char q = s[i];
        i++;
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= s.Length) throw new FormatException("文字列が閉じない");
            char c = s[i];
            if (c == q) { i++; return sb.ToString(); }
            if (c == '\\')
            {
                if (i + 1 >= s.Length) throw new FormatException("エスケープが不完全");
                char e = s[i + 1];
                switch (e)
                {
                    case 'n': sb.Append('\n'); i += 2; break;
                    case 't': sb.Append('\t'); i += 2; break;
                    case 'r': sb.Append('\r'); i += 2; break;
                    case 'b': sb.Append('\b'); i += 2; break;
                    case 'f': sb.Append('\f'); i += 2; break;
                    case '0': sb.Append('\0'); i += 2; break;
                    case 'x':
                        if (i + 3 >= s.Length) throw new FormatException("\\xが不完全");
                        sb.Append((char)Convert.ToInt32(s.Substring(i + 2, 2), 16));
                        i += 4;
                        break;
                    case 'u':
                        if (i + 5 >= s.Length) throw new FormatException("\\uが不完全");
                        sb.Append((char)Convert.ToInt32(s.Substring(i + 2, 4), 16));
                        i += 6;
                        break;
                    default: sb.Append(e); i += 2; break; // \' \" \\ \/ など
                }
                continue;
            }
            sb.Append(c);
            i++;
        }
    }

    // 整数は long、小数点や指数を含むものは double で返す。
    // スキーマ中の maxLength などを 3.0 のような形に変えないため、整数はlongのまま保つ
    static object ParseNumber(string s, ref int i)
    {
        int start = i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        bool isDouble = false;
        while (i < s.Length)
        {
            char c = s[i];
            if (c >= '0' && c <= '9') { i++; continue; }
            if (c == '.' || c == 'e' || c == 'E') { isDouble = true; i++; continue; }
            if ((c == '-' || c == '+') && (s[i - 1] == 'e' || s[i - 1] == 'E')) { i++; continue; }
            break;
        }
        string num = s.Substring(start, i - start);
        if (!isDouble)
        {
            long l;
            if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                return l;
        }
        return double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    internal static string JsonSerialize(object o)
    {
        var sb = new StringBuilder(4096);
        WriteJson(o, sb);
        return sb.ToString();
    }

    static void WriteJson(object o, StringBuilder sb)
    {
        if (o == null) { sb.Append("null"); return; }
        if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
        string str = o as string;
        if (str != null) { WriteJsonString(str, sb); return; }
        if (o is long) { sb.Append(((long)o).ToString(CultureInfo.InvariantCulture)); return; }
        if (o is int) { sb.Append(((int)o).ToString(CultureInfo.InvariantCulture)); return; }
        if (o is double)
        {
            double d = (double)o;
            if (d == Math.Floor(d) && !double.IsInfinity(d) && Math.Abs(d) < 9e15)
                sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
            else
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
            return;
        }
        OrderedDictionary od = o as OrderedDictionary;
        if (od != null)
        {
            sb.Append('{');
            bool first = true;
            foreach (System.Collections.DictionaryEntry e in od)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteJsonString((string)e.Key, sb);
                sb.Append(':');
                WriteJson(e.Value, sb);
            }
            sb.Append('}');
            return;
        }
        System.Collections.IEnumerable en = o as System.Collections.IEnumerable;
        if (en != null)
        {
            sb.Append('[');
            bool first = true;
            foreach (object item in en)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteJson(item, sb);
            }
            sb.Append(']');
            return;
        }
        throw new InvalidOperationException("シリアライズ不能な型: " + o.GetType());
    }

    // 非ASCIIはすべて\uXXXXにエスケープする (出力ボディをASCII安全にするため)
    static void WriteJsonString(string s, StringBuilder sb)
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
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
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

    // llama.cpp 付属の json.gbnf 相当。スキーマ変換に失敗したときの保険で、
    // 少なくとも「構文的に正しいJSONオブジェクト」だけを出力させる
    const string GenericJsonGrammar =
        "root   ::= object\n" +
        "value  ::= object | array | string | number | (\"true\" | \"false\" | \"null\") ws\n" +
        "object ::= \"{\" ws ( string \":\" ws value (\",\" ws string \":\" ws value)* )? \"}\" ws\n" +
        "array  ::= \"[\" ws ( value (\",\" ws value)* )? \"]\" ws\n" +
        "string ::= \"\\\"\" ( [^\"\\\\\\x7F\\x00-\\x1F] | \"\\\\\" ([\"\\\\bfnrt] | \"u\" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]) )* \"\\\"\" ws\n" +
        "number ::= (\"-\"? ([0-9] | [1-9] [0-9]{0,15})) (\".\" [0-9]+)? ([eE] [-+]? [0-9] [1-9]{0,15})? ws\n" +
        "ws     ::= | \" \" | \"\\n\" [ \\t]{0,20}\n";
}
