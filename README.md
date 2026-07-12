# InstantaleLlmProxy — LLMリクエスト置換プロキシMOD

## 概要

ゲーム(Instantale)とローカルLLM(llama-server)の間に入り、**ゲーム→LLM のリクエストに含まれる文字列を置換する**プロキシです。

例:

> 「あなたはダークファンタジーRPGのキャラクター生成AIだ。」
> → 「あなたは明るく平和なRPGのキャラクター生成AIだ。」

MODの全ファイルはこの `InstantaleLlmProxy` フォルダに収まっており、ゲームフォルダ直下には他に何も置きません。

## 導入手順

1. `InstantaleLlmProxy` フォルダごと、ゲームフォルダ (`instantale.exe` がある場所) の直下に置く
2. `InstantaleLlmProxy.exe` を起動して「MOD適用」を押す
3. ゲームを起動する

**ユーザが操作するのは `InstantaleLlmProxy.exe` だけです。** ルール編集・適用/解除・ログ閲覧はすべてGUIからできます。

> - 適用時のビルドはWindows標準搭載のC#コンパイラで行うため追加インストール不要
> - 上書き展開すると `llm_replacements.txt` (置換ルール) が初期化されるので、編集済みの場合はバックアップしてから展開すること

## 仕組み

ゲームは `bin\llama-b7054-bin-win-*\llama-server.exe` を毎回空きポートで起動し、HTTP (`/apply-template`, `/completion`) で通信しています。
このMODは `llama-server.exe` を「置換プロキシ内蔵ラッパー」に差し替えます。

```
ゲーム → [llama-server.exe (ラッパー/プロキシ)] → [llama-server-real.exe (本物)]
```

- ラッパーは本物を別の空きポートで起動し、ゲーム指定のポートで待ち受ける
- リクエストボディ(JSON)内の文字列をルールに従って置換して中継
- レスポンス(ストリーミング含む)は無加工で素通し
- 日本語が `\uXXXX` エスケープされて送られる場合にも自動対応
- `llm_replacements.txt` の変更はリクエストのたびに自動検知して再読込
  (ゲームを起動したまま保存すれば次のLLMリクエストから反映される)
- ラッパーが終了/killされると本物も道連れで終了する (プロセス残留なし)
- 適用時に `bin\llama-*\` へ `llm_proxy_dir.txt` (このMODフォルダの場所) を書き、ラッパーはそれを見てルールファイルの場所を確実に特定する。そのためMODフォルダの名前は自由に変えてよい (解除時に自動削除される)

## 使い方 (GUI)

`InstantaleLlmProxy.exe` をダブルクリックすると管理GUIが起動する。

- 対象フォルダの切替 (「参照...」でゲームフォルダを選択、次回起動時も記憶)
  → 別のゲームフォルダ(本体とコピー等)もこのGUIひとつで管理できる
- 置換ルールの編集/保存 (表形式、「有効」チェックでルールごとにON/OFF) と置換テスト
- MODの適用/解除、llama-serverプロセスの状態表示と強制終了
- `llm_proxy.log` の閲覧 (自動更新)

> - ラッパー自体はゲームが起動時に自動で立ち上げ、終了時に終わらせるのでGUIから起動する必要はない。
> - 対象が Program Files 配下の場合、適用/解除やルール保存には管理者権限が必要なことがある (その場合はGUIを管理者として実行)。ラッパーがログを書けない場合は `%LOCALAPPDATA%\llm_proxy.log` に出力される (GUIは自動表示)。

## 置換ルールの手動編集

GUIを使わずに `llm_replacements.txt` を直接編集してもよい。

```
置換前=>置換後
```

- 1行1ルール、UTF-8、行頭 `#` はコメント
- 行頭に `#off:` を付けるとそのルールだけ無効化 (GUIの「有効」チェックと連動)
- ゲーム起動中に編集・保存してもOK。次のLLMリクエストから自動反映される
- 置換された内容は `llm_proxy.log` に記録される

## ファイル構成

```
InstantaleLlmProxy\
├─ InstantaleLlmProxy.exe   … 管理GUI (ユーザが操作するのはこれだけ)
├─ llm_replacements.txt     … 置換ルール (保存すると即時反映・再起動不要)
├─ llm_proxy.log            … 動作ログ ([REPLACE] 行が置換の記録・実行時に生成)
├─ README.md                … このファイル
└─ src\                     … ソースと開発者向けスクリプト (通常は触らなくてよい)
   ├─ llm_proxy.cs          … ラッパーのソース (適用時にGUIがビルドする)
   ├─ llm_proxy_gui.cs      … 管理GUIのソース
   ├─ llm_proxy_apply.bat   … MOD適用 (コマンドライン派・GUIと同等)
   ├─ llm_proxy_revert.bat  … MOD解除 (コマンドライン派・GUIと同等)
   └─ llm_proxy_gui.bat     … GUIをソースから再ビルドして起動 (開発用)
```

## 注意

- ゲームのアップデートで `bin\` 内の `llama-server.exe` が上書きされたら「MOD適用」を再実行してください
- 置換はLLMへの「入力」のみ。LLMからの応答文は変更しません
- クラウドLLM(OpenAI API等)使用時は経由しないので効きません
