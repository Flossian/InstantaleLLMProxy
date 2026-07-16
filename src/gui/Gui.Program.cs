// ============================================================================
// src\gui\*.cs — InstantaleLlmProxy 管理GUI
// (機能ごとに Gui.*.cs へ分割。すべて partial class MainForm で1つのクラス)
//
// 配置: MODフォルダ直下に InstantaleLlmProxy.exe、ソース類は src\ 配下。
//       ユーザが操作するのはこのGUI (InstantaleLlmProxy.exe) だけでよい。
//
// 機能:
//   ・置換ルール (llm_replacements.txt) のグリッド編集と置換テスト
//   ・MODの適用(ラッパーのビルド+差し替え)と解除(本物の復元)
//   ・ラッパー/本物プロセスの状態表示と強制終了
//   ・llm_proxy.log の閲覧
//
// 注意: ラッパー(llama-server.exe)はゲームが起動時に自動で立ち上げ、
//       ゲーム終了時に終了させる。GUIから行うのは有効化/無効化と強制終了。
//
// ビルド: src\llm_proxy_gui.bat 参照
// ============================================================================
// ----------------------------------------------------------------------------
// Gui.Program.cs (LlmProxy GUI: エントリポイント)
// ----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
