using System.Windows.Input;

namespace PCMig.WinUI.Presentation;

/// <summary>
/// WinUI 侧的 ICommand 实现 —— 语义照搬 <c>src\PCMig.Gui\Helpers.cs</c> 的 RelayCommand：
///   · 防重入：Execute 是 async void，连点两次不会跑两遍；
///   · 内部异常统一经 <see cref="OnError"/> 出口，绝不外溢成进程崩溃；
///   · 支持同步与异步两种 delegate。
///
/// 与 WPF 版的唯一差异（有意为之）：WPF 的 <c>CanExecuteChanged</c> 挂在
/// <c>CommandManager.RequerySuggested</c>（WPF 专有类型），WinUI 没有 CommandManager，
/// 因此改为**显式**触发：状态变化后由 ViewModel 调 <see cref="RaiseCanExecuteChanged"/>。
/// 本文件不引用任何 WPF 类型。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly string _name;
    private bool _running;

    /// <summary>
    /// 命令内部异常的统一出口，由 App 在启动时挂上（写日志 + 可读提示）。
    /// 稳定性要点：Execute 是 async void，异常若漏出去就是整个程序崩溃；这里必须兜住。
    /// </summary>
    public static Action<string, Exception>? OnError;

    public RelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null, string? displayName = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _name = displayName ?? "命令";
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null, string? displayName = null)
        : this(p => { execute(p); return Task.CompletedTask; }, canExecute, displayName) { }

    /// <summary>无参命令的便捷构造（页面里最常见的形态）。</summary>
    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null, string? displayName = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute(), displayName) { }

    public event EventHandler? CanExecuteChanged;

    /// <summary>状态变化后由 ViewModel 显式调用（WinUI 无 CommandManager，不存在自动 requery）。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (_running) return;   // 防重入
        _running = true;
        RaiseCanExecuteChanged();   // 运行期间 CanExecute 变 false，UI 应同步禁用
        try { await _execute(parameter); }
        catch (Exception ex)
        {
            try { OnError?.Invoke(_name, ex); }
            catch { /* 兜底本身再出错也不能崩 */ }
        }
        finally
        {
            _running = false;
            try { RaiseCanExecuteChanged(); } catch { /* 订阅方异常不影响命令收尾 */ }
        }
    }
}