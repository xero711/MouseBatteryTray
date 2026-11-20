# Third-party notices

The Razer PID, model-name, and transaction-ID reference table used by the managed and native implementations was prepared from the device table in HaloBattery's `providers/razer.py`:

- https://github.com/HeyOkay/HaloBattery/blob/main/providers/razer.py
- https://github.com/HeyOkay/HaloBattery/blob/main/LICENSE

HaloBattery is licensed under the MIT License. The protocol implementations in this project are independent C# and C++ implementations; this notice is retained for the referenced device mapping and protocol documentation.

```text
MIT License

Copyright (c) 2026 Halo Battery contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## SteelSeries mouse provider reference

The SteelSeries mouse model/PID table and protocol mapping in the native implementation were informed by the SteelSeries provider in [BatteryTray](https://github.com/qu1ncky/BatteryTray/blob/main/src/providers/steelseries.rs). Razer PID additions and model-specific response delays were cross-checked against [BatteryTray's Razer provider](https://github.com/qu1ncky/BatteryTray/blob/main/src/providers/razer_peripheral.rs). The Logitech HID++ battery feature order and voltage curve were also cross-checked against [BatteryTray's Logitech provider](https://github.com/qu1ncky/BatteryTray/blob/main/src/providers/logitech.rs) and [Solaar](https://github.com/pwr-Solaar/Solaar). The SteelSeries commands follow the published [rivalcfg](https://github.com/flozz/rivalcfg) documentation. No BatteryTray source file is included in this project.

BatteryTray's SteelSeries provider is licensed under the MIT License:

```text
MIT License

Copyright (c) 2026 qu1ncky

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Pulsar and VAXEE protocol reference

The Pulsar and VAXEE report formats and battery response fields were cross-checked against [mousebatt](https://github.com/ryanlewis/mousebatt/blob/main/src/protocol.rs). This project contains an independent C++ implementation; no mousebatt source file is included.

mousebatt is licensed under the MIT License:

```text
MIT License

Copyright (c) 2026 Ryan Lewis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The VAXEE product ID list is referenced from [mouse-battery-monitor's VAXEE protocol notes](https://github.com/stuffz/mouse-battery-monitor/blob/main/docs/VAXEE.md). This project contains an independent C++ implementation and does not include its source files.

mouse-battery-monitor is licensed under the MIT License:

```text
MIT License

Copyright (c) 2025

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
