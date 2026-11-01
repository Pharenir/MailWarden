using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MailWarden.Presentation;

/// <summary>
/// Base class of the view models: raises <see cref="PropertyChanged"/>.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/>.</summary>
    /// <param name="propertyName">Name of the changed property; the caller's name by default.</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>Stores the value and raises <see cref="PropertyChanged"/> when it differs from the current one.</summary>
    /// <typeparam name="TValue">Type of the property.</typeparam>
    /// <param name="field">Backing field.</param>
    /// <param name="value">New value.</param>
    /// <param name="propertyName">Name of the property; the caller's name by default.</param>
    /// <returns>True when the value changed.</returns>
    protected bool SetProperty<TValue>(ref TValue field, TValue value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<TValue>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
