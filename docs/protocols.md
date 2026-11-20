# HID 検出とバッテリー読み取り

この文書は `native/MouseBatteryTray.cpp` の軽量版について説明します。残量の取得にはバッテリー問い合わせだけを使い、ボタン割り当てやDPIなどのマウス設定は変更しません。

## 検出と更新

- Windows HID の Generic Desktop / Mouse (`0x01:0x02`) コレクションと、既知機種の専用バッテリーコレクションを列挙します。
- 起動時とWindowsのデバイス到着・取り外し通知を受けたときに再列挙します。
- 残量は約1秒ごとに機器ごとのワーカースレッドから並列に問い合わせます。前回の問い合わせが終わっていない機器と、HIDパスを共有する項目だけは重ねず、他の機器の更新は進めます。終了要求は停止イベントで読み取り中のワーカーにも通知し、完了したスレッドを定期的に回収します。ドライバーがI/Oを完了しない場合、終了操作から3秒後にWindowsへプロセス終了を要求しますが、OSの終了処理に時間がかかる可能性があります。機器が値を通知しない限り、残量更新自体はポーリングです。
- 同時表示数は1〜16台から選択できます。WindowsのHID Container IDで同じ物理機器の複数コレクションをまとめ、同型番の別マウスは別項目にします。Container IDが取得できない機器はHIDパスで識別します。
- 日本語/英語、表示数は `%LOCALAPPDATA%\MouseBatteryTray\settings.ini` に保存します。Windows起動時の開始設定は現在ユーザーの `Run` レジストリキーを使います。

## Logitech HID++ 2.0 mice

- Logitech mouse として検出した `046D` HID++ 2.0 interface (`FF00:0002`, 20-byte output report) を対象にします。有線はdevice index `FF`、レシーバー接続はslotごとに残量を読みます。ペアリング済みのslotはReceiver Information (`B5`) のHID++ 1.0 pairing-info subregisterから調べます。Unifying `C52B/C532`とNano `C534`はsubregister `20`〜、Bolt `C548`は `51`〜を読み、mouseとtrackballだけを表示対象にします。レシーバーslotのエラー確認にだけ短い `FF00:0001` コレクションを使います。
- Unified Battery `0x1004` を優先し、 Battery Status `0x1000`、その後に Battery Voltage `0x1001` を試します。電圧は3500〜4500mVの単セルLi-ion範囲だけSolaarの放電カーブで百分率換算します。AA/AAAや複数セルを誤換算しないよう範囲外は採用しません。
- HID++の有効な応答経路をキャッシュして、次回以降の問い合わせを短縮します。
- G502 X PLUS以外のLogitech機種と複数slot構成はこのPCで未検証です。レシーバー側でpairing-infoを公開しない機種では、従来どおりHID++ 2.0で応答したslotを探すフォールバックを使います。HID++ 1.0のみの機種はスロット種別を判別できても、残量取得には未対応です。
- Pairing-info subregister、device-kind値、Bolt独自slot情報は[SolaarのReceiver実装](https://github.com/pwr-Solaar/Solaar/blob/master/lib/logitech_receiver/receiver.py#L246-L276)、[HID++ 1.0定数](https://github.com/pwr-Solaar/Solaar/blob/master/lib/logitech_receiver/hidpp10_constants.py#L152-L196)を参照しました。

## LAMZU Maya

- 対象: USB `3554:F50F` と4Kレシーバー `3554:F510`。
- usage page/usage `FF02:0002` のコレクションにCompx report-8のバッテリーコマンド `08 04` を送ります。17 byte応答のヘッダー、チェックサム、残量(0〜100)を確認します。
- `3554:F510` は開発PCで確認した4KレシーバーのIDです。製品内部のマウスモデルまではレシーバーIDのみで特定しません。

## SteelSeries wireless mice

- VID `1038` の実装済み24 PIDを対象にします。Aerox 3/5/9、Prime/Prime Mini、Rival 3 Wireless/Gen 2、Rival 650 のケーブルまたは2.4GHzレシーバー接続です。モデルごとに指定されたHID interfaceだけを使います。
- Aerox/Prime系は出力レポート `92`（ケーブル）または `D2`（無線）を送り、Windowsが要求するHIDレポート長にゼロ埋めして送信します。入力レポート内のlevelと充電bitを読みます。Aerox 3 Wireless Gen 2はstatusと百分率を個別に確認します。Rival系は `AA 01` 要求を送り、妥当なlevelを含む応答を読みます。
- モデル/PIDと応答形式の参照元は[BatteryTrayのSteelSeries provider](https://github.com/qu1ncky/BatteryTray/blob/main/src/providers/steelseries.rs)と[rivalcfg](https://github.com/flozz/rivalcfg)です。SteelSeriesマウスがこの開発PCにないため実機検証は未完了です。MITライセンスは[第三者ライセンス表示](../THIRD-PARTY-NOTICES.md)に記載しています。

## Pulsar mice

- Pulsar X2 CrazyLightの有線 `3710:3414` と8K receiver `3710:5406` に限定し、`FF02:0002` collectionを使います。17-byte battery command `08 04` をchecksum付きで送り、応答command、0〜100の残量、charging byteを確認します。出力HID reportが17 byteより長い場合はWindowsのreport長に合わせてゼロ埋めします。
- Report formatは [mousebatt](https://github.com/ryanlewis/mousebatt/blob/main/src/protocol.rs)、対応PIDと両接続での実機応答は[HaloBatteryのデバイス報告](https://github.com/HeyOkay/HaloBattery/issues/164)を参照しました。このPCにPulsar実機はないためアプリからの実機検証は未完了です。MITライセンスは[第三者ライセンス表示](../THIRD-PARTY-NOTICES.md)に記載しています。

## VAXEE mice

- VID `3057` のうち、既知のVAXEE receiver PID (`0005`, `1001`, `1002`, `2001`) とmouse PID (`1003`〜`1013`) の `FF05:0001` collectionを対象にします。Feature report ID `0E` で battery command `0B` と charging command `10` を照会し、各応答がcommandをechoした場合だけ採用します。取得は読み取り専用で、polling rateなどの設定は変更しません。Batteryの1 stepを5%として表示します。
- Report formatは [mousebatt](https://github.com/ryanlewis/mousebatt/blob/main/src/protocol.rs)、PID一覧は[mouse-battery-monitorのVAXEE資料](https://github.com/stuffz/mouse-battery-monitor/blob/main/docs/VAXEE.md)を参照しました。VAXEEマウスがこの開発PCにないため実機検証は未完了です。参照実装のMITライセンスは[第三者ライセンス表示](../THIRD-PARTY-NOTICES.md)に記載しています。

## Razer PID support

- Vendor ID `1532` の実装済みPIDだけを専用経路で扱います。有線専用 Basilisk V3 35K (`00CB`) は専用経路に含めていません。Orochi V2 Bluetooth (`0095`) はRazer専用経路と、WindowsがPnPプロパティに残量を公開し、HIDマウスと同じContainer IDで関連付けできる場合のBluetooth経路を試します。
- 追加PIDとして、Orochi V2 receiver (`00E0`)、Basilisk V4 Pro USB/receiver (`00E2`/`00E3`)、Naga V3 Pro USB/receiver (`00E7`/`00E8`)、Basilisk V4 HyperSpeed USB/receiver (`00EB`/`00EA`) を扱います。Naga V3 Pro receiverには初回応答を約100 ms待つ設定を使います。他の追加PIDは約31 msです。
- Windowsのreport-ID byteを含む91 byte Feature reportを使います。残量要求はRazer payload `class 0x07 / command 0x80`、充電状態は `0x07 / 0x84` です。応答はtransaction IDとcommand class/idが要求に一致した場合だけ採用し、battery byteを0〜255から0〜100%に換算します。
- Viper Ultimate (`007A`/`007B`) はtransaction ID `0x3F` を優先します。OpenMouse Projectの実機報告では `0x1F` が無応答で、`0x3F` ならbatteryを含む読み取りに成功しています。OpenRazer由来の従来値 `0xFF` ではなく実機応答を優先します。
- PIDごとの優先transaction IDと応答待ちに加え、代替値を試します。バッテリーを正常に読めたtransaction IDは次回以降に再利用します。読めたことのないIDはスリープ応答だけでは確定せず、誤ったIDを固定しないようにします。Atherisと既存Orochi V2 PID (`0062`/`0094`/`0095`) は約400 ms、Naga V3 Pro receiver (`00E8`) は約100 ms待って初回応答を読みます。
- 代替transaction IDを探すアプリ側の応答期限は1台あたり約1.5秒です。ドライバーがキャンセルに応答しないと実際の処理が延びることがあります。不応答のFeature要求はその機器の別経路へ進み、他の機器の更新は止めません。
- PIDの参照元として [HaloBatteryのRazer provider](https://github.com/HeyOkay/HaloBattery/blob/main/providers/razer.py)、[BatteryTrayのRazer provider](https://github.com/qu1ncky/BatteryTray/blob/main/src/providers/razer_peripheral.rs)、[OpenRazerのマウスドライバー](https://github.com/openrazer/driver/blob/master/driver/razermouse_driver.c)と、[Naga V3 Pro](https://github.com/openrazer/openrazer/pull/2904)、[Basilisk V4 HyperSpeed](https://github.com/openrazer/openrazer/pull/2959)、[Basilisk V4 Pro](https://github.com/openrazer/openrazer/pull/2975)、[Orochi V2 receiver](https://github.com/openrazer/openrazer/issues/2856)の公開資料を参照しました（未マージのPRや未解決のissueを含みます）。Viper Ultimateのtransaction IDは[OpenMouse Projectの実機検証](https://github.com/OpenMouse-Project/mouse-protocol/blob/main/docs/razer-testing.md)に合わせています。Razer実機がこの開発PCに無いため、コードはビルド済みですが実機検証は未完了です。参照ライセンスは[第三者ライセンス表示](../THIRD-PARTY-NOTICES.md)に記載しています。

## 標準HID Battery Strength

- HID Feature report descriptor内に Generic Device Controls / Battery Strength (`0x06:0x20`) usageがある場合に読み取ります。マウスとして認識した各HIDインターフェースは直接調べます。その他の兄弟インターフェースは、Windowsが同じ非null Container IDを付け、VID/PIDも一致し、同じContainer ID内にキーボード機能が見つからない場合に限って関連付けます。コンボレシーバーでキーボードの電池をマウスに誤表示しにくくするためです。
- Overlapped `IOCTL_HID_GET_FEATURE` でFeature report値を取得し、論理値の範囲を0〜100%に換算します。機器の応答待ちは最大約1.5秒です。レポートの無い機器や、Feature reportを公開しない機器では値を表示しません。
- Windowsが別のContainer IDに分けるバッテリー機能、複数の同型HID機器を一つのContainer IDにまとめるレシーバー、Feature reportではなく入力レポートで残量を通知する機器、Bluetooth GATT専用の機器は現在対象外です。複合機器の全インターフェースをWindowsがキーボードとして識別しない場合、別機能の電池レポートを除外できないことがあります。

## Windows Bluetooth battery property

- `System.Devices.BatteryLife` はWindowsが公開する Byte型プロパティ（GUID `49CD1F76-5626-4B17-A4E8-18B4AA1A2213`、PID 10）です。Bluetoothスタック用プロパティ（GUID `104EA319-6EE2-4701-BD47-8DDBF425BBE5`、PID 2）も調べます。どちらもByte型で0〜100の値だけを残量として採用します。
- マウスの再列挙時に、現在存在する `BTHENUM`、`BTHLE`、`BTHLEDEVICE` ノードのうち、HIDマウスと同じContainer IDを持つものを関連付けます。WindowsがPnPプロパティを後から公開した場合も、関連付けたノードの `System.Devices.BatteryLife` またはBluetooth電池プロパティを約1秒ごとに読み直します。
- 同じContainer IDのHIDマウスが複数ある場合、Bluetoothノードは最初に一致した項目に結び付けられます。ノード自体がマウスのスキャン後に追加された場合は、Windowsのデバイス変更通知後の再スキャンで認識します。
- Windows側に値が無い機器、Container IDを共有できない機器、値を公開しない機器は対象外です。Windowsが返す値はキャッシュの場合があり、接続中かやデバイス側で計測された時刻は分かりません。画面ではWindows報告値として表示します。読み取りが途切れて前回値しか残っていない場合は、その旨を表示します。

## 適用範囲と確認上の制限

通常のマウス入力だけで電池残量は分かりません。HID Usage TablesにあるBattery Strength usageをすべてのマウスが公開するわけではなく、無線マウスの多くはメーカー独自方式を使います。そのため、HIDマウスを一覧に検出できても、バッテリー対応機種でないと残量は取得できません。Bluetooth機器も、WindowsがPnPプロパティに残量を公開しない場合や、未実装メーカー独自方式の場合は未対応です。

同じマウスがUSB接続とレシーバー接続の両方で別のHID機器として現れると、2項目になる場合があります。Logitechの実機を使った複数slot検証はまだできていません。

参考:

- [USB-IF HID Usage Tables 1.7](https://usb.org/document-library/hid-usage-tables-17)
- [Microsoft: Battery and charging](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/battery-and-charging)
- [Microsoft: System.Devices.BatteryLife](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-devices-batterylife)
- [Bluetooth Battery Tray property reference](https://github.com/dkamioka/bluetooth-battery-tray)
- [Logitech HID++ 2.0 protocol overview](https://github.com/Logitech/cpg-docs/blob/master/hidpp20/README.rst)
- [HaloBattery protocol notes](https://github.com/HeyOkay/HaloBattery/blob/main/docs/protocols.md)
- [HaloBattery LAMZU provider](https://github.com/HeyOkay/HaloBattery/blob/main/providers/lamzu.py)
