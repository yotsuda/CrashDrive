---
document type: cmdlet
external help file: CrashDrive.dll-Help.xml
HelpUri:
Locale: ja-JP
Module Name: CrashDrive
ms.date: 04/17/2026
PlatyPS schema version: 2024-05-01
title: Invoke-CrashCommand
---

# Invoke-CrashCommand

## SYNOPSIS

Dump / Ttd ドライブの共有 dbgeng セッションに対して任意の dbgeng コマンドを実行し、出力テキストを返します。

## SYNTAX

### __AllParameterSets

```
Invoke-CrashCommand [-Command] <string> [-Drive <string>] [-Position <string>] [-ThreadId <string>]
 [<CommonParameters>]
```

## DESCRIPTION

`Invoke-CrashCommand` はプロバイダが内部で使っているのと同じ dbgeng セッション (`DbgEngSessionManager.AcquireFor` 経由) でコマンドを実行します。別エンジンインスタンスを起こさないので、ロードしたシンボルや解決済みモジュールはそのまま使われ、他ドライブとの奪い合いも起きません。

**「パス優先」の設計思想の逃げ道として使う**ことを想定したコマンドです。`threads\`、`frames\`、`heap\` などで表現できる操作はプロバイダ経由の方が一貫性があり、AI エージェントにも扱いやすくなります。`!locks`、`!syncblk`、独自の `.ecxr` ワークフローのようにパスで表現しづらい dbgeng 拡張を呼びたい時にだけ使用してください。繰り返し使うパターンが現れたら、パス化する合図です。

TTD ドライブでは `-Position` / `-ThreadId` により、コマンド実行前に位置/スレッドへ seek できます。Dump ドライブではこれらは無視され、警告のみ出力されます (スレッド切替が必要なら `~~[0x<tid>]s; cmd` のように先頭に付けてください)。

## EXAMPLES

### Example 1: 例外解析を実行

```powershell
PS dmp:\> Invoke-CrashCommand '!analyze -v'
```

Dump ドライブで `!analyze -v` を流して詳細な例外解析テキストを取得します。`dmp:\analyze.txt` にも同内容がキャッシュされていますが、こちらはシンボル状態を変えた後にフレッシュに実行したい場合に使えます。

### Example 2: ロードモジュール一覧

```powershell
PS dmp:\> Invoke-CrashCommand lm
```

`lm` でロード済みモジュール一覧を取得します。整形済みテキストがそのまま返ります。

### Example 3: TTD で特定位置にシークしてから実行

```powershell
PS ttd:\> Invoke-CrashCommand -Command 'k' -Position 1CBF_8C0
```

TTD を `1CBF:8C0` にシークした状態でスタックトレース (`k`) を取得します。`-Position` は内部で `1CBF:8C0` (コロン区切り) に変換されてから `!tt` されます。`start` / `end` エイリアスも使えます。

### Example 4: TTD でスレッド切替 + データモデル呼び出し

```powershell
PS ttd:\> Invoke-CrashCommand -Command 'dx @$cursession.TTD.Events.Count()' -ThreadId a098
```

16 進のスレッド ID を指定して `~~[0xa098]s` 相当のスレッド切替をしたうえで、TTD データモデル経由で総イベント数を取得します。

### Example 5: パイプラインで複数コマンドを連続実行

```powershell
PS dmp:\> '!threads', '!clrstack', '!dso' | Invoke-CrashCommand
```

SOS 系コマンドをパイプで順次流し、出力テキストをまとめて受け取ります。いずれも同じセッションで実行されるので、シンボルロードのコストは初回のみです。

## PARAMETERS

### -Command

実行する dbgeng コマンド文字列。`!analyze -v`、`lm`、`k`、`dx @$cursession.TTD.Events.Count()` など WinDbg と同じ構文です。セミコロンで区切れば複数コマンドを 1 回の呼び出しで実行できます。

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: 0
  IsRequired: true
  ValueFromPipeline: true
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Drive

対象ドライブ名。省略時はカレントロケーションのドライブが使われます。Dump / Ttd 以外のドライブを指定するとエラーになります。

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Position

TTD 限定: コマンド実行前にシークする位置。ネイティブ形式 `major:minor`、パス形式 `major_minor`、もしくはエイリアス `start` / `end` を受け付けます。Dump ドライブでは無視され、警告が出力されます。

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -ThreadId

TTD 限定: コマンド実行前に切り替えるスレッド ID。16 進、`0x` プレフィックスあり/なし両方受け付け、`~~[0x<tid>]s` として発行されます。Dump ドライブでは無視され、警告が出力されます。

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### CommonParameters

This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable,
-InformationAction, -InformationVariable, -OutBuffer, -OutVariable, -PipelineVariable,
-ProgressAction, -Verbose, -WarningAction, and -WarningVariable. For more information, see
[about_CommonParameters](https://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### System.String

`-Command` はパイプライン入力に対応しています。複数のコマンド文字列を順次実行できます。

## OUTPUTS

### System.String

dbgeng がキャプチャした出力テキストがそのまま 1 つの文字列として返ります (複数行を含む改行付きテキスト)。

## NOTES

- Trace プロバイダのドライブでは使えません (dbgeng バックエンドを持たないため)。
- コマンドは共有セッションで実行されるので、`.reload` や `.sympath` のような副作用のあるコマンドは後続の操作にも影響します。
- Dump ドライブに `-Position` / `-ThreadId` を渡すと警告のみでコマンドは実行されます。スレッド切替が必要な場合は `Invoke-CrashCommand '~~[0x<tid>]s; k'` のように先頭で切り替えてください。
- 出力は文字列 1 個です。行ごとに処理したい場合は `-split "`r?`n"` 等で分割してください。

## RELATED LINKS

- [Read-CrashMemory]()
- [Get-CrashLocalVariable]()
- [Get-CrashObject]()
- [New-CrashDrive]()
