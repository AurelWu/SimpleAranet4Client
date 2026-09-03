using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Diagnostics;

namespace SimpleAranet4Client.Aranet
{
    /// <summary>
    /// Values from the "current readings, detailed" characteristic.
    /// <paramref name="Co2Ppm"/> is null when the sensor has no valid CO2 value to give, which is
    /// what it reports while it is calibrating. The other values stay usable.
    /// </summary>
    public sealed record Aranet4Reading(
        int? Co2Ppm,
        double TemperatureC,
        double PressureHpa,
        int HumidityPercent,
        int BatteryPercent,
        int IntervalSeconds,
        int AgoSeconds);

    public sealed record Aranet4HistoryPoint(DateTime Timestamp, int Co2Ppm);

    /// <summary>
    /// Minimal Aranet4 BLE client: current CO2, CO2 history and update interval.
    /// Protocol per https://github.com/Anrijs/Aranet4-Python
    /// </summary>
    public sealed class Aranet4Client : IAsyncDisposable
    {
        public const string DeviceNameFilter = "Aranet";

        /// <summary>Intervals the sensor accepts, in minutes.</summary>
        public static readonly int[] SupportedIntervalMinutes = [1, 2, 5, 10];

        // SAF Tehnika service (firmware 1.2.0+) and the legacy service of older firmware.
        static readonly Guid ServiceUuid = Guid.Parse("0000fce0-0000-1000-8000-00805f9b34fb");
        static readonly Guid LegacyServiceUuid = Guid.Parse("f0cd1400-95da-4f4b-9ac8-aa55d312af0c");

        static readonly Guid SensorStateUuid = Guid.Parse("f0cd1401-95da-4f4b-9ac8-aa55d312af0c");
        static readonly Guid CurrentReadingsUuid = Guid.Parse("f0cd3001-95da-4f4b-9ac8-aa55d312af0c");
        static readonly Guid TotalReadingsUuid = Guid.Parse("f0cd2001-95da-4f4b-9ac8-aa55d312af0c");
        static readonly Guid CommandUuid = Guid.Parse("f0cd1402-95da-4f4b-9ac8-aa55d312af0c");
        static readonly Guid HistoryV2Uuid = Guid.Parse("f0cd2005-95da-4f4b-9ac8-aa55d312af0c");

        /// <summary>Most measurements an Aranet4 keeps in memory.</summary>
        public const int MaxStoredMeasurements = 5760;

        // Every command byte the public clients know: 0x61 history v2, 0x82 history v1,
        // 0x90 interval, 0x91 smart home integration, 0x92 Bluetooth range (not used here).
        //
        // Calibration is deliberately absent. No public client - not Aranet4-Python, not Anrijs'
        // ESP32 client - knows a command that starts a CO2 calibration. The official Aranet app can
        // start one, so the sensor accepts some command for it, but as there is a manual option I decided to leave it out for now. Users have to use the hardware switch on
        // the sensor or the official app; MainPage shows them how.
        //
        // Calibration state is readable though, and could be surfaced without any write: the
        // advertisement carries manufacturer data under the SAF Tehnika company id 0x0702 whose
        // first byte holds flags, and bits 2-3 of it are the calibration state
        // (0 not active, 1 end request, 2 in progress, 3 error). The "sensor calibration data"
        // characteristic f0cd1502 is undocumented and returns FF:FF:FF:FF:FF:FF:FF:FF in practice.
        //
        // Vendor procedure: https://help.aranet.com/aranet4/aranet4-home/usage-and-measurements/calibration-and-its-errors
        const byte CmdHistoryV2 = 0x61;
        const byte CmdSetInterval = 0x90;
        const byte CmdSetIntegration = 0x91;
        const byte ParamCo2 = 0x04;

        // Bit 7 of the second settings byte in the sensor state characteristic.
        const byte IntegrationFlag = 0x80;

        // Rather than a measurement, the sensor stores a magic number with the high bit set whenever
        // it has nothing valid to report - during a calibration, for one. Taken at face value those
        // read as 32768 ppm and upwards.
        const int InvalidCo2Flag = 0x8000;

        /// <summary>The raw value, or null when the sensor flagged it as not a real measurement.</summary>
        static int? ParseCo2(ushort raw) => (raw & InvalidCo2Flag) != 0 ? null : raw;

        static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(5);

        // A full log can be thousands of values, so the history read is bounded by lack of progress
        // rather than by a fixed duration.
        static readonly TimeSpan HistoryStallTimeout = TimeSpan.FromSeconds(4);
        static readonly TimeSpan HistoryOverallTimeout = TimeSpan.FromMinutes(2);
        const int MaxHistoryRequests = 8;

        readonly IAdapter _adapter = CrossBluetoothLE.Current.Adapter;

        ICharacteristic? _sensorState;
        ICharacteristic? _currentReadings;
        ICharacteristic? _totalReadings;
        ICharacteristic? _command;
        ICharacteristic? _history;

        public IDevice? Device { get; private set; }

        public string DeviceName => Device?.Name ?? "unknown";

        public bool IsConnected =>
            Device is { State: DeviceState.Connected } &&
            _currentReadings != null && _totalReadings != null &&
            _command != null && _history != null;

        /// <summary>Scan for advertising Aranet devices. Returns them in discovery order.</summary>
        public static async Task<IReadOnlyList<IDevice>> ScanAsync(TimeSpan duration, CancellationToken ct = default)
        {
            var adapter = CrossBluetoothLE.Current.Adapter;
            var found = new List<IDevice>();

            void OnDiscovered(object? sender, DeviceEventArgs e)
            {
                var name = e.Device.Name;
                if (string.IsNullOrWhiteSpace(name)) return;
                if (!name.Contains(DeviceNameFilter, StringComparison.OrdinalIgnoreCase)) return;

                lock (found)
                {
                    if (!found.Any(d => d.Id == e.Device.Id))
                        found.Add(e.Device);
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(duration);

            adapter.DeviceDiscovered += OnDiscovered;
            try
            {
                if (adapter.IsScanning)
                    await adapter.StopScanningForDevicesAsync();

                await adapter.StartScanningForDevicesAsync(cancellationToken: cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected: the scan window elapsed
            }
            finally
            {
                adapter.DeviceDiscovered -= OnDiscovered;
                if (adapter.IsScanning)
                    await adapter.StopScanningForDevicesAsync();
            }

            lock (found)
            {
                return found.ToList();
            }
        }

        public async Task<bool> ConnectAsync(IDevice device, CancellationToken ct = default)
        {
            Device = device;

            try
            {
                if (device.State != DeviceState.Connected)
                    await _adapter.ConnectToDeviceAsync(device, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ConnectAsync failed: {ex.Message}");
                return false;
            }

            return await DiscoverAsync(ct);
        }

        public async Task<Aranet4Reading?> ReadCurrentAsync(CancellationToken ct = default)
        {
            if (!await EnsureConnectedAsync(ct)) return null;

            var data = await ReadAsync(_currentReadings, ct);
            if (data == null || data.Length < 13) return null;

            return new Aranet4Reading(
                Co2Ppm: ParseCo2(BitConverter.ToUInt16(data, 0)),
                TemperatureC: BitConverter.ToUInt16(data, 2) / 20.0,
                PressureHpa: BitConverter.ToUInt16(data, 4) / 10.0,
                HumidityPercent: data[6],
                BatteryPercent: data[7],
                IntervalSeconds: BitConverter.ToUInt16(data, 9),
                AgoSeconds: BitConverter.ToUInt16(data, 11));
        }

        /// <summary>Number of measurements the sensor currently has stored.</summary>
        public async Task<int> ReadTotalReadingsAsync(CancellationToken ct = default)
        {
            if (!await EnsureConnectedAsync(ct)) return 0;

            var data = await ReadAsync(_totalReadings, ct);
            return data is { Length: >= 2 } ? BitConverter.ToUInt16(data, 0) : 0;
        }

        /// <summary>Set the measurement interval. The sensor only accepts <see cref="SupportedIntervalMinutes"/>.</summary>
        public async Task<bool> SetUpdateIntervalAsync(int minutes, CancellationToken ct = default)
        {
            if (!SupportedIntervalMinutes.Contains(minutes))
                throw new ArgumentOutOfRangeException(nameof(minutes), minutes, "Unsupported Aranet4 interval.");

            if (!await EnsureConnectedAsync(ct)) return false;
            if (_command is null || !_command.CanWrite) return false;

            try
            {
                _command.WriteType = CharacteristicWriteType.Default; // write with response
                using var cts = NewTimeout(ct);
                return await _command.WriteAsync([CmdSetInterval, (byte)minutes], cts.Token) == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetUpdateIntervalAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Whether the sensor lets other apps read it ("Smart Home integration").
        /// Returns null when the sensor does not report the setting.
        /// </summary>
        public async Task<bool?> ReadSmartHomeIntegrationAsync(CancellationToken ct = default)
        {
            if (!await EnsureConnectedAsync(ct)) return null;

            var data = await ReadAsync(_sensorState, ct);
            if (data == null || data.Length < 3) return null;

            return (data[2] & IntegrationFlag) != 0;
        }

        /// <summary>
        /// Turn "Smart Home integration" on or off. Turning it off also cuts this app off from the
        /// sensor, and it then has to be re-enabled from the official Aranet app.
        /// </summary>
        public async Task<bool> SetSmartHomeIntegrationAsync(bool enabled, CancellationToken ct = default)
        {
            if (!await EnsureConnectedAsync(ct)) return false;
            if (_command is null || !_command.CanWrite) return false;

            try
            {
                _command.WriteType = CharacteristicWriteType.Default;
                using var cts = NewTimeout(ct);
                return await _command.WriteAsync([CmdSetIntegration, enabled ? (byte)1 : (byte)0], cts.Token) == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetSmartHomeIntegrationAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Read up to <paramref name="maxPoints"/> of the most recent stored CO2 values.
        /// Timestamps come from the live reading (interval + seconds since the last measurement).
        /// </summary>
        public async Task<IReadOnlyList<Aranet4HistoryPoint>> ReadCo2HistoryAsync(
            int maxPoints,
            Aranet4Reading? current = null,
            IProgress<(int Read, int Wanted)>? progress = null,
            CancellationToken ct = default)
        {
            if (!await EnsureConnectedAsync(ct)) return [];
            if (_command is null || !_command.CanWrite || _history is null || !_history.CanRead) return [];

            current ??= await ReadCurrentAsync(ct);
            int intervalSeconds = current?.IntervalSeconds > 0 ? current.IntervalSeconds : 300;
            int agoSeconds = current?.AgoSeconds ?? 0;

            int total = await ReadTotalReadingsAsync(ct);
            if (total <= 0) return [];

            int wanted = Math.Min(maxPoints, total);
            int firstWanted = total - wanted + 1; // 1 based index of the oldest value we want

            // measurement index (1 based) -> ppm, or -1 for a value the sensor flagged as invalid
            var byIndex = new SortedDictionary<int, int>();
            var overallDeadline = DateTime.UtcNow + HistoryOverallTimeout;

            try
            {
                // The sensor streams the requested block in chunks, one per read. If it stops early,
                // ask again starting at the first value we are still missing.
                for (int attempt = 0; attempt < MaxHistoryRequests; attempt++)
                {
                    if (DateTime.UtcNow >= overallDeadline) break;

                    int missing = FirstMissingIndex(byIndex, firstWanted, total);
                    if (missing < 0) break; // got everything

                    if (!await WriteHistoryRequestAsync(missing - 1, ct)) break;

                    var stallDeadline = DateTime.UtcNow + HistoryStallTimeout;

                    while (DateTime.UtcNow < stallDeadline && DateTime.UtcNow < overallDeadline)
                    {
                        ct.ThrowIfCancellationRequested();

                        var packet = await ReadAsync(_history, ct);
                        if (packet == null || packet.Length < 10)
                        {
                            await Task.Delay(50, ct);
                            continue;
                        }

                        // 10 byte header: param, interval, ago, total, start (1 based), count
                        byte param = packet[0];
                        int chunkStart = BitConverter.ToUInt16(packet, 7);
                        int chunkCount = packet[9];

                        if (param != ParamCo2 || chunkCount == 0)
                        {
                            await Task.Delay(50, ct);
                            continue;
                        }

                        int usable = Math.Min(chunkCount, (packet.Length - 10) / 2);
                        int before = byIndex.Count;

                        for (int i = 0; i < usable; i++)
                        {
                            int index = chunkStart + i;
                            if (index >= firstWanted && index <= total)
                            {
                                // Invalid values are kept as -1 rather than skipped: the loop below
                                // asks again for every index it has not seen, and a value the sensor
                                // will never fill in would keep it asking.
                                byIndex[index] = ParseCo2(BitConverter.ToUInt16(packet, 10 + i * 2)) ?? -1;
                            }
                        }

                        if (byIndex.Count > before)
                        {
                            progress?.Report((byIndex.Count, wanted));
                            stallDeadline = DateTime.UtcNow + HistoryStallTimeout;
                        }

                        if (byIndex.Count >= wanted || chunkStart - 1 + chunkCount >= total)
                            break; // all we asked for, or the end of the log
                    }
                }

                // The newest stored measurement was taken agoSeconds ago, older ones step back by one interval each.
                var newest = DateTime.Now.AddSeconds(-agoSeconds);
                return byIndex
                    .Where(kv => kv.Value >= 0) // drop what the sensor flagged as no measurement
                    .Select(kv => new Aranet4HistoryPoint(
                        newest.AddSeconds(-(total - kv.Key) * (double)intervalSeconds),
                        kv.Value))
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReadCo2HistoryAsync failed: {ex.Message}");
                return [];
            }
        }

        /// <summary>Lowest 1 based index in [from..to] that has not been received yet, or -1 when none is missing.</summary>
        static int FirstMissingIndex(SortedDictionary<int, int> received, int from, int to)
        {
            for (int i = from; i <= to; i++)
                if (!received.ContainsKey(i)) return i;

            return -1;
        }

        async Task<bool> WriteHistoryRequestAsync(int startIndex, CancellationToken ct)
        {
            if (_command is null) return false;

            try
            {
                _command.WriteType = CharacteristicWriteType.Default;
                using var cts = NewTimeout(ct);

                byte[] request =
                [
                    CmdHistoryV2,
                    ParamCo2,
                    (byte)(startIndex & 0xFF),
                    (byte)((startIndex >> 8) & 0xFF)
                ];

                return await _command.WriteAsync(request, cts.Token) == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"History request failed: {ex.Message}");
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            _currentReadings = _totalReadings = _command = _history = null;

            if (Device == null) return;

            try { await _adapter.DisconnectDeviceAsync(Device); }
            catch (Exception ex) { Debug.WriteLine($"DisconnectAsync failed: {ex.Message}"); }
        }

        public async ValueTask DisposeAsync() => await DisconnectAsync();

        /// <summary>Aranet closes idle connections aggressively, so reconnect and rediscover on demand.</summary>
        async Task<bool> EnsureConnectedAsync(CancellationToken ct)
        {
            if (IsConnected) return true;
            if (Device == null) return false;

            return await ConnectAsync(Device, ct);
        }

        async Task<bool> DiscoverAsync(CancellationToken ct)
        {
            if (Device == null) return false;

            var services = new List<IService>();
            foreach (var uuid in new[] { ServiceUuid, LegacyServiceUuid })
            {
                try
                {
                    var service = await Device.GetServiceAsync(uuid, ct);
                    if (service != null) services.Add(service);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"GetServiceAsync({uuid}) failed: {ex.Message}");
                }
            }

            if (services.Count == 0)
            {
                Debug.WriteLine("No Aranet service found on device.");
                return false;
            }

            // Optional: older firmware may not expose the state characteristic, so it is not part of IsConnected.
            _sensorState = await FindCharacteristicAsync(services, SensorStateUuid, ct);

            _currentReadings = await FindCharacteristicAsync(services, CurrentReadingsUuid, ct);
            _totalReadings = await FindCharacteristicAsync(services, TotalReadingsUuid, ct);
            _command = await FindCharacteristicAsync(services, CommandUuid, ct);
            _history = await FindCharacteristicAsync(services, HistoryV2Uuid, ct);

            if (!IsConnected)
                Debug.WriteLine("Aranet initialization incomplete: missing characteristics.");

            return IsConnected;
        }

        static async Task<ICharacteristic?> FindCharacteristicAsync(
            IEnumerable<IService> services, Guid uuid, CancellationToken ct)
        {
            foreach (var service in services)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var characteristic = await service.GetCharacteristicAsync(uuid);
                    if (characteristic != null) return characteristic;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"GetCharacteristicAsync({uuid}) failed: {ex.Message}");
                }
            }

            return null;
        }

        static async Task<byte[]?> ReadAsync(ICharacteristic? characteristic, CancellationToken ct)
        {
            if (characteristic == null || !characteristic.CanRead) return null;

            try
            {
                using var cts = NewTimeout(ct);
                var (data, _) = await characteristic.ReadAsync(cts.Token);
                return data;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Read of {characteristic.Id} failed: {ex.Message}");
                return null;
            }
        }

        static CancellationTokenSource NewTimeout(CancellationToken ct)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(IoTimeout);
            return cts;
        }
    }
}
