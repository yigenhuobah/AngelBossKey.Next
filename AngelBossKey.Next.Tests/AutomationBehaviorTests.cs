using AngelBossKey.Next.Win32;

namespace AngelBossKey.Next.Tests;

public sealed class AutomationBehaviorTests
{
    [Fact]
    public void TriggerDebouncer_AppliesCooldownAtTheBoundary()
    {
        var debouncer = new TriggerDebouncer();

        Assert.True(debouncer.TryEnter(1_000, 1_000));
        Assert.False(debouncer.TryEnter(1_999, 1_000));
        Assert.True(debouncer.TryEnter(2_000, 1_000));
    }

    [Fact]
    public async Task PrivacyDesktop_ReturnWhileInactiveIsNonDestructive()
    {
        using var desktop = new PrivacyDesktopService();

        var result = await desktop.ReturnAsync();

        Assert.True(result.Success);
        Assert.False(desktop.IsActive);
    }
}
