// ----------------------------------------------------------------------------
// Gui.Core.cs (MainForm partial: 起動・ウィンドウ構築・対象フォルダ/設定ファイルの読み書き・状態表示)
// ----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

partial class MainForm : Form
{
    // ゲームが同梱する llama.cpp のバックエンド別フォルダ (bin\ の下)。
    // どれが使われるかは実行環境次第なので、適用/解除は見つかったすべてに対して行う。
    // フォルダ名にはゲーム側 llama.cpp の版数 (llama-b7054-... の b7054) が入り、
    // ゲーム更新のたびに変わるため、固定名は持たずパターンで毎回拾う。
    const string BackendPattern = "llama-*-bin-win-*";

    string _root;                 // 対象のゲームルート (GUIで切替可能)
    string _rulesPath;            // llm_replacements.txt (対象フォルダ側)
    string _logPath;              // llm_proxy.log (対象フォルダ側)
    string _settingsPath;         // llm_proxy_settings.ini (対象フォルダ側、プロキシの動作トグル用)
    string _activeLogPath;        // 実際に表示中のログ (フォールバック含む)
    readonly string _srcDir;      // src\proxy (GUI同梱のソース一式)
    readonly string _wrapperPath; // ビルド成果物 (GUI側に生成)
    readonly string _configPath;  // 前回選択した対象フォルダの記憶先

    // 対象フォルダで実際に見つかったバックエンドフォルダ (フルパス) と、対応する状態ラベル。
    // 添字で対応し、フォルダ構成が変わったら RebuildBackendRows で作り直す (null=未構築)
    List<string> _backendDirs;
    readonly List<Label> _dirStatus = new List<Label>();
    TableLayoutPanel _backendTable;
    GroupBox _statusGrp;
    TextBox _rootBox;
    Label _procStatus;
    Label _llmModeStatus;         // 状態欄の「LLM動作」行 (ローカルLLM/ラッパー翻訳/中継サーバの現在モード)
    TabControl _ruleTabs;
    TabPage _allPage;             // 全タブ横断表示 (読み取り専用・検索付き)
    DataGridView _allGrid;
    TextBox _allSearch;
    TextBox _logBox;
    CheckBox _autoRefresh;
    TextBox _prevIn, _prevOut;
    TabControl _tabs;
    Timer _timer;
    bool _dirty;
    CheckBox _schemaCompactCheck; // プロンプト圧縮 (JSONスキーマ説明のコンパクト化) のON/OFF
    bool _loadingSettings;        // 設定ロード中はCheckedChangedでの書き戻しを抑止するガード
    ToolStripMenuItem _relayStartItem, _relayStopItem, _relayStatusItem; // OpenAI互換メニュー (中継サーバの起動/停止/状態)
    ToolStripMenuItem _wrapperModeItem; // OpenAI互換メニュー (ラッパー翻訳ON/OFF = ローカルLLM⇔OpenAI互換の切替)
    int _dragTabIndex = -1;       // 置換ルールタブのドラッグ並び替え中の掴んでいるタブ添字 (-1=非ドラッグ)

    public MainForm()
    {
        // ソースとビルド成果物はGUI自身のフォルダ (MODフォルダ) の src\ 配下に固定。
        // 対象のゲームフォルダは切替可能で、bin・ルール・ログはそちらを参照する。
        string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
        _srcDir = Path.Combine(exeDir, "src", "proxy");
        _wrapperPath = Path.Combine(exeDir, "src", "llm_proxy_wrapper.exe");
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "llm_proxy_gui.ini");

        // 既定はGUIの親フォルダ。前回選択した対象フォルダが有効ならそちらを優先
        string root = Path.GetFullPath(Path.Combine(exeDir, ".."));
        if (!Directory.Exists(Path.Combine(root, "bin"))) root = exeDir;
        string[] cfgLines = null;
        try
        {
            if (File.Exists(_configPath)) cfgLines = File.ReadAllLines(_configPath, Encoding.UTF8);
        }
        catch { }
        if (cfgLines != null && cfgLines.Length > 0)
        {
            string saved = cfgLines[0].Trim();
            if (saved.Length > 0 && Directory.Exists(Path.Combine(saved, "bin"))) root = saved;
        }

        BuildUi();
        ApplySavedWindowBounds(cfgLines);
        SetRoot(root, false);

        _timer = new Timer();
        _timer.Interval = 2000;
        _timer.Tick += delegate
        {
            RefreshStatus();
            if (_autoRefresh.Checked && _tabs.SelectedIndex == 1) RefreshLog();
        };
        _timer.Start();
    }

    // ---------------------------------------------------------------- UI構築

    void BuildUi()
    {
        Text = "InstantaleLlmProxy — LLM置換プロキシ管理";
        Font = new Font("Meiryo UI", 9F);
        Size = new Size(920, 700);
        MinimumSize = new Size(720, 520);
        StartPosition = FormStartPosition.CenterScreen;

        // ---- 上段: 状態と操作 ----
        var grp = new GroupBox();
        grp.Text = "状態と操作";
        grp.Dock = DockStyle.Top;
        grp.Height = BackendGroupHeight(3); // 実際の枚数に応じて RebuildBackendRows が調整する
        grp.Padding = new Padding(10, 4, 10, 6);
        _statusGrp = grp;

        var table = new TableLayoutPanel();
        table.Dock = DockStyle.Fill;
        table.ColumnCount = 2;
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowCount = 5;

        // 対象のゲームフォルダ (切替可能)
        var rootLbl = new Label();
        rootLbl.Text = "対象フォルダ:";
        rootLbl.AutoSize = true;
        rootLbl.Margin = new Padding(3, 8, 3, 0);
        var rootPanel = new TableLayoutPanel();
        rootPanel.Dock = DockStyle.Fill;
        rootPanel.Height = 30;
        rootPanel.Margin = new Padding(0);
        rootPanel.ColumnCount = 2;
        rootPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rootPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        _rootBox = new TextBox();
        _rootBox.ReadOnly = true;
        _rootBox.Dock = DockStyle.Fill;
        _rootBox.Margin = new Padding(3, 4, 3, 0);
        var btnBrowse = MakeButton("参照...", 70, OnBrowseRoot);
        btnBrowse.Height = 26;
        btnBrowse.Margin = new Padding(3, 2, 3, 0);
        rootPanel.Controls.Add(_rootBox, 0, 0);
        rootPanel.Controls.Add(btnBrowse, 1, 0);
        table.Controls.Add(rootLbl, 0, 0);
        table.Controls.Add(rootPanel, 1, 0);

        // バックエンドの行数は対象フォルダ次第なので、専用の入れ子テーブルに分けて
        // RebuildBackendRows で作り直せるようにする (列幅は外側と揃えて見た目を保つ)
        _backendTable = new TableLayoutPanel();
        _backendTable.Dock = DockStyle.Fill;
        _backendTable.AutoSize = true;
        _backendTable.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _backendTable.Margin = new Padding(0);
        _backendTable.ColumnCount = 2;
        _backendTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        _backendTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.Controls.Add(_backendTable, 0, 1);
        table.SetColumnSpan(_backendTable, 2);

        var procName = new Label();
        procName.Text = "プロセス:";
        procName.AutoSize = true;
        procName.Margin = new Padding(3, 6, 3, 0);
        _procStatus = new Label();
        _procStatus.AutoSize = true;
        _procStatus.Margin = new Padding(3, 6, 3, 0);
        table.Controls.Add(procName, 0, 2);
        table.Controls.Add(_procStatus, 1, 2);

        // どのLLMで動く設定になっているか(ローカル/ラッパー翻訳/中継サーバ)を常時表示する。
        // メニューを開かなくてもモードの切替忘れに気づけるようにするため
        var llmModeName = new Label();
        llmModeName.Text = "LLM動作:";
        llmModeName.AutoSize = true;
        llmModeName.Margin = new Padding(3, 6, 3, 0);
        _llmModeStatus = new Label();
        _llmModeStatus.AutoSize = true;
        _llmModeStatus.Margin = new Padding(3, 6, 3, 0);
        table.Controls.Add(llmModeName, 0, 3);
        table.Controls.Add(_llmModeStatus, 1, 3);

        var buttons = new FlowLayoutPanel();
        buttons.Dock = DockStyle.Fill;
        buttons.Margin = new Padding(0, 8, 0, 0);
        var btnApply = MakeButton("MOD適用 (ビルド+差し替え)", 190, OnApply);
        var btnRevert = MakeButton("MOD解除 (元に戻す)", 150, OnRevert);
        var btnKill = MakeButton("プロセス強制終了", 130, OnKill);
        var btnRefresh = MakeButton("状態更新", 90, delegate { RefreshStatus(); });
        buttons.Controls.Add(btnApply);
        buttons.Controls.Add(btnRevert);
        buttons.Controls.Add(btnKill);
        buttons.Controls.Add(btnRefresh);

        _schemaCompactCheck = new CheckBox();
        _schemaCompactCheck.Text = "プロンプト圧縮 (JSONスキーマ説明を圧縮)";
        _schemaCompactCheck.AutoSize = true;
        _schemaCompactCheck.Checked = true;
        _schemaCompactCheck.Margin = new Padding(14, 6, 3, 0);
        _schemaCompactCheck.CheckedChanged += delegate
        {
            if (_loadingSettings) return;
            WriteSetting("schema_compact", _schemaCompactCheck.Checked);
        };
        var tip = new ToolTip();
        tip.SetToolTip(_schemaCompactCheck,
            "ONの場合、プロンプトに埋め込まれたJSONスキーマ説明(Python dict形式)を簡潔な表記へ圧縮します。\n" +
            "json_schema(grammar制約)自体は変更されないため、出力の構造的な正しさには影響しません。\n" +
            "切替は即時反映されます(ゲーム再起動不要)。設定は " + SettingsFileName + " に保存されます。");
        buttons.Controls.Add(_schemaCompactCheck);

        var note = new Label();
        note.Text = "※ラッパーはゲームが自動起動/終了します";
        note.AutoSize = true;
        note.ForeColor = Color.Gray;
        note.Margin = new Padding(10, 8, 3, 0);
        buttons.Controls.Add(note);
        table.Controls.Add(buttons, 0, 4);
        table.SetColumnSpan(buttons, 2);

        grp.Controls.Add(table);

        // ---- タブ ----
        _tabs = new TabControl();
        _tabs.Dock = DockStyle.Fill;
        _tabs.TabPages.Add(BuildRulesTab());
        _tabs.TabPages.Add(BuildLogTab());

        Controls.Add(_tabs);
        Controls.Add(grp);
        // メニューは最後に追加する。Top ドックは後に追加したものほど外側 (上端) に来るため、
        // grp より後に足すことでメニューバーが最上段に配置される。
        Controls.Add(BuildMenu());

        FormClosing += OnClosingCheckDirty;
    }

    // 上部のメニューバー。現状は「設定」メニューのみ。
    MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();
        menu.Dock = DockStyle.Top;
        menu.ShowItemToolTips = true; // 既定falseのままだと各項目の ToolTipText が表示されない

        var settings = new ToolStripMenuItem("設定(&S)");
        settings.DropDownItems.Add(new ToolStripMenuItem("機能設定...", null, OnOpenFeatureSettings));
        settings.DropDownItems.Add(new ToolStripMenuItem("デバッグ設定...", null, OnOpenDebugSettings));

        // 任意OpenAI互換サーバへの中継 (接続設定と、ゲーム側OpenAI設定用の中継サーバの起動/停止)
        var openai = new ToolStripMenuItem("OpenAI互換(&O)");
        var cfgItem = new ToolStripMenuItem("接続設定...");
        cfgItem.ToolTipText =
            "任意のOpenAI互換サーバ(OpenAI本家/LM Studio/Ollama/vLLM等)へ中継するための設定。\n" +
            "設定は " + SettingsFileName + " に保存されます。";
        cfgItem.Click += delegate { ShowOpenAiSettingsDialog(); };
        _wrapperModeItem = new ToolStripMenuItem("ラッパー翻訳を使用 (ローカルLLMの代わりに接続先を使う)");
        _wrapperModeItem.ToolTipText =
            "チェックON: ゲームのローカルLLM設定のまま、同梱llama-serverの代わりに接続先の\n" +
            "OpenAI互換サーバへ翻訳中継します。OFF: ゲーム同梱のローカルLLMで動きます(既定)。\n" +
            "接続設定(エンドポイント等)はOFFにしても保持されます。切替はゲームの再起動で反映。";
        _wrapperModeItem.Click += OnToggleWrapperMode;
        _relayStartItem = new ToolStripMenuItem("中継サーバを起動");
        _relayStartItem.ToolTipText =
            "ゲーム側のOpenAI互換設定から使う待ち受けサーバを起動します。\n" +
            "ゲームのエンドポイント欄に http://127.0.0.1:<待ち受けポート>/v1 を指定してください。";
        _relayStartItem.Click += OnRelayStart;
        _relayStopItem = new ToolStripMenuItem("中継サーバを停止");
        _relayStopItem.Click += OnRelayStop;
        _relayStatusItem = new ToolStripMenuItem("状態: 停止中");
        _relayStatusItem.Enabled = false;
        openai.DropDownItems.Add(cfgItem);
        openai.DropDownItems.Add(_wrapperModeItem);
        openai.DropDownItems.Add(new ToolStripSeparator());
        openai.DropDownItems.Add(_relayStartItem);
        openai.DropDownItems.Add(_relayStopItem);
        openai.DropDownItems.Add(new ToolStripSeparator());
        openai.DropDownItems.Add(_relayStatusItem);
        openai.DropDownOpening += delegate { UpdateRelayMenu(); };

        menu.Items.Add(settings);
        menu.Items.Add(openai);
        MainMenuStrip = menu;
        return menu;
    }

    static Button MakeButton(string text, int width, EventHandler onClick)
    {
        var b = new Button();
        b.Text = text;
        b.Width = width;
        b.Height = 28;
        b.Click += onClick;
        return b;
    }

    // ---------------------------------------------------------------- バックエンドフォルダの検出

    // bin\ 直下の llama.cpp バックエンドフォルダ (フルパス) を列挙する。
    // フォルダ名の版数部分はゲーム更新で変わるため、固定名リストは持たずここで毎回拾う
    static string[] EnumerateBackendDirs(string root)
    {
        try
        {
            string bin = Path.Combine(root, "bin");
            if (!Directory.Exists(bin)) return new string[0];
            string[] dirs = Directory.GetDirectories(bin, BackendPattern);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase); // 表示順を安定させる
            return dirs;
        }
        catch { return new string[0]; }
    }

    // 見栄えのためだけの対応表。ここに無いバックエンドも大文字化して表示するので、
    // ゲーム側が新しいバックエンドを同梱しても機能上は何もしなくてよい
    static readonly Dictionary<string, string> BackendPrettyNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "cpu", "CPU" }, { "cuda", "CUDA" }, { "vulkan", "Vulkan" }, { "hip", "HIP" },
            { "rocm", "ROCm" }, { "sycl", "SYCL" }, { "opencl", "OpenCL" }, { "musa", "MUSA" }
        };

    // フォルダ名 llama-b7054-bin-win-cuda-12.4-x64 → 表示名 "CUDA 12.4"
    static string BackendDisplayName(string dir)
    {
        string n = Path.GetFileName(dir);
        const string Mid = "-bin-win-";
        int i = n.IndexOf(Mid, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return n;
        string tail = n.Substring(i + Mid.Length); // 例 cuda-12.4-x64
        foreach (string a in new[] { "-x64", "-x86", "-arm64" })
            if (tail.EndsWith(a, StringComparison.OrdinalIgnoreCase))
            {
                tail = tail.Substring(0, tail.Length - a.Length);
                break;
            }
        if (tail.Length == 0) return n;
        string[] parts = tail.Split('-');
        string pretty;
        parts[0] = BackendPrettyNames.TryGetValue(parts[0], out pretty) ? pretty : parts[0].ToUpperInvariant();
        return string.Join(" ", parts);
    }

    static bool SameDirList(string[] a, List<string> b)
    {
        if (b == null || a.Length != b.Count) return false;
        for (int i = 0; i < a.Length; i++)
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // 検出結果に合わせて状態表示の行を作り直す。行数が変わるのでグループ枠の高さも合わせる
    void RebuildBackendRows(string[] dirs)
    {
        _backendDirs = new List<string>(dirs);
        _dirStatus.Clear();
        // Dispose は親の Controls から自分を外すため、列挙しながら呼ぶとコレクション変更例外になる。
        // 先に控えを取ってから外し、あとでまとめて破棄する
        var old = new List<Control>();
        foreach (Control c in _backendTable.Controls) old.Add(c);
        _backendTable.Controls.Clear();
        foreach (Control c in old) c.Dispose();
        _backendTable.RowCount = Math.Max(1, dirs.Length);

        if (dirs.Length == 0)
        {
            var none = new Label();
            none.Text = "バックエンドフォルダなし (bin\\" + BackendPattern + " が見つかりません)";
            none.AutoSize = true;
            none.ForeColor = Color.Gray;
            none.Margin = new Padding(3, 6, 3, 0);
            _backendTable.Controls.Add(none, 0, 0);
            _backendTable.SetColumnSpan(none, 2);
        }
        else
        {
            for (int i = 0; i < dirs.Length; i++)
            {
                var name = new Label();
                name.Text = BackendDisplayName(dirs[i]) + ":";
                name.AutoSize = true;
                name.Margin = new Padding(3, 6, 3, 0);
                var st = new Label();
                st.AutoSize = true;
                st.Margin = new Padding(3, 6, 3, 0);
                _dirStatus.Add(st);
                _backendTable.Controls.Add(name, 0, i);
                _backendTable.Controls.Add(st, 1, i);
            }
        }
        _statusGrp.Height = BackendGroupHeight(dirs.Length);
    }

    // 「状態と操作」枠の高さ。バックエンド3枚を前提にした元の高さを基準に、増減分だけ足し引きする
    static int BackendGroupHeight(int backendCount)
    {
        return 224 + (Math.Max(1, backendCount) - 3) * 22;
    }

    // ---------------------------------------------------------------- 対象フォルダ

    // 対象フォルダ内のルールファイルの場所を決める。
    // 適用時に bin 側へ書かれたマーカー (llm_proxy_dir.txt) を最優先で使い、
    // 後方互換で標準配置 (InstantaleLlmProxy\ / mod_llm_proxy\) と旧配置 (直下) も探す。
    // 見つからない場合の新規作成先は、GUI自身が対象フォルダ直下にあればそのフォルダ、なければ標準配置。
    static string ResolveRulesPath(string root)
    {
        foreach (string backend in EnumerateBackendDirs(root))
        {
            string dir = ReadMarkerDir(Path.Combine(backend, "llm_proxy_dir.txt"));
            if (dir != null) return Path.Combine(dir, "llm_replacements.txt");
        }
        string modStyle = Path.Combine(root, "InstantaleLlmProxy", "llm_replacements.txt");
        string oldModStyle = Path.Combine(root, "mod_llm_proxy", "llm_replacements.txt");
        string legacy = Path.Combine(root, "llm_replacements.txt");
        if (File.Exists(modStyle)) return modStyle;
        if (File.Exists(oldModStyle)) return oldModStyle;
        if (File.Exists(legacy)) return legacy;
        string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
        string exeParent = Path.GetDirectoryName(exeDir);
        if (exeParent != null &&
            string.Equals(
                Path.GetFullPath(exeParent).TrimEnd('\\'),
                Path.GetFullPath(root).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
            return Path.Combine(exeDir, "llm_replacements.txt");
        return modStyle;
    }

    // マーカーファイルからMODフォルダのパスを読む (GUIのUTF-8 / apply.batのANSI 両対応)
    static string ReadMarkerDir(string marker)
    {
        if (!File.Exists(marker)) return null;
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

    // 対象のゲームフォルダを切り替え、ルール/ログ/状態をすべて読み直す
    void SetRoot(string root, bool save)
    {
        _root = root;
        _rulesPath = ResolveRulesPath(root);
        _logPath = Path.Combine(Path.GetDirectoryName(_rulesPath), "llm_proxy.log");
        _settingsPath = Path.Combine(Path.GetDirectoryName(_rulesPath), SettingsFileName);
        if (_rootBox != null) _rootBox.Text = _root;
        Text = "InstantaleLlmProxy — " + _root;
        if (save) WriteConfig();
        LoadRules();
        LoadSettingsUi();
        RefreshStatus();
        RefreshLog();
    }

    // ---------------------------------------------------------------- 設定ファイル (プロキシの動作トグル)
    // llm_replacements.txt と同じフォルダに置くINI形式の設定ファイル。
    // プロキシ側 (src\proxy\*.cs) はこれを読んで動作を変える (更新時刻を見て自動再読込)。
    // schema_compact / debug_log / diag_log / openai_* を1つの名前空間で持つ。
    const string SettingsFileName = "llm_proxy_settings.ini";

    // ダンプON/OFFの旧方式 (このファイルを置くとON)。設定ファイルへ移行後も後方互換で残っている
    const string DiagOnFileName = "llm_proxy_diag_on.txt";

    // セクション見出し([Settings]等)は無視して、全キーを1つの名前空間として読む
    // (現状はプロキシ側も同じ単純化をしているので合わせてある)
    Dictionary<string, string> ReadSettingsFile()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (_settingsPath != null && File.Exists(_settingsPath))
            {
                foreach (string raw in File.ReadAllLines(_settingsPath, Encoding.UTF8))
                {
                    string line = raw.Trim().TrimStart('﻿').Trim();
                    if (line.Length == 0 ||
                        line.StartsWith("#", StringComparison.Ordinal) ||
                        line.StartsWith(";", StringComparison.Ordinal) ||
                        line.StartsWith("[", StringComparison.Ordinal)) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    dict[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
        }
        catch { }
        return dict;
    }

    bool ReadSettingBoolUi(string key, bool dflt)
    {
        string v;
        if (!ReadSettingsFile().TryGetValue(key, out v)) return dflt;
        v = v.Trim();
        if (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v, "on", StringComparison.OrdinalIgnoreCase)) return true;
        if (v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)) return false;
        return dflt;
    }

    string ReadSettingStringUi(string key, string dflt)
    {
        string v;
        return ReadSettingsFile().TryGetValue(key, out v) ? v.Trim() : dflt;
    }

    // 設定辞書をINIファイルへ書き出す(既存キーは保持済み前提)。bool/文字列トグルの共通処理。
    void SaveSettingsDict(Dictionary<string, string> dict)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; ============================================================");
        sb.AppendLine("; InstantaleLlmProxy 設定ファイル (GUIの操作で自動生成・更新)");
        sb.AppendLine("; key=value 形式。手動編集も可。保存すると次のLLMリクエストから反映(一部は要ゲーム再起動)");
        sb.AppendLine("; ・schema_compact: プロンプトに埋め込まれたJSONスキーマ説明を圧縮するか (1/0)");
        sb.AppendLine("; ・jsonfix_enabled / dedup_enabled / eventlog_trim_enabled / singleton_enabled:");
        sb.AppendLine(";   自動的な安定化・軽量化機能(JSONFIX/DEDUP/EVENTLOG/シングルトン化)のON/OFF");
        sb.AppendLine(";   (1/0・既定1。GUIの「設定」→「機能設定...」で切替)");
        sb.AppendLine("; ・log_replace / log_compact / log_dedup / log_jsonfix / log_rules / log_openai:");
        sb.AppendLine(";   llm_proxy.log に出す動作ログの項目別ON/OFF (1/0・既定1)");
        sb.AppendLine("; ・log_diag: [DIAG]診断行を出すか (1/0・調査用・既定0)");
        sb.AppendLine("; ・dump_schema / dump_prompt / dump_resp: 各ダンプ(llm_proxy_*_dump.log)を出すか");
        sb.AppendLine(";   (1/0・調査用・既定0・ログが非常に大きくなる)");
        sb.AppendLine("; ・openai_wrapper: ゲームがローカルLLM設定のとき、同梱llama-serverの代わりに接続先の");
        sb.AppendLine(";   OpenAI互換サーバへ翻訳中継するか (1/0・既定0=ローカルLLM。要ゲーム再起動)");
        sb.AppendLine("; ・openai_endpoint / openai_model / openai_api_key: 任意OpenAI互換サーバへ中継する接続設定");
        sb.AppendLine(";   (保存されているだけでは動作は変わらない。openai_wrapper か 中継サーバ起動で使われる)");
        sb.AppendLine("; ・openai_listen_port: ゲーム側のOpenAI互換設定から使う中継サーバの待ち受けポート");
        sb.AppendLine(";   (GUIメニューの「中継サーバを起動」で待ち受け開始。空なら中継サーバ無効)");
        sb.AppendLine("; ・openai_json_mode: object(既定)|schema|off (ラッパー翻訳時のみ)");
        sb.AppendLine("; ============================================================");
        sb.AppendLine("[Settings]");
        foreach (var kv in dict) sb.AppendLine(kv.Key + "=" + kv.Value);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath));
        File.WriteAllText(_settingsPath, sb.ToString(), new UTF8Encoding(true));
    }

    // 既存のキーは保持したまま(将来キーが増えても手書き分を壊さない)、指定キーだけ更新して書き戻す。
    // 保存は即時に行い、次のLLMリクエストからプロキシ側に自動反映される (ゲーム再起動不要)。
    void WriteSetting(string key, bool value)
    {
        try
        {
            var dict = ReadSettingsFile();
            dict[key] = value ? "1" : "0";
            SaveSettingsDict(dict);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "設定の保存に失敗:\n" + ex.Message +
                "\n\n対象が Program Files 配下などの場合は、GUIを管理者として実行してください。",
                "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // 対象フォルダ切替時などに、設定ファイルの内容をチェックボックスへ反映する。
    // _loadingSettings で囲むことで、反映中に CheckedChanged が誤って書き戻すのを防ぐ
    void LoadSettingsUi()
    {
        _loadingSettings = true;
        try
        {
            _schemaCompactCheck.Checked = ReadSettingBoolUi("schema_compact", true);
            // OpenAI互換の接続設定はダイアログを開いたときに読み込む (常駐コントロールを持たない)
        }
        finally { _loadingSettings = false; }
    }

    // 前回終了時のウィンドウ位置・サイズを設定ファイルの2行目から復元する。
    // 画面外に外れている場合 (モニタ構成が変わった等) は既定 (画面中央) のままにする。
    void ApplySavedWindowBounds(string[] cfgLines)
    {
        if (cfgLines == null || cfgLines.Length < 2) return;
        string[] parts = cfgLines[1].Trim().Split(',');
        if (parts.Length != 5) return;
        FormWindowState state;
        int x, y, w, h;
        if (!Enum.TryParse<FormWindowState>(parts[0], out state)) return;
        if (!int.TryParse(parts[1], out x) || !int.TryParse(parts[2], out y) ||
            !int.TryParse(parts[3], out w) || !int.TryParse(parts[4], out h)) return;
        if (w < MinimumSize.Width) w = MinimumSize.Width;
        if (h < MinimumSize.Height) h = MinimumSize.Height;
        var bounds = new Rectangle(x, y, w, h);
        bool visible = false;
        foreach (Screen s in Screen.AllScreens)
            if (s.WorkingArea.IntersectsWith(bounds)) { visible = true; break; }
        if (!visible) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        if (state == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
    }

    // 対象フォルダと現在のウィンドウ位置・サイズを設定ファイルへ保存する
    void WriteConfig()
    {
        try
        {
            Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            FormWindowState state = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
            string line2 = state + "," + b.X + "," + b.Y + "," + b.Width + "," + b.Height;
            File.WriteAllText(_configPath, _root + "\r\n" + line2, Encoding.UTF8);
        }
        catch { }
    }

    void OnBrowseRoot(object sender, EventArgs e)
    {
        if (!ConfirmDiscardIfDirty()) return;
        using (var dlg = new FolderBrowserDialog())
        {
            dlg.Description = "ゲームフォルダ (instantale.exe がある場所) を選択してください";
            dlg.SelectedPath = _root;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string sel = dlg.SelectedPath;
            if (!Directory.Exists(Path.Combine(sel, "bin")))
            {
                if (MessageBox.Show(this,
                        "bin フォルダが見つかりません。ゲームフォルダではない可能性があります。\nこのフォルダを対象にしますか?",
                        "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }
            SetRoot(sel, true);
        }
    }

    // ---------------------------------------------------------------- 状態表示

    // 2秒ごとのタイマーからも呼ばれる。
    // 適用済みかどうかは llama-server-real.exe (退避された本物) の有無で判断する:
    // 本物が退避されている = llama-server.exe はラッパーに差し替わっている、とみなせる
    void RefreshStatus()
    {
        // ゲーム更新でフォルダ名が変わったり対象フォルダを切り替えたりしても追従できるよう、
        // 毎回検出し直して構成が変わったときだけ行を作り直す
        string[] found = EnumerateBackendDirs(_root);
        if (!SameDirList(found, _backendDirs)) RebuildBackendRows(found);

        for (int i = 0; i < _backendDirs.Count && i < _dirStatus.Count; i++)
        {
            string exe = Path.Combine(_backendDirs[i], "llama-server.exe");
            string real = Path.Combine(_backendDirs[i], "llama-server-real.exe");
            if (File.Exists(real) && File.Exists(exe))
                SetStatus(_dirStatus[i], "適用中 (プロキシ有効)", Color.Green);
            else if (File.Exists(exe))
                SetStatus(_dirStatus[i], "未適用 (オリジナルのまま)", Color.DarkOrange);
            else
                SetStatus(_dirStatus[i], "異常 (llama-server.exeなし)", Color.Red);
        }

        int wrappers = 0, reals = 0;
        foreach (var p in FindTargetProcesses())
        {
            if (string.Equals(p.ProcessName, "llama-server-real", StringComparison.OrdinalIgnoreCase)) reals++;
            else wrappers++;
            p.Dispose();
        }
        if (wrappers == 0 && reals == 0)
            SetStatus(_procStatus, "停止中", Color.Gray);
        else
            SetStatus(_procStatus,
                "起動中  ラッパー: " + wrappers + "個 / 本物: " + reals + "個",
                Color.Green);

        RefreshLlmModeStatus();
    }

    // 状態欄の「LLM動作」行。設定(openai_wrapper)と中継サーバの死活から現在モードを組み立てる。
    // タイマー(2秒)ごとに呼ばれるが、読むのは小さな設定ファイルとpidファイルだけなので負荷は無視できる
    void RefreshLlmModeStatus()
    {
        if (_llmModeStatus == null) return;
        bool wrapperOn = ReadSettingBoolUi("openai_wrapper", false);
        int rpid, rport;
        bool relayRunning = ReadRelayInfo(out rpid, out rport) && RelayProcessAlive(rpid);
        string endpoint = (wrapperOn || relayRunning) ? ReadSettingStringUi("openai_endpoint", "") : "";

        if (wrapperOn && relayRunning)
            SetStatus(_llmModeStatus,
                "OpenAI互換: ラッパー翻訳ON ＋ 中継サーバ稼働中 (port " + rport + ") → " + endpoint,
                Color.RoyalBlue);
        else if (wrapperOn)
            SetStatus(_llmModeStatus,
                "OpenAI互換: ラッパー翻訳ON (ローカルLLMの代わりに " + endpoint + " を使用)",
                Color.RoyalBlue);
        else if (relayRunning)
            SetStatus(_llmModeStatus,
                "ローカルLLM ＋ 中継サーバ稼働中 (port " + rport + " → " + endpoint + " ／ ゲーム側OpenAI互換設定用)",
                Color.RoyalBlue);
        else
            SetStatus(_llmModeStatus, "ローカルLLM (ゲーム同梱)", Color.Gray);
    }

    static void SetStatus(Label l, string text, Color c)
    {
        l.Text = text;
        l.ForeColor = c;
    }

    bool ConfirmDiscardIfDirty()
    {
        if (!_dirty) return true;
        return MessageBox.Show(this, "未保存の変更があります。破棄して続行しますか?",
            "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    void OnClosingCheckDirty(object sender, FormClosingEventArgs e)
    {
        if (_dirty)
        {
            var r = MessageBox.Show(this, "置換ルールに未保存の変更があります。保存しますか?",
                "確認", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Cancel) { e.Cancel = true; return; }
            if (r == DialogResult.Yes)
            {
                SaveRules();
                if (_dirty) { e.Cancel = true; return; } // 保存失敗時は閉じない
            }
        }
        WriteConfig();
    }
}
