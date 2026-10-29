using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClipNest.UI.Infrastructure;

/// <summary>
/// 极小的 INotifyPropertyChanged 基类。项目只有两三个视图模型，
/// 引入整套 MVVM 框架不划算。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
