# SimpleAranet4Client

A small .NET MAUI app that talks to an [Aranet4](https://aranet.com/) CO2 sensor over Bluetooth LE.

It is meant as an alternative for when using the official Aranet app is not a viable option — not as
a replacement for it. Some things only the official app can do, calibration among them.

## What it does

- **Scan and connect** to any nearby device whose name contains "Aranet".
- **Current reading** — CO2, temperature, humidity, air pressure, battery, and how long ago the
  sensor last measured.
- **CO2 history** — reads the values stored on the sensor (up to 5760 of them) for the last hour,
  6 hours, 24 hours or 7 days, and draws them as a line chart with 1000 / 1400 ppm bands.
- **CSV export** of the loaded history, via the platform share sheet.
- **Measurement interval** — switch the sensor between 1, 2, 5 and 10 minutes.
- **Smart home integration** — read and toggle the setting that decides whether third-party apps
  may read the sensor at all.
- **Calibration help** — the vendor's procedure for calibrating the sensor, which this app cannot
  trigger itself (see below).

## Requirements

- The sensor's **"Smart home integration"** setting has to be on, otherwise the sensor refuses to
  talk to anything but the official app. It can be turned on from the official app, and from this
  one — but turning it *off* here also locks this app out, and only the official app can undo that.
- .NET 10 SDK with the MAUI workload (`dotnet workload install maui`).
- Bluetooth LE hardware, plus the platform's Bluetooth permissions granted at runtime.

## Building

Windows is the target this has actually been built and run against:

```
dotnet build SimpleAranet4Client/SimpleAranet4Client.csproj -f net10.0-windows10.0.19041.0
```

The project also targets `net10.0-android`, `net10.0-ios` and `net10.0-maccatalyst`. Those build
from the same source but have not been exercised — expect rough edges, in particular around the
top of the page, where the Shell navigation bar is hidden and Android's edge-to-edge layout may
need a safe-area padding.

## About calibration

This app cannot start a CO2 calibration. The publicly known Bluetooth commands the sensor accepts
are only the measurement interval, smart home integration and Bluetooth range; the command the
official app uses to calibrate has never been reverse engineered. The in-app "How to calibrate the
sensor" button passes on the vendor's own procedure instead: put the sensor in fresh air and flick
the first switch behind the batteries MANUAL → AUTO → MANUAL. See
[Aranet's calibration page](https://help.aranet.com/aranet4/aranet4-home/usage-and-measurements/calibration-and-its-errors).

While the sensor is calibrating it stores magic numbers instead of measurements. The client
recognises those (high bit set) and reports no value rather than a nonsensical one.

## Credits

The BLE protocol — service and characteristic UUIDs, packet layouts, command bytes — follows
[Anrijs/Aranet4-Python](https://github.com/Anrijs/Aranet4-Python), which reverse engineered it.
Bluetooth access uses [Plugin.BLE](https://github.com/dotnet-bluetooth-le/dotnet-bluetooth-le).

Aranet and Aranet4 are trademarks of SAF Tehnika. This project is not affiliated with or endorsed
by them.

## Disclaimer

Use at your own risk. To the best of my knowledge there is no real risk of bricking the sensor, but
I can give no guarantee, and a sensor can always fail for an unrelated reason at the moment you
happen to be using this app. The app asks you to accept this on first start.
