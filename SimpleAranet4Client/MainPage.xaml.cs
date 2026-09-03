using Plugin.BLE.Abstractions.Contracts;
using SimpleAranet4Client.Aranet;
// Aliased rather than importing the whole namespace, which would make Path ambiguous with System.IO.Path.
using RoundRectangle = Microsoft.Maui.Controls.Shapes.RoundRectangle;
using SimpleAranet4Client.Bluetooth;
using System.Globalization;
using System.Text;

namespace SimpleAranet4Client
{
    public partial class MainPage : ContentPage
    {
        static readonly TimeSpan ScanDuration = TimeSpan.FromSeconds(10);

        static readonly (string Label, TimeSpan Span)[] HistoryRanges =
        [
            ("Last hour", TimeSpan.FromHours(1)),
            ("Last 6 hours", TimeSpan.FromHours(6)),
            ("Last 24 hours", TimeSpan.FromHours(24)),
            ("Last 7 days", TimeSpan.FromDays(7)),
        ];

        // The sensor has no known Bluetooth command for this - see the comment in Aranet4Client -
        // so all we can do is pass on the procedure from
        // https://help.aranet.com/aranet4/aranet4-home/usage-and-measurements/calibration-and-its-errors
        // One line per step: the dialog wraps the text itself, so hard breaks here would show up as
        // stray ones in the middle of a step.
        const string CalibrationHelp =
            """
            Calibrating from this app is not supported.

            Start it on the sensor itself:

            1. Put the sensor in fresh air, about 420 ppm - outdoors, away from people.
            2. Open the battery cover on the back.
            3. With the SIM tool, flick the 1st switch MANUAL - AUTO - MANUAL, leaving at most 1 second between each movement.
            4. Keep the surroundings unchanged and stay at least 1 m away for 30 minutes.

            The sensor shows the progress on its own display. The official Aranet Home app can start a calibration as well.
            """;

        readonly Aranet4Client _client = new();
        readonly Co2HistoryDrawable _chart = new();

        Aranet4Reading? _lastReading;
        IReadOnlyList<Aranet4HistoryPoint> _history = [];
        bool _busy;
        bool _settingSwitchFromSensor;
        bool _disclaimerHandled;
        double _lastPanX;

        public MainPage()
        {
            InitializeComponent();

            HistoryChart.Drawable = _chart;

            foreach (var (label, _) in HistoryRanges)
                HistoryRangePicker.Items.Add(label);
            HistoryRangePicker.SelectedIndex = 1;

            foreach (int minutes in Aranet4Client.SupportedIntervalMinutes)
                IntervalPicker.Items.Add(minutes == 1 ? "1 minute" : $"{minutes} minutes");
            IntervalPicker.SelectedIndex = 0;

            BuildLegend();
        }

        /// <summary>One swatch and caption per GO IAQS band, from <see cref="GoAqsScale"/>.</summary>
        void BuildLegend()
        {
            foreach (var level in new[] { GoAqsLevel.Good, GoAqsLevel.Moderate, GoAqsLevel.Unhealthy })
            {
                var swatch = new Border
                {
                    BackgroundColor = GoAqsScale.ColorFor(level),
                    Stroke = Colors.Transparent,
                    WidthRequest = 12,
                    HeightRequest = 12,
                    VerticalOptions = LayoutOptions.Center,
                    StrokeShape = new RoundRectangle { CornerRadius = 3 }
                };

                var caption = new Label
                {
                    Text = $"{GoAqsScale.TitleFor(level)}  {GoAqsScale.RangeFor(level)}",
                    FontSize = 12,
                    TextColor = Colors.Gray,
                    VerticalOptions = LayoutOptions.Center
                };

                LegendLayout.Children.Add(new HorizontalStackLayout
                {
                    Spacing = 5,
                    Margin = new Thickness(0, 2, 16, 2),
                    Children = { swatch, caption }
                });
            }
        }

        /// <summary>
        /// First run: make the user accept the disclaimer before anything can touch the sensor.
        /// Dismissing the modal brings us back here, hence the guard.
        /// </summary>
        protected override async void OnAppearing()
        {
            base.OnAppearing();

            if (_disclaimerHandled || DisclaimerPage.Accepted) return;
            _disclaimerHandled = true;

            await Navigation.PushModalAsync(new DisclaimerPage(), animated: false);
        }

        async void OnConnectClicked(object? sender, EventArgs e)
        {
            await RunAsync(async () =>
            {
                if (!await BlePermissions.RequestAsync())
                {
                    StatusLabel.Text = "Bluetooth permission denied.";
                    return;
                }

                if (!BlePermissions.IsBluetoothOn)
                {
                    StatusLabel.Text = "Bluetooth is off. Turn it on and try again.";
                    return;
                }

                StatusLabel.Text = "Scanning...";
                var devices = await Aranet4Client.ScanAsync(ScanDuration);

                if (devices.Count == 0)
                {
                    StatusLabel.Text = "No Aranet found. Make sure the sensor is in range and not connected elsewhere.";
                    return;
                }

                IDevice device = devices[0];
                if (devices.Count > 1)
                {
                    string[] names = devices.Select(d => d.Name).ToArray();
                    string? choice = await DisplayActionSheetAsync("Select sensor", "Cancel", null, names);
                    if (choice is null or "Cancel") { StatusLabel.Text = "Cancelled."; return; }
                    device = devices[Array.IndexOf(names, choice)];
                }

                StatusLabel.Text = $"Connecting to {device.Name}...";
                if (!await _client.ConnectAsync(device))
                {
                    StatusLabel.Text = $"Could not connect to {device.Name}.";
                    return;
                }

                StatusLabel.Text = $"Connected to {_client.DeviceName}";
                SetSensorControlsEnabled(true);

                await ReadCurrentAsync();
            });
        }

        async void OnRefreshClicked(object? sender, EventArgs e) => await RunAsync(ReadCurrentAsync);

        async void OnLoadHistoryClicked(object? sender, EventArgs e)
        {
            await RunAsync(async () =>
            {
                var reading = _lastReading ?? await _client.ReadCurrentAsync();
                if (reading == null)
                {
                    StatusLabel.Text = "Could not read from the sensor.";
                    return;
                }

                _lastReading = reading;

                int index = Math.Max(0, HistoryRangePicker.SelectedIndex);
                var span = HistoryRanges[index].Span;

                // The sensor cannot hold more than its log, whatever the selected range asks for.
                int points = Math.Min(
                    Aranet4Client.MaxStoredMeasurements,
                    (int)(span.TotalSeconds / reading.IntervalSeconds));

                var progress = new Progress<(int Read, int Wanted)>(p =>
                    HistorySummaryLabel.Text = $"Reading {p.Read} of {p.Wanted} values...");

                HistorySummaryLabel.Text = $"Reading up to {points} values...";
                var history = await _client.ReadCo2HistoryAsync(points, reading, progress);

                _history = history;
                _chart.Points = history; // resets the zoom window to the whole series
                RedrawChart();
                ExportButton.IsEnabled = history.Count > 0;

                HistorySummaryLabel.Text = history.Count == 0
                    ? "No history returned."
                    : $"{history.Count} values, {history[0].Timestamp:dd.MM. HH:mm} - {history[^1].Timestamp:dd.MM. HH:mm}   " +
                      $"min {history.Min(p => p.Co2Ppm)} / avg {history.Average(p => p.Co2Ppm):0} / max {history.Max(p => p.Co2Ppm)} ppm";
            });
        }

        // Chart navigation. No BLE work, so these stay outside RunAsync and keep working while a
        // sensor read is in flight.

        void OnChartPinch(object? sender, PinchGestureUpdatedEventArgs e)
        {
            if (e.Status != GestureStatus.Running || e.Scale <= 0) return;

            // Scale above 1 means fingers spreading, which should show fewer points.
            _chart.ZoomAt(e.ScaleOrigin.X, 1 / e.Scale);
            RedrawChart();
        }

        void OnChartPan(object? sender, PanUpdatedEventArgs e)
        {
            // TotalX is measured from the start of the gesture, so pan by the change since last time.
            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    _lastPanX = 0;
                    return;

                case GestureStatus.Running:
                    double width = HistoryChart.Width;
                    if (width <= 0) return;

                    // Dragging right should reveal earlier values, so the window moves back.
                    _chart.PanBy(-(e.TotalX - _lastPanX) / width);
                    _lastPanX = e.TotalX;
                    RedrawChart();
                    return;
            }
        }

        void OnChartDoubleTapped(object? sender, TappedEventArgs e)
        {
            _chart.Reset();
            RedrawChart();
        }

        void OnZoomInClicked(object? sender, EventArgs e)
        {
            _chart.ZoomAt(0.5, 0.5);
            RedrawChart();
        }

        void OnZoomOutClicked(object? sender, EventArgs e)
        {
            _chart.ZoomAt(0.5, 2);
            RedrawChart();
        }

        void OnZoomResetClicked(object? sender, EventArgs e)
        {
            _chart.Reset();
            RedrawChart();
        }

        void RedrawChart()
        {
            HistoryChart.Invalidate();

            bool hasData = _history.Count > 1;
            ZoomInButton.IsEnabled = hasData;
            ZoomOutButton.IsEnabled = hasData && _chart.CanZoomOut;
            ZoomResetButton.IsEnabled = hasData && _chart.CanZoomOut;

            if (!hasData)
            {
                ZoomLabel.Text = string.Empty;
                return;
            }

            int shown = (int)Math.Round(_chart.VisibleCount);
            ZoomLabel.Text = shown >= _history.Count
                ? $"all {_history.Count} values"
                : $"{shown} of {_history.Count} values";
        }

        async void OnExportClicked(object? sender, EventArgs e)
        {
            if (_history.Count == 0) return;

            await RunAsync(async () =>
            {
                var csv = new StringBuilder("timestamp,co2_ppm\n");
                foreach (var point in _history)
                {
                    csv.Append(point.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                       .Append(',')
                       .Append(point.Co2Ppm.ToString(CultureInfo.InvariantCulture))
                       .Append('\n');
                }

                string fileName = $"{Sanitize(_client.DeviceName)}-co2-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
                string path = Path.Combine(FileSystem.CacheDirectory, fileName);
                await File.WriteAllTextAsync(path, csv.ToString());

                try
                {
                    await Share.Default.RequestAsync(new ShareFileRequest
                    {
                        Title = "Aranet4 CO2 history",
                        File = new ShareFile(path)
                    });

                    StatusLabel.Text = $"Exported {_history.Count} values as {fileName}";
                }
                catch (Exception ex)
                {
                    // Sharing is not available everywhere (unpackaged Windows, for example) - the file is written either way.
                    System.Diagnostics.Debug.WriteLine($"Share failed: {ex.Message}");
                    StatusLabel.Text = $"Saved {_history.Count} values to {path}";
                }
            });
        }

        static string Sanitize(string name)
        {
            var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            return string.IsNullOrEmpty(cleaned) ? "aranet4" : cleaned;
        }

        async void OnApplyIntervalClicked(object? sender, EventArgs e)
        {
            await RunAsync(async () =>
            {
                int minutes = Aranet4Client.SupportedIntervalMinutes[Math.Max(0, IntervalPicker.SelectedIndex)];

                IntervalLabel.Text = $"Setting interval to {minutes} min...";
                bool ok = await _client.SetUpdateIntervalAsync(minutes);

                if (!ok)
                {
                    IntervalLabel.Text = "Could not change the interval.";
                    return;
                }

                // Read it back so the label shows what the sensor actually uses.
                await ReadCurrentAsync();
            });
        }

        async void OnIntegrationToggled(object? sender, ToggledEventArgs e)
        {
            if (_settingSwitchFromSensor) return;

            bool enable = e.Value;

            if (!enable)
            {
                bool confirmed = await DisplayAlertAsync(
                    "Disable smart home integration?",
                    "Other apps - including this one - can then no longer read the sensor. " +
                    "You will have to enable it again from the official Aranet app.",
                    "Disable",
                    "Cancel");

                if (!confirmed)
                {
                    SetIntegrationSwitch(true);
                    return;
                }
            }

            await RunAsync(async () =>
            {
                IntegrationLabel.Text = enable ? "Enabling..." : "Disabling...";

                if (!await _client.SetSmartHomeIntegrationAsync(enable))
                {
                    IntegrationLabel.Text = "Could not change the setting.";
                    SetIntegrationSwitch(!enable);
                    return;
                }

                if (enable)
                {
                    await RefreshIntegrationAsync();
                    return;
                }

                // Reading the setting back is pointless now: the sensor no longer answers us.
                SetIntegrationSwitch(false);
                IntegrationLabel.Text = "Off - re-enable it in the Aranet app to use this sensor again.";
            });
        }

        async Task RefreshIntegrationAsync()
        {
            bool? enabled = await _client.ReadSmartHomeIntegrationAsync();

            if (enabled == null)
            {
                IntegrationLabel.Text = "This sensor does not report the setting.";
                return;
            }

            SetIntegrationSwitch(enabled.Value);
            IntegrationLabel.Text = enabled.Value
                ? "On - other apps can read this sensor."
                : "Off - other apps cannot read this sensor.";
        }

        /// <summary>Sets the switch to the value the sensor reports, without triggering a write back.</summary>
        void SetIntegrationSwitch(bool value)
        {
            _settingSwitchFromSensor = true;
            IntegrationSwitch.IsToggled = value;
            _settingSwitchFromSensor = false;
        }

        // No BLE work, so this deliberately stays outside RunAsync and outside SetSensorControlsEnabled:
        // it works with no sensor connected and while another operation is running.
        async void OnCalibrationHelpClicked(object? sender, EventArgs e) =>
            await DisplayAlertAsync("Calibrate the CO2 sensor", CalibrationHelp, "OK");

        async Task ReadCurrentAsync()
        {
            var reading = await _client.ReadCurrentAsync();
            if (reading == null)
            {
                StatusLabel.Text = "Could not read from the sensor.";
                return;
            }

            _lastReading = reading;

            Co2Label.Text = reading.Co2Ppm?.ToString() ?? "--";
            DetailsLabel.Text =
                // No CO2 value means the sensor is busy with something else, usually a calibration.
                (reading.Co2Ppm == null ? "No CO2 reading right now - the sensor may be calibrating.\n" : "") +
                $"{reading.TemperatureC:0.0} C   {reading.HumidityPercent} %   " +
                $"{reading.PressureHpa:0.0} hPa   battery {reading.BatteryPercent} %   " +
                $"measured {reading.AgoSeconds} s ago";

            int minutes = Math.Max(1, reading.IntervalSeconds / 60);
            IntervalLabel.Text = $"Sensor measures every {minutes} min.";

            int index = Array.IndexOf(Aranet4Client.SupportedIntervalMinutes, minutes);
            if (index >= 0)
                IntervalPicker.SelectedIndex = index;

            StatusLabel.Text = $"Connected to {_client.DeviceName}";

            await RefreshIntegrationAsync();
        }

        void SetSensorControlsEnabled(bool enabled)
        {
            RefreshButton.IsEnabled = enabled;
            HistoryButton.IsEnabled = enabled;
            ApplyIntervalButton.IsEnabled = enabled;
            IntegrationSwitch.IsEnabled = enabled;
        }

        /// <summary>Runs one sensor operation at a time and keeps the UI from firing overlapping BLE calls.</summary>
        async Task RunAsync(Func<Task> action)
        {
            if (_busy) return;
            _busy = true;

            bool sensorControlsWereEnabled = RefreshButton.IsEnabled;
            ConnectButton.IsEnabled = false;
            SetSensorControlsEnabled(false);

            try
            {
                await action();
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Error: {ex.Message}";
            }
            finally
            {
                ConnectButton.IsEnabled = true;
                SetSensorControlsEnabled(sensorControlsWereEnabled || _client.IsConnected);
                _busy = false;
            }
        }
    }
}
