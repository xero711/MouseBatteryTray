namespace MouseBatteryTray;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var instanceMutex = new Mutex(true, @"Local\MouseBatteryTray", out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
