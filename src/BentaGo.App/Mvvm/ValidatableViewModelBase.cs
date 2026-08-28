using System.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BentaGo.App.Mvvm;

/// <summary>
/// View model base with WPF-native validation (bg-004-synthesis §8.1):
/// INotifyDataErrorInfo is the contract WPF's default
/// ValidatesOnNotifyDataErrors=true already reacts to, so the red border +
/// error template appear with zero per-field view glue.
/// </summary>
public abstract class ValidatableViewModelBase : ViewModelBase, INotifyDataErrorInfo
{
    private readonly Dictionary<string, string?> _errors = new(StringComparer.Ordinal);

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    /// <summary>Any field currently carries an error.</summary>
    public bool HasErrors => _errors.Values.Any(message => message is not null);

    public IEnumerable GetErrors(string? propertyName)
    {
        if (propertyName is null)
        {
            return _errors.Values.Where(message => message is not null).ToArray()!;
        }

        return _errors.TryGetValue(propertyName, out string? message) && message is not null
            ? new[] { message }
            : Array.Empty<string>();
    }

    /// <summary>
    /// Sets/clears one field error; null clears. Raises ErrorsChanged and
    /// re-raises HasErrors when the overall state changes.
    /// </summary>
    protected void SetFieldError(string propertyName, string? message)
    {
        bool hadErrors = HasErrors;
        string? previous = _errors.TryGetValue(propertyName, out string? current) ? current : null;
        if (string.Equals(previous, message, StringComparison.Ordinal))
        {
            return;
        }

        if (message is null)
        {
            _errors.Remove(propertyName);
        }
        else
        {
            _errors[propertyName] = message;
        }

        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
        bool hasErrors = HasErrors;
        if (hasErrors != hadErrors)
        {
            OnPropertyChanged(nameof(HasErrors));
        }
    }

    /// <summary>
    /// Combined setter + validator: mutates via SetProperty, then applies
    /// validate(value) to SetFieldError(propertyName, ...). Errors drive
    /// command enablement through HasErrors.
    /// </summary>
    protected bool SetValidatedField<T>(
        ref T field,
        T value,
        Func<T, string?> validate,
        [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        SetFieldError(propertyName!, validate(value));
        return true;
    }

    protected void ClearFieldErrors()
    {
        foreach (string key in _errors.Keys.ToArray())
        {
            SetFieldError(key, null);
        }
    }
}