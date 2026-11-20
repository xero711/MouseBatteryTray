using Microsoft.Win32;
using System.Runtime.InteropServices;
using MouseBatteryTray.Hardware;
using MouseBatteryTray.Tray;

namespace MouseBatteryTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string StartupKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "MouseBatteryTray";
    private const int DeviceChangeMessage = 0x0219;
    private const int DeviceNodesChanged = 0x0007;
    private const int DeviceArrival = 0x8000;
    private const int DeviceRemovalComplete = 0x8004;
    private const int DeviceInterface = 5;
    private const int DeviceNotifyWindowHandle = 0;
    private static readonly Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private readonly Dictionary<string, BatteryReading> _readings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NotifyIcon> _trayIcons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Icon> _ownedIcons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolStripMenuItem> _statusItems = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _countItem;
    private readonly ToolStripMenuItem _languageItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly ToolStripMenuItem[] _countOptions;
    private readonly ToolStripMenuItem[] _languageOptions;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly System.Windows.Forms.Timer _deviceChangeTimer;
    private readonly DeviceChangeWindow _deviceChangeWindow;
    private List<IMouseBatteryProvider> _providers = [];
    private AppSettings _settings;
    private AppText _text;
    private BatteryPanelForm _panel = null!;
    private NotifyIcon? _emptyTrayIcon;
    private bool _isPolling;
    private bool _isExiting;
    private bool _refreshPending;

    public TrayApplicationContext()
    {
        _settings = SettingsStore.Load();
        _text = new AppText(_settings.Language);
        _menu = new ContextMenuStrip();

        _refreshItem = new ToolStripMenuItem(_text.Refresh, null, async (_, _) => await RefreshAsync());
        _startupItem = new ToolStripMenuItem(_text.Startup)
        {
            CheckOnClick = true,
            Checked = IsRegisteredForStartup()
        };
        _startupItem.CheckedChanged += OnStartupCheckedChanged;

        _countItem = new ToolStripMenuItem();
        _countOptions = Enumerable.Range(1, 4).Select(count =>
        {
            var item = new ToolStripMenuItem(FormatCountOption(count)) { CheckOnClick = true, Tag = count };
            item.Click += OnCountOptionClick;
            _countItem.DropDownItems.Add(item);
            return item;
        }).ToArray();

        _languageItem = new ToolStripMenuItem();
        _languageOptions = new[]
        {
            new ToolStripMenuItem("日本語") { CheckOnClick = true, Tag = AppLanguage.Japanese },
            new ToolStripMenuItem("English") { CheckOnClick = true, Tag = AppLanguage.English }
        };
        foreach (var item in _languageOptions)
        {
            item.Click += OnLanguageOptionClick;
            _languageItem.DropDownItems.Add(item);
        }

        _exitItem = new ToolStripMenuItem(_text.Exit, null, (_, _) => ExitThread());
        RebuildMenu();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 1_000 };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _deviceChangeTimer = new System.Windows.Forms.Timer { Interval = 350 };
        _deviceChangeTimer.Tick += (_, _) =>
        {
            _deviceChangeTimer.Stop();
            ReconcileDevices();
            _ = RefreshAsync();
        };

        ReconcileDevices();
        _panel = CreatePanel();
        _deviceChangeWindow = new DeviceChangeWindow(() =>
        {
            if (!_isExiting)
            {
                _deviceChangeTimer.Stop();
                _deviceChangeTimer.Start();
            }
        });

        _pollTimer.Start();
        _ = RefreshAsync();
    }

    private BatteryPanelForm CreatePanel() => new(
        _providers.Select(provider => (_readings[provider.DeviceId], GetAccent(provider.DeviceId))).ToArray(),
        _menu,
        _text);

    private void ReconcileDevices()
    {
        if (_isExiting)
        {
            return;
        }

        var discovered = MouseDiscovery.Discover(_settings.MaxMouseCount);
        var previousProviders = _providers.ToDictionary(provider => provider.DeviceId, StringComparer.Ordinal);
        var nextProviders = new List<IMouseBatteryProvider>(discovered.Count);
        foreach (var candidate in discovered)
        {
            if (previousProviders.TryGetValue(candidate.DeviceId, out var existing))
            {
                // Keep the fresh HID handle for generic mice after plug/unplug while retaining known protocol state.
                nextProviders.Add(candidate.DeviceId.StartsWith("hid-mouse-", StringComparison.Ordinal) ? candidate : existing);
            }
            else
            {
                nextProviders.Add(candidate);
            }
        }

        var oldIds = _providers.Select(provider => provider.DeviceId).ToArray();
        var newIds = nextProviders.Select(provider => provider.DeviceId).ToArray();
        _providers = nextProviders;
        if (oldIds.SequenceEqual(newIds, StringComparer.Ordinal))
        {
            return;
        }

        var keep = new HashSet<string>(newIds, StringComparer.Ordinal);
        foreach (var id in _trayIcons.Keys.Where(id => !keep.Contains(id)).ToArray())
        {
            var trayIcon = _trayIcons[id];
            trayIcon.Visible = false;
            trayIcon.Dispose();
            _trayIcons.Remove(id);
            _ownedIcons[id].Dispose();
            _ownedIcons.Remove(id);
            _readings.Remove(id);
            _statusItems.Remove(id);
        }

        foreach (var provider in _providers)
        {
            if (!_readings.ContainsKey(provider.DeviceId))
            {
                _readings[provider.DeviceId] = new BatteryReading(
                    provider.DeviceId,
                    provider.DisplayName,
                    null,
                    null,
                    BatteryAvailability.Unknown,
                    null,
                    _settings.Language == AppLanguage.Japanese ? "初回読み込み中" : "Loading");
            }

            if (!_trayIcons.ContainsKey(provider.DeviceId))
            {
                var icon = TrayIconFactory.Create(_readings[provider.DeviceId], GetGlyph(provider.DeviceId), GetAccent(provider.DeviceId));
                _ownedIcons[provider.DeviceId] = icon;
                var trayIcon = new NotifyIcon
                {
                    Text = FormatToolTip(_readings[provider.DeviceId]),
                    Icon = icon,
                    ContextMenuStrip = _menu,
                    Visible = true
                };
                trayIcon.MouseClick += OnTrayIconMouseClick;
                _trayIcons[provider.DeviceId] = trayIcon;
            }
        }

        if (_providers.Count == 0)
        {
            if (_emptyTrayIcon is null)
            {
                _emptyTrayIcon = new NotifyIcon
                {
                    Text = "Mouse Battery Tray",
                    Icon = SystemIcons.Application,
                    ContextMenuStrip = _menu,
                    Visible = true
                };
                _emptyTrayIcon.MouseClick += OnTrayIconMouseClick;
            }
        }
        else if (_emptyTrayIcon is not null)
        {
            _emptyTrayIcon.Visible = false;
            _emptyTrayIcon.Dispose();
            _emptyTrayIcon = null;
        }

        RebuildMenu();
        if (_panel is not null)
        {
            _panel.SetDevices(_providers.Select(provider => (_readings[provider.DeviceId], GetAccent(provider.DeviceId))));
        }
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        _statusItems.Clear();
        foreach (var provider in _providers)
        {
            var status = new ToolStripMenuItem(FormatMenuStatus(_readings.GetValueOrDefault(provider.DeviceId)
                ?? new BatteryReading(provider.DeviceId, provider.DisplayName, null, null, BatteryAvailability.Unknown, null)))
            {
                Enabled = false
            };
            _statusItems[provider.DeviceId] = status;
            _menu.Items.Add(status);
        }

        if (_providers.Count > 0)
        {
            _menu.Items.Add(new ToolStripSeparator());
        }

        _refreshItem.Text = _text.Refresh;
        _startupItem.Text = _text.Startup;
        _countItem.Text = $"{_text.DetectCount}: {_settings.MaxMouseCount}";
        _languageItem.Text = _text.Language;
        _exitItem.Text = _text.Exit;
        foreach (var option in _countOptions)
        {
            var count = (int)option.Tag!;
            option.Text = FormatCountOption(count);
            option.Checked = count == _settings.MaxMouseCount;
        }

        foreach (var option in _languageOptions)
        {
            option.Checked = (AppLanguage)option.Tag! == _settings.Language;
        }

        _menu.Items.Add(_refreshItem);
        _menu.Items.Add(_countItem);
        _menu.Items.Add(_languageItem);
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);
    }

    private string FormatCountOption(int count) => _settings.Language == AppLanguage.Japanese
        ? $"{count}台"
        : $"{count} {(count == 1 ? "mouse" : "mice")}";

    private async Task RefreshAsync()
    {
        if (_isExiting)
        {
            return;
        }

        if (_isPolling)
        {
            _refreshPending = true;
            return;
        }

        _isPolling = true;
        _refreshItem.Enabled = false;
        _refreshItem.Text = _text.Refreshing;
        _panel?.SetRefreshing(true);

        try
        {
            var providers = _providers.ToArray();
            var reads = providers.Select(async provider =>
            {
                try
                {
                    var reading = await Task.Run(() => provider.Read(_shutdown.Token), _shutdown.Token);
                    return (Provider: provider, Reading: reading);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return (Provider: provider, Reading: (BatteryReading?)null);
                }
                catch (Exception ex)
                {
                    var previous = _readings.GetValueOrDefault(provider.DeviceId);
                    var failed = previous is null
                        ? new BatteryReading(provider.DeviceId, provider.DisplayName, null, null, BatteryAvailability.Unknown, null, ex.Message)
                        : previous with { Availability = BatteryAvailability.Unknown, Detail = ex.Message };
                    return (Provider: provider, Reading: (BatteryReading?)failed);
                }
            });

            foreach (var result in await Task.WhenAll(reads))
            {
                if (result.Reading is not null && _readings.ContainsKey(result.Provider.DeviceId))
                {
                    UpdateReading(result.Reading);
                }
            }
        }
        finally
        {
            _isPolling = false;
            if (!_isExiting && !_refreshItem.IsDisposed)
            {
                _refreshItem.Enabled = true;
                _refreshItem.Text = _text.Refresh;
                _panel?.SetRefreshing(false);
            }

            if (_refreshPending && !_isExiting)
            {
                _refreshPending = false;
                _ = RefreshAsync();
            }
        }
    }

    private void UpdateReading(BatteryReading reading)
    {
        if (!_trayIcons.TryGetValue(reading.DeviceId, out var trayIcon))
        {
            return;
        }

        var previous = _readings[reading.DeviceId];
        _readings[reading.DeviceId] = reading;
        _statusItems[reading.DeviceId].Text = FormatMenuStatus(reading);
        _panel.UpdateReading(reading);

        if (previous.Percent != reading.Percent
            || previous.IsCharging != reading.IsCharging
            || previous.Availability != reading.Availability)
        {
            var newIcon = TrayIconFactory.Create(reading, GetGlyph(reading.DeviceId), GetAccent(reading.DeviceId));
            trayIcon.Icon = newIcon;
            var oldIcon = _ownedIcons[reading.DeviceId];
            _ownedIcons[reading.DeviceId] = newIcon;
            oldIcon.Dispose();
        }

        trayIcon.Text = FormatToolTip(reading);
        if (reading.Percent is <= 15 && previous.Percent is > 15 && reading.Availability == BatteryAvailability.Available)
        {
            trayIcon.BalloonTipTitle = reading.DisplayName;
            trayIcon.BalloonTipText = _text.LowBattery(reading.Percent.Value);
            trayIcon.BalloonTipIcon = ToolTipIcon.Warning;
            trayIcon.ShowBalloonTip(5000);
        }
    }

    private string FormatMenuStatus(BatteryReading reading) => _text.MenuStatus(reading);

    private string FormatToolTip(BatteryReading reading)
    {
        var text = FormatMenuStatus(reading);
        return text.Length <= 63 ? text : text[..60] + "…";
    }

    private void OnTrayIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _panel.Toggle();
        }
    }

    private void OnCountOptionClick(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem { Tag: int count } || count == _settings.MaxMouseCount)
        {
            return;
        }

        _settings = _settings with { MaxMouseCount = count };
        SettingsStore.Save(_settings);
        ReconcileDevices();
        RebuildMenu();
        _ = RefreshAsync();
    }

    private void OnLanguageOptionClick(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem { Tag: AppLanguage language } || language == _settings.Language)
        {
            return;
        }

        _settings = _settings with { Language = language };
        SettingsStore.Save(_settings);
        _text = new AppText(language);
        _panel.SetLanguage(_text);
        RebuildMenu();
        foreach (var reading in _readings.Values)
        {
            if (_trayIcons.TryGetValue(reading.DeviceId, out var trayIcon))
            {
                trayIcon.Text = FormatToolTip(reading);
            }
        }
    }

    private void OnStartupCheckedChanged(object? sender, EventArgs e)
    {
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(StartupKeyPath, writable: true);
            if (_startupItem.Checked)
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    throw new InvalidOperationException(_text.StartupPathError);
                }

                runKey.SetValue(StartupValueName, $"\"{executablePath}\" --tray");
            }
            else
            {
                runKey.DeleteValue(StartupValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(_text.StartupError(ex.Message), "Mouse Battery Tray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _startupItem.CheckedChanged -= OnStartupCheckedChanged;
            _startupItem.Checked = !_startupItem.Checked;
            _startupItem.CheckedChanged += OnStartupCheckedChanged;
        }
    }

    private static bool IsRegisteredForStartup()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(StartupKeyPath, writable: false);
            return runKey?.GetValue(StartupValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    private static string GetGlyph(string deviceId) => deviceId switch
    {
        "lamzu-maya" => "M",
        "logitech-g502-x-plus" => "G",
        _ => "?"
    };

    private static Color GetAccent(string deviceId) => deviceId switch
    {
        "lamzu-maya" => Color.FromArgb(177, 137, 255),
        "logitech-g502-x-plus" => Color.FromArgb(104, 172, 255),
        _ => Color.FromArgb(140, 170, 190)
    };

    protected override void ExitThreadCore()
    {
        _isExiting = true;
        _shutdown.Cancel();
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _deviceChangeTimer.Stop();
        _deviceChangeTimer.Dispose();
        _deviceChangeWindow.Dispose();

        foreach (var trayIcon in _trayIcons.Values)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
        }

        _emptyTrayIcon?.Dispose();
        foreach (var icon in _ownedIcons.Values)
        {
            icon.Dispose();
        }

        _panel.Dispose();
        _menu.Dispose();
        _shutdown.Dispose();
        base.ExitThreadCore();
    }

    private sealed class DeviceChangeWindow : NativeWindow, IDisposable
    {
        private readonly Action _onDeviceChange;
        private readonly IntPtr _notification;

        public DeviceChangeWindow(Action onDeviceChange)
        {
            _onDeviceChange = onDeviceChange;
            // A hidden top-level window receives DBT_DEVNODES_CHANGED as a fallback;
            // RegisterDeviceNotification below adds per-HID arrival/removal events.
            CreateHandle(new CreateParams { Caption = "MouseBatteryTray.DeviceChange", ExStyle = 0x00000080 });

            var filter = new DeviceBroadcastInterface
            {
                Size = Marshal.SizeOf<DeviceBroadcastInterface>(),
                DeviceType = DeviceInterface,
                ClassGuid = HidInterfaceGuid,
                Name = '\0'
            };
            _notification = RegisterDeviceNotification(Handle, ref filter, DeviceNotifyWindowHandle);
        }

        protected override void WndProc(ref Message message)
        {
            var eventType = message.WParam.ToInt32();
            if (message.Msg == DeviceChangeMessage
                && (eventType is DeviceNodesChanged or DeviceArrival or DeviceRemovalComplete))
            {
                _onDeviceChange();
            }

            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (_notification != IntPtr.Zero)
            {
                UnregisterDeviceNotification(_notification);
            }

            DestroyHandle();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DeviceBroadcastInterface
        {
            public int Size;
            public int DeviceType;
            public int Reserved;
            public Guid ClassGuid;
            public char Name;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DeviceBroadcastInterface notificationFilter, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterDeviceNotification(IntPtr notificationHandle);
    }
}
