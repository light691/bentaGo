using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using BentaGo.App.Data;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Display;
using BentaGo.Core.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Features.Admin;

/// <summary>
/// User management (BG-005d, docs/05 BG-005): list accounts, create a user
/// (username/password/role), activate/deactivate, change a password (admin
/// for anyone, anyone for themselves). The screen is Admin-only: the rail
/// entry is role-gated, the shell's AdminNavigateCommand is role-gated, AND
/// this view model re-checks the session actor's role through
/// <see cref="RoleExtensions.CanManageUsers"/> on every refresh (defense in
/// depth — never trust navigation alone). Every write goes through
/// <see cref="IUserAccountService"/>, which re-reads the session actor and
/// rejects a command whose By does not equal the signed-in user (F8
/// AUTH_ACTOR_MISMATCH): the view model passes IActorContext.CurrentUserId as
/// By and never constructs a command without a signed-in session.
///
/// The signed-in user's own row is read-only except password change (the
/// deactivate action is hidden for it — the service also rejects self
/// deactivation with AUTH_CANNOT_DEACTIVATE_SELF).
/// </summary>
public sealed class UserManagementViewModel : ValidatableViewModelBase, IRetainableScreen
{
    private readonly IUserAccountService _accounts;
    private readonly AdminReadService _reads;
    private readonly IActorContext _actorContext;

    private IReadOnlyList<UserRow> _users = Array.Empty<UserRow>();
    private UserRow? _selectedRow;
    private string _newUsername = "";
    private string _newPassword = "";
    private Role _selectedRole = Role.Peddler;
    private string _changePasswordNew = "";
    private string? _errorMessage;
    private string? _statusMessage;
    private bool _isBusy;

    public UserManagementViewModel(IServiceProvider services)
    {
        _accounts = services.GetRequiredService<IUserAccountService>();
        _reads = services.GetRequiredService<AdminReadService>();
        _actorContext = services.GetRequiredService<IActorContext>();

        RoleChips = RoleChip.CreateAll(SelectRole, _selectedRole);

        RefreshCommand = new RelayCommand(async _ => await RunAsync(RefreshUsersAsync,
            "The account list could not be loaded. Try again."), _ => !IsBusy);
        CreateUserCommand = new RelayCommand(async _ => await CreateUserAsync(), _ => CanCreate);
        SetActiveCommand = new RelayCommand(async parameter => await SetActiveAsync((UserRow)parameter!), _ => !IsBusy);
        ChangePasswordCommand = new RelayCommand(async _ => await ChangePasswordAsync(), _ => CanChangePassword);

        // Boot load: populates the list without blocking the shell (the view
        // binds when the async read lands; refresh is F5 / the action bar).
        _ = RunAsync(RefreshUsersAsync, "The account list could not be loaded. Try again.");
    }

    public IReadOnlyList<RoleChip> RoleChips { get; }

    public ICommand RefreshCommand { get; }

    public ICommand CreateUserCommand { get; }

    public ICommand SetActiveCommand { get; }

    public ICommand ChangePasswordCommand { get; }

    public IReadOnlyList<UserRow> Users
    {
        get => _users;
        private set => SetProperty(ref _users, value);
    }

    public UserRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                _changePasswordNew = "";
                OnPropertyChanged(nameof(CanChangePassword));
                OnPropertyChanged(nameof(SelectedTargetText));
                (ChangePasswordCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public string SelectedTargetText => SelectedRow is null ? "Select a user" : $"Change password for {SelectedRow.Username}";

    public string NewUsername
    {
        get => _newUsername;
        set
        {
            if (SetValidatedField(ref _newUsername, value, v => string.IsNullOrWhiteSpace(v) ? "Username is required." : null))
            {
                NotifyCreateGate();
            }
        }
    }

    public string NewPassword
    {
        get => _newPassword;
        set
        {
            if (SetValidatedField(ref _newPassword, value, v => string.IsNullOrEmpty(v) ? "Password is required." : null))
            {
                NotifyCreateGate();
            }
        }
    }

    public string ChangePasswordNew
    {
        get => _changePasswordNew;
        set
        {
            if (SetProperty(ref _changePasswordNew, value))
            {
                OnPropertyChanged(nameof(CanChangePassword));
                (ChangePasswordCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCreateGate();
                OnPropertyChanged(nameof(CanChangePassword));
                (RefreshCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (SetActiveCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanCreate => !IsBusy && !string.IsNullOrWhiteSpace(NewUsername) && !string.IsNullOrEmpty(NewPassword);

    public bool CanChangePassword => !IsBusy && SelectedRow is not null && !string.IsNullOrEmpty(ChangePasswordNew);

    private async Task RefreshUsersAsync()
    {
        ErrorMessage = null;
        User? session = await _reads.GetSessionUserAsync();
        if (session?.Role.CanManageUsers() != true)
        {
            ErrorMessage = "Only an admin can manage user accounts.";
            Users = Array.Empty<UserRow>();
            return;
        }

        IReadOnlyList<User> users = await _reads.ListUsersAsync();
        Users = users.Select(user => ToRow(user, session.Id)).ToArray();
    }

    private async Task CreateUserAsync() => await RunAsync(async () =>
    {
        ValidateCreate();
        if (HasErrors)
        {
            return;
        }

        UserId? by = _actorContext.CurrentUserId;
        if (by is null)
        {
            ErrorMessage = "Sign in again and retry.";
            return;
        }

        Result<User> result = await _accounts.CreateUserAsync(new CreateUserCommand(
            new UserId(Guid.NewGuid()),
            NewUsername.Trim(),
            NewPassword,
            _selectedRole,
            PeddlerLink: null, // peddler linking is a later slice; the schema column exists (BG-005a)
            by.Value,
            DateTimeOffset.UtcNow));

        if (result.IsFailure)
        {
            ErrorMessage = Friendly(result.Errors[0]);
            return;
        }

        NewUsername = "";
        NewPassword = "";
        ClearFieldErrors();
        StatusMessage = "User created.";
        ErrorMessage = null;
        await RefreshUsersAsync();
    }, "The user could not be created. Try again.");

    private async Task SetActiveAsync(UserRow row) => await RunAsync(async () =>
    {
        UserId? by = _actorContext.CurrentUserId;
        if (by is null)
        {
            ErrorMessage = "Sign in again and retry.";
            return;
        }

        Result<User> result = await _accounts.SetUserActiveAsync(new SetUserActiveCommand(
            row.Id,
            !row.IsActive,
            by.Value,
            DateTimeOffset.UtcNow));

        if (result.IsFailure)
        {
            ErrorMessage = Friendly(result.Errors[0]);
            return;
        }

        StatusMessage = row.IsActive ? $"{row.Username} deactivated." : $"{row.Username} activated.";
        ErrorMessage = null;
        await RefreshUsersAsync();
    }, "The account state could not be updated. Try again.");

    private async Task ChangePasswordAsync() => await RunAsync(async () =>
    {
        UserRow? target = SelectedRow;
        if (target is null || string.IsNullOrEmpty(ChangePasswordNew))
        {
            return; // gated by CanChangePassword; defensive no-op
        }

        UserId? by = _actorContext.CurrentUserId;
        if (by is null)
        {
            ErrorMessage = "Sign in again and retry.";
            return;
        }

        Result<User> result = await _accounts.ChangePasswordAsync(new ChangePasswordCommand(
            target.Id,
            ChangePasswordNew,
            by.Value,
            DateTimeOffset.UtcNow));

        if (result.IsFailure)
        {
            ErrorMessage = Friendly(result.Errors[0]);
            return;
        }

        ChangePasswordNew = "";
        ClearFieldErrors();
        StatusMessage = "Password updated.";
        ErrorMessage = null;
    }, "The password could not be changed. Try again.");

    /// <summary>Submit-time field validation (bg-004-synthesis §8.1): corrective copy.</summary>
    private void ValidateCreate()
    {
        SetFieldError(nameof(NewUsername), string.IsNullOrWhiteSpace(NewUsername) ? "Username is required." : null);
        SetFieldError(nameof(NewPassword), string.IsNullOrEmpty(NewPassword) ? "Password is required." : null);
    }

    private void SelectRole(Role role)
    {
        _selectedRole = role;
        foreach (RoleChip chip in RoleChips)
        {
            chip.IsSelected = chip.Role == role;
        }
    }

    private static UserRow ToRow(User user, UserId sessionId) => new(
        user.Id,
        user.Username,
        user.Role.ToString(),
        user.DeactivatedUtc is null ? "Active" : "Deactivated",
        user.DeactivatedUtc is null,
        user.Id == sessionId,
        user.LockedUntilUtc is not null,
        user.LockedUntilUtc is null ? "-" : "Locked until " + ManilaDisplay.DateTime(user.LockedUntilUtc.Value),
        ManilaDisplay.DateTime(user.CreatedUtc),
        user.DeactivatedUtc is null ? "Deactivate" : "Activate",
        user.Id != sessionId);

    private void NotifyCreateGate()
    {
        OnPropertyChanged(nameof(CanCreate));
        (CreateUserCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private async Task RunAsync(Func<Task> body, string failure)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            await body();
        }
        catch
        {
            ErrorMessage = failure;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Domain failure copy (error/validation conventions): generic, never
    /// echoes secrets or reveals account existence beyond what the action
    /// itself implies. Codes map to the docs' corrective-copy style.
    /// </summary>
    internal static string Friendly(DomainError error) => error.Code switch
    {
        "FORBIDDEN" => "You do not have permission to do that.",
        "AUTH_ACTOR_NOT_SIGNED_IN" or "AUTH_ACTOR_MISMATCH" or "AUTH_ACTOR_UNKNOWN" or "AUTH_ACTOR_DEACTIVATED" => "Sign in again and retry.",
        "DUP_USERNAME" => "That username is already in use. Choose another.",
        "AUTH_CANNOT_DEACTIVATE_SELF" => "You cannot deactivate your own account.",
        "AUTH_NEEDS_ADMIN" => "The last active admin cannot be deactivated.",
        "INVALID_USERNAME" => "Enter a username.",
        "INVALID_PASSWORD" => "Enter a password.",
        "USER_UNKNOWN" => "That user no longer exists. Refresh the list.",
        _ => "The action could not be completed. Try again.",
    };
}

/// <summary>
/// One account row (immutable snapshot; the list is refreshed after each
/// write). ToggleLabel/CanToggle drive the per-row activate/deactivate
/// action: the signed-in user's own row is read-only except password change.
/// </summary>
public sealed record UserRow(
    UserId Id,
    string Username,
    string RoleLabel,
    string StatusLabel,
    bool IsActive,
    bool IsSelf,
    bool IsLocked,
    string LockText,
    string CreatedText,
    string ToggleLabel,
    bool CanToggle);

/// <summary>One role chip in the create form (selectable, keyboard-first).</summary>
public sealed class RoleChip : ViewModelBase
{
    private bool _isSelected;

    public RoleChip(Role role, string label, bool isSelected, Action<Role> select)
    {
        Role = role;
        Label = label;
        IsSelected = isSelected;
        SelectCommand = new RelayCommand(_ => select(role));
    }

    public Role Role { get; }

    public string Label { get; }

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    internal static IReadOnlyList<RoleChip> CreateAll(Action<Role> select, Role selected) =>
        new RoleChip[]
        {
            new(Role.Peddler, "Peddler", selected == Role.Peddler, select),
            new(Role.Supervisor, "Supervisor", selected == Role.Supervisor, select),
            new(Role.Admin, "Admin", selected == Role.Admin, select),
        };
}
