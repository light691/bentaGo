using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using BentaGo.App.Data;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Features.Admin;

/// <summary>
/// Admin overview / trips (BG-005d, docs/05 BG-005): a read-mostly,
/// PER-PEDDLER selection view over <see cref="ITripReadService"/> reads
/// (trips + on-hand) plus the today-sales fold (AdminReadService). The
/// peddler picker is one tab per peddler; the selected tab drives the
/// displayed card + on-hand table. The cross-peddler read API the docs
/// describe is a documented follow-up — the conservative per-peddler
/// selection is this slice's delivery.
///
/// Admin-only like every admin screen: role-gated at the rail, at the shell
/// command, and re-checked against the session actor's role here on every
/// load (RoleExtensions.CanManageUsers).
/// </summary>
public sealed class AdminOverviewViewModel : ViewModelBase, IRetainableScreen
{
    private readonly AdminReadService _reads;

    private IReadOnlyList<AdminOverviewTab> _tabs = Array.Empty<AdminOverviewTab>();
    private IReadOnlyDictionary<PeddlerId, PeddlerOverviewData> _overviews = new Dictionary<PeddlerId, PeddlerOverviewData>();
    private AdminOverviewTab? _selectedTab;
    private bool _isBusy;
    private string? _errorMessage;

    public AdminOverviewViewModel(IServiceProvider services)
    {
        _reads = services.GetRequiredService<AdminReadService>();

        RefreshCommand = new RelayCommand(async _ => await RunAsync(ReloadAsync,
            "The overview could not be loaded. Try again."), _ => !IsBusy);

        _ = RunAsync(ReloadAsync, "The overview could not be loaded. Try again.");
    }

    public ICommand RefreshCommand { get; }

    public IReadOnlyList<AdminOverviewTab> Tabs
    {
        get => _tabs;
        private set
        {
            if (SetProperty(ref _tabs, value))
            {
                OnPropertyChanged(nameof(HasPeddlers));
            }
        }
    }

    public bool HasPeddlers => Tabs.Count > 0;

    public AdminOverviewTab? SelectedTab
    {
        get => _selectedTab;
        private set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(SelectedOverview));
                OnPropertyChanged(nameof(SelectedOverviewName));
            }
        }
    }

    public PeddlerOverviewData? SelectedOverview =>
        SelectedTab is null ? null : _overviews.TryGetValue(SelectedTab.Id, out PeddlerOverviewData? data) ? data : null;

    public string SelectedOverviewName => SelectedOverview?.DisplayName ?? "No peddler selected";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                (RefreshCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    private async Task ReloadAsync()
    {
        ErrorMessage = null;
        User? session = await _reads.GetSessionUserAsync();
        if (session?.Role.CanManageUsers() != true)
        {
            ErrorMessage = "Only an admin can view the overview.";
            Tabs = Array.Empty<AdminOverviewTab>();
            _overviews = new Dictionary<PeddlerId, PeddlerOverviewData>();
            SelectedTab = null;
            return;
        }

        IReadOnlyList<PeddlerOverviewData> overviews = await _reads.GetPeddlerOverviewsAsync();
        _overviews = overviews.ToDictionary(o => o.Id);
        Tabs = overviews
            .Select((overview, index) => new AdminOverviewTab(overview.Id, overview.DisplayName, SelectTab)
            {
                IsSelected = index == 0,
            })
            .ToArray();
        SelectedTab = Tabs.FirstOrDefault();
    }

    private void SelectTab(AdminOverviewTab tab)
    {
        foreach (AdminOverviewTab candidate in Tabs)
        {
            candidate.IsSelected = ReferenceEquals(candidate, tab);
        }

        SelectedTab = tab;
    }

    private async Task RunAsync(Func<Task> body, string failure)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
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
}

/// <summary>
/// One peddler picker tab (keyboard-first: chips navigated with Tab, chosen
/// with Enter/Space — the same selection contract MethodTabButtonStyle binds).
/// </summary>
public sealed class AdminOverviewTab : ViewModelBase
{
    private bool _isSelected;
    private readonly Action<AdminOverviewTab> _onSelect;

    public AdminOverviewTab(PeddlerId id, string label, Action<AdminOverviewTab> onSelect)
    {
        Id = id;
        Label = label;
        _onSelect = onSelect;
        SelectCommand = new RelayCommand(_ => _onSelect(this));
    }

    public PeddlerId Id { get; }

    public string Label { get; }

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}
