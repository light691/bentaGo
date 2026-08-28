using BentaGo.App.Features.Login;
using BentaGo.App.Features.Sales;
using BentaGo.App.Mvvm;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Validation-state tests (bg-004-synthesis §8.1, §10, docs/05 "validation
/// state"): SetFieldError/clear semantics, SetValidatedField, HasErrors, and
/// the exact docs/03 corrective copy for a missed scan. Headless.
/// </summary>
public sealed class ValidationStateTests : IDisposable
{
    private readonly AppShellTestHost _host;

    public ValidationStateTests()
    {
        _host = new AppShellTestHost(seedAdmin: true);
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public void SetFieldErrorFlipsHasErrorsAndGetErrors()
    {
        var probe = new ProbeValidatable();
        Assert.False(probe.HasErrors);

        probe.SetError("Field", "Something is wrong.");
        Assert.True(probe.HasErrors);
        Assert.Equal(new[] { "Something is wrong." }, probe.GetErrors("Field").Cast<string>());
        Assert.Equal(new[] { "Something is wrong." }, probe.GetErrors(null).Cast<string>());
    }

    [Fact]
    public void ClearingAnErrorRestoresStateAndRaisesErrorsChanged()
    {
        var probe = new ProbeValidatable();
        var errorsChanged = new List<string?>();
        probe.ErrorsChanged += (_, e) => errorsChanged.Add(e.PropertyName);

        probe.SetError("Field", "Something is wrong.");
        Assert.True(probe.HasErrors);

        probe.SetError("Field", null);
        Assert.False(probe.HasErrors);
        Assert.Empty(probe.GetErrors("Field").Cast<string>());
        Assert.Contains("Field", errorsChanged);
    }

    [Fact]
    public void SetValidatedFieldRunsTheValidator()
    {
        var probe = new ProbeValidatable();
        probe.SomeValue = "ok";
        Assert.False(probe.HasErrors);

        probe.SomeValue = "bad";
        Assert.True(probe.HasErrors);
        Assert.Equal(new[] { "The value must say ok." }, probe.GetErrors(nameof(ProbeValidatable.SomeValue)).Cast<string>());

        probe.SomeValue = "ok";
        Assert.False(probe.HasErrors);
    }

    [Fact]
    public void MissedScanCarriesExactCorrectiveCopy()
    {
        var sales = _host.Services.GetRequiredService<SalesViewModel>();
        sales.ScanBoxText = "NOT-A-REAL-SKU";
        sales.ScanCommand.Execute(null);

        Assert.Equal(SalesViewModel.ScanNotFoundMessage, sales.ScanError);
        Assert.True(sales.HasErrors);
        Assert.Equal(new[] { SalesViewModel.ScanNotFoundMessage }, sales.GetErrors(nameof(SalesViewModel.ScanBoxText)).Cast<string>());
        Assert.Equal("NOT-A-REAL-SKU", sales.ScanBoxText, ignoreCase: true); // typed value preserved
    }

    [Fact]
    public void BlankLoginFieldsGetCorrectiveFieldCopyOnSubmit()
    {
        var login = _host.Services.GetRequiredService<LoginViewModel>();
        login.ValidateSubmit();

        Assert.True(login.HasErrors);
        Assert.Equal(new[] { "Username is required." }, login.GetErrors(nameof(LoginViewModel.Username)).Cast<string>());
        Assert.Equal(new[] { "Password is required." }, login.GetErrors(nameof(LoginViewModel.Password)).Cast<string>());
    }

    [Fact]
    public void AuthenticationFailureCopyStaysGenericAndFormLevel()
    {
        var login = _host.Services.GetRequiredService<LoginViewModel>();
        login.Username = "admin";
        login.Password = "definitely-wrong";
        login.LoginCommand.Execute(null);
        Thread.Sleep(300);

        Assert.Equal("Sign in failed. Check your username and password.", login.ErrorMessage);
        Assert.False(login.HasErrors); // the failure is form-level, never field-level
        Assert.DoesNotContain("definitely-wrong", login.ErrorMessage);
    }

    /// <summary>Minimal ValidatableViewModelBase probe for direct base-contract tests.</summary>
    private sealed class ProbeValidatable : ValidatableViewModelBase
    {
        private string _someValue = "";

        public void SetError(string property, string? message) => SetFieldError(property, message);

        public string SomeValue
        {
            get => _someValue;
            set => SetValidatedField(ref _someValue, value, v => v == "ok" ? null : "The value must say ok.", nameof(SomeValue));
        }
    }
}