// ----------------------------------------------------------------------------
// Gui.LogTab.cs (MainForm partial: ログタブの表示・更新・クリア)
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
    TabPage BuildLogTab()
    {
        var page = new TabPage("ログ (llm_proxy.log)");

        var bar = new FlowLayoutPanel();
        bar.Dock = DockStyle.Top;
        bar.Height = 34;
        bar.Controls.Add(MakeButton("更新", 90, delegate { RefreshLog(); }));
        _autoRefresh = new CheckBox();
        _autoRefresh.Text = "自動更新 (2秒)";
        _autoRefresh.AutoSize = true;
        _autoRefresh.Checked = true;
        _autoRefresh.Margin = new Padding(10, 6, 3, 0);
        bar.Controls.Add(_autoRefresh);
        bar.Controls.Add(MakeButton("ログクリア", 90, OnClearLog));

        _logBox = new TextBox();
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Both;
        _logBox.WordWrap = false;
        _logBox.Dock = DockStyle.Fill;
        _logBox.Font = new Font("Consolas", 9F);
        _logBox.BackColor = Color.White;

        page.Controls.Add(_logBox);
        page.Controls.Add(bar);
        return page;
    }

    void RefreshLog()
    {
        try
        {
            // ラッパーの実際の出力先を探す: 新配置 → 旧配置 → %LOCALAPPDATA% (書込不可時のフォールバック)
            string path = _logPath;
            if (!File.Exists(path))
            {
                var cands = new[]
                {
                    Path.Combine(_root, "InstantaleLlmProxy", "llm_proxy.log"),
                    Path.Combine(_root, "mod_llm_proxy", "llm_proxy.log"),
                    Path.Combine(_root, "llm_proxy.log"),
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "llm_proxy.log")
                };
                foreach (string c in cands)
                {
                    if (File.Exists(c)) { path = c; break; }
                }
            }
            _activeLogPath = path;
            if (!File.Exists(path))
            {
                _logBox.Text = "(ログファイルはまだありません: " + _logPath + ")";
                return;
            }
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                // 大きい場合は末尾200KBだけ表示
                if (fs.Length > 200 * 1024) fs.Seek(-200 * 1024, SeekOrigin.End);
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    text = sr.ReadToEnd();
            }
            if (!string.Equals(path, _logPath, StringComparison.OrdinalIgnoreCase))
                text = "※フォールバック先を表示中: " + path + "\r\n" + text;
            if (_logBox.Text != text)
            {
                _logBox.Text = text;
                // 末尾へスクロール
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
        }
        catch (Exception ex)
        {
            _logBox.Text = "(ログ読み込みエラー: " + ex.Message + ")";
        }
    }

    void OnClearLog(object sender, EventArgs e)
    {
        if (MessageBox.Show(this, "llm_proxy.log を削除します。よろしいですか?",
                "ログクリア", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        try
        {
            if (_activeLogPath != null && File.Exists(_activeLogPath)) File.Delete(_activeLogPath);
            RefreshLog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "削除に失敗 (ラッパー起動中は削除できない場合があります):\n" + ex.Message,
                "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
