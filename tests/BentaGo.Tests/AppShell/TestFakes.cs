using BentaGo.App.Dialogs;
using BentaGo.App.Navigation;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Recording IFocusService: captures every symbolic focus request so the
/// headless tests can assert payloads without any UI (bg-004-synthesis §10).
/// Registered over FocusCoordinator in test hosts via the last-registration
/// rule of Microsoft.Extensions.DependencyInjection.
/// </summary>
internal sealed class RecordingFocusService : IFocusService
{
    public event EventHandler<FocusRequest>? Requested;

    public List<string> Requests { get; } = new();

    public void Request(string elementName)
    {
        Requests.Add(elementName);
        Requested?.Invoke(this, new FocusRequest(elementName));
    }
}

/// <summary>
/// Fake dialog service: never opens a window; returns a canned result and
/// records the caller's view model + focusAfterClose (dialog contract tests,
/// bg-004-synthesis §10 "dialog results").
/// </summary>
internal sealed class FakeDialogService : IDialogService
{
    public List<(Type ResultType, object ViewModel, string? FocusAfterClose)> Shows { get; } = new();

    public object? CannedResult { get; set; }

    public bool IsOpen { get; set; }

    public Task<TResult?> ShowAsync<TResult>(DialogViewModelBase<TResult> viewModel, string? focusAfterClose = null)
        where TResult : class
    {
        Shows.Add((typeof(TResult), (object)viewModel, focusAfterClose));
        return Task.FromResult((TResult?)CannedResult);
    }
}
