using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AutoCompressor.Desktop.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Tell bindings that every property may have changed.</summary>
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>An ObservableCollection that can change many items with a single notification.</summary>
public sealed class RangeCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        RaiseReset();
    }

    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;
        // A handful of rows is cheaper to add one by one than to make the view rebuild everything.
        if (items.Count <= 40)
        {
            for (int i = 0; i < items.Count; i++) Insert(index + i, items[i]);
            return;
        }
        ((List<T>)Items).InsertRange(index, items);
        RaiseReset();
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0) return;
        if (count <= 40)
        {
            for (int i = 0; i < count; i++) RemoveAt(index);
            return;
        }
        ((List<T>)Items).RemoveRange(index, count);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
