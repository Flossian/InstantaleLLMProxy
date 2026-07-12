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
// ルールファイル: <MODフォルダ>\llm_replacements.txt (UTF-8)
//   MODフォルダの場所は、適用時に exe と同じ場所に書かれる llm_proxy_dir.txt で
//   確実に特定する。無い場合のみ後方互換で上位フォルダから探索する。
//   1行1ルール「置換前=>置換後」。行頭 # はコメント。
//   GUIで無効化されたルールは「#off:」付きで保存され、コメントとして無視される。
//   ファイルの更新はリクエストごとに検知して自動再読込する (ゲーム再起動不要)。
//   Pythonクライアントが日本語を \uXXXX エスケープして送る場合にも対応するため、
//   各ルールのエスケープ版も自動生成して照合する。
//
// ログ: ルールファイルと同じフォルダの llm_proxy.log
//
// ビルド: src\llm_proxy_apply.bat または管理GUI (InstantaleLlmProxy.exe) が行う
//         (Windows同梱の csc.exe / .NET Framework 4.x)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
    class Rule
    {
        public string From;
        public string To;
        public string DispFrom;
        public string DispTo;
    }

    // 現在有効なルール一覧。再読込時は新しいリストへ丸ごと差し替える (読む側はロック不要)
    static volatile List<Rule> Rules = new List<Rule>();
    static readonly object RulesLock = new object();
    static string _exeDir;
    static string _rulesPath;
    static DateTime _rulesStamp = DateTime.MinValue;
    static int _upstreamPort;
    static Process _child;
    static IntPtr _job = IntPtr.Zero;

    static int Main(string[] args)
    {
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

    static int Run(string exeDir, string rulesPath, string[] args)
    {
        _exeDir = exeDir;
        _rulesPath = rulesPath;
        ReloadRulesIfChanged();
        Log("[BOOT] 起動 args: " + string.Join(" ", args));
        Log("[BOOT] ルール: " + (_rulesPath ?? "(なし)") + " " + Rules.Count + "パターン(エスケープ版含む)");

        _upstreamPort = FindFreePort();

        // --port / --host を解析し、子プロセス用引数ではポートを内部ポートに差し替える
        int listenPort = 8080;          // llama-server のデフォルト
        string listenHost = "127.0.0.1";
        bool portFound = false;
        var childArgs = new List<string>(args);
        for (int i = 0; i < childArgs.Count; i++)
        {
            string a = childArgs[i];
            if (a == "--port" && i + 1 < childArgs.Count)
            {
                int.TryParse(childArgs[i + 1], out listenPort);
                childArgs[i + 1] = _upstreamPort.ToString();
                portFound = true;
            }
            else if (a.StartsWith("--port=", StringComparison.Ordinal))
            {
                int.TryParse(a.Substring(7), out listenPort);
                childArgs[i] = "--port=" + _upstreamPort;
                portFound = true;
            }
            else if (a == "--host" && i + 1 < childArgs.Count) listenHost = childArgs[i + 1];
            else if (a.StartsWith("--host=", StringComparison.Ordinal)) listenHost = a.Substring(7);
        }
        if (!portFound)
        {
            // ポート未指定ならデフォルト8080で待ち受け、本物は内部ポートへ
            childArgs.Add("--port");
            childArgs.Add(_upstreamPort.ToString());
        }

        string realExe = Path.Combine(exeDir, RealExeName);
        if (!File.Exists(realExe))
        {
            Log("[FATAL] 本物のサーバが見つかりません: " + realExe);
            return 1;
        }

        _child = StartChild(realExe, childArgs);
        SetupJobObject(_child); // ラッパーが死んだら本物も道連れにする

        // 子プロセス監視: 本物が終了したらラッパーも同じコードで終了
        var watchdog = new Thread(WatchChild);
        watchdog.IsBackground = true;
        watchdog.Start();

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

    // 本物のサーバへ接続する。モデルロード中でポートが開くまで再試行する
    static TcpClient ConnectUpstream()
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            try { return new TcpClient("127.0.0.1", _upstreamPort); }
            catch (SocketException) { }
            try { if (_child.HasExited) return null; } catch { return null; }
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

        byte[] body = new byte[contentLength];
        int off = 0;
        while (off < contentLength)
        {
            int n = reader.Read(body, off, contentLength - off);
            if (n <= 0) return false;
            off += n;
        }

        byte[] newBody = contentLength > 0 ? ApplyRules(body, reqLine) : body;
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

    // \r\n\r\n までを読み取る。開始前にEOFなら null
    static byte[] ReadHeaderBlock(Stream s)
    {
        var buf = new List<byte>(1024);
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
            if (buf.Count > 1024 * 1024) throw new InvalidOperationException("ヘッダが大きすぎます");
        }
    }

    static void PipeRaw(TcpClient src, TcpClient dst)
    {
        try
        {
            NetworkStream a = src.GetStream(), b = dst.GetStream();
            var buf = new byte[65536];
            int n;
            while ((n = a.Read(buf, 0, buf.Length)) > 0) b.Write(buf, 0, n);
        }
        catch { }
        finally
        {
            // 片方が閉じたら両方閉じて、反対方向のスレッドも解放する
            SafeClose(src);
            SafeClose(dst);
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
        foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = raw.Trim().TrimStart('\uFEFF').Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            int idx = line.IndexOf("=>", StringComparison.Ordinal);
            if (idx <= 0)
            {
                Log("[WARN] 不正なルール行を無視: " + line);
                continue;
            }
            string from = line.Substring(0, idx);
            string to = line.Substring(idx + 2);
            rules.Add(new Rule { From = from, To = to, DispFrom = from, DispTo = to });
            // Pythonの json.dumps(ensure_ascii=True) は日本語を \uXXXX にするので、その形も登録
            string ef = EscapeNonAscii(from);
            if (ef != from)
                rules.Add(new Rule
                {
                    From = ef,
                    To = EscapeNonAscii(to),
                    DispFrom = from + "〔エスケープ形式〕",
                    DispTo = to
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

    static byte[] ApplyRules(byte[] body, string reqLine)
    {
        ReloadRulesIfChanged();
        var rules = Rules; // 再読込による差し替えに備えて参照を固定
        if (rules.Count == 0) return body;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(body); }
        catch { return body; } // バイナリはそのまま
        bool changed = false;
        foreach (var rule in rules)
        {
            if (text.Contains(rule.From))
            {
                text = text.Replace(rule.From, rule.To);
                changed = true;
                Log("[REPLACE] " + reqLine + " | \"" + Snip(rule.DispFrom) + "\" -> \"" + Snip(rule.DispTo) + "\"");
            }
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

    // ---------------------------------------------------------------- ユーティリティ

    static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

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

    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    const int JobObjectExtendedLimitInformation = 9;

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
