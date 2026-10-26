using System.Diagnostics;
using Microsoft.Win32;

namespace GlanceBar;

internal static class Installation
{
    internal static void ConfigureCurrentUser()
    {
        MigrateCurrentUser();
        Startup.SetEnabled(true);
    }

    internal static void MigrateCurrentUser()
    {
        MigrateLegacyName("TaskbarPulse");
        MigrateLegacyName("GlanceBar");
    }

    private static void MigrateLegacyName(string name)
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacyExecutable = Path.Combine(localData, "Programs", name, name + ".exe");
        RemoveLegacyUserInstallation(name, legacyExecutable);

        using var runKey = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (runKey?.GetValue(name) is string command &&
            command.Contains(legacyExecutable, StringComparison.OrdinalIgnoreCase))
        {
            runKey.DeleteValue(name, throwOnMissingValue: false);
        }

        if (name == "TaskbarPulse")
        {
            using (var oldSettings = Registry.CurrentUser.OpenSubKey(@"Software\TaskbarPulse"))
            using (var newSettings = Registry.CurrentUser.CreateSubKey(@"Software\GlanceBar", writable: true))
            {
                if (newSettings.GetValue("TaskbarPosition") is null &&
                    oldSettings?.GetValue("TaskbarPosition") is int position)
                {
                    newSettings.SetValue("TaskbarPosition", position, RegistryValueKind.DWord);
                }
            }
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\TaskbarPulse", throwOnMissingSubKey: false);
        }
    }

    internal static void ClearCurrentUserSettings()
    {
        MigrateCurrentUser();
        Startup.SetEnabled(false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\GlanceBar", throwOnMissingSubKey: false);
    }

    private static void RemoveLegacyUserInstallation(string name, string legacyExecutable)
    {
        if (string.Equals(Environment.ProcessPath, legacyExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                try
                {
                    if (string.Equals(process.MainModule?.FileName, legacyExecutable, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The old UI can finish exiting while Setup is migrating it.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Setup stops the elevated legacy task; never elevate the user UI.
                }
            }
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DeleteLegacyDirectory(Path.Combine(localData, "Programs", name));
        DeleteLegacyDirectory(Path.Combine(localData, name));
    }

    private static void DeleteLegacyDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
