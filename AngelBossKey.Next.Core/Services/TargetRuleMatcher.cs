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
                StringComparison.OrdinalIgnoreCase) &&
            MatchesTitle(window.Title, target));
    }

    public static bool MatchesPath(string executablePath, TargetRule target) =>
        string.Equals(
            NormalizePath(target.ExecutablePath),
            NormalizePath(executablePath),
            StringComparison.OrdinalIgnoreCase);

    private static bool MatchesTitle(string title, TargetRule target)
    {
        if (!string.IsNullOrWhiteSpace(target.TitleIncludes) &&
            !title.Contains(target.TitleIncludes.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(target.TitleExcludes) ||
            !title.Contains(target.TitleExcludes.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
