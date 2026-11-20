using System.Security.Cryptography;
using System.Text;
using HidSharp;
using HidSharp.Reports;

namespace MouseBatteryTray.Hardware;

internal static class MouseDiscovery
{
    private const uint MouseUsage = 0x00010002;
    private const uint BatteryStrengthUsage = 0x00060020;

    public static List<IMouseBatteryProvider> Discover(int maximum)
    {
        var candidates = new Dictionary<string, (string SortKey, IMouseBatteryProvider Provider)>(StringComparer.Ordinal);
        IEnumerable<HidDevice> devices;
        try
        {
            devices = DeviceList.Local.GetHidDevices().ToArray();
        }
        catch
        {
            return [];
        }

        foreach (var device in devices)
        {
            ReportDescriptor descriptor;
            try
            {
                descriptor = device.GetReportDescriptor();
            }
            catch
            {
                continue;
            }

            if (RazerMouseProvider.TryCreate(device, out var razerProvider) && razerProvider is not null)
            {
                var razerPath = device.DevicePath ?? $"{device.VendorID:X4}:{device.ProductID:X4}";
                candidates.TryAdd(razerProvider.DeviceId, (razerPath, razerProvider));
                continue;
            }

            if (!descriptor.DeviceItems.Any(item => item.Usages.GetAllValues().Contains(MouseUsage)))
            {
                continue;
            }

            var friendlyName = device.GetFriendlyName();
            var productName = string.IsNullOrWhiteSpace(friendlyName)
                ? $"HID mouse {device.VendorID:X4}:{device.ProductID:X4}"
                : friendlyName.Trim();

            // Some keyboards expose a secondary pointer collection for media controls.
            if (LooksLikeKeyboard(productName))
            {
                continue;
            }

            IMouseBatteryProvider provider;
            if (device.VendorID == 0x046D && device.ProductID is 0xC095 or 0xC547)
            {
                provider = new LogitechG502XPlusProvider();
            }
            else if (device.VendorID == 0x3554 && device.ProductID is 0xF50F or 0xF510)
            {
                provider = new LamzuMayaProvider();
            }
            else
            {
                provider = new GenericHidMouseProvider(device, descriptor, productName);
            }

            var sortKey = device.DevicePath ?? $"{device.VendorID:X4}:{device.ProductID:X4}:{productName}";
            candidates.TryAdd(provider.DeviceId, (sortKey, provider));
        }

        return candidates.Values
            .OrderBy(candidate => candidate.Provider.DeviceId.StartsWith("hid-mouse-", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(candidate => candidate.SortKey, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maximum, 1, 4))
            .Select(candidate => candidate.Provider)
            .ToList();
    }

    private sealed class GenericHidMouseProvider : CachedBatteryProviderBase
    {
        private readonly HidDevice _device;
        private readonly ReportDescriptor _descriptor;
        private readonly bool _hasBatteryUsage;

        public GenericHidMouseProvider(HidDevice device, ReportDescriptor descriptor, string displayName)
        {
            _device = device;
            _descriptor = descriptor;
            DisplayName = displayName;
            var identity = device.DevicePath ?? $"{device.VendorID:X4}:{device.ProductID:X4}:{displayName}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..10].ToLowerInvariant();
            DeviceId = $"hid-mouse-{hash}";
            _hasBatteryUsage = descriptor.Reports.Any(report => report.GetAllUsages().Contains(BatteryStrengthUsage));
        }

        public override string DeviceId { get; }
        public override string DisplayName { get; }

        protected override BatteryReading ReadCore(CancellationToken cancellationToken)
        {
            if (!_hasBatteryUsage)
            {
                return new BatteryReading(DeviceId, DisplayName, null, null, BatteryAvailability.Unknown, null,
                    "Standard HID Generic Device Controls Battery Strength usage is not exposed");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!_device.TryOpen(out HidStream? stream) || stream is null)
            {
                return new BatteryReading(DeviceId, DisplayName, null, null, BatteryAvailability.Unknown, null,
                    "HID battery collection could not be opened");
            }

            using (stream)
            {
                foreach (var report in _descriptor.FeatureReports.Where(HasBatteryStrength))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var buffer = new byte[Math.Max(report.Length, _device.GetMaxFeatureReportLength())];
                    buffer[0] = report.ReportID;
                    try
                    {
                        stream.GetFeature(buffer, 0, buffer.Length);
                        if (TryReadPercent(report, buffer, out var percent))
                        {
                            return Available(percent, null, "Standard HID Generic Device Controls Battery Strength");
                        }
                    }
                    catch
                    {
                        // Some devices advertise the feature but do not implement Get Feature.
                    }
                }

                foreach (var report in _descriptor.InputReports.Where(HasBatteryStrength))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var buffer = new byte[Math.Max(report.Length, _device.GetMaxInputReportLength())];
                    try
                    {
                        stream.ReadTimeout = 150;
                        var length = stream.Read(buffer, 0, buffer.Length);
                        if (length > 0 && TryReadPercent(report, buffer, out var percent))
                        {
                            return Available(percent, null, "Standard HID Generic Device Controls Battery Strength");
                        }
                    }
                    catch (TimeoutException)
                    {
                        break;
                    }
                    catch
                    {
                        break;
                    }
                }
            }

            return new BatteryReading(DeviceId, DisplayName, null, null, BatteryAvailability.Unknown, null,
                "HID battery report did not include a percentage");
        }

        private static bool HasBatteryStrength(Report report) => report.GetAllUsages().Contains(BatteryStrengthUsage);

        private static bool TryReadPercent(Report report, byte[] buffer, out int percent)
        {
            percent = 0;
            var foundPercent = 0;
            var found = false;
            try
            {
                report.Read(buffer, 0, value =>
                {
                    if (!value.IsValid || !value.Usages.Contains(BatteryStrengthUsage))
                    {
                        return;
                    }

                    var raw = value.GetLogicalValue();
                    var dataItem = value.DataItem;
                    var scaled = dataItem.LogicalMinimum >= 0 && dataItem.LogicalMaximum <= 100
                        ? raw
                        : (int)Math.Round(value.GetScaledValue(0, 100));
                    if (scaled is >= 0 and <= 100)
                    {
                        foundPercent = scaled;
                        found = true;
                    }
                });
            }
            catch
            {
                return false;
            }

            percent = foundPercent;
            return found;
        }
    }

    private static bool LooksLikeKeyboard(string productName) =>
        productName.Contains("keyboard", StringComparison.OrdinalIgnoreCase)
        || productName.Contains("keypad", StringComparison.OrdinalIgnoreCase)
        || productName.Contains("huntsman", StringComparison.OrdinalIgnoreCase)
        || productName.Contains("blackwidow", StringComparison.OrdinalIgnoreCase)
        || productName.Contains("cynosa", StringComparison.OrdinalIgnoreCase)
        || productName.Contains("ornata", StringComparison.OrdinalIgnoreCase);
}
