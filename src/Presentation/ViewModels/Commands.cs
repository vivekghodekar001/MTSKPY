using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Mskpy.Presentation.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(property); return true; }
}
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
    public void Refresh() => CommandRaise.Raise(CanExecuteChanged, this);
}
public sealed class AsyncCommand(Func<object?, CancellationToken, Task> execute, Action<Exception> onError, Func<object?, bool>? canExecute = null) : ICommand
{
    CancellationTokenSource? cancellation;
    public event EventHandler? CanExecuteChanged;
    public bool IsRunning => cancellation is not null;
    public bool CanExecute(object? parameter) => !IsRunning && (canExecute?.Invoke(parameter) ?? true);
    public async void Execute(object? parameter) => await ExecuteAsync(parameter);
    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter)) return;
        using var source = new CancellationTokenSource(); cancellation = source; Refresh();
        try { await execute(parameter, source.Token); }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception ex) { onError(ex); }
        finally { cancellation = null; Refresh(); }
    }
    public void Cancel() => cancellation?.Cancel();
    public void Refresh() => CommandRaise.Raise(CanExecuteChanged, this);
}

/// <summary>
/// Raising CanExecuteChanged makes WPF re-query the bound button, and that
/// touches the button's Command property — a UI-thread object. An await that
/// resumes on a background thread (no SynchronizationContext, or a test with no
/// dispatcher) therefore crashed the process with
/// "the calling thread cannot access this object because a different thread
/// owns it". The raise is marshalled onto the UI thread when there is one.
/// </summary>
internal static class CommandRaise
{
    public static void Raise(EventHandler? handler, object sender)
    {
        if (handler == null) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            handler(sender, EventArgs.Empty);
            return;
        }
        dispatcher.BeginInvoke(new Action(() => handler(sender, EventArgs.Empty)));
    }
}
