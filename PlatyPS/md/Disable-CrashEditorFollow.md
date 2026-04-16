---
document type: cmdlet
external help file: CrashDrive.dll-Help.xml
HelpUri:
Locale: ja-JP
Module Name: CrashDrive
ms.date: 04/17/2026
PlatyPS schema version: 2024-05-01
title: Disable-CrashEditorFollow
---

# Disable-CrashEditorFollow

## SYNOPSIS

`Enable-CrashEditorFollow` で設定したカレントロケーション変更フックを解除し、`cd` 時のエディタ自動ジャンプを無効化します。

## SYNTAX

### __AllParameterSets

```
Disable-CrashEditorFollow [<CommonParameters>]
```

## DESCRIPTION

`Disable-CrashEditorFollow` はランスペースの `LocationChangedAction` を `$null` に戻します。`Enable-CrashEditorFollow` で登録したフックが解除され、以降の `cd` ではエディタが起動しなくなります。

`Enable-CrashEditorFollow` を呼んでいない状態で呼んでも副作用はなく (既に `$null` に `$null` を代入するだけ)、安全に何度でも実行できます。

CrashDrive 本体の動作には影響しません。ドライブのマウントや `ls` / `cat` はそのまま使えます。

## EXAMPLES

### Example 1: エディタフォローを無効化

```powershell
PS C:\> Disable-CrashEditorFollow
```

`cd` による自動ジャンプが止まります。

### Example 2: タブが増えすぎたので一時停止

```powershell
PS dmp:\threads\12\frames\> Disable-CrashEditorFollow
PS dmp:\threads\12\frames\> 0..50 | % { cd $_ ; Get-Item . }
PS dmp:\threads\12\frames\> Enable-CrashEditorFollow
```

大量のフレームを走査する前に一時的に止めておき、作業が終わったら再度有効化します。エディタのタブ爆発を防ぐ定番パターンです。

### Example 3: スクリプト末尾のクリーンアップで呼ぶ

```powershell
try {
    Enable-CrashEditorFollow
    # ...調査...
}
finally {
    Disable-CrashEditorFollow
}
```

関数やスクリプトの一時的な副作用として有効化している場合は `finally` で確実に解除しておくのが安全です。

## PARAMETERS

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

出力はありません (`-Verbose` 時のみ解除された旨が出力されます)。

## NOTES

- `Enable-CrashEditorFollow` を呼んでいない状態で実行しても副作用はありません (冪等)。
- 他の PowerShell モジュールやユーザースクリプトが `LocationChangedAction` に独自のハンドラを入れている場合、本 cmdlet はそれらも `$null` に戻します。共存する構成では最後に何を設定するかに注意してください。
- セッションを閉じれば設定は破棄されるため、明示的に `Disable-CrashEditorFollow` を呼ばなくても次回起動時には無効状態から始まります。

## RELATED LINKS

- [Enable-CrashEditorFollow]()
- [New-CrashDrive]()
