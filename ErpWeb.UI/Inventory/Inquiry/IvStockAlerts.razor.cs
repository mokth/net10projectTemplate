using System.Collections;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace ErpWeb.UI.Inventory.Inquiry;

/// <summary>
/// Stock control alerts — one page, seven rules (D1).
///
/// <para>
/// The rule is the <em>only</em> control that changes the question; everything else is a filter. The
/// rule also decides which columns exist, because "Min stock" is meaningless on an expiry rule and
/// "Expiry date" is meaningless on a threshold rule.
/// </para>
///
/// <para>
/// The as-of date is resolved server-side in the service from the company-local clock (D14) and shown
/// in the header, so the user can see which date the ageing was measured against rather than guessing.
/// </para>
/// </summary>
public partial class IvStockAlerts : PageBase
{
    [Inject] private IIvStockAlertService Alerts { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private ICurrentDateService Dates { get; set; } = default!;

    private DxGrid? _grid;

    protected bool IsBootstrapping = true;
    protected bool FilterPopupVisible;
    protected string? StatusMessage;
    protected int TotalCount;

    protected bool CanExport;

    /// <summary>The one date every rule is evaluated against, shown to the user (D14).</summary>
    protected DateTime AsOfDate;

    // Applied state.
    protected string AppliedRule = IvStockAlertRules.Default;
    protected string? AppliedICode;
    protected string? AppliedWhCode;
    protected string? AppliedIClassCode;
    protected int AppliedSlowDays = 90;
    protected int AppliedDeadDays = 180;
    protected int AppliedExpiryDays = 30;
    protected bool AppliedIncludeInactive;
    protected bool AppliedIncludeNonStockControl;

    // Summary strip.
    protected int SummaryItemCount;
    protected decimal SummaryTotalOnHand;

    // Draft (popup) state. The rule is edited in BOTH the toolbar and the popup, so it is one field.
    protected string? DraftRule;
    protected string? DraftICode;
    protected string? DraftWhCode;
    protected string? DraftIClassCode;
    protected int DraftSlowDays = 90;
    protected int DraftDeadDays = 180;
    protected int DraftExpiryDays = 30;
    protected bool DraftIncludeInactive;
    protected bool DraftIncludeNonStockControl;

    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Classes { get; set; } = [];

    /// <summary>The rule list box's option shape — the house pattern for a combo over a code list.</summary>
    public sealed record RuleOption(string Token, string Name);

    protected IReadOnlyList<RuleOption> RuleOptions { get; } =
    [
        new(IvStockAlertRules.Low, "Low stock (below minimum)"),
        new(IvStockAlertRules.Over, "Overstock (above maximum)"),
        new(IvStockAlertRules.Slow, "Slow moving"),
        new(IvStockAlertRules.Dead, "Dead stock"),
        new(IvStockAlertRules.NeverMoved, "Never moved (no movement history)"),
        new(IvStockAlertRules.Expiring, "Expiring lots"),
        new(IvStockAlertRules.Expired, "Expired lots")
    ];

    protected IvStockAlertGridDataSource DataSource { get; private set; } = default!;

    protected string? SelectedRule
    {
        get => AppliedRule;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                AppliedRule = IvStockAlertRules.Normalize(value);
            }
        }
    }

    protected string RuleCaption => IvStockAlertRules.Describe(AppliedRule);

    protected string GridTitle => $"{RuleCaption} — {TotalCountLabel}";

    protected string TotalCountLabel => TotalCount == 1 ? "1 row" : $"{TotalCount:N0} rows";

    protected bool RuleNeedsDays => IvStockAlertRules.IsMovementRule(AppliedRule);

    protected bool RuleIsExpiry => IvStockAlertRules.IsLotRule(AppliedRule);

    protected bool RuleIsThreshold =>
        AppliedRule is IvStockAlertRules.Low or IvStockAlertRules.Over;

    protected string ThresholdBasisCaption =>
        string.IsNullOrWhiteSpace(AppliedWhCode) ? "All warehouses" : AppliedWhCode!;

    /// <summary>A restatement of the rule in words, so the grid's shape is never a surprise.</summary>
    protected string RuleExplanation => AppliedRule switch
    {
        IvStockAlertRules.Low =>
            "Items whose on-hand across the basis is below the item's own minimum stock. A never-stocked item still alerts here.",
        IvStockAlertRules.Over =>
            "Items whose on-hand exceeds the item's own maximum stock. A maximum of zero or blank means \"not configured\" and never alerts.",
        IvStockAlertRules.Slow =>
            $"Items with stock on hand whose last posted movement is older than {AppliedSlowDays} days. An item with no movement history is reported separately as \"never moved\".",
        IvStockAlertRules.Dead =>
            $"Items with stock on hand whose last posted movement is older than {AppliedDeadDays} days. Dead days must stay above slow days.",
        IvStockAlertRules.NeverMoved =>
            "Items holding stock with no posted movement history at all — typically opening or imported on-hand. This is NOT dead stock: nothing ever moved, so nothing went stale.",
        IvStockAlertRules.Expiring =>
            $"Lots expiring between today and the next {AppliedExpiryDays} days, per warehouse holding them. A lot expiring today is not expired.",
        IvStockAlertRules.Expired =>
            "Lots whose expiry date is already past. Expired stock is the control exception and is never dropped from this list.",
        _ => string.Empty
    };

    protected List<GridColumnData> Columns()
    {
        var index = 1;
        var columns = new List<GridColumnData>
        {
            new() { Caption = "Item", FieldName = nameof(IvStockAlertRow.ICode), Width = "120px", VisibleIndex = index++, SortIndex = 0 },
            new() { Caption = "Description", FieldName = nameof(IvStockAlertRow.IDesc), VisibleIndex = index++ },
            new() { Caption = "Class", FieldName = nameof(IvStockAlertRow.IClassCode), Width = "100px", VisibleIndex = index++ },
            new() { Caption = "Std UOM", FieldName = nameof(IvStockAlertRow.StdUom), Width = "80px", VisibleIndex = index++ }
        };

        if (RuleIsThreshold)
        {
            columns.Add(new() { Caption = "Min stock", FieldName = nameof(IvStockAlertRow.MinStock), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = index++ });
            columns.Add(new() { Caption = "Max stock", FieldName = nameof(IvStockAlertRow.MaxStock), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = index++ });
        }

        columns.Add(new() { Caption = "On hand", FieldName = nameof(IvStockAlertRow.OnHand), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = index++ });

        if (RuleIsThreshold)
        {
            columns.Add(new() { Caption = "Variance", FieldName = nameof(IvStockAlertRow.Variance), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = index++ });
        }

        if (RuleNeedsDays)
        {
            columns.Add(new() { Caption = "Last movement", FieldName = nameof(IvStockAlertRow.LastMovement), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "120px", VisibleIndex = index++ });
            columns.Add(new() { Caption = "Days since", FieldName = nameof(IvStockAlertRow.DaysSinceMovement), DataType = "int", Width = "100px", VisibleIndex = index++ });
        }

        if (RuleIsExpiry)
        {
            columns.Add(new() { Caption = "Warehouse", FieldName = nameof(IvStockAlertRow.WhCode), Width = "100px", VisibleIndex = index++ });
            columns.Add(new() { Caption = "Lot", FieldName = nameof(IvStockAlertRow.LotNo), Width = "130px", VisibleIndex = index++ });
            columns.Add(new() { Caption = "Expiry", FieldName = nameof(IvStockAlertRow.ExpiryDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = index++ });
            columns.Add(new() { Caption = "Days to expiry", FieldName = nameof(IvStockAlertRow.DaysToExpiry), DataType = "int", Width = "110px", VisibleIndex = index++ });
        }

        columns.Add(new() { Caption = "Threshold basis", FieldName = nameof(IvStockAlertRow.ThresholdBasis), Width = "130px", VisibleIndex = index++ });
        columns.AddRange(AuditColumns.For(index));
        return columns;
    }

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new IvStockAlertGridDataSource(SearchPageAsync);

        CanExport = await AccessRights.CanAsync(MenuCodes.InventoryStockAlerts, PermissionCodes.Export);

        // The same company-local clock the service uses, so the header is populated before the first
        // search returns and cannot drift from it.
        AsOfDate = Dates.Today;

        Buttons =
        [
            new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
            new() { Text = "EXPORT", IConClass = "fa-solid fa-file-excel", Style = "primary", Enabled = CanExport }
        ];

        await LoadLookupsAsync();
        await ReloadGridAsync();
        IsBootstrapping = false;
    }
    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected async Task OnButtonClick(SelectedButtonInfo<IvStockAlertRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadGridAsync();
                break;
            case "EXPORT":
                await OnExportAsync();
                break;
        }
    }

    /// <summary>
    /// The toolbar rule selector. Changing the rule changes the question, so it reloads immediately —
    /// the popup's Apply is for filters only.
    /// </summary>
    protected async Task OnRuleChanged(string? rule)
    {
        AppliedRule = IvStockAlertRules.Normalize(rule);
        await ReloadGridAsync();
    }

    protected async Task OnItemSelectedAsync(IvStockMasterLookupRow item)
    {
        AppliedICode = item?.ICode;
        await ReloadGridAsync();
    }

    protected Task OnItemCodeChangedAsync(string? code)
    {
        AppliedICode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        return Task.CompletedTask;
    }

    protected void OpenFilterPopup()
    {
        DraftRule = AppliedRule;
        DraftICode = AppliedICode;
        DraftWhCode = AppliedWhCode;
        DraftIClassCode = AppliedIClassCode;
        DraftSlowDays = AppliedSlowDays;
        DraftDeadDays = AppliedDeadDays;
        DraftExpiryDays = AppliedExpiryDays;
        DraftIncludeInactive = AppliedIncludeInactive;
        DraftIncludeNonStockControl = AppliedIncludeNonStockControl;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedRule = IvStockAlertRules.Normalize(DraftRule);
        AppliedICode = Normalize(DraftICode);
        AppliedWhCode = Normalize(DraftWhCode);
        AppliedIClassCode = Normalize(DraftIClassCode);
        AppliedSlowDays = Math.Max(1, DraftSlowDays);
        AppliedDeadDays = Math.Max(1, DraftDeadDays);
        AppliedExpiryDays = Math.Max(1, DraftExpiryDays);
        AppliedIncludeInactive = DraftIncludeInactive;
        AppliedIncludeNonStockControl = DraftIncludeNonStockControl;
        FilterPopupVisible = false;
        await ReloadGridAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftRule = IvStockAlertRules.Default;
        DraftICode = null;
        DraftWhCode = null;
        DraftIClassCode = null;
        DraftSlowDays = 90;
        DraftDeadDays = 180;
        DraftExpiryDays = 30;
        DraftIncludeInactive = false;
        DraftIncludeNonStockControl = false;

        AppliedRule = IvStockAlertRules.Default;
        AppliedICode = null;
        AppliedWhCode = null;
        AppliedIClassCode = null;
        AppliedSlowDays = 90;
        AppliedDeadDays = 180;
        AppliedExpiryDays = 30;
        AppliedIncludeInactive = false;
        AppliedIncludeNonStockControl = false;
        FilterPopupVisible = false;
        await ReloadGridAsync();
    }

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError() => ErrorMessage = null;

    private async Task ReloadGridAsync()
    {
        DataSource.UpdateFilters(BuildQuery());
        await RefreshSummaryAsync();

        // The column set depends on the rule, so a rule change rebuilds the grid as well as reloading it.
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private IvStockAlertQuery BuildQuery() =>
        new()
        {
            Rule = AppliedRule,
            ICode = AppliedICode,
            WhCode = AppliedWhCode,
            IClassCode = AppliedIClassCode,
            SlowDays = AppliedSlowDays,
            DeadDays = AppliedDeadDays,
            ExpiryDays = AppliedExpiryDays,
            IncludeInactive = AppliedIncludeInactive,
            IncludeNonStockControl = AppliedIncludeNonStockControl
        };

    private async Task RefreshSummaryAsync()
    {
        var query = BuildQuery();
        query.Skip = 0;
        query.Take = 1;

        var result = await Alerts.GetSummaryAsync(MenuCodes.InventoryStockAlerts, query);
        if (result.Succeeded && result.Data is not null)
        {
            TotalCount = result.Data.TotalRows;
            SummaryItemCount = result.Data.ItemCount;
            SummaryTotalOnHand = result.Data.TotalOnHand;
        }
        else
        {
            TotalCount = 0;
            SummaryItemCount = 0;
            SummaryTotalOnHand = 0m;
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                ErrorMessage = result.Message;
            }
        }
    }

    private async Task<(IReadOnlyList<IvStockAlertRow> Rows, int TotalCount)> SearchPageAsync(
        IvStockAlertQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Alerts.SearchAsync(MenuCodes.InventoryStockAlerts, query, cancellationToken);
        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.Message ?? "Unable to load alerts.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        // The as-of date the service actually used, so the header can never show a different date from
        // the one the rules were evaluated against.
        await InvokeAsync(() =>
        {
            if (result.Data.AsOfDate != default)
            {
                AsOfDate = result.Data.AsOfDate;
            }

            TotalCount = result.Data.TotalCount;
        });

        return (result.Data.Rows, result.Data.TotalCount);
    }

    private async Task LoadLookupsAsync()
    {
        var warehouses = await Lookups.ListActiveWarehousesAsync();
        var classes = await Lookups.ListActiveClassesAsync();

        Warehouses = warehouses.Succeeded ? warehouses.Rows : [];
        Classes = classes.Succeeded ? classes.Rows : [];

        if (!warehouses.Succeeded || !classes.Succeeded)
        {
            ErrorMessage = warehouses.ErrorMessage ?? classes.ErrorMessage ?? "Unable to load filter lookups.";
        }
    }

    private async Task OnExportAsync()
    {
        if (!CanExport)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        var url = QueryHelpers.AddQueryString("/inventory/stock-alerts/export", BuildQueryDictionary());
        Navigation.NavigateTo(url, forceLoad: true);
        await Task.CompletedTask;
    }

    /// <summary>
    /// The applied rule and filters — never the grid's current page. Company and branch are absent on
    /// purpose: the endpoint resolves them server-side (D19).
    /// </summary>
    private Dictionary<string, string?> BuildQueryDictionary()
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["rule"] = AppliedRule,
            ["slowDays"] = AppliedSlowDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["deadDays"] = AppliedDeadDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["expiryDays"] = AppliedExpiryDays.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        AddText(dict, "iCode", AppliedICode);
        AddText(dict, "whCode", AppliedWhCode);
        AddText(dict, "iClassCode", AppliedIClassCode);

        if (AppliedIncludeInactive)
        {
            dict["includeInactive"] = "true";
        }

        if (AppliedIncludeNonStockControl)
        {
            dict["includeNonStockControl"] = "true";
        }

        return dict;
    }

    private static void AddText(Dictionary<string, string?> dict, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            dict[key] = value.Trim();
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Server-side paging source for the alerts via <see cref="IIvStockAlertService.SearchAsync"/>. The
/// filters are rebuilt from the applied state on every reload, so the grid can never page with a stale
/// rule.
/// </summary>
public sealed class IvStockAlertGridDataSource : GridCustomDataSource
{
    private readonly Func<IvStockAlertQuery, CancellationToken, Task<(IReadOnlyList<IvStockAlertRow> Rows, int TotalCount)>> _loader;
    private IvStockAlertQuery _filters = new();

    public IvStockAlertGridDataSource(
        Func<IvStockAlertQuery, CancellationToken, Task<(IReadOnlyList<IvStockAlertRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public void UpdateFilters(IvStockAlertQuery query) => _filters = Clone(query);

    public override async Task<int> GetItemCountAsync(
        GridCustomDataSourceCountOptions options,
        CancellationToken cancellationToken)
    {
        var query = Clone(_filters);
        query.Skip = 0;
        query.Take = 1;
        var (_, total) = await _loader(query, cancellationToken);
        return total;
    }

    public override async Task<IList> GetItemsAsync(
        GridCustomDataSourceItemsOptions options,
        CancellationToken cancellationToken)
    {
        var query = Clone(_filters);
        query.Skip = Math.Max(0, options.StartIndex);
        query.Take = options.Count <= 0 ? 50 : options.Count;
        var (rows, _) = await _loader(query, cancellationToken);
        return rows.ToList();
    }

    private static IvStockAlertQuery Clone(IvStockAlertQuery source) =>
        new()
        {
            Rule = source.Rule,
            AsOfDate = source.AsOfDate,
            ICode = source.ICode,
            WhCode = source.WhCode,
            IClassCode = source.IClassCode,
            SearchText = source.SearchText,
            SlowDays = source.SlowDays,
            DeadDays = source.DeadDays,
            ExpiryDays = source.ExpiryDays,
            IncludeInactive = source.IncludeInactive,
            IncludeNonStockControl = source.IncludeNonStockControl,
            SortField = source.SortField,
            SortDescending = source.SortDescending,
            Skip = source.Skip,
            Take = source.Take
        };
}
