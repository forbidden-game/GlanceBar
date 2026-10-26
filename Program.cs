using Microsoft.Win32;

namespace GlanceBar;

internal static class Program
{
    private const string MutexName = @"Local\GlanceBar.SingleInstance.6D9AA8A2";

    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments.Contains(NetworkHelper.ServiceArgument, StringComparer.OrdinalIgnoreCase))
        {
            return NetworkHelper.RunService();
        }
        if (arguments.Contains("--migrate-user", StringComparer.OrdinalIgnoreCase))
        {
            Installation.MigrateCurrentUser();
            return 0;
        }
        if (arguments.Contains("--configure-user", StringComparer.OrdinalIgnoreCase))
        {
            Installation.ConfigureCurrentUser();
            return 0;
        }
        if (arguments.Contains("--clear-user-settings", StringComparer.OrdinalIgnoreCase))
        {
            Installation.ClearCurrentUserSettings();
            return 0;
        }
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            return 0;
        }

        try
        {
            using var window = new PulseWindow();
            return window.Run();
        }
        catch (Exception exception)
        {
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlanceBar-error.log"),
                $"{DateTimeOffset.Now:O} {exception}\r\n");
            return 1;
        }
    }
}

internal static class Startup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GlanceBar";

    internal static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    internal static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot locate GlanceBar.exe.");
            key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    internal static bool SystemUsesLightTheme()
    {
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    private const string SettingsKeyPath = @"Software\GlanceBar";
    private const string PositionValueName = "TaskbarPosition";

    internal static int? GetTaskbarPosition()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: false);
        return key?.GetValue(PositionValueName) is int value
            ? Math.Clamp(value, 0, 10_000)
            : null;
    }

    internal static void SetTaskbarPosition(int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
        key.SetValue(PositionValueName, Math.Clamp(value, 0, 10_000), RegistryValueKind.DWord);
    }
}
