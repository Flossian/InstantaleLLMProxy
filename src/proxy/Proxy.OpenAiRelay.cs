// ----------------------------------------------------------------------------
// Proxy.OpenAiRelay.cs (LlmProxy partial: 任意OpenAI互換サーバへの中継 (ラッパー翻訳 / スタンドアロン中継))
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
    // ゲーム側のOpenAI互換設定がこの中継サーバを指す構成。openai_listen_port で待ち受け、
    // openai_endpoint へ中継する。GUIが起動/停止と状態確認に使えるよう、起動時に
    // MODフォルダへ pid,port を書く (llm_proxy_relay.txt)。
    static int RunOpenAiRelay()
    {
        _openai = LoadOpenAiConfig(false);
        if (_openai == null)
        {
            Log("[FATAL] 中継モードには openai_endpoint の設定が必要です (" + SettingsFileName + ")");
            return 1;
        }
        int listenPort;
        int.TryParse(ReadSettingString("openai_listen_port", ""), out listenPort);
        if (listenPort <= 0 || listenPort > 65535)
        {
            Log("[FATAL] 中継モードには openai_listen_port の設定が必要です (" + SettingsFileName + ")");
            return 1;
        }
        EnableModernTls();

        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, listenPort);
            listener.Start();
        }
        catch (SocketException ex)
        {
            Log("[FATAL] 中継ポート " + listenPort + " で待ち受けできません (既に起動中か他プロセスが使用中): " + ex.Message);
            return 1;
        }
        WriteRelayPid(listenPort);
        Log("[BOOT] OpenAI互換中継: listen 127.0.0.1:" + listenPort + " -> " + _openai.Endpoint +
            (string.IsNullOrEmpty(_openai.Model) ? "" : " (model既定 " + _openai.Model + ")"));

        var gameWatch = new Thread(WatchGameProcess) { IsBackground = true };
        gameWatch.Start();

        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            TcpClient c = client;
            var t = new Thread(() => HandleClientOpenAi(c));
            t.IsBackground = true;
            t.Start();
        }
    }

    static void WriteRelayPid(int port)
    {
        try
        {
            string baseDir = _rulesPath != null ? Path.GetDirectoryName(_rulesPath)
                                                : Path.GetDirectoryName(_logPath);
            if (baseDir == null) return;
            File.WriteAllText(Path.Combine(baseDir, RelayPidFileName),
                Process.GetCurrentProcess().Id + "," + port);
        }
        catch { }
    }

    // ゲーム本体(instantale.exe)のプロセス名。中継サーバはGUIの子プロセスにしない
    // (GUIを閉じても動き続ける)ため、代わりにゲーム本体の生死を監視して自動終了する。
    const string GameProcessName = "instantale";

    // ゲームがまだ起動していない間(中継サーバを先に起動した直後など)に誤って
    // 即終了しないよう、一度でもゲームの起動を確認してから「いなくなったら終了」に切り替える。
    static void WatchGameProcess()
    {
        bool everSeen = false;
        while (true)
        {
            Thread.Sleep(10000);
            bool running = IsGameRunning();
            if (running) everSeen = true;
            else if (everSeen)
            {
                Log("[OPENAI] ゲーム(" + GameProcessName + ".exe)の終了を検知、中継サーバーを終了します");
                Environment.Exit(0);
            }
        }
    }

    static bool IsGameRunning()
    {
        Process[] procs = Process.GetProcessesByName(GameProcessName);
        try { return procs.Length > 0; }
        finally { foreach (Process p in procs) p.Dispose(); }
    }

    // .NET Framework 4 の既定は TLS1.2 が無効なことがあるので明示的に有効化する。
    // 併せて同一ホストへの同時接続上限を引き上げる(既定2だと並行生成が詰まる)。
    static void EnableModernTls()
    {
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { } // Tls12
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)768; } catch { }  // Tls11
        try { ServicePointManager.DefaultConnectionLimit = 32; } catch { }
        try { ServicePointManager.Expect100Continue = false; } catch { }
    }

    // ---- 接続処理(OpenAI互換モード) ----

    sealed class HttpReq
    {
        public string Method;
        public string Path;
        public string RawReqLine;
        public byte[] Body;
        public bool KeepAlive = true;
        public string Auth;    // Authorization ヘッダの値 (ゲーム側APIキーの引き継ぎ用)
    }

    // ネイティブ経路(HandleClient)と違い、こちらはリクエスト全体をメモリに読んでから
    // 完全なHTTPレスポンス(ヘッダ+ボディ)を組み立てて返す方式。ゲーム→上流→ゲームの
    // 中間で内容を作り替える(llama.cpp形式⇔OpenAI形式の翻訳)必要があるため、
    // レスポンス方向を無加工で素通しする PipeRaw は使えない
    static void HandleClientOpenAi(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            NetworkStream cs = client.GetStream();
            var reader = new BufferedStream(cs, 16384);
            while (true)
            {
                HttpReq req = ReadHttpRequest(reader);
                if (req == null) break;              // クライアント切断(keep-aliveの正常終了)
                if (!DispatchOpenAi(req, cs)) break;  // keep-alive でなければ接続終了
            }
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log("[OPENAI] client: " + ex.Message); }
        finally { SafeClose(client); }
    }

    // ネイティブ経路の ForwardOneRequest と役割は同じ(ヘッダ解析+ボディ読取)だが、
    // こちらはボディをその場で上流へ転送せず HttpReq に保持して返す。
    // OpenAI互換モードは経路によって行き先(/apply-templateの自前処理、上流サーバへの
    // 翻訳・素通し等)がまちまちで、ヘッダ解析の直後に転送先を決め打てないため
    static HttpReq ReadHttpRequest(BufferedStream reader)
    {
        byte[] header = ReadHeaderBlock(reader);
        if (header == null) return null;
        string headerText = Encoding.GetEncoding(28591).GetString(header);
        string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        var req = new HttpReq();
        req.RawReqLine = lines[0];
        string[] parts = lines[0].Split(' ');
        req.Method = parts.Length > 0 ? parts[0] : "";
        string path = parts.Length > 1 ? parts[1] : "/";
        int q = path.IndexOf('?');
        if (q >= 0) path = path.Substring(0, q);
        req.Path = path;

        int contentLength = 0;
        bool chunked = false;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            int c = lines[i].IndexOf(':');
            if (c <= 0) continue;
            string name = lines[i].Substring(0, c).Trim().ToLowerInvariant();
            string val = lines[i].Substring(c + 1).Trim();
            if (name == "content-length") int.TryParse(val, out contentLength);
            else if (name == "transfer-encoding" && val.ToLowerInvariant().Contains("chunked")) chunked = true;
            else if (name == "connection")
                req.KeepAlive = val.ToLowerInvariant().IndexOf("close", StringComparison.Ordinal) < 0;
            else if (name == "authorization") req.Auth = val;
        }

        if (chunked) req.Body = ReadChunkedBody(reader);
        else
        {
            byte[] body = new byte[contentLength];
            int off = 0;
            while (off < contentLength)
            {
                int n = reader.Read(body, off, contentLength - off);
                if (n <= 0) return null; // ボディ途中で切断: 不完全なリクエストは処理しない
                off += n;
            }
            req.Body = body;
        }
        return req;
    }

    // チャンク転送のボディ(ゲームは通常Content-Lengthだが保険で対応)
    static byte[] ReadChunkedBody(BufferedStream reader)
    {
        var ms = new MemoryStream();
        while (true)
        {
            string sizeLine = ReadLineAscii(reader);
            if (sizeLine == null) break;
            int semi = sizeLine.IndexOf(';');
            if (semi >= 0) sizeLine = sizeLine.Substring(0, semi);
            int size;
            if (!int.TryParse(sizeLine.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size)) break;
            if (size == 0) { ReadLineAscii(reader); break; } // 末尾CRLF
            byte[] buf = new byte[size];
            int off = 0;
            while (off < size)
            {
                int n = reader.Read(buf, off, size - off);
                if (n <= 0) break;
                off += n;
            }
            ms.Write(buf, 0, off);
            ReadLineAscii(reader); // チャンク後のCRLF
        }
        return ms.ToArray();
    }

    static string ReadLineAscii(Stream s)
    {
        var sb = new StringBuilder();
        int b = s.ReadByte();
        if (b < 0) return null;
        while (b >= 0)
        {
            if (b == '\n') break;
            if (b != '\r') sb.Append((char)b);
            b = s.ReadByte();
        }
        return sb.ToString();
    }

    static bool DispatchOpenAi(HttpReq req, NetworkStream cs)
    {
        string p = req.Path;
        if (p == "/apply-template") return ServeApplyTemplate(req, cs);
        // llama.cpp ネイティブの /completion は翻訳、OpenAIプロトコルの /chat/completions は
        // 素通し中継。後者はクライアント(ゲーム)がOpenAI形式のレスポンスを期待している
        if (p == "/completion" || p == "/completions" || p == "/v1/completions")
            return ServeCompletion(req, cs);
        if (p == "/v1/chat/completions" || p == "/chat/completions")
            return ServeChatPassthrough(req, cs);
        if (p == "/health" || p == "/v1/health")
            return WriteJsonResponse(cs, "{\"status\":\"ok\"}", req.KeepAlive);
        if (p == "/v1/models" || p == "/models")
            return WriteJsonResponse(cs, ModelsJson(), req.KeepAlive);
        if (p == "/props")
            return WriteJsonResponse(cs, PropsJson(), req.KeepAlive);
        // 未知のパスは無害な空応答でプローブを止めない
        return WriteJsonResponse(cs, "{}", req.KeepAlive);
    }

    static string ModelsJson()
    {
        var root = new OrderedDictionary();
        root["object"] = "list";
        var m = new OrderedDictionary();
        m["id"] = _openai.Model;
        m["object"] = "model";
        m["owned_by"] = "instantale-llm-proxy";
        var list = new List<object>();
        list.Add(m);
        root["data"] = list;
        return JsonSerialize(root);
    }

    static string PropsJson()
    {
        // ゲームがコンテキスト長等を参照する場合に備えた最小限の /props 応答
        var root = new OrderedDictionary();
        var dgs = new OrderedDictionary();
        dgs["n_ctx"] = (long)(_ctxSize > 0 ? _ctxSize : 32768);
        root["default_generation_settings"] = dgs;
        root["total_slots"] = 1L;
        root["model_path"] = _openai.Model;
        root["chat_template"] = "";
        return JsonSerialize(root);
    }

    // /apply-template : messages を1本のプロンプト文字列へ畳んで返す。
    static bool ServeApplyTemplate(HttpReq req, NetworkStream cs)
    {
        string prompt = "";
        try
        {
            byte[] replaced = ApplyRules(req.Body, req.RawReqLine);
            string text = new UTF8Encoding(false, true).GetString(replaced);
            int pos = 0;
            var root = ParseLiteral(text, ref pos) as OrderedDictionary;
            List<object> msgs = null;
            if (root != null && root.Contains("messages")) msgs = root["messages"] as List<object>;
            prompt = BuildTemplatedPrompt(msgs);
        }
        catch (Exception ex) { Log("[OPENAI] apply-template 解析失敗: " + ex.Message); }
        var od = new OrderedDictionary();
        od["prompt"] = prompt;
        return WriteJsonResponse(cs, JsonSerialize(od), req.KeepAlive);
    }

    // messages を1本のプロンプトへ畳む。/completion 側の ParseTemplatedPrompt がこの
    // マーカーを見て messages を復元する。マーカーが崩れても素の文字列として送れる。
    static string BuildTemplatedPrompt(List<object> msgs)
    {
        var sb = new StringBuilder();
        if (msgs != null)
        {
            foreach (object o in msgs)
            {
                var md = o as OrderedDictionary;
                if (md == null) continue;
                string role = md.Contains("role") ? md["role"] as string : "user";
                string content = md.Contains("content") ? StringifyContent(md["content"]) : "";
                sb.Append("<|im_start|>").Append(role != null ? role : "user").Append('\n')
                  .Append(content != null ? content : "").Append("<|im_end|>\n");
            }
        }
        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    // content は文字列、または [{type:"text",text:"..."}] のような配列でありうる。テキストを連結。
    static string StringifyContent(object content)
    {
        string s = content as string;
        if (s != null) return s;
        var list = content as List<object>;
        if (list != null)
        {
            var sb = new StringBuilder();
            foreach (object part in list)
            {
                var pd = part as OrderedDictionary;
                if (pd != null && pd.Contains("text")) sb.Append(pd["text"] as string);
                else if (part is string) sb.Append((string)part);
            }
            return sb.ToString();
        }
        return content == null ? "" : content.ToString();
    }

    // プロンプト文字列を messages へ復元する。BuildTemplatedPrompt のマーカーが無ければ
    // 全体を1つのuserメッセージとして扱う(ゲームが別経路で組んだプロンプトへの保険)。
    static List<object> ParseTemplatedPrompt(string prompt)
    {
        var messages = new List<object>();
        if (prompt == null) prompt = "";
        if (prompt.IndexOf("<|im_start|>", StringComparison.Ordinal) < 0)
        {
            var m = new OrderedDictionary();
            m["role"] = "user";
            m["content"] = prompt;
            messages.Add(m);
            return messages;
        }
        int idx = 0;
        while (true)
        {
            int s = prompt.IndexOf("<|im_start|>", idx, StringComparison.Ordinal);
            if (s < 0) break;
            s += 12; // "<|im_start|>".Length
            int nl = prompt.IndexOf('\n', s);
            if (nl < 0) break;
            string role = prompt.Substring(s, nl - s).Trim();
            int contentStart = nl + 1;
            int e = prompt.IndexOf("<|im_end|>", contentStart, StringComparison.Ordinal);
            string content;
            if (e < 0) { content = prompt.Substring(contentStart); idx = prompt.Length; }
            else { content = prompt.Substring(contentStart, e - contentStart); idx = e + 10; } // "<|im_end|>".Length
            // 末尾の空 assistant プライマ(<|im_start|>assistant\n で終端)は送らない
            if (e < 0 && content.Trim().Length == 0) break;
            var md = new OrderedDictionary();
            md["role"] = role == "model" ? "assistant" : role;
            md["content"] = content;
            messages.Add(md);
        }
        if (messages.Count == 0)
        {
            var m = new OrderedDictionary();
            m["role"] = "user";
            m["content"] = prompt;
            messages.Add(m);
        }
        return messages;
    }

    // /chat/completions : OpenAIプロトコル同士の素通し中継。ゲームがOpenAI互換設定で
    // この中継サーバを指している場合の経路で、置換ルールの適用とモデル名の補完だけ行い、
    // リクエストもレスポンスも形式は変えない(レスポンスは受けたバイトをそのまま流す)。
    // APIキーは設定(openai_api_key)があればそれを、無ければゲームが送ってきた
    // Authorization ヘッダをそのまま引き継ぐ。
    // messages[].content の各文字列に DEDUP と COMPACT を適用する(json_schema/grammar注入は
    // 対象外)。配列形式のcontent([{type:"text",...}]等)は稀なケースとして対象外にする。
    static bool CompactMessagesInPlace(List<object> messages, string reqLine)
    {
        if (messages == null) return false;
        bool changed = false;
        foreach (object o in messages)
        {
            var md = o as OrderedDictionary;
            if (md == null || !md.Contains("content")) continue;
            string content = md["content"] as string;
            if (content == null) continue;

            if (DedupEnabled())
            {
                string collapsed = CollapseRepeatedBlocks(content);
                if (collapsed.Length != content.Length)
                {
                    LogIf(LogDedupKey, "[DEDUP] " + reqLine + " | 重複ブロックを畳んだ " +
                        content.Length + "→" + collapsed.Length + "文字");
                    content = collapsed;
                    changed = true;
                }
            }
            if (EventLogTrimEnabled())
            {
                string trimmed = TrimEventLog(content, reqLine);
                if (trimmed.Length != content.Length)
                {
                    content = trimmed;
                    changed = true;
                }
            }
            if (SchemaCompactEnabled())
            {
                string compacted = CompactEmbeddedSchema(content, reqLine);
                if (compacted != null)
                {
                    content = compacted;
                    changed = true;
                }
            }
            md["content"] = content;
        }
        return changed;
    }

    static bool ServeChatPassthrough(HttpReq req, NetworkStream cs)
    {
        byte[] replaced = ApplyRules(req.Body, req.RawReqLine);

        // モデル名の補完に加え、messages[].content に対しても DEDUP(重複ブロック畳み込み)と
        // COMPACT(埋め込みスキーマ説明の圧縮)を適用する。ただしゲーム自身のOpenAIモードは
        // スキーマを本文に埋め込まず response_format(json_schema) で送るため、この経路では
        // 圧縮対象が無いのが正常 (COMPACTは本文にスキーマ説明を埋め込むリクエストが来た場合の
        // 保険)。どちらだったかをログから追えるよう、response_format の種別も1行に併記する。
        // json_schema/grammar注入(JSONFIX)はOpenAIプロトコルに無い概念のため行わない
        // (構造そのものを変える変換はせず、忠実中継の方針は維持する)。
        byte[] payload = replaced;
        string model = "?";
        string rfInfo = "?";
        int msgCount = -1;
        try
        {
            string text = new UTF8Encoding(false, true).GetString(replaced);
            int pos = 0;
            var root = ParseLiteral(text, ref pos) as OrderedDictionary;
            if (root != null)
            {
                bool changed = false;

                var msgs = root.Contains("messages") ? root["messages"] as List<object> : null;
                msgCount = msgs != null ? msgs.Count : -1;
                if (CompactMessagesInPlace(msgs, req.RawReqLine)) changed = true;

                if (root.Contains("response_format"))
                {
                    var rfd = root["response_format"] as OrderedDictionary;
                    string rft = (rfd != null && rfd.Contains("type")) ? rfd["type"] as string : null;
                    rfInfo = rft != null ? rft + "(ゲーム側)" : "あり(ゲーム側)";
                }
                else rfInfo = "なし";

                string m = root.Contains("model") ? root["model"] as string : null;
                if (string.IsNullOrEmpty(m) && !string.IsNullOrEmpty(_openai.Model))
                {
                    root["model"] = _openai.Model;
                    m = _openai.Model;
                    changed = true;
                }
                model = m ?? "(なし)";

                if (changed || !ReferenceEquals(replaced, req.Body))
                    payload = Encoding.UTF8.GetBytes(JsonSerialize(root));
            }
        }
        catch { payload = replaced; }

        LogIf(LogOpenAiKey, "[OPENAI] passthrough " + req.RawReqLine + " -> " + model +
            " (" + payload.Length + "バイト, msgs=" + msgCount + ", response_format=" + rfInfo + ")");

        HttpWebResponse resp;
        try { resp = SendUpstream(payload, req.Auth); }
        catch (WebException wex)
        {
            // 上流がHTTPエラーを返した場合は、そのステータスとボディをそのまま
            // ゲームへ返す(ゲームはOpenAI形式のエラーを解釈できる前提のため)。
            // ボディは一度だけ読み、ログには先頭500文字、ゲームへは全文を転送する。
            var er = wex.Response as HttpWebResponse;
            if (er != null)
            {
                byte[] body;
                using (var rs = er.GetResponseStream()) body = ReadAllBytes(rs);
                string bodyText = body.Length > 0
                    ? Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 500))
                    : "";
                Log("[OPENAI] passthrough 上流エラー HTTP " + (int)er.StatusCode + " " + bodyText);
                RelayHttpResponseBytes(cs, er, body, req.KeepAlive);
                return req.KeepAlive;
            }
            Log("[OPENAI] passthrough 接続失敗: " + wex.Message);
            return WriteBadGateway(cs, req, wex.Message);
        }
        catch (Exception ex)
        {
            Log("[OPENAI] passthrough 例外: " + ex.Message);
            return WriteBadGateway(cs, req, ex.Message);
        }
        RelayHttpResponse(cs, resp, req.KeepAlive);
        return req.KeepAlive;
    }

    // 上流のHTTPレスポンス(正常/エラーどちらも)を、ステータス・Content-Typeを保って
    // そのままゲームへ流す。SSEにも対応するため常にチャンク転送で受けた分から書き出す。
    static void RelayHttpResponse(NetworkStream cs, HttpWebResponse resp, bool keepAlive)
    {
        try
        {
            string ct = resp.ContentType;
            if (string.IsNullOrEmpty(ct)) ct = "application/json";
            WriteResponseHead(cs, (int)resp.StatusCode + " " + resp.StatusDescription, new[]
            {
                "Content-Type: " + ct,
                "Cache-Control: no-cache",
                "Connection: " + (keepAlive ? "keep-alive" : "close"),
                "Transfer-Encoding: chunked"
            });
            using (var rs = resp.GetResponseStream())
            {
                var buf = new byte[65536];
                int n;
                while ((n = rs.Read(buf, 0, buf.Length)) > 0) WriteChunk(cs, buf, n);
            }
            WriteChunkEnd(cs);
        }
        finally { try { resp.Close(); } catch { } }
    }

    // 既にバイト列として読み込んだ上流レスポンス(エラーログ用に先読みした場合)を、
    // ステータス・Content-Typeを保ってそのままゲームへ流す
    static void RelayHttpResponseBytes(NetworkStream cs, HttpWebResponse resp, byte[] body, bool keepAlive)
    {
        try
        {
            string ct = resp.ContentType;
            if (string.IsNullOrEmpty(ct)) ct = "application/json";
            WriteResponseHead(cs, (int)resp.StatusCode + " " + resp.StatusDescription, new[]
            {
                "Content-Type: " + ct,
                "Cache-Control: no-cache",
                "Connection: " + (keepAlive ? "keep-alive" : "close"),
                "Transfer-Encoding: chunked"
            });
            if (body.Length > 0) WriteChunk(cs, body, body.Length);
            WriteChunkEnd(cs);
        }
        finally { try { resp.Close(); } catch { } }
    }

    static byte[] ReadAllBytes(Stream s)
    {
        using (var ms = new MemoryStream())
        {
            var buf = new byte[65536];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
            return ms.ToArray();
        }
    }

    // 上流に到達できなかったときのOpenAI形式エラー(502)
    static bool WriteBadGateway(NetworkStream cs, HttpReq req, string message)
    {
        var err = new OrderedDictionary();
        var e2 = new OrderedDictionary();
        e2["message"] = "プロキシ: 接続先に到達できません: " + message;
        e2["type"] = "proxy_error";
        err["error"] = e2;
        return WriteJsonResponseStatus(cs, "502 Bad Gateway", JsonSerialize(err), req.KeepAlive);
    }

    // /completion : プロンプトを messages に復元し、OpenAI互換 Chat Completions へ翻訳する。
    static bool ServeCompletion(HttpReq req, NetworkStream cs)
    {
        OrderedDictionary root;
        try
        {
            byte[] replaced = ApplyRules(req.Body, req.RawReqLine);
            string text = new UTF8Encoding(false, true).GetString(replaced);
            int pos = 0;
            root = ParseLiteral(text, ref pos) as OrderedDictionary;
        }
        catch { root = null; }
        if (root == null) return WriteUpstreamError(cs, req, "リクエストJSONを解析できませんでした");

        List<object> messages;
        object schema = null;
        bool wantsJson = false;
        string prompt = root.Contains("prompt") ? root["prompt"] as string : null;
        if (prompt != null)
        {
            if (DedupEnabled())
            {
                string collapsed = CollapseRepeatedBlocks(prompt);
                if (collapsed.Length != prompt.Length)
                {
                    LogIf(LogDedupKey, "[DEDUP] " + req.RawReqLine + " | 重複ブロックを畳んだ " +
                        prompt.Length + "→" + collapsed.Length + "文字");
                    prompt = collapsed;
                }
            }
            if (EventLogTrimEnabled())
            {
                string trimmed = TrimEventLog(prompt, req.RawReqLine);
                if (trimmed.Length != prompt.Length) prompt = trimmed;
            }
            int ss = FindSchemaStart(prompt);
            if (ss >= 0)
            {
                wantsJson = true;
                try { int pp = ss; schema = ParseLiteral(prompt, ref pp); } catch { schema = null; }
                // ローカルモードと同様に、プロンプト内の冗長なスキーマ説明をコンパクト表記へ
                // 置き換える(schema_compact設定に従う)。抽出済みの schema 本体は response_format
                // として別途送るので、テキスト側はフィールド名・enum等の意味づけだけ残せばよい。
                // (ゲーム自身のOpenAIモードがスキーマを本文に埋め込まないのと同じ形に近づく)
                if (SchemaCompactEnabled())
                {
                    string compacted = CompactEmbeddedSchema(prompt, req.RawReqLine);
                    if (compacted != null) prompt = compacted;
                }
            }
            messages = ParseTemplatedPrompt(prompt);
        }
        else if (root.Contains("messages"))
        {
            messages = root["messages"] as List<object>;
            if (messages == null) messages = new List<object>();
        }
        else messages = new List<object>();

        if (root.Contains("json_schema")) { wantsJson = true; if (schema == null) schema = root["json_schema"]; }
        if (root.Contains("grammar")) wantsJson = true;

        bool stream = !root.Contains("stream") || ToBool(root["stream"], true);
        int nPredict = root.Contains("n_predict") ? (int)ToLong(root["n_predict"]) : -1;

        // モデル名はゲームがリクエストに載せてきたらそれを優先し、無ければ設定の値を使う
        // (ゲームのOpenAI互換設定でモデルを指定するケースに追従する)。
        string reqModel = root.Contains("model") ? root["model"] as string : null;
        string model = !string.IsNullOrEmpty(reqModel) ? reqModel : _openai.Model;

        var reqObj = new OrderedDictionary();
        reqObj["model"] = model;
        reqObj["messages"] = messages;

        int maxTokens = _openai.MaxTokens > 0 ? _openai.MaxTokens : (nPredict > 0 ? nPredict : -1);
        if (maxTokens > 0) reqObj["max_tokens"] = (long)maxTokens;

        if (!double.IsNaN(_openai.Temperature)) reqObj["temperature"] = _openai.Temperature;
        else if (root.Contains("temperature")) reqObj["temperature"] = ToDouble(root["temperature"]);
        if (root.Contains("top_p")) reqObj["top_p"] = ToDouble(root["top_p"]);

        // JSON安定化: スキーマ指示があるリクエストにだけ response_format を付ける。
        // 既定は json_object(必ず妥当なJSONを返す=最も広く互換)。厳密適用は openai_json_mode=schema。
        string mode = _openai.JsonMode;
        if (wantsJson && mode != "off")
        {
            var rf = new OrderedDictionary();
            if (mode == "schema" && schema is OrderedDictionary)
            {
                rf["type"] = "json_schema";
                var js = new OrderedDictionary();
                js["name"] = "response";
                js["strict"] = false;
                js["schema"] = schema;
                rf["json_schema"] = js;
            }
            else rf["type"] = "json_object";
            reqObj["response_format"] = rf;
        }

        if (stream)
        {
            reqObj["stream"] = true;
            var so = new OrderedDictionary();
            so["include_usage"] = true;
            reqObj["stream_options"] = so;
        }

        string reqJson = JsonSerialize(reqObj);
        LogIf(LogOpenAiKey, "[OPENAI] " + req.RawReqLine + " -> " + model +
            " msgs=" + messages.Count + " json=" + (wantsJson ? mode : "off") +
            " stream=" + stream + " max_tokens=" + (maxTokens > 0 ? maxTokens.ToString() : "-"));

        return stream ? StreamOpenAiCompletion(cs, req, reqJson)
                      : BufferOpenAiCompletion(cs, req, reqJson);
    }

    // 上流(OpenAI互換)へ POST /chat/completions する。呼び出し側がレスポンスを読む。
    // APIキーは設定値を優先し、無ければゲームが送ってきた Authorization (auth) を引き継ぐ。
    static HttpWebResponse SendUpstream(byte[] payload, string auth)
    {
        string url = ChatCompletionsUrl(_openai.Endpoint);
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "POST";
        request.ContentType = "application/json";
        request.Accept = "text/event-stream";
        if (!string.IsNullOrEmpty(_openai.ApiKey))
            request.Headers["Authorization"] = "Bearer " + _openai.ApiKey;
        else if (!string.IsNullOrEmpty(auth))
            request.Headers["Authorization"] = auth;
        request.KeepAlive = true;
        request.Timeout = 600000;
        request.ReadWriteTimeout = 600000;
        request.AllowWriteStreamBuffering = false;
        request.ContentLength = payload.Length;
        using (var os = request.GetRequestStream()) os.Write(payload, 0, payload.Length);
        return (HttpWebResponse)request.GetResponse();
    }

    // ストリーミング: 上流のOpenAI SSE を llama.cpp の /completion SSE 形式へ再構築して流す。
    static bool StreamOpenAiCompletion(NetworkStream cs, HttpReq req, string reqJson)
    {
        WriteResponseHead(cs, "200 OK", new[]
        {
            "Content-Type: text/event-stream",
            "Cache-Control: no-cache",
            "Connection: " + (req.KeepAlive ? "keep-alive" : "close"),
            "Transfer-Encoding: chunked"
        });

        long promptTokens = -1, completionTokens = -1;
        string finish = null;
        try
        {
            HttpWebResponse resp = SendUpstream(Encoding.UTF8.GetBytes(reqJson), req.Auth);
            using (var rs = resp.GetResponseStream())
            using (var sr = new StreamReader(rs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    string data = line.Substring(5).Trim();
                    if (data == "[DONE]") break;
                    OrderedDictionary obj;
                    try { int pos = 0; obj = ParseLiteral(data, ref pos) as OrderedDictionary; }
                    catch { continue; }
                    if (obj == null) continue;

                    if (obj.Contains("usage"))
                    {
                        var u = obj["usage"] as OrderedDictionary;
                        if (u != null)
                        {
                            if (u.Contains("prompt_tokens")) promptTokens = ToLong(u["prompt_tokens"]);
                            if (u.Contains("completion_tokens")) completionTokens = ToLong(u["completion_tokens"]);
                        }
                    }

                    var choices = obj.Contains("choices") ? obj["choices"] as List<object> : null;
                    if (choices == null || choices.Count == 0) continue;
                    var ch0 = choices[0] as OrderedDictionary;
                    if (ch0 == null) continue;
                    if (ch0.Contains("finish_reason") && ch0["finish_reason"] is string)
                        finish = (string)ch0["finish_reason"];
                    var delta = ch0.Contains("delta") ? ch0["delta"] as OrderedDictionary : null;
                    string piece = (delta != null && delta.Contains("content")) ? delta["content"] as string : null;
                    if (!string.IsNullOrEmpty(piece))
                        WriteChunk(cs, "data: " + LlamaStreamEvent(piece) + "\n\n");
                }
            }
        }
        catch (WebException wex)
        {
            string msg = ReadWebError(wex);
            Log("[OPENAI] upstream エラー: " + msg);
            WriteChunk(cs, "data: " + LlamaStreamEvent("【プロキシ:上流エラー】" + msg) + "\n\n");
            finish = "stop";
        }
        catch (Exception ex)
        {
            Log("[OPENAI] ストリーム例外: " + ex.Message);
            finish = "stop";
        }

        string stopType = finish == "length" ? "limit" : "eos";
        WriteChunk(cs, "data: " + LlamaFinalEvent("", promptTokens, completionTokens, stopType) + "\n\n");
        WriteChunkEnd(cs);
        return req.KeepAlive;
    }

    // 非ストリーミング: 上流の完了レスポンスをllama.cppの単発 /completion 応答へ変換して返す。
    static bool BufferOpenAiCompletion(NetworkStream cs, HttpReq req, string reqJson)
    {
        string content = "";
        long promptTokens = -1, completionTokens = -1;
        string finish = "stop";
        try
        {
            HttpWebResponse resp = SendUpstream(Encoding.UTF8.GetBytes(reqJson), req.Auth);
            string full;
            using (var rs = resp.GetResponseStream())
            using (var sr = new StreamReader(rs, Encoding.UTF8))
                full = sr.ReadToEnd();
            int pos = 0;
            var obj = ParseLiteral(full, ref pos) as OrderedDictionary;
            if (obj != null)
            {
                if (obj.Contains("usage"))
                {
                    var u = obj["usage"] as OrderedDictionary;
                    if (u != null)
                    {
                        if (u.Contains("prompt_tokens")) promptTokens = ToLong(u["prompt_tokens"]);
                        if (u.Contains("completion_tokens")) completionTokens = ToLong(u["completion_tokens"]);
                    }
                }
                var choices = obj.Contains("choices") ? obj["choices"] as List<object> : null;
                if (choices != null && choices.Count > 0)
                {
                    var ch0 = choices[0] as OrderedDictionary;
                    if (ch0 != null)
                    {
                        if (ch0.Contains("finish_reason") && ch0["finish_reason"] is string)
                            finish = (string)ch0["finish_reason"];
                        var msg = ch0.Contains("message") ? ch0["message"] as OrderedDictionary : null;
                        if (msg != null && msg.Contains("content")) content = StringifyContent(msg["content"]);
                    }
                }
            }
        }
        catch (WebException wex)
        {
            string m = ReadWebError(wex);
            Log("[OPENAI] upstream エラー: " + m);
            content = "【プロキシ:上流エラー】" + m;
        }
        catch (Exception ex)
        {
            Log("[OPENAI] 応答処理の例外: " + ex.Message);
            content = "【プロキシ:例外】" + ex.Message;
        }

        string stopType = finish == "length" ? "limit" : "eos";
        return WriteJsonResponse(cs, LlamaFinalEvent(content, promptTokens, completionTokens, stopType), req.KeepAlive);
    }

    // llama.cpp の /completion ストリーミング中間イベント(content増分, stop:false)
    static string LlamaStreamEvent(string content)
    {
        var d = new OrderedDictionary();
        d["index"] = 0L;
        d["content"] = content;
        d["tokens"] = new List<object>();
        d["stop"] = false;
        return JsonSerialize(d);
    }

    // llama.cpp の /completion 最終イベント(stop:true)。非ストリーミング応答としても使う。
    static string LlamaFinalEvent(string content, long promptTokens, long completionTokens, string stopType)
    {
        var d = new OrderedDictionary();
        d["index"] = 0L;
        d["content"] = content != null ? content : "";
        d["tokens"] = new List<object>();
        d["stop"] = true;
        d["model"] = _openai.Model;
        d["tokens_predicted"] = completionTokens;
        d["tokens_evaluated"] = promptTokens;
        d["has_new_line"] = false;
        d["truncated"] = false;
        d["stop_type"] = stopType;
        d["stopping_word"] = "";
        return JsonSerialize(d);
    }

    static string ReadWebError(WebException wex)
    {
        try
        {
            var resp = wex.Response as HttpWebResponse;
            if (resp != null)
            {
                using (var rs = resp.GetResponseStream())
                using (var sr = new StreamReader(rs, Encoding.UTF8))
                {
                    string body = sr.ReadToEnd();
                    if (body.Length > 500) body = body.Substring(0, 500);
                    return "HTTP " + (int)resp.StatusCode + " " + body;
                }
            }
        }
        catch { }
        return wex.Message;
    }

    // ---- HTTPレスポンス書き出し(OpenAI互換モード) ----

    static readonly byte[] Crlf = { 13, 10 };

    static void WriteResponseHead(NetworkStream cs, string status, string[] headers)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        foreach (string h in headers) sb.Append(h).Append("\r\n");
        sb.Append("\r\n");
        byte[] b = Encoding.ASCII.GetBytes(sb.ToString());
        cs.Write(b, 0, b.Length);
        cs.Flush();
    }

    static void WriteChunk(NetworkStream cs, string s)
    {
        byte[] data = Encoding.UTF8.GetBytes(s);
        WriteChunk(cs, data, data.Length);
    }

    static void WriteChunk(NetworkStream cs, byte[] data, int n)
    {
        byte[] head = Encoding.ASCII.GetBytes(n.ToString("x", CultureInfo.InvariantCulture) + "\r\n");
        cs.Write(head, 0, head.Length);
        cs.Write(data, 0, n);
        cs.Write(Crlf, 0, 2);
        cs.Flush();
    }

    static void WriteChunkEnd(NetworkStream cs)
    {
        byte[] end = Encoding.ASCII.GetBytes("0\r\n\r\n");
        cs.Write(end, 0, end.Length);
        cs.Flush();
    }

    static bool WriteJsonResponse(NetworkStream cs, string json, bool keepAlive)
    {
        return WriteJsonResponseStatus(cs, "200 OK", json, keepAlive);
    }

    static bool WriteJsonResponseStatus(NetworkStream cs, string status, string json, bool keepAlive)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        sb.Append("Content-Type: application/json\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n\r\n");
        byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
        cs.Write(head, 0, head.Length);
        cs.Write(body, 0, body.Length);
        cs.Flush();
        return keepAlive;
    }

    // 解析不能などで上流を呼べないとき、ゲームがJSONを期待するので最終イベント相当で返す。
    static bool WriteUpstreamError(NetworkStream cs, HttpReq req, string message)
    {
        Log("[OPENAI] " + message);
        return WriteJsonResponse(cs, LlamaFinalEvent("【プロキシ】" + message, -1, -1, "eos"), req.KeepAlive);
    }

    static bool ToBool(object o, bool dflt)
    {
        if (o is bool) return (bool)o;
        return dflt;
    }

    static double ToDouble(object o)
    {
        if (o is double) return (double)o;
        if (o is long) return (double)(long)o;
        if (o is int) return (double)(int)o;
        return 0.0;
    }
}
