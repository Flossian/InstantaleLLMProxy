// ----------------------------------------------------------------------------
// Gui.RulesTab.cs (MainForm partial: 置換ルールタブのUI構築・タブ管理・置換テスト)
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
        hint.Text = "タブ単位のチェックで一括ON/OFF。タブ見出しはドラッグで並び替え。確率は同じ置換前の中から抽選。保存で即時反映。セル内改行は Shift+Enter。「正規表現」ONの行はパターン一致 (置換後で $1 参照可)。";
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
        hint.Text = "全タブのルールを横断表示 (タブ名・置換前・置換後・メモで検索)。行をダブルクリックすると該当タブの該当行へ移動。ここでは編集できません。";
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
        var cRx = new DataGridViewTextBoxColumn();
        cRx.HeaderText = "正規表現";
        cRx.Width = 64;
        cRx.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
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
        var cMemo = new DataGridViewTextBoxColumn();
        cMemo.HeaderText = "メモ";
        cMemo.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        cMemo.FillWeight = 25;
        _allGrid.Columns.Add(cTab);
        _allGrid.Columns.Add(cOn);
        _allGrid.Columns.Add(cRx);
        _allGrid.Columns.Add(cProb);
        _allGrid.Columns.Add(cFrom);
        _allGrid.Columns.Add(cTo);
        _allGrid.Columns.Add(cMemo);
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
                string from = Convert.ToString(row.Cells[3].Value) ?? "";
                string to = Convert.ToString(row.Cells[4].Value) ?? "";
                string memo = Convert.ToString(row.Cells[5].Value) ?? "";
                if (from.Trim().Length == 0 && to.Trim().Length == 0) continue;
                if (q.Length > 0
                    && from.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && to.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && memo.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && t.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                object on = row.Cells[0].Value;
                bool ruleOn = !(on is bool) || (bool)on;
                object rxc = row.Cells[1].Value;
                bool isRegex = (rxc is bool) && (bool)rxc;
                string prob = Convert.ToString(row.Cells[2].Value) ?? "";
                int idx = _allGrid.Rows.Add(t.Name, ruleOn ? "✓" : "", isRegex ? "✓" : "",
                    prob, from, to, memo);
                var ar = _allGrid.Rows[idx];
                ar.Tag = new AllRowRef { Tab = t, Row = row };
                // 編集グリッドと同じく、正規表現ルールの置換前セルは淡黄色で示す
                if (isRegex) ar.Cells[4].Style.BackColor = Color.LemonChiffon;
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
                int col = Math.Min(3, g.ColumnCount - 1); // 置換前セルにフォーカス
                g.CurrentCell = ar.Row.Cells[col];
                ar.Row.Selected = true;
                g.FirstDisplayedScrollingRowIndex = ar.Row.Index;
                g.Focus();
            }
        }
        catch { }
    }

    // ルール編集グリッドを1枚作る。列の並びは
    //   0=有効(チェック) / 1=正規表現(チェック) / 2=置換確率(%) / 3=置換前 / 4=置換後 / 5=メモ
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
        var colRegex = new DataGridViewCheckBoxColumn();
        colRegex.HeaderText = "正規表現";
        colRegex.Width = 64;
        colRegex.TrueValue = true;
        colRegex.FalseValue = false;
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
        var colMemo = new DataGridViewTextBoxColumn();
        colMemo.HeaderText = "メモ";
        colMemo.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        colMemo.FillWeight = 25;
        grid.Columns.Add(colOn);
        grid.Columns.Add(colRegex);
        grid.Columns.Add(colProb);
        grid.Columns.Add(colFrom);
        grid.Columns.Add(colTo);
        grid.Columns.Add(colMemo);
        grid.CellValueChanged += delegate(object s, DataGridViewCellEventArgs ev)
        {
            _dirty = true;
            // 正規表現チェックの切替で置換前セルの背景色 (CellFormatting) を描き直す
            if (ev.ColumnIndex == 1 && ev.RowIndex >= 0) grid.InvalidateRow(ev.RowIndex);
        };
        grid.RowsRemoved += delegate { _dirty = true; };
        // 正規表現ルールの置換前セルは淡黄色にして一目で分かるようにする
        grid.CellFormatting += delegate(object s, DataGridViewCellFormattingEventArgs ev)
        {
            if (ev.ColumnIndex != 3 || ev.RowIndex < 0) return;
            var row = grid.Rows[ev.RowIndex];
            if (row.IsNewRow) return;
            object v = row.Cells[1].Value;
            if (v is bool && (bool)v) ev.CellStyle.BackColor = Color.LemonChiffon;
        };
        // 新規行のチェックは既定で有効ON・正規表現OFF、置換確率は100
        grid.DefaultValuesNeeded += delegate(object s, DataGridViewRowEventArgs ev)
        {
            ev.Row.Cells[0].Value = true;
            ev.Row.Cells[1].Value = false;
            ev.Row.Cells[2].Value = "100";
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
            // プロキシと同様、正規表現とリテラルは同じ置換前文字列でも別グループ
            string key = (r.IsRegex ? "R\0" : "L\0") + r.From;
            List<RuleEntry> g;
            if (!index.TryGetValue(key, out g))
            {
                g = new List<RuleEntry>();
                index.Add(key, g);
                groups.Add(g);
            }
            g.Add(r);
        }

        foreach (var g in groups)
        {
            bool hit;
            if (g[0].IsRegex)
            {
                // 未保存 (未検証) のパターンでも押せるので、不正・タイムアウトは黙ってスキップ
                try { hit = Regex.IsMatch(text, g[0].From, RegexOptions.None, TimeSpan.FromSeconds(1)); }
                catch { continue; }
            }
            else
                hit = text.Contains(g[0].From);
            if (!hit) continue;
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
            if (chosen == null) continue;
            if (chosen.IsRegex)
            {
                try
                {
                    text = Regex.Replace(text, chosen.From, chosen.To,
                        RegexOptions.None, TimeSpan.FromSeconds(1));
                }
                catch { }
            }
            else
                text = text.Replace(chosen.From, chosen.To);
        }
        _prevOut.Text = text;
    }
}
