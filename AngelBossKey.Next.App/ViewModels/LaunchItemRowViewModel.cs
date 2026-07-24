using AngelBossKey.Next.App.Infrastructure;
using AngelBossKey.Next.Core.Models;
using System.IO;

namespace AngelBossKey.Next.App.ViewModels;

public sealed class LaunchItemRowViewModel : ObservableObject
{
    private string _displayName;
    private string _executablePath;
    private string _arguments;
    private string _workingDirectory;
    private bool _enabled;
    private bool _isPathValid;

    public LaunchItemRowViewModel(WorkspaceLaunchItem item)
    {
        Id = item.Id;
        _displayName = item.DisplayName;
        _executablePath = item.ExecutablePath;
        _arguments = item.Arguments;
        _workingDirectory = item.WorkingDirectory;
        _enabled = item.Enabled;
        _isPathValid = File.Exists(item.ExecutablePath);
    }

    public Guid Id { get; }
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value ?? string.Empty); }
    public string ExecutablePath { get => _executablePath; set => SetProperty(ref _executablePath, value ?? string.Empty); }
    public string Arguments { get => _arguments; set => SetProperty(ref _arguments, value ?? string.Empty); }
    public string WorkingDirectory { get => _workingDirectory; set => SetProperty(ref _workingDirectory, value ?? string.Empty); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public bool IsPathValid => _isPathValid;
    public string StatusText => IsPathValid ? "可用" : "路径失效";

    public bool RefreshPathValidity()
    {
        var current = File.Exists(ExecutablePath);
        if (current == _isPathValid) return false;
        _isPathValid = current;
        OnPropertyChanged(nameof(IsPathValid));
        OnPropertyChanged(nameof(StatusText));
        return true;
    }

    public WorkspaceLaunchItem ToModel() => new()
    {
        Id = Id,
        DisplayName = string.IsNullOrWhiteSpace(DisplayName)
            ? Path.GetFileNameWithoutExtension(ExecutablePath)
            : DisplayName.Trim(),
        ExecutablePath = ExecutablePath.Trim(),
        Arguments = Arguments.Trim(),
        WorkingDirectory = WorkingDirectory.Trim(),
        Enabled = Enabled
    };
}
