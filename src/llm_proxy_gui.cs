// ============================================================================
// llm_proxy_gui.cs — InstantaleLlmProxy 管理GUI
//
// 配置: MODフォルダ直下に InstantaleLlmProxy.exe、ソース類は src\ 配下。
//       ユーザが操作するのはこのGUI (InstantaleLlmProxy.exe) だけでよい。
//
// 機能:
//   ・置換ルール (llm_replacements.txt) のグリッド編集と置換テスト
//   ・MODの適用(ラッパーのビルド+差し替え)と解除(本物の復元)
//   ・ラッパー/本物プロセスの状態表示と強制終了
//   ・llm_proxy.log の閲覧
//
// 注意: ラッパー(llama-server.exe)はゲームが起動時に自動で立ち上げ、
//       ゲーム終了時に終了させる。GUIから行うのは有効化/無効化と強制終了。
//
// ビルド: src\llm_proxy_gui.bat 参照
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

class MainForm : Form
{
    static readonly string[] BackendDirs =
    {
        "llama-b7054-bin-win-cpu-x64",
        "llama-b7054-bin-win-cuda-12.4-x64",
        "llama-b7054-bin-win-vulkan-x64"
    };
    static readonly string[] BackendNames = { "CPU", "CUDA", "Vulkan" };

    string _root;                 // 対象のゲームルート (GUIで切替可能)
    string _rulesPath;            // llm_replacements.txt (対象フォルダ側)
    string _logPath;              // llm_proxy.log (対象フォルダ側)
    string _activeLogPath;        // 実際に表示中のログ (フォールバック含む)
    readonly string _srcPath;     // llm_proxy.cs (GUI同梱のソース)
    readonly string _wrapperPath; // ビルド成果物 (GUI側に生成)
    readonly string _configPath;  // 前回選択した対象フォルダの記憶先

    readonly Label[] _dirStatus = new Label[BackendDirs.Length];
    TextBox _rootBox;
    Label _procStatus;
    DataGridView _grid;
    TextBox _logBox;
    CheckBox _autoRefresh;
    TextBox _prevIn, _prevOut;
    TabControl _tabs;
    Timer _timer;
    bool _dirty;

    public MainForm()
    {
        // ソースとビルド成果物はGUI自身のフォルダ (MODフォルダ) の src\ 配下に固定。
        // 対象のゲームフォルダは切替可能で、bin・ルール・ログはそちらを参照する。
        string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
        _srcPath = Path.Combine(exeDir, "src", "llm_proxy.cs");
        _wrapperPath = Path.Combine(exeDir, "src", "llm_proxy_wrapper.exe");
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "llm_proxy_gui.ini");

        // 既定はGUIの親フォルダ。前回選択した対象フォルダが有効ならそちらを優先
        string root = Path.GetFullPath(Path.Combine(exeDir, ".."));
        if (!Directory.Exists(Path.Combine(root, "bin"))) root = exeDir;
        try
        {
            if (File.Exists(_configPath))
            {
                string saved = File.ReadAllText(_configPath, Encoding.UTF8).Trim();
                if (saved.Length > 0 && Directory.Exists(Path.Combine(saved, "bin"))) root = saved;
            }
        }
        catch { }

        BuildUi();
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
        grp.Height = 200;
        grp.Padding = new Padding(10, 4, 10, 6);

        var table = new TableLayoutPanel();
        table.Dock = DockStyle.Fill;
        table.ColumnCount = 2;
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowCount = BackendDirs.Length + 3;

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

        for (int i = 0; i < BackendDirs.Length; i++)
        {
            var name = new Label();
            name.Text = BackendNames[i] + ":";
            name.AutoSize = true;
            name.Margin = new Padding(3, 6, 3, 0);
            var st = new Label();
            st.AutoSize = true;
            st.Margin = new Padding(3, 6, 3, 0);
            _dirStatus[i] = st;
            table.Controls.Add(name, 0, i + 1);
            table.Controls.Add(st, 1, i + 1);
        }

        var procName = new Label();
        procName.Text = "プロセス:";
        procName.AutoSize = true;
        procName.Margin = new Padding(3, 6, 3, 0);
        _procStatus = new Label();
        _procStatus.AutoSize = true;
        _procStatus.Margin = new Padding(3, 6, 3, 0);
        table.Controls.Add(procName, 0, BackendDirs.Length + 1);
        table.Controls.Add(_procStatus, 1, BackendDirs.Length + 1);

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
        var note = new Label();
        note.Text = "※ラッパーはゲームが自動起動/終了します";
        note.AutoSize = true;
        note.ForeColor = Color.Gray;
        note.Margin = new Padding(10, 8, 3, 0);
        buttons.Controls.Add(note);
        table.Controls.Add(buttons, 0, BackendDirs.Length + 2);
        table.SetColumnSpan(buttons, 2);

        grp.Controls.Add(table);

        // ---- タブ ----
        _tabs = new TabControl();
        _tabs.Dock = DockStyle.Fill;
        _tabs.TabPages.Add(BuildRulesTab());
        _tabs.TabPages.Add(BuildLogTab());

        Controls.Add(_tabs);
        Controls.Add(grp);

        FormClosing += OnClosingCheckDirty;
    }

    TabPage BuildRulesTab()
    {
        var page = new TabPage("置換ルール");

        var bar = new FlowLayoutPanel();
        bar.Dock = DockStyle.Top;
        bar.Height = 34;
        bar.Controls.Add(MakeButton("保存", 90, delegate { SaveRules(); }));
        bar.Controls.Add(MakeButton("再読込", 90, delegate
        {
            if (ConfirmDiscardIfDirty()) LoadRules();
        }));
        var hint = new Label();
        hint.Text = "1行=1ルール。チェックを外すと無効化。保存すると起動中のプロキシにも即時反映されます。";
        hint.AutoSize = true;
        hint.ForeColor = Color.Gray;
        hint.Margin = new Padding(10, 8, 3, 0);
        bar.Controls.Add(hint);

        _grid = new DataGridView();
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = true;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersWidth = 30;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        var colOn = new DataGridViewCheckBoxColumn();
        colOn.HeaderText = "有効";
        colOn.Width = 44;
        colOn.TrueValue = true;
        colOn.FalseValue = false;
        var colFrom = new DataGridViewTextBoxColumn();
        colFrom.HeaderText = "置換前";
        colFrom.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        colFrom.FillWeight = 50;
        var colTo = new DataGridViewTextBoxColumn();
        colTo.HeaderText = "置換後";
        colTo.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        colTo.FillWeight = 50;
        _grid.Columns.Add(colOn);
        _grid.Columns.Add(colFrom);
        _grid.Columns.Add(colTo);
        _grid.CellValueChanged += delegate { _dirty = true; };
        _grid.RowsRemoved += delegate { _dirty = true; };
        // 新規行のチェックは既定でON
        _grid.DefaultValuesNeeded += delegate(object s, DataGridViewRowEventArgs ev)
        {
            ev.Row.Cells[0].Value = true;
        };
        // チェックボックスはクリック直後に確定させる (即_dirtyにするため)
        _grid.CurrentCellDirtyStateChanged += delegate
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        // ---- 置換テスト ----
        var prevGrp = new GroupBox();
        prevGrp.Text = "置換テスト (上のルールを貼り付けたテキストに適用して確認)";
        prevGrp.Dock = DockStyle.Bottom;
        prevGrp.Height = 170;
        prevGrp.Padding = new Padding(8);

        var prevTable = new TableLayoutPanel();
        prevTable.Dock = DockStyle.Fill;
        prevTable.ColumnCount = 3;
        prevTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        prevTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        prevTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _prevIn = new TextBox();
        _prevIn.Multiline = true;
        _prevIn.ScrollBars = ScrollBars.Vertical;
        _prevIn.Dock = DockStyle.Fill;
        _prevIn.Text = "あなたはダークファンタジーRPGのキャラクター生成AIだ。";

        var btnPrev = MakeButton("置換 →", 80, OnPreview);
        btnPrev.Anchor = AnchorStyles.None;

        _prevOut = new TextBox();
        _prevOut.Multiline = true;
        _prevOut.ScrollBars = ScrollBars.Vertical;
        _prevOut.ReadOnly = true;
        _prevOut.Dock = DockStyle.Fill;

        prevTable.Controls.Add(_prevIn, 0, 0);
        prevTable.Controls.Add(btnPrev, 1, 0);
        prevTable.Controls.Add(_prevOut, 2, 0);
        prevGrp.Controls.Add(prevTable);

        page.Controls.Add(_grid);
        page.Controls.Add(prevGrp);
        page.Controls.Add(bar);
        return page;
    }

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

    static Button MakeButton(string text, int width, EventHandler onClick)
    {
        var b = new Button();
        b.Text = text;
        b.Width = width;
        b.Height = 28;
        b.Click += onClick;
        return b;
    }

    // ---------------------------------------------------------------- 対象フォルダ

    // 対象フォルダ内のルールファイルの場所を決める。
    // 適用時に bin 側へ書かれたマーカー (llm_proxy_dir.txt) を最優先で使い、
    // 後方互換で標準配置 (InstantaleLlmProxy\ / mod_llm_proxy\) と旧配置 (直下) も探す。
    // 見つからない場合の新規作成先は、GUI自身が対象フォルダ直下にあればそのフォルダ、なければ標準配置。
    static string ResolveRulesPath(string root)
    {
        for (int i = 0; i < BackendDirs.Length; i++)
        {
            string marker = Path.Combine(root, "bin", BackendDirs[i], "llm_proxy_dir.txt");
            string dir = ReadMarkerDir(marker);
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
        if (_rootBox != null) _rootBox.Text = _root;
        Text = "InstantaleLlmProxy — " + _root;
        if (save)
        {
            try { File.WriteAllText(_configPath, _root, Encoding.UTF8); } catch { }
        }
        LoadRules();
        RefreshStatus();
        RefreshLog();
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

    void RefreshStatus()
    {
        for (int i = 0; i < BackendDirs.Length; i++)
        {
            string dir = Path.Combine(_root, "bin", BackendDirs[i]);
            string exe = Path.Combine(dir, "llama-server.exe");
            string real = Path.Combine(dir, "llama-server-real.exe");
            if (!Directory.Exists(dir))
            {
                SetStatus(_dirStatus[i], "フォルダなし", Color.Gray);
            }
            else if (File.Exists(real) && File.Exists(exe))
            {
                SetStatus(_dirStatus[i], "適用中 (プロキシ有効)", Color.Green);
            }
            else if (File.Exists(exe))
            {
                SetStatus(_dirStatus[i], "未適用 (オリジナルのまま)", Color.DarkOrange);
            }
            else
            {
                SetStatus(_dirStatus[i], "異常 (llama-server.exeなし)", Color.Red);
            }
        }

        int wrappers = Process.GetProcessesByName("llama-server").Length;
        int reals = Process.GetProcessesByName("llama-server-real").Length;
        if (wrappers == 0 && reals == 0)
            SetStatus(_procStatus, "停止中", Color.Gray);
        else
            SetStatus(_procStatus,
                "起動中  ラッパー: " + wrappers + "個 / 本物: " + reals + "個",
                Color.Green);
    }

    static void SetStatus(Label l, string text, Color c)
    {
        l.Text = text;
        l.ForeColor = c;
    }

    // ---------------------------------------------------------------- 適用/解除/終了

    void OnApply(object sender, EventArgs e)
    {
        if (!EnsureNoProcess("MODを適用")) return;
        if (!File.Exists(_srcPath))
        {
            MessageBox.Show(this, "ソースが見つかりません:\n" + _srcPath, "エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Cursor = Cursors.WaitCursor;
        try
        {
            string cscOut;
            if (!BuildWrapper(out cscOut))
            {
                MessageBox.Show(this, "ラッパーのビルドに失敗しました:\n\n" + cscOut, "ビルドエラー",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var results = new StringBuilder();
            for (int i = 0; i < BackendDirs.Length; i++)
            {
                string dir = Path.Combine(_root, "bin", BackendDirs[i]);
                string exe = Path.Combine(dir, "llama-server.exe");
                string real = Path.Combine(dir, "llama-server-real.exe");
                if (!Directory.Exists(dir) || !File.Exists(exe))
                {
                    results.AppendLine(BackendNames[i] + ": スキップ");
                    continue;
                }
                if (!File.Exists(real)) File.Move(exe, real); // 初回のみ本物を退避
                File.Copy(_wrapperPath, exe, true);
                // ラッパーがMODフォルダ(ルール/ログの場所)を確実に特定できるよう記録する
                File.WriteAllText(Path.Combine(dir, "llm_proxy_dir.txt"),
                    Path.GetDirectoryName(_rulesPath), Encoding.UTF8);
                results.AppendLine(BackendNames[i] + ": 適用OK");
            }
            // 対象フォルダにルールファイルが無いとラッパーは何も置換しないので、グリッドの内容で作成する
            if (!File.Exists(_rulesPath))
            {
                var rules = CollectRules(false);
                WriteRulesFile(rules);
                results.AppendLine("llm_replacements.txt を新規作成 (" + rules.Count + "件)");
            }
            MessageBox.Show(this, results.ToString(), "MOD適用 完了",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "適用中にエラー:\n" + ex.Message +
                "\n\n対象が Program Files 配下などの場合は、GUIを管理者として実行してください。",
                "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
            RefreshStatus();
        }
    }

    void OnRevert(object sender, EventArgs e)
    {
        if (!EnsureNoProcess("MODを解除")) return;
        if (MessageBox.Show(this, "プロキシを外してオリジナルの llama-server.exe に戻します。よろしいですか?",
                "MOD解除", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        try
        {
            var results = new StringBuilder();
            for (int i = 0; i < BackendDirs.Length; i++)
            {
                string dir = Path.Combine(_root, "bin", BackendDirs[i]);
                string exe = Path.Combine(dir, "llama-server.exe");
                string real = Path.Combine(dir, "llama-server-real.exe");
                string marker = Path.Combine(dir, "llm_proxy_dir.txt");
                if (File.Exists(marker)) File.Delete(marker);
                if (!File.Exists(real))
                {
                    results.AppendLine(BackendNames[i] + ": 未適用");
                    continue;
                }
                if (File.Exists(exe)) File.Delete(exe); // ラッパーを削除
                File.Move(real, exe);
                results.AppendLine(BackendNames[i] + ": 復元OK");
            }
            MessageBox.Show(this, results.ToString(), "MOD解除 完了",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "解除中にエラー:\n" + ex.Message +
                "\n\n対象が Program Files 配下などの場合は、GUIを管理者として実行してください。",
                "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            RefreshStatus();
        }
    }

    void OnKill(object sender, EventArgs e)
    {
        if (MessageBox.Show(this,
                "llama-server / llama-server-real のプロセスを強制終了します。\n" +
                "(ゲームプレイ中に実行するとLLM機能がエラーになります)\nよろしいですか?",
                "プロセス強制終了", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        KillAll();
        RefreshStatus();
    }

    static void KillAll()
    {
        foreach (string name in new[] { "llama-server", "llama-server-real" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); p.WaitForExit(3000); }
                catch { }
            }
        }
    }

    // 実行中プロセスがあれば確認して止める。falseなら操作中断
    bool EnsureNoProcess(string action)
    {
        int n = Process.GetProcessesByName("llama-server").Length
              + Process.GetProcessesByName("llama-server-real").Length;
        if (n == 0) return true;
        var r = MessageBox.Show(this,
            "llama-server が起動中です。" + action + "するには終了が必要です。\n強制終了しますか?",
            "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r != DialogResult.Yes) return false;
        KillAll();
        return true;
    }

    bool BuildWrapper(out string output)
    {
        string csc = Path.Combine(
            Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
            @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
        var psi = new ProcessStartInfo(csc,
            "/nologo /optimize /codepage:65001 /out:\"" + _wrapperPath + "\" \"" + _srcPath + "\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using (var p = Process.Start(psi))
        {
            string so = p.StandardOutput.ReadToEnd();
            string se = p.StandardError.ReadToEnd();
            p.WaitForExit();
            output = (so + "\n" + se).Trim();
            return p.ExitCode == 0;
        }
    }

    // ---------------------------------------------------------------- ルール編集

    // \u30B0\u30EA\u30C3\u30C91\u884C\u5206\u306E\u30EB\u30FC\u30EB\u3002Enabled=false \u306F\u300C#off:\u300D\u4ED8\u304D\u3067\u30D5\u30A1\u30A4\u30EB\u306B\u4FDD\u5B58\u3055\u308C\u3001
    // \u30D7\u30ED\u30AD\u30B7\u5074\u304B\u3089\u306F\u30B3\u30E1\u30F3\u30C8\u884C\u3068\u3057\u3066\u7121\u8996\u3055\u308C\u308B (\u30D7\u30ED\u30AD\u30B7\u306E\u5909\u66F4\u4E0D\u8981)
    class RuleEntry
    {
        public bool Enabled;
        public string From;
        public string To;
    }

    const string DisabledPrefix = "#off:";

    void LoadRules()
    {
        _grid.Rows.Clear();
        if (File.Exists(_rulesPath))
        {
            foreach (string raw in File.ReadAllLines(_rulesPath, Encoding.UTF8))
            {
                string line = raw.Trim().TrimStart('\uFEFF').Trim();
                if (line.Length == 0) continue;
                bool enabled = true;
                if (line.StartsWith(DisabledPrefix, StringComparison.Ordinal))
                {
                    enabled = false;
                    line = line.Substring(DisabledPrefix.Length).Trim();
                }
                else if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                int idx = line.IndexOf("=>", StringComparison.Ordinal);
                if (idx <= 0) continue;
                _grid.Rows.Add(enabled, line.Substring(0, idx), line.Substring(idx + 2));
            }
        }
        _dirty = false;
    }

    void SaveRules()
    {
        var rules = CollectRules(true);
        if (rules == null) return; // バリデーションエラー

        try
        {
            WriteRulesFile(rules);
            _dirty = false;
            MessageBox.Show(this,
                rules.Count + "件のルールを保存しました。\n保存先: " + _rulesPath +
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

    // 対象フォルダの llm_replacements.txt にルールを書き出す
    void WriteRulesFile(List<RuleEntry> rules)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ============================================================");
        sb.AppendLine("# LLMリクエスト置換ルール (llm_proxy用)");
        sb.AppendLine("# ・1行1ルール:  置換前=>置換後");
        sb.AppendLine("# ・行頭 # はコメント / UTF-8で保存");
        sb.AppendLine("# ・行頭 #off: は無効化されたルール (GUIの「有効」チェックで切替)");
        sb.AppendLine("# ・変更を保存すると次のLLMリクエストから自動反映 (ゲーム再起動不要)");
        sb.AppendLine("# ・このファイルは管理GUIからも編集できます (InstantaleLlmProxy.exe)");
        sb.AppendLine("# ============================================================");
        sb.AppendLine();
        foreach (var r in rules)
            sb.AppendLine((r.Enabled ? "" : DisabledPrefix) + r.From + "=>" + r.To);
        Directory.CreateDirectory(Path.GetDirectoryName(_rulesPath)); // 新配置のフォルダが無ければ作る
        File.WriteAllText(_rulesPath, sb.ToString(), new UTF8Encoding(true));
    }

    // グリッドからルールを集める。validate=trueで不正時にメッセージを出してnullを返す
    List<RuleEntry> CollectRules(bool validate)
    {
        var rules = new List<RuleEntry>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            object on = row.Cells[0].Value;
            bool enabled = !(on is bool) || (bool)on; // 未設定はONとみなす
            string from = Convert.ToString(row.Cells[1].Value);
            string to = Convert.ToString(row.Cells[2].Value);
            if (from == null) from = "";
            if (to == null) to = "";
            from = from.Trim();
            to = to.Trim();
            if (from.Length == 0 && to.Length == 0) continue;
            if (validate)
            {
                if (from.Length == 0)
                {
                    MessageBox.Show(this, (row.Index + 1) + "行目: 置換前が空です。", "入力エラー",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }
                if (from.Contains("=>") || to.Contains("=>"))
                {
                    MessageBox.Show(this, (row.Index + 1) + "行目: 「=>」は使用できません。", "入力エラー",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }
                if (from.Contains("\n") || to.Contains("\n"))
                {
                    MessageBox.Show(this, (row.Index + 1) + "行目: 改行は使用できません。", "入力エラー",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }
            }
            rules.Add(new RuleEntry { Enabled = enabled, From = from, To = to });
        }
        return rules;
    }

    void OnPreview(object sender, EventArgs e)
    {
        var rules = CollectRules(false);
        string text = _prevIn.Text;
        foreach (var r in rules)
        {
            if (r.Enabled && r.From.Length > 0) text = text.Replace(r.From, r.To);
        }
        _prevOut.Text = text;
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
                if (_dirty) e.Cancel = true; // 保存失敗時は閉じない
            }
        }
    }

    // ---------------------------------------------------------------- ログ

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
