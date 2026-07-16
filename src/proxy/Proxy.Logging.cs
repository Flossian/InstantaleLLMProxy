// ----------------------------------------------------------------------------
// Proxy.Logging.cs (LlmProxy partial: ログ出力・診断ダンプ)
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

    // 複数スレッド (接続ごと) から呼ばれるので追記は直列化する。
    // ログ出力の失敗でゲームを止めたくないので、例外はすべて握り潰す
    // 項目別の詳細ログ。その項目がONのときだけ出力する。
    static void LogIf(string key, string msg)
    {
        if (LogEnabled(key)) Log(msg);
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

    // ============================================================================
    // 任意OpenAI互換サーバへの中継 (OpenAI互換モード)
    //
    // 任意のOpenAI互換サーバ(OpenAI本家に限らず、LM Studio / Ollama / vLLM / 各種
    // ゲートウェイなど /v1/chat/completions を話すもの)へ中継する。使い方は2つあり、
    // どちらも接続先の設定は llm_proxy_settings.ini の openai_* キー(GUIのメニュー
    // 「OpenAI互換 > 接続設定」で編集)。
    //
    // (1) ラッパー翻訳: MOD適用済みで、ゲームがローカルLLM(llama-server起動)の場合。
    //     openai_wrapper=1 で明示的に有効化されたとき(かつ openai_endpoint と
    //     openai_model があるとき)、本物を起動せずにゲームの llama.cpp ネイティブAPIを
    //     OpenAI互換APIへ翻訳して中継する。接続設定が保存されているだけでは作動しない
    //     (設定を保持したままローカルLLMへ戻せるようにするため)。
    //       ・/apply-template : messages を自前で1本のプロンプト文字列へ畳んで返す
    //       ・/completion     : プロンプトを messages に復元し /chat/completions を
    //                           ストリーミング呼び出し。返ってくるSSEを llama.cpp の
    //                           /completion SSE 形式へ再構築してゲームへ流す。
    //       ・/health,/v1/models,/props 等 : 起動時プローブ用に最小限の応答を返す
    //     JSON安定化は、リクエストにスキーマ指示があれば response_format を付ける
    //     (既定は json_object。厳密なスキーマ適用は openai_json_mode=schema)。
    //
    // (2) スタンドアロン中継 (--openai-relay): ゲーム側のOpenAI互換設定(エンドポイント/
    //     APIキー/モデル名)に、この中継サーバ http://127.0.0.1:<openai_listen_port>/v1 を
    //     指定する場合。ゲームは llama-server を起動しないため、GUIのメニューから
    //     この中継サーバを起動しておく。ゲームはOpenAIプロトコルで話すので翻訳は不要で、
    //     /chat/completions は置換ルールだけ適用して素通しする(レスポンスも無加工)。
    //     モデル名とAPIキーはゲーム側の指定を優先し、無ければ設定値で補う。
    //
    // 置換ルール(llm_replacements.txt)はどちらの経路でも適用する。DEDUP/COMPACT もどちらでも
    // 適用するが、(2)はゲームが response_format.json_schema を自分で送り本文にスキーマを
    // 埋め込まないため、実際には圧縮対象が無いことが多い(COMPACTが出なくても正常)。
    //
    // 設定 (llm_proxy_settings.ini。セクション見出しは飾りで、実際にはキー名だけで読む):
    //   openai_wrapper=1                           ; (1)の有効化。既定0=ローカルLLM
    //   openai_endpoint=https://api.openai.com/v1  ; 接続先。空ならローカルllama-server(従来動作)
    //   openai_model=gpt-4o-mini                   ; (1)では必須。(2)ではゲーム側の指定を優先
    //   openai_api_key=sk-...                      ; 任意(ローカル互換サーバでは空でよい)
    //   openai_listen_port=8181                    ; (2)の待ち受けポート。空なら(2)無効
    //   openai_json_mode=object                    ; object(既定)|schema|off ((1)のみ)
    //   openai_temperature=                        ; 空ならリクエスト値/サーバ既定 ((1)のみ)
    //   openai_max_tokens=                         ; 空ならリクエストの n_predict ((1)のみ)
    // endpoint は /v1 まで(末尾の /chat/completions は付けても付けなくてもよい)。
    // ============================================================================
}
