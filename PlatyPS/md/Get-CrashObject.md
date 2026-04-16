---
document type: cmdlet
external help file: CrashDrive.dll-Help.xml
HelpUri:
Locale: ja-JP
Module Name: CrashDrive
ms.date: 04/17/2026
PlatyPS schema version: 2024-05-01
title: Get-CrashObject
---

# Get-CrashObject

## SYNOPSIS

Dump ドライブの GC ヒープ上にあるマネージド (.NET) オブジェクトをアドレスから取得し、型名・サイズ・フィールド一覧を構造化して返します。

## SYNTAX

### __AllParameterSets

```
Get-CrashObject [-Address] <string> [-Drive <string>] [-Expand] [<CommonParameters>]
```

## DESCRIPTION

`Get-CrashObject` は ClrMD の `ClrHeap.GetObject` を使ってアドレスを解釈し、`Address`、`TypeName`、`Size`、`MethodTable` と、型に応じた追加プロパティを持つ `PSObject` を返します。

- **文字列** (`System.String`): `Value` プロパティに最大 2048 文字ぶんの文字列内容。
- **配列**: `Length` / `ElementType` / `Preview` (先頭最大 16 要素)。
- **通常の参照型**: `Fields` プロパティに各インスタンスフィールドの `Name` / `TypeName` / `Value`。参照型フィールドは既定でアドレス文字列 (`0x<hex> <TypeName>`) として表示され、`-Expand` 指定時のみ 1 段深く展開されます。

ClrMD 依存のため **Dump ドライブ専用**です。TTD のオブジェクト検査は SOS のロードが必要で、配線はされていません (代わりに `Invoke-CrashCommand '!do <addr>'` で SOS を直接呼べます)。CLR が含まれていない dump (ネイティブのみ) ではエラーになります。

ヒープ上の参照をたどる基本パターンは「フィールド値に出ているアドレスを次の `Get-CrashObject` に渡す」です。パイプライン入力に対応しているので、アドレス文字列を `Select-Object -ExpandProperty` で抜いてそのまま渡せます。

## EXAMPLES

### Example 1: アドレスを指定してオブジェクトを取得

```powershell
PS dmp:\> Get-CrashObject 0x00000233A5B4C0E0
```

指定アドレスのオブジェクトを取得し、`TypeName` / `Size` / `Fields` を持つ `PSObject` を返します。

### Example 2: ヒープから特定型のインスタンスを拾って検査

```powershell
PS dmp:\> Get-ChildItem dmp:\heap | ? TypeName -Match 'MyApp\.Order$' |
>>        Select-Object -First 1 -ExpandProperty SampleAddress |
>>        Get-CrashObject
```

`heap\` を走査して `MyApp.Order` 型の代表インスタンスを 1 つ取り出し、そのフィールドを表示します (`SampleAddress` は `heap\` のアイテムに付く代表アドレスです)。

### Example 3: 参照フィールドを 1 段展開して表示

```powershell
PS dmp:\> Get-CrashObject 0x00000233A5B4C0E0 -Expand
```

`-Expand` を付けると参照型フィールドがアドレス文字列ではなく、1 段展開された子 `PSObject` として表示されます。さらに深く辿りたい場合はアドレスを取り出して再帰的に `Get-CrashObject` を呼んでください。

### Example 4: フィールドから別オブジェクトへ辿る

```powershell
PS dmp:\> $o = Get-CrashObject 0x00000233A5B4C0E0
PS dmp:\> $o.Fields | Format-Table Name, TypeName, Value
PS dmp:\> $next = ($o.Fields | ? Name -eq '_items').Value -replace ' .*',''
PS dmp:\> Get-CrashObject $next
```

まず `Fields` を表形式で表示して目的の参照フィールド (`_items` など) のアドレス文字列を抜き、次の `Get-CrashObject` に渡して子オブジェクトを取得します。

### Example 5: 文字列オブジェクトの値を直接取得

```powershell
PS dmp:\> (Get-CrashObject 0x00000233A5B4C500).Value
```

対象が `System.String` の場合、`Value` プロパティに文字列内容がそのまま入っています。

## PARAMETERS

### -Address

取得するマネージドオブジェクトのアドレス。`0x` プレフィックス付きの 16 進、プレフィックスなしの 10 進を受け付けます。WinDbg 風の桁区切り (`_` や `` ` ``) は除去されます。パイプライン入力に対応しています。

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

対象ドライブ名。省略時はカレントロケーションのドライブが使われます。Dump 以外のドライブを指定するとエラーになります。

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

### -Expand

参照型フィールドを 1 段展開します。未指定時はアドレス文字列 (`0x<hex> <TypeName>`) として表示されます。さらに深く展開したい場合はアドレスを取り出して再帰的に呼んでください。

```yaml
Type: System.Management.Automation.SwitchParameter
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

`-Address` はパイプライン入力に対応しており、アドレス文字列を流し込んで一括検査できます。

## OUTPUTS

### System.Management.Automation.PSObject

`Address` / `TypeName` / `Size` / `MethodTable` と、型別の追加プロパティ (`Value` / `Length` / `ElementType` / `Preview` / `Fields`) を持つオブジェクト。

## NOTES

- Dump ドライブ専用です。Ttd / Trace ドライブを指定するとエラーになります。
- CLR が含まれない dump (ネイティブのみ) では `No CLR runtime in this dump` エラーになります。
- 配列の `Preview` は最大 16 要素までに切り詰められます。全要素を見たい場合は `!da <addr>` (SOS) を `Invoke-CrashCommand` 経由で呼ぶのが早道です。
- 参照型の深さは `-Expand` で 1 段のみ展開されます。再帰的な展開は呼び出し側で行ってください (無制限展開は循環参照で無限ループになるため意図的にしていません)。

## RELATED LINKS

- [Read-CrashMemory]()
- [Get-CrashLocalVariable]()
- [Invoke-CrashCommand]()
- [New-CrashDrive]()
