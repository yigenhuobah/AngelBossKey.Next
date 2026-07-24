using AngelBossKey.Next.App.Infrastructure;
using AngelBossKey.Next.Core.Models;
using System.IO;
using System.Windows.Media;

namespace AngelBossKey.Next.App.ViewModels;

public sealed class TargetRowViewModel : ObservableObject
{
    private bool _enabled;
    private bool _temporarilyExcluded;
    private string _titleIncludes;
    private string _titleExcludes;
    private bool _isPathValid;
    private bool _muteWhenHidden;

    public TargetRowViewModel(TargetRule rule)
    {
        Id = rule.Id;
        DisplayName = rule.DisplayName;
        ExecutablePath = rule.ExecutablePath;
        _enabled = rule.Enabled;
        _titleIncludes = rule.TitleIncludes;
        _titleExcludes = rule.TitleExcludes;
        _isPathValid = File.Exists(rule.ExecutablePath);
        _muteWhenHidden = rule.MuteWhenHidden;
        Icon = IconLoader.LoadFromExecutable(rule.ExecutablePath);
    }

    public Guid Id { get; }
    public string DisplayName { get; }
    public string ExecutablePath { get; }
    public ImageSource Icon { get; }
    public bool IsPathValid => _isPathValid;
    public bool EffectiveEnabled => Enabled && !TemporarilyExcluded && IsPathValid;
    public string StatusText => !IsPathValid
        ? "路径失效"
        : TemporarilyExcluded
            ? "本次运行已排除"
            : Enabled ? "已启用" : "已停用";

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(EffectiveEnabled));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool TemporarilyExcluded
    {
        get => _temporarilyExcluded;
        set
        {
            if (SetProperty(ref _temporarilyExcluded, value))
            {
                OnPropertyChanged(nameof(EffectiveEnabled));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string TitleIncludes
    {
        get => _titleIncludes;
        set => SetProperty(ref _titleIncludes, value ?? string.Empty);
    }

    public string TitleExcludes
    {
        get => _titleExcludes;
        set => SetProperty(ref _titleExcludes, value ?? string.Empty);
    }

    public bool MuteWhenHidden
    {
        get => _muteWhenHidden;
        set => SetProperty(ref _muteWhenHidden, value);
    }

    public TargetRule ToModel() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        ExecutablePath = ExecutablePath,
        Enabled = Enabled,
        TitleIncludes = TitleIncludes.Trim(),
        TitleExcludes = TitleExcludes.Trim(),
        MuteWhenHidden = MuteWhenHidden
    };

    public TargetRule ToEffectiveModel() => ToModel() with { Enabled = EffectiveEnabled };

    public bool RefreshPathValidity()
    {
        var isValid = File.Exists(ExecutablePath);
        if (isValid == _isPathValid)
        {
            return false;
        }

        _isPathValid = isValid;
        OnPropertyChanged(nameof(IsPathValid));
        OnPropertyChanged(nameof(EffectiveEnabled));
        OnPropertyChanged(nameof(StatusText));
        return true;
    }
}
