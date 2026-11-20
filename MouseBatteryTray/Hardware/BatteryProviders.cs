using System.Diagnostics;
using HidSharp;
using HidSharp.Reports;

namespace MouseBatteryTray.Hardware;

/// <summary>
/// Shared plumbing: HID enumeration by VID/PID + top-level usage, and a
/// short-lived "last good value" cache (max 300 s) used for Sleeping results.
/// </summary>
internal static class HidHelpers
{
    /// <summary>Returns (usagePage, usage) of the device's top-level collection, or null.</summary>
    public static (ushort Page, ushort Usage)? GetTopLevelUsage(HidDevice device, out ReportDescriptor? descriptor)
    {
        descriptor = null;
        try
        {
            descriptor = device.GetReportDescriptor();
            var item = descriptor.DeviceItems.FirstOrDefault();
            if (item is null)
            {
                return null;
            }

            foreach (uint value in item.Usages.GetAllValues())
            {
                return ((ushort)(value >> 16), (ushort)(value & 0xFFFF));
            }
        }
        catch
        {
            // Descriptor could not be obtained/parsed: treat as non-matching (conservative).
        }

        return null;
    }

    /// <summary>
    /// Enumerates HID collections for VID/PID whose top-level usage matches exactly.
    /// </summary>
    public static List<(HidDevice Device, ReportDescriptor Descriptor)> FindCollections(
        int vendorId, int productId, ushort usagePage, ushort usage)
    {
        var result = new List<(HidDevice, ReportDescriptor)>();
        IEnumerable<HidDevice> devices;
        try
        {
            devices = DeviceList.Local.GetHidDevices(vendorId, productId).ToList();
        }
        catch
        {
            return result;
        }

        foreach (var device in devices)
        {
            var tlu = GetTopLevelUsage(device, out var descriptor);
            if (tlu is { } u && u.Page == usagePage && u.Usage == usage && descriptor is not null)
            {
                result.Add((device, descriptor));
            }
        }

        return result;
    }

    public static bool HasOutputReport(ReportDescriptor descriptor, byte reportId, int expectedLength)
    {
        try
        {
            return descriptor.OutputReports.Any(r => r.ReportID == reportId && r.Length == expectedLength);
        }
        catch
        {
            return false;
        }
    }

    public static bool HasInputReport(ReportDescriptor descriptor, byte reportId)
    {
        try
        {
            return descriptor.InputReports.Any(r => r.ReportID == reportId);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads one input report with the given timeout. Returns the byte count, or 0 on timeout.
    /// On Windows HidSharp throws <see cref="TimeoutException"/> when ReadTimeout elapses.
    /// </summary>
    public static int TryRead(HidStream stream, byte[] buffer, int timeoutMs)
    {
        try
        {
            stream.ReadTimeout = Math.Max(1, timeoutMs);
            return stream.Read(buffer, 0, buffer.Length);
        }
        catch (TimeoutException)
        {
            return 0;
        }
    }
}

/// <summary>Base class holding identity and the 300-second last-known-good cache.</summary>
public abstract class CachedBatteryProviderBase : IMouseBatteryProvider
{
    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromSeconds(300);

    private readonly object _gate = new();
    private int? _lastPercent;
    private DateTimeOffset? _lastUpdated;

    public abstract string DeviceId { get; }
    public abstract string DisplayName { get; }

    public BatteryReading Read(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ReadCore(cancellationToken);
        }
    }

    protected abstract BatteryReading ReadCore(CancellationToken cancellationToken);

    protected BatteryReading Available(int percent, bool? charging, string? detail)
    {
        var now = DateTimeOffset.Now;
        _lastPercent = percent;
        _lastUpdated = now;
        return new BatteryReading(DeviceId, DisplayName, percent, charging, BatteryAvailability.Available, now, detail);
    }

    protected BatteryReading NotDetected(string detail)
        => new(DeviceId, DisplayName, null, null, BatteryAvailability.NotDetected, null, detail);

    /// <summary>Target HID exists but the mouse did not answer. Never fabricates a 0%.</summary>
    protected BatteryReading Sleeping(string detail)
    {
        if (_lastPercent is int p && _lastUpdated is DateTimeOffset t && DateTimeOffset.Now - t <= MaxCacheAge)
        {
            return new BatteryReading(DeviceId, DisplayName, p, null, BatteryAvailability.Sleeping, t, detail);
        }

        _lastPercent = null;
        _lastUpdated = null;
        return new BatteryReading(DeviceId, DisplayName, null, null, BatteryAvailability.Sleeping, null, detail);
    }
}

/// <summary>
/// Logitech G502 X PLUS over USB (046D:C095) or LIGHTSPEED receiver (046D:C547), HID++ 2.0.
/// Requests are sent only as long report 0x11 to ff00:0002. USB uses device index FF;
/// receiver traffic scans slots 1..6 and observes HID++ 1.0 errors (0x8F).
/// </summary>
public sealed class LogitechG502XPlusProvider : CachedBatteryProviderBase
{
    private const int VendorId = 0x046D;
    private const int WiredProductId = 0xC095;
    private const int ReceiverProductId = 0xC547;
    private const ushort VendorUsagePage = 0xFF00;
    private const ushort ShortUsage = 0x0001;
    private const ushort LongUsage = 0x0002;

    private const byte ShortReportId = 0x10;
    private const byte LongReportId = 0x11;
    private const int ShortReportLength = 7;
    private const int LongReportLength = 20;

    private const ushort FeatureUnifiedBattery = 0x1004;
    private const ushort FeatureBatteryStatus = 0x1000;

    // Non-zero software ID (0 is reserved for device notifications).
    private const byte SoftwareId = 0x0B;

    private const int RequestTimeoutMs = 700;
    private const int ReadSliceMs = 40;

    // Keep route caches separate so USB device index FF never leaks into receiver requests.
    private readonly Dictionary<int, CachedRoute> _cachedRoutes = new();
    private readonly record struct CachedRoute(byte DeviceIndex, byte FeatureIndex, ushort FeatureId);

    public override string DeviceId => "logitech-g502-x-plus";
    public override string DisplayName => "G502 X";

    protected override BatteryReading ReadCore(CancellationToken cancellationToken)
    {
        string lastDetail = "no response from mouse";
        var foundCollection = false;

        // A connected USB cable is the charging path, so prefer it before the receiver.
        var wiredReading = TryReadOnProduct(WiredProductId, isReceiver: false, cancellationToken,
            ref foundCollection, ref lastDetail);
        if (wiredReading is not null)
        {
            return wiredReading;
        }

        var receiverReading = TryReadOnProduct(ReceiverProductId, isReceiver: true, cancellationToken,
            ref foundCollection, ref lastDetail);
        if (receiverReading is not null)
        {
            return receiverReading;
        }

        return foundCollection
            ? Sleeping(lastDetail)
            : NotDetected("G502 X PLUS HID++ collection (046D:C095/C547) not found");
    }

    private BatteryReading? TryReadOnProduct(
        int productId,
        bool isReceiver,
        CancellationToken cancellationToken,
        ref bool foundCollection,
        ref string lastDetail)
    {
        var longCollections = HidHelpers.FindCollections(VendorId, productId, VendorUsagePage, LongUsage)
            .Where(c => c.Device.GetMaxOutputReportLength() == LongReportLength
                        && HidHelpers.HasOutputReport(c.Descriptor, LongReportId, LongReportLength))
            .ToList();

        if (longCollections.Count == 0)
        {
            return null;
        }

        foundCollection = true;
        var shortCollections = isReceiver
            ? HidHelpers.FindCollections(VendorId, productId, VendorUsagePage, ShortUsage)
                .Where(c => c.Device.GetMaxInputReportLength() == ShortReportLength)
                .Select(c => c.Device)
                .ToList()
            : new List<HidDevice>();

        foreach (var (longDevice, _) in longCollections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!longDevice.TryOpen(out HidStream? longStream))
            {
                lastDetail = "could not open HID++ long collection";
                continue;
            }

            var shortStreams = new List<HidStream>();
            try
            {
                foreach (var sd in shortCollections)
                {
                    if (sd.TryOpen(out HidStream? ss))
                    {
                        shortStreams.Add(ss);
                    }
                }

                var reading = TryReadFromDevice(productId, longDevice, longStream, shortStreams,
                    isReceiver, cancellationToken, ref lastDetail);
                if (reading is not null)
                {
                    return reading;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastDetail = "HID I/O error: " + ex.GetType().Name;
            }
            finally
            {
                foreach (var ss in shortStreams)
                {
                    ss.Dispose();
                }

                longStream.Dispose();
            }
        }

        return null;
    }

    private BatteryReading? TryReadFromDevice(
        int productId,
        HidDevice longDevice,
        HidStream longStream,
        List<HidStream> shortStreams,
        bool isReceiver,
        CancellationToken ct,
        ref string lastDetail)
    {
        var io = new HidppIo(longStream, shortStreams, longDevice.GetMaxInputReportLength());
        var transportName = isReceiver ? "receiver" : "USB";

        // Fast path for this exact transport.
        if (_cachedRoutes.TryGetValue(productId, out var cached))
        {
            var r = QueryBattery(io, cached.DeviceIndex, cached.FeatureIndex, cached.FeatureId, transportName, ct);
            if (r is not null)
            {
                return r;
            }

            _cachedRoutes.Remove(productId);
        }

        var deviceIndices = isReceiver
            ? Enumerable.Range(1, 6).Select(i => (byte)i)
            : new[] { (byte)0xFF };

        foreach (var deviceIndex in deviceIndices)
        {
            ct.ThrowIfCancellationRequested();

            // Prefer 0x1004 Unified Battery, fall back to 0x1000 Battery Status.
            foreach (var featureId in new[] { FeatureUnifiedBattery, FeatureBatteryStatus })
            {
                var lookup = GetFeatureIndex(io, deviceIndex, featureId, ct);
                if (lookup.Unreachable)
                {
                    break; // nothing answering at this index; next slot
                }

                if (lookup.Index is not byte featureIndex)
                {
                    continue; // feature not supported; try fallback
                }

                var r = QueryBattery(io, deviceIndex, featureIndex, featureId, transportName, ct);
                if (r is not null)
                {
                    _cachedRoutes[productId] = new CachedRoute(deviceIndex, featureIndex, featureId);
                    return r;
                }
            }
        }

        lastDetail = isReceiver
            ? "no HID++ battery response in receiver slots 1..6"
            : "no HID++ battery response over USB (device index FF)";
        return null;
    }

    private (bool Unreachable, byte? Index) GetFeatureIndex(HidppIo io, byte deviceIndex, ushort featureId, CancellationToken ct)
    {
        // Root feature (index 0x00), function 0 = GetFeature(featureId).
        var resp = io.Request(deviceIndex, 0x00, 0, new[] { (byte)(featureId >> 8), (byte)(featureId & 0xFF) }, ct);
        if (resp.Status != HidppStatus.Ok)
        {
            // Timeout / receiver error => nobody reachable; HID++ error => treat as unsupported.
            return (resp.Status != HidppStatus.HidppError, null);
        }

        byte index = resp.Params[0];
        return (false, index == 0 ? null : index);
    }

    private BatteryReading? QueryBattery(
        HidppIo io,
        byte deviceIndex,
        byte featureIndex,
        ushort featureId,
        string transportName,
        CancellationToken ct)
    {
        if (featureId == FeatureUnifiedBattery)
        {
            // 0x1004 function 0 reports whether a state-of-charge percentage
            // is supported (response p1 bit 1). Without that capability p0 in
            // GetStatus is always zero and must not be shown as 0%.
            var capabilities = io.Request(deviceIndex, featureIndex, 0, Array.Empty<byte>(), ct);
            if (capabilities.Status != HidppStatus.Ok || (capabilities.Params[1] & 0x02) == 0)
            {
                return null;
            }

            // Function 1 GetStatus: p0 = state of charge %, p2 = charging status.
            var resp = io.Request(deviceIndex, featureIndex, 1, Array.Empty<byte>(), ct);
            if (resp.Status != HidppStatus.Ok)
            {
                return null;
            }

            int percent = resp.Params[0];
            if (percent > 100)
            {
                return null;
            }

            byte chargingStatus = resp.Params[2];
            bool? charging = MapChargingStatus(chargingStatus);
            return Available(percent, charging, $"HID++ 0x1004 {transportName} index {deviceIndex:X2}");
        }

        if (featureId == FeatureBatteryStatus)
        {
            // 0x1000 function 0 GetBatteryLevelStatus: p0 = level %, p2 = status.
            var resp = io.Request(deviceIndex, featureIndex, 0, Array.Empty<byte>(), ct);
            if (resp.Status != HidppStatus.Ok)
            {
                return null;
            }

            int percent = resp.Params[0];
            if (percent is 0 or > 100)
            {
                // 0 means "level unknown" for 0x1000; do not report it as 0%.
                return null;
            }

            byte status = resp.Params[2];
            bool? charging = MapChargingStatus(status);
            return Available(percent, charging, $"HID++ 0x1000 {transportName} index {deviceIndex:X2}");
        }

        return null;
    }

    private static bool? MapChargingStatus(byte status) => status switch
    {
        0 or 3 => false,       // discharging or full
        1 or 2 or 4 => true,   // charging, nearly full, or slow charging
        _ => null              // invalid battery or charging error
    };

    private enum HidppStatus
    {
        Ok,
        Timeout,
        HidppError,
        ReceiverError
    }

    private readonly record struct HidppResponse(HidppStatus Status, byte[] Params);

    /// <summary>
    /// Sends one long HID++ 2.0 request and waits for the exactly-matching reply
    /// (report ID, device index/slot, feature index, function, software ID).
    /// </summary>
    private sealed class HidppIo
    {
        private readonly HidStream _longStream;
        private readonly List<HidStream> _shortStreams;
        private readonly byte[] _longBuffer;
        private readonly byte[] _shortBuffer = new byte[ShortReportLength];

        public HidppIo(HidStream longStream, List<HidStream> shortStreams, int longInputLength)
        {
            _longStream = longStream;
            _shortStreams = shortStreams;
            _longBuffer = new byte[Math.Max(longInputLength, LongReportLength)];
        }

        public HidppResponse Request(byte slot, byte featureIndex, byte function, byte[] args, CancellationToken ct)
        {
            byte funcSwid = (byte)(((function & 0x0F) << 4) | (SoftwareId & 0x0F));

            var request = new byte[LongReportLength];
            request[0] = LongReportId;
            request[1] = slot;
            request[2] = featureIndex;
            request[3] = funcSwid;
            Array.Copy(args, 0, request, 4, Math.Min(args.Length, LongReportLength - 4));

            _longStream.Write(request, 0, request.Length);

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < RequestTimeoutMs)
            {
                ct.ThrowIfCancellationRequested();

                int n = HidHelpers.TryRead(_longStream, _longBuffer, ReadSliceMs);
                if (n >= LongReportLength && _longBuffer[0] == LongReportId && _longBuffer[1] == slot)
                {
                    // Normal response.
                    if (_longBuffer[2] == featureIndex && _longBuffer[3] == funcSwid)
                    {
                        var p = new byte[16];
                        Array.Copy(_longBuffer, 4, p, 0, 16);
                        return new HidppResponse(HidppStatus.Ok, p);
                    }

                    // HID++ 2.0 error: [11, dev, FF, featIdx, funcSwid, err].
                    if (_longBuffer[2] == 0xFF && _longBuffer[3] == featureIndex && _longBuffer[4] == funcSwid)
                    {
                        return new HidppResponse(HidppStatus.HidppError, Array.Empty<byte>());
                    }
                }

                // Receiver HID++ 1.0 error on short collection: [10, dev, 8F, featIdx, funcSwid, err, 00].
                foreach (var ss in _shortStreams)
                {
                    int m = HidHelpers.TryRead(ss, _shortBuffer, 1);
                    if (m >= ShortReportLength
                        && _shortBuffer[0] == ShortReportId
                        && _shortBuffer[1] == slot
                        && _shortBuffer[2] == 0x8F
                        && _shortBuffer[3] == featureIndex
                        && _shortBuffer[4] == funcSwid)
                    {
                        return new HidppResponse(HidppStatus.ReceiverError, Array.Empty<byte>());
                    }
                }
            }

            return new HidppResponse(HidppStatus.Timeout, Array.Empty<byte>());
        }
    }
}

/// <summary>
/// LAMZU Maya over USB (3554:F50F) or Compx 4K receiver (3554:F510).
/// Uses the read-only Compx report-8 battery query (08 04 ...), sent only to the
/// ff02:0002 collection; replies must match 08 04 and the 0x55 checksum.
/// </summary>
public sealed class LamzuMayaProvider : CachedBatteryProviderBase
{
    private const int VendorId = 0x3554;
    private const int WiredProductId = 0xF50F;
    private const int ReceiverProductId = 0xF510;
    private const ushort UsagePage = 0xFF02;
    private const ushort Usage = 0x0002;

    private const byte ReportId = 0x08;
    private const byte CommandBattery = 0x04;
    private const int FrameLength = 17;
    private const byte ChecksumBase = 0x55;

    private const int RequestTimeoutMs = 600;
    private const int ReadSliceMs = 50;

    public override string DeviceId => "lamzu-maya";
    public override string DisplayName => "LAMZU Maya";

    protected override BatteryReading ReadCore(CancellationToken cancellationToken)
    {
        string lastDetail = "no response from mouse";
        var foundCollection = false;

        // Maya charges over the USB device path (F50F); use it before its 4K receiver (F510).
        var wiredReading = TryReadOnProduct(WiredProductId, "wired", cancellationToken,
            ref foundCollection, ref lastDetail);
        if (wiredReading?.Availability == BatteryAvailability.Available)
        {
            return wiredReading;
        }

        var receiverReading = TryReadOnProduct(ReceiverProductId, "receiver", cancellationToken,
            ref foundCollection, ref lastDetail);
        if (receiverReading?.Availability == BatteryAvailability.Available)
        {
            return receiverReading;
        }

        if (receiverReading is not null)
        {
            return receiverReading;
        }

        if (wiredReading is not null)
        {
            return wiredReading;
        }

        return foundCollection
            ? Sleeping(lastDetail)
            : NotDetected("Compx collection (3554:F50F/F510) not found");
    }

    private BatteryReading? TryReadOnProduct(
        int productId,
        string transportName,
        CancellationToken cancellationToken,
        ref bool foundCollection,
        ref string lastDetail)
    {
        var collections = HidHelpers.FindCollections(VendorId, productId, UsagePage, Usage)
            .Where(c => c.Device.GetMaxOutputReportLength() == FrameLength
                        && c.Device.GetMaxInputReportLength() >= FrameLength
                        && HidHelpers.HasOutputReport(c.Descriptor, ReportId, FrameLength)
                        && HidHelpers.HasInputReport(c.Descriptor, ReportId))
            .Select(c => c.Device)
            .ToList();

        if (collections.Count == 0)
        {
            return null;
        }

        foundCollection = true;
        foreach (var device in collections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!device.TryOpen(out HidStream? stream))
            {
                lastDetail = "could not open Compx collection";
                continue;
            }

            try
            {
                var reading = Query(device, stream, transportName, cancellationToken);
                if (reading is not null)
                {
                    return reading;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastDetail = "HID I/O error: " + ex.GetType().Name;
            }
            finally
            {
                stream.Dispose();
            }
        }

        return Sleeping(lastDetail);
    }

    private BatteryReading? Query(HidDevice device, HidStream stream, string transportName, CancellationToken ct)
    {
        var request = new byte[FrameLength];
        request[0] = ReportId;
        request[1] = CommandBattery;
        // bytes 2..15 = 0
        request[16] = Checksum(request);

        stream.Write(request, 0, request.Length);

        var buffer = new byte[Math.Max(device.GetMaxInputReportLength(), FrameLength)];
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < RequestTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();

            int n = HidHelpers.TryRead(stream, buffer, ReadSliceMs);
            if (n < FrameLength || buffer[0] != ReportId || buffer[1] != CommandBattery)
            {
                continue;
            }

            if (buffer[16] != Checksum(buffer))
            {
                continue;
            }

            int percent = buffer[6];
            if (percent > 100)
            {
                continue;
            }

            bool charging = buffer[7] != 0;
            return Available(percent, charging, $"Compx {transportName} report-8 cmd 0x04");
        }

        return null;
    }

    /// <summary>(0x55 - sum(bytes 0..15)) mod 256.</summary>
    private static byte Checksum(byte[] frame)
    {
        int sum = 0;
        for (int i = 0; i < 16; i++)
        {
            sum += frame[i];
        }

        return (byte)((ChecksumBase - sum) & 0xFF);
    }
}
