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
    TabControl _ruleTabs;
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
        bar.Controls.Add(MakeButton("保存", 80, delegate { SaveRules(); }));
        bar.Controls.Add(MakeButton("再読込", 80, delegate
        {
            if (ConfirmDiscardIfDirty()) LoadRules();
        }));
        bar.Controls.Add(MakeButton("タブ追加", 90, OnAddTab));
        bar.Controls.Add(MakeButton("タブ名変更", 100, OnRenameTab));
        bar.Controls.Add(MakeButton("タブ削除", 90, OnDeleteTab));
        var hint = new Label();
        hint.Text = "タブ単位のチェックで一括ON/OFF。確率は同じ置換前の中から抽選。保存で即時反映。";
        hint.AutoSize = true;
        hint.ForeColor = Color.Gray;
        hint.Margin = new Padding(10, 8, 3, 0);
        bar.Controls.Add(hint);

        // ルールはタブごとのグリッドで管理する (タブ単位で有効/無効を切替可能)
        _ruleTabs = new TabControl();
        _ruleTabs.Dock = DockStyle.Fill;

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

        page.Controls.Add(_ruleTabs);
        page.Controls.Add(prevGrp);
        page.Controls.Add(bar);
        return page;
    }

    // ルールタブ1枚分。TabPage.Tag に格納して相互参照する
    class RuleTab
    {
        public string Name;
        public bool Enabled;      // タブ単位の有効/無効
        public TabPage Page;
        public DataGridView Grid;
        public CheckBox Check;
    }

    RuleTab CreateRuleTab(string name, bool enabled)
    {
        var tab = new RuleTab();
        tab.Name = name;
        tab.Enabled = enabled;

        var page = new TabPage();
        page.Tag = tab;
        tab.Page = page;

        var check = new CheckBox();
        check.Text = "このタブのルールを有効にする";
        check.AutoSize = true;
        check.Dock = DockStyle.Top;
        check.Padding = new Padding(6, 4, 0, 2);
        check.Checked = enabled;
        check.CheckedChanged += delegate
        {
            tab.Enabled = check.Checked;
            UpdateTabText(tab);
            _dirty = true;
        };
        tab.Check = check;

        tab.Grid = CreateRuleGrid();

        page.Controls.Add(tab.Grid);
        page.Controls.Add(check);
        UpdateTabText(tab);
        return tab;
    }

    DataGridView CreateRuleGrid()
    {
        var grid = new DataGridView();
        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = true;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersWidth = 30;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        var colOn = new DataGridViewCheckBoxColumn();
        colOn.HeaderText = "有効";
        colOn.Width = 40;
        colOn.TrueValue = true;
        colOn.FalseValue = false;
        var colProb = new DataGridViewTextBoxColumn();
        colProb.HeaderText = "置換確率(%)";
        colProb.Width = 84;
        colProb.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        var colFrom = new DataGridViewTextBoxColumn();
        colFrom.HeaderText = "置換前";
        colFrom.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        colFrom.FillWeight = 50;
        var colTo = new DataGridViewTextBoxColumn();
        colTo.HeaderText = "置換後";
        colTo.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        colTo.FillWeight = 50;
        grid.Columns.Add(colOn);
        grid.Columns.Add(colProb);
        grid.Columns.Add(colFrom);
        grid.Columns.Add(colTo);
        grid.CellValueChanged += delegate { _dirty = true; };
        grid.RowsRemoved += delegate { _dirty = true; };
        // 新規行のチェックは既定でON、置換確率は100
        grid.DefaultValuesNeeded += delegate(object s, DataGridViewRowEventArgs ev)
        {
            ev.Row.Cells[0].Value = true;
            ev.Row.Cells[1].Value = "100";
        };
        // チェックボックスはクリック直後に確定させる (即_dirtyにするため)
        grid.CurrentCellDirtyStateChanged += delegate
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        return grid;
    }

    void UpdateTabText(RuleTab tab)
    {
        tab.Page.Text = (tab.Enabled ? "" : "[OFF] ") + tab.Name;
    }

    // 同名タブがあればそれを返し、無ければ新規作成して末尾に追加する
    RuleTab AddRuleTab(string name, bool enabled)
    {
        if (name.Length == 0) name = DefaultTabName;
        RuleTab found = FindRuleTab(name);
        if (found != null) return found;
        var tab = CreateRuleTab(name, enabled);
        _ruleTabs.TabPages.Add(tab.Page);
        return tab;
    }

    RuleTab FindRuleTab(string name)
    {
        foreach (TabPage p in _ruleTabs.TabPages)
        {
            var t = (RuleTab)p.Tag;
            if (t.Name == name) return t;
        }
        return null;
    }

    RuleTab CurrentRuleTab()
    {
        TabPage p = _ruleTabs.SelectedTab;
        return p == null ? null : (RuleTab)p.Tag;
    }

    void OnAddTab(object sender, EventArgs e)
    {
        string name = PromptText(this, "タブ追加", "新しいタブの名前:", "");
        if (name == null) return;
        if (name.Length == 0)
        {
            MessageBox.Show(this, "タブ名を入力してください。", "入力エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (FindRuleTab(name) != null)
        {
            MessageBox.Show(this, "同じ名前のタブがあります: " + name, "入力エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var tab = AddRuleTab(name, true);
        _ruleTabs.SelectedTab = tab.Page;
        _dirty = true;
    }

    void OnRenameTab(object sender, EventArgs e)
    {
        var tab = CurrentRuleTab();
        if (tab == null) return;
        string name = PromptText(this, "タブ名変更", "タブの名前:", tab.Name);
        if (name == null || name == tab.Name) return;
        if (name.Length == 0)
        {
            MessageBox.Show(this, "タブ名を入力してください。", "入力エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (FindRuleTab(name) != null)
        {
            MessageBox.Show(this, "同じ名前のタブがあります: " + name, "入力エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        tab.Name = name;
        UpdateTabText(tab);
        _dirty = true;
    }

    void OnDeleteTab(object sender, EventArgs e)
    {
        if (_ruleTabs.TabPages.Count <= 1)
        {
            MessageBox.Show(this, "最後のタブは削除できません。", "タブ削除",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var tab = CurrentRuleTab();
        if (tab == null) return;
        int n = tab.Grid.Rows.Count - (tab.Grid.AllowUserToAddRows ? 1 : 0);
        if (MessageBox.Show(this,
                "タブ「" + tab.Name + "」とそのルール" + n + "件を削除します。よろしいですか?",
                "タブ削除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _ruleTabs.TabPages.Remove(tab.Page);
        _dirty = true;
    }

    // 1行入力の簡易ダイアログ。キャンセル時は null
    static string PromptText(IWin32Window owner, string title, string label, string initial)
    {
        using (var dlg = new Form())
        {
            dlg.Text = title;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MinimizeBox = false;
            dlg.MaximizeBox = false;
            dlg.ShowInTaskbar = false;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.ClientSize = new Size(340, 104);
            var lbl = new Label();
            lbl.Text = label;
            lbl.AutoSize = true;
            lbl.Location = new Point(12, 12);
            var box = new TextBox();
            box.Location = new Point(12, 34);
            box.Width = 316;
            box.Text = initial;
            var ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(172, 68, 75, 26);
            var cancel = new Button();
            cancel.Text = "キャンセル";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(253, 68, 75, 26);
            dlg.Controls.Add(lbl);
            dlg.Controls.Add(box);
            dlg.Controls.Add(ok);
            dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            return dlg.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
        }
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
                var tabs = CollectAllTabs(false);
                WriteRulesFile(tabs);
                results.AppendLine("llm_replacements.txt を新規作成 (" + CountRules(tabs) + "件)");
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
        public int Prob; // 置換確率 (0-100)
        public string From;
        public string To;
    }

    // タブ1枚分の保存用データ
    class TabRules
    {
        public string Name;
        public bool Enabled;
        public List<RuleEntry> Rules;
    }

    const string DisabledPrefix = "#off:";
    const string TabPrefix = "#tab:";       // 有効なタブのセクション行
    const string OffTabPrefix = "#offtab:"; // 無効化されたタブのセクション行
    const string DefaultTabName = "標準";   // タブ行が無い旧形式ファイル用

    void LoadRules()
    {
        _ruleTabs.TabPages.Clear();
        RuleTab cur = null;
        if (File.Exists(_rulesPath))
        {
            foreach (string raw in File.ReadAllLines(_rulesPath, Encoding.UTF8))
            {
                string line = raw.Trim().TrimStart('\uFEFF').Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith(TabPrefix, StringComparison.Ordinal))
                {
                    cur = AddRuleTab(line.Substring(TabPrefix.Length).Trim(), true);
                    continue;
                }
                if (line.StartsWith(OffTabPrefix, StringComparison.Ordinal))
                {
                    cur = AddRuleTab(line.Substring(OffTabPrefix.Length).Trim(), false);
                    continue;
                }
                bool enabled = true;
                if (line.StartsWith(DisabledPrefix, StringComparison.Ordinal))
                {
                    enabled = false;
                    line = line.Substring(DisabledPrefix.Length).Trim();
                }
                else if (line.StartsWith("#", StringComparison.Ordinal)) continue;
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
                cur.Grid.Rows.Add(enabled, prob.ToString(), from, rest);
            }
        }
        if (_ruleTabs.TabPages.Count == 0) AddRuleTab(DefaultTabName, true);
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

    // 対象フォルダの llm_replacements.txt にルールを書き出す
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
        sb.AppendLine("# ・行頭 # はコメント / UTF-8で保存");
        sb.AppendLine("# ・行頭 #off: は無効化されたルール (GUIの「有効」チェックで切替)");
        sb.AppendLine("# ・変更を保存すると次のLLMリクエストから自動反映 (ゲーム再起動不要)");
        sb.AppendLine("# ・このファイルは管理GUIからも編集できます (InstantaleLlmProxy.exe)");
        sb.AppendLine("# ============================================================");
        foreach (var t in tabs)
        {
            sb.AppendLine();
            sb.AppendLine((t.Enabled ? TabPrefix : OffTabPrefix) + t.Name);
            foreach (var r in t.Rules)
                sb.AppendLine((r.Enabled ? "" : DisabledPrefix) + r.From + "=>" + r.To +
                              (r.Prob == 100 ? "" : "=>" + r.Prob));
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
            var t = (RuleTab)p.Tag;
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
            string probText = Convert.ToString(row.Cells[1].Value);
            string from = Convert.ToString(row.Cells[2].Value);
            string to = Convert.ToString(row.Cells[3].Value);
            if (probText == null) probText = "";
            if (from == null) from = "";
            if (to == null) to = "";
            probText = probText.Trim();
            from = from.Trim();
            to = to.Trim();
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
                if (from.Contains("\n") || to.Contains("\n"))
                {
                    ShowRuleError(tab, row, "改行は使用できません。");
                    return null;
                }
            }
            rules.Add(new RuleEntry { Enabled = enabled, Prob = prob, From = from, To = to });
        }
        return rules;
    }

    void ShowRuleError(RuleTab tab, DataGridViewRow row, string msg)
    {
        _ruleTabs.SelectedTab = tab.Page; // 問題のあるタブを前面に出す
        MessageBox.Show(this, "タブ「" + tab.Name + "」" + (row.Index + 1) + "行目: " + msg,
            "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    readonly Random _previewRng = new Random();

    // プロキシと同じロジック: 有効なタブの有効なルールを対象に、同一「置換前」の
    // ルールをグループ化し、確率で置換の有無と置換先を1つ選ぶ (押すたびに結果が変わりうる)
    void OnPreview(object sender, EventArgs e)
    {
        var rules = CollectActiveRules();
        string text = _prevIn.Text;

        var groups = new List<List<RuleEntry>>();
        var index = new Dictionary<string, List<RuleEntry>>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            List<RuleEntry> g;
            if (!index.TryGetValue(r.From, out g))
            {
                g = new List<RuleEntry>();
                index.Add(r.From, g);
                groups.Add(g);
            }
            g.Add(r);
        }

        foreach (var g in groups)
        {
            if (!text.Contains(g[0].From)) continue;
            int total = 0;
            foreach (var r in g) total += r.Prob;
            if (total <= 0) continue;
            // 合計100以下: 残りの確率は無置換。合計100超: 値/合計 の割合で必ずどれかに置換
            int roll = _previewRng.Next(Math.Max(total, 100));
            RuleEntry chosen = null;
            int acc = 0;
            foreach (var r in g)
            {
                acc += r.Prob;
                if (roll < acc) { chosen = r; break; }
            }
            if (chosen != null) text = text.Replace(chosen.From, chosen.To);
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
