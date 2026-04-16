---
document type: cmdlet
external help file: CrashDrive.dll-Help.xml
HelpUri:
Locale: ja-JP
Module Name: CrashDrive
ms.date: 04/17/2026
PlatyPS schema version: 2024-05-01
title: Read-CrashMemory
---

# Read-CrashMemory

## SYNOPSIS

Dump / Ttd ドライブのメモリアドレスを読み取り、Hex / ASCII / Unicode / DWORD / QWORD / ポインタチェーン形式で返します。

## SYNTAX

### __AllParameterSets

```
Read-CrashMemory [-Address] <string> [-Drive <string>] [-Position <string>] [-Length <int>]
 [-Format <string>] [<CommonParameters>]
```

## DESCRIPTION

`Read-CrashMemory` はドライブの共有 dbgeng セッション (`ExecuteDbgCommand` 経由) で `db` / `da` / `du` / `dd` / `dq` / `dps` を発行し、出力テキストをそのまま返します。プロバイダと同じセッションを使うのでシンボル状態は共有され、エンジンの多重起動も起きません。

Dump ドライブでは単純にアドレスを読みます。Ttd ドライブでは `-Position` で時間位置を指定でき、省略時はセッションが最後に seek された位置 (通常は Lifetime end) がそのまま使われます。

パス指向の代替として `ttd:\memory\<start>_<end>\` のツリーも存在し、範囲内の読み書きアクセスや先頭書き込み位置などを構造化して見られます。アドホックに任意アドレスを読みたい、あるいは別フォーマットで見たいときに本 cmdlet を使い、「どこで誰がこのメモリを触ったか」を辿る用途ではパスを使うのが自然です。

Trace ドライブでは dbgeng バックエンドを持たないため使えません (エラー)。

## EXAMPLES

### Example 1: Dump ドライブで既定の Hex ダンプ

```powershell
PS dmp:\> Read-CrashMemory 0x7ffb12340000
```

`0x7ffb12340000` から 128 バイトを `db` で読み、バイト列と ASCII 表現が付いた整形済みテキストを返します。

### Example 2: Unicode 文字列として読み取り

```powershell
PS dmp:\> Read-CrashMemory 0x00000233A5B4C0E0 -Format Unicode -Length 64
```

`du` で 64 ワイド文字ぶん読み取ります。`-Length` は `db` / `da` などと同じ意味で、「何バイト / 何要素まで読むか」を指定します (既定 128)。

### Example 3: TTD の特定位置でポインタチェーンを展開

```powershell
PS ttd:\> Read-CrashMemory 0x7ff7ab12c080 -Position 1CBF_8C0 -Format Pointers -Length 8
```

TTD を `1CBF:8C0` にシークしてから `dps` で 8 ワード読み、各ワードに対応するシンボル付きアドレスを返します。`-Position` は path 形式 (`1CBF_8C0`) / native 形式 (`1CBF:8C0`) / `start` / `end` エイリアスを受け付けます。

### Example 4: QWORD 並びとして読み取り

```powershell
PS dmp:\> Read-CrashMemory 0x00007ffd_12345000 -Format Qword -Length 32
```

`dq` で 32 QWORD を読み取ります。アドレスの `_` と `` ` `` は区切り文字として除去されるので、WinDbg の表記をそのまま貼り付けられます。

### Example 5: ドライブを明示的に指定

```powershell
PS C:\> Read-CrashMemory 0x7ffb12340000 -Drive dmp -Format Ascii
```

カレントロケーションが CrashDrive 外にあっても、`-Drive` で対象ドライブを指定して読めます。Dump / Ttd 以外のドライブを指すとエラーになります。

## PARAMETERS

### -Address

読み取るメモリアドレス。`0x` プレフィックス付きの 16 進、プレフィックスなしの 10 進を受け付けます。WinDbg 風の桁区切り (`_` や `` ` ``) は除去されます。

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: 0
  IsRequired: true
  ValueFromPipeline: false
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

### -Format

出力フォーマット。`Hex` (`db`) / `Ascii` (`da`) / `Unicode` (`du`) / `Dword` (`dd`) / `Qword` (`dq`) / `Pointers` (`dps`)。既定は `Hex`。

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
AcceptedValues:
- Hex
- Ascii
- Unicode
- Dword
- Qword
- Pointers
HelpMessage: ''
```

### -Length

読み取り要素数 (バイト単位で `db` / `da`、ワイド文字単位で `du`、DWORD/QWORD/ポインタ単位でそれぞれ)。既定 128。dbgeng の `L0n<decimal>` に変換されます。

```yaml
Type: System.Int32
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

TTD 限定: 読み取り前にシークする位置。ネイティブ `major:minor`、パス形式 `major_minor`、`start` / `end` エイリアスを受け付けます。Dump ドライブでは無視されます。省略時は直近に seek された位置 (通常は Lifetime end) のままで読みます。

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

### None

パイプライン入力はありません。パラメータはすべて名前付きまたは位置指定で渡します。

## OUTPUTS

### System.String

dbgeng がキャプチャした整形済み出力テキスト (複数行を含む 1 つの文字列)。

## NOTES

- Trace プロバイダのドライブでは使えません (dbgeng バックエンドを持たないため)。
- 読み取りは共有セッションで行われ、`-Position` 指定時は TTD のカレント位置を変更します。続けて `Invoke-CrashCommand` で別コマンドを流すと影響が残る点に注意してください。
- 範囲内の読み書きアクセスや「最後にこのアドレスに書いたのは誰か」を辿るなら、path 側の `ttd:\memory\<start>_<end>\` ツリーの方が構造化されていて便利です。
- 出力は文字列 1 個です。行ごとに処理したい場合は `-split "`r?`n"` 等で分割してください。

## RELATED LINKS

- [Invoke-CrashCommand]()
- [Get-CrashObject]()
- [Get-CrashLocalVariable]()
- [New-CrashDrive]()
