// ----------------------------------------------------------------------------
// Proxy.SchemaFix.cs (LlmProxy partial: JSON安定化 (JSONFIX): Python dict形式スキーマ検出とJSON Schema変換)
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
    // ゲームはLLMにJSON形式での出力を指示するが、素のLLM出力は壊れたJSONに
    // なりやすい (Python表記のTrue/None混入、カッコ閉じ忘れ、コードブロック等)。
    // /completion リクエストのプロンプト内に埋め込まれたPython dict形式の
    // スキーマを検出・変換し、llama-server の json_schema パラメータとして
    // 注入することで、文法制約付き生成により構文的に正しいJSONだけが
    // 出力されるようにする。
    // ・プロンプト内のPython表記スキーマも本物のJSON表記に置き換える
    // ・スキーマの解析に失敗した場合は汎用JSON文法(GBNF)で最低限の構文を保証
    // ・ゲーム側が既に json_schema / grammar を指定している場合は何もしない
    // ・スキーマ指示の無いリクエスト (自由文ナレーション等) には作用しない
    // ・無効化したい場合はMODフォルダに llm_proxy_jsonfix_off.txt を置く
    internal static byte[] ApplyJsonSchemaFix(byte[] body, string reqLine)
    {
        try
        {
            if (!JsonFixEnabled()) return body;
            string text;
            try { text = new UTF8Encoding(false, true).GetString(body); }
            catch { return body; } // バイナリはそのまま

            int pos = 0;
            object rootObj;
            try { rootObj = ParseLiteral(text, ref pos); }
            catch { return body; } // JSONとして読めないボディは触らない
            OrderedDictionary root = rootObj as OrderedDictionary;
            if (root == null) return body;

            // 診断用: ゲームが実際に送ってきたリクエストのトップレベルキー一覧やスキーマ本体を記録する。
            // json_schema/grammar をゲーム側が既に指定しているかどうかの実地調査用。
            // 通常運用ではログを汚さないよう、GUIでONにした項目だけ動く ([DIAG]行/各ダンプは別項目)。
            bool diagLine = DiagEnabled(LogDiagKey);
            bool dumpSchema = DiagEnabled(DumpSchemaKey);
            bool dumpPrompt = DiagEnabled(DumpPromptKey);
            if (diagLine || dumpSchema || dumpPrompt)
            {
                long nPredictDiag = root.Contains("n_predict") ? ToLong(root["n_predict"]) : -1;
                string promptDiag = root.Contains("prompt") ? root["prompt"] as string : null;
                if (diagLine)
                {
                    var keyNames = new List<string>();
                    foreach (System.Collections.DictionaryEntry e in root) keyNames.Add((string)e.Key);
                    Log("[DIAG] " + reqLine + " | 受信キー: " + string.Join(", ", keyNames.ToArray()) +
                        " | n_predict=" + nPredictDiag +
                        " | prompt文字数=" + (promptDiag != null ? promptDiag.Length : -1));
                }
                if (dumpSchema && root.Contains("json_schema"))
                    DumpSchema(reqLine, root["json_schema"], nPredictDiag, promptDiag);
                if (promptDiag != null)
                {
                    // 再生成のたびにプロンプトが固定量ずつ増える(=同じブロックの重複追加)疑いを
                    // 実データで確定させる。重複ブロックを検出したら長さ・位置を1行で報告し、
                    // 全文も別ファイルにダンプして重複部分を目視できるようにする。
                    if (diagLine)
                    {
                        string dup = DetectDuplicateBlock(promptDiag);
                        if (dup != null) Log("[DIAG] [DUP] " + reqLine + " | 重複ブロック検出 " + dup);
                    }
                    if (dumpPrompt) DumpPrompt(reqLine, nPredictDiag, promptDiag);
                }
            }

            // プロンプト内で大きなブロックが直後に完全一致で繰り返される(tandem repeat)場合、1個に畳む。
            // ゲームがスキーマ系システムメッセージを再生成のたびに重複追加し、コンテキスト枯渇→500エラーを
            // 起こす不具合の対策。json_schemaの有無に関わらず適用する。無効化は MODフォルダに llm_proxy_dedup_off.txt。
            bool bodyChanged = false;
            if (DedupEnabled() && root.Contains("prompt"))
            {
                string pr0 = root["prompt"] as string;
                if (pr0 != null)
                {
                    string collapsed = CollapseRepeatedBlocks(pr0);
                    if (collapsed.Length != pr0.Length)
                    {
                        root["prompt"] = collapsed;
                        bodyChanged = true;
                        LogIf(LogDedupKey, "[DEDUP] " + reqLine + " | 重複ブロックを畳んだ " +
                            pr0.Length + "→" + collapsed.Length + "文字");
                    }
                }
            }

            // 「今回のイベント内ログ」の蓄積を直近3ターンだけに削る(field_event_evaluator/
            // quest_referee_event_resolveのみ対象。マーカーの無いプロンプトには作用しない)。
            // json_schemaの有無に関わらず適用する(DEDUPと同じ位置づけ)
            if (EventLogTrimEnabled() && root.Contains("prompt"))
            {
                string pr2 = root["prompt"] as string;
                if (pr2 != null)
                {
                    string trimmed = TrimEventLog(pr2, reqLine);
                    if (trimmed.Length != pr2.Length)
                    {
                        root["prompt"] = trimmed;
                        bodyChanged = true;
                    }
                }
            }

            if (root.Contains("json_schema") || root.Contains("grammar"))
            {
                // ゲーム自身が既に json_schema (grammar制約) を送っているのが実運用のほぼ全件。
                // 構造の正しさはgrammarがトークン単位で強制するため、プロンプトに人間向けの説明
                // として埋め込まれた同一スキーマの冗長なPython dict表記(フィールド1つにつき
                // {'title':...,'type':'string'}等の定型が付く)は、意味づけの参考(フィールド名・
                // enum候補・入れ子構造)さえ残せば安全に圧縮できる。最も複雑なスキーマでは
                // プロンプト全体の6〜7割をこの説明文が占めており、削るほどn_predictに対する
                // 実効の余裕(=打ち切りされにくさ)が増える。
                if (SchemaCompactEnabled() && root.Contains("prompt"))
                {
                    string pr1 = root["prompt"] as string;
                    if (pr1 != null)
                    {
                        string compacted = CompactEmbeddedSchema(pr1, reqLine);
                        if (compacted != null)
                        {
                            root["prompt"] = compacted;
                            bodyChanged = true;
                        }
                    }
                }
                return bodyChanged ? Encoding.UTF8.GetBytes(JsonSerialize(root)) : body;
            }
            string prompt = root.Contains("prompt") ? root["prompt"] as string : null;
            if (prompt == null)
                return bodyChanged ? Encoding.UTF8.GetBytes(JsonSerialize(root)) : body;

            int schemaStart = FindSchemaStart(prompt);
            if (schemaStart < 0) // JSON出力を要求しないリクエスト
                return bodyChanged ? Encoding.UTF8.GetBytes(JsonSerialize(root)) : body;

            object schema = null;
            int schemaEnd = schemaStart;
            try
            {
                int p = schemaStart;
                schema = ParseLiteral(prompt, ref p);
                schemaEnd = p;
            }
            catch { schema = null; }

            if (schema is OrderedDictionary)
            {
                string schemaJson = JsonSerialize(schema);
                // プロンプト内のPython表記スキーマも本物のJSON表記に揃える
                root["prompt"] = prompt.Substring(0, schemaStart) + schemaJson +
                                 prompt.Substring(schemaEnd);
                root["json_schema"] = schema;
                LogIf(LogJsonFixKey, "[JSONFIX] " + reqLine + " | json_schemaを注入 (" + schemaJson.Length + "文字)");
            }
            else
            {
                // スキーマらしき部分はあるが解析できない → 最低限の構文だけ保証
                root["grammar"] = GenericJsonGrammar;
                LogIf(LogJsonFixKey, "[JSONFIX] " + reqLine + " | スキーマ解析失敗のため汎用JSON文法を注入");
            }
            return Encoding.UTF8.GetBytes(JsonSerialize(root));
        }
        catch (Exception ex)
        {
            Log("[JSONFIX] 失敗のため無加工で中継: " + ex.Message);
            return body;
        }
    }

    // プロンプト内のスキーマ開始位置。Python表記/JSON表記のどちらでも検出する
    static readonly string[] SchemaMarkers =
    {
        "{'$defs':", "{'properties':", "{\"$defs\":", "{\"properties\":"
    };

    static int FindSchemaStart(string prompt)
    {
        int best = -1;
        foreach (string m in SchemaMarkers)
        {
            int i = prompt.IndexOf(m, StringComparison.Ordinal);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        return best;
    }

    // json_schemaが既にある(=grammarで構造が強制される)場合に、プロンプト内へ埋め込まれた
    // 同一スキーマの説明文をコンパクト表記へ置き換える。置き換え後の方が短い場合のみ適用する。
    internal static string CompactEmbeddedSchema(string prompt, string reqLine)
    {
        int schemaStart = FindSchemaStart(prompt);
        if (schemaStart < 0) return null; // スキーマ説明の埋め込みが無いリクエストには作用しない

        object schema;
        int schemaEnd = schemaStart;
        try
        {
            int p = schemaStart;
            schema = ParseLiteral(prompt, ref p);
            schemaEnd = p;
        }
        catch { return null; }

        OrderedDictionary schemaDict = schema as OrderedDictionary;
        if (schemaDict == null) return null;

        string compact;
        try { compact = CompactSchemaText(schemaDict); }
        catch { return null; } // 未知の形は安全側で無加工のまま

        if (string.IsNullOrEmpty(compact)) return null;

        string newPrompt = prompt.Substring(0, schemaStart) + compact.TrimEnd('\n') +
                            prompt.Substring(schemaEnd);
        if (newPrompt.Length >= prompt.Length) return null; // 圧縮できなければ変更しない

        LogIf(LogCompactKey, "[COMPACT] " + reqLine + " | スキーマ説明を圧縮 " + prompt.Length + "→" + newPrompt.Length + "文字");
        return newPrompt;
    }

    // JSON Schema (Pydanticが吐く形) をLLM向けの簡潔な一覧表記に落とす。
    // 例: "Area: name, layout_description, locations:string[], atomosphere:∈{tense,normal,...}"
    // grammarが型・必須・enum・入れ子構造をトークン単位で強制するため、ここで保持するのは
    // 「フィールド名」「意味の手がかりになるenum値/参照先の型名」「必須/任意」だけでよい。
    // title・type:stringのような定型句(全フィールドの大半を占める)はあえて捨てる。
    internal static string CompactSchemaText(OrderedDictionary root)
    {
        var sb = new StringBuilder();
        if (root.Contains("$defs"))
        {
            OrderedDictionary defs = root["$defs"] as OrderedDictionary;
            if (defs != null)
                foreach (System.Collections.DictionaryEntry e in defs)
                    AppendTypeLine(sb, (string)e.Key, e.Value as OrderedDictionary);
        }
        string rootTitle = root.Contains("title") ? root["title"] as string : null;
        AppendTypeLine(sb, string.IsNullOrEmpty(rootTitle) ? "Structure" : rootTitle, root);
        return sb.Length > 0 ? sb.ToString() : null;
    }

    static void AppendTypeLine(StringBuilder sb, string typeName, OrderedDictionary typeSchema)
    {
        if (typeSchema == null || !typeSchema.Contains("properties")) return;
        OrderedDictionary props = typeSchema["properties"] as OrderedDictionary;
        if (props == null) return;
        var required = new HashSet<string>();
        if (typeSchema.Contains("required"))
        {
            System.Collections.IEnumerable reqList = typeSchema["required"] as System.Collections.IEnumerable;
            if (reqList != null)
                foreach (object r in reqList) required.Add(Convert.ToString(r));
        }
        sb.Append(typeName).Append(": ");
        bool first = true;
        foreach (System.Collections.DictionaryEntry e in props)
        {
            string fname = (string)e.Key;
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(fname);
            string desc = DescribeField(e.Value as OrderedDictionary);
            if (!string.IsNullOrEmpty(desc)) sb.Append(':').Append(desc);
            if (!required.Contains(fname)) sb.Append('?');
        }
        sb.Append('\n');
    }

    // フィールド1つ分の「意味の手がかり」だけを短く書き出す。素のstring/integer/boolean/numberは
    // 何も付けない(既定の型として扱われるため書くだけ無駄)。enum・$ref・配列・共用体だけ表記する
    static string DescribeField(OrderedDictionary f)
    {
        if (f == null) return "";
        if (f.Contains("const")) return "=\"" + Convert.ToString(f["const"]) + "\"";
        if (f.Contains("$ref")) return RefName(f["$ref"] as string);
        if (f.Contains("enum"))
        {
            System.Collections.IEnumerable vals = f["enum"] as System.Collections.IEnumerable;
            if (vals == null) return "";
            var parts = new List<string>();
            foreach (object v in vals) parts.Add(Convert.ToString(v));
            return "∈{" + string.Join(",", parts.ToArray()) + "}";
        }
        if (f.Contains("anyOf"))
        {
            System.Collections.IEnumerable options = f["anyOf"] as System.Collections.IEnumerable;
            if (options == null) return "";
            var parts = new List<string>();
            bool nullable = false;
            foreach (object opt in options)
            {
                OrderedDictionary od = opt as OrderedDictionary;
                if (od == null) continue;
                if (od.Contains("type") && (od["type"] as string) == "null") { nullable = true; continue; }
                string d = DescribeField(od);
                if (!string.IsNullOrEmpty(d)) parts.Add(d);
            }
            string joined = string.Join("|", parts.ToArray());
            return nullable && joined.Length > 0 ? joined + "?" : joined;
        }
        if (f.Contains("items"))
        {
            string inner = DescribeField(f["items"] as OrderedDictionary);
            if (string.IsNullOrEmpty(inner))
            {
                OrderedDictionary items = f["items"] as OrderedDictionary;
                string itemType = items != null && items.Contains("type") ? items["type"] as string : null;
                inner = itemType ?? "";
            }
            return inner + "[]";
        }
        return "";
    }

    static string RefName(string refPath)
    {
        if (string.IsNullOrEmpty(refPath)) return "";
        int idx = refPath.LastIndexOf('/');
        return idx >= 0 ? refPath.Substring(idx + 1) : refPath;
    }
}
