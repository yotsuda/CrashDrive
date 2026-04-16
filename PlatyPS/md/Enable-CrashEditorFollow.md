---
document type: cmdlet
external help file: CrashDrive.dll-Help.xml
HelpUri:
Locale: ja-JP
Module Name: CrashDrive
ms.date: 04/17/2026
PlatyPS schema version: 2024-05-01
title: Enable-CrashEditorFollow
---

# Enable-CrashEditorFollow

## SYNOPSIS

カレントロケーションが CrashDrive のパスに移動したとき、対応するソースファイル:行をエディタで自動的に開くモードを有効化します。

## SYNTAX

### __AllParameterSets

```
Enable-CrashEditorFollow [-EditorCommand <string>] [-ArgumentsTemplate <string>]
 [<CommonParameters>]
```

## DESCRIPTION

`Enable-CrashEditorFollow` はランスペースの `LocationChangedAction` に `cd` 時のフックを登録します。Trace / Dump / Ttd プロバイダのパスに `cd` したとき、対象アイテムが `SourceFile` と `Line` を持っていれば、指定エディタコマンドを非同期で起動してその位置へ飛びます。

既定では VS Code を想定し `code --goto "{file}:{line}"` を発行します。`-EditorCommand` と `-ArgumentsTemplate` を組み合わせることで、任意の CLI (Cursor / WebStorm / Vim 等) に差し替え可能です。`{file}` と `{line}` のプレースホルダが置換されます。

ソース解決が効く対象は以下です。

- **Dump**: ネイティブは dbgeng `ln`、マネージドは ClrMD + portable PDB (`DumpType.Full` 必須)。公開 Microsoft PDB には source info がないので飛べません。
- **TTD**: 同じく dbgeng `ln` 経路。
- **Trace**: Python トレーサーは実ファイル/行を持ちますが、.NET Harmony トレーサーは現状 `file=<assembly-name> line=0` なのでエディタは開きません (ロードマップ項目)。

無効化するには `Disable-CrashEditorFollow` を呼びます。セッション越しには永続化しないので、プロファイルで有効化することが多くなります。

## EXAMPLES

### Example 1: VS Code で有効化 (既定)

```powershell
PS C:\> Enable-CrashEditorFollow
PS C:\> cd dmp:\threads\12\frames\3
```

既定設定 (`code --goto "{file}:{line}"`) で有効化し、`cd` するとそのフレームのソースで VS Code が開きます。ソース解決できないアイテムでは何もせず、エラーも出ません。

### Example 2: Cursor で有効化

```powershell
PS C:\> Enable-CrashEditorFollow -EditorCommand cursor
```

`EditorCommand` だけを差し替えて Cursor を起動します。引数テンプレートは既定 (`--goto "{file}:{line}"`) のまま流用できます。

### Example 3: Vim (gvim) 用に引数テンプレートをカスタマイズ

```powershell
PS C:\> Enable-CrashEditorFollow -EditorCommand gvim -ArgumentsTemplate '+{line} "{file}"'
```

エディタ固有の行ジャンプ構文に合わせてテンプレートを変えます。`{file}` と `{line}` は必ずテンプレート内で使ってください (置換対象)。

### Example 4: プロファイルから常時有効化

```powershell
# $PROFILE
Import-Module CrashDrive
Enable-CrashEditorFollow
```

pwsh のプロファイルで有効化しておけば、セッション起動のたびに CrashDrive + editor-follow が即座に使える状態になります。

### Example 5: 一時的に無効化

```powershell
PS C:\> Disable-CrashEditorFollow
# ...デバッグ作業中は飛ばさない...
PS C:\> Enable-CrashEditorFollow
```

タブが増えすぎるのを避けたいときは `Disable-CrashEditorFollow` で一時停止し、必要になったら再度 `Enable-CrashEditorFollow` を呼びます。

## PARAMETERS

### -ArgumentsTemplate

エディタコマンドに渡す引数テンプレート。`{file}` と `{line}` がそれぞれ解決済みのフルパスと行番号に置換されます。既定は `--goto "{file}:{line}"` (VS Code 向け)。

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

### -EditorCommand

起動するエディタの実行ファイル名またはフルパス。`code`、`cursor`、`gvim`、`C:\Program Files\...\idea64.exe` など PATH 解決できる任意のコマンドを指定可能。既定は `code`。

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

パイプライン入力はありません。

## OUTPUTS

### None

出力はありません (`-Verbose` 時のみ設定内容が出力されます)。

## NOTES

- 設定はランスペースの `LocationChangedAction` を書き換える形で保持されます。別セッションには引き継がれないので、常用するならプロファイルでの有効化を推奨します。
- ソース解決は Dump では `DumpType.Full` 推奨 (Normal dump はマネージドフレームで行が取れないことがあります)。公開 Microsoft PDB の `ln` は source info を含まないので飛べません。
- .NET Harmony トレース (`Trace` プロバイダ) は現状 `file=<assembly-name> line=0` なのでエディタは起動しません。portable PDB の sequence point ルックアップ対応はロードマップ項目です。
- エラーは握り潰されます (`try { ... } catch { }`): エディタが見つからない・起動できない場合でも CrashDrive 側の操作は中断されません。

## RELATED LINKS

- [Disable-CrashEditorFollow]()
- [New-CrashDrive]()
- [Get-CrashLocalVariable]()
