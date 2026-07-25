using AngelBossKey.Next.Core.Models;

namespace AngelBossKey.Next.Win32;

internal static class PrivacyDesktopShellCoordinator
{
    internal static ShellPreparationResult Prepare(
        PrivacyDesktopShellMode requestedMode,
        Func<(bool Success, string Message)> ensureExplorer,
        Func<(bool Success, string Message)> ensureCompatibility,
        Action stopExplorer,
        Action stopCompatibility)
    {
        if (requestedMode == PrivacyDesktopShellMode.Compatibility)
        {
            stopExplorer();
            var compatible = ensureCompatibility();
            return new ShellPreparationResult(
                compatible.Success,
                PrivacyDesktopShellMode.Compatibility,
                compatible.Message,
                UsedFallback: false);
        }

        stopCompatibility();
        var explorer = ensureExplorer();
        if (explorer.Success)
        {
            return new ShellPreparationResult(
                true,
                PrivacyDesktopShellMode.FullExplorer,
                explorer.Message,
                UsedFallback: false);
        }

        var fallback = ensureCompatibility();
        var message = fallback.Success
            ? $"{explorer.Message} 已自动回退到兼容轻量桌面。"
            : $"{explorer.Message} 兼容轻量桌面也未能启动：{fallback.Message}";
        return new ShellPreparationResult(
            fallback.Success,
            PrivacyDesktopShellMode.Compatibility,
            message,
            UsedFallback: true);
    }

    internal static (bool Success, string Message) EnsureExisting(
        PrivacyDesktopShellMode mode,
        Func<(bool Success, string Message)> ensureExplorer,
        Func<(bool Success, string Message)> ensureCompatibility) =>
        mode == PrivacyDesktopShellMode.FullExplorer
            ? ensureExplorer()
            : ensureCompatibility();
}

internal sealed record ShellPreparationResult(
    bool Success,
    PrivacyDesktopShellMode Mode,
    string Message,
    bool UsedFallback);
