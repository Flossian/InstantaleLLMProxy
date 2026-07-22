// ----------------------------------------------------------------------------
// Proxy.Settings.cs (LlmProxy partial: llm_proxy_settings.ini の読み込みと各種フラグ・OpenAI互換設定)
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
    const string JsonFixOffFileName = "llm_proxy_jsonfix_off.txt";
    const string DedupOffFileName = "llm_proxy_dedup_off.txt";
    const string SingletonOffFileName = "llm_proxy_singleton_off.txt";
    const string EventLogTrimOffFileName = "llm_proxy_eventlog_off.txt";
    const string DiagOnFileName = "llm_proxy_diag_on.txt";
    const string SettingsFileName = "llm_proxy_settings.ini";

    // MODフォルダ直下に決め打ちの名前の空ファイルを置くだけで判定できる「フラグファイル」の共通実装。
    // JsonFixEnabled/DedupEnabled/SingletonEnabled が使う。この3つは ini設定やGUIのチェックボックスを
    // 持たない切り分け専用スイッチで、ファイルを置く/消すだけで確実に切り替えられることを優先している
    static bool FlagFileExists(string fileName)
    {
        try
        {
            string baseDir = _rulesPath != null ? Path.GetDirectoryName(_rulesPath)
                                                : Path.GetDirectoryName(_logPath);
            return baseDir != null && File.Exists(Path.Combine(baseDir, fileName));
        }
        catch { return false; }
    }

    // JSON安定化(JSONFIX)はデフォルトON。無効化はMODフォルダに llm_proxy_jsonfix_off.txt を置く
    static bool JsonFixEnabled()
    {
        return !FlagFileExists(JsonFixOffFileName);
    }

    // プロンプト重複ブロックの畳み込みはデフォルトON。無効化はMODフォルダに llm_proxy_dedup_off.txt。
    static bool DedupEnabled()
    {
        return !FlagFileExists(DedupOffFileName);
    }

    // 「今回のイベント内ログ」(field_event_evaluator/quest_referee_event_resolve)の
    // 直近ターンのみ保持はデフォルトON。無効化はMODフォルダに llm_proxy_eventlog_off.txt。
    static bool EventLogTrimEnabled()
    {
        return !FlagFileExists(EventLogTrimOffFileName);
    }

    // プロンプト内スキーマ説明のコンパクト化はデフォルトON。GUIの「プロンプト圧縮」チェックボックスで
    // ON/OFFすると llm_proxy_settings.ini (INI形式) の schema_compact キーに書かれ、ここで読む。
    // json_schema(grammar制約)が既に付いているリクエストが対象なので、構造の正しさ
    // 自体は圧縮の有無に関係なく保証される(grammarがトークン単位で強制する)。圧縮で削るのは
    // フィールドの意味づけを助ける説明的な冗長部分だけ。
    static bool SchemaCompactEnabled()
    {
        return CachedSettingBool("schema_compact", true);
    }

    // llm_proxy_settings.ini から key=value 形式の設定値を読む (キー無し/ファイル無しは既定値)。
    // 大文字小文字は区別せず、値は 1/0, true/false, on/off のいずれでも受け付ける。
    // 呼び出しは CachedSettingBool 経由 (毎回読み直さず更新時刻で再読込する) を基本とする。
    // baseDir を明示的に取るのは、テストから実行時グローバル状態に触れずに直接呼べるようにするため
    // (internal もそのため)。INIのセクション見出し([Settings]等)と ; / # 両方のコメント記法を許容し、
    // セクションは区別せず全キーを1つの名前空間として読む(現状これで十分なため)。
    internal static bool ReadSettingBool(string baseDir, string key, bool dflt)
    {
        try
        {
            if (baseDir == null) return dflt;
            string path = Path.Combine(baseDir, SettingsFileName);
            if (!File.Exists(path)) return dflt;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim().TrimStart('﻿').Trim();
                if (line.Length == 0 ||
                    line.StartsWith("#", StringComparison.Ordinal) ||
                    line.StartsWith(";", StringComparison.Ordinal) ||
                    line.StartsWith("[", StringComparison.Ordinal)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    continue;
                string v = line.Substring(eq + 1).Trim();
                if (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(v, "on", StringComparison.OrdinalIgnoreCase)) return true;
                if (v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)) return false;
                return dflt;
            }
            return dflt;
        }
        catch { return dflt; }
    }

    // llm_proxy_settings.ini から文字列値を読む(キー無し/ファイル無しは既定値)。
    // ReadSettingBool と同じくセクションは区別せず、; と # のコメントを許容する。
    static string ReadSettingString(string key, string dflt)
    {
        try
        {
            string baseDir = _rulesPath != null ? Path.GetDirectoryName(_rulesPath)
                                                : Path.GetDirectoryName(_logPath);
            if (baseDir == null) return dflt;
            string path = Path.Combine(baseDir, SettingsFileName);
            if (!File.Exists(path)) return dflt;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim().TrimStart('﻿').Trim();
                if (line.Length == 0 ||
                    line.StartsWith("#", StringComparison.Ordinal) ||
                    line.StartsWith(";", StringComparison.Ordinal) ||
                    line.StartsWith("[", StringComparison.Ordinal)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    continue;
                return line.Substring(eq + 1).Trim();
            }
            return dflt;
        }
        catch { return dflt; }
    }

    // 設定ファイル由来のフラグを、更新時刻を見てキャッシュしつつ読む。
    // ログ1行ごと/リクエストごとにINIを読み直すのは無駄なので、設定ファイルが書き換わった
    // ときだけ読み直す。GUIで保存すると更新時刻が変わるので、次の呼び出しから自動反映される
    // (ゲーム再起動不要)。ルールファイルの自動再読込と同じ考え方。
    static readonly Dictionary<string, bool> SettingsFlagCache = new Dictionary<string, bool>();
    static DateTime _settingsFlagStamp = DateTime.MinValue;
    static readonly object SettingsFlagLock = new object();
    static bool CachedSettingBool(string key, bool dflt)
    {
        try
        {
            string baseDir = _rulesPath != null ? Path.GetDirectoryName(_rulesPath)
                                                : Path.GetDirectoryName(_logPath);
            if (baseDir == null) return dflt;
            string path = Path.Combine(baseDir, SettingsFileName);
            DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            lock (SettingsFlagLock)
            {
                if (stamp != _settingsFlagStamp)
                {
                    _settingsFlagStamp = stamp;
                    SettingsFlagCache.Clear(); // 設定ファイルが変わったら全キー読み直し
                }
                bool v;
                if (SettingsFlagCache.TryGetValue(key, out v)) return v;
                v = ReadSettingBool(baseDir, key, dflt);
                SettingsFlagCache[key] = v;
                return v;
            }
        }
        catch { return dflt; }
    }

    // ---- ログ項目ごとのON/OFF ----
    // GUIの「設定」→「デバッグ設定...」で項目ごとに切り替える。キーは llm_proxy_settings.ini。
    // [BOOT]/[FATAL]/[ERROR]/[EXIT] 等の重要ログはどの項目にも属さず、常に出力する(切り分けに要るため)。
    //
    // 項目別になる前の一括スイッチ (debug_log / diag_log / llm_proxy_diag_on.txt) は、
    // 項目キーが無いときの既定値として引き継ぐ。これで旧い設定ファイルのままでも従来どおりの挙動になる。

    // 軽めの詳細ログ (llm_proxy.log の1リクエスト単位の行)。既定ON
    const string LogReplaceKey = "log_replace"; // [REPLACE]/[SKIP]
    const string LogCompactKey = "log_compact"; // [COMPACT]
    const string LogDedupKey = "log_dedup";     // [DEDUP]
    const string LogEventLogKey = "log_eventlog"; // [EVENTLOG]
    const string LogJsonFixKey = "log_jsonfix"; // [JSONFIX]
    const string LogRulesKey = "log_rules";     // [RULES]
    const string LogOpenAiKey = "log_openai";   // [OPENAI] (任意OpenAI互換サーバへの中継時のみ)

    // 調査用の診断ログ/ダンプ。既定OFF (通常運用ではログが非常に大きくなるため)
    const string LogDiagKey = "log_diag";       // [DIAG] 行 (受信キー一覧/[DUP]/[RESP])
    const string DumpSchemaKey = "dump_schema"; // llm_proxy_schema_dump.log
    const string DumpPromptKey = "dump_prompt"; // llm_proxy_prompt_dump.log
    const string DumpRespKey = "dump_resp";     // llm_proxy_resp_dump.log

    static bool LogEnabled(string key)
    {
        return CachedSettingBool(key, CachedSettingBool("debug_log", true));
    }

    static bool DiagEnabled(string key)
    {
        // 旧方式のフラグファイル(llm_proxy_diag_on.txt)が置かれていれば全項目ON (READMEに載っている
        // 切り分け手段を残す)。項目別の設定より優先する = ファイルを置くだけで確実に全部出る、と
        // 説明できる形にする
        if (FlagFileExists(DiagOnFileName)) return true;
        return CachedSettingBool(key, CachedSettingBool("diag_log", false));
    }

    // 本物のシングルトン化(集約)はデフォルトON。無効化はMODフォルダに llm_proxy_singleton_off.txt。
    // 無効時は各ラッパーが専用の本物を起動する(コンテキスト分割による context-exceeded の切り分け用)。
    static bool SingletonEnabled()
    {
        return !FlagFileExists(SingletonOffFileName);
    }

    // 診断ログ用に数値を取り出す。キーが無い/数値でない場合は -1 (「不明」の意味)
    static long ToLong(object o)
    {
        if (o is long) return (long)o;
        if (o is double) return (long)(double)o;
        return -1;
    }

    static readonly object SchemaDumpLock = new object();

    static OpenAiConfig _openai;

    sealed class OpenAiConfig
    {
        public string Endpoint;              // 例 https://api.openai.com/v1
        public string Model;
        public string ApiKey;
        public string JsonMode = "object";   // object | schema | off
        public double Temperature = double.NaN;
        public int MaxTokens = -1;
    }

    // llm_proxy_settings.ini から OpenAI互換の中継設定を読む。ラッパー翻訳(requireModel=true)
    // は endpoint と model の両方が必要(llama.cpp APIのリクエストはモデル名を持たないため)。
    // スタンドアロン中継(requireModel=false)は endpoint だけでよい(モデル名はゲーム側の
    // 指定を優先できるため)。条件を満たさなければ null (=ローカル動作)。
    static OpenAiConfig LoadOpenAiConfig(bool requireModel)
    {
        try
        {
            string endpoint = ReadSettingString("openai_endpoint", "");
            string model = ReadSettingString("openai_model", "");
            if (string.IsNullOrEmpty(endpoint)) return null;
            if (requireModel && string.IsNullOrEmpty(model)) return null;

            var cfg = new OpenAiConfig();
            cfg.Endpoint = endpoint.TrimEnd('/');
            cfg.Model = model;
            cfg.ApiKey = ReadSettingString("openai_api_key", "");
            string jm = ReadSettingString("openai_json_mode", "object").ToLowerInvariant();
            if (jm.Length > 0) cfg.JsonMode = jm;
            string temp = ReadSettingString("openai_temperature", "");
            double d;
            if (temp.Length > 0 && double.TryParse(temp, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                cfg.Temperature = d;
            string mt = ReadSettingString("openai_max_tokens", "");
            int n;
            if (mt.Length > 0 && int.TryParse(mt, out n)) cfg.MaxTokens = n;
            return cfg;
        }
        catch (Exception ex) { Log("[OPENAI] 設定読込に失敗: " + ex.Message); return null; }
    }

    // endpoint から /chat/completions のURLを組み立てる。ユーザーが /v1 まで、あるいは
    // /v1/chat/completions まで、あるいはホストだけを入れても動くよう吸収する。
    static string ChatCompletionsUrl(string endpoint)
    {
        string e = endpoint.TrimEnd('/');
        if (e.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return e;
        if (e.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return e + "/chat/completions";
        return e + "/v1/chat/completions";
    }

    static bool HasFlag(string[] args, string name)
    {
        foreach (string a in args) if (a == name) return true;
        return false;
    }

    // ---- スタンドアロン中継モード (--openai-relay) ----

    const string RelayPidFileName = "llm_proxy_relay.txt";
}
