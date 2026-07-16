// ----------------------------------------------------------------------------
// Gui.DebugSettings.cs (MainForm partial: デバッグ設定ダイアログ (ログ項目別ON/OFF))
// ----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

partial class MainForm
{
    // ログ項目1つ分。キーは llm_proxy_settings.ini のキー名で、プロキシ側の
    // LogEnabled/DiagEnabled が同じ名前で読む (名前を変えるときは両方直すこと)
    class LogOpt
    {
        public string Key;
        public string Text;
        public CheckBox Box;
        public LogOpt(string key, string text) { Key = key; Text = text; }
    }

    // llm_proxy.log の1リクエスト単位の詳細ログ (既定ON)
    static LogOpt[] LightLogOpts()
    {
        return new[]
        {
            new LogOpt("log_replace", "置換 ([REPLACE] / [SKIP])"),
            new LogOpt("log_compact", "プロンプト圧縮 ([COMPACT])"),
            new LogOpt("log_dedup",   "重複ブロックの畳み込み ([DEDUP])"),
            new LogOpt("log_jsonfix", "JSON安定化 ([JSONFIX])"),
            new LogOpt("log_rules",   "ルールファイルの読込 ([RULES])"),
            new LogOpt("log_openai",  "OpenAI互換サーバへの中継 ([OPENAI])")
        };
    }

    // 調査用の診断ログとダンプ (既定OFF・ログが非常に大きくなる)
    static LogOpt[] DiagLogOpts()
    {
        return new[]
        {
            new LogOpt("log_diag",    "診断行 ([DIAG] 受信キー・トークン数・重複検出)"),
            new LogOpt("dump_schema", "JSONスキーマのダンプ (llm_proxy_schema_dump.log)"),
            new LogOpt("dump_prompt", "プロンプト全文のダンプ (llm_proxy_prompt_dump.log)"),
            new LogOpt("dump_resp",   "応答のダンプ (llm_proxy_resp_dump.log)")
        };
    }

    // 項目ごとにログのON/OFFを切り替える。プロキシ側は設定ファイルの更新時刻を見て
    // 読み直すので、OKで保存した時点から反映される (ゲーム再起動不要)。
    void OnOpenDebugSettings(object sender, EventArgs e)
    {
        var light = LightLogOpts();
        var diag = DiagLogOpts();
        // 項目キーがまだ無い場合の既定値は、項目別になる前の一括スイッチを引き継ぐ
        // (プロキシ側の LogEnabled/DiagEnabled と同じ判定にして表示と挙動を一致させる)
        bool lightDflt = ReadSettingBoolUi("debug_log", true);
        bool diagDflt = LegacyDiagFileOn() || ReadSettingBoolUi("diag_log", false);

        using (var dlg = new Form())
        {
            dlg.Text = "デバッグ設定";
            dlg.Font = Font;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MinimizeBox = false;
            dlg.MaximizeBox = false;
            dlg.ShowInTaskbar = false;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.ClientSize = new Size(590, 424);

            var g1 = new GroupBox();
            g1.Text = "動作ログ (llm_proxy.log) — 既定はすべてON";
            g1.SetBounds(12, 8, 566, 30 + light.Length * 24 + 8);
            AddLogChecks(g1, light, lightDflt);

            var g2 = new GroupBox();
            g2.Text = "調査用の診断ログ・ダンプ — 既定はすべてOFF";
            g2.SetBounds(12, g1.Bottom + 10, 566, 30 + diag.Length * 24 + 30);
            AddLogChecks(g2, diag, diagDflt);
            var warn = new Label();
            warn.Text = "※ ログが非常に大きくなります。不具合の調査時だけONにしてください。";
            warn.ForeColor = Color.FromArgb(180, 80, 0);
            warn.SetBounds(14, 30 + diag.Length * 24 + 4, 540, 18);
            g2.Controls.Add(warn);

            var note = new Label();
            note.Text = "起動・エラー等の重要ログ ([BOOT]/[ERROR] 等) は常に記録されます。\n" +
                        "切替は即時反映されます (ゲーム再起動不要)。設定は " + SettingsFileName + " に保存されます。";
            note.ForeColor = Color.Gray;
            note.SetBounds(14, g2.Bottom + 8, 566, 34);

            var ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(422, note.Bottom + 6, 75, 28);
            var cancel = new Button();
            cancel.Text = "キャンセル";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(503, note.Bottom + 6, 75, 28);

            dlg.ClientSize = new Size(590, ok.Bottom + 12);
            dlg.Controls.Add(g1);
            dlg.Controls.Add(g2);
            dlg.Controls.Add(note);
            dlg.Controls.Add(ok);
            dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;

            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            SaveDebugSettings(light, diag);
        }
    }

    // グループ内に項目のチェックボックスを縦に並べ、現在値を反映する
    void AddLogChecks(GroupBox g, LogOpt[] opts, bool dflt)
    {
        for (int i = 0; i < opts.Length; i++)
        {
            var cb = new CheckBox();
            cb.Text = opts[i].Text;
            cb.AutoSize = true;
            cb.Location = new Point(14, 26 + i * 24);
            cb.Checked = ReadSettingBoolUi(opts[i].Key, dflt);
            opts[i].Box = cb;
            g.Controls.Add(cb);
        }
    }

    // 全項目をまとめて1回で書き戻す (キーごとに書くと設定ファイルを何度も書き換えるため)。
    // 項目キーを明示的に書くので、移行元の一括スイッチ (debug_log/diag_log) は用済みとして消す。
    void SaveDebugSettings(LogOpt[] light, LogOpt[] diag)
    {
        try
        {
            var dict = ReadSettingsFile();
            bool allDiagOn = true;
            foreach (var o in light) dict[o.Key] = o.Box.Checked ? "1" : "0";
            foreach (var o in diag)
            {
                dict[o.Key] = o.Box.Checked ? "1" : "0";
                if (!o.Box.Checked) allDiagOn = false;
            }
            dict.Remove("debug_log");
            dict.Remove("diag_log");
            SaveSettingsDict(dict);
            RemoveLegacyDiagFileIfNeeded(allDiagOn);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "設定の保存に失敗:\n" + ex.Message +
                "\n\n対象が Program Files 配下などの場合は、GUIを管理者として実行してください。",
                "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // 旧方式のフラグファイルがあるか (プロキシ側の LegacyDiagFileOn と同じ判定)
    bool LegacyDiagFileOn()
    {
        try { return File.Exists(Path.Combine(Path.GetDirectoryName(_settingsPath), DiagOnFileName)); }
        catch { return false; }
    }

    // 旧方式の llm_proxy_diag_on.txt は「置けば診断ログ・ダンプを全部ON」の上書きスイッチなので、
    // 残っているとGUIでOFFにした項目が止まらない。全部ONにした場合以外は併せて消す。
    // 消せなかった場合は黙って効かないより知らせる
    void RemoveLegacyDiagFileIfNeeded(bool allDiagOn)
    {
        if (allDiagOn) return;
        string p = Path.Combine(Path.GetDirectoryName(_settingsPath), DiagOnFileName);
        try
        {
            if (File.Exists(p)) File.Delete(p);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "旧方式のフラグファイルを削除できませんでした:\n" + p + "\n" + ex.Message +
                "\n\nこのファイルが残っている間は診断ログ・ダンプがすべてONのままになります。手動で削除してください。",
                "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
