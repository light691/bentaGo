using System.IO;
using System.Windows.Threading;
using BentaGo.App;
using BentaGo.App.Dialogs;
using BentaGo.App.Dialogs.Confirm;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// STA real-dialog round trip (bg-004-synthesis §7.2, §10): Confirm closes
/// the modal with a typed result; Esc dismisses to null; focusAfterClose is
/// re-requested through IFocusService on both paths; and a second ShowAsync
/// from a non-top scope is refused while a dialog is open. This is the
/// unit-level foretaste of lane L6.
///
/// Modal-window mechanics: ShowDialog pushes its own nested dispatch frame,
/// so the driver ops (guard check, Confirm/Cancel) are queued on the
/// dispatcher BEFORE the pump that opens the modal; the modal loop itself
/// runs them and the close signal unwinds the frame.
/// </summary>
[Collection("AppShellSta")]
public sealed class DialogGuardAndFocusStaTests
{
    private readonly AppShellStaFixture _sta;

    public DialogGuardAndFocusStaTests(AppShellStaFixture sta) => _sta = sta;

    [Fact]
    public void ConfirmAndEscProduceTypedResultsAndRestoreFocus()
    {
        _sta.Run(() =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "bentago-dialog-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var services = BentaGoHost.Build(
                    development: true,
                    dataRootOverride: temp,
                    extra: appServices =>
                    {
                        ObservableObjectFactory.RegisterAppServices(appServices);
                        appServices.AddSingleton<IFocusService, RecordingFocusService>();
                    });
                var dialogs = services.GetRequiredService<IDialogService>();
                var focus = (RecordingFocusService)services.GetRequiredService<IFocusService>();

                // ---- Confirm path + top-dialog guard ----
                var confirm = services.GetRequiredService<ConfirmDialogViewModel>();
                confirm.Message = "Remove item?";
                Task<ConfirmDialogResult?>? confirmTask = null;
                bool guardThrew = false;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    confirmTask = dialogs.ShowAsync(confirm, focusAfterClose: "Home.NewSaleButton")));
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    var second = services.GetRequiredService<ConfirmDialogViewModel>();
                    second.Message = "second";
                    try
                    {
                        dialogs.ShowAsync(second).GetAwaiter().GetResult();
                    }
                    catch (InvalidOperationException)
                    {
                        guardThrew = true;
                    }

                    confirm.ConfirmCommand.Execute(null);
                }));

                WaitFor(() => confirmTask is { IsCompleted: true });
                Assert.True(guardThrew, "a second ShowAsync while a dialog is open must be refused");
                Assert.True(confirmTask!.Result!.Confirmed);
                Assert.False(dialogs.IsOpen);
                Assert.Contains("Home.NewSaleButton", focus.Requests);

                // ---- Esc path ----
                var confirm2 = services.GetRequiredService<ConfirmDialogViewModel>();
                confirm2.Message = "dismiss me";
                Task<ConfirmDialogResult?>? escTask = null;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    escTask = dialogs.ShowAsync(confirm2, focusAfterClose: "Stock.StockSearchBox")));
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    confirm2.CancelCommand.Execute(null)));

                WaitFor(() => escTask is { IsCompleted: true });
                Assert.Null(escTask!.Result);
                Assert.False(dialogs.IsOpen);
                Assert.Contains("Stock.StockSearchBox", focus.Requests);
            }
            finally
            {
                try
                {
                    Directory.Delete(temp, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        });
    }

    private void WaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("condition not met on the STA host");
            }

            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Thread.Sleep(10);
        }
    }
}
