---
document type: cmdlet
external help file: CrashDrive.dll-Help.xml
HelpUri:
Locale: ja-JP
Module Name: CrashDrive
ms.date: 04/17/2026
PlatyPS schema version: 2024-05-01
title: New-TtdBookmark
---

# New-TtdBookmark

## SYNOPSIS

TTD ドライブ上の特定位置に覚えやすい名前を付け、`ttd:\bookmarks\<name>\` としてアクセス可能にします。

## SYNTAX

### __AllParameterSets

```
New-TtdBookmark [-Name] <string> [-Position] <string> [-Drive <string>] [<CommonParameters>]
```

## DESCRIPTION

`New-TtdBookmark` は TTD の時間位置に別名を付けるコマンドです。登録したブックマークは `ttd:\bookmarks\<Name>\` として露出し、内部的には `ttd:\positions\<encoded>\` と同じレイアウト (`position.json`、`threads\<id>\frames\<n>\` など) を持ちます。

同じ TTD レコーディングを繰り返し調べるときに、`1CBF:8C0` のような native 位置文字列を何度も打ち込む代わりに `ttd:\bookmarks\crash-point\` のような安定した名前で参照できます。

ブックマークは **セッションローカル** で、`TtdDriveInfo` (PSDrive のサブクラス) 上の辞書に保持されます。`Remove-PSDrive` や pwsh 終了で消えます。永続化が必要な場合はスクリプト/プロファイルで起動時に `New-TtdBookmark` を呼び直す構成にしてください。

`-Name` はパス区切り (`/`、`\`、`:`) を含めず、かつ空でないことが求められます。既存の同名ブックマークは上書きされます (エラーにはなりません)。

## EXAMPLES

### Example 1: 例外発生位置にブックマークを付ける

```powershell
PS ttd:\> New-TtdBookmark crash-point 1CBF:8C0
```

`1CBF:8C0` に `crash-point` という名前を付けます。以降 `cd ttd:\bookmarks\crash-point\` でこの位置に戻れます。

### Example 2: path 形式の position からブックマーク

```powershell
PS ttd:\positions\1CBF_8C0\> New-TtdBookmark crash-point 1CBF_8C0
```

path 形式 (`_` 区切り) も受け付けられ、内部的には native 形式 (`:` 区切り) に正規化されます。`ttd:\positions\` を歩いて見つけた位置をそのままブックマークする定番パターンです。

### Example 3: Lifetime 端を名前付けする

```powershell
PS ttd:\> New-TtdBookmark prog-start start
PS ttd:\> New-TtdBookmark prog-end   end
```

`start` / `end` エイリアスは自動的に Lifetime の開始/終了の native 位置に解決されます。プログラム全体を走査する起点・終点を一度決めておく、というワークフローに使えます。

### Example 4: ドライブを明示指定 (複数 TTD 比較)

```powershell
PS C:\> New-TtdBookmark -Name crash-point -Position 1CBF:8C0 -Drive ttd1
PS C:\> New-TtdBookmark -Name crash-point -Position 2A5F:100 -Drive ttd2
```

複数の TTD ドライブをマウントしている場合、同じ名前のブックマークをドライブ別に付けて、`cd ttd1:\bookmarks\crash-point` と `cd ttd2:\bookmarks\crash-point` で対照比較ができます。

### Example 5: パイプラインで結果を確認

```powershell
PS ttd:\> New-TtdBookmark crash-point 1CBF:8C0 | Format-List
```

返値は `Name` / `Position` / `Drive` プロパティを持つ `PSObject` です。Position は常に native 形式に正規化されているので、スクリプトから参照する場合はこちらが安定しています。

## PARAMETERS

### -Drive

対象ドライブ名。省略時はカレントロケーションのドライブが使われます。Ttd 以外のドライブを指定するとエラーになります。

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

### -Name

ブックマーク名。空文字不可、`/`、`\`、`:` を含めることはできません。既存の同名ブックマークは上書きされます。

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

### -Position

ブックマークする時間位置。ネイティブ形式 `major:minor`、パス形式 `major_minor`、もしくはエイリアス `start` / `end` を受け付けます。エイリアスは Lifetime 開始/終了の native 位置に解決されます。

```yaml
Type: System.String
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

### CommonParameters

This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable,
-InformationAction, -InformationVariable, -OutBuffer, -OutVariable, -PipelineVariable,
-ProgressAction, -Verbose, -WarningAction, and -WarningVariable. For more information, see
[about_CommonParameters](https://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

パイプライン入力はありません。

## OUTPUTS

### System.Management.Automation.PSObject

`Name` / `Position` (native 形式文字列) / `Drive` (ドライブ名) の 3 プロパティを持つオブジェクト。

## NOTES

- ブックマークはセッションローカルで、ドライブが `Remove-PSDrive` されるか pwsh が終了すると失われます。永続化にはプロファイルスクリプトで再登録してください。
- Ttd ドライブ専用です。Dump / Trace ドライブに対して呼ぶとエラーになります。
- 同名の上書きは許容されます (エラーなし)。確実に作り直したい場合は先に `Remove-TtdBookmark` を呼ぶ必要はありません。
- `ttd:\bookmarks\<name>\` と `ttd:\positions\<encoded>\` は中身のレイアウトが揃えてあり、パス指向の操作がそのまま使えます。

## RELATED LINKS

- [Get-TtdBookmark]()
- [Remove-TtdBookmark]()
- [Invoke-CrashCommand]()
- [New-CrashDrive]()
