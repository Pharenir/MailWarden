using System.Windows.Input;

namespace MailWarden.Presentation;

/// <summary>
/// <see cref="ICommand"/> that calls a delegate.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action execute;
    private readonly Func<bool>? canExecute;

    /// <summary>Creates the command.</summary>
    /// <param name="execute">Called by <see cref="Execute"/>.</param>
    /// <param name="canExecute">Answers <see cref="CanExecute"/>; null means always.</param>
    /// <exception cref="ArgumentNullException"><paramref name="execute"/> is null.</exception>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => execute();

    /// <summary>Tells bound controls to ask <see cref="CanExecute"/> again.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
