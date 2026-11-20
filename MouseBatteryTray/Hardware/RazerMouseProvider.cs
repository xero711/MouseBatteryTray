using System.Diagnostics;
using HidSharp;
using HidSharp.Reports;

namespace MouseBatteryTray.Hardware;

/// <summary>Razer wireless mouse battery protocol (feature report 0x07:0x80).</summary>
internal sealed class RazerMouseProvider : CachedBatteryProviderBase
{
    private const int VendorId = 0x1532;
    private const int FeatureReportLength = 91; // Windows report-ID byte + 90-byte Razer payload.
    private const byte BatteryCommand = 0x80;
    private const byte ChargingCommand = 0x84;
    private const byte PowerClass = 0x07;
    private const byte StatusOk = 0x02;
    private const byte StatusBusy = 0x01;
    private const byte StatusTimeout = 0x04;
    private const byte StatusUnsupported = 0x05;
    private static readonly byte[] FallbackTransactionIds = [0x1F, 0x3F, 0xFF, 0x9F, 0x08];

    private readonly int _productId;
    private readonly byte _preferredTransactionId;
    private string? _cachedPath;
    private byte? _cachedTransactionId;

    private RazerMouseProvider(int productId, RazerModel model)
    {
        _productId = productId;
        _preferredTransactionId = model.TransactionId;
        DisplayName = model.Name;
        DeviceId = $"razer-{productId:X4}";
    }

    public override string DeviceId { get; }
    public override string DisplayName { get; }

    public static bool TryCreate(HidDevice device, out RazerMouseProvider? provider)
    {
        if (device.VendorID == VendorId && Models.TryGetValue(device.ProductID, out var model))
        {
            provider = new RazerMouseProvider(device.ProductID, model);
            return true;
        }

        provider = null;
        return false;
    }

    protected override BatteryReading ReadCore(CancellationToken cancellationToken)
    {
        List<(HidDevice Device, ReportDescriptor Descriptor)> candidates;
        try
        {
            candidates = DeviceList.Local.GetHidDevices(VendorId, _productId)
                .Select(device =>
                {
                    try
                    {
                        var descriptor = device.GetReportDescriptor();
                        return (Device: device, Descriptor: descriptor);
                    }
                    catch
                    {
                        return (Device: device, Descriptor: (ReportDescriptor?)null);
                    }
                })
                .Where(candidate => candidate.Descriptor is not null
                    && candidate.Device.GetMaxFeatureReportLength() >= FeatureReportLength)
                .Select(candidate => (candidate.Device, candidate.Descriptor!))
                .OrderByDescending(candidate => IsPreferredUsage(candidate.Item2))
                .ToList();
        }
        catch
        {
            return NotDetected("Razer HID interfaces are unavailable");
        }

        if (candidates.Count == 0)
        {
            _cachedPath = null;
            _cachedTransactionId = null;
            return NotDetected("Razer battery feature interface not found");
        }

        if (_cachedPath is not null)
        {
            candidates = candidates
                .OrderByDescending(candidate => string.Equals(candidate.Device.DevicePath, _cachedPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var transactionIds = _cachedTransactionId is byte cached
            ? new[] { cached }
            : new[] { _preferredTransactionId }.Concat(FallbackTransactionIds)
                .Distinct()
                .ToArray();

        var sawOpenInterface = false;
        foreach (var (device, _) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!device.TryOpen(out HidStream? stream) || stream is null)
            {
                continue;
            }

            using (stream)
            {
                sawOpenInterface = true;
                foreach (var transactionId in transactionIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var battery = Query(stream, transactionId, BatteryCommand, cancellationToken);
                    if (battery.Status == QueryStatus.Success && battery.Value is byte raw)
                    {
                        _cachedPath = device.DevicePath;
                        _cachedTransactionId = transactionId;
                        var percent = Math.Clamp((int)Math.Round(raw * 100.0 / 255.0), 0, 100);
                        bool? charging = Query(stream, transactionId, ChargingCommand, cancellationToken) switch
                        {
                            { Status: QueryStatus.Success, Value: 0 } => false,
                            { Status: QueryStatus.Success, Value: 1 } => true,
                            _ => null
                        };
                        return Available(percent, charging, $"Razer HID feature PID {_productId:X4}");
                    }

                    if (battery.Status == QueryStatus.Timeout && _cachedTransactionId == transactionId)
                    {
                        return Sleeping("Razer mouse is asleep or not responding");
                    }

                    if (battery.Status is QueryStatus.Timeout or QueryStatus.Unsupported)
                    {
                        continue;
                    }
                }
            }
        }

        if (_cachedPath is not null && sawOpenInterface)
        {
            _cachedPath = null;
            _cachedTransactionId = null;
        }

        return Sleeping(sawOpenInterface
            ? "Razer battery command did not receive a matching reply"
            : "Razer feature interface could not be opened");
    }

    private static bool IsPreferredUsage(ReportDescriptor descriptor)
    {
        foreach (var item in descriptor.DeviceItems)
        {
            foreach (var value in item.Usages.GetAllValues())
            {
                var page = (ushort)(value >> 16);
                if (page is 0x0001 or 0xFF00)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static QueryResult Query(HidStream stream, byte transactionId, byte command, CancellationToken cancellationToken)
    {
        var request = new byte[FeatureReportLength];
        request[0] = 0; // Windows HID report ID prefix.
        request[1] = 0; // Razer payload status.
        request[2] = transactionId;
        request[6] = 0x02; // payload size
        request[7] = PowerClass;
        request[8] = command;
        byte checksum = 0;
        for (var i = 3; i <= 88; i++)
        {
            checksum ^= request[i];
        }
        request[89] = checksum;

        try
        {
            stream.SetFeature(request, 0, request.Length);
        }
        catch
        {
            return new QueryResult(QueryStatus.Rejected, null);
        }

        var response = new byte[FeatureReportLength];
        var stopwatch = Stopwatch.StartNew();
        var foreignResponses = 0;
        var sends = 1;
        while (stopwatch.ElapsedMilliseconds < 850)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(stopwatch.ElapsedMilliseconds < 80 ? 60 : 75);
            try
            {
                response[0] = 0;
                stream.GetFeature(response, 0, response.Length);
            }
            catch
            {
                return new QueryResult(QueryStatus.Rejected, null);
            }

            var status = response[1];
            var matches = response[2] == transactionId
                && response[7] == PowerClass
                && response[8] == command;
            if (matches)
            {
                if (status == StatusOk)
                {
                    return new QueryResult(QueryStatus.Success, response[10]);
                }

                if (status == StatusBusy)
                {
                    continue;
                }

                if (status == StatusTimeout)
                {
                    return new QueryResult(QueryStatus.Timeout, null);
                }

                if (status == StatusUnsupported)
                {
                    return new QueryResult(QueryStatus.Unsupported, null);
                }

                return new QueryResult(QueryStatus.Rejected, null);
            }

            if (status == StatusOk && ++foreignResponses % 4 == 0 && sends < 3)
            {
                try
                {
                    stream.SetFeature(request, 0, request.Length);
                    sends++;
                }
                catch
                {
                    return new QueryResult(QueryStatus.Rejected, null);
                }
            }
        }

        return new QueryResult(QueryStatus.NoReply, null);
    }

    private enum QueryStatus
    {
        Success,
        Timeout,
        Unsupported,
        NoReply,
        Rejected
    }

    private readonly record struct QueryResult(QueryStatus Status, byte? Value);
    private readonly record struct RazerModel(string Name, byte TransactionId);

    // Wireless and cable PIDs from OpenRazer's battery-readable mouse list.
    private static readonly Dictionary<int, RazerModel> Models = new()
    {
        [0x001F] = new("Razer Naga Epic", 0xFF),
        [0x0024] = new("Razer Mamba (2012)", 0xFF), [0x0025] = new("Razer Mamba (2012)", 0xFF),
        [0x0032] = new("Razer Ouroboros", 0xFF),
        [0x003E] = new("Razer Naga Epic Chroma", 0xFF), [0x003F] = new("Razer Naga Epic Chroma", 0xFF),
        [0x0044] = new("Razer Mamba", 0xFF), [0x0045] = new("Razer Mamba", 0xFF),
        [0x0059] = new("Razer Lancehead", 0x3F), [0x005A] = new("Razer Lancehead", 0x3F),
        [0x0062] = new("Razer Atheris", 0x1F),
        [0x006F] = new("Razer Lancehead Wireless", 0x1F), [0x0070] = new("Razer Lancehead Wireless", 0x1F),
        [0x0072] = new("Razer Mamba Wireless", 0x3F), [0x0073] = new("Razer Mamba Wireless", 0x3F),
        [0x0077] = new("Razer Pro Click", 0x1F), [0x0080] = new("Razer Pro Click", 0x1F),
        [0x007A] = new("Razer Viper Ultimate", 0xFF), [0x007B] = new("Razer Viper Ultimate", 0xFF),
        [0x007C] = new("Razer DeathAdder V2 Pro", 0x3F), [0x007D] = new("Razer DeathAdder V2 Pro", 0x3F),
        [0x0083] = new("Razer Basilisk X HyperSpeed", 0xFF),
        [0x0086] = new("Razer Basilisk Ultimate", 0x1F), [0x0088] = new("Razer Basilisk Ultimate", 0x1F),
        [0x008F] = new("Razer Naga Pro", 0x1F), [0x0090] = new("Razer Naga Pro", 0x1F),
        [0x0094] = new("Razer Orochi V2", 0x1F),
        [0x009A] = new("Razer Pro Click Mini", 0x1F), [0x009C] = new("Razer DeathAdder V2 X HyperSpeed", 0x1F),
        [0x009E] = new("Razer Viper Mini SE", 0x1F), [0x009F] = new("Razer Viper Mini SE", 0x1F),
        [0x00A5] = new("Razer Viper V2 Pro", 0x1F), [0x00A6] = new("Razer Viper V2 Pro", 0x1F),
        [0x00A7] = new("Razer Naga V2 Pro", 0x1F), [0x00A8] = new("Razer Naga V2 Pro", 0x1F),
        [0x00AA] = new("Razer Basilisk V3 Pro", 0x1F), [0x00AB] = new("Razer Basilisk V3 Pro", 0x1F),
        [0x00AF] = new("Razer Cobra Pro", 0x1F), [0x00B0] = new("Razer Cobra Pro", 0x1F),
        [0x00B3] = new("Razer HyperPolling Dongle", 0x1F), [0x00B4] = new("Razer Naga V2 HyperSpeed", 0x1F),
        [0x00B6] = new("Razer DeathAdder V3 Pro", 0x1F), [0x00B7] = new("Razer DeathAdder V3 Pro", 0x1F),
        [0x00B8] = new("Razer Viper V3 HyperSpeed", 0x1F), [0x00B9] = new("Razer Basilisk V3 X HyperSpeed", 0x1F),
        [0x00BE] = new("Razer DeathAdder V4 Pro", 0x1F), [0x00BF] = new("Razer DeathAdder V4 Pro", 0x1F),
        [0x00C0] = new("Razer Viper V3 Pro", 0x1F), [0x00C1] = new("Razer Viper V3 Pro", 0x1F),
        [0x00C2] = new("Razer DeathAdder V3 Pro", 0x1F), [0x00C3] = new("Razer DeathAdder V3 Pro", 0x1F),
        [0x00C4] = new("Razer DeathAdder V3 HyperSpeed", 0x1F), [0x00C5] = new("Razer DeathAdder V3 HyperSpeed", 0x1F),
        [0x00C7] = new("Razer Pro Click V2 Vertical Edition", 0x1F), [0x00C8] = new("Razer Pro Click V2 Vertical Edition", 0x1F),
        [0x00CC] = new("Razer Basilisk V3 Pro 35K", 0x1F), [0x00CD] = new("Razer Basilisk V3 Pro 35K", 0x1F),
        [0x00D0] = new("Razer Pro Click V2", 0x1F), [0x00D1] = new("Razer Pro Click V2", 0x1F),
        [0x00D3] = new("Razer Basilisk Mobile", 0x1F), [0x00D4] = new("Razer Basilisk Mobile", 0x1F),
        [0x00D6] = new("Razer Basilisk V3 Pro 35K Phantom Green", 0x1F), [0x00D7] = new("Razer Basilisk V3 Pro 35K Phantom Green", 0x1F),
        [0x00DE] = new("Razer Viper V3 Pro SE", 0x1F), [0x00DF] = new("Razer Viper V3 Pro SE", 0x1F),
        [0x00E5] = new("Razer Viper V4 Pro", 0x1F), [0x00E6] = new("Razer Viper V4 Pro", 0x1F)
    };
}
