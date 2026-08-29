using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using BentaGo.App.Data;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Ui;
using BentaGo.Application.Contracts;
using BentaGo.Application.Import;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Features.Admin;

/// <summary>
/// Catalog import (BG-005d, docs/05 BG-005): specify the client CSV path
/// (typed or picked via the standard OpenFileDialog — the app has no other
/// file dialog), run the preview (derived SKUs, per-row errors, file summary)
/// and confirm the import (valid rows upsert by SKU; error rows are excluded
/// — the service's locked contract). The preview and commit both go through
/// <see cref="IProductImportService"/> on a worker thread so a large client
/// file never stalls the UI (docs/05 preview &lt;= 3s / commit &lt;= 5s rule).
///
/// Copy conventions: file-level failures are generic (never echo file
/// contents); per-row mapping errors come from the mapper's own messages;
/// the commit summary states exactly what was written. The last known path is
/// remembered for the session so a repeat import restarts from the same file
/// (retained screen).
/// </summary>
public sealed class CatalogImportViewModel : ValidatableViewModelBase, IRetainableScreen
{
    private readonly IProductImportService _imports;
    private readonly IFocusService _focus;

    private string? _filePath;
    private string? _lastKnownPath;
    private ImportPreview? _preview;
    private ImportCommitResult? _commit;
    private bool _isBusy;
    private string? _errorMessage;
    private string? _statusMessage;

    public CatalogImportViewModel(IServiceProvider services)
    {
        _imports = services.GetRequiredService<IProductImportService>();
        _focus = services.GetRequiredService<IFocusService>();

        BrowseCommand = new RelayCommand(_ => Browse());
        PreviewCommand = new RelayCommand(async _ => await PreviewAsync(), _ => CanPreview);
        ConfirmImportCommand = new RelayCommand(async _ => await ConfirmImportAsync(), _ => CanImport);
        ResetCommand = new RelayCommand(_ => ResetPreview(), _ => !IsBusy);
        FocusPathCommand = new RelayCommand(_ => _focus.Request(AutomationIds.AdminImportFilePath));
    }

    public ICommand BrowseCommand { get; }

    public ICommand PreviewCommand { get; }

    public ICommand ConfirmImportCommand { get; }

    public ICommand ResetCommand { get; }

    public ICommand FocusPathCommand { get; }

    public string? FilePath
    {
        get => _filePath;
        set
        {
            if (SetProperty(ref _filePath, value))
            {
                OnPropertyChanged(nameof(CanPreview));
                (PreviewCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanPreview));
                NotifyImportGate();
                (ResetCommand as RelayCommand)?.NotifyCanExecuteChanged();
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

    public bool HasPreview => Preview is not null;

    public bool HasFileErrors => Preview is { Summary.Ok: false };

    public bool HasErrorRows => Preview is { Summary.ErrorRows: > 0 };

    public bool CanPreview => !IsBusy && !string.IsNullOrWhiteSpace(FilePath);

    /// <summary>Enabled when a preview exists, the file header was valid and at least one row is importable.</summary>
    public bool CanImport => !IsBusy && Preview is { Summary.Ok: true, Summary.ValidRows: > 0 } && Commit is null;

    public bool ImportSucceeded => Commit is not null;

    /// <summary>Summary line after a preview: "N rows · X valid · Y with errors".</summary>
    public string SummaryText => Preview is null
        ? ""
        : $"{Preview.Summary.TotalRows} rows · {Preview.Summary.ValidRows} valid · {Preview.Summary.ErrorRows} with errors";

    /// <summary>File-level (header) errors, one line each.</summary>
    public string FileErrorText => Preview is null
        ? ""
        : string.Join(Environment.NewLine, Preview.Summary.FileErrors.Select(e => e.Message));

    /// <summary>Commit summary: "Imported N rows (X inserted, Y updated)."</summary>
    public string CommitText => Commit is null
        ? ""
        : $"Imported {Commit.Imported} rows ({Commit.Inserted} inserted, {Commit.Updated} updated).";

    public ImportPreview? Preview
    {
        get => _preview;
        private set
        {
            if (SetProperty(ref _preview, value))
            {
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(HasFileErrors));
                OnPropertyChanged(nameof(HasErrorRows));
                OnPropertyChanged(nameof(SummaryText));
                OnPropertyChanged(nameof(FileErrorText));
                OnPropertyChanged(nameof(PreviewRows));
                NotifyImportGate();
            }
        }
    }

    public ImportCommitResult? Commit
    {
        get => _commit;
        private set
        {
            if (SetProperty(ref _commit, value))
            {
                OnPropertyChanged(nameof(ImportSucceeded));
                OnPropertyChanged(nameof(CommitText));
                NotifyImportGate();
            }
        }
    }

    /// <summary>Every preview row mapped to display strings (derived SKU always shown).</summary>
    public IReadOnlyList<ImportRowModel> PreviewRows => Preview is null
        ? Array.Empty<ImportRowModel>()
        : Preview.Rows.Select(ToRow).ToArray();

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select product CSV file",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(_lastKnownPath))
        {
            dialog.InitialDirectory = System.IO.Path.GetDirectoryName(_lastKnownPath);
        }

        if (dialog.ShowDialog() == true)
        {
            FilePath = dialog.FileName;
        }
    }

    private async Task PreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            SetFieldError(nameof(FilePath), "Choose or enter a CSV file path.");
            return;
        }

        SetFieldError(nameof(FilePath), null);
        await RunAsync(async () =>
        {
            string path = FilePath!;
            // Worker thread: parsing 50k rows must not stall the UI (docs/05).
            ImportPreview preview = await Task.Run(() => _imports.PreviewAsync(path));
            Preview = preview;
            Commit = null;
            _lastKnownPath = path;
            ErrorMessage = null;
            if (preview.Summary.Ok)
            {
                StatusMessage = "Preview ready. Review the rows, then confirm the import.";
            }
        }, "The file could not be read. Check the path and try again.");
    }

    private async Task ConfirmImportAsync()
    {
        if (Preview is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            string path = FilePath ?? "";
            ImportCommitResult commit = await Task.Run(() => _imports.ImportAsync(path));
            Commit = commit;
            _lastKnownPath = path;
            ErrorMessage = null;
            StatusMessage = "Import complete.";
        }, "The import failed. No rows were committed.");
    }

    private void ResetPreview()
    {
        if (IsBusy)
        {
            return;
        }

        Preview = null;
        Commit = null;
        ErrorMessage = null;
        StatusMessage = null;
        SetFieldError(nameof(FilePath), null);
    }

    private void NotifyImportGate()
    {
        OnPropertyChanged(nameof(CanImport));
        (ConfirmImportCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private static ImportRowModel ToRow(ProductImportRow row) => new(
        row.LineNumber,
        row.Sku,
        string.IsNullOrWhiteSpace(row.Name) ? "-" : row.Name,
        string.IsNullOrWhiteSpace(row.Unit) ? "-" : row.Unit,
        row.RetailPrice is null ? "-" : MoneyDisplay.Peso(row.RetailPrice.Value),
        row.Errors.Count == 0 ? "-" : string.Join("; ", row.Errors.Select(e => e.Message)),
        row.Errors.Count > 0);

    private async Task RunAsync(Func<Task> body, string failure)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
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
}

/// <summary>One preview grid row (preformatted display strings).</summary>
public sealed record ImportRowModel(
    int LineNumber,
    string Sku,
    string Name,
    string Unit,
    string RetailText,
    string ErrorsText,
    bool HasErrors);