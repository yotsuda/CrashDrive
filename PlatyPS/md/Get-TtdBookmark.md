---
document type: cmdlet
external help file: CrashDrive.dll-Help.xml
HelpUri:
Locale: ja-JP
Module Name: CrashDrive
ms.date: 04/17/2026
PlatyPS schema version: 2024-05-01
title: Get-TtdBookmark
---

# Get-TtdBookmark

## SYNOPSIS

TTD ドライブに登録されているブックマークを列挙、もしくは名前で 1 件取得します。

## SYNTAX

### __AllParameterSets

```
Get-TtdBookmark [[-Name] <string>] [-Drive <string>] [<CommonParameters>]
```

## DESCRIPTION

`Get-TtdBookmark` は TTD ドライブに紐付いたブックマーク辞書を参照するコマンドです。`-Name` 省略時は全ブックマークを名前順 (序数順) で列挙し、指定時はそのエントリだけを返します。

戻り値は `New-TtdBookmark` と同じ形の `PSObject` (`Name` / `Position` / `Drive`) で、Position は常に native 形式 (`major:minor`) に正規化されています。スクリプトから位置情報を取り出して `Invoke-CrashCommand -Position` などに流し込む用途に向きます。

存在しない名前を指定した場合は `ItemNotFoundException` が `WriteError` されます (パイプライン処理は継続)。

## EXAMPLES

### Example 1: 全ブックマークを一覧

```powershell
PS ttd:\> Get-TtdBookmark
```

登録されているすべてのブックマークを名前順に列挙します。未登録時は何も出力しません (エラーにはなりません)。

### Example 2: 名前で 1 件取得

```powershell
PS ttd:\> Get-TtdBookmark crash-point
```

`crash-point` という名前のブックマークのみ返します。存在しない場合は `BookmarkNotFound` エラーになります。

### Example 3: Position を取り出して Invoke-CrashCommand に流す

```powershell
PS ttd:\> $bm = Get-TtdBookmark crash-point
PS ttd:\> Invoke-CrashCommand -Command 'k' -Position $bm.Position.Replace(':','_')
```

ブックマークから native 形式の position を取り出し、path 形式に変換して `Invoke-CrashCommand` に渡します。

### Example 4: ブックマーク付きの位置へまとめて cd

```powershell
PS ttd:\> Get-TtdBookmark | % { Write-Host $_.Name; cd "ttd:\bookmarks\$($_.Name)"; ls }
```

すべてのブックマーク位置を順に訪問し、それぞれの場所でスタック/スレッドを眺めます。定点観測のような使い方に便利です。

### Example 5: 特定ドライブのブックマークだけを見る

```powershell
PS C:\> Get-TtdBookmark -Drive ttd2
```

複数 TTD ドライブをマウントしているとき、カレントロケーションに関係なく `ttd2` ドライブのブックマークだけを取得します。

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

取得するブックマーク名。省略時は全ブックマークを列挙します。存在しない名前を指定するとエラーになります。

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: 0
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

パイプライン入力はありません。

## OUTPUTS

### System.Management.Automation.PSObject

`Name` / `Position` (native 形式文字列) / `Drive` (ドライブ名) の 3 プロパティを持つオブジェクト。列挙時は複数、名前指定時は 0 または 1 件。

## NOTES

- Ttd ドライブ専用です。Dump / Trace ドライブに対して呼ぶとエラーになります。
- 列挙は名前の序数順 (`StringComparer.Ordinal`) です。大文字小文字は区別されます。
- Position は常に native 形式 (`major:minor`) で返されます。path 形式に変換するには `.Replace(':','_')` を挟んでください。
- `ttd:\bookmarks\` を `ls` した結果と対になる情報ですが、`Get-TtdBookmark` は生の辞書を返すので、スクリプトから扱う際はこちらの方がシンプルです。

## RELATED LINKS

- [New-TtdBookmark]()
- [Remove-TtdBookmark]()
- [Invoke-CrashCommand]()
