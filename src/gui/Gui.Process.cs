// ----------------------------------------------------------------------------
// Gui.Process.cs (MainForm partial: MOD適用/解除・ラッパーのビルド・プロセス強制終了)
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
    // ---------------------------------------------------------------- 適用/解除/終了

    // ラッパーをビルドし、各バックエンドの llama-server.exe と差し替える。
    // exeを掴んだままのプロセスがあると差し替えに失敗するので、先に終了させる。
    // 本物の退避は初回だけ (2回目以降にmoveすると、退避先がラッパーで上書きされて本物を失う)
    void OnApply(object sender, EventArgs e)
    {
        if (!EnsureNoProcess("MODを適用")) return;
        if (!Directory.Exists(_srcDir) || Directory.GetFiles(_srcDir, "*.cs").Length == 0)
        {
            MessageBox.Show(this, "ソースが見つかりません:\n" + _srcDir, "エラー",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // 適用直前に検出し直す (タイマー任せにせず、この操作が見ている構成で確実に処理する)
        string[] backends = EnumerateBackendDirs(_root);
        if (backends.Length == 0)
        {
            MessageBox.Show(this,
                "バックエンドフォルダが見つかりません:\n" + Path.Combine(_root, "bin", BackendPattern) +
                "\n\n対象フォルダがゲームフォルダか確認してください。",
                "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            foreach (string dir in backends)
            {
                string label = BackendDisplayName(dir);
                string exe = Path.Combine(dir, "llama-server.exe");
                string real = Path.Combine(dir, "llama-server-real.exe");
                if (!File.Exists(exe))
                {
                    results.AppendLine(label + ": スキップ");
                    continue;
                }
                if (!File.Exists(real)) File.Move(exe, real); // 初回のみ本物を退避
                File.Copy(_wrapperPath, exe, true);
                // ラッパーがMODフォルダ(ルール/ログの場所)を確実に特定できるよう記録する
                File.WriteAllText(Path.Combine(dir, "llm_proxy_dir.txt"),
                    Path.GetDirectoryName(_rulesPath), Encoding.UTF8);
                results.AppendLine(label + ": 適用OK");
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
            foreach (string dir in EnumerateBackendDirs(_root))
            {
                string label = BackendDisplayName(dir);
                string exe = Path.Combine(dir, "llama-server.exe");
                string real = Path.Combine(dir, "llama-server-real.exe");
                string marker = Path.Combine(dir, "llm_proxy_dir.txt");
                if (File.Exists(marker)) File.Delete(marker);
                if (!File.Exists(real))
                {
                    results.AppendLine(label + ": 未適用");
                    continue;
                }
                if (File.Exists(exe)) File.Delete(exe); // ラッパーを削除
                File.Move(real, exe);
                results.AppendLine(label + ": 復元OK");
            }
            if (results.Length == 0) results.AppendLine("バックエンドフォルダが見つかりません。");
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
                "対象フォルダの llama-server / llama-server-real のプロセスを強制終了します。\n" +
                _root + "\n\n" +
                "(ゲームプレイ中に実行するとLLM機能がエラーになります)\nよろしいですか?",
                "プロセス強制終了", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        KillAll();
        RefreshStatus();
    }

    // 対象フォルダの bin\ 配下から起動された llama-server / llama-server-real だけを返す。
    // プロセス名だけで拾うと、別フォルダの無関係な llama-server (他ゲームや素の llama.cpp) まで
    // 巻き込んでしまうため、実行ファイルの場所で絞る。場所を読めないプロセス (別ユーザーの
    // ものなど) は、どのみち操作できないので対象外にする。
    // 呼び出し側は使い終わった Process を Dispose すること
    List<Process> FindTargetProcesses()
    {
        var list = new List<Process>();
        string binRoot;
        try { binRoot = Path.GetFullPath(Path.Combine(_root, "bin")).TrimEnd('\\') + "\\"; }
        catch { return list; }
        foreach (string name in new[] { "llama-server", "llama-server-real" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                string exe = TryGetProcessPath(p);
                if (exe != null && exe.StartsWith(binRoot, StringComparison.OrdinalIgnoreCase)) list.Add(p);
                else p.Dispose();
            }
        }
        return list;
    }

    static string TryGetProcessPath(Process p)
    {
        try { return Path.GetFullPath(p.MainModule.FileName); }
        catch { return null; } // 終了直後・権限不足・ビット数違い等
    }

    void KillAll()
    {
        foreach (var p in FindTargetProcesses())
        {
            try { p.Kill(); p.WaitForExit(3000); }
            catch { }
            finally { p.Dispose(); }
        }
    }

    // 実行中プロセスがあれば確認して止める。falseなら操作中断
    bool EnsureNoProcess(string action)
    {
        int n = 0;
        foreach (var p in FindTargetProcesses()) { n++; p.Dispose(); }
        if (n == 0) return true;
        var r = MessageBox.Show(this,
            "llama-server が起動中です。" + action + "するには終了が必要です。\n強制終了しますか?",
            "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r != DialogResult.Yes) return false;
        KillAll();
        return true;
    }

    // src\proxy\*.cs をその場でコンパイルする。
    // cscはWindows同梱の .NET Framework 4.x のものを直接叩くので、
    // 開発環境やSDKのインストールは不要 (ユーザのPCでもそのままビルドできる)
    bool BuildWrapper(out string output)
    {
        string csc = Path.Combine(
            Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows",
            @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
        var psi = new ProcessStartInfo(csc,
            "/nologo /optimize /codepage:65001 /out:\"" + _wrapperPath + "\" \"" +
            Path.Combine(_srcDir, "*.cs") + "\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using (var p = Process.Start(psi))
        {
            // stdoutとstderrを順に同期読みすると、先に読んでいない側のパイプが
            // 埋まったときにcscと互いに待ち合ってデッドロックするため、stderrは別スレッドで読む
            string se = "";
            var errReader = new System.Threading.Thread(delegate() { try { se = p.StandardError.ReadToEnd(); } catch { } });
            errReader.IsBackground = true;
            errReader.Start();
            string so = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            errReader.Join(5000);
            output = (so + "\n" + se).Trim();
            return p.ExitCode == 0;
        }
    }
}
