// ----------------------------------------------------------------------------
// Gui.RulesData.cs (MainForm partial: 置換ルールファイルの読み込み・保存・検証)
// ----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

partial class MainForm
{
    // ---------------------------------------------------------------- ルール編集

    // グリッド1行分のルール。Enabled=false は「#off:」付きでファイルに保存され、
    // プロキシ側からはコメント行として無視される (プロキシの変更不要)
    class RuleEntry
    {
        public bool Enabled;
        public bool IsRegex; // 置換前を .NET 正規表現として解釈 (ファイル上は行頭「regex:」)
        public int Prob; // 置換確率 (0-100)
        public string From;
        public string To;
        public string Memo; // 覚え書き。置換動作には影響しない
    }

    // タブ1枚分の保存用データ
    class TabRules
    {
        public string Name;
        public bool Enabled;
        public List<RuleEntry> Rules;
    }

    // llm_replacements.txt が置かれているフォルダをエクスプローラーで開く
    void OpenRulesFolder()
    {
        try
        {
            string dir = Path.GetDirectoryName(_rulesPath);
            if (dir == null || !Directory.Exists(dir))
            {
                MessageBox.Show(this, "フォルダが見つかりません:\n" + dir, "エラー",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "フォルダを開けませんでした:\n" + ex.Message, "エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    const string DisabledPrefix = "#off:";
    const string RegexPrefix = "regex:";    // 正規表現ルール (#off: の後ろに付く: #off:regex:...)
    const string TabPrefix = "#tab:";       // 有効なタブのセクション行
    const string OffTabPrefix = "#offtab:"; // 無効化されたタブのセクション行
    const string MemoPrefix = "#memo:";     // 直後のルール行に対するメモ (プロキシはコメントとして無視)
    const string DefaultTabName = "標準";   // タブ行が無い旧形式ファイル用

    // ルールファイルを読んでタブとグリッドを作り直す。
    // ファイルが無い/読めない場合も空のタブ1枚で始められるようにする
    void LoadRules()
    {
        _ruleTabs.TabPages.Clear();
        _ruleTabs.TabPages.Add(_allPage); // 「すべて」タブは常に先頭に残す
        RuleTab cur = null;
        string memo = null; // \u76F4\u524D\u306E #memo: \u884C\u306E\u5185\u5BB9\u3002\u6B21\u306E\u30EB\u30FC\u30EB\u884C\u306B\u53D6\u308A\u4ED8\u3051\u308B
        if (File.Exists(_rulesPath))
        {
            foreach (string raw in File.ReadAllLines(_rulesPath, Encoding.UTF8))
            {
                string line = raw.Trim().TrimStart('\uFEFF').Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith(TabPrefix, StringComparison.Ordinal))
                {
                    cur = AddRuleTab(line.Substring(TabPrefix.Length).Trim(), true);
                    memo = null;
                    continue;
                }
                if (line.StartsWith(OffTabPrefix, StringComparison.Ordinal))
                {
                    cur = AddRuleTab(line.Substring(OffTabPrefix.Length).Trim(), false);
                    memo = null;
                    continue;
                }
                if (line.StartsWith(MemoPrefix, StringComparison.Ordinal))
                {
                    memo = UnescapeNewlines(line.Substring(MemoPrefix.Length).Trim());
                    continue;
                }
                bool enabled = true;
                if (line.StartsWith(DisabledPrefix, StringComparison.Ordinal))
                {
                    enabled = false;
                    line = line.Substring(DisabledPrefix.Length).Trim();
                }
                else if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                bool isRegex = false;
                if (line.StartsWith(RegexPrefix, StringComparison.Ordinal))
                {
                    isRegex = true;
                    line = line.Substring(RegexPrefix.Length);
                }
                int idx = line.IndexOf("=>", StringComparison.Ordinal);
                if (idx <= 0) continue;
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
                if (cur == null) cur = AddRuleTab(DefaultTabName, true); // 旧形式 (タブ行なし)
                // 正規表現の置換前は \n の2文字自体がパターンの一部なので改行に展開しない
                cur.Grid.Rows.Add(enabled, isRegex, prob.ToString(),
                    isRegex ? from : UnescapeNewlines(from), UnescapeNewlines(rest), memo ?? "");
                memo = null;
            }
        }
        if (RealTabCount() == 0) AddRuleTab(DefaultTabName, true);
        RefreshAllGrid();
        _dirty = false;
    }

    void SaveRules()
    {
        var tabs = CollectAllTabs(true);
        if (tabs == null) return; // バリデーションエラー

        try
        {
            WriteRulesFile(tabs);
            _dirty = false;
            MessageBox.Show(this,
                CountRules(tabs) + "件のルール (" + tabs.Count + "タブ) を保存しました。\n保存先: " + _rulesPath +
                "\n起動中のプロキシには次のLLMリクエストから自動反映されます。",
                "保存完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "保存に失敗:\n" + ex.Message +
                "\n\n対象が Program Files 配下などの場合は、GUIを管理者として実行してください。",
                "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // 対象フォルダの llm_replacements.txt にルールを書き出す。
    // 追記ではなくファイル全体を書き直すので、手書きで足したコメント行は消える
    // (先頭の説明コメントは毎回ここで生成し直している)。
    // プロキシは書き込み時刻の変化を見て自動再読込するため、保存＝即反映になる
    void WriteRulesFile(List<TabRules> tabs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ============================================================");
        sb.AppendLine("# LLMリクエスト置換ルール (llm_proxy用)");
        sb.AppendLine("# ・1行1ルール:  置換前=>置換後=>置換確率");
        sb.AppendLine("# ・置換確率は0-100の整数。省略時は100 (常に置換)");
        sb.AppendLine("# ・同じ置換前のルールが複数ある場合、確率に応じてどれか1つを選択");
        sb.AppendLine("#   (確率の合計が100を超える場合は 値/合計 の割合で必ずどれかに置換)");
        sb.AppendLine("# ・#tab:タブ名 の行から次のタブ行までが1つのタブ (GUIのタブと連動)");
        sb.AppendLine("# ・#offtab:タブ名 は無効化されたタブ (中のルールはすべて無視)");
        sb.AppendLine("# ・改行は \\n (2文字) で書く。LLMリクエストのJSON内の改行と一致する形式");
        sb.AppendLine("#   (GUIのグリッド上では実際の改行として表示・編集される)");
        sb.AppendLine("# ・行頭 # はコメント / UTF-8で保存");
        sb.AppendLine("# ・行頭 #off: は無効化されたルール (GUIの「有効」チェックで切替)");
        sb.AppendLine("# ・行頭 regex: は正規表現ルール (GUIの「正規表現」チェックと連動)");
        sb.AppendLine("#   .NET正規表現。置換後では $1 $2 でキャプチャを参照できる ($自体は $$)");
        sb.AppendLine("#   プロンプト中の改行 (JSON内の \\n の2文字) に当てるにはパターンに \\\\n と書く");
        sb.AppendLine("#   正規表現ルールには \\uXXXX エスケープ形式の自動対応が無い (必要なら自分で書く)");
        sb.AppendLine("# ・#memo:メモ の行は直後のルールの覚え書き (GUIのメモ欄と連動、置換には影響しない)");
        sb.AppendLine("# ・変更を保存すると次のLLMリクエストから自動反映 (ゲーム再起動不要)");
        sb.AppendLine("# ・このファイルは管理GUIからも編集できます (InstantaleLlmProxy.exe)");
        sb.AppendLine("# ============================================================");
        foreach (var t in tabs)
        {
            sb.AppendLine();
            sb.AppendLine((t.Enabled ? TabPrefix : OffTabPrefix) + t.Name);
            foreach (var r in t.Rules)
            {
                if (r.Memo.Length > 0) sb.AppendLine(MemoPrefix + EscapeNewlines(r.Memo));
                sb.AppendLine((r.Enabled ? "" : DisabledPrefix) + (r.IsRegex ? RegexPrefix : "") +
                              EscapeNewlines(r.From) + "=>" + EscapeNewlines(r.To) +
                              (r.Prob == 100 ? "" : "=>" + r.Prob));
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_rulesPath)); // 新配置のフォルダが無ければ作る
        File.WriteAllText(_rulesPath, sb.ToString(), new UTF8Encoding(true));
    }

    // 全タブのルールを集める。validate=trueで不正時にメッセージを出してnullを返す
    List<TabRules> CollectAllTabs(bool validate)
    {
        var tabs = new List<TabRules>();
        foreach (TabPage p in _ruleTabs.TabPages)
        {
            var t = p.Tag as RuleTab;
            if (t == null) continue; // ALLタブは保存対象外
            var rules = CollectRules(t, validate);
            if (rules == null) return null;
            tabs.Add(new TabRules { Name = t.Name, Enabled = t.Enabled, Rules = rules });
        }
        return tabs;
    }

    static int CountRules(List<TabRules> tabs)
    {
        int n = 0;
        foreach (var t in tabs) n += t.Rules.Count;
        return n;
    }

    // 有効なタブの有効なルールだけを集める (プロキシが実際に適用するもの)
    List<RuleEntry> CollectActiveRules()
    {
        var act = new List<RuleEntry>();
        var tabs = CollectAllTabs(false);
        foreach (var t in tabs)
        {
            if (!t.Enabled) continue;
            foreach (var r in t.Rules)
                if (r.Enabled && r.From.Length > 0) act.Add(r);
        }
        return act;
    }

    // 1タブのグリッドからルールを集める。validate=trueで不正時にメッセージを出してnullを返す
    List<RuleEntry> CollectRules(RuleTab tab, bool validate)
    {
        var rules = new List<RuleEntry>();
        foreach (DataGridViewRow row in tab.Grid.Rows)
        {
            if (row.IsNewRow) continue;
            object on = row.Cells[0].Value;
            bool enabled = !(on is bool) || (bool)on; // 未設定はONとみなす
            object rxc = row.Cells[1].Value;
            bool isRegex = (rxc is bool) && (bool)rxc; // 未設定はOFF (通常の文字一致)
            string probText = Convert.ToString(row.Cells[2].Value);
            string from = Convert.ToString(row.Cells[3].Value);
            string to = Convert.ToString(row.Cells[4].Value);
            string rowMemo = Convert.ToString(row.Cells[5].Value);
            if (probText == null) probText = "";
            if (from == null) from = "";
            if (to == null) to = "";
            if (rowMemo == null) rowMemo = "";
            probText = probText.Trim();
            // 先頭/末尾の改行は意図的に入れている可能性があるので、空白とタブだけ落とす
            from = from.Trim(' ', '\t');
            to = to.Trim(' ', '\t');
            rowMemo = rowMemo.Trim();
            if (from.Length == 0 && to.Length == 0) continue;
            int prob = 100; // 未入力は100 (常に置換)
            bool probOk = probText.Length == 0 ||
                          (int.TryParse(probText, out prob) && prob >= 0 && prob <= 100);
            if (!probOk) prob = 100;
            if (validate)
            {
                if (from.Length == 0)
                {
                    ShowRuleError(tab, row, "置換前が空です。");
                    return null;
                }
                if (!probOk)
                {
                    ShowRuleError(tab, row, "置換確率は0〜100の整数で入力してください。");
                    return null;
                }
                if (from.Contains("=>") || to.Contains("=>"))
                {
                    ShowRuleError(tab, row, "「=>」は使用できません。");
                    return null;
                }
                if (isRegex)
                {
                    // パターンはJSON文字列の中身検査 (CheckJsonStringSafe) の対象外。
                    // \d などJSONでは不正なエスケープが正規表現では普通に使われるため、
                    // 代わりにコンパイルできるかどうかで検証する
                    if (from.IndexOf('\n') >= 0 || from.IndexOf('\r') >= 0)
                    {
                        ShowRuleError(tab, row,
                            "正規表現の置換前にセル内改行は使えません。プロンプト中の改行に" +
                            "マッチさせるにはパターンに「\\\\n」と書いてください。");
                        return null;
                    }
                    try { new Regex(from, RegexOptions.None, TimeSpan.FromSeconds(1)); }
                    catch (ArgumentException ex)
                    {
                        ShowRuleError(tab, row, "正規表現が不正です: " + ex.Message);
                        return null;
                    }
                }
                else
                {
                    if (from.StartsWith(RegexPrefix, StringComparison.Ordinal))
                    {
                        ShowRuleError(tab, row,
                            "置換前を「regex:」で始めることはできません (ファイル上で正規表現" +
                            "ルールと区別できなくなるため)。");
                        return null;
                    }
                    string jsonErr = CheckJsonStringSafe(from);
                    if (jsonErr != null)
                    {
                        ShowRuleError(tab, row, "置換前: " + jsonErr);
                        return null;
                    }
                }
                string jsonErrTo = CheckJsonStringSafe(to);
                if (jsonErrTo != null)
                {
                    ShowRuleError(tab, row, "置換後: " + jsonErrTo);
                    return null;
                }
            }
            rules.Add(new RuleEntry
            {
                Enabled = enabled, IsRegex = isRegex, Prob = prob,
                From = from, To = to, Memo = rowMemo
            });
        }
        return rules;
    }

    // グリッド上は実際の改行で表示・編集し、ファイル上は \n (2文字) で保持する。
    // プロキシはJSONボディを生テキストのまま置換するので、ファイル側の \n が
    // JSON文字列内の改行エスケープにそのまま一致する。
    static string EscapeNewlines(string s)
    {
        return s.Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n");
    }

    static string UnescapeNewlines(string s)
    {
        return s.Replace("\\n", "\r\n");
    }

    // プロキシはLLMリクエストのJSONボディを生テキストのまま置換するので、ルールの文字列は
    // 「JSON文字列の中身」として妥当でなければならない。妥当でないと置換した瞬間にボディの
    // JSONが壊れ、ゲーム側やJSONFIXがパースに失敗する。しかも失敗しても無加工で中継される
    // ため、「なぜかルールが効かない/JSON安定化が止まる」という分かりにくい形で出る。
    // 検査はファイルに保存される形 (グリッド上の改行は \n の2文字) に対して行う。
    // 問題があればユーザー向けの説明を返し、無ければ null。
    static string CheckJsonStringSafe(string s)
    {
        string t = EscapeNewlines(s);
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '"')
                return "「\"」はJSON文字列を終端させてしまうため、そのままでは使えません (「\\\"」と書いてください)。";
            if (c < 0x20)
                return "タブなどの制御文字はJSON文字列に直接書けません (タブは「\\t」)。";
            if (c != '\\') continue;
            if (i + 1 >= t.Length)
                return "末尾の「\\」が不正なエスケープです (「\\」自体を表すには「\\\\」)。";
            char e = t[++i];
            if ("\"\\/bfnrt".IndexOf(e) >= 0) continue; // JSONで有効なエスケープ
            if (e == 'u')
            {
                if (i + 4 >= t.Length) return "「\\u」の後には16進4桁が必要です。";
                for (int k = 1; k <= 4; k++)
                    if (!Uri.IsHexDigit(t[i + k])) return "「\\u」の後には16進4桁が必要です。";
                i += 4;
                continue;
            }
            return "「\\" + e + "」はJSONで使えないエスケープです (「\\」自体を表すには「\\\\」)。";
        }
        return null;
    }

    void ShowRuleError(RuleTab tab, DataGridViewRow row, string msg)
    {
        _ruleTabs.SelectedTab = tab.Page; // 問題のあるタブを前面に出す
        MessageBox.Show(this, "タブ「" + tab.Name + "」" + (row.Index + 1) + "行目: " + msg,
            "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    readonly Random _previewRng = new Random();
}
