using AngelBossKey.Next.Core.Models;

namespace AngelBossKey.Next.Core.Services;

public static class TargetRuleMatcher
{
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }

    public static bool Matches(WindowInfo window, IEnumerable<TargetRule> targets)
    {
        var windowPath = NormalizePath(window.ExecutablePath);
        return targets.Any(target =>
            target.Enabled &&
            string.Equals(
                NormalizePath(target.ExecutablePath),
                windowPath,
                StringComparison.OrdinalIgnoreCase));
    }
}
