# Mouse Battery Tray

Windows の通知領域で、接続中のマウスを検出してバッテリー残量を表示するアプリです。

## 機能

- HID のマウスコレクションを起動時と機器の接続・取り外し時に再検出します。
- バッテリー値を取得できる機器は約1秒間隔で更新します。接続・取り外しは Windows のデバイス通知を受けて再走査します。
- 同時に表示するマウス数を1〜4台から選べます。設定は `%LOCALAPPDATA%\MouseBatteryTray\settings.json` に保存します。
- 表示言語は日本語と英語を選択できます。
- 「Windows 起動時に開始」は通知領域メニューからオン・オフできます。既定値はオフです。
- 15%以下は赤く表示して通知し、取得できない値を0%として扱いません。

## 対応範囲

現在、専用バッテリー読み取りに対応する機種は次のとおりです。

- Logitech G502 X PLUS (USB `046D:C095`、LIGHTSPEED レシーバー `046D:C547`)
- LAMZU Maya (USB `3554:F50F`、4Kレシーバー `3554:F510`)
- Razer の対応済み wireless PID 群（Naga、Mamba、Ouroboros、Lancehead、Atheris、Pro Click、Viper、DeathAdder、Basilisk、Cobra Pro など）
- HID Battery System の Battery Strength レポートを公開するマウス

Razerの対応PIDは有線専用モデルを除く、既知の電池対応マウスと受信機です。Razerマウス実機がこのPCに無いため、Razerプロトコルの動作はまだ実機確認できていません。アプリは一般的な HID マウスを検出しますが、残量が分かる共通規格を公開しない機種では「この機器の残量方式は未対応」と表示します。無線マウスの多くはメーカー独自プロトコルを使うため、現時点で「すべてのマウスの残量に対応」とは保証できません。未対応機種を追加するには、メーカー・型番・接続方式ごとの読み取り実装が必要です。規格上も HID Battery System は専用の任意コレクションで、通常のマウス入力だけから残量を得られるわけではありません。

## 表示と更新

各マウスのトレイアイコンを左クリックすると、検出した機器の一覧を開きます。右クリックで更新、検出台数、言語、Windows 起動時の開始を設定できます。残量の通知がない機種では、表示更新の間隔を短くしても残量の取得速度は上がりません。

## メモリ

GC 設定を軽量寄りにし、同じ状態のアイコンを毎回描き直さないようにしています。ただし .NET 10 と Windows Forms のランタイムを使うため、プロセス全体のメモリ使用量10MB未満は達成・保証できていません。既存実行ファイルのアイドル時にも10MBを大きく超える使用量が見られました。10MB未満を必須にする場合、Windows Forms/.NET ランタイムを使わないネイティブ実装への移行が必要です。

## ビルド

Windows x64 と .NET 10 SDK が必要です。実行には .NET 10 Desktop Runtime が必要です。

```powershell
dotnet publish .\MouseBatteryTray\MouseBatteryTray.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\dist
```

自己完結版は `--self-contained true -p:IncludeNativeLibrariesForSelfExtract=true` を指定してください。

HID 通信には HidSharp 2.6.4 を使用します。機種別プロトコルと確認資料は [docs/protocols.md](docs/protocols.md) を参照してください。
