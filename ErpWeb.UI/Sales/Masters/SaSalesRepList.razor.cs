using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Masters;

public partial class SaSalesRepList : SaCodeRefListPageBase<SaSalesRepListRow>
{
    [Inject] private ISaCustLookupService Lookups { get; set; } = default!;

    protected override string MenuCode => MenuCodes.SalesSalesRep;
    protected override string EntityLabel => "Sales Rep";
    protected override bool SupportsActivate => true;

    protected SaSalesRepEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }
    private string? _loadedFingerprint;

    /// <summary>
    /// Ungated state/country lists — the same combos <c>SaCustEntry</c> and <c>PoSuppEntry</c> already use
    /// for their address blocks. These fields are NOT validated server-side, so this is a typing guard
    /// only: no new server-side check is introduced here (plan §4.0 rule 5 / §4.2).
    /// </summary>
    protected IReadOnlyList<IvCodeLookupRow> StateOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> CountryOptions { get; set; } = [];

    // ---- Monthly targets (sales-analysis Phase 1) ----
    // Targets are company-wide: they carry no branch, and attainment on the analysis screen ignores the
    // optional branch filter. The grid shows one calendar year at a time; every month is upserted by
    // (code, year, month), so saving a month twice updates it instead of failing.
    protected int TargetYear { get; set; } = DateTime.Today.Year;
    protected List<SalesRepTargetEntry> TargetMonths { get; set; } = [];
    protected bool TargetsBusy { get; set; }
    protected string? TargetMessage { get; set; }

    /// <summary>Set while the popup shows a rep that exists, so the target panel has a key to save against.</summary>
    protected bool ShowTargets => IsEditMode && !string.IsNullOrWhiteSpace(EditModel.Code);

    protected static string MonthName(int month) => month switch
    {
        1 => "Jan", 2 => "Feb", 3 => "Mar", 4 => "Apr", 5 => "May", 6 => "Jun",
        7 => "Jul", 8 => "Aug", 9 => "Sep", 10 => "Oct", 11 => "Nov", _ => "Dec"
    };

    protected sealed class SalesRepTargetEntry
    {
        public int Month { get; init; }
        public decimal Amount { get; set; }
        public decimal Original { get; init; }
    }

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(SaSalesRepListRow.Code),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "100px"
        },
        new()
        {
            Caption = "Name",
            FieldName = nameof(SaSalesRepListRow.Name),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Tel",
            FieldName = nameof(SaSalesRepListRow.Tel),
            DataType = "string",
            VisibleIndex = 3,
            Width = "120px"
        },
        new()
        {
            Caption = "Email",
            FieldName = nameof(SaSalesRepListRow.Email),
            DataType = "string",
            VisibleIndex = 4,
            Width = "180px"
        },
        new()
        {
            Caption = "Commission",
            FieldName = nameof(SaSalesRepListRow.CommissionRate),
            DataType = "decimal",
            VisibleIndex = 5,
            Width = "110px"
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(SaSalesRepListRow.IsActive),
            DataType = "bool",
            VisibleIndex = 6,
            Width = "80px"
        }
    ];

    protected override async Task OnPageInitializedAsync() => await ReloadListAsync();

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await RefService.ListSalesRepsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load sales reps.";
                Data = [];
            }
            else
            {
                Data = result.Data ?? [];
            }

            SelectedRows.Clear();
            Grid?.Reload();
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected override string GetRowCode(SaSalesRepListRow row) => row.Code;

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<string> codes) =>
        RefService.CanDeleteSalesRepsAsync(codes);

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<string> codes) =>
        RefService.DeleteSalesRepsAsync(codes);

    protected override Task<IvMasterOperationResult<object>> SetActiveByCodesAsync(
        IReadOnlyList<string> codes,
        bool isActive) =>
        RefService.SetSalesRepActiveAsync(codes, isActive);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new SaSalesRepEditVm { IsActive = true };
        _loadedFingerprint = null;
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        TargetMonths = [];
        TargetMessage = null;
        TargetYear = DateTime.Today.Year;
        await LoadLookupsAsync();
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(SaSalesRepListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access))
        {
            return;
        }

        if (!await LoadEditModelAsync(row.Code))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        await LoadLookupsAsync();
        await LoadTargetsAsync();
        PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(SaSalesRepListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        if (!await LoadEditModelAsync(row.Code))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = true;
        CanEditFromView = false;
        await LoadLookupsAsync();
        await LoadTargetsAsync();
        PopupVisible = true;
    }

    private async Task LoadLookupsAsync()
    {
        StateOptions = await Lookups.ListStatesForAssignmentAsync();
        CountryOptions = await Lookups.ListCountriesForAssignmentAsync();
    }

    // ---- Monthly targets (sales-analysis Phase 1) ----

    private async Task LoadTargetsAsync()
    {
        TargetMessage = null;
        if (!ShowTargets)
        {
            TargetMonths = [];
            return;
        }

        TargetsBusy = true;
        TargetMonths = [];
        try
        {
            var result = await RefService.ListSalesRepTargetsAsync(EditModel.Code, TargetYear);
            if (!result.Succeeded)
            {
                TargetMessage = SaRefListMessages.FormatResultMessage(result);
                return;
            }

            var byMonth = (result.Data ?? [])
                .ToDictionary(x => x.Month, x => x.TargetAmount);

            TargetMonths = Enumerable.Range(1, 12)
                .Select(month => new SalesRepTargetEntry
                {
                    Month = month,
                    Amount = byMonth.TryGetValue(month, out var amount) ? amount : 0m,
                    Original = byMonth.TryGetValue(month, out var original) ? original : 0m
                })
                .ToList();
        }
        finally
        {
            TargetsBusy = false;
        }
    }

    protected async Task ChangeTargetYearAsync(int delta)
    {
        TargetYear += delta;
        await LoadTargetsAsync();
    }

    protected async Task SaveTargetsAsync()
    {
        if (IsSubmitting || TargetsBusy || !EditEnabled)
        {
            return;
        }

        var changed = TargetMonths.Where(x => x.Amount != x.Original).ToList();
        if (changed.Count == 0)
        {
            TargetMessage = "No target changes to save.";
            return;
        }

        if (changed.Any(x => x.Amount < 0m))
        {
            TargetMessage = "Target amounts cannot be negative.";
            return;
        }

        TargetsBusy = true;
        TargetMessage = null;
        try
        {
            foreach (var entry in changed)
            {
                var result = await RefService.SaveSalesRepTargetAsync(new SaSalesRepTargetEditVm
                {
                    Code = EditModel.Code,
                    Year = TargetYear,
                    Month = entry.Month,
                    TargetAmount = entry.Amount
                });

                if (!result.Succeeded)
                {
                    TargetMessage = SaRefListMessages.FormatResultMessage(result);
                    return;
                }
            }

            TargetMessage = $"Saved {TargetYear} targets.";
            await LoadTargetsAsync();
        }
        finally
        {
            TargetsBusy = false;
        }
    }

    protected async Task ClearTargetAsync(SalesRepTargetEntry entry)
    {
        if (IsSubmitting || TargetsBusy || !EditEnabled)
        {
            return;
        }

        if (entry.Original <= 0m && entry.Amount == 0m)
        {
            return;
        }

        TargetsBusy = true;
        TargetMessage = null;
        try
        {
            var result = await RefService.DeleteSalesRepTargetAsync(EditModel.Code, TargetYear, entry.Month);
            if (!result.Succeeded && result.ErrorCode != IvMasterErrorCode.NotFound)
            {
                TargetMessage = SaRefListMessages.FormatResultMessage(result);
                return;
            }

            await LoadTargetsAsync();
        }
        finally
        {
            TargetsBusy = false;
        }
    }

    protected async Task SwitchViewToEditAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        EditEnabled = true;
        CanEditFromView = false;
    }

    protected async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await RefService.SaveSalesRepAsync(
                EditModel,
                isNew: !IsEditMode,
                expectedFingerprint: IsEditMode ? _loadedFingerprint : null);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Sales rep updated successfully." : "Sales rep added successfully.";
                await ReloadListAsync();
            }
            else if (result.ErrorCode == IvMasterErrorCode.Concurrency)
            {
                ErrorMessage = result.Message ?? "This record was modified by another user.";
                if (IsEditMode)
                {
                    await LoadEditModelAsync(EditModel.Code);
                }
            }
            else
            {
                ErrorMessage = SaRefListMessages.FormatResultMessage(result);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task<bool> LoadEditModelAsync(string code)
    {
        var result = await RefService.GetSalesRepAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load sales rep.";
            return false;
        }

        EditModel = result.Data;
        _loadedFingerprint = SaMasterFingerprint.SalesRep(EditModel);
        ErrorMessage = null;
        return true;
    }
}
