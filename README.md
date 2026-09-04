# SimpleAranet4Client

A small .NET MAUI app that talks to an [Aranet4](https://aranet.com/) CO2 sensor over Bluetooth LE.

It is meant as an alternative for when using the official Aranet app is not a viable option for whatever reasons

## What it does

- **Scan and connect** to any nearby device whose name contains "Aranet".
- **Current reading** — CO2, temperature, humidity, air pressure, battery, and how long ago the
  sensor last measured.
- **CO2 history** — reads the values stored on the sensor (up to 5760 of them) for the last hour,
  6 hours, 24 hours or 7 days, and draws them as a line chart
- **CSV export** of the loaded history, via the platform share sheet.
- **Measurement interval** — switch the sensor between 1, 2, 5 and 10 minutes.
- **Smart home integration** — read and toggle the setting that decides whether third-party apps
  may read the sensor at all.

## Requirements

- The sensor's **"Smart home integration"** setting has to be on for some functions to work, can be enabled by this client itself

## About calibration

This app cannot start a CO2 calibration itself currently, however the Device can be calibrated without the use of any App:
When doing a manual calibration the Aranet4 device must be exposed to fresh air (about 420 ppm of CO2) and the environment should be stable (not changing) for 30 minutes while the calibration is done. Maintain a distance of at least 1 meter from the device during the calibration process so the CO2 from your breath wouldn’t impede the calibration. To initiate the manual CO2 calibration change the switch position at the back of the device behind the batteries from MANUAL to AUTO and back to MANUAL (maintain a maximum of 1 second between each movement):

## Credits

The BLE protocol — service and characteristic UUIDs, packet layouts, command bytes — follows
[Anrijs/Aranet4-Python](https://github.com/Anrijs/Aranet4-Python), which reverse engineered it.
Bluetooth access uses [Plugin.BLE](https://github.com/dotnet-bluetooth-le/dotnet-bluetooth-le).

Aranet and Aranet4 are trademarks of SAF Tehnika. This project is not affiliated with or endorsed
by them.

## Licence

MIT, see [LICENSE](LICENSE).

## Disclaimer

Use at your own risk. To the best of my knowledge there is no real risk of bricking the sensor, but
I can give no guarantee, and a sensor can always fail for an unrelated reason at the moment you
happen to be using this app. The app asks you to accept this on first start.
