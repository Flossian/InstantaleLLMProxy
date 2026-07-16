// ----------------------------------------------------------------------------
// Gui.OpenAiRelay.cs (MainForm partial: OpenAI互換 中継サーバの設定ダイアログ・起動/停止管理)
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
    // ---------------------------------------------------------------- OpenAI互換 (メニュー)
    // 設定は llm_proxy_settings.ini の openai_* キー。使い方は2通り:
    //  (1) ラッパー翻訳: MOD適用済みでゲームがローカルLLMの場合。openai_wrapper=1 のとき、
    //      ラッパーがローカルの代わりに外部へ翻訳中継する (待ち受けポート不要)。
    //      接続設定(エンドポイント/モデル名)が保存されているだけでは作動しない: 設定値を
    //      保持したままローカルLLM⇔OpenAI互換を切り替えられるようにするため (既定はローカル)。
    //  (2) スタンドアロン中継: ゲーム側のOpenAI互換設定を使う場合。「中継サーバを起動」で
    //      待ち受けを開始し、ゲームのエンドポイント欄に http://127.0.0.1:<ポート>/v1 を指定する。
    //      モデル名/APIキーはゲーム側の指定が優先され、空のときだけここの値で補われる。
    //      中継サーバは明示的に起動したときだけ動く (起動していなければゲームのローカルLLM
    //      設定に影響しない)

    // メニューのチェック項目で ローカルLLM⇔ラッパー翻訳 を切り替える。
    // ONにするには接続設定(エンドポイント/モデル名)が必要なので、不足時はダイアログへ誘導する
    void OnToggleWrapperMode(object sender, EventArgs e)
    {
        bool turnOn = !ReadSettingBoolUi("openai_wrapper", false);
        if (turnOn &&
            (ReadSettingStringUi("openai_endpoint", "").Length == 0 ||
             ReadSettingStringUi("openai_model", "").Length == 0))
        {
            MessageBox.Show(this,
                "ラッパー翻訳を有効にするには、先に「接続設定」で エンドポイント と モデル名 を設定してください。",
                "設定が必要", MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowOpenAiSettingsDialog();
            return;
        }
        try
        {
            var dict = ReadSettingsFile();
            dict["openai_wrapper"] = turnOn ? "1" : "0";
            SaveSettingsDict(dict);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "設定の保存に失敗:\n" + ex.Message +
                "\n\n対象が Program Files 配下などの場合は、GUIを管理者として実行してください。",
                "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        UpdateRelayMenu();
        MessageBox.Show(this,
            turnOn
                ? "ラッパー翻訳を有効にしました。\nゲームのローカルLLM設定のまま、接続先のOpenAI互換サーバを使います。\n反映にはゲーム(llama-server)の起動し直しが必要です。"
                : "ラッパー翻訳を無効にしました。\nゲーム同梱のローカルLLMで動きます (接続設定は保持されています)。\n反映にはゲーム(llama-server)の起動し直しが必要です。",
            "切替", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // メニュー「OpenAI互換 > 接続設定...」のモーダルダイアログ
    void ShowOpenAiSettingsDialog()
    {
        using (var dlg = new Form())
        {
            dlg.Text = "OpenAI互換 接続設定";
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MaximizeBox = false;
            dlg.MinimizeBox = false;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.ClientSize = new Size(620, 528);
            dlg.Font = Font;
            var tip = new ToolTip();

            // ---- 動作モード: ゲームがローカルLLM設定のとき、どちらのLLMを使うか ----
            var modeGrp = new GroupBox();
            modeGrp.Text = "動作モード (ゲームがローカルLLM設定のとき)";
            modeGrp.SetBounds(12, 8, 596, 78);

            var rbLocal = new RadioButton();
            rbLocal.Text = "ローカルLLMを使う (ゲーム同梱の llama-server) ※既定";
            rbLocal.AutoSize = true;
            rbLocal.Location = new Point(14, 22);

            var rbWrapper = new RadioButton();
            rbWrapper.Text = "OpenAI互換サーバへ翻訳中継する (下の接続先を使用。ラッパー翻訳)";
            rbWrapper.AutoSize = true;
            rbWrapper.Location = new Point(14, 48);
            tip.SetToolTip(rbWrapper,
                "ゲームのローカルLLM設定のまま、同梱llama-serverの代わりに接続先のOpenAI互換サーバを\n" +
                "使います。エンドポイントとモデル名が必須です。切替はゲームの再起動で反映されます。");

            if (ReadSettingBoolUi("openai_wrapper", false)) rbWrapper.Checked = true;
            else rbLocal.Checked = true;

            modeGrp.Controls.Add(rbLocal);
            modeGrp.Controls.Add(rbWrapper);

            // ---- 送信: この中継サーバ → 接続先 ----
            var sendGrp = new GroupBox();
            sendGrp.Text = "接続先 (プロキシ → OpenAI互換サーバ)";
            sendGrp.SetBounds(12, 94, 596, 186);

            var endLbl = new Label();
            endLbl.Text = "エンドポイント:";
            endLbl.AutoSize = true;
            endLbl.Location = new Point(12, 30);
            var endBox = new TextBox();
            endBox.SetBounds(150, 26, 430, 23);
            endBox.Text = ReadSettingStringUi("openai_endpoint", "");
            tip.SetToolTip(endBox,
                "例: https://api.openai.com/v1  または  http://127.0.0.1:1234/v1\n" +
                "OpenAI本家に限らず LM Studio / Ollama / vLLM 等のOpenAI互換サーバを指定できます。\n" +
                "(/v1 まで。/chat/completions は付けても付けなくても可)");

            var modelLbl = new Label();
            modelLbl.Text = "モデル名:";
            modelLbl.AutoSize = true;
            modelLbl.Location = new Point(12, 62);
            var modelBox = new TextBox();
            modelBox.SetBounds(150, 58, 280, 23);
            modelBox.Text = ReadSettingStringUi("openai_model", "");
            tip.SetToolTip(modelBox,
                "例: gpt-4o-mini\n" +
                "ゲーム側(またはリクエスト)でモデル名が指定された場合はそちらを優先し、\n" +
                "空のときだけこの値を使います。ラッパー翻訳(MOD適用)ではこの値が必須です。");

            var keyLbl = new Label();
            keyLbl.Text = "APIキー (任意):";
            keyLbl.AutoSize = true;
            keyLbl.Location = new Point(12, 94);
            var keyBox = new TextBox();
            keyBox.SetBounds(150, 90, 340, 23);
            keyBox.UseSystemPasswordChar = true;
            keyBox.Text = ReadSettingStringUi("openai_api_key", "");
            tip.SetToolTip(keyBox,
                "APIキーが必要なサービスのみ入力 (設定ファイルに平文で保存されます)。\n" +
                "空の場合、ゲーム側がAPIキーを送ってくればそれをそのまま引き継ぎます。");
            var showKey = new CheckBox();
            showKey.Text = "表示";
            showKey.AutoSize = true;
            showKey.Location = new Point(500, 92);
            showKey.CheckedChanged += delegate { keyBox.UseSystemPasswordChar = !showKey.Checked; };

            var jmLbl = new Label();
            jmLbl.Text = "JSON安定化:";
            jmLbl.AutoSize = true;
            jmLbl.Location = new Point(12, 126);
            var jmCombo = new ComboBox();
            jmCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            jmCombo.Items.AddRange(new object[] { "object", "schema", "off" });
            jmCombo.SetBounds(150, 122, 130, 23);
            int jmIdx = jmCombo.Items.IndexOf(ReadSettingStringUi("openai_json_mode", "object"));
            jmCombo.SelectedIndex = jmIdx >= 0 ? jmIdx : 0;
            tip.SetToolTip(jmCombo,
                "ラッパー翻訳(MOD適用)時、スキーマ指示のあるリクエストに response_format を付ける方式。\n" +
                "object: 妥当なJSONを必ず返す(最も広く互換。既定) / schema: スキーマを厳密適用(対応サーバのみ)\n" +
                "/ off: 付けない。中継サーバ経由(ゲーム側OpenAI設定)ではリクエストを変更しません。");

            var sendNote = new Label();
            sendNote.Text = "接続設定は保存され、上の動作モードを「ローカルLLM」に戻しても消えません。\n" +
                            "翻訳中継モードにはエンドポイントとモデル名の両方が必要です。";
            sendNote.AutoSize = true;
            sendNote.ForeColor = Color.Gray;
            sendNote.Location = new Point(12, 152);

            sendGrp.Controls.Add(endLbl);
            sendGrp.Controls.Add(endBox);
            sendGrp.Controls.Add(modelLbl);
            sendGrp.Controls.Add(modelBox);
            sendGrp.Controls.Add(keyLbl);
            sendGrp.Controls.Add(keyBox);
            sendGrp.Controls.Add(showKey);
            sendGrp.Controls.Add(jmLbl);
            sendGrp.Controls.Add(jmCombo);
            sendGrp.Controls.Add(sendNote);

            // ---- 受信: ゲーム → この中継サーバ ----
            var recvGrp = new GroupBox();
            recvGrp.Text = "待ち受け (ゲーム → プロキシ)  ※ゲーム側のOpenAI互換設定を使う場合のみ";
            recvGrp.SetBounds(12, 288, 596, 152);

            var portLbl = new Label();
            portLbl.Text = "待ち受けポート:";
            portLbl.AutoSize = true;
            portLbl.Location = new Point(12, 30);
            var portBox = new TextBox();
            portBox.SetBounds(150, 26, 90, 23);
            portBox.Text = ReadSettingStringUi("openai_listen_port", "");
            tip.SetToolTip(portBox, "例: 8181。空にすると中継サーバは使えません(ラッパー翻訳のみ)。");

            var recvHint = new Label();
            recvHint.AutoSize = false;
            recvHint.SetBounds(12, 58, 570, 84);
            EventHandler updHint = delegate
            {
                string p = portBox.Text.Trim();
                recvHint.Text =
                    "メニューの「中継サーバを起動」で待ち受けを開始した後、ゲーム内のOpenAI互換設定に\n" +
                    "  エンドポイント:  http://127.0.0.1:" + (p.Length > 0 ? p : "<ポート>") + "/v1\n" +
                    "を指定してください。ゲーム側で入力したモデル名/APIキーはそのまま接続先へ引き継がれ、\n" +
                    "置換ルール(llm_replacements.txt)はこの中継でも適用されます。";
            };
            portBox.TextChanged += updHint;
            updHint(null, EventArgs.Empty);

            recvGrp.Controls.Add(portLbl);
            recvGrp.Controls.Add(portBox);
            recvGrp.Controls.Add(recvHint);

            var applyNote = new Label();
            applyNote.Text = "※変更の反映: 動作モードとラッパー翻訳はゲーム(llama-server)の起動し直し、中継サーバは再起動が必要です。";
            applyNote.AutoSize = true;
            applyNote.ForeColor = Color.Gray;
            applyNote.Location = new Point(12, 450);

            var btnSave = MakeButton("保存", 100, null);
            btnSave.SetBounds(410, 486, 100, 30);
            var btnCancel = MakeButton("キャンセル", 100, null);
            btnCancel.SetBounds(516, 486, 92, 30);
            btnCancel.Click += delegate { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            btnSave.Click += delegate
            {
                string portText = portBox.Text.Trim();
                int lp;
                if (portText.Length > 0 && (!int.TryParse(portText, out lp) || lp <= 0 || lp > 65535))
                {
                    MessageBox.Show(dlg, "待ち受けポートは 1〜65535 の数値か、空(中継サーバを使わない)にしてください。",
                        "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (rbWrapper.Checked &&
                    (endBox.Text.Trim().Length == 0 || modelBox.Text.Trim().Length == 0))
                {
                    MessageBox.Show(dlg,
                        "「OpenAI互換サーバへ翻訳中継する」には エンドポイント と モデル名 の両方が必要です。\n" +
                        "入力するか、動作モードを「ローカルLLMを使う」に戻してください。",
                        "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try
                {
                    var dict = ReadSettingsFile();
                    dict["openai_wrapper"] = rbWrapper.Checked ? "1" : "0";
                    dict["openai_endpoint"] = endBox.Text.Trim();
                    dict["openai_model"] = modelBox.Text.Trim();
                    dict["openai_api_key"] = keyBox.Text.Trim();
                    dict["openai_listen_port"] = portText;
                    dict["openai_json_mode"] = jmCombo.SelectedItem == null
                        ? "object" : jmCombo.SelectedItem.ToString();
                    SaveSettingsDict(dict);
                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dlg, "設定の保存に失敗:\n" + ex.Message +
                        "\n\n対象が Program Files 配下などの場合は、GUIを管理者として実行してください。",
                        "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            dlg.Controls.Add(modeGrp);
            dlg.Controls.Add(sendGrp);
            dlg.Controls.Add(recvGrp);
            dlg.Controls.Add(applyNote);
            dlg.Controls.Add(btnSave);
            dlg.Controls.Add(btnCancel);
            dlg.AcceptButton = btnSave;
            dlg.CancelButton = btnCancel;
            dlg.ShowDialog(this);
            UpdateRelayMenu();
        }
    }

    // ---- スタンドアロン中継サーバ (llm_proxy_wrapper.exe --openai-relay) の起動/停止 ----
    // 状態はプロキシが起動時に書く llm_proxy_relay.txt (pid,port) で判定する。
    // GUIの子プロセスにはしないので、GUIを閉じても中継サーバは動き続ける (停止はメニューから)。

    string RelayPidPath()
    {
        return Path.Combine(Path.GetDirectoryName(_rulesPath), "llm_proxy_relay.txt");
    }

    bool ReadRelayInfo(out int pid, out int port)
    {
        pid = 0;
        port = 0;
        try
        {
            string p = RelayPidPath();
            if (!File.Exists(p)) return false;
            string[] parts = File.ReadAllText(p).Trim().Split(',');
            return parts.Length >= 2 && int.TryParse(parts[0].Trim(), out pid) &&
                   int.TryParse(parts[1].Trim(), out port);
        }
        catch { return false; }
    }

    // PID再利用で無関係なプロセスを中継サーバと誤認しないよう、実行ファイル名まで確かめる
    static bool RelayProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using (var p = Process.GetProcessById(pid))
            {
                if (p.HasExited) return false;
                return p.ProcessName.StartsWith("llm_proxy_wrapper", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { return false; }
    }

    // メニューを開くたびに状態を確認して チェック/有効状態と状態表示を更新する
    void UpdateRelayMenu()
    {
        int pid, port;
        bool running = ReadRelayInfo(out pid, out port) && RelayProcessAlive(pid);
        _relayStartItem.Enabled = !running;
        _relayStopItem.Enabled = running;
        bool wrapperOn = ReadSettingBoolUi("openai_wrapper", false);
        _wrapperModeItem.Checked = wrapperOn;
        _relayStatusItem.Text =
            (wrapperOn ? "ローカルLLM設定時: 接続先へ翻訳中継" : "ローカルLLM設定時: ゲーム同梱LLM") +
            " ／ 中継サーバ: " + (running ? "稼働中 (port " + port + ")" : "停止中");
    }

    void OnRelayStart(object sender, EventArgs e)
    {
        string endpoint = ReadSettingStringUi("openai_endpoint", "");
        int listenPort;
        int.TryParse(ReadSettingStringUi("openai_listen_port", ""), out listenPort);
        if (endpoint.Length == 0 || listenPort <= 0)
        {
            MessageBox.Show(this,
                "先に「OpenAI互換 > 接続設定」で 接続先エンドポイント と 待ち受けポート を設定してください。",
                "設定が必要", MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowOpenAiSettingsDialog();
            return;
        }
        int pid, port;
        if (ReadRelayInfo(out pid, out port) && RelayProcessAlive(pid))
        {
            MessageBox.Show(this, "中継サーバは既に稼働中です (port " + port + ")。",
                "確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string cscOut;
        if (!BuildWrapper(out cscOut))
        {
            MessageBox.Show(this, "中継サーバのビルドに失敗しました:\n\n" + cscOut, "ビルドエラー",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        try
        {
            // 中継サーバが設定/ルール/ログの場所(MODフォルダ)を見つけられるようマーカーを書く
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(_wrapperPath), "llm_proxy_dir.txt"),
                Path.GetDirectoryName(_rulesPath), new UTF8Encoding(true));
            try { File.Delete(RelayPidPath()); } catch { }
            var psi = new ProcessStartInfo(_wrapperPath, "--openai-relay");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "中継サーバの起動に失敗:\n" + ex.Message, "エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // 起動確認: プロキシがpidファイルを書き、プロセスが生きていれば成功
        bool ok = false;
        for (int i = 0; i < 20 && !ok; i++)
        {
            System.Threading.Thread.Sleep(150);
            ok = ReadRelayInfo(out pid, out port) && RelayProcessAlive(pid);
        }
        if (ok)
            MessageBox.Show(this,
                "中継サーバを起動しました (port " + listenPort + ")。\n\n" +
                "ゲーム内のOpenAI互換設定のエンドポイントに\n" +
                "  http://127.0.0.1:" + listenPort + "/v1\n" +
                "を指定してください。GUIを閉じても中継サーバは動き続けます (停止はこのメニューから)。",
                "起動", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else
            MessageBox.Show(this,
                "起動を確認できませんでした。ログタブで [FATAL] を確認してください\n" +
                "(ポートが他プロセスに使われている場合など)。",
                "起動失敗の可能性", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        RefreshLog();
    }

    void OnRelayStop(object sender, EventArgs e)
    {
        int pid, port;
        if (!ReadRelayInfo(out pid, out port) || !RelayProcessAlive(pid))
        {
            try { File.Delete(RelayPidPath()); } catch { }
            MessageBox.Show(this, "中継サーバは稼働していません。", "確認",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            using (var p = Process.GetProcessById(pid))
            {
                p.Kill();
                p.WaitForExit(3000);
            }
        }
        catch { }
        try { File.Delete(RelayPidPath()); } catch { }
        MessageBox.Show(this, "中継サーバを停止しました。", "停止",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
