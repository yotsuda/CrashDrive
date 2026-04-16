---
document type: cmdlet
external help file: CrashDrive.dll-Help.xml
HelpUri:
Locale: ja-JP
Module Name: CrashDrive
ms.date: 04/17/2026
PlatyPS schema version: 2024-05-01
title: Get-CrashLocalVariable
---

# Get-CrashLocalVariable

## SYNOPSIS

Dump / Ttd ドライブの特定スタックフレームにおけるローカル変数を dbgeng 経由で取得します。

## SYNTAX

### __AllParameterSets

```
Get-CrashLocalVariable [-ThreadId] <string> [-Frame] <int> [-Drive <string>] [-Position <string>]
 [<CommonParameters>]
```

## DESCRIPTION

`Get-CrashLocalVariable` は dbgeng のコマンド列 `~~[0x<tid>]s; .frame 0n<n>; dv /V /i /t` を発行し、指定スレッド・指定フレームのローカル変数 (値 + 型 + 間接アドレス) を返します。TTD ドライブでは `-Position` により時間位置も指定できます。

出力の質は **シンボルの可用性に完全に依存します**。プライベートビルドやソースインデックス付き PDB があれば名前 / 型 / 値がすべて揃いますが、Microsoft の公開 PDB のように locals/params 情報を含まない PDB ではほとんど何も出ません (これはデータ側の制約で、回避策はありません)。

マネージドフレーム (JIT コード) に対しては dbgeng は JIT 出力しか見えず、メソッドシグネチャを知らないので `dv` は意味のある結果を返しません。正しいマネージドローカル取得には SOS 拡張が必要ですが、そちらはまだ配線されていません。当面は `Invoke-CrashCommand '!clrstack -l'` などを経由してください。

Dump ドライブでは `-ThreadId` にマネージドスレッド ID (例: `20`) を渡すと自動的に OS スレッド ID に解決されます。16 進の OS ID (`0x20` / `20` / `a098` など) もそのまま受け付けます。

## EXAMPLES

### Example 1: Dump でスレッド 0 のトップフレームのローカルを取得

```powershell
PS dmp:\> Get-CrashLocalVariable 0x1a98 0
```

OS スレッド ID `0x1a98` のフレーム 0 (最も内側) に対して `dv /V /i /t` を発行します。`-ThreadId` は 16 進文字列で `0x` 付き・なし両方受け付けます。

### Example 2: マネージドスレッド ID で指定

```powershell
PS dmp:\> Get-CrashLocalVariable 20 2
```

Dump ドライブではマネージドスレッド ID (ClrMD の `ManagedThreadId`) を渡すと、対応する OS ID へ自動解決されます。フレーム 2 を指定しているので、上から 3 段目のフレームが対象です。

### Example 3: TTD で特定位置にシークして検査

```powershell
PS ttd:\> Get-CrashLocalVariable a098 0 -Position 1CBF_8C0
```

TTD を `1CBF:8C0` にシークした状態で、スレッド `0xa098` のフレーム 0 のローカルを取得します。時間位置を跨ぐ比較は、同じ `-ThreadId`/`-Frame` に対して `-Position` を変えながら呼ぶのが基本パターンです。

### Example 4: ドライブを明示的に指定

```powershell
PS C:\> Get-CrashLocalVariable -ThreadId 1a98 -Frame 0 -Drive dmp
```

CrashDrive 外から呼ぶ場合は `-Drive` で対象ドライブを明示します。Dump / Ttd 以外のドライブではエラーになります。

### Example 5: 複数フレームを横断的に比較

```powershell
PS dmp:\> 0..5 | % { "--- frame $_ ---"; Get-CrashLocalVariable 0x1a98 $_ }
```

フレーム 0〜5 のローカルを順に取得し、コールスタックを跨いで変数がどう変化しているかを一度に眺めます。

## PARAMETERS

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

### -Frame

対象フレームのインデックス (0 が最も内側)。`threads\<id>\frames\<n>` の `<n>` と一致します。

```yaml
Type: System.Int32
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: 1
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Position

TTD 限定: 評価前にシークする位置。ネイティブ `major:minor`、パス形式 `major_minor`、`start` / `end` エイリアスを受け付けます。Dump ドライブでは無視されます。

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

対象スレッドの ID。TTD では 16 進 OS ID (`0x` 付き・なし両方) を受け付け、`~~[0x<tid>]s` としてスレッド切替されます。Dump では OS ID のほか、`ClrMD.ManagedThreadId` を 10 進で渡すと自動的に OS ID に解決されます。

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

dbgeng がキャプチャした `dv /V /i /t` の出力テキスト (複数行を含む 1 つの文字列)。

## NOTES

- Trace プロバイダのドライブでは使えません (dbgeng バックエンドを持たないため)。
- locals/params 情報を持たない PDB (公開 Microsoft PDB など) に対してはほとんど何も出ません。これはデータ側の制約であり、ソースインデックス付き PDB やプライベートビルドが必要です。
- マネージドフレームに対する `dv` は意味のある結果を返しません (dbgeng は JIT 出力しか見えないため)。マネージドローカルには `Invoke-CrashCommand '!clrstack -l'` 等の SOS 経由が現状の回避策です。
- TTD で `-Position` を指定するとセッションのカレント位置が変わります。同じセッションで後続コマンドを実行する場合は影響が残る点に注意してください。

## RELATED LINKS

- [Read-CrashMemory]()
- [Get-CrashObject]()
- [Invoke-CrashCommand]()
- [Enable-CrashEditorFollow]()
