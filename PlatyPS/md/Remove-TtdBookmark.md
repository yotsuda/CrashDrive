---
document type: cmdlet
external help file: CrashDrive.dll-Help.xml
HelpUri:
Locale: ja-JP
Module Name: CrashDrive
ms.date: 04/17/2026
PlatyPS schema version: 2024-05-01
title: Remove-TtdBookmark
---

# Remove-TtdBookmark

## SYNOPSIS

TTD ドライブに登録されたブックマークを名前で削除します。存在しない名前を指定しても無害 (silent no-op) です。

## SYNTAX

### __AllParameterSets

```
Remove-TtdBookmark [-Name] <string> [-Drive <string>] [<CommonParameters>]
```

## DESCRIPTION

`Remove-TtdBookmark` は TTD ドライブのブックマーク辞書から指定名のエントリを削除します。削除後は `ttd:\bookmarks\<Name>\` はアクセス不能になります (親の `bookmarks\` 列挙からも消えます)。

存在しない名前を渡しても **エラーにはなりません** (`ConcurrentDictionary.TryRemove` が失敗するだけ)。スクリプトから冪等に呼ぶ用途を想定しています。一覧を確認してから削除したい場合は `Get-TtdBookmark` と組み合わせてください。

削除対象は `TtdDriveInfo` 上の in-memory 辞書のみで、TTD レコーディングファイル (`.run`) には一切手を加えません。

## EXAMPLES

### Example 1: 名前を指定して削除

```powershell
PS ttd:\> Remove-TtdBookmark crash-point
```

`crash-point` ブックマークを削除します。以降 `ttd:\bookmarks\crash-point\` は見えなくなります。

### Example 2: 全ブックマークをまとめて削除

```powershell
PS ttd:\> Get-TtdBookmark | % { Remove-TtdBookmark $_.Name }
```

登録済みブックマークを全件削除します。`TtdDriveInfo` を `Remove-PSDrive` するだけでも消えますが、ドライブは残したまま辞書だけクリアしたい場合に使います。

### Example 3: 存在しない名前でも安全に呼べる (冪等)

```powershell
PS ttd:\> Remove-TtdBookmark crash-point
PS ttd:\> Remove-TtdBookmark crash-point   # 何も起きない
```

2 回目の呼び出しはサイレントで何もしません。スクリプトの冪等なクリーンアップ処理としてそのまま埋め込めます。

### Example 4: 特定ドライブから削除

```powershell
PS C:\> Remove-TtdBookmark -Name crash-point -Drive ttd2
```

複数 TTD ドライブをマウントしているとき、`-Drive` でどのドライブから消すかを明示します。

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

削除するブックマーク名。存在しない名前を指定してもエラーにはならず、何も起きません。

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

パイプライン入力はありません。

## OUTPUTS

### None

出力はありません。

## NOTES

- Ttd ドライブ専用です。Dump / Trace ドライブに対して呼ぶとエラーになります。
- 存在しない名前に対しても silent no-op のため、事前チェック (`Get-TtdBookmark -Name <name>`) は不要です。
- 削除は in-memory の辞書のみで、TTD レコーディングファイル (`.run`) には一切影響しません。
- `Remove-PSDrive` でドライブそのものを外せば、そのドライブのブックマークもすべて破棄されます。

## RELATED LINKS

- [New-TtdBookmark]()
- [Get-TtdBookmark]()
