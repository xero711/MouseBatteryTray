# Mouse Battery Tray

Windows の通知領域で、接続中の HID マウスを一覧表示し、対応する機器のバッテリー残量を表示する軽量アプリです。通常の検出は Windows の接続・取り外し通知を使い、残量は約1秒ごとに確認します。

## 機能

- HID マウスを起動時と機器の接続・取り外し時に検出します。
- 同時に表示する HID マウス数を1〜16台から設定できます。機器ごとの設定は `%LOCALAPPDATA%\MouseBatteryTray\settings.ini` に保存します。
- 表示言語は日本語と英語から選べます。
- 「Windows 起動時に開始」を通知領域メニューでオン・オフできます。既定値はオフです。
- 通知領域メニューとパネルで、取得した残量と未取得状態を確認できます。

## バッテリー対応範囲

- Logitech HID++ 2.0対応マウス（G502 X PLUSで確認。他の検出対象モデルはプロトコル上の対応で実機未確認）
- LAMZU Maya の USB とレシーバー接続
- Pulsar X2 CrazyLight (USB / 8K receiver) のCompx battery report と、実装済みPIDのVAXEEマウスのfeature report
- SteelSeries Aerox 3/5/9、Prime/Prime Mini、Rival 3 Wireless/Gen 2、Rival 650 の実装済みPID群
- Razer の wireless PID 群（Naga、Mamba、Lancehead、Atheris、Orochi V2、Pro Click、Viper、DeathAdder、Basilisk、Cobra など）
- マウスのHID Feature reportに標準 Generic Device Controls / Battery Strength usage (`0x06:0x20`) がある機器
- WindowsがBluetoothの電池残量を公開し、マウスと同じContainer IDに関連付けられる機器

一般的な HID マウスも一覧に検出しますが、メーカー独自のバッテリー方式だけを持つ機器では残量を取得できません。Bluetooth機器は、Windowsが電池残量を公開するか、HIDとして公開して上記の読み取り方式に対応する場合に限ります。すべてのマウスから残量を共通方式で取得できる規格はありません。マウス入力レポートだけから残量を推測することもありません。

Razer、SteelSeries、Pulsar、VAXEEの専用プロトコルは公開参照に基づいて実装していますが、このPCに実機がないメーカーは実機確認が未完了です。Windowsが電池を公開しないBluetooth GATT専用機器や、未実装メーカー独自方式には対応できません。Logitech の1台のレシーバーに複数マウスがペアリングされている場合、現在はレシーバー内で応答した最初のスロットを表示します。USB接続とレシーバー接続の両方が個別に見える機種では、同じ物理マウスが2台として表示されることがあります。

## メモリ

このRelease版を約25秒、250 ms間隔で測定したところ、初期化後のピークはWorking Set 2.20 MB、Private Bytes 2.22 MBでした。初回HID列挙中だけ全体Working Setが一時的に11.23 MBまで上がり、列挙直後のトリム後は2.20 MB以下でした。Private Bytesは全計測を通じて2.22 MB以下です。実行ファイルは210 KiBです。Windowsの共有コードページを含むWorking SetはOSや起動状況で変わります。

## ビルド

Windows x64 と Visual Studio の C++ デスクトップ開発ツールが必要です。Developer PowerShell または Developer Command Prompt から実行してください。

```powershell
msbuild .\native\MouseBatteryTrayNative.vcxproj /p:Configuration=Release /p:Platform=x64
```

実行ファイルは `native\out\Release\MouseBatteryTray.exe` に作成されます。`MouseBatteryTray` フォルダーには旧 .NET / Windows Forms 実装も残しています。軽量版をビルドする場合は `native` のプロジェクトを使用してください。

機種別プロトコルと確認状況は [docs/protocols.md](docs/protocols.md) を、Razerの参照実装に関するライセンス表示は [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。
