using AngelBossKey.Next.App.Infrastructure;
using AngelBossKey.Next.Core.Models;
using System.Windows.Media;

namespace AngelBossKey.Next.App.ViewModels;

public sealed class TargetRowViewModel : ObservableObject
{
    private bool _enabled;

    public TargetRowViewModel(TargetRule rule)
    {
        Id = rule.Id;
        DisplayName = rule.DisplayName;
        ExecutablePath = rule.ExecutablePath;
        _enabled = rule.Enabled;
        Icon = IconLoader.LoadFromExecutable(rule.ExecutablePath);
    }

    public Guid Id { get; }
    public string DisplayName { get; }
    public string ExecutablePath { get; }
    public ImageSource Icon { get; }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public TargetRule ToModel() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        ExecutablePath = ExecutablePath,
        Enabled = Enabled
    };
}
