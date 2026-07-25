using AngelBossKey.Next.Core.Models;
using System.Globalization;
using System.Text;

namespace AngelBossKey.Next.Core.Services;

public sealed record DiagnosticReportSnapshot
{
    public string AppVersion { get; init; } = "unknown";
    public string OperatingSystem { get; init; } = "unknown";
    public string ProcessArchitecture { get; init; } = "unknown";
    public SceneMode SceneMode { get; init; }
    public PrivacyDesktopShellMode PrivacyShellMode { get; init; }
    public int SceneCount { get; init; }
    public int TargetRuleCount { get; init; }
    public int EnabledTargetRuleCount { get; init; }
    public bool WindowsHidden { get; init; }
    public bool PrivacyDesktopActive { get; init; }
    public bool PrivacyWorkspaceOpen { get; init; }
    public bool AudioActive { get; init; }
    public int PendingAudioRestores { get; init; }
    public bool ElevatedBrokerEnabled { get; init; }
}

public static class DiagnosticReportFormatter
{
    public static string Format(DiagnosticReportSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var builder = new StringBuilder();
        builder.AppendLine("AngelBossKey Next diagnostic report");
        AppendLine(builder, $"GeneratedUtc: {DateTimeOffset.UtcNow:O}");
        AppendLine(builder, $"AppVersion: {SafeValue(snapshot.AppVersion)}");
        AppendLine(builder, $"OperatingSystem: {SafeValue(snapshot.OperatingSystem)}");
        AppendLine(builder, $"ProcessArchitecture: {SafeValue(snapshot.ProcessArchitecture)}");
        AppendLine(builder, $"SceneMode: {snapshot.SceneMode}");
        AppendLine(builder, $"PrivacyShellMode: {snapshot.PrivacyShellMode}");
        AppendLine(builder, $"SceneCount: {Math.Max(0, snapshot.SceneCount)}");
        AppendLine(builder, $"TargetRules: total={Math.Max(0, snapshot.TargetRuleCount)}; enabled={Math.Max(0, snapshot.EnabledTargetRuleCount)}");
        AppendLine(builder, $"WindowsHidden: {snapshot.WindowsHidden}");
        AppendLine(builder, $"PrivacyDesktopActive: {snapshot.PrivacyDesktopActive}");
        AppendLine(builder, $"PrivacyWorkspaceOpen: {snapshot.PrivacyWorkspaceOpen}");
        AppendLine(builder, $"AudioActive: {snapshot.AudioActive}");
        AppendLine(builder, $"PendingAudioRestores: {Math.Max(0, snapshot.PendingAudioRestores)}");
        AppendLine(builder, $"ElevatedBrokerEnabled: {snapshot.ElevatedBrokerEnabled}");
        builder.Append("Privacy: excludes window titles, executable paths, launch arguments, and log contents.");
        return builder.ToString();
    }

    private static string SafeValue(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 128 ? normalized : normalized[..128];
    }

    private static void AppendLine(StringBuilder builder, FormattableString value)
    {
        builder.AppendFormat(CultureInfo.InvariantCulture, value.Format, value.GetArguments());
        builder.AppendLine();
    }
}
