// ----------------------------------------------------------------------------
// Proxy.Rules.cs (LlmProxy partial: 置換ルールの読み込み・適用・重複ブロック検出)
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
using System.Text.RegularExpressions;
using System.Threading;

static partial class LlmProxy
{
    // 行頭「regex:」のルールは置換前を .NET 正規表現として解釈する (GUIの「正規表現」列と連動)
    const string RegexPrefix = "regex:";
    // 暴走パターン (破滅的バックトラック) で中継が止まらないようにするための上限
    static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    static List<Rule> LoadRules(string path)
    {
        var rules = new List<Rule>();
        // タブセクション: 「#tab:名前」以降は有効タブ、「#offtab:名前」以降は無効タブ (中のルールを無視)。
        // タブ行が1つも無い旧形式のファイルは、全行が有効として読まれる。
        bool tabEnabled = true;
        foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = raw.Trim().TrimStart('\uFEFF').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#tab:", StringComparison.Ordinal)) { tabEnabled = true; continue; }
            if (line.StartsWith("#offtab:", StringComparison.Ordinal)) { tabEnabled = false; continue; }
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;
            if (!tabEnabled) continue;
            bool isRegex = false;
            if (line.StartsWith(RegexPrefix, StringComparison.Ordinal))
            {
                isRegex = true;
                line = line.Substring(RegexPrefix.Length);
            }
            int idx = line.IndexOf("=>", StringComparison.Ordinal);
            if (idx <= 0)
            {
                Log("[WARN] 不正なルール行を無視: " + line);
                continue;
            }
            string from = line.Substring(0, idx);
            string rest = line.Substring(idx + 2);
            // 「置換前=>置換後=>確率」形式。確率 (0-100) は省略可能で、省略時は100
            int prob = 100;
            int idx2 = rest.LastIndexOf("=>", StringComparison.Ordinal);
            if (idx2 >= 0)
            {
                int p;
                if (int.TryParse(rest.Substring(idx2 + 2).Trim(), out p) && p >= 0 && p <= 100)
                {
                    prob = p;
                    rest = rest.Substring(0, idx2);
                }
            }
            string to = rest;
            if (isRegex)
            {
                Regex rx;
                try { rx = new Regex(from, RegexOptions.None, RegexTimeout); }
                catch (ArgumentException ex)
                {
                    Log("[WARN] 不正な正規表現ルールを無視: " + from + " (" + ex.Message + ")");
                    continue;
                }
                // \uXXXX エスケープ版は機械変換すると正規表現の意味が変わるため作らない。
                // エスケープ形式のプロンプトに正規表現で当てたい場合はパターン側に \\uXXXX を書く
                rules.Add(new Rule
                {
                    From = from,
                    To = to,
                    DispFrom = from + "〔正規表現〕",
                    DispTo = to,
                    Prob = prob,
                    IsRegex = true,
                    Rx = rx
                });
                continue;
            }
            rules.Add(new Rule { From = from, To = to, DispFrom = from, DispTo = to, Prob = prob });
            // Pythonの json.dumps(ensure_ascii=True) は日本語を \uXXXX にするので、その形も登録
            string ef = EscapeNonAscii(from);
            if (ef != from)
                rules.Add(new Rule
                {
                    From = ef,
                    To = EscapeNonAscii(to),
                    DispFrom = from + "〔エスケープ形式〕",
                    DispTo = to,
                    Prob = prob
                });
        }
        return rules;
    }

    // ルールファイルの更新をリクエストのたびに検知し、その場で再読込する (ゲーム再起動不要)。
    // 読み込みに失敗した場合 (保存の書き込み途中など) は前回のルールを使い続け、次回に再試行する。
    static void ReloadRulesIfChanged()
    {
        lock (RulesLock)
        {
            try
            {
                if (_rulesPath == null || !File.Exists(_rulesPath))
                {
                    // 起動後にファイルが作られた/移動された場合に備えて場所を探し直す
                    string found = FindRulesFile(_exeDir);
                    if (found == null)
                    {
                        if (Rules.Count > 0)
                        {
                            Rules = new List<Rule>();
                            _rulesStamp = DateTime.MinValue;
                            Log("[RULES] ルールファイルが見つからないため置換を無効化");
                        }
                        return;
                    }
                    _rulesPath = found;
                    _rulesStamp = DateTime.MinValue;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(_rulesPath);
                if (stamp == _rulesStamp) return;
                Rules = LoadRules(_rulesPath);
                _rulesStamp = stamp;
                LogIf(LogRulesKey, "[RULES] 読込: " + _rulesPath + " " + Rules.Count + "パターン(エスケープ版含む)");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static readonly Random Rng = new Random();

    static byte[] ApplyRules(byte[] body, string reqLine)
    {
        ReloadRulesIfChanged();
        var rules = Rules; // 再読込による差し替えに備えて参照を固定
        if (rules.Count == 0) return body;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(body); }
        catch { return body; } // バイナリはそのまま
        bool changed = false;

        // 同一「置換前」のルールをグループ化する (出現順を保持)。
        // 正規表現とリテラルは同じ文字列でも意味が違うので先頭タグで別グループに分ける
        var groups = new List<List<Rule>>();
        var index = new Dictionary<string, List<Rule>>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            string key = (rule.IsRegex ? "R\0" : "L\0") + rule.From;
            List<Rule> g;
            if (!index.TryGetValue(key, out g))
            {
                g = new List<Rule>();
                index.Add(key, g);
                groups.Add(g);
            }
            g.Add(rule);
        }

        // グループごとに1回抽選し、置換するかどうかと置換先を決める。
        // 合計が100以下: 各ルールは (確率/100) で発動し、残りは無置換。
        // 合計が100超 : 各ルールは (確率/合計) で発動し、必ずどれかに置換される。
        foreach (var g in groups)
        {
            bool hit;
            try { hit = g[0].IsRegex ? g[0].Rx.IsMatch(text) : text.Contains(g[0].From); }
            catch (RegexMatchTimeoutException)
            {
                Log("[WARN] 正規表現の照合がタイムアウトしたためスキップ: \"" + Snip(g[0].DispFrom) + "\"");
                continue;
            }
            if (!hit) continue;
            int total = 0;
            foreach (var r in g) total += r.Prob;
            if (total <= 0)
            {
                LogIf(LogReplaceKey, "[SKIP] " + reqLine + " | \"" + Snip(g[0].DispFrom) + "\" 確率0のため置換せず");
                continue;
            }
            int denom = Math.Max(total, 100);
            int roll;
            lock (Rng) roll = Rng.Next(denom);
            Rule chosen = null;
            int acc = 0;
            foreach (var r in g)
            {
                acc += r.Prob;
                if (roll < acc) { chosen = r; break; }
            }
            if (chosen == null)
            {
                LogIf(LogReplaceKey, "[SKIP] " + reqLine + " | \"" + Snip(g[0].DispFrom) + "\" 確率判定により置換せず (" +
                    total + "/" + denom + ")");
                continue;
            }
            if (chosen.IsRegex)
            {
                try { text = chosen.Rx.Replace(text, chosen.To); }
                catch (RegexMatchTimeoutException)
                {
                    Log("[WARN] 正規表現の置換がタイムアウトしたためスキップ: \"" + Snip(chosen.DispFrom) + "\"");
                    continue;
                }
            }
            else
                text = text.Replace(chosen.From, chosen.To);
            changed = true;
            LogIf(LogReplaceKey, "[REPLACE] " + reqLine + " | \"" + Snip(chosen.DispFrom) + "\" -> \"" + Snip(chosen.DispTo) +
                "\" (確率" + chosen.Prob + "/" + denom + ")");
        }
        return changed ? Encoding.UTF8.GetBytes(text) : body;
    }

    // json.dumps(ensure_ascii=True) と同じ形式 (小文字16進、UTF-16コード単位ごと) でエスケープ
    static string EscapeNonAscii(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c < 0x80) sb.Append(c);
            else sb.Append("\\u").Append(((int)c).ToString("x4"));
        }
        return sb.ToString();
    }

    static string Snip(string s)
    {
        return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
    }

    static string FindRulesFile(string startDir)
    {
        // 適用時に exe と同じ場所へ書かれたマーカー (MODフォルダの絶対パス) を最優先で使う。
        // MODフォルダの名前や場所に依存せず、ゲームルートにも何も置かない。
        string marker = Path.Combine(startDir, MarkerFileName);
        if (File.Exists(marker))
        {
            string dir = ReadMarkerDir(marker);
            if (dir != null)
            {
                string rp = Path.Combine(dir, RulesFileName);
                if (File.Exists(rp)) return rp;
            }
        }

        // 後方互換: マーカーが無い旧適用のために従来の探索も残す
        string d = startDir;
        for (int i = 0; i < 5 && !string.IsNullOrEmpty(d); i++)
        {
            string p = Path.Combine(d, "InstantaleLlmProxy", RulesFileName);
            if (File.Exists(p)) return p;
            p = Path.Combine(d, "mod_llm_proxy", RulesFileName);
            if (File.Exists(p)) return p;
            p = Path.Combine(d, RulesFileName);
            if (File.Exists(p)) return p;
            d = Path.GetDirectoryName(d);
        }
        return null;
    }

    // マーカーファイルからMODフォルダのパスを読む。
    // GUI (UTF-8 BOM付き) と apply.bat (ANSI) のどちらが書いた形式でも読めるようにする
    static string ReadMarkerDir(string marker)
    {
        Encoding[] encs = { Encoding.UTF8, Encoding.Default };
        foreach (var enc in encs)
        {
            try
            {
                string dir = File.ReadAllText(marker, enc).Trim().Trim('"');
                if (dir.Length > 0 && Directory.Exists(dir)) return Path.GetFullPath(dir);
            }
            catch { }
        }
        return null;
    }

    // プロンプト内に重複した大きなブロック(再生成のたびに固定量ずつ増える原因の疑い)を
    // 検出し、長さ・位置・先頭断片を返す。無ければ null。
    // 9等分の各点に置いた128文字プローブが2度目に出現するかを見て、当たれば前後へ伸ばして
    // 重複区間の全長を測る(重複ブロックがこの粒度なら少なくとも1点が内部に当たる)。
    internal static string DetectDuplicateBlock(string p)
    {
        if (p == null || p.Length < 512) return null;
        const int Probe = 128;
        int bestLen = 0, bestA = 0, bestB = 0;
        for (int k = 1; k <= 8; k++)
        {
            int off = (int)((long)p.Length * k / 9);
            if (off + Probe >= p.Length) continue;
            string probe = p.Substring(off, Probe);
            int second = p.IndexOf(probe, off + Probe, StringComparison.Ordinal);
            if (second < 0) continue;
            int a = off, b = second;
            int fwd = Probe;
            while (a + fwd < p.Length && b + fwd < p.Length && p[a + fwd] == p[b + fwd]) fwd++;
            int bwd = 0;
            // 2区間が重ならない範囲で後方へ伸ばす (b側の開始が a側の終端を越えないように)
            while (a - bwd - 1 >= 0 && b - bwd - 1 > a + fwd - 1 && p[a - bwd - 1] == p[b - bwd - 1]) bwd++;
            int len = fwd + bwd;
            if (len > bestLen) { bestLen = len; bestA = a - bwd; bestB = b - bwd; }
        }
        if (bestLen < 200) return null; // 短い偶然の一致は無視
        string head = p.Substring(bestA, Math.Min(60, bestLen)).Replace("\r", "").Replace("\n", "\\n");
        return "len=" + bestLen + " pos1=" + bestA + " pos2=" + bestB + " 先頭=\"" + head + "…\"";
    }

    // プロンプト内で大きなブロックが直後に完全一致で繰り返される(tandem repeat)場合、1個に畳む。
    // 完全一致の隣接コピーだけを畳むので意味は変わらない。畳めなければ元の文字列をそのまま返す。
    // 9等分の各点に128文字プローブを置き、2度目の出現との距離を周期候補として前後に伸ばし、
    // 周期領域が2コピー分以上あれば畳み込む。偶然の一致や正当な短い定型を守るため周期は1000文字以上に限定。
    internal static string CollapseRepeatedBlocks(string p)
    {
        if (p == null || p.Length < 2048) return p;
        const int Probe = 128;
        const int MinPeriod = 1000;
        for (int guard = 0; guard < 8; guard++)
        {
            int foundPeriod = 0, foundLo = 0;
            for (int k = 1; k <= 8 && foundPeriod == 0; k++)
            {
                int off = (int)((long)p.Length * k / 9);
                if (off + Probe >= p.Length) continue;
                string probe = p.Substring(off, Probe);
                int second = p.IndexOf(probe, off + Probe, StringComparison.Ordinal);
                if (second < 0) continue;
                int period = second - off;
                if (period < MinPeriod) continue;
                // この周期で隣接コピーが成立するか、前後に伸ばして周期領域を確定する
                int lo = off;
                while (lo - 1 >= 0 && p[lo - 1] == p[lo - 1 + period]) lo--;
                int hi = off;
                while (hi + period < p.Length && p[hi] == p[hi + period]) hi++;
                int spanLen = (hi - lo) + period; // 周期領域の全長
                if (spanLen >= 2 * period) { foundPeriod = period; foundLo = lo; }
            }
            if (foundPeriod == 0) break;
            // foundLo から foundPeriod ごとに同一コピーが続く分を1個だけ残して除去する
            int posEnd = foundLo;
            while (posEnd + foundPeriod <= p.Length &&
                   string.CompareOrdinal(p, posEnd, p, foundLo, foundPeriod) == 0)
                posEnd += foundPeriod;
            p = p.Substring(0, foundLo + foundPeriod) + p.Substring(posEnd);
        }
        return p;
    }

    // field_event_evaluator / quest_referee_event_resolve の2種のプロンプトに現れる
    // 「【今回のイベント内ログ】」ブロックは、名前に反して「今回のイベント」の過去ターンだけでなく
    // クエスト全体で発生した全フィールドイベントの履歴を延々と蓄積する(新クエスト開始でのみ
    // リセットされる)。フィールドイベント自体は必ず3ターン以内に終わる設計のため、判定に古い
    // ターンの詳細は不要。実データでは終盤に29ターン・8,300文字超(プロンプト全体の6割超)に
    // 達することを確認したため、直近3ターンだけ残し、それより前は無言で削る。
    const string EventLogMarker = "【今回のイベント内ログ】";
    const string EventLogTurnMarker = "〈プレイヤーの入力〉";
    const int EventLogKeepTurns = 3;

    internal static string TrimEventLog(string text, string reqLine)
    {
        if (string.IsNullOrEmpty(text)) return text;
        int markerIdx = text.IndexOf(EventLogMarker, StringComparison.Ordinal);
        if (markerIdx < 0) return text; // このブロックを持たないプロンプトには作用しない
        int bodyStart = markerIdx + EventLogMarker.Length;

        var turnStarts = new List<int>();
        int p = bodyStart;
        while (true)
        {
            int f = text.IndexOf(EventLogTurnMarker, p, StringComparison.Ordinal);
            if (f < 0) break;
            turnStarts.Add(f);
            p = f + EventLogTurnMarker.Length;
        }
        if (turnStarts.Count <= EventLogKeepTurns) return text; // 直近件数以内なら削る必要なし

        int cutFrom = turnStarts[turnStarts.Count - EventLogKeepTurns];
        string trimmed = text.Substring(0, bodyStart) + text.Substring(cutFrom);
        LogIf(LogEventLogKey, "[EVENTLOG] " + reqLine + " | 過去ターンを削減 " +
            turnStarts.Count + "→" + EventLogKeepTurns + "件 (" + text.Length + "→" + trimmed.Length + "文字)");
        return trimmed;
    }

    // JSONFIXの対象は生成リクエストだけ。/apply-template などの前処理には触らない。
    // リクエスト行「POST /completion HTTP/1.1」からパス部分だけを取り出して判定する
    static bool IsCompletionRequest(string reqLine)
    {
        if (!reqLine.StartsWith("POST ", StringComparison.Ordinal)) return false;
        int sp = reqLine.IndexOf(' ', 5);
        string path = sp > 0 ? reqLine.Substring(5, sp - 5) : reqLine.Substring(5);
        int q = path.IndexOf('?');
        if (q >= 0) path = path.Substring(0, q);
        return path == "/completion" || path == "/completions" || path == "/v1/completions";
    }
}
