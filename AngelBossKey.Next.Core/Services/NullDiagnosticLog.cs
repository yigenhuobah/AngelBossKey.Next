using AngelBossKey.Next.Core.Abstractions;

namespace AngelBossKey.Next.Core.Services;

public sealed class NullDiagnosticLog : IDiagnosticLog
{
    public static NullDiagnosticLog Instance { get; } = new();

    private NullDiagnosticLog()
    {
    }

    public void Info(string eventName, string details)
    {
    }

    public void Warning(string eventName, string details)
    {
    }

    public void LogError(string eventName, Exception exception)
    {
    }
}
