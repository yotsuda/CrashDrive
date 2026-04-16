---
title: about_CrashDrive
---

# about_CrashDrive

## Short Description
Windows のポストモーテム成果物 (クラッシュダンプ / TTD レコーディング / 実行トレース) を PSDrive としてマウントし、ファイルシステムのイディオムで調査するための PowerShell モジュールです。

## Long Description
CrashDrive は PowerShell の NavigationCmdletProvider を 3 つ (`Trace` / `Dump` / `Ttd`) 実装し、ポストモーテム成果物をドライブとしてマウントします。`dir`、`cd`、`Get-Content` などの標準コマンドでスレッド・フレーム・ヒープ・TTD イベントをブラウズできます。

AI エージェントが特殊なデバッガ語彙を覚えなくても、人間と同じファイルシステム的な問いかけでポストモーテム状態を推論できることが設計の中心です。

## ドライブのマウント

```powershell
# クラッシュダンプ (.dmp) をマウント
New-CrashDrive dmp .\crash.dmp

# TTD レコーディング (.run) をマウント (WinDbg Preview 必須)
New-CrashDrive ttd .\recording.run

# JSONL トレースをマウント
New-CrashDrive t .\cart.jsonl

# Python プログラムをトレーサー配下で起動 → トレース自動マウント
New-CrashDrive pycap -ExecutablePath .\sample_target.py

# .NET プログラムを Harmony トレーサー配下で起動
New-CrashDrive dncap -ExecutablePath .\tinyapp.exe
```

拡張子 (`.dmp` / `.run` / `.jsonl`) から自動的に適切なプロバイダが選ばれます。`-ExecutablePath` はさらに言語 (python / dotnet) を拡張子から自動推定します。

## Dump ドライブの構造

```
dump:\
├── summary.json        概要 (アーキテクチャ、CLR 版、スレッド数など)
├── analyze.txt         !analyze -v 出力 (dbgeng、キャッシュ済み)
├── threads\            スレッド一覧
│   └── <managed-id>\   マネージドスレッド ID
│       ├── info.json   ThreadId, OSThreadId, GCMode, IsAlive など
│       ├── registers.txt
│       └── frames\     スタックフレーム
│           └── <n>.json  Frame, Method, Module, Source (解決できれば)
├── modules\            ロード済みモジュール (native + managed の結合)
└── heap\               GC ヒープ
    ├── types\          型別インスタンス統計
    └── objects\        アドレス指定のオブジェクト (Get-CrashObject 経由)
```

## TTD ドライブの構造

```
ttd:\
├── triage.md           アンサー・ファースト要約 (例外、重要イベント、Lifetime)
├── summary.json        Recording メタデータ
├── timeline.json       全イベント時系列
├── timeline\           answer-first 系統別ビュー
│   ├── events\         全イベント (Position 順)
│   ├── exceptions\     Exception* イベントのみ
│   └── significant\    ModuleLoaded / Thread* のみ
├── ttd-events\         Events 生リスト
├── positions\          時刻位置を辿る
│   ├── start\          Lifetime 開始
│   ├── end\            Lifetime 終端
│   ├── first-exception\   最初の例外位置 (あれば)
│   ├── last-exception\    最後の例外位置 (2 件以上あれば)
│   ├── last-meaningful-event\   最後の ModuleLoaded/Thread* イベント
│   └── <encoded-pos>\  個別のイベント位置
│       ├── position.json
│       └── threads\<tid>\{info.json, registers.txt, frames\}
├── bookmarks\          セッション中のみ有効な名前付き位置 (New-TtdBookmark)
├── calls\              関数呼び出し履歴
│   └── <module>\<function>\    <= 256 hits ならフラット、超えると <start>-<end>\ で自動ページング
└── memory\             メモリアクセス履歴
    └── <start>_<end>\  {writes, reads, rw}\<n>.json, first-write.json, last-write-before\
```

## dir (Get-ChildItem)

```powershell
# スレッド一覧
dir dump:\threads

# フレーム一覧
dir dump:\threads\2\frames

# TTD の全例外イベント
dir ttd:\timeline\exceptions

# 特定モジュール・関数の呼び出し履歴 (自動ページング)
dir ttd:\calls\python313\PyObject_IsTrue
# →  0-255\, 256-511\, 512-767\, 768-1023\, 1024-1041\  (1042 hits)

# ヒープの型別統計 (上位 10 件、サイズ降順)
dir dump:\heap\types | Sort-Object TotalBytes -Descending | Select -First 10
```

## cd (Set-Location)

```powershell
# TTD の重要イベント位置に移動
cd ttd:\positions\last-meaningful-event

# そこからスレッド → フレームへ
cd threads\0xa098\frames

# ブックマーク経由でも同じツリーに飛べる
New-TtdBookmark crash-point 1CBF:8C0
cd ttd:\bookmarks\crash-point\threads\0xa098
```

## Get-Content (JSON / テキストを読む)

```powershell
# ダンプの概要
Get-Content dump:\summary.json | ConvertFrom-Json

# TTD の分類済みトリアージ (Markdown)
Get-Content ttd:\triage.md

# 特定フレームの詳細 (ソース位置を解決できていれば File/Line が入る)
Get-Content dump:\threads\2\frames\0.json | ConvertFrom-Json

# !analyze -v のキャッシュ出力
Get-Content dump:\analyze.txt
```

## Invoke-CrashCommand (raw dbgeng エスケープハッチ)

```powershell
# クラッシュダンプで !locks
Invoke-CrashCommand -Drive dump '!locks'

# TTD 特定位置で dx 式
Invoke-CrashCommand -Drive ttd -Position start 'dx -r0 @$curprocess.TTD.Lifetime'

# パイプラインで複数コマンドをまとめて
'r rax', 'r rip' | Invoke-CrashCommand -Drive dump
```

パスで表現できない問い合わせ (`!locks`、`!syncblk`、特殊な `.ecxr` ワークフローなど) のための脱出口です。`DbgEngSessionManager` 経由で既存のドライブと同一セッションを共有します。

## Read-CrashMemory (メモリ読み取り)

```powershell
# Hex 128 バイト (既定)
Read-CrashMemory 0x7ffe0000 -Drive dump

# ASCII / Unicode
Read-CrashMemory 0x00000001234 -Drive dump -Format Ascii -Length 64
Read-CrashMemory 0x00000001234 -Drive dump -Format Unicode

# TTD の特定位置で読む
Read-CrashMemory 0x1A3740078C0 -Drive ttd -Position start -Format Pointers
```

パス版 (`ttd:\memory\<start>_<end>\...`) は履歴を辿りやすく、`Read-CrashMemory` は一発読みに向く使い分けです。

## Get-CrashObject (マネージドヒープ検査)

```powershell
# アドレス指定でマネージドオブジェクトを取得
Get-CrashObject 0x1a3740656c0 -Drive dump

# 参照型メンバーを 1 段インライン展開
Get-CrashObject 0x1a3740656c0 -Drive dump -Expand
```

ClrMD ベースなので Dump ドライブ専用です。TTD のマネージドオブジェクト検査は将来的に SOS 経由で対応予定。

## Get-CrashLocalVariable (スタックフレームのローカル変数)

```powershell
# スレッド 2 のフレーム 0 のローカルを表示
Get-CrashLocalVariable 2 0 -Drive dump

# TTD で特定位置・スレッドのフレーム
Get-CrashLocalVariable 0xa098 3 -Drive ttd -Position last-meaningful-event
```

シンボル情報 (ローカル変数名・型) がないとあまり有用な出力は得られません。Microsoft の公開 PDB はパブリック PDB が多くこの情報が欠落しているため、自分でビルドしたコードでは充実します。

## TTD Bookmarks (名前付き位置)

```powershell
# 位置に名前を付ける
New-TtdBookmark crash-point 1CBF:8C0
New-TtdBookmark initial-load start

# 列挙
Get-TtdBookmark

# 削除
Remove-TtdBookmark initial-load
```

セッションローカル (drive を remove するとブックマークも消えます)。`ttd:\bookmarks\<name>\` 配下は `ttd:\positions\<encoded>\` と完全に同じツリー構造を持ちます。

## Position aliases (前倒しの目印)

```powershell
# 最初の例外発生時点へ即飛ぶ
cd ttd:\positions\first-exception

# そこからスレッド・フレームを辿る
dir threads
cd threads\0xa098\frames
```

`first-exception` / `last-exception` / `last-meaningful-event` は、backing データがあるときのみ enumeration に現れます。存在しないエイリアスを直接指定すると `ItemExists=false` を返します (例外がないトレースで `first-exception` はそもそも見えない)。

## Enable-CrashEditorFollow (cd でエディタ自動ジャンプ)

```powershell
# VS Code でフレームのソース位置を自動で開く
Enable-CrashEditorFollow -Editor code

# Cursor / Vim も可
Enable-CrashEditorFollow -Editor cursor
Enable-CrashEditorFollow -Editor vim

# 解除
Disable-CrashEditorFollow
```

`cd dump:\threads\2\frames\0` の瞬間に対応するソースファイル:行がエディタで開きます。`LocationChangedAction` フックとして実装されているため、手動で `Get-Content` する必要がありません。

## シンボルパス

既定は `srv*%LOCALAPPDATA%\dbg\sym` (ローカルキャッシュのみ、ネットワーク参照なし)。Microsoft シンボルサーバーから公開 PDB を取得したい場合は `-SymbolPath` で明示的に渡します:

```powershell
New-CrashDrive dmp .\crash.dmp `
    -SymbolPath 'srv*cache*https://msdl.microsoft.com/download/symbols'
```

既定を local-only にしているのは、大規模な dump で初回 `WaitForEvent` が数分ハングするのを避けるためです。

## マネージドソースコードの解決

Full dump ではスタックフレームの IP からソースファイル:行まで解決できます。WithHeap / Normal dump では `ClrRuntime.GetMethodByInstructionPointer` がコードヒープメタデータを要求するため、ClrMD スタックウォーカーを通じた IP→Method キャッシュで補完しています。ただし `ILOffsetMap` が空の場合は "メソッド名は出せるが行番号は出せない" ところまでしか辿れません — これは Normal dump の本質的な情報不足です。

## 制約

- Windows 限定 (dbgeng + ClrMD に依存)
- PowerShell 7.4+ (`PowerShellVersion = '7.4'`)
- TTD の .run を開くには WinDbg Preview が必要 (`winget install Microsoft.WinDbg`)
- 同時にマウントできる dbgeng ターゲットは 1 つ (プロセスワイドなシングルトン制約、`DbgEngSessionManager` が管理)

## See Also

- New-CrashDrive
- Invoke-CrashCommand
- Read-CrashMemory
- Get-CrashObject
- Get-CrashLocalVariable
- Enable-CrashEditorFollow
- New-TtdBookmark
