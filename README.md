# Mouse Battery Tray

Windows の通知領域で、接続中の HID マウスを一覧表示し、対応する機器のバッテリー残量を表示する軽量アプリです。通常の検出は Windows の接続・取り外し通知を使い、残量は約1秒ごとに確認します。

## 機能

- HID マウスを起動時と機器の接続・取り外し時に検出します。
- 同時に表示する HID 機器数を1〜4台から設定できます。機器ごとの設定は `%LOCALAPPDATA%\MouseBatteryTray\settings.ini` に保存します。
- 表示言語は日本語と英語から選べます。
- 「Windows 起動時に開始」を通知領域メニューでオン・オフできます。既定値はオフです。
- 通知領域メニューとパネルで、取得した残量と未取得状態を確認できます。

## バッテリー対応範囲

- Logitech G502 X PLUS の USB と LIGHTSPEED HID++ 接続
- LAMZU Maya の USB とレシーバー接続
- Razer の実装済み wireless PID 群（Naga、Mamba、Lancehead、Atheris、Pro Click、Viper、DeathAdder、Basilisk、Cobra Pro など）
- マウスのHID Feature reportに標準 Generic Device Controls / Battery Strength usage (`0x06:0x20`) がある機器

一般的な HID マウスも一覧に検出しますが、メーカー独自のバッテリー方式だけを持つ機器では残量を取得できません。Bluetooth 機器も Windows が HID として公開し、上記のいずれかの読み取り方式に対応している場合に限ります。すべてのマウスから残量を共通方式で取得できる規格はありません。マウス入力レポートだけから残量を推測することもありません。

Razer の各プロトコルはソース参照に基づいて実装していますが、このPCにRazerマウスが無いため実機での動作確認は未完了です。Logitech の1台のレシーバーに複数マウスがペアリングされている場合、現在はレシーバー内で応答した最初のスロットを表示します。USB接続とレシーバー接続の両方が個別に見える機種では、同じ物理マウスが2台として表示されることがあります。

## メモリ

Release版をこのPCで約8秒起動して測定した値は、Private Bytes 2.18 MB、Private Working Set 1.61 MB、共有ページを含むWorking Set 11.87 MBでした。実行ファイルは約182 KiBです。アプリ自身が専有するメモリは10 MB未満ですが、Working Set全体では10 MBを超えます。この短時間の測定は最大負荷の保証ではなく、Windowsの計測方法により表示値は変わります。

## ビルド

Windows x64 と Visual Studio の C++ デスクトップ開発ツールが必要です。Developer PowerShell または Developer Command Prompt から実行してください。

```powershell
msbuild .\native\MouseBatteryTrayNative.vcxproj /p:Configuration=Release /p:Platform=x64
```

実行ファイルは `native\out\Release\MouseBatteryTray.exe` に作成されます。`MouseBatteryTray` フォルダーには旧 .NET / Windows Forms 実装も残しています。軽量版をビルドする場合は `native` のプロジェクトを使用してください。

機種別プロトコルと確認状況は [docs/protocols.md](docs/protocols.md) を、Razerの参照実装に関するライセンス表示は [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。
