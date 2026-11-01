using System.Windows.Input;

namespace MailWarden.Presentation;

/// <summary>
/// <see cref="ICommand"/> that hands its parameter to a delegate.
/// </summary>
public sealed class ParameterRelayCommand : ICommand
{
    private readonly Action<object?> execute;
    private readonly Func<object?, bool> canExecute;

    /// <summary>Creates the command.</summary>
    /// <param name="execute">Called by <see cref="Execute"/>.</param>
    /// <param name="canExecute">Answers <see cref="CanExecute"/>.</param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    public ParameterRelayCommand(Action<object?> execute, Func<object?, bool> canExecute)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => canExecute(parameter);

    /// <inheritdoc />
    public void Execute(object? parameter) => execute(parameter);

    /// <summary>Tells bound controls to ask <see cref="CanExecute"/> again.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
