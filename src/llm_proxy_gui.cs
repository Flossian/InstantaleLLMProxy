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
    // ゲームが同梱する llama.cpp のバックエンド別フォルダ (bin\ の下)。
    // どれが使われるかは実行環境次第なので、適用/解除は3つすべてに対して行う。
    // ゲーム側の llama.cpp が更新されるとフォルダ名 (b7054の部分) が変わるので、
    // 「フォルダなし」表示になったらここを更新する。BackendNamesとは添字で対応。
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
    string _settingsPath;         // llm_proxy_settings.ini (対象フォルダ側、プロキシの動作トグル用)
    string _activeLogPath;        // 実際に表示中のログ (フォールバック含む)
    readonly string _srcPath;     // llm_proxy.cs (GUI同梱のソース)
    readonly string _wrapperPath; // ビルド成果物 (GUI側に生成)
    readonly string _configPath;  // 前回選択した対象フォルダの記憶先

    readonly Label[] _dirStatus = new Label[BackendDirs.Length];
    TextBox _rootBox;
    Label _procStatus;
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
    ToolStripMenuItem _debugLogItem; // 設定メニュー: デバッグログを出力するか
    bool _loadingSettings;        // 設定ロード中はCheckedChangedでの書き戻しを抑止するガード
    int _dragTabIndex = -1;       // 置換ルールタブのドラッグ並び替え中の掴んでいるタブ添字 (-1=非ドラッグ)

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

        var settings = new ToolStripMenuItem("設定(&S)");

        _debugLogItem = new ToolStripMenuItem("デバッグログを出力する");
        _debugLogItem.CheckOnClick = true;
        _debugLogItem.Checked = true;
        _debugLogItem.ToolTipText =
            "ONの場合、置換・変換の詳細 ([REPLACE]/[SKIP]/[JSONFIX]/[DEDUP]/[COMPACT] 等) を\n" +
            "llm_proxy.log に記録します。OFFにすると起動・エラー等の重要ログだけになり、\n" +
            "ログが肥大しません。切替は即時反映されます (ゲーム再起動不要)。\n" +
            "設定は " + SettingsFileName + " に保存されます。";
        _debugLogItem.Click += delegate
        {
            if (_loadingSettings) return;
            WriteSetting("debug_log", _debugLogItem.Checked);
        };
        settings.DropDownItems.Add(_debugLogItem);

        menu.Items.Add(settings);
        MainMenuStrip = menu;
        return menu;
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
        hint.Text = "タブ単位のチェックで一括ON/OFF。タブ見出しはドラッグで並び替え。確率は同じ置換前の中から抽選。保存で即時反映。セル内改行は Shift+Enter。";
        hint.AutoSize = true;
        hint.ForeColor = Color.Gray;
        hint.Margin = new Padding(10, 8, 3, 0);
        bar.Controls.Add(hint);

        // ルールはタブごとのグリッドで管理する (タブ単位で有効/無効を切替可能)
        _ruleTabs = new TabControl();
        _ruleTabs.Dock = DockStyle.Fill;
        // 先頭に全タブ横断表示の「すべて」タブを固定で置く
        _allPage = BuildAllPage();
        _ruleTabs.TabPages.Add(_allPage);
        _ruleTabs.SelectedIndexChanged += delegate
        {
            if (_ruleTabs.SelectedTab == _allPage) RefreshAllGrid();
        };
        // タブ見出しのドラッグで実タブを並び替える (「すべて」タブは先頭固定で動かさない)
        _ruleTabs.MouseDown += OnRuleTabsMouseDown;
        _ruleTabs.MouseMove += OnRuleTabsMouseMove;
        _ruleTabs.MouseUp += delegate { _dragTabIndex = -1; };

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

    // ---- 「すべて」タブ (全タブ横断表示・検索・ダブルクリックでジャンプ) ----

    // ALLグリッドの各行が、どのタブのどの行に対応するかを覚えておく
    class AllRowRef
    {
        public RuleTab Tab;
        public DataGridViewRow Row;
    }

    TabPage BuildAllPage()
    {
        var page = new TabPage("すべて"); // Tag=null が「ALLタブ」の目印 (実タブはTagにRuleTab)

        var top = new FlowLayoutPanel();
        top.Dock = DockStyle.Top;
        top.Height = 32;
        var lbl = new Label();
        lbl.Text = "検索:";
        lbl.AutoSize = true;
        lbl.Margin = new Padding(6, 8, 3, 0);
        _allSearch = new TextBox();
        _allSearch.Width = 240;
        _allSearch.Margin = new Padding(3, 4, 3, 0);
        _allSearch.TextChanged += delegate { RefreshAllGrid(); };
        var clr = MakeButton("クリア", 60, delegate { _allSearch.Text = ""; });
        var hint = new Label();
        hint.Text = "全タブのルールを横断表示 (タブ名・置換前・置換後で検索)。行をダブルクリックすると該当タブの該当行へ移動。ここでは編集できません。";
        hint.AutoSize = true;
        hint.ForeColor = Color.Gray;
        hint.Margin = new Padding(10, 8, 3, 0);
        top.Controls.Add(lbl);
        top.Controls.Add(_allSearch);
        top.Controls.Add(clr);
        top.Controls.Add(hint);

        _allGrid = new DataGridView();
        _allGrid.Dock = DockStyle.Fill;
        _allGrid.ReadOnly = true;
        _allGrid.AllowUserToAddRows = false;
        _allGrid.AllowUserToDeleteRows = false;
        _allGrid.AllowUserToResizeRows = false;
        _allGrid.RowHeadersVisible = false;
        _allGrid.MultiSelect = false;
        _allGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _allGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _allGrid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        var cTab = new DataGridViewTextBoxColumn();
        cTab.HeaderText = "タブ";
        cTab.Width = 120;
        var cOn = new DataGridViewTextBoxColumn();
        cOn.HeaderText = "有効";
        cOn.Width = 40;
        cOn.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        var cProb = new DataGridViewTextBoxColumn();
        cProb.HeaderText = "置換確率(%)";
        cProb.Width = 84;
        cProb.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        var cFrom = new DataGridViewTextBoxColumn();
        cFrom.HeaderText = "置換前";
        cFrom.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        cFrom.FillWeight = 50;
        var cTo = new DataGridViewTextBoxColumn();
        cTo.HeaderText = "置換後";
        cTo.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        cTo.FillWeight = 50;
        _allGrid.Columns.Add(cTab);
        _allGrid.Columns.Add(cOn);
        _allGrid.Columns.Add(cProb);
        _allGrid.Columns.Add(cFrom);
        _allGrid.Columns.Add(cTo);
        _allGrid.CellDoubleClick += OnAllGridDoubleClick;

        page.Controls.Add(_allGrid);
        page.Controls.Add(top);
        return page;
    }

    // ALLグリッドを全タブの現在の内容から作り直す (検索語で絞り込み)
    void RefreshAllGrid()
    {
        if (_allGrid == null) return;
        string q = (_allSearch.Text ?? "").Trim();
        _allGrid.Rows.Clear();
        foreach (TabPage p in _ruleTabs.TabPages)
        {
            var t = p.Tag as RuleTab;
            if (t == null) continue; // ALLタブ自身は除外
            foreach (DataGridViewRow row in t.Grid.Rows)
            {
                if (row.IsNewRow) continue;
                string from = Convert.ToString(row.Cells[2].Value) ?? "";
                string to = Convert.ToString(row.Cells[3].Value) ?? "";
                if (from.Trim().Length == 0 && to.Trim().Length == 0) continue;
                if (q.Length > 0
                    && from.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && to.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && t.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                object on = row.Cells[0].Value;
                bool ruleOn = !(on is bool) || (bool)on;
                string prob = Convert.ToString(row.Cells[1].Value) ?? "";
                int idx = _allGrid.Rows.Add(t.Name, ruleOn ? "✓" : "", prob, from, to);
                var ar = _allGrid.Rows[idx];
                ar.Tag = new AllRowRef { Tab = t, Row = row };
                // タブOFF もしくは ルールOFF は灰色で「効いていない」ことを示す
                if (!t.Enabled || !ruleOn) ar.DefaultCellStyle.ForeColor = Color.Gray;
            }
        }
    }

    void OnAllGridDoubleClick(object sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var ar = _allGrid.Rows[e.RowIndex].Tag as AllRowRef;
        if (ar == null) return;
        _ruleTabs.SelectedTab = ar.Tab.Page; // 該当タブを前面に
        try
        {
            var g = ar.Tab.Grid;
            g.ClearSelection();
            if (!ar.Row.IsNewRow && ar.Row.Index >= 0)
            {
                int col = Math.Min(2, g.ColumnCount - 1); // 置換前セルにフォーカス
                g.CurrentCell = ar.Row.Cells[col];
                ar.Row.Selected = true;
                g.FirstDisplayedScrollingRowIndex = ar.Row.Index;
                g.Focus();
            }
        }
        catch { }
    }

    // ルール編集グリッドを1枚作る。列の並びは
    //   0=有効(チェック) / 1=置換確率(%) / 2=置換前 / 3=置換後
    // で固定。CollectRules や RefreshAllGrid はこの添字を直接使うので、
    // 列を増減・入れ替えする場合はそちらも合わせて直すこと。
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
            var t = p.Tag as RuleTab; // ALLタブは Tag=null なので除外される
            if (t != null && t.Name == name) return t;
        }
        return null;
    }

    // 実タブ (ALLタブを除く) の枚数
    int RealTabCount()
    {
        int n = 0;
        foreach (TabPage p in _ruleTabs.TabPages)
            if (p.Tag is RuleTab) n++;
        return n;
    }

    // 現在選択中の実タブ。ALLタブ選択中や未選択なら null
    RuleTab CurrentRuleTab()
    {
        TabPage p = _ruleTabs.SelectedTab;
        return p == null ? null : p.Tag as RuleTab;
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
        if (tab == null)
        {
            MessageBox.Show(this, "「すべて」タブは名前変更できません。編集したい実タブを選んでください。",
                "タブ名変更", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
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
        var tab = CurrentRuleTab();
        if (tab == null)
        {
            MessageBox.Show(this, "「すべて」タブは削除できません。削除したい実タブを選んでください。",
                "タブ削除", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (RealTabCount() <= 1)
        {
            MessageBox.Show(this, "最後のタブは削除できません。", "タブ削除",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        int n = tab.Grid.Rows.Count - (tab.Grid.AllowUserToAddRows ? 1 : 0);
        if (MessageBox.Show(this,
                "タブ「" + tab.Name + "」とそのルール" + n + "件を削除します。よろしいですか?",
                "タブ削除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        _ruleTabs.TabPages.Remove(tab.Page);
        _dirty = true;
    }

    // ---- タブ見出しのドラッグ並び替え ----
    // 実タブ同士だけ入れ替える。先頭の「すべて」タブ (index 0) は掴めず、そこへも割り込ませない。
    // 並び順はファイル保存時のタブ順にそのまま反映される (WriteRulesFile が TabPages 順に書く)

    void OnRuleTabsMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int i = TabIndexAt(e.Location);
        _dragTabIndex = (i >= 1) ? i : -1; // ALLタブ(0)や見出し外は掴まない
    }

    void OnRuleTabsMouseMove(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _dragTabIndex < 1) return;
        int over = TabIndexAt(e.Location);
        if (over < 1) return;            // ALLタブより前へは移動させない
        if (over == _dragTabIndex) return;
        var page = _ruleTabs.TabPages[_dragTabIndex];
        _ruleTabs.TabPages.Remove(page);
        _ruleTabs.TabPages.Insert(over, page);
        _ruleTabs.SelectedTab = page;
        _dragTabIndex = over;
        _dirty = true;
    }

    // 指定座標にあるタブ見出しの添字。見出しの上でなければ -1
    int TabIndexAt(Point pt)
    {
        for (int i = 0; i < _ruleTabs.TabCount; i++)
            if (_ruleTabs.GetTabRect(i).Contains(pt)) return i;
        return -1;
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
    // プロキシ側 (llm_proxy.cs) はリクエストのたびにこれを読んで動作を変える。
    // 現状は schema_compact (プロンプト圧縮) の1項目のみだが、増えても対応できる形にしてある。
    const string SettingsFileName = "llm_proxy_settings.ini";

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

    // 既存のキーは保持したまま(将来キーが増えても手書き分を壊さない)、指定キーだけ更新して書き戻す。
    // 保存は即時に行い、次のLLMリクエストからプロキシ側に自動反映される (ゲーム再起動不要)。
    void WriteSetting(string key, bool value)
    {
        try
        {
            var dict = ReadSettingsFile();
            dict[key] = value ? "1" : "0";
            var sb = new StringBuilder();
            sb.AppendLine("; ============================================================");
            sb.AppendLine("; InstantaleLlmProxy 設定ファイル (GUIの操作で自動生成・更新)");
            sb.AppendLine("; key=value 形式 (1/0)。手動編集も可。保存すると次のLLMリクエストから反映");
            sb.AppendLine("; ・schema_compact: プロンプトに埋め込まれたJSONスキーマ説明を圧縮するか");
            sb.AppendLine("; ・debug_log: 置換/変換の詳細ログ([REPLACE]等)を llm_proxy.log に出すか");
            sb.AppendLine("; ============================================================");
            sb.AppendLine("[Settings]");
            foreach (var kv in dict) sb.AppendLine(kv.Key + "=" + kv.Value);
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath));
            File.WriteAllText(_settingsPath, sb.ToString(), new UTF8Encoding(true));
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
            if (_debugLogItem != null) _debugLogItem.Checked = ReadSettingBoolUi("debug_log", true);
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

    // ラッパーをビルドし、各バックエンドの llama-server.exe と差し替える。
    // exeを掴んだままのプロセスがあると差し替えに失敗するので、先に終了させる。
    // 本物の退避は初回だけ (2回目以降にmoveすると、退避先がラッパーで上書きされて本物を失う)
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

    // ラッパーを消して、退避しておいた本物を llama-server.exe の名前に戻す。
    // これでゲームは何も知らずにオリジナルのサーバを起動するようになる
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

    // llm_proxy.cs をその場でコンパイルする。
    // cscはWindows同梱の .NET Framework 4.x のものを直接叩くので、
    // 開発環境やSDKのインストールは不要 (ユーザのPCでもそのままビルドできる)
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

    // グリッド1行分のルール。Enabled=false は「#off:」付きでファイルに保存され、
    // プロキシ側からはコメント行として無視される (プロキシの変更不要)
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

    // ルールファイルを読んでタブとグリッドを作り直す。
    // ファイルが無い/読めない場合も空のタブ1枚で始められるようにする
    void LoadRules()
    {
        _ruleTabs.TabPages.Clear();
        _ruleTabs.TabPages.Add(_allPage); // 「すべて」タブは常に先頭に残す
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
                cur.Grid.Rows.Add(enabled, prob.ToString(),
                    UnescapeNewlines(from), UnescapeNewlines(rest));
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
        sb.AppendLine("# ・変更を保存すると次のLLMリクエストから自動反映 (ゲーム再起動不要)");
        sb.AppendLine("# ・このファイルは管理GUIからも編集できます (InstantaleLlmProxy.exe)");
        sb.AppendLine("# ============================================================");
        foreach (var t in tabs)
        {
            sb.AppendLine();
            sb.AppendLine((t.Enabled ? TabPrefix : OffTabPrefix) + t.Name);
            foreach (var r in t.Rules)
                sb.AppendLine((r.Enabled ? "" : DisabledPrefix) +
                              EscapeNewlines(r.From) + "=>" + EscapeNewlines(r.To) +
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
            string probText = Convert.ToString(row.Cells[1].Value);
            string from = Convert.ToString(row.Cells[2].Value);
            string to = Convert.ToString(row.Cells[3].Value);
            if (probText == null) probText = "";
            if (from == null) from = "";
            if (to == null) to = "";
            probText = probText.Trim();
            // 先頭/末尾の改行は意図的に入れている可能性があるので、空白とタブだけ落とす
            from = from.Trim(' ', '\t');
            to = to.Trim(' ', '\t');
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
            }
            rules.Add(new RuleEntry { Enabled = enabled, Prob = prob, From = from, To = to });
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
                if (_dirty) { e.Cancel = true; return; } // 保存失敗時は閉じない
            }
        }
        WriteConfig();
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
