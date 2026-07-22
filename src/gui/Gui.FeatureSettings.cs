// ----------------------------------------------------------------------------
// Gui.FeatureSettings.cs (MainForm partial: 機能設定ダイアログ (安定化・軽量化機能のON/OFF))
// ----------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Windows.Forms;

partial class MainForm
{
    // 機能1つ分。キーは llm_proxy_settings.ini のキー名で、プロキシ側の
    // JsonFixEnabled/DedupEnabled/EventLogTrimEnabled/SingletonEnabled が同じ名前で
    // 読む (名前を変えるときは src\proxy\Proxy.Settings.cs 側も直すこと)。
    // いずれも旧版はMODフォルダに置く空ファイル (llm_proxy_*_off.txt) で無効化する方式
    // だったが、GUIから切り替えられるようini設定へ統一した (既定は全てON)
    static FeatureOpt[] FeatureOpts()
    {
        return new[]
        {
            new FeatureOpt("jsonfix_enabled", "JSON安定化 (JSONFIX)",
                "json_schemaが付いていないリクエストを検出した場合の安全網として、プロンプト内の\n" +
                "Python dict形式スキーマを本物のJSON Schemaへ変換して注入します。"),
            new FeatureOpt("dedup_enabled", "プロンプト重複ブロックの畳み込み (DEDUP)",
                "再生成のたびに重複追加されていくスキーマ指示ブロックを1個に畳み、\n" +
                "コンテキスト枯渇による500エラーを防止します。"),
            new FeatureOpt("eventlog_trim_enabled", "クエストイベント履歴ログの圧縮 (EVENTLOG)",
                "イベント判定用プロンプトに溜まり続ける古いターンの履歴を直近3ターンだけに\n" +
                "削減し、クエストが進むほどプロンプトが肥大化するのを防ぎます。"),
            new FeatureOpt("singleton_enabled", "本物 (llama-server) の多重起動抑止",
                "同時に何本も起動されがちな本体プロセスを1本に集約し、VRAM枯渇や\n" +
                "ロード遅延を防止します。OFFにすると各ラッパーが専用の本物を起動します\n" +
                "(コンテキスト分割によるcontext-exceededの切り分け用)。")
        };
    }

    class FeatureOpt
    {
        public string Key;
        public string Text;
        public string Tip;
        public CheckBox Box;
        public FeatureOpt(string key, string text, string tip) { Key = key; Text = text; Tip = tip; }
    }

    // 自動的な安定化・軽量化機能のON/OFFを切り替える。プロキシ側は設定ファイルの更新時刻を
    // 見て読み直すので、OKで保存した時点から反映される (ゲーム再起動不要)。
    void OnOpenFeatureSettings(object sender, EventArgs e)
    {
        var opts = FeatureOpts();

        using (var dlg = new Form())
        {
            dlg.Text = "機能設定";
            dlg.Font = Font;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MinimizeBox = false;
            dlg.MaximizeBox = false;
            dlg.ShowInTaskbar = false;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.ClientSize = new Size(560, 30 + opts.Length * 24 + 8);

            var g = new GroupBox();
            g.Text = "自動的な安定化・軽量化 — 既定はすべてON";
            g.SetBounds(12, 8, 536, 30 + opts.Length * 24 + 8);

            var tip = new ToolTip();
            tip.AutoPopDelay = 15000;
            for (int i = 0; i < opts.Length; i++)
            {
                var cb = new CheckBox();
                cb.Text = opts[i].Text;
                cb.AutoSize = true;
                cb.Location = new Point(14, 26 + i * 24);
                cb.Checked = ReadSettingBoolUi(opts[i].Key, true);
                tip.SetToolTip(cb, opts[i].Tip);
                opts[i].Box = cb;
                g.Controls.Add(cb);
            }

            var note = new Label();
            note.Text = "いずれもゲーム本体側の挙動への回避策です。不具合の切り分け時のみOFFにしてください。\n" +
                        "切替は即時反映されます (ゲーム再起動不要)。設定は " + SettingsFileName + " に保存されます。";
            note.ForeColor = Color.Gray;
            note.SetBounds(14, g.Bottom + 8, 536, 34);

            var ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(392, note.Bottom + 6, 75, 28);
            var cancel = new Button();
            cancel.Text = "キャンセル";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(473, note.Bottom + 6, 75, 28);

            dlg.ClientSize = new Size(560, ok.Bottom + 12);
            dlg.Controls.Add(g);
            dlg.Controls.Add(note);
            dlg.Controls.Add(ok);
            dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;

            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            SaveFeatureSettings(opts);
        }
    }

    // 全項目をまとめて1回で書き戻す (キーごとに書くと設定ファイルを何度も書き換えるため)。
    void SaveFeatureSettings(FeatureOpt[] opts)
    {
        try
        {
            var dict = ReadSettingsFile();
            foreach (var o in opts) dict[o.Key] = o.Box.Checked ? "1" : "0";
            SaveSettingsDict(dict);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "設定の保存に失敗:\n" + ex.Message +
                "\n\n対象が Program Files 配下などの場合は、GUIを管理者として実行してください。",
                "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
