# 読み出し方式

このアプリは、対象の受信機・HIDコレクションにバッテリー問い合わせだけを送り、応答が合っている場合に残量を表示します。ボタン割り当てやDPIなどの設定は変更しません。

## G502 X PLUS

- 対象: 有線USB `046D:C095` と LIGHTSPEED レシーバー `046D:C547`。両方見つかった場合は充電経路である有線を優先します。
- 方式: Logitech HID++ 2.0。どちらもHID usage page `FF00` の長いレポートコレクション `0002` にだけ問い合わせます。有線は device index `FF`、受信機はslot 1〜6を検索します。受信機のslotエラー確認に限って短い `FF00:0001` コレクションを読み取り専用で開きます。
- バッテリー機能はUnified Battery `0x1004` を優先します。先にcapabilityを問い合わせ、百分率対応フラグがある場合だけ数値を表示します。対応しない場合はBattery Status `0x1000` を読みます。`0x1004` の百分率対応フラグがある場合は0%も有効値です。`0x1000` の0は不明値なので0%として扱いません。
- 応答はレポートID、device index/slot、feature、function、software IDが問い合わせと一致したものだけ受け入れます。有線と受信機のfeature経路キャッシュは分離します。

## LAMZU Maya

- 対象: 有線USB `3554:F50F` とこのPCで見つかった4Kレシーバー `3554:F510`。両方見つかった場合は有線を優先します。
- 方式: HID usage page/usage `FF02:0002` のコレクションで、Compx report-8の読み取り専用バッテリーコマンド `08 04` を送ります。17バイトの応答のヘッダー、チェックサム、残量(0〜100)を確認し、取得元がUSBか受信機かを記録します。
- 対象コレクションが見つからない場合は「未接続」、コレクションはあるが応答しない場合は「スリープ中」と表示します。取得できていない残量は作らず、前回値があれば最大300秒間、灰色の値とスリープ印で表示します。

`3554:F510` はPC上で確認したLAMZU 4KレシーバーのID、`3554:F50F` は有線接続中に確認したマウスのIDです。公開資料では同じIDのレシーバーをCompx系と扱う例がありますが、受信機IDだけでは内部のマウスモデルまでは特定できません。このアプリではユーザー指定に従って表示名を「LAMZU Maya」としています。

## 標準HIDマウス

- 起動時にHIDのGeneric Desktop / Mouse (`0x01:0x02`) を列挙し、WindowsのHID接続・取り外し通知でも再列挙します。
- 同時に表示する数は1〜4台です。対応モデルの専用プロバイダーを優先し、それ以外は標準Battery System usage page `0x85` の Battery Strength usage `0x66` がレポートにある場合だけ読み取ります。
- 標準のBattery Strengthレポートがない機器は一覧に表示しますが、残量は「この機器の残量方式は未対応」とします。マウス入力usageだけでは電池残量を推測しません。
- 標準レポートと専用プロバイダーは約1秒間隔で確認します。これはポーリング方式です。接続・取り外しの検出はWindows通知による即時再走査ですが、残量の変化を機器が自発通知しない限り、完全なイベント駆動にはなりません。

HID Usage Tables ではマウス入力と Battery System が別のusageとして定義されています。Battery System usageは全マウスに必須ではなく、メーカー独自の読み取り手順が必要な場合があります。

## トレイの状態表示

- 残量は緑・橙・赤のゲージで示し、充電中は黄色い稲妻を重ねます。
- スリープ中は最後に取得した値を灰色で示し、青い `Z` 印を付けます。前回値がない場合もスリープ印だけを表示します。
- 正確な0%は空の赤いゲージと `!`、1〜15%は短い赤いゲージで区別します。
- 未接続は斜線、不明・初回読み込みは `?` 印で示します。無応答や未接続をバッテリー切れとして表示しません。

## 参考資料

- [Logitech HID++ 2.0 protocol overview](https://github.com/Logitech/cpg-docs/blob/master/hidpp20/README.rst)
- [HaloBattery protocol notes](https://github.com/HeyOkay/HaloBattery/blob/main/docs/protocols.md) — G502 X PLUS、Logitech battery features、Compx report-8
- [HaloBattery LAMZU provider](https://github.com/HeyOkay/HaloBattery/blob/main/providers/lamzu.py) — `3554:F510` のCompxコレクション識別とreport-8読み出し例
- [HidSharp 2.6.4](https://www.nuget.org/packages/HidSharp/2.6.4)
- [USB-IF HID Usage Tables](https://usb.org/sites/default/files/hut1_5.pdf)
