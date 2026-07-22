// ============================================================================
// src\proxy\*.cs — Instantale LLMリクエスト置換プロキシ (llama-server.exe ラッパー)
// (機能ごとに Proxy.*.cs へ分割。すべて partial class LlmProxy で1つのクラス)
//
// 仕組み:
//   ゲームは bin\llama-*\llama-server.exe を「--host 127.0.0.1 --port <空きポート>」で
//   起動し、HTTPで /apply-template と /completion を叩く。
//   このexeを llama-server.exe として置き、本物 (llama-server-real.exe) を
//   別の空きポートで起動して、ゲーム→LLM のリクエストボディ内の文字列を
//   llm_replacements.txt のルールで置換して中継する。
//   レスポンス(ストリーミング含む)は無加工でそのまま流す。
//
// JSON安定化 (JSONFIX):
//   /completion のプロンプト内にPython dict形式のJSONスキーマを検出したら、
//   本物のJSON Schemaへ変換して llama-server の json_schema パラメータに注入する。
//   これによりサーバ側の文法制約付き生成が働き、構文的に壊れたJSONが
//   出力されなくなる。詳細は「JSON安定化」セクションを参照。
//   無効化するにはGUIの「設定」→「機能設定...」で切り替える。
//
// ルールファイル: <MODフォルダ>\llm_replacements.txt (UTF-8)
//   MODフォルダの場所は、適用時に exe と同じ場所に書かれる llm_proxy_dir.txt で
//   確実に特定する。無い場合のみ後方互換で上位フォルダから探索する。
//   1行1ルール「置換前=>置換後=>置換確率」。確率(0-100)は省略可で省略時100。行頭 # はコメント。
//   同一の置換前を持つルールが複数ある場合は確率に応じてどれか1つが選ばれる
//   (確率の合計が100を超える場合は 値/合計 の割合で必ずどれかに置換)。
//   GUIで無効化されたルールは「#off:」付きで保存され、コメントとして無視される。
//   「#tab:タブ名」から次のタブ行まではそのタブのルール。「#offtab:タブ名」は
//   無効化されたタブで、中のルールはすべて無視される (GUIのタブと連動)。
//   ファイルの更新はリクエストごとに検知して自動再読込する (ゲーム再起動不要)。
//   Pythonクライアントが日本語を \uXXXX エスケープして送る場合にも対応するため、
//   各ルールのエスケープ版も自動生成して照合する。
//
// 本物のシングルトン化 (多重ロード抑止):
//   ゲームは1回の操作でラッパー(llama-server.exe)を複数同時に起動し、しかも
//   古いラッパーを確実には終了させない (プロキシ無しでも発生するゲーム側の挙動)。
//   各ラッパーが素直に本物を起動すると、同じ26Bモデルが何本もGPUに載って
//   VRAMが枯渇し、ロードが激遅になる (→再生成で更に増える悪循環)。
//   そこで、同一起動引数(=同一モデル・同一設定)の本物は1本だけに集約する:
//     ・最初に起動したラッパーが本物を起動して「所有者」になる
//     ・以降のラッパーは本物を起動せず、所有者の本物ポートへ中継するだけ(フォロワー)
//   調停はMODフォルダのレジストリファイル + 名前付きMutexで行う。所有者が消えて
//   本物も道連れ(Jobオブジェクト)になった場合は、フォロワーが検知して共有先を
//   取り直す (誰かが昇格していればそれを、居なければ自分が昇格して本物を再起動)。
//   引数が異なる(別モデル等)場合は別グループとして各1本が起動する。
//
// ログ: ルールファイルと同じフォルダの llm_proxy.log
//
// ビルド: src\llm_proxy_apply.bat または管理GUI (InstantaleLlmProxy.exe) が行う
//         (Windows同梱の csc.exe / .NET Framework 4.x。src\proxy\*.cs をまとめてコンパイル)
// ============================================================================
// ----------------------------------------------------------------------------
// Proxy.Core.cs (LlmProxy partial: エントリポイント・ラッパー本体のシングルトン化・子プロセス管理・Jobオブジェクト)
// ----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

static partial class LlmProxy
{
    const string RealExeName = "llama-server-real.exe";
    const string RulesFileName = "llm_replacements.txt";
    const string LogFileName = "llm_proxy.log";
    const string MarkerFileName = "llm_proxy_dir.txt"; // 適用時に書かれるMODフォルダの場所

    static readonly object LogLock = new object();
    static string _logPath;

    // 置換ルール。From/To は照合・置換に使う実体 (\uXXXX エスケープ版も別ルールとして登録)。
    // DispFrom/DispTo はログ表示用の読みやすい形 (常に元の日本語)。
    // Prob は置換確率 (0-100)。同一Fromのルールはグループ化され、確率でどれか1つ (または無置換) が選ばれる。
    // IsRegex=true は From を .NET 正規表現として照合・置換する (Rx にコンパイル済みを保持)。
    class Rule
    {
        public string From;
        public string To;
        public string DispFrom;
        public string DispTo;
        public int Prob;
        public bool IsRegex;
        public System.Text.RegularExpressions.Regex Rx;
    }

    // 現在有効なルール一覧。再読込時は新しいリストへ丸ごと差し替える (読む側はロック不要)
    static volatile List<Rule> Rules = new List<Rule>();
    static readonly object RulesLock = new object();
    static string _exeDir;
    static string _rulesPath;
    static DateTime _rulesStamp = DateTime.MinValue;
    static volatile int _upstreamPort;
    static Process _child;
    static IntPtr _job = IntPtr.Zero;

    // 本物のシングルトン化に使う状態。
    // _isOwner=trueなら自分が本物(_child)を起動・所有している。falseなら他ラッパーの
    // 本物(_ownerPid)へ中継するだけのフォロワー。所有者消失時はフォロワーが昇格しうる。
    static string[] _origArgs = new string[0];
    static string _realExe;
    static int _ctxSize;                    // --ctx-size (診断で打ち切り判定に併記)
    static string _sigHash;                 // 起動引数から作る集約キー(モデル・設定が同じなら同一)
    static volatile bool _isOwner;
    static volatile int _ownerPid;          // 共有している本物を所有するラッパーのpid
    static volatile bool _watchStarted;     // WatchChildスレッドを二重起動しないためのフラグ
    static readonly object AcquireLock = new object();
    static readonly string SelfProcName = Process.GetCurrentProcess().ProcessName;

    // 起動直後の唯一の関心事はログの出力先を確定させること。
    // ここから先の失敗はすべてログに残したいので、Runに入る前に _logPath を決める。
    static int Main(string[] args)
    {
        // カレントディレクトリはゲーム側の都合で決まるため当てにできない。
        // 基準は常に「このexeが置かれている場所」(= bin\llama-*\) とする。
        string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

        // ルールファイルの場所を特定する (適用時のマーカー優先、無ければ後方互換の探索)
        string rulesPath = FindRulesFile(exeDir);
        string baseDir;
        if (rulesPath != null) baseDir = Path.GetDirectoryName(rulesPath);
        else
        {
            // ルール未発見でもマーカーがあればそのMODフォルダへログを出す。
            // それも無ければ exe と同じ場所 (ゲームルートには何も置かない)
            string marker = Path.Combine(exeDir, MarkerFileName);
            baseDir = (File.Exists(marker) ? ReadMarkerDir(marker) : null) ?? exeDir;
        }
        _logPath = Path.Combine(baseDir, LogFileName);

        // Program Files 配下など書き込めない場所では %LOCALAPPDATA% にフォールバック
        try { File.AppendAllText(_logPath, ""); }
        catch
        {
            _logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LogFileName);
        }

        try
        {
            // 肥大化防止: 5MB を超えていたらリセット
            var fi = new FileInfo(_logPath);
            if (fi.Exists && fi.Length > 5 * 1024 * 1024) fi.Delete();
        }
        catch { }

        try
        {
            return Run(exeDir, rulesPath, args);
        }
        catch (Exception ex)
        {
            Log("[FATAL] " + ex);
            if (_child != null && !_child.HasExited) { try { _child.Kill(); } catch { } }
            return 1;
        }
    }

    // ゲームから渡された引数をそのまま本物へ引き継ぎつつ、ポートだけを差し替える。
    // ゲームが繋ぎに来るポート(listenPort)でこちらが待ち受け、本物は内部ポート(_upstreamPort)へ追いやる。
    static int Run(string exeDir, string rulesPath, string[] args)
    {
        _exeDir = exeDir;
        _rulesPath = rulesPath;
        _origArgs = args;
        ReloadRulesIfChanged();
        Log("[BOOT] 起動 args: " + string.Join(" ", args));
        Log("[BOOT] ルール: " + (_rulesPath ?? "(なし)") + " " + Rules.Count + "パターン(エスケープ版含む)");

        // スタンドアロン中継モード。GUIの「OpenAI互換 > 中継サーバを起動」から --openai-relay
        // 付きで起動される。ゲーム側のOpenAI互換設定(エンドポイント欄)にこの中継サーバを
        // 指定する使い方で、この構成ではゲームは llama-server を起動しない。
        if (HasFlag(args, "--openai-relay")) return RunOpenAiRelay();

        // ゲームが繋ぎに来る待ち受け先。これはラッパー固有(引数の--port)で、集約とは無関係
        int listenPort;
        string listenHost;
        ParseListenEndpoint(args, out listenHost, out listenPort);
        _ctxSize = ParseIntArg(args, "--ctx-size", 0);

        // ラッパー翻訳モード。openai_wrapper=1 で明示的に有効化されたときだけ、ローカルの
        // 本物を起動せず、ゲームのllama.cpp APIをOpenAI互換 Chat Completions へ翻訳して中継する。
        // endpoint/model が保存されているだけでは乗っ取らない: 中継サーバ用の接続設定を保持
        // したまま、ゲーム同梱のローカルLLMへいつでも戻せるようにするため(GUIのモード切替)。
        bool wrapperOn = CachedSettingBool("openai_wrapper", false);
        _openai = wrapperOn ? LoadOpenAiConfig(true) : null;
        if (_openai != null)
        {
            EnableModernTls();
            Log("[BOOT] OpenAI互換モード: endpoint=" + _openai.Endpoint +
                " model=" + _openai.Model + " json_mode=" + _openai.JsonMode + " (ローカルllama-serverは起動しない)");
        }
        else
        {
            // なぜローカルで動いているのかをログから追えるように、判定の内訳を1行残す
            if (wrapperOn)
                Log("[BOOT] openai_wrapper=1 だが openai_endpoint/openai_model が不足 → ローカルLLMで動作");
            else if (ReadSettingString("openai_endpoint", "").Length > 0)
                Log("[BOOT] OpenAI互換の接続設定は保存済み (ラッパー翻訳は無効=ローカルLLMで動作。切替はGUIの「OpenAI互換」メニュー)");
            _realExe = Path.Combine(exeDir, RealExeName);
            if (!File.Exists(_realExe))
            {
                Log("[FATAL] 本物のサーバが見つかりません: " + _realExe);
                return 1;
            }

            // 同一引数の本物を1本に集約する。所有者になれば本物を起動し、既存が生きていれば
            // それを共有する。_upstreamPort / _isOwner / _child はここで確定する
            _sigHash = ShortHash(BuildSignature(args));
            AcquireUpstream();
        }

        IPAddress addr;
        if (listenHost == "0.0.0.0") addr = IPAddress.Any;
        else if (!IPAddress.TryParse(listenHost, out addr)) addr = IPAddress.Loopback;
        var listener = new TcpListener(addr, listenPort);
        listener.Start();
        if (_openai != null)
            Log("[BOOT] listen " + listenHost + ":" + listenPort + " -> openai " + _openai.Endpoint +
                " (" + _openai.Model + ")");
        else
            Log("[BOOT] listen " + listenHost + ":" + listenPort + " -> upstream 127.0.0.1:" + _upstreamPort);

        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            TcpClient c = client;
            var t = new Thread(() =>
            {
                if (_openai != null) HandleClientOpenAi(c);
                else HandleClient(c);
            });
            t.IsBackground = true;
            t.Start();
        }
    }

    // ---------------------------------------------------------------- 本物の集約(シングルトン化)

    // ゲームが繋ぎに来る待ち受けホスト/ポートを引数から取り出す(未指定はllama-serverと同じ8080)
    static void ParseListenEndpoint(string[] args, out string host, out int port)
    {
        host = "127.0.0.1";
        port = 8080;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--port" && i + 1 < args.Length) int.TryParse(args[i + 1], out port);
            else if (a.StartsWith("--port=", StringComparison.Ordinal)) int.TryParse(a.Substring(7), out port);
            else if (a == "--host" && i + 1 < args.Length) host = args[i + 1];
            else if (a.StartsWith("--host=", StringComparison.Ordinal)) host = a.Substring(7);
        }
    }

    // 共有する本物のスロットを1つに固定する (--parallel 1)。既存の指定は上書きし、
    // 無ければ追加する。これで並行リクエストは直列化され、各々フルのコンテキストを使える。
    static void ForceParallelOne(List<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if ((args[i] == "--parallel" || args[i] == "-np") && i + 1 < args.Count)
            {
                args[i + 1] = "1";
                return;
            }
            if (args[i].StartsWith("--parallel=", StringComparison.Ordinal))
            {
                args[i] = "--parallel=1";
                return;
            }
        }
        args.Add("--parallel");
        args.Add("1");
    }

    // 「--name 値」または「--name=値」形式の整数引数を取り出す。無ければ dflt
    static int ParseIntArg(string[] args, string name, int dflt)
    {
        string eq = name + "=";
        for (int i = 0; i < args.Length; i++)
        {
            int v;
            if (args[i] == name && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out v)) return v;
            }
            else if (args[i].StartsWith(eq, StringComparison.Ordinal))
            {
                if (int.TryParse(args[i].Substring(eq.Length), out v)) return v;
            }
        }
        return dflt;
    }

    // 本物へ渡す引数。--port だけを内部の空きポートに差し替える(未指定なら追加する)
    static List<string> BuildChildArgs(string[] args, int upstreamPort)
    {
        var childArgs = new List<string>(args);
        bool portFound = false;
        for (int i = 0; i < childArgs.Count; i++)
        {
            string a = childArgs[i];
            if (a == "--port" && i + 1 < childArgs.Count)
            {
                childArgs[i + 1] = upstreamPort.ToString();
                portFound = true;
            }
            else if (a.StartsWith("--port=", StringComparison.Ordinal))
            {
                childArgs[i] = "--port=" + upstreamPort;
                portFound = true;
            }
        }
        if (!portFound)
        {
            childArgs.Add("--port");
            childArgs.Add(upstreamPort.ToString());
        }
        return childArgs;
    }

    // 集約キー。待ち受け先(--port/--host)以外の引数が全て同じなら同一モデル・同一設定と見なす。
    // これが一致するラッパー同士で本物を1本に共有する
    static string BuildSignature(string[] args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--port" || a == "--host") { i++; continue; } // 値も飛ばす
            if (a.StartsWith("--port=", StringComparison.Ordinal) ||
                a.StartsWith("--host=", StringComparison.Ordinal)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(a);
        }
        return sb.ToString();
    }

    // 識別用キーで暗号強度は不要。起動引数パターンの種類は少なく、12桁で実用上衝突しない
    static string ShortHash(string s)
    {
        using (var md5 = MD5.Create())
        {
            byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
            var sb = new StringBuilder();
            for (int i = 0; i < 6; i++) sb.Append(h[i].ToString("x2"));
            return sb.ToString();
        }
    }

    // レジストリ/Mutex はMODフォルダ(ルールファイルと同じ場所)基準。同一集約キーの
    // ラッパー同士が同じファイル/Mutexを見ることで、プロセスをまたいで調停できる
    static string RegistryPath()
    {
        try
        {
            string baseDir = _rulesPath != null ? Path.GetDirectoryName(_rulesPath)
                                                : Path.GetDirectoryName(_logPath);
            return baseDir == null ? null : Path.Combine(baseDir, "llm_proxy_upstream_" + _sigHash + ".txt");
        }
        catch { return null; }
    }

    static bool ReadRegistry(out int pid, out int port)
    {
        pid = 0; port = 0;
        try
        {
            string p = RegistryPath();
            if (p == null || !File.Exists(p)) return false;
            string[] parts = File.ReadAllText(p).Trim().Split(',');
            if (parts.Length < 2) return false;
            return int.TryParse(parts[0].Trim(), out pid) && int.TryParse(parts[1].Trim(), out port);
        }
        catch { return false; }
    }

    static void WriteRegistry(int pid, int port)
    {
        try
        {
            string p = RegistryPath();
            if (p != null) File.WriteAllText(p, pid + "," + port);
        }
        catch { }
    }

    // pid のプロセスが「生きているラッパー」か。PID再利用で無関係なプロセスを所有者と
    // 誤認しないよう、自分と同じ実行ファイル名(llama-server)であることまで確かめる
    static bool IsWrapperAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using (var p = Process.GetProcessById(pid))
            {
                if (p.HasExited) return false;
                return string.Equals(p.ProcessName, SelfProcName, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { return false; }
    }

    // 本物の共有先を確定する。既に生きた所有者が居ればフォロワーとしてそのポートを使い、
    // 居なければ自分が本物を起動して所有者になる。所有者消失後の再取得(昇格)にも使う。
    // 判断はプロセスをまたぐ名前付きMutexで直列化し、二重起動を防ぐ
    static void AcquireUpstream()
    {
        lock (AcquireLock)
        {
            if (_isOwner) return; // 既に自分が所有しているなら何もしない

            // シングルトン無効時は集約せず、各ラッパーが自分専用の本物を起動する(従来動作)。
            // ゲームが並行生成する場合、共有だと1本の16384コンテキストを分割してしまい
            // context-exceeded になるため、切り分け用のスイッチ (GUIの「設定」→「機能設定...」)。
            if (!SingletonEnabled())
            {
                BecomeOwner(Process.GetCurrentProcess().Id);
                Log("[BOOT] シングルトン無効: 専用の本物を起動");
                return;
            }

            int myPid = Process.GetCurrentProcess().Id;
            var mtx = new Mutex(false, "Local\\InstantaleLlmProxy_" + _sigHash);
            bool haveMutex = false;
            try
            {
                try { haveMutex = mtx.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { haveMutex = true; } // 前所有者の異常終了時も取得する
                if (!haveMutex)
                    Log("[WARN] 調停Mutexを30秒で取得できず、ロック無しで続行 (本物が二重起動する可能性)");

                int pid, port;
                if (ReadRegistry(out pid, out port) && pid != myPid && IsWrapperAlive(pid))
                {
                    // 生きた所有者が居る → 本物は起動せず共有する(フォロワー)
                    _ownerPid = pid;
                    _upstreamPort = port;
                    _isOwner = false;
                    Log("[BOOT] 既存の本物を共有 owner_pid=" + pid + " -> upstream 127.0.0.1:" + port);
                    return;
                }

                BecomeOwner(myPid); // 所有者になる: 本物を起動してレジストリに登録する
            }
            finally
            {
                if (haveMutex) { try { mtx.ReleaseMutex(); } catch { } }
                mtx.Close();
            }
        }
    }

    // 自分が本物を起動して所有者になる (AcquireLock 保持前提)
    static void BecomeOwner(int myPid)
    {
        int up = FindFreePort();
        var childArgs = BuildChildArgs(_origArgs, up);
        // シングルトン有効時は1本の本物を複数ラッパーで共有する。本物が複数スロット
        // (--parallel>1) だと 16384 のコンテキストをスロットで分割し、並行生成で
        // context-exceeded(HTTP500)になる。--parallel 1 に固定して並行リクエストを
        // 直列化し、各リクエストにフルのコンテキストを与える。
        if (SingletonEnabled()) ForceParallelOne(childArgs);
        _child = StartChild(_realExe, childArgs);
        SetupJobObject(_child); // 所有ラッパーが死んだら本物も道連れにする
        _upstreamPort = up;
        _ownerPid = myPid;
        _isOwner = true;
        WriteRegistry(myPid, up);
        if (!_watchStarted)
        {
            _watchStarted = true;
            // 子プロセス監視: 本物が終了したらラッパーも同じコードで終了
            var watchdog = new Thread(WatchChild) { IsBackground = true };
            watchdog.Start();
        }
    }

    // フォロワーが共有先の所有者消失を検知したときの復旧。所有者が生きているなら
    // (ロード中の可能性があるので)何もせず待つ。消えていれば共有先を取り直す
    static void RecoverUpstream()
    {
        if (_isOwner) return;
        AcquireUpstream();
    }

    // ---------------------------------------------------------------- 子プロセス

    static Process StartChild(string exe, List<string> args)
    {
        var sb = new StringBuilder();
        foreach (string a in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(QuoteArg(a));
        }
        var psi = new ProcessStartInfo(exe, sb.ToString());
        psi.UseShellExecute = false; // 標準入出力はそのまま継承
        var p = Process.Start(psi);
        Log("[BOOT] 本物を起動 pid=" + p.Id + " : " + exe + " " + sb);
        return p;
    }

    static void WatchChild()
    {
        try
        {
            _child.WaitForExit();
            Log("[EXIT] 本物が終了 code=" + _child.ExitCode);
            Environment.Exit(_child.ExitCode);
        }
        catch { Environment.Exit(1); }
    }

    // Windows (CommandLineToArgvW/MSVCRT) の引数クォート規約でエスケープする。
    // 素朴に " を \" に置換するだけでは、直前に連続する \ の解釈がずれて壊れるため
    // (\ の連なりは直後が " のときだけ倍加してエスケープする規約)、bs で保留中の連続 \ 数を数える
    static string QuoteArg(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        var sb = new StringBuilder("\"");
        int bs = 0;
        foreach (char ch in a)
        {
            if (ch == '\\') { bs++; continue; }
            if (ch == '"') { sb.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
            sb.Append('\\', bs).Append(ch);
            bs = 0;
        }
        sb.Append('\\', bs * 2).Append('"');
        return sb.ToString();
    }

    // ---------------------------------------------------------------- ユーティリティ

    // ポート0でbindするとOSが空きポートを割り当てる。すぐ閉じて番号だけ使う
    // (閉じてから本物が掴むまでの間に他プロセスに奪われる可能性は残るが、実用上問題にならない)
    static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    // ---------------------------------------------------------------- Jobオブジェクト
    // ゲームはラッパー(llama-server.exe)をプロセス名でkillする。
    // その際に本物(llama-server-real.exe)が残らないよう、Jobオブジェクトで
    // 「ラッパー終了 = 本物も強制終了」を保証する。

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll")]
    static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    // 以下2つの構造体はWin32のヘッダ定義と1バイトも違わない必要がある。
    // フィールドの型・順序を変えると SetInformationJobObject が黙って失敗する
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    // Jobのハンドルが閉じられた時 (= ラッパー終了時) に中のプロセスを全て終了させるフラグ。
    // プロセスがkillされてもハンドルはOSが閉じるので、強制終了でも本物が道連れになる
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    const int JobObjectExtendedLimitInformation = 9;

    // 失敗しても中継自体は続行できるので、警告ログだけ残して先へ進む
    // (最悪、本物がゲーム終了後も残るが、GUIの「プロセス強制終了」で回収できる)
    static void SetupJobObject(Process child)
    {
        try
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero) throw new InvalidOperationException("CreateJobObject失敗");
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int len = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ptr, (uint)len))
                    throw new InvalidOperationException("SetInformationJobObject失敗");
            }
            finally { Marshal.FreeHGlobal(ptr); }
            if (!AssignProcessToJobObject(_job, child.Handle))
                throw new InvalidOperationException("AssignProcessToJobObject失敗 err=" + Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            Log("[WARN] Jobオブジェクト設定失敗 (本物が残る可能性あり): " + ex.Message);
        }
    }
}
