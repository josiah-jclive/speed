using Microsoft.Win32;

namespace Speed.Core;

/// <summary>
/// "Start JSPEED when Windows starts": adds or removes JSPEED in the current user's
/// Run list (HKCU\...\CurrentVersion\Run), which needs no admin rights. Does nothing
/// on macOS, where Login Items in System Settings does the same job.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "JSPEED";

    /// <summary>
    /// Makes the Run entry match the setting. Called at launch too, so if JSPEED.exe has
    /// moved, the entry is pointed at the copy that's running now.
    /// </summary>
    public static void Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            var current = key.GetValue(ValueName) as string;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                var command = $"\"{exe}\"";
                if (current == command) return;
                key.SetValue(ValueName, command);
                AppLog.Info(current == null
                    ? $"JSPEED will start when Windows starts, from {exe}."
                    : $"Windows startup now opens this copy of JSPEED, at {exe}.");
            }
            else if (current != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                AppLog.Info("JSPEED will no longer start when Windows starts.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Couldn't change the Windows startup setting: {ex.Message}");
        }
    }
}
