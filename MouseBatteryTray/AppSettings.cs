using System.Text.Json;
using MouseBatteryTray.Hardware;

namespace MouseBatteryTray;

internal enum AppLanguage
{
    Japanese,
    English
}

internal sealed record AppSettings(int MaxMouseCount = 4, AppLanguage Language = AppLanguage.Japanese);

internal static class SettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MouseBatteryTray",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (settings is not null)
                {
                    return settings with
                    {
                        MaxMouseCount = Math.Clamp(settings.MaxMouseCount, 1, 4),
                        Language = Enum.IsDefined(settings.Language) ? settings.Language : AppLanguage.Japanese
                    };
                }
            }
        }
        catch
        {
            // A damaged preferences file falls back to the defaults.
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // Settings are optional; the running session still uses the new values.
        }
    }
}

internal sealed class AppText(AppLanguage language)
{
    private readonly AppLanguage _language = language;
    private bool IsJapanese => _language == AppLanguage.Japanese;

    public string Refresh => IsJapanese ? "今すぐ更新" : "Refresh now";
    public string Refreshing => IsJapanese ? "更新中…" : "Refreshing…";
    public string Startup => IsJapanese ? "Windows 起動時に開始" : "Start with Windows";
    public string DetectCount => IsJapanese ? "検出するマウス数" : "Maximum mice to detect";
    public string Language => IsJapanese ? "言語 / Language" : "Language / 言語";
    public string Japanese => "日本語";
    public string English => "English";
    public string Exit => IsJapanese ? "終了" : "Exit";
    public string PanelTitle => IsJapanese ? "マウス バッテリー" : "Mouse Battery";
    public string PanelHint => IsJapanese
        ? "接続を即時検出・残量を1秒ごとに確認・Escで閉じる"
        : "Device changes detected instantly · battery checked every second · Esc to close";
    public string PanelRefreshing => IsJapanese ? "更新中…" : "Refreshing…";
    public string NoMouse => IsJapanese ? "マウスが検出されていません" : "No mice detected";
    public string Updated(DateTimeOffset time) => IsJapanese ? $"取得 {time:HH:mm:ss}" : $"Updated {time:HH:mm:ss}";
    public string NoReading => IsJapanese ? "取得記録なし" : "No reading yet";
    public string BatteryUnsupported => IsJapanese ? "この機器の残量方式は未対応" : "Battery protocol not supported";
    public string StartupPathError => IsJapanese ? "実行ファイルの場所を取得できませんでした。" : "Could not locate the executable.";
    public string StartupError(string message) => IsJapanese
        ? $"起動設定を変更できませんでした。\n{message}"
        : $"Could not change startup setting.\n{message}";
    public string LowBattery(int percent) => IsJapanese
        ? $"バッテリー残量が {percent}% です。充電してください。"
        : $"Battery is at {percent}%. Please charge the mouse.";

    public string MenuStatus(BatteryReading reading)
    {
        var status = reading.Availability switch
        {
            BatteryAvailability.Available when reading.Percent == 0 && reading.IsCharging == true => IsJapanese ? "0%・バッテリー切れ・充電中" : "0% · Empty · Charging",
            BatteryAvailability.Available when reading.Percent == 0 => IsJapanese ? "0%・バッテリー切れ" : "0% · Empty",
            BatteryAvailability.Available when reading.Percent is int percent && reading.IsCharging == true => IsJapanese ? $"{percent}%・充電中" : $"{percent}% · Charging",
            BatteryAvailability.Available when reading.Percent is int percent && percent <= 15 => IsJapanese ? $"{percent}%・残量低下" : $"{percent}% · Low",
            BatteryAvailability.Available when reading.Percent is int percent => $"{percent}%",
            BatteryAvailability.Sleeping when reading.Percent is int percent => IsJapanese ? $"スリープ中・前回 {percent}%" : $"Sleeping · Last {percent}%",
            BatteryAvailability.Sleeping => IsJapanese ? "スリープ中・残量未取得" : "Sleeping · No reading",
            BatteryAvailability.NotDetected => IsJapanese ? "未接続" : "Disconnected",
            BatteryAvailability.Unknown when reading.DeviceId.StartsWith("hid-mouse-", StringComparison.Ordinal) => BatteryUnsupported,
            BatteryAvailability.Unknown when reading.Percent is int percent => IsJapanese ? $"取得失敗・前回 {percent}%" : $"Read failed · Last {percent}%",
            BatteryAvailability.Unknown => reading.Detail ?? (IsJapanese ? "読み込み中" : "Loading"),
            _ => IsJapanese ? "残量未取得" : "Battery unavailable"
        };

        return $"{reading.DisplayName}  {status}";
    }

    public (string Text, Color Color) PanelStatus(BatteryReading reading) => reading.Availability switch
    {
        BatteryAvailability.Available when reading.Percent == 0 && reading.IsCharging == true => (IsJapanese ? "0%・充電中" : "0% · Charging", Color.FromArgb(255, 210, 90)),
        BatteryAvailability.Available when reading.Percent == 0 => (IsJapanese ? "バッテリー切れ" : "Empty", Color.FromArgb(255, 92, 100)),
        BatteryAvailability.Available when reading.IsCharging == true => (IsJapanese ? "充電中" : "Charging", Color.FromArgb(255, 210, 90)),
        BatteryAvailability.Available when reading.Percent is <= 15 => (IsJapanese ? "残量低下・充電してください" : "Low · Please charge", Color.FromArgb(255, 92, 100)),
        BatteryAvailability.Available when reading.Percent is not null => (IsJapanese ? "接続中" : "Connected", Color.FromArgb(82, 211, 143)),
        BatteryAvailability.Available => (IsJapanese ? "接続中・残量未取得" : "Connected · No reading", Color.FromArgb(160, 168, 184)),
        BatteryAvailability.Sleeping when reading.Percent is not null => (IsJapanese ? "スリープ中（最後の取得値）" : "Sleeping · Last reading", Color.FromArgb(150, 160, 200)),
        BatteryAvailability.Sleeping => (IsJapanese ? "スリープ中・残量未取得" : "Sleeping · No reading", Color.FromArgb(150, 160, 200)),
        BatteryAvailability.NotDetected => (IsJapanese ? "未接続" : "Disconnected", Color.FromArgb(120, 127, 142)),
        _ when reading.DeviceId.StartsWith("hid-mouse-", StringComparison.Ordinal) => (BatteryUnsupported, Color.FromArgb(160, 168, 184)),
        _ when reading.LastUpdated is null && reading.Percent is null => (IsJapanese ? "読み込み中" : "Loading", Color.FromArgb(160, 168, 184)),
        _ when reading.Percent is not null => (IsJapanese ? "取得失敗（前回値）" : "Read failed · Last value", Color.FromArgb(255, 92, 100)),
        _ => (IsJapanese ? "残量未取得" : "Battery unavailable", Color.FromArgb(160, 168, 184))
    };
}
