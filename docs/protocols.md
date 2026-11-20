# HID 検出とバッテリー読み取り

この文書は `native/MouseBatteryTray.cpp` の軽量版について説明します。残量の取得にはバッテリー問い合わせだけを使い、ボタン割り当てやDPIなどのマウス設定は変更しません。

## 検出と更新

- Windows HID の Generic Desktop / Mouse (`0x01:0x02`) コレクションと、既知機種の専用バッテリーコレクションを列挙します。
- 起動時とWindowsのデバイス到着・取り外し通知を受けたときに再列挙します。
- 残量は約1秒ごとに別スレッドから問い合わせます。機器が値を通知しない限り、残量更新自体はポーリングです。
- 同時表示数は1〜4台です。WindowsのHID Container IDで同じ物理機器の複数コレクションをまとめ、同型番の別マウスは別項目にします。Container IDが取得できない機器はHIDパスで識別します。
- 日本語/英語、表示数は `%LOCALAPPDATA%\MouseBatteryTray\settings.ini` に保存します。Windows起動時の開始設定は現在ユーザーの `Run` レジストリキーを使います。

## Logitech G502 X PLUS

- 対象: USB `046D:C095` と LIGHTSPEED レシーバー `046D:C547`。
- HID++ 2.0。usage page `FF00` の長い `0002` レポートコレクションに要求し、有線はdevice index `FF`、レシーバーはslot 1〜6を確認します。レシーバーslotのエラー確認にだけ短い `FF00:0001` コレクションを開きます。
- Unified Battery `0x1004` を優先し、百分率対応フラグが無い場合は Battery Status `0x1000` を試します。0%を不明値として扱うかどうかは使用するfeatureに従います。
- HID++の有効な応答経路をキャッシュして、次回以降の問い合わせを短縮します。
- 1台のレシーバー内で複数slotが応答する場合、現状は最初のバッテリー応答slotだけを表示します。

## LAMZU Maya

- 対象: USB `3554:F50F` と4Kレシーバー `3554:F510`。
- usage page/usage `FF02:0002` のコレクションにCompx report-8のバッテリーコマンド `08 04` を送ります。17 byte応答のヘッダー、チェックサム、残量(0〜100)を確認します。
- `3554:F510` は開発PCで確認した4KレシーバーのIDです。製品内部のマウスモデルまではレシーバーIDのみで特定しません。

## Razer wireless mice

- Vendor ID `1532` の実装済みPIDだけを専用経路で扱います。有線専用 Basilisk V3 35K (`00CB`) と Orochi V2 Bluetooth (`0095`) は含めていません。このアプリはWindowsのBluetooth電池残量APIもまだ使わないため、Orochi V2をBluetooth接続した場合は残量を表示しません。
- Windowsのreport-ID byteを含む91 byte Feature reportを使います。残量要求はRazer payload `class 0x07 / command 0x80`、充電状態は `0x07 / 0x84` です。応答はtransaction IDとcommand class/idが要求に一致した場合だけ採用し、battery byteを0〜255から0〜100%に換算します。
- PIDごとの優先transaction IDに加え、代替値を試します。成功したtransaction IDは次回以降に再利用します。
- PIDとtransaction IDの参照元は [HaloBatteryのRazer provider](https://github.com/HeyOkay/HaloBattery/blob/main/providers/razer.py)、対応モデルの確認には[OpenRazerのマウスドライバー](https://github.com/openrazer/driver/blob/master/driver/razermouse_driver.c)を使いました。Razer実機がこの開発PCに無いため、コードはビルド済みですが実機検証は未完了です。参照ライセンスは[第三者ライセンス表示](../THIRD-PARTY-NOTICES.md)に記載しています。

## 標準HID Battery Strength

- HID Feature report descriptor内に Generic Device Controls / Battery Strength (`0x06:0x20`) usageがある場合に読み取ります。マウスとして認識した各HIDインターフェースは直接調べます。その他の兄弟インターフェースは、Windowsが同じ非null Container IDを付け、VID/PIDも一致し、同じContainer ID内にキーボード機能が見つからない場合に限って関連付けます。コンボレシーバーでキーボードの電池をマウスに誤表示しにくくするためです。
- HIDPでFeature report値を取得し、論理値の範囲を0〜100%に換算します。レポートの無い機器や、Feature reportを公開しない機器では値を表示しません。
- Windowsが別のContainer IDに分けるバッテリー機能、複数の同型HID機器を一つのContainer IDにまとめるレシーバー、Feature reportではなく入力レポートで残量を通知する機器、Bluetooth GATT専用の機器は現在対象外です。複合機器の全インターフェースをWindowsがキーボードとして識別しない場合、別機能の電池レポートを除外できないことがあります。

## 適用範囲と確認上の制限

通常のマウス入力だけで電池残量は分かりません。HID Usage TablesにあるBattery Strength usageをすべてのマウスが公開するわけではなく、無線マウスの多くはメーカー独自方式を使います。そのため、HIDマウスを一覧に検出できても、バッテリー対応機種でないと残量は取得できません。Bluetooth GATT専用の機器や、未実装メーカー独自方式も未対応です。

同じマウスがUSB接続とレシーバー接続の両方で別のHID機器として現れると、2項目になる場合があります。Logitechのレシーバーで複数slotを使う場合も、今の版では1台分しか表示できません。

参考:

- [USB-IF HID Usage Tables 1.7](https://usb.org/document-library/hid-usage-tables-17)
- [Microsoft: Battery and charging](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/battery-and-charging)
- [Logitech HID++ 2.0 protocol overview](https://github.com/Logitech/cpg-docs/blob/master/hidpp20/README.rst)
- [HaloBattery protocol notes](https://github.com/HeyOkay/HaloBattery/blob/main/docs/protocols.md)
- [HaloBattery LAMZU provider](https://github.com/HeyOkay/HaloBattery/blob/main/providers/lamzu.py)
