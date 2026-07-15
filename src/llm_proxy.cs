// ============================================================================
// llm_proxy.cs — Instantale LLMリクエスト置換プロキシ (llama-server.exe ラッパー)
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
//   無効化するにはMODフォルダに llm_proxy_jsonfix_off.txt を置く。
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
//         (Windows同梱の csc.exe / .NET Framework 4.x)
// ============================================================================

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

static class LlmProxy
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
    class Rule
    {
        public string From;
        public string To;
        public string DispFrom;
        public string DispTo;
        public int Prob;
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

        // ゲームが繋ぎに来る待ち受け先。これはラッパー固有(引数の--port)で、集約とは無関係
        int listenPort;
        string listenHost;
        ParseListenEndpoint(args, out listenHost, out listenPort);
        _ctxSize = ParseIntArg(args, "--ctx-size", 0);

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

        IPAddress addr;
        if (listenHost == "0.0.0.0") addr = IPAddress.Any;
        else if (!IPAddress.TryParse(listenHost, out addr)) addr = IPAddress.Loopback;
        var listener = new TcpListener(addr, listenPort);
        listener.Start();
        Log("[BOOT] listen " + listenHost + ":" + listenPort + " -> upstream 127.0.0.1:" + _upstreamPort);

        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            TcpClient c = client;
            var t = new Thread(() => HandleClient(c));
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
            var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            return string.Equals(p.ProcessName, SelfProcName, StringComparison.OrdinalIgnoreCase);
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
            // context-exceeded になるため、切り分け用のスイッチ (llm_proxy_singleton_off.txt)。
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

    // Windowsの引数クォート規約でエスケープする
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

    // ---------------------------------------------------------------- プロキシ本体

    static void HandleClient(TcpClient client)
    {
        TcpClient upstream = null;
        try
        {
            client.NoDelay = true;
            upstream = ConnectUpstream();
            if (upstream == null) return;
            upstream.NoDelay = true;

            NetworkStream cs = client.GetStream();
            NetworkStream us = upstream.GetStream();

            // レスポンス方向は無加工で素通し (SSEストリーミング対応のためバッファリングしない)
            TcpClient c2 = client, u2 = upstream;
            var down = new Thread(() => PipeRaw(u2, c2));
            down.IsBackground = true;
            down.Start();

            // リクエスト方向: HTTPを1件ずつ解析してボディを置換して転送 (keep-alive対応)
            var reader = new BufferedStream(cs, 16384);
            while (ForwardOneRequest(reader, us)) { }
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log("[ERROR] client: " + ex.Message); }
        finally
        {
            SafeClose(client);
            if (upstream != null) SafeClose(upstream);
        }
    }

    // 本物のサーバへ接続する。モデルロード中でポートが開くまで再試行する。
    // フォロワーの場合、共有先の所有者が消えていたら共有先を取り直して(昇格を含む)続行する
    static TcpClient ConnectUpstream()
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            try { return new TcpClient("127.0.0.1", _upstreamPort); }
            catch (SocketException) { }

            if (_isOwner)
            {
                // 自分の本物が終了したなら中継しても無駄。接続を諦める
                try { if (_child.HasExited) return null; } catch { return null; }
            }
            else if (!IsWrapperAlive(_ownerPid))
            {
                // 共有していた所有者が消えた=本物も道連れ。共有先を取り直す
                // (誰かが昇格していればそれを、居なければ自分が昇格する)
                RecoverUpstream();
                if (_isOwner)
                {
                    try { if (_child.HasExited) return null; } catch { return null; }
                }
            }
            // フォロワーで所有者が生きている場合はロード完了待ちとして再試行する
            Thread.Sleep(250);
        }
        Log("[ERROR] upstream接続タイムアウト port=" + _upstreamPort);
        return null;
    }

    // リクエストを1件読み取り、ボディを置換して転送する。falseで接続終了
    static bool ForwardOneRequest(BufferedStream reader, NetworkStream us)
    {
        byte[] header = ReadHeaderBlock(reader);
        if (header == null) return false; // クライアント切断

        // ヘッダはLatin-1 (28591) で文字列化する。1バイト=1文字が保証され、
        // 解析だけしてそのまま書き戻してもバイト列が壊れない (UTF-8だと非ASCIIで崩れる)
        string headerText = Encoding.GetEncoding(28591).GetString(header);
        string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        string reqLine = lines[0];

        int contentLength = 0;
        bool chunked = false;
        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c <= 0) continue;
            string name = lines[i].Substring(0, c).Trim().ToLowerInvariant();
            string val = lines[i].Substring(c + 1).Trim();
            if (name == "content-length") int.TryParse(val, out contentLength);
            else if (name == "transfer-encoding" && val.ToLowerInvariant().Contains("chunked")) chunked = true;
        }

        if (chunked)
        {
            // チャンク転送は想定外なので、以降このコネクションは無加工トンネルにする
            Log("[WARN] chunkedリクエストのため無加工で中継: " + reqLine);
            us.Write(header, 0, header.Length);
            CopyStream(reader, us);
            return false;
        }

        // Content-Length分を読み切る (1回のReadでは足りないことがある)
        byte[] body = new byte[contentLength];
        int off = 0;
        while (off < contentLength)
        {
            int n = reader.Read(body, off, contentLength - off);
            if (n <= 0) return false;
            off += n;
        }

        // 加工の有無は参照の同一性で判定する。無加工なら元のヘッダをそのまま流せる
        byte[] newBody = contentLength > 0 ? ApplyRules(body, reqLine) : body;
        if (newBody.Length > 0 && IsCompletionRequest(reqLine))
            newBody = ApplyJsonSchemaFix(newBody, reqLine);
        if (!ReferenceEquals(newBody, body))
        {
            // ボディが変わったので Content-Length を書き換えてヘッダを再構築
            var sb = new StringBuilder();
            sb.Append(reqLine).Append("\r\n");
            for (int i = 1; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (ln.Length == 0) continue;
                int c = ln.IndexOf(':');
                if (c > 0 && ln.Substring(0, c).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    sb.Append("Content-Length: ").Append(newBody.Length).Append("\r\n");
                else
                    sb.Append(ln).Append("\r\n");
            }
            sb.Append("\r\n");
            byte[] hb = Encoding.GetEncoding(28591).GetBytes(sb.ToString());
            us.Write(hb, 0, hb.Length);
        }
        else
        {
            us.Write(header, 0, header.Length);
        }
        if (newBody.Length > 0) us.Write(newBody, 0, newBody.Length);
        us.Flush();
        return true;
    }

    // \r\n\r\n までを読み取る。開始前にEOFなら null (keep-aliveの正常な終了)
    static byte[] ReadHeaderBlock(Stream s)
    {
        var buf = new List<byte>(1024);
        // ヘッダ終端 \r\n\r\n を1バイトずつ追う状態機械。
        // state: 0=何もなし 1=\r 2=\r\n 3=\r\n\r → 次が \n なら終端
        int state = 0;
        while (true)
        {
            int b = s.ReadByte();
            if (b < 0) return null;
            buf.Add((byte)b);
            if (b == '\r') state = (state == 2) ? 3 : 1;
            else if (b == '\n')
            {
                if (state == 3) return buf.ToArray();
                state = (state == 1) ? 2 : 0;
            }
            else state = 0;
            // 終端が来ないまま無限にメモリを食うのを防ぐ (壊れたクライアント対策)
            if (buf.Count > 1024 * 1024) throw new InvalidOperationException("ヘッダが大きすぎます");
        }
    }

    // 無加工の一方向中継。レスポンス(本物→ゲーム)はこれで流す。
    // トークンが生成されるそばから届くよう、溜めずに読めた分だけ即書き出す。
    // 診断モード時のみ、SSEレスポンスの最終イベントを RespStats で覗き見して統計を
    // ログに記録する(中身は一切変更しない)。実フォーマットが版によって違う可能性が
    // あるため、生の最終イベントも別ファイルへダンプして後から突き合わせられるようにする。
    static void PipeRaw(TcpClient src, TcpClient dst)
    {
        try
        {
            NetworkStream a = src.GetStream(), b = dst.GetStream();
            var buf = new byte[65536];
            int n;
            RespStats stats = DiagEnabled() ? new RespStats(_ctxSize) : null;
            while ((n = a.Read(buf, 0, buf.Length)) > 0)
            {
                b.Write(buf, 0, n);
                if (stats != null) stats.Feed(buf, n);
            }
            if (stats != null) stats.Flush();
        }
        catch { }
        finally
        {
            // 片方が閉じたら両方閉じて、反対方向のスレッドも解放する
            SafeClose(src);
            SafeClose(dst);
        }
    }

    // s の中の key ("...":) 直後の整数を取り出す。無い/整数でないなら -1
    static long ExtractLong(string s, string key)
    {
        int i = s.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return -1;
        i += key.Length;
        while (i < s.Length && s[i] == ' ') i++;
        int start = i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
        if (i == start) return -1;
        long v;
        return long.TryParse(s.Substring(start, i - start),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1;
    }

    // レスポンス(SSEストリーム)の最終イベントを集めて統計をログする診断用。
    // 「"stop":true」を含むイベントが最終イベント(トークン途中は "stop":false)。
    // 版によって打ち切りフィールド名が違う(stopped_limit / stop_type:"limit")ため、
    // 最終イベントを丸ごと集めてから名前非依存で抽出する。さらに生の最終イベント
    // (捕捉できなければレスポンス末尾)を llm_proxy_resp_dump.log に出し、実フォーマットを残す。
    internal sealed class RespStats
    {
        readonly int _ctx;
        bool _inFinal;
        bool _emitted;
        readonly StringBuilder _final = new StringBuilder();
        byte[] _carry;              // "stop":true が境界で割れても拾うための小さな繰り越し
        byte[] _tail;               // 常時保持する末尾(マーカー不検出時の生ダンプ用)
        const int TailKeep = 2048;
        const int Cap = 262144;     // 最終イベントが prompt/settings 込みで肥大しても上限で確定
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
        static readonly object DumpLock = new object();

        public RespStats(int ctx) { _ctx = ctx; }

        public void Feed(byte[] buf, int n)
        {
            UpdateTail(buf, n);
            if (!_inFinal)
            {
                int carryLen = _carry == null ? 0 : _carry.Length;
                string s;
                if (carryLen > 0)
                {
                    var combined = new byte[carryLen + n];
                    Buffer.BlockCopy(_carry, 0, combined, 0, carryLen);
                    Buffer.BlockCopy(buf, 0, combined, carryLen, n);
                    s = Latin1.GetString(combined);
                }
                else s = Latin1.GetString(buf, 0, n);

                int idx = s.IndexOf("\"stop\":true", StringComparison.Ordinal);
                if (idx < 0)
                {
                    int keep = Math.Min(16, s.Length);
                    _carry = Latin1.GetBytes(s.Substring(s.Length - keep));
                    return;
                }
                _inFinal = true;
                _carry = null;
                _final.Append(s, idx, s.Length - idx);
            }
            else
            {
                _final.Append(Latin1.GetString(buf, 0, n));
            }

            int term = IndexOfTerminator(_final);
            if (term >= 0 || _final.Length > Cap)
            {
                Emit(term >= 0 ? _final.ToString(0, term) : _final.ToString());
                _inFinal = false;
                _final.Length = 0;
            }
        }

        // レスポンス終了時。最終イベントを取りこぼしていたら末尾を生ダンプして実態を残す。
        public void Flush()
        {
            if (_inFinal && _final.Length > 0)
            {
                int term = IndexOfTerminator(_final);
                Emit(term >= 0 ? _final.ToString(0, term) : _final.ToString());
            }
            else if (!_emitted && _tail != null)
            {
                Dump("[stop:true 未検出のためレスポンス末尾をダンプ]\r\n" + Latin1.GetString(_tail));
            }
        }

        void UpdateTail(byte[] buf, int n)
        {
            int prev = _tail == null ? 0 : _tail.Length;
            int keep = Math.Min(TailKeep, prev + n);
            var t = new byte[keep];
            int fromBuf = Math.Min(keep, n);
            Buffer.BlockCopy(buf, n - fromBuf, t, keep - fromBuf, fromBuf);
            int rem = keep - fromBuf;
            if (rem > 0 && _tail != null) Buffer.BlockCopy(_tail, prev - rem, t, 0, rem);
            _tail = t;
        }

        // 圧縮JSON中に生の改行は無い(文字列内は \n にエスケープ済み)ので、
        // 生の \n\n または \r\n\r\n はSSEイベント境界を意味する
        static int IndexOfTerminator(StringBuilder sb)
        {
            for (int i = 1; i < sb.Length; i++)
            {
                if (sb[i] == '\n' && sb[i - 1] == '\n') return i - 1;
                if (i >= 3 && sb[i] == '\n' && sb[i - 1] == '\r' && sb[i - 2] == '\n' && sb[i - 3] == '\r') return i - 3;
            }
            return -1;
        }

        void Emit(string ev)
        {
            _emitted = true;
            long te = ExtractLong(ev, "\"tokens_evaluated\":");
            if (te < 0) te = ExtractLong(ev, "\"prompt_n\":");
            long tp = ExtractLong(ev, "\"tokens_predicted\":");
            if (tp < 0) tp = ExtractLong(ev, "\"predicted_n\":");
            bool limit = ev.IndexOf("\"stopped_limit\":true", StringComparison.Ordinal) >= 0
                      || ev.IndexOf("\"stop_type\":\"limit\"", StringComparison.Ordinal) >= 0;
            bool trunc = ev.IndexOf("\"truncated\":true", StringComparison.Ordinal) >= 0;
            Log("[DIAG] [RESP] tokens_evaluated=" + te + " tokens_predicted=" + tp +
                " truncated=" + trunc + " limit=" + limit + " ctx=" + _ctx);
            Dump(ev);
        }

        // 生の最終イベント(Latin-1でデコード。ASCIIのフィールド名/数値は読める。日本語本文は化ける)を追記
        void Dump(string ev)
        {
            try
            {
                string baseDir = _rulesPath != null ? Path.GetDirectoryName(_rulesPath)
                                                    : Path.GetDirectoryName(_logPath);
                if (baseDir == null) return;
                string path = Path.Combine(baseDir, "llm_proxy_resp_dump.log");
                string dump = ev;
                if (dump.Length > 8000)
                    dump = dump.Substring(0, 2500) + "\r\n…(" + (ev.Length - 6500) + " 文字省略)…\r\n" +
                           dump.Substring(dump.Length - 4000);
                var sb = new StringBuilder();
                sb.Append("==== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                  .Append(" final-event (").Append(ev.Length).Append(" 文字) ====\r\n")
                  .Append(dump).Append("\r\n\r\n");
                lock (DumpLock)
                {
                    File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 20 * 1024 * 1024) fi.Delete();
                }
            }
            catch { }
        }
    }

    static void CopyStream(Stream src, Stream dst)
    {
        var buf = new byte[65536];
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
    }

    static void SafeClose(TcpClient c)
    {
        try { c.Close(); } catch { }
    }

    // ---------------------------------------------------------------- 置換ルール

    static List<Rule> LoadRules(string path)
    {
        var rules = new List<Rule>();
        // タブセクション: 「#tab:名前」以降は有効タブ、「#offtab:名前」以降は無効タブ (中のルールを無視)。
        // タブ行が1つも無い旧形式のファイルは、全行が有効として読まれる。
        bool tabEnabled = true;
        foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = raw.Trim().TrimStart('\uFEFF').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#tab:", StringComparison.Ordinal)) { tabEnabled = true; continue; }
            if (line.StartsWith("#offtab:", StringComparison.Ordinal)) { tabEnabled = false; continue; }
            if (line.StartsWith("#", StringComparison.Ordinal)) continue;
            if (!tabEnabled) continue;
            int idx = line.IndexOf("=>", StringComparison.Ordinal);
            if (idx <= 0)
            {
                Log("[WARN] 不正なルール行を無視: " + line);
                continue;
            }
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
            string to = rest;
            rules.Add(new Rule { From = from, To = to, DispFrom = from, DispTo = to, Prob = prob });
            // Pythonの json.dumps(ensure_ascii=True) は日本語を \uXXXX にするので、その形も登録
            string ef = EscapeNonAscii(from);
            if (ef != from)
                rules.Add(new Rule
                {
                    From = ef,
                    To = EscapeNonAscii(to),
                    DispFrom = from + "〔エスケープ形式〕",
                    DispTo = to,
                    Prob = prob
                });
        }
        return rules;
    }

    // ルールファイルの更新をリクエストのたびに検知し、その場で再読込する (ゲーム再起動不要)。
    // 読み込みに失敗した場合 (保存の書き込み途中など) は前回のルールを使い続け、次回に再試行する。
    static void ReloadRulesIfChanged()
    {
        lock (RulesLock)
        {
            try
            {
                if (_rulesPath == null || !File.Exists(_rulesPath))
                {
                    // 起動後にファイルが作られた/移動された場合に備えて場所を探し直す
                    string found = FindRulesFile(_exeDir);
                    if (found == null)
                    {
                        if (Rules.Count > 0)
                        {
                            Rules = new List<Rule>();
                            _rulesStamp = DateTime.MinValue;
                            Log("[RULES] ルールファイルが見つからないため置換を無効化");
                        }
                        return;
                    }
                    _rulesPath = found;
                    _rulesStamp = DateTime.MinValue;
                }
                DateTime stamp = File.GetLastWriteTimeUtc(_rulesPath);
                if (stamp == _rulesStamp) return;
                Rules = LoadRules(_rulesPath);
                _rulesStamp = stamp;
                Log("[RULES] 読込: " + _rulesPath + " " + Rules.Count + "パターン(エスケープ版含む)");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static readonly Random Rng = new Random();

    static byte[] ApplyRules(byte[] body, string reqLine)
    {
        ReloadRulesIfChanged();
        var rules = Rules; // 再読込による差し替えに備えて参照を固定
        if (rules.Count == 0) return body;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(body); }
        catch { return body; } // バイナリはそのまま
        bool changed = false;

        // 同一「置換前」のルールをグループ化する (出現順を保持)
        var groups = new List<List<Rule>>();
        var index = new Dictionary<string, List<Rule>>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            List<Rule> g;
            if (!index.TryGetValue(rule.From, out g))
            {
                g = new List<Rule>();
                index.Add(rule.From, g);
                groups.Add(g);
            }
            g.Add(rule);
        }

        // グループごとに1回抽選し、置換するかどうかと置換先を決める。
        // 合計が100以下: 各ルールは (確率/100) で発動し、残りは無置換。
        // 合計が100超 : 各ルールは (確率/合計) で発動し、必ずどれかに置換される。
        foreach (var g in groups)
        {
            if (!text.Contains(g[0].From)) continue;
            int total = 0;
            foreach (var r in g) total += r.Prob;
            if (total <= 0)
            {
                Log("[SKIP] " + reqLine + " | \"" + Snip(g[0].DispFrom) + "\" 確率0のため置換せず");
                continue;
            }
            int denom = Math.Max(total, 100);
            int roll;
            lock (Rng) roll = Rng.Next(denom);
            Rule chosen = null;
            int acc = 0;
            foreach (var r in g)
            {
                acc += r.Prob;
                if (roll < acc) { chosen = r; break; }
            }
            if (chosen == null)
            {
                Log("[SKIP] " + reqLine + " | \"" + Snip(g[0].DispFrom) + "\" 確率判定により置換せず (" +
                    total + "/" + denom + ")");
                continue;
            }
            text = text.Replace(chosen.From, chosen.To);
            changed = true;
            Log("[REPLACE] " + reqLine + " | \"" + Snip(chosen.DispFrom) + "\" -> \"" + Snip(chosen.DispTo) +
                "\" (確率" + chosen.Prob + "/" + denom + ")");
        }
        return changed ? Encoding.UTF8.GetBytes(text) : body;
    }

    // json.dumps(ensure_ascii=True) と同じ形式 (小文字16進、UTF-16コード単位ごと) でエスケープ
    static string EscapeNonAscii(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c < 0x80) sb.Append(c);
            else sb.Append("\\u").Append(((int)c).ToString("x4"));
        }
        return sb.ToString();
    }

    static string Snip(string s)
    {
        return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
    }

    static string FindRulesFile(string startDir)
    {
        // 適用時に exe と同じ場所へ書かれたマーカー (MODフォルダの絶対パス) を最優先で使う。
        // MODフォルダの名前や場所に依存せず、ゲームルートにも何も置かない。
        string marker = Path.Combine(startDir, MarkerFileName);
        if (File.Exists(marker))
        {
            string dir = ReadMarkerDir(marker);
            if (dir != null)
            {
                string rp = Path.Combine(dir, RulesFileName);
                if (File.Exists(rp)) return rp;
            }
        }

        // 後方互換: マーカーが無い旧適用のために従来の探索も残す
        string d = startDir;
        for (int i = 0; i < 5 && !string.IsNullOrEmpty(d); i++)
        {
            string p = Path.Combine(d, "InstantaleLlmProxy", RulesFileName);
            if (File.Exists(p)) return p;
            p = Path.Combine(d, "mod_llm_proxy", RulesFileName);
            if (File.Exists(p)) return p;
            p = Path.Combine(d, RulesFileName);
            if (File.Exists(p)) return p;
            d = Path.GetDirectoryName(d);
        }
        return null;
    }

    // マーカーファイルからMODフォルダのパスを読む。
    // GUI (UTF-8 BOM付き) と apply.bat (ANSI) のどちらが書いた形式でも読めるようにする
    static string ReadMarkerDir(string marker)
    {
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

    // ---------------------------------------------------------------- JSON安定化
    // ゲームはLLMにJSON形式での出力を指示するが、素のLLM出力は壊れたJSONに
    // なりやすい (Python表記のTrue/None混入、カッコ閉じ忘れ、コードブロック等)。
    // /completion リクエストのプロンプト内に埋め込まれたPython dict形式の
    // スキーマを検出・変換し、llama-server の json_schema パラメータとして
    // 注入することで、文法制約付き生成により構文的に正しいJSONだけが
    // 出力されるようにする。
    // ・プロンプト内のPython表記スキーマも本物のJSON表記に置き換える
    // ・スキーマの解析に失敗した場合は汎用JSON文法(GBNF)で最低限の構文を保証
    // ・ゲーム側が既に json_schema / grammar を指定している場合は何もしない
    // ・スキーマ指示の無いリクエスト (自由文ナレーション等) には作用しない
    // ・無効化したい場合はMODフォルダに llm_proxy_jsonfix_off.txt を置く

    const string JsonFixOffFileName = "llm_proxy_jsonfix_off.txt";
    const string DedupOffFileName = "llm_proxy_dedup_off.txt";
    const string SingletonOffFileName = "llm_proxy_singleton_off.txt";
    const string DiagOnFileName = "llm_proxy_diag_on.txt";

    static bool JsonFixEnabled()
    {
        try
        {
            string baseDir = _rulesPath != null ? Path.GetDirectoryName(_rulesPath)
                                                : Path.GetDirectoryName(_logPath);
            if (baseDir == null) return true;
            return !File.Exists(Path.Combine(baseDir, JsonFixOffFileName));
        }
        catch { return true; }
    }

    // プロンプト重複ブロックの畳み込みはデフォルトON。無効化はMODフォルダに llm_proxy_dedup_off.txt。
    static bool DedupEnabled()
    {
        try
        {
            string baseDir = _rulesPath != null ? Path.GetDirectoryName(_rulesPath)
                                                : Path.GetDirectoryName(_logPath);
            if (baseDir == null) return true;
            return !File.Exists(Path.Combine(baseDir, DedupOffFileName));
        }
        catch { return true; }
    }

    // 本物のシングルトン化(集約)はデフォルトON。無効化はMODフォルダに llm_proxy_singleton_off.txt。
    // 無効時は各ラッパーが専用の本物を起動する(コンテキスト分割による context-exceeded の切り分け用)。
    static bool SingletonEnabled()
    {
        try
        {
            string baseDir = _rulesPath != null ? Path.GetDirectoryName(_rulesPath)
                                                : Path.GetDirectoryName(_logPath);
            if (baseDir == null) return true;
            return !File.Exists(Path.Combine(baseDir, SingletonOffFileName));
        }
        catch { return true; }
    }

    // 調査用の[DIAG]ログ/スキーマダンプはデフォルトOFF。実際に不具合を目撃したときだけ
    // MODフォルダに llm_proxy_diag_on.txt を置いて再度有効化する (通常運用でログを汚さない)
    static bool DiagEnabled()
    {
        try
        {
            string baseDir = _rulesPath != null ? Path.GetDirectoryName(_rulesPath)
                                                : Path.GetDirectoryName(_logPath);
            if (baseDir == null) return false;
            return File.Exists(Path.Combine(baseDir, DiagOnFileName));
        }
        catch { return false; }
    }

    // 診断ログ用に数値を取り出す。キーが無い/数値でない場合は -1 (「不明」の意味)
    static long ToLong(object o)
    {
        if (o is long) return (long)o;
        if (o is double) return (long)(double)o;
        return -1;
    }

    static readonly object SchemaDumpLock = new object();

    // 診断用: ゲームが実際に送ってきた json_schema の中身をそのまま別ファイルに追記する。
    // n_predict値とprompt文字数も併記し、「打ち切りで壊れているのか」「スキーマ自体が
    // 複雑すぎて変換に失敗しているのか」を後から突き合わせられるようにする。
    // 調査用の一時コードであり、通常運用では読まなくてよい。
    static void DumpSchema(string reqLine, object schema, long nPredict, string prompt)
    {
        try
        {
            string baseDir = _rulesPath != null ? Path.GetDirectoryName(_rulesPath)
                                                : Path.GetDirectoryName(_logPath);
            if (baseDir == null) return;
            string path = Path.Combine(baseDir, "llm_proxy_schema_dump.log");
            string schemaJson;
            try { schemaJson = JsonSerialize(schema); }
            catch (Exception ex) { schemaJson = "(シリアライズ失敗: " + ex.Message + ")"; }
            var sb = new StringBuilder();
            sb.Append("==== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(" ").Append(reqLine)
              .Append(" | n_predict=").Append(nPredict)
              .Append(" | prompt文字数=").Append(prompt != null ? prompt.Length : -1)
              .Append(" ====\r\n");
            sb.Append(schemaJson).Append("\r\n\r\n");
            lock (SchemaDumpLock)
            {
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 20 * 1024 * 1024) fi.Delete(); // 肥大化防止
            }
        }
        catch (Exception ex)
        {
            Log("[DIAG] スキーマダンプ失敗: " + ex.Message);
        }
    }

    static readonly object PromptDumpLock = new object();

    // 診断用: 受信プロンプト全文を別ファイルに追記する。再生成のたびに増える分を
    // 前後のダンプで突き合わせ、何が重複しているかを目視で確定するため。
    static void DumpPrompt(string reqLine, long nPredict, string prompt)
    {
        try
        {
            string baseDir = _rulesPath != null ? Path.GetDirectoryName(_rulesPath)
                                                : Path.GetDirectoryName(_logPath);
            if (baseDir == null) return;
            string path = Path.Combine(baseDir, "llm_proxy_prompt_dump.log");
            var sb = new StringBuilder();
            sb.Append("==== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(" ").Append(reqLine)
              .Append(" | n_predict=").Append(nPredict)
              .Append(" | prompt文字数=").Append(prompt.Length)
              .Append(" ====\r\n").Append(prompt).Append("\r\n\r\n");
            lock (PromptDumpLock)
            {
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 20 * 1024 * 1024) fi.Delete(); // 肥大化防止
            }
        }
        catch (Exception ex)
        {
            Log("[DIAG] プロンプトダンプ失敗: " + ex.Message);
        }
    }

    // プロンプト内に重複した大きなブロック(再生成のたびに固定量ずつ増える原因の疑い)を
    // 検出し、長さ・位置・先頭断片を返す。無ければ null。
    // 9等分の各点に置いた128文字プローブが2度目に出現するかを見て、当たれば前後へ伸ばして
    // 重複区間の全長を測る(重複ブロックがこの粒度なら少なくとも1点が内部に当たる)。
    internal static string DetectDuplicateBlock(string p)
    {
        if (p == null || p.Length < 512) return null;
        const int Probe = 128;
        int bestLen = 0, bestA = 0, bestB = 0;
        for (int k = 1; k <= 8; k++)
        {
            int off = (int)((long)p.Length * k / 9);
            if (off + Probe >= p.Length) continue;
            string probe = p.Substring(off, Probe);
            int second = p.IndexOf(probe, off + Probe, StringComparison.Ordinal);
            if (second < 0) continue;
            int a = off, b = second;
            int fwd = Probe;
            while (a + fwd < p.Length && b + fwd < p.Length && p[a + fwd] == p[b + fwd]) fwd++;
            int bwd = 0;
            // 2区間が重ならない範囲で後方へ伸ばす (b側の開始が a側の終端を越えないように)
            while (a - bwd - 1 >= 0 && b - bwd - 1 > a + fwd - 1 && p[a - bwd - 1] == p[b - bwd - 1]) bwd++;
            int len = fwd + bwd;
            if (len > bestLen) { bestLen = len; bestA = a - bwd; bestB = b - bwd; }
        }
        if (bestLen < 200) return null; // 短い偶然の一致は無視
        string head = p.Substring(bestA, Math.Min(60, bestLen)).Replace("\r", "").Replace("\n", "\\n");
        return "len=" + bestLen + " pos1=" + bestA + " pos2=" + bestB + " 先頭=\"" + head + "…\"";
    }

    // プロンプト内で大きなブロックが直後に完全一致で繰り返される(tandem repeat)場合、1個に畳む。
    // 完全一致の隣接コピーだけを畳むので意味は変わらない。畳めなければ元の文字列をそのまま返す。
    // 9等分の各点に128文字プローブを置き、2度目の出現との距離を周期候補として前後に伸ばし、
    // 周期領域が2コピー分以上あれば畳み込む。偶然の一致や正当な短い定型を守るため周期は1000文字以上に限定。
    internal static string CollapseRepeatedBlocks(string p)
    {
        if (p == null || p.Length < 2048) return p;
        const int Probe = 128;
        const int MinPeriod = 1000;
        for (int guard = 0; guard < 8; guard++)
        {
            int foundPeriod = 0, foundLo = 0;
            for (int k = 1; k <= 8 && foundPeriod == 0; k++)
            {
                int off = (int)((long)p.Length * k / 9);
                if (off + Probe >= p.Length) continue;
                string probe = p.Substring(off, Probe);
                int second = p.IndexOf(probe, off + Probe, StringComparison.Ordinal);
                if (second < 0) continue;
                int period = second - off;
                if (period < MinPeriod) continue;
                // この周期で隣接コピーが成立するか、前後に伸ばして周期領域を確定する
                int lo = off;
                while (lo - 1 >= 0 && p[lo - 1] == p[lo - 1 + period]) lo--;
                int hi = off;
                while (hi + period < p.Length && p[hi] == p[hi + period]) hi++;
                int spanLen = (hi - lo) + period; // 周期領域の全長
                if (spanLen >= 2 * period) { foundPeriod = period; foundLo = lo; }
            }
            if (foundPeriod == 0) break;
            // foundLo から foundPeriod ごとに同一コピーが続く分を1個だけ残して除去する
            int posEnd = foundLo;
            while (posEnd + foundPeriod <= p.Length &&
                   string.CompareOrdinal(p, posEnd, p, foundLo, foundPeriod) == 0)
                posEnd += foundPeriod;
            p = p.Substring(0, foundLo + foundPeriod) + p.Substring(posEnd);
        }
        return p;
    }

    // JSONFIXの対象は生成リクエストだけ。/apply-template などの前処理には触らない。
    // リクエスト行「POST /completion HTTP/1.1」からパス部分だけを取り出して判定する
    static bool IsCompletionRequest(string reqLine)
    {
        if (!reqLine.StartsWith("POST ", StringComparison.Ordinal)) return false;
        int sp = reqLine.IndexOf(' ', 5);
        string path = sp > 0 ? reqLine.Substring(5, sp - 5) : reqLine.Substring(5);
        int q = path.IndexOf('?');
        if (q >= 0) path = path.Substring(0, q);
        return path == "/completion" || path == "/completions" || path == "/v1/completions";
    }

    internal static byte[] ApplyJsonSchemaFix(byte[] body, string reqLine)
    {
        try
        {
            if (!JsonFixEnabled()) return body;
            string text;
            try { text = new UTF8Encoding(false, true).GetString(body); }
            catch { return body; } // バイナリはそのまま

            int pos = 0;
            object rootObj;
            try { rootObj = ParseLiteral(text, ref pos); }
            catch { return body; } // JSONとして読めないボディは触らない
            OrderedDictionary root = rootObj as OrderedDictionary;
            if (root == null) return body;

            // 診断用: ゲームが実際に送ってきたリクエストのトップレベルキー一覧やスキーマ本体を記録する。
            // json_schema/grammar をゲーム側が既に指定しているかどうかの実地調査用。
            // 通常運用ではログを汚さないよう、MODフォルダに llm_proxy_diag_on.txt がある時だけ動く。
            if (DiagEnabled())
            {
                var keyNames = new List<string>();
                foreach (System.Collections.DictionaryEntry e in root) keyNames.Add((string)e.Key);
                long nPredictDiag = root.Contains("n_predict") ? ToLong(root["n_predict"]) : -1;
                string promptDiag = root.Contains("prompt") ? root["prompt"] as string : null;
                Log("[DIAG] " + reqLine + " | 受信キー: " + string.Join(", ", keyNames.ToArray()) +
                    " | n_predict=" + nPredictDiag +
                    " | prompt文字数=" + (promptDiag != null ? promptDiag.Length : -1));
                if (root.Contains("json_schema"))
                    DumpSchema(reqLine, root["json_schema"], nPredictDiag, promptDiag);
                if (promptDiag != null)
                {
                    // 再生成のたびにプロンプトが固定量ずつ増える(=同じブロックの重複追加)疑いを
                    // 実データで確定させる。重複ブロックを検出したら長さ・位置を1行で報告し、
                    // 全文も別ファイルにダンプして重複部分を目視できるようにする。
                    string dup = DetectDuplicateBlock(promptDiag);
                    if (dup != null) Log("[DIAG] [DUP] " + reqLine + " | 重複ブロック検出 " + dup);
                    DumpPrompt(reqLine, nPredictDiag, promptDiag);
                }
            }

            // プロンプト内で大きなブロックが直後に完全一致で繰り返される(tandem repeat)場合、1個に畳む。
            // ゲームがスキーマ系システムメッセージを再生成のたびに重複追加し、コンテキスト枯渇→500エラーを
            // 起こす不具合の対策。json_schemaの有無に関わらず適用する。無効化は MODフォルダに llm_proxy_dedup_off.txt。
            bool bodyChanged = false;
            if (DedupEnabled() && root.Contains("prompt"))
            {
                string pr0 = root["prompt"] as string;
                if (pr0 != null)
                {
                    string collapsed = CollapseRepeatedBlocks(pr0);
                    if (collapsed.Length != pr0.Length)
                    {
                        root["prompt"] = collapsed;
                        bodyChanged = true;
                        Log("[DEDUP] " + reqLine + " | 重複ブロックを畳んだ " +
                            pr0.Length + "→" + collapsed.Length + "文字");
                    }
                }
            }

            if (root.Contains("json_schema") || root.Contains("grammar"))
                return bodyChanged ? Encoding.UTF8.GetBytes(JsonSerialize(root)) : body;
            string prompt = root.Contains("prompt") ? root["prompt"] as string : null;
            if (prompt == null)
                return bodyChanged ? Encoding.UTF8.GetBytes(JsonSerialize(root)) : body;

            int schemaStart = FindSchemaStart(prompt);
            if (schemaStart < 0) // JSON出力を要求しないリクエスト
                return bodyChanged ? Encoding.UTF8.GetBytes(JsonSerialize(root)) : body;

            object schema = null;
            int schemaEnd = schemaStart;
            try
            {
                int p = schemaStart;
                schema = ParseLiteral(prompt, ref p);
                schemaEnd = p;
            }
            catch { schema = null; }

            if (schema is OrderedDictionary)
            {
                string schemaJson = JsonSerialize(schema);
                // プロンプト内のPython表記スキーマも本物のJSON表記に揃える
                root["prompt"] = prompt.Substring(0, schemaStart) + schemaJson +
                                 prompt.Substring(schemaEnd);
                root["json_schema"] = schema;
                Log("[JSONFIX] " + reqLine + " | json_schemaを注入 (" + schemaJson.Length + "文字)");
            }
            else
            {
                // スキーマらしき部分はあるが解析できない → 最低限の構文だけ保証
                root["grammar"] = GenericJsonGrammar;
                Log("[JSONFIX] " + reqLine + " | スキーマ解析失敗のため汎用JSON文法を注入");
            }
            return Encoding.UTF8.GetBytes(JsonSerialize(root));
        }
        catch (Exception ex)
        {
            Log("[JSONFIX] 失敗のため無加工で中継: " + ex.Message);
            return body;
        }
    }

    // プロンプト内のスキーマ開始位置。Python表記/JSON表記のどちらでも検出する
    static readonly string[] SchemaMarkers =
    {
        "{'$defs':", "{'properties':", "{\"$defs\":", "{\"properties\":"
    };

    static int FindSchemaStart(string prompt)
    {
        int best = -1;
        foreach (string m in SchemaMarkers)
        {
            int i = prompt.IndexOf(m, StringComparison.Ordinal);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        return best;
    }

    // JSONとPythonリテラル (dict/list/tuple/str/数値/True/False/None) の両方を
    // 同じ文法のスーパーセットとして解析する。dictはOrderedDictionary (キー順保持)。
    internal static object ParseLiteral(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) throw new FormatException("入力の末尾に到達");
        char c = s[i];
        if (c == '{')
        {
            var d = new OrderedDictionary();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                string key = ParseLiteral(s, ref i) as string;
                if (key == null) throw new FormatException("dictのキーが文字列でない");
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("':'が無い");
                i++;
                d[key] = ParseLiteral(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("'}'が無い");
                if (s[i] == ',')
                {
                    i++;
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == '}') { i++; return d; } // 末尾カンマ許容
                    continue;
                }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("dictの区切りが不正: pos=" + i);
            }
        }
        if (c == '[' || c == '(')
        {
            char close = c == '[' ? ']' : ')';
            var list = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == close) { i++; return list; }
            while (true)
            {
                list.Add(ParseLiteral(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("リストが閉じない");
                if (s[i] == ',')
                {
                    i++;
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == close) { i++; return list; }
                    continue;
                }
                if (s[i] == close) { i++; return list; }
                throw new FormatException("リストの区切りが不正: pos=" + i);
            }
        }
        if (c == '\'' || c == '"') return ParseQuotedString(s, ref i);
        if (MatchWord(s, i, "True")) { i += 4; return true; }
        if (MatchWord(s, i, "False")) { i += 5; return false; }
        if (MatchWord(s, i, "None")) { i += 4; return null; }
        if (MatchWord(s, i, "true")) { i += 4; return true; }
        if (MatchWord(s, i, "false")) { i += 5; return false; }
        if (MatchWord(s, i, "null")) { i += 4; return null; }
        if (c == '-' || c == '+' || (c >= '0' && c <= '9')) return ParseNumber(s, ref i);
        throw new FormatException("不明なリテラル: pos=" + i);
    }

    static void SkipWs(string s, ref int i)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
            else break;
        }
    }

    // 位置iがちょうど単語wか。直後が英数字/_ なら別の識別子の一部とみなして不一致にする
    // (例: "None" と "NoneOfThem" を取り違えない)
    static bool MatchWord(string s, int i, string w)
    {
        if (i + w.Length > s.Length) return false;
        if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false;
        int e = i + w.Length;
        if (e < s.Length)
        {
            char n = s[e];
            if (char.IsLetterOrDigit(n) || n == '_') return false;
        }
        return true;
    }

    // 引用符はシングル/ダブルの両方を受け付ける (Python表記のdictは 'key' を使う)。
    // エスケープもJSONの範囲に加えてPython固有の \x41 や \' まで解釈する
    static string ParseQuotedString(string s, ref int i)
    {
        char q = s[i];
        i++;
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= s.Length) throw new FormatException("文字列が閉じない");
            char c = s[i];
            if (c == q) { i++; return sb.ToString(); }
            if (c == '\\')
            {
                if (i + 1 >= s.Length) throw new FormatException("エスケープが不完全");
                char e = s[i + 1];
                switch (e)
                {
                    case 'n': sb.Append('\n'); i += 2; break;
                    case 't': sb.Append('\t'); i += 2; break;
                    case 'r': sb.Append('\r'); i += 2; break;
                    case 'b': sb.Append('\b'); i += 2; break;
                    case 'f': sb.Append('\f'); i += 2; break;
                    case '0': sb.Append('\0'); i += 2; break;
                    case 'x':
                        if (i + 3 >= s.Length) throw new FormatException("\\xが不完全");
                        sb.Append((char)Convert.ToInt32(s.Substring(i + 2, 2), 16));
                        i += 4;
                        break;
                    case 'u':
                        if (i + 5 >= s.Length) throw new FormatException("\\uが不完全");
                        sb.Append((char)Convert.ToInt32(s.Substring(i + 2, 4), 16));
                        i += 6;
                        break;
                    default: sb.Append(e); i += 2; break; // \' \" \\ \/ など
                }
                continue;
            }
            sb.Append(c);
            i++;
        }
    }

    // 整数は long、小数点や指数を含むものは double で返す。
    // スキーマ中の maxLength などを 3.0 のような形に変えないため、整数はlongのまま保つ
    static object ParseNumber(string s, ref int i)
    {
        int start = i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        bool isDouble = false;
        while (i < s.Length)
        {
            char c = s[i];
            if (c >= '0' && c <= '9') { i++; continue; }
            if (c == '.' || c == 'e' || c == 'E') { isDouble = true; i++; continue; }
            if ((c == '-' || c == '+') && (s[i - 1] == 'e' || s[i - 1] == 'E')) { i++; continue; }
            break;
        }
        string num = s.Substring(start, i - start);
        if (!isDouble)
        {
            long l;
            if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                return l;
        }
        return double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    internal static string JsonSerialize(object o)
    {
        var sb = new StringBuilder(4096);
        WriteJson(o, sb);
        return sb.ToString();
    }

    static void WriteJson(object o, StringBuilder sb)
    {
        if (o == null) { sb.Append("null"); return; }
        if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
        string str = o as string;
        if (str != null) { WriteJsonString(str, sb); return; }
        if (o is long) { sb.Append(((long)o).ToString(CultureInfo.InvariantCulture)); return; }
        if (o is int) { sb.Append(((int)o).ToString(CultureInfo.InvariantCulture)); return; }
        if (o is double)
        {
            double d = (double)o;
            if (d == Math.Floor(d) && !double.IsInfinity(d) && Math.Abs(d) < 9e15)
                sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
            else
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
            return;
        }
        OrderedDictionary od = o as OrderedDictionary;
        if (od != null)
        {
            sb.Append('{');
            bool first = true;
            foreach (System.Collections.DictionaryEntry e in od)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteJsonString((string)e.Key, sb);
                sb.Append(':');
                WriteJson(e.Value, sb);
            }
            sb.Append('}');
            return;
        }
        System.Collections.IEnumerable en = o as System.Collections.IEnumerable;
        if (en != null)
        {
            sb.Append('[');
            bool first = true;
            foreach (object item in en)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteJson(item, sb);
            }
            sb.Append(']');
            return;
        }
        throw new InvalidOperationException("シリアライズ不能な型: " + o.GetType());
    }

    // 非ASCIIはすべて\uXXXXにエスケープする (出力ボディをASCII安全にするため)
    static void WriteJsonString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7e)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    // llama.cpp 付属の json.gbnf 相当。スキーマ変換に失敗したときの保険で、
    // 少なくとも「構文的に正しいJSONオブジェクト」だけを出力させる
    const string GenericJsonGrammar =
        "root   ::= object\n" +
        "value  ::= object | array | string | number | (\"true\" | \"false\" | \"null\") ws\n" +
        "object ::= \"{\" ws ( string \":\" ws value (\",\" ws string \":\" ws value)* )? \"}\" ws\n" +
        "array  ::= \"[\" ws ( value (\",\" ws value)* )? \"]\" ws\n" +
        "string ::= \"\\\"\" ( [^\"\\\\\\x7F\\x00-\\x1F] | \"\\\\\" ([\"\\\\bfnrt] | \"u\" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]) )* \"\\\"\" ws\n" +
        "number ::= (\"-\"? ([0-9] | [1-9] [0-9]{0,15})) (\".\" [0-9]+)? ([eE] [-+]? [0-9] [1-9]{0,15})? ws\n" +
        "ws     ::= | \" \" | \"\\n\" [ \\t]{0,20}\n";

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

    // 複数スレッド (接続ごと) から呼ばれるので追記は直列化する。
    // ログ出力の失敗でゲームを止めたくないので、例外はすべて握り潰す
    static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                File.AppendAllText(
                    _logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + msg + "\r\n",
                    Encoding.UTF8);
            }
        }
        catch { }
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
