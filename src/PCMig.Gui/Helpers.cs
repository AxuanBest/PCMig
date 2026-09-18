using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace PCMig.Gui;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not Visibility.Visible;
}

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly string _name;
    private bool _running;

    /// <summary>
    /// 命令内部异常的统一出口，由 App 在启动时挂上（写日志 + 写崩溃文件 + 弹可读提示）。
    /// 稳定性要点：Execute 是 async void，异常若漏出去就是整个程序崩溃；这里必须兜住。
    /// </summary>
    public static Action<string, Exception>? OnError;

    public RelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null, string? displayName = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _name = displayName ?? "命令";
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null, string? displayName = null)
        : this(p => { execute(p); return Task.CompletedTask; }, canExecute, displayName) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (_running) return;   // 防重入：连点两下不会跑两遍
        _running = true;
        try { await _execute(parameter); }
        catch (Exception ex)
        {
            try { OnError?.Invoke(_name, ex); }
            catch { /* 兜底本身再出错也不能崩 */ }
        }
        finally
        {
            _running = false;
            try { CommandManager.InvalidateRequerySuggested(); } catch { }
        }
    }
}
