---
document type: cmdlet
external help file: CrashDrive.dll-Help.xml
HelpUri:
Locale: ja-JP
Module Name: CrashDrive
ms.date: 04/17/2026
PlatyPS schema version: 2024-05-01
title: New-CrashDrive
---

# New-CrashDrive

## SYNOPSIS

ポストモーテム成果物 (dump / TTD / トレース) を PSDrive としてマウント、またはプログラムをトレーサー配下で実行して取得したトレースをマウントします。

## SYNTAX

### FromFile (Default)

```
New-CrashDrive [-Name] <string> [-Path] <string> [-SymbolPath <string>] [-PassThru]
 [<CommonParameters>]
```

### Capture

```
New-CrashDrive [-Name] <string> -ExecutablePath <string> [-Language <string>]
 [-ExecutableArgs <string[]>] [-TimeoutSeconds <int>] [-OutputFile <string>]
 [-EventTypes <string[]>] [-IncludeGlobals] [-Watch <string[]>] [-Include <string[]>]
 [-Exclude <string[]>] [-PassThru] [<CommonParameters>]
```

## DESCRIPTION

`New-CrashDrive` は CrashDrive 唯一のマウントコマンドで、二つのパラメータセットを持ちます。

**FromFile モード (既定)** は既存の成果物をマウントします。`-Path` に渡されたファイルの拡張子から自動判別され、`.dmp` → Dump、`.run` → Ttd、`.jsonl` → Trace のプロバイダが選ばれます。マウント後は `cd <name>:\` で内部ツリーをブラウズできます。

**Capture モード** は `-ExecutablePath` で指定したプログラムを言語ネイティブのトレーサー配下で起動し、出力された JSONL トレースをそのままマウントします。`-Language` は拡張子から自動推定され (`.py` / `.pyw` → python、`.exe` / `.dll` → dotnet)、必要に応じて明示指定もできます。

`-Path` と `-ExecutablePath` はあえて対称ではなく、「既存の成果物を読む」のか「プロセスを起動する」のかが呼び出し側で一目で分かるように設計されています。

`-SymbolPath` は Dump / Ttd のシンボルパスを上書きします。既定は `srv*%LOCALAPPDATA%\dbg\sym` (ローカルキャッシュのみ) で、Microsoft シンボルサーバーから公開 PDB を取得する場合は明示的に `srv*cache*https://msdl.microsoft.com/download/symbols` を渡します。

## EXAMPLES

### Example 1: 既存のクラッシュダンプをマウント

```powershell
PS C:\> New-CrashDrive dmp .\crash.dmp
PS C:\> cd dmp:\
PS dmp:\> ls
```

`.dmp` を Dump プロバイダでマウントし、`threads\`、`modules\`、`heap\` などを持つ仮想ファイルシステムとして公開します。

### Example 2: TTD レコーディングをマウント

```powershell
PS C:\> New-CrashDrive ttd .\recording.run
PS C:\> cd ttd:\calls\python313\PyObject_IsTrue
PS ttd:\calls\python313\PyObject_IsTrue\> ls
```

`.run` を Ttd プロバイダでマウントすると `calls\<module>\<fn>\` の下に対象関数の全呼び出しが並び、1 つずつ `cd` で開けばその位置の呼び出しスタック・引数が見られます。

### Example 3: Python トレーサーで新規キャプチャ + マウント

```powershell
PS C:\> New-CrashDrive app -ExecutablePath .\sample_target.py -IncludeGlobals
PS C:\> cd app:\by-function
```

`sample_target.py` を Python `sys.monitoring` トレーサー配下で実行し、生成された JSONL をそのまま Trace プロバイダでマウントします。`-IncludeGlobals` でグローバル変数のスナップショットも記録されます。

### Example 4: .NET プログラムをトレース (Harmony)

```powershell
PS C:\> New-CrashDrive app -ExecutablePath .\MyApp.exe `
>>       -Include 'MyApp*' `
>>       -ExecutableArgs @('--flag', 'value')
```

`MyApp.exe` を `DOTNET_STARTUP_HOOKS` 経由の Harmony トレーサー配下で実行し、`MyApp*` に一致するアセンブリのみをパッチ対象にします。`--flag value` はターゲットプログラムへの引数として渡されます。

### Example 5: シンボルサーバーを指定してマウント

```powershell
PS C:\> New-CrashDrive dmp .\pwsh.dmp -SymbolPath 'srv*cache*https://msdl.microsoft.com/download/symbols'
```

リモートシンボルサーバーから PDB を取得しながらマウントします。既定ではローカルキャッシュのみで、既にダウンロード済みの PDB だけが解決されます。

### Example 6: PassThru で PSDriveInfo を受け取る

```powershell
PS C:\> $d = New-CrashDrive dmp .\crash.dmp -PassThru
PS C:\> $d | Format-List Name, Provider, Root
```

`-PassThru` をつけるとマウントされたドライブの `PSDriveInfo` がパイプラインに流れます。

## PARAMETERS

### -EventTypes

Python トレーサー限定: 記録するイベント種別。`call` / `return` / `exception` から複数選択可能。既定はすべて (`call,return,exception`)。`-Language dotnet` では無視されます。

```yaml
Type: System.String[]
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: Capture
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Exclude

.NET トレーサー限定: パッチ対象から除外するアセンブリ名の glob パターン。`-Include` と併用可能。既定フィルタから更に絞り込みたい場合に使用します。`-Language python` では無視されます。

```yaml
Type: System.String[]
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: Capture
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -ExecutableArgs

ターゲットプログラムへ渡すコマンドライン引数。残余引数として受け取るので、`-ExecutableArgs @('--flag','value')` のほか、末尾に素で並べる書き方も可能です。

```yaml
Type: System.String[]
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: Capture
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: true
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -ExecutablePath

トレーサー配下で実行するプログラムのパス。`.py` / `.pyw` / `.exe` / `.dll` を受け付けます。あえて名前付きのみ (非 positional) とし、既存成果物をマウントする `-Path` と取り違えないようにしています。

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: Capture
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Include

.NET トレーサー限定: パッチ対象アセンブリ名の glob パターン (例: `'MyApp*'`)。指定時は既定の user-authored フィルタを上書きし、一致するアセンブリのみが Harmony でパッチされます。`-Language python` では無視されます。

```yaml
Type: System.String[]
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: Capture
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -IncludeGlobals

Python トレーサー限定: グローバル変数のスナップショットもトレースに含めます。`-Language dotnet` では無視されます。

```yaml
Type: System.Management.Automation.SwitchParameter
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: Capture
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Language

トレーサー言語。`python` または `dotnet`。省略時は `-ExecutablePath` の拡張子から自動推定されます (`.py`/`.pyw` → `python`、`.exe`/`.dll` → `dotnet`)。拡張子が非標準の場合のみ明示指定が必要です。

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: Capture
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

マウント先 PSDrive の名前 (例: `dmp`、`ttd`、`app`)。`cd <Name>:\` でアクセスします。

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

### -OutputFile

Capture モード限定: トレース JSONL の出力先パス。省略時は `%TEMP%\crashdrive_trace_<timestamp>_<guid>.jsonl` に書き出されます。

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: Capture
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -PassThru

指定時はマウントした `PSDriveInfo` をパイプラインに返します。既定ではドライブだけ作られ、出力はありません。

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

### -Path

FromFile モード: マウントする既存成果物のパス。`.dmp` (Dump)、`.run` (Ttd)、`.jsonl` (Trace) のいずれか。拡張子でプロバイダが自動決定されます。エイリアス `-File` も使えます。

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases:
- File
ParameterSets:
- Name: FromFile
  Position: 1
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -SymbolPath

FromFile モード: dbgeng の `.sympath` を上書きします。既定は `srv*%LOCALAPPDATA%\dbg\sym` (ローカルキャッシュのみ)。リモート取得したい場合は `srv*cache*https://msdl.microsoft.com/download/symbols` のように指定します。

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: FromFile
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -TimeoutSeconds

Capture モード限定: ターゲットプロセスの実行タイムアウト (秒)。範囲は 1 ～ 3600、既定 60 秒。超過時はプロセスツリーを kill し、その時点までの部分トレースを残したうえで `TimeoutException` を投げます。

```yaml
Type: System.Int32
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: Capture
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Watch

Python トレーサー限定: 値の変化を追跡する変数名のリスト (カンマ区切りで渡す配列)。指定した名前が `call` / `return` イベントのたびに評価されます。`-Language dotnet` では無視されます。

```yaml
Type: System.String[]
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: Capture
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

### None

既定では何も出力しません。`-PassThru` 指定時のみ `System.Management.Automation.PSDriveInfo` を返します。

## NOTES

- TTD (`.run`) のマウントには WinDbg Preview (Microsoft Store 版) が必要です。System32 の dbgeng では `.run` を開けません。
- Python トレース取得には Python 3.12+、.NET トレース取得には .NET 6+ のターゲットが必要です (`DOTNET_STARTUP_HOOKS` の都合)。
- Capture モードで生成された JSONL は既定で `%TEMP%` に残ります。後から再マウントしたい場合は `-OutputFile` で保存先を指定してください。
- ソース行の解決は Dump の場合 `DumpType.Full` が必要です。Normal dump ではメソッド名までは取れても ILOffsetMap が空で行が取れない、という挙動になります (データの制約であり、回避策はありません)。

## RELATED LINKS

- [Invoke-CrashCommand]()
- [Read-CrashMemory]()
- [Get-CrashObject]()
- [Get-CrashLocalVariable]()
- [Enable-CrashEditorFollow]()
- [New-TtdBookmark]()
