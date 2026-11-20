namespace MouseBatteryTray.Hardware;

public enum BatteryAvailability
{
    Unknown,
    Available,
    Sleeping,
    NotDetected
}

public sealed record BatteryReading(
    string DeviceId,
    string DisplayName,
    int? Percent,
    bool? IsCharging,
    BatteryAvailability Availability,
    DateTimeOffset? LastUpdated,
    string? Detail = null);

public interface IMouseBatteryProvider
{
    string DeviceId { get; }
    string DisplayName { get; }
    BatteryReading Read(CancellationToken cancellationToken);
}
