// ----------------------------------------------------------------------------
// Proxy.Forward.cs (LlmProxy partial: ネイティブ llama.cpp モード: 生TCP中継 (ルール適用・診断用SSE覗き見))
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
            // [RESP]行とレスポンスダンプは別項目なので、どちらかがONなら覗き見する
            RespStats stats = (DiagEnabled(LogDiagKey) || DiagEnabled(DumpRespKey))
                ? new RespStats(_ctxSize) : null;
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
            if (DiagEnabled(LogDiagKey))
                Log("[DIAG] [RESP] tokens_evaluated=" + te + " tokens_predicted=" + tp +
                    " truncated=" + trunc + " limit=" + limit + " ctx=" + _ctx);
            Dump(ev);
        }

        // 生の最終イベント(Latin-1でデコード。ASCIIのフィールド名/数値は読める。日本語本文は化ける)を追記
        void Dump(string ev)
        {
            try
            {
                if (!DiagEnabled(DumpRespKey)) return;
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

}
