using AngelBossKey.Next.Core.Abstractions;
using Microsoft.Win32;

namespace AngelBossKey.Next.Win32;

public sealed class StartupRegistration : IStartupRegistration
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AngelBossKey.Next";

    public bool IsEnabledFor(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
        return string.Equals(
            key?.GetValue(ValueName) as string,
            BuildCommand(executablePath),
            StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, true);
        if (enabled)
        {
            key.SetValue(ValueName, BuildCommand(executablePath), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }

    internal static string BuildCommand(string executablePath) => $"\"{executablePath}\" --background";
}
