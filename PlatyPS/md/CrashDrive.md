---
document type: module
Help Version: 1.0.0.0
HelpInfoUri:
Locale: ja-JP
Module Guid: b6f1e4d2-5a9c-4e83-91f4-7a3b6e2d8c4f
Module Name: CrashDrive
ms.date: 04/17/2026
PlatyPS schema version: 2024-05-01
title: CrashDrive Module
---

# CrashDrive Module

## Description

Windows のポストモーテム成果物 (クラッシュダンプ / Time-Travel Debugging レコーディング / 実行トレース) を PSDrive としてマウントし、`ls`・`cd`・`cat` といった人間が既に知っているファイルシステムのイディオムで調査できるようにする PowerShell モジュールです。Trace / Dump / Ttd の 3 プロバイダをファイル拡張子から自動選択し、スレッド・フレーム・ヒープ・TTD イベントなどを仮想ツリーとして公開します。

splash (ConPTY ベースのシェル MCP サーバー) と組み合わせれば、AI エージェントも同じファイルシステム的な問いかけでポストモーテム状態を推論できるようになります — デバッガ固有の語彙を AI に覚えさせる必要はありません。

関連: splash - https://github.com/yotsuda/splash (npm: @ytsuda/splash)

## CrashDrive Cmdlets

### [Disable-CrashEditorFollow](Disable-CrashEditorFollow.md)

`Enable-CrashEditorFollow` で設定したカレントロケーション変更フックを解除し、`cd` 時のエディタ自動ジャンプを無効化します。

### [Enable-CrashEditorFollow](Enable-CrashEditorFollow.md)

カレントロケーションが CrashDrive のパスに移動したとき、対応するソースファイル:行をエディタで自動的に開くモードを有効化します。

### [Get-CrashLocalVariable](Get-CrashLocalVariable.md)

Dump / Ttd ドライブの特定スタックフレームにおけるローカル変数を dbgeng 経由で取得します。

### [Get-CrashObject](Get-CrashObject.md)

Dump ドライブの GC ヒープ上にあるマネージド (.NET) オブジェクトをアドレスから取得し、型名・サイズ・フィールド一覧を構造化して返します。

### [Get-TtdBookmark](Get-TtdBookmark.md)

TTD ドライブに登録されているブックマークを列挙、もしくは名前で 1 件取得します。

### [Invoke-CrashCommand](Invoke-CrashCommand.md)

Dump / Ttd ドライブの共有 dbgeng セッションに対して任意の dbgeng コマンドを実行し、出力テキストを返します。

### [New-CrashDrive](New-CrashDrive.md)

ポストモーテム成果物 (dump / TTD / トレース) を PSDrive としてマウント、またはプログラムをトレーサー配下で実行して取得したトレースをマウントします。

### [New-TtdBookmark](New-TtdBookmark.md)

TTD ドライブ上の特定位置に覚えやすい名前を付け、`ttd:\bookmarks\<name>\` としてアクセス可能にします。

### [Read-CrashMemory](Read-CrashMemory.md)

Dump / Ttd ドライブのメモリアドレスを読み取り、Hex / ASCII / Unicode / DWORD / QWORD / ポインタチェーン形式で返します。

### [Remove-TtdBookmark](Remove-TtdBookmark.md)

TTD ドライブに登録されたブックマークを名前で削除します。存在しない名前を指定しても無害 (silent no-op) です。
