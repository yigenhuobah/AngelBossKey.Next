using AngelBossKey.Next.Core.Models;
using System.ComponentModel;

namespace AngelBossKey.Next.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void LoadSelectedTargets()
    {
        foreach (var row in Targets) row.PropertyChanged -= OnTargetPropertyChanged;
        Targets.Clear();
        foreach (var target in _selectedScene.Targets) AddTargetRow(target);
    }

    private void SaveSelectedTargets() => _selectedScene.SetTargets(Targets.Select(target => target.ToModel()));

    private void SaveSelectedLaunchItems() =>
        _selectedScene.SetLaunchItems(LaunchItems.Select(item => item.ToModel()));

    private TargetRowViewModel AddTargetRow(TargetRule target)
    {
        var row = new TargetRowViewModel(target);
        row.PropertyChanged += OnTargetPropertyChanged;
        Targets.Add(row);
        return row;
    }

    private void RemoveTarget(object? parameter)
    {
        if (parameter is not TargetRowViewModel target) return;
        target.PropertyChanged -= OnTargetPropertyChanged;
        Targets.Remove(target);
        QueueRuleChanges($"已移除 {target.DisplayName}。");
        RefreshAllState();
    }

    private void MoveTarget(object? parameter, int offset)
    {
        if (parameter is not TargetRowViewModel target) return;
        var oldIndex = Targets.IndexOf(target);
        var newIndex = oldIndex + offset;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= Targets.Count) return;
        Targets.Move(oldIndex, newIndex);
        QueueRuleChanges("规则顺序已更新。");
    }

    private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressRuleChanges) return;
        if (e.PropertyName is nameof(TargetRowViewModel.Enabled) or
            nameof(TargetRowViewModel.TemporarilyExcluded) or
            nameof(TargetRowViewModel.TitleIncludes) or
            nameof(TargetRowViewModel.TitleExcludes) or
            nameof(TargetRowViewModel.MuteWhenHidden))
        {
            QueueRuleChanges();
            RefreshAllState();
        }
    }
}
