using System.Globalization;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.WebUtilities;

namespace ErpWeb.UI.Sales.Pricing;

public partial class SaPriceMaintenance : PageBase
{
    private const string NeedsInitialItemDefaultPriceWarning =
        "No current Item Default price. Enter the initial selling price in Proposed, then Review Changes again.";

    private const string MissingItemDefaultUomWarning =
        "Item has no Selling UOM or Standard UOM. Fix Item Master UOM before relying on this Item Default price.";

    [Inject] private ISaPriceMaintenanceService Maintenance { get; set; } = default!;
    [Inject] private ISaCustLookupService CustomerLookups { get; set; } = default!;
    [Inject] private IIvInventoryLookupService InventoryLookups { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected SaPriceReviewQuery Query { get; } = new();
    protected IReadOnlyList<SaPriceReviewRow> Rows { get; private set; } = [];
    protected IReadOnlyList<object> SelectedDataItems { get; private set; } = [];
    protected SaPricePreviewResult? LastPreview { get; private set; }
    protected SaPriceApplyResult? LastApply { get; private set; }
    protected SaPriceListImpactSummary? PriceListImpact { get; private set; }
    protected int TotalCount { get; private set; }
    protected bool HasLoaded { get; private set; }
    protected string? StatusMessage { get; private set; }
    protected string? Reason { get; set; }
    protected SaPriceImportPreview? ImportPreview { get; private set; }
    protected bool CanApply { get; private set; }
    protected bool CanExport { get; private set; }
    protected bool CanImport { get; private set; }
    protected bool ShowLoadConfirmation { get; private set; }
    protected bool ShowApplyConfirmation { get; private set; }

    protected IReadOnlyList<IvCodeLookupRow> TargetOptions { get; } =
    [
        new() { Code = SaPriceMaintenanceTargets.ItemDefault, Desc = "Item Default" },
        new() { Code = SaPriceMaintenanceTargets.PriceList, Desc = "Price List" },
        new() { Code = SaPriceMaintenanceTargets.CustomerItem, Desc = "Customer Special" }
    ];

    protected IReadOnlyList<IvCodeLookupRow> AdjustmentOptions { get; } =
    [
        new() { Code = SaPriceAdjustmentMethods.SetPrice, Desc = "Set price" },
        new() { Code = SaPriceAdjustmentMethods.IncreasePercent, Desc = "Increase by %" },
        new() { Code = SaPriceAdjustmentMethods.DecreasePercent, Desc = "Decrease by %" },
        new() { Code = SaPriceAdjustmentMethods.IncreaseAmount, Desc = "Increase amount" },
        new() { Code = SaPriceAdjustmentMethods.DecreaseAmount, Desc = "Decrease amount" }
    ];

    protected IReadOnlyList<IvCodeLookupRow> RoundingOptions { get; } =
    [
        new() { Code = SaPriceRoundingModes.Normal, Desc = "Normal" },
        new() { Code = SaPriceRoundingModes.Up, Desc = "Round up" },
        new() { Code = SaPriceRoundingModes.Down, Desc = "Round down" }
    ];

    protected IReadOnlyList<IvCodeLookupRow> PriceListUpdateOptions { get; } =
    [
        new() { Code = SaPriceListUpdateModes.UpdateSelectedRow, Desc = "Update selected row" },
        new() { Code = SaPriceListUpdateModes.ScheduleFromDate, Desc = "Schedule from effective date" }
    ];

    protected IReadOnlyList<IvCodeLookupRow> CustomerTypes { get; private set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> CustomerGroups { get; private set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> PriceLists { get; private set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> ItemTypes { get; private set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> ItemClasses { get; private set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> ItemSubClasses { get; private set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Uoms { get; private set; } = [];

    protected bool IsPriceList => string.Equals(Query.TargetType, SaPriceMaintenanceTargets.PriceList, StringComparison.OrdinalIgnoreCase);
    protected bool IsCustomerItem => string.Equals(Query.TargetType, SaPriceMaintenanceTargets.CustomerItem, StringComparison.OrdinalIgnoreCase);
    protected bool IsItemDefault => string.Equals(Query.TargetType, SaPriceMaintenanceTargets.ItemDefault, StringComparison.OrdinalIgnoreCase);

    protected override async Task OnPageInitializedAsync()
    {
        Query.ReviewAsOf = Dates.Today.Date;

        var lookupTasks = new Task[]
        {
            LoadCustomerLookupsAsync(),
            LoadInventoryLookupsAsync(),
            LoadPermissionsAsync()
        };
        await Task.WhenAll(lookupTasks);
    }

    private async Task LoadPermissionsAsync()
    {
        var permissions = await Task.WhenAll(
            AccessRights.CanAsync(MenuCodes.SalesPriceMaintenance, PermissionCodes.Edit),
            AccessRights.CanAsync(MenuCodes.SalesPriceMaintenance, PermissionCodes.Export),
            AccessRights.CanAsync(MenuCodes.SalesPriceMaintenance, PermissionCodes.Import));

        CanApply = permissions[0];
        CanExport = permissions[1];
        CanImport = permissions[2];
    }

    private async Task LoadCustomerLookupsAsync()
    {
        CustomerTypes = await CustomerLookups.ListTypesForAssignmentAsync();
        CustomerGroups = await CustomerLookups.ListGroupsForAssignmentAsync();
        PriceLists = await CustomerLookups.ListPriceGroupsForAssignmentAsync();
    }

    private async Task LoadInventoryLookupsAsync()
    {
        var types = await InventoryLookups.ListActiveTypesAsync();
        var classes = await InventoryLookups.ListActiveClassesAsync();
        var uoms = await InventoryLookups.ListActiveUomsAsync();

        ItemTypes = types.Succeeded ? types.Rows : [];
        ItemClasses = classes.Succeeded ? classes.Rows : [];
        Uoms = uoms.Succeeded ? uoms.Rows : [];
    }

    protected async Task OnTargetChangedAsync(string? value)
    {
        Query.TargetType = Clean(value) ?? SaPriceMaintenanceTargets.ItemDefault;
        Query.Skip = 0;
        Rows = [];
        TotalCount = 0;
        HasLoaded = false;
        SelectedDataItems = [];
        LastPreview = null;
        LastApply = null;
        PriceListImpact = null;
        ImportPreview = null;
        ErrorMessage = null;
        StatusMessage = null;
        await InvokeAsync(StateHasChanged);
    }

    protected async Task OnItemClassChangedAsync(string? value)
    {
        Query.ItemClass = Clean(value);
        Query.ItemSubClass = null;
        ItemSubClasses = [];
        if (!string.IsNullOrWhiteSpace(Query.ItemClass))
        {
            var result = await InventoryLookups.ListActiveSubClassesAsync(Query.ItemClass);
            ItemSubClasses = result.Succeeded ? result.Rows : [];
        }
    }

    protected Task OnItemCodeChangedAsync(string? value)
    {
        Query.ItemCode = Clean(value);
        return Task.CompletedTask;
    }

    protected Task OnItemSelectedAsync(IvStockMasterLookupRow item)
    {
        Query.ItemCode = item.ICode;
        return Task.CompletedTask;
    }

    protected Task OnCustomerCodeChangedAsync(string? value)
    {
        Query.CustCode = Clean(value);
        return Task.CompletedTask;
    }

    protected async Task OnPriceListChangedAsync(string? value)
    {
        Query.CustPriceCode = Clean(value);
        PriceListImpact = null;
        if (IsPriceList && !string.IsNullOrWhiteSpace(Query.CustPriceCode))
        {
            await LoadPriceListImpactAsync();
        }
    }

    protected Task RequestSearchAsync()
    {
        if (IsBusy)
        {
            return Task.CompletedTask;
        }

        if (IsItemDefault && Query.LoadAllActiveItems)
        {
            ShowLoadConfirmation = true;
            return Task.CompletedTask;
        }

        return SearchAsync();
    }

    protected Task ConfirmLoadAsync()
    {
        ShowLoadConfirmation = false;
        return SearchAsync();
    }

    protected async Task SearchAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        LastPreview = null;
        LastApply = null;
        ImportPreview = null;
        SelectedDataItems = [];
        HasLoaded = false;
        Query.Skip = 0;
        Query.Take = SaPriceMaintenanceLimits.MaxReviewRows;

        try
        {
            var result = await Maintenance.SearchAsync(Query);
            if (!result.Succeeded || result.Data is null)
            {
                Rows = [];
                TotalCount = 0;
                ErrorMessage = result.Message ?? "Unable to load price rows.";
                return;
            }

            Rows = result.Data.Rows.ToList();
            TotalCount = result.Data.TotalCount;
            HasLoaded = true;
            await LoadPriceListImpactAsync();
            StatusMessage = $"{TotalCount:N0} matching row(s) loaded. Select the rows to stage.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected Task OnSelectedDataItemsChanged(IReadOnlyList<object> selected)
    {
        SelectedDataItems = selected;
        foreach (var row in Rows)
        {
            row.Selected = selected.Contains(row);
        }

        return Task.CompletedTask;
    }

    protected async Task ReviewChangesAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;
        LastPreview = null;
        LastApply = null;
        ImportPreview = null;

        var selected = SelectedRows().ToList();
        if (selected.Count == 0)
        {
            ErrorMessage = "Select at least one row before reviewing changes.";
            return;
        }

        var rowsNeedingCalculation = selected
            .Where(x => !x.ProposedPrice.HasValue)
            .ToList();
        if (rowsNeedingCalculation.Count > 0)
        {
            CalculateProposals(rowsNeedingCalculation);
        }

        await PreviewAsync();
    }

    private void CalculateProposals(IReadOnlyList<SaPriceReviewRow> rows)
    {
        foreach (var row in rows)
        {
            row.Selected = true;
            if (IsItemDefault
                && row.CurrentPrice is null
                && !string.Equals(
                    QueryAdjustmentMethod?.Trim(),
                    SaPriceAdjustmentMethods.SetPrice,
                    StringComparison.OrdinalIgnoreCase))
            {
                row.ProposedPrice = null;
                row.Warning = JoinWarnings(
                    NeedsInitialItemDefaultPriceWarning,
                    ItemDefaultUomWarning(row));
                row.Status = SaPriceReviewStatuses.Blocked;
                UpdateDifference(row);
                continue;
            }

            var calculation = SaPriceAdjustmentCalculator.Calculate(
                row.CurrentPrice,
                QueryAdjustmentMethod,
                AdjustmentValue,
                DecimalPlaces,
                RoundingMode);

            row.ProposedPrice = calculation.NewPrice;
            row.Warning = JoinWarnings(calculation.Error, ItemDefaultUomWarning(row));
            row.Status = calculation.Succeeded
                ? PriceStatusFor(row.CurrentPrice, row.ProposedPrice)
                : SaPriceReviewStatuses.Blocked;
            UpdateDifference(row);
        }
    }

    protected async Task PreviewAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (SelectedRows().Count == 0)
        {
            ErrorMessage = "Select at least one row before reviewing changes.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        LastApply = null;
        ImportPreview = null;
        try
        {
            var result = await Maintenance.PreviewAsync(BuildRequest<SaPricePreviewRequest>());
            if (!result.Succeeded || result.Data is null)
            {
                LastPreview = null;
                ErrorMessage = result.Message ?? "Unable to review the price change.";
                return;
            }

            LastPreview = result.Data;
            Rows = result.Data.Rows.ToList();
            TotalCount = Rows.Count;
            SelectedDataItems = Rows.Where(x => x.Selected).Cast<object>().ToList();
            StatusMessage = $"Review ready: {result.Data.Summary.Changing:N0} changing, {result.Data.Summary.Unchanged:N0} unchanged, {result.Data.Summary.Blocked:N0} need attention.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected Task RequestApplyAsync()
    {
        if (IsBusy)
        {
            return Task.CompletedTask;
        }

        if (LastPreview is null)
        {
            ErrorMessage = "Run Review Changes before applying a price change.";
            return Task.CompletedTask;
        }

        if (LastPreview.Summary.Changing == 0)
        {
            ErrorMessage = "There are no changing rows in this review.";
            return Task.CompletedTask;
        }

        if (HasBlockedRows)
        {
            ErrorMessage = "Resolve the rows that need attention, then Review Changes again.";
            return Task.CompletedTask;
        }

        if (!CanApply)
        {
            ErrorMessage = "EDIT permission is required to apply a price change.";
            return Task.CompletedTask;
        }

        var reason = Clean(Reason);
        if (string.IsNullOrWhiteSpace(reason))
        {
            ErrorMessage = "A reason is required before applying a price change.";
            return Task.CompletedTask;
        }

        if (reason.Length > SaPriceMaintenanceLimits.MaxReasonLength)
        {
            ErrorMessage = $"Reason must be {SaPriceMaintenanceLimits.MaxReasonLength} characters or fewer.";
            return Task.CompletedTask;
        }

        ErrorMessage = null;
        ShowApplyConfirmation = true;
        return Task.CompletedTask;
    }

    protected async Task ConfirmApplyAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (LastPreview is null)
        {
            ShowApplyConfirmation = false;
            ErrorMessage = "Run Review Changes before applying a price change.";
            return;
        }

        if (LastPreview.Summary.Changing == 0)
        {
            ShowApplyConfirmation = false;
            ErrorMessage = "There are no changing rows in this review.";
            return;
        }

        if (HasBlockedRows)
        {
            ShowApplyConfirmation = false;
            ErrorMessage = "Resolve the rows that need attention, then Review Changes again.";
            return;
        }

        if (!CanApply)
        {
            ShowApplyConfirmation = false;
            ErrorMessage = "EDIT permission is required to apply a price change.";
            return;
        }

        var reason = Clean(Reason);
        if (string.IsNullOrWhiteSpace(reason))
        {
            ShowApplyConfirmation = false;
            ErrorMessage = "A reason is required before applying a price change.";
            return;
        }

        if (reason.Length > SaPriceMaintenanceLimits.MaxReasonLength)
        {
            ShowApplyConfirmation = false;
            ErrorMessage = $"Reason must be {SaPriceMaintenanceLimits.MaxReasonLength} characters or fewer.";
            return;
        }

        ShowApplyConfirmation = false;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var request = BuildRequest<SaPriceApplyRequest>();
            request.Reason = reason;
            var result = await Maintenance.ApplyAsync(request);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Unable to apply the price change.";
                return;
            }

            LastApply = result.Data;
            LastPreview = null;
            ImportPreview = null;

            var reloaded = await ReloadOfficialRowsAfterApplyAsync();
            StatusMessage = reloaded
                ? $"Applied {result.Data.ChangedRowCount:N0} row(s). Batch {result.Data.BatchReference} was recorded. The grid was reloaded from the official price master."
                : $"Applied {result.Data.ChangedRowCount:N0} row(s). Batch {result.Data.BatchReference} was recorded, but the grid could not refresh. Click Load items to verify the saved values.";
            if (!reloaded)
            {
                ErrorMessage = "The price change was saved, but the grid refresh failed. Click Load items to verify the current price.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected Task OpenPriceHistoryAsync()
    {
        Navigation.NavigateTo("/sales/inquiry/price-change-history");
        return Task.CompletedTask;
    }

    protected Task StartNewReviewAsync()
    {
        Navigation.NavigateTo("/sales/pricing/review", forceLoad: true);
        return Task.CompletedTask;
    }

    protected Task OnProposedPriceChangedAsync(SaPriceReviewRow row)
    {
        LastPreview = null;
        LastApply = null;
        ImportPreview = null;
        row.Status = row.ProposedPrice.HasValue
            ? PriceStatusFor(row.CurrentPrice, row.ProposedPrice)
            : SaPriceReviewStatuses.Blocked;
        row.Warning = JoinWarnings(
            row.ProposedPrice.HasValue ? null : "Enter a proposed price.",
            ItemDefaultUomWarning(row));

        UpdateDifference(row);
        return Task.CompletedTask;
    }

    protected Task ExportWorkbookAsync()
    {
        if (!CanExport)
        {
            ErrorMessage = "EXPORT permission is required to download the review workbook.";
            return Task.CompletedTask;
        }

        var url = QueryHelpers.AddQueryString(
            "/sales/pricing/review/export",
            BuildExportQuery());
        Navigation.NavigateTo(url, forceLoad: true);
        return Task.CompletedTask;
    }

    protected async Task ImportWorkbookAsync(InputFileChangeEventArgs args)
    {
        if (IsBusy)
        {
            return;
        }

        if (!CanImport)
        {
            ErrorMessage = "IMPORT permission is required to load a review workbook.";
            return;
        }

        if (!HasRows)
        {
            ErrorMessage = "Load a review before importing a workbook.";
            return;
        }

        var file = args.File;
        if (file is null || file.Size <= 0)
        {
            ErrorMessage = "Select a non-empty .xlsx workbook.";
            return;
        }

        if (file.Size > SaPriceReviewWorkbookLimits.MaxFileBytes)
        {
            ErrorMessage = $"Workbook exceeds the {SaPriceReviewWorkbookLimits.MaxFileBytes / (1024 * 1024):N0} MB limit.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        LastPreview = null;
        LastApply = null;
        ImportPreview = null;

        try
        {
            await using var stream = file.OpenReadStream(SaPriceReviewWorkbookLimits.MaxFileBytes);
            var result = await Maintenance.ParseImportAsync(
                stream,
                new SaPriceImportContext
                {
                    TargetType = Query.TargetType,
                    StagedRows = Rows
                });

            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Unable to import the review workbook.";
                return;
            }

            ImportPreview = result.Data;
            if (!result.Data.IsValid)
            {
                ErrorMessage = "The workbook contains validation errors. Correct the listed rows and import it again.";
                return;
            }

            var byKey = result.Data.Updates.ToDictionary(x => x.ReviewRowKey, StringComparer.Ordinal);
            foreach (var row in Rows)
            {
                if (byKey.TryGetValue(row.ReviewRowKey, out var update))
                {
                    ApplyImportedUpdate(row, update);
                }
            }

            SelectedDataItems = Rows.Where(x => x.Selected).Cast<object>().ToList();
            StatusMessage = $"Imported {result.Data.AcceptedRowCount:N0} workbook row(s). Review the staged values, then click Review Changes.";
        }
        catch (IOException)
        {
            ErrorMessage = "The review workbook could not be read.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected string QueryAdjustmentMethod { get; set; } = SaPriceAdjustmentMethods.SetPrice;
    protected decimal AdjustmentValue { get; set; }
    protected int DecimalPlaces { get; set; } = 2;
    protected string RoundingMode { get; set; } = SaPriceRoundingModes.Normal;
    protected string PriceListUpdateMode { get; set; } = SaPriceListUpdateModes.UpdateSelectedRow;
    protected DateTime? EffectiveFrom { get; set; }
    protected IReadOnlyList<int> DecimalPlaceOptions { get; } = [2, 4];

    protected int SelectedCount => SelectedRows().Count;
    protected bool CanSchedule => IsPriceList;
    protected bool HasRows => Rows.Count > 0;
    protected bool HasBlockedRows => Rows.Any(x => x.Status == SaPriceReviewStatuses.Blocked);

    protected string DisplayStatus(SaPriceReviewRow row)
    {
        if (row.Status == SaPriceReviewStatuses.Blocked
            && string.Equals(row.TargetType, SaPriceMaintenanceTargets.ItemDefault, StringComparison.OrdinalIgnoreCase)
            && row.CurrentPrice is null
            && row.ProposedPrice is null
            && !string.Equals(
                QueryAdjustmentMethod?.Trim(),
                SaPriceAdjustmentMethods.SetPrice,
                StringComparison.OrdinalIgnoreCase))
        {
            return "Needs Price";
        }

        return row.Status switch
        {
            SaPriceReviewStatuses.Ready => "Ready",
            SaPriceReviewStatuses.Unchanged => "No Change",
            SaPriceReviewStatuses.Blocked => "Check Warning",
            _ => row.Status
        };
    }

    protected string ApplyStateHint =>
        IsBusy
            ? "Working..."
            : !CanApply
                ? "Apply unavailable: your account needs EDIT permission for Price Review & Update."
                : LastPreview is null
                    ? "Run Review Changes before applying."
                    : LastPreview.Summary.Changing == 0
                        ? "There are no changing rows in this review."
                    : HasBlockedRows
                        ? "Resolve the rows that need attention, then Review Changes again."
                        : string.IsNullOrWhiteSpace(Reason)
                            ? "Enter a reason, then click Apply price change."
                            : "Ready to apply the reviewed price change.";

    private async Task<bool> ReloadOfficialRowsAfterApplyAsync()
    {
        Query.Skip = 0;
        Query.Take = SaPriceMaintenanceLimits.MaxReviewRows;

        var result = await Maintenance.SearchAsync(Query);
        if (!result.Succeeded || result.Data is null)
        {
            SelectedDataItems = [];
            return false;
        }

        Rows = result.Data.Rows.ToList();
        TotalCount = result.Data.TotalCount;
        HasLoaded = true;
        await LoadPriceListImpactAsync();
        SelectedDataItems = [];
        return true;
    }

    private async Task LoadPriceListImpactAsync()
    {
        if (!IsPriceList || string.IsNullOrWhiteSpace(Query.CustPriceCode))
        {
            PriceListImpact = null;
            return;
        }

        var result = await Maintenance.GetPriceListImpactAsync(Query.CustPriceCode);
        PriceListImpact = result.Succeeded ? result.Data : null;
    }

    private IReadOnlyList<SaPriceReviewRow> SelectedRows() =>
        SelectedDataItems.OfType<SaPriceReviewRow>().ToList();

    private TRequest BuildRequest<TRequest>()
        where TRequest : SaPriceChangeRequestBase, new()
    {
        return new TRequest
        {
            TargetType = Query.TargetType,
            AdjustmentMethod = QueryAdjustmentMethod,
            AdjustmentValue = AdjustmentValue,
            DecimalPlaces = DecimalPlaces,
            RoundingMode = RoundingMode,
            PriceListUpdateMode = PriceListUpdateMode,
            EffectiveFrom = EffectiveFrom?.Date,
            Reason = Clean(Reason),
            ReviewScope = CloneQuery(),
            Selections = SelectedRows().Select(x => new SaPriceReviewSelection
            {
                ReviewRowKey = x.ReviewRowKey,
                BaselinePrice = x.CurrentPrice,
                NewPrice = x.ProposedPrice,
                RowVersion = x.RowVersion,
                HeaderRowVersion = x.HeaderRowVersion
            }).ToList()
        };
    }

    private SaPriceReviewQuery CloneQuery() => new()
    {
        TargetType = Query.TargetType,
        ItemCode = Clean(Query.ItemCode),
        ItemSearch = Clean(Query.ItemSearch),
        ItemType = Clean(Query.ItemType),
        ItemClass = Clean(Query.ItemClass),
        ItemSubClass = Clean(Query.ItemSubClass),
        Brand = Clean(Query.Brand),
        ActiveItemsOnly = Query.ActiveItemsOnly,
        LoadAllActiveItems = Query.LoadAllActiveItems,
        CustPriceCode = Clean(Query.CustPriceCode),
        CustGroupCode = Clean(Query.CustGroupCode),
        Uom = Clean(Query.Uom),
        CurrencyCode = Clean(Query.CurrencyCode),
        ReviewAsOf = Query.ReviewAsOf?.Date,
        CustCode = Clean(Query.CustCode),
        CustType = Clean(Query.CustType),
        CustGroup = Clean(Query.CustGroup),
        ActiveCustomersOnly = Query.ActiveCustomersOnly,
        Moq = Query.Moq,
        Skip = 0,
        Take = SaPriceMaintenanceLimits.MaxReviewRows
    };

    private Dictionary<string, string?> BuildExportQuery()
    {
        var query = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["targetType"] = Query.TargetType,
            ["activeItemsOnly"] = Query.ActiveItemsOnly ? "true" : "false",
            ["loadAllActiveItems"] = Query.LoadAllActiveItems ? "true" : "false",
            ["activeCustomersOnly"] = Query.ActiveCustomersOnly ? "true" : "false",
            ["adjustmentMethod"] = QueryAdjustmentMethod,
            ["adjustmentValue"] = AdjustmentValue.ToString(CultureInfo.InvariantCulture),
            ["decimalPlaces"] = DecimalPlaces.ToString(CultureInfo.InvariantCulture),
            ["roundingMode"] = RoundingMode
        };

        AddQueryText(query, "itemCode", Query.ItemCode);
        AddQueryText(query, "itemSearch", Query.ItemSearch);
        AddQueryText(query, "itemType", Query.ItemType);
        AddQueryText(query, "itemClass", Query.ItemClass);
        AddQueryText(query, "itemSubClass", Query.ItemSubClass);
        AddQueryText(query, "brand", Query.Brand);
        AddQueryText(query, "custPriceCode", Query.CustPriceCode);
        AddQueryText(query, "custGroupCode", Query.CustGroupCode);
        AddQueryText(query, "uom", Query.Uom);
        AddQueryText(query, "currencyCode", Query.CurrencyCode);
        AddQueryText(query, "custCode", Query.CustCode);
        AddQueryText(query, "custType", Query.CustType);
        AddQueryText(query, "custGroup", Query.CustGroup);

        if (Query.ReviewAsOf.HasValue)
        {
            query["reviewAsOf"] = Query.ReviewAsOf.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (Query.Moq.HasValue)
        {
            query["moq"] = Query.Moq.Value.ToString(CultureInfo.InvariantCulture);
        }

        return query;
    }

    private static void AddQueryText(Dictionary<string, string?> query, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query[key] = value.Trim();
        }
    }

    private static void ApplyImportedUpdate(SaPriceReviewRow row, SaPriceImportRow update)
    {
        row.Selected = update.Selected;
        row.ProposedPrice = update.NewPrice;
        row.Warning = update.Selected && !update.NewPrice.HasValue
            ? "Enter a proposed price."
            : null;
        row.Status = row.Warning is not null
            ? SaPriceReviewStatuses.Blocked
            : update.NewPrice.HasValue
                ? PriceStatusFor(row.CurrentPrice, update.NewPrice)
                : SaPriceReviewStatuses.Ready;
        UpdateDifference(row);
    }

    private static string PriceStatusFor(decimal? current, decimal? proposed) =>
        current.HasValue && proposed.HasValue && current.Value == proposed.Value
            ? SaPriceReviewStatuses.Unchanged
            : SaPriceReviewStatuses.Ready;

    private static string? ItemDefaultUomWarning(SaPriceReviewRow row) =>
        string.Equals(row.TargetType, SaPriceMaintenanceTargets.ItemDefault, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(row.Uom)
            ? MissingItemDefaultUomWarning
            : null;

    private static string? JoinWarnings(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.IsNullOrWhiteSpace(second) ? null : second;
        }

        return string.IsNullOrWhiteSpace(second) ? first : $"{first} {second}";
    }

    private static void UpdateDifference(SaPriceReviewRow row)
    {
        row.DifferenceAmount = row.CurrentPrice.HasValue && row.ProposedPrice.HasValue
            ? row.ProposedPrice.Value - row.CurrentPrice.Value
            : null;
        row.DifferencePercent = row.CurrentPrice is null or 0m || !row.ProposedPrice.HasValue
            ? null
            : (row.ProposedPrice.Value - row.CurrentPrice.Value) / row.CurrentPrice.Value * 100m;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
