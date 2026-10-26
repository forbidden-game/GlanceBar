using Microsoft.Win32;

namespace TaskbarPulse;

internal static class Program
{
    private const string MutexName = @"Local\TaskbarPulse.SingleInstance.6D9AA8A2";

    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments.Contains(NetworkHelper.HelperArgument, StringComparer.OrdinalIgnoreCase))
        {
            return NetworkHelper.Run();
        }
        if (arguments.Contains(NetworkHelper.InstallArgument, StringComparer.OrdinalIgnoreCase))
        {
            return NetworkHelper.Install();
        }
        if (arguments.Contains(NetworkHelper.UninstallArgument, StringComparer.OrdinalIgnoreCase))
        {
            return NetworkHelper.Uninstall();
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
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarPulse-error.log"),
                $"{DateTimeOffset.Now:O} {exception}\r\n");
            return 1;
        }
    }
}

internal static class Startup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskbarPulse";

    internal static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    internal static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open the current user's Run key.");

        if (enabled)
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot locate TaskbarPulse.exe.");
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

    private const string SettingsKeyPath = @"Software\TaskbarPulse";
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
