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
    static int _upstreamPort;
    static Process _child;
    static IntPtr _job = IntPtr.Zero;

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
    // トークンが生成されるそばから届くよう、溜めずに読めた分だけ即書き出す
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
            }

            if (root.Contains("json_schema") || root.Contains("grammar")) return body;
            string prompt = root.Contains("prompt") ? root["prompt"] as string : null;
            if (prompt == null) return body;

            int schemaStart = FindSchemaStart(prompt);
            if (schemaStart < 0) return body; // JSON出力を要求しないリクエスト

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
