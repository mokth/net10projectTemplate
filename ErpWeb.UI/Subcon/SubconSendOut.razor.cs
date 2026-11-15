using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.Core.Subcon;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Subcon;

public partial class SubconSendOut : PageBase
{
    [Parameter] public string Mode { get; set; } = "new";
    [Parameter] public string? DocNo { get; set; }
    [SupplyParameterFromQuery(Name = "kind")] public string? KindQuery { get; set; }

    [Inject] private ISubconApplicationService Subcon { get; set; } = default!;
    [Inject] private ISubconBomReadService BomReader { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private ICurrentDateService Clock { get; set; } = default!;
    [Inject] private IIvInventoryLookupService InventoryLookups { get; set; } = default!;

    private bool _isDirty;

    protected bool IsSubmitting;
    protected bool LinePopupVisible;
    protected bool DraftPreviewVisible;
    protected bool DiscardConfirmVisible;
    protected bool IsLoadingWarehouses;
    protected bool IsLoadingLocations;
    protected bool IsLoadingUoms;
    protected bool CanUseProductionMode;
    protected bool CanEditAfterView;
    protected string? StatusMessage;
    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; private set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Locations { get; private set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Uoms { get; private set; } = [];
    protected IReadOnlyList<JobKindOption> JobKindOptions { get; } =
    [
        new(SubconJobKinds.Processing, "Send for processing"),
        new(SubconJobKinds.Assembly, "Assemble product")
    ];
    protected HeaderVm Header { get; set; } = new();
    protected List<LineVm> Lines { get; set; } = [];
    protected LineVm DraftLine { get; set; } = new();

    protected bool IsNew => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsView => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected bool IsAssemblyJob => SubconJobKinds.IsAssembly(Header.JobKind);
    protected bool CanEditDocument => !IsView && SubconStatuses.CanEdit(Header.Status);
    protected bool CanEditDocumentAfterView => CanEditAfterView && SubconStatuses.CanEdit(Header.Status);
    protected bool CanSave => CanEditDocument && !IsSubmitting && Lines.Count > 0 && !string.IsNullOrWhiteSpace(Header.SupplierCode);
    protected string PageHeading => IsNew ? "Send Out" : $"Send Out {Header.DocNo}";
    protected string ModeChip => IsNew ? "New" : IsView ? "View" : "Edit";
    protected string DocNoDisplay => string.IsNullOrWhiteSpace(Header.DocNo) ? "(assigned on save)" : Header.DocNo;
    protected string LineCountLabel => Lines.Count == 1 ? "1 line" : $"{Lines.Count:N0} lines";

    protected override async Task OnPageInitializedAsync()
    {
        CanEditAfterView = await AccessRights.CanAsync(MenuCodes.SubconSendOut, PermissionCodes.Edit);
        if (IsNew)
        {
            var initialJobKind = string.Equals(KindQuery, "assembly", StringComparison.OrdinalIgnoreCase)
                ? SubconJobKinds.Assembly
                : SubconJobKinds.Processing;
            Header = new HeaderVm
            {
                TrxDate = Clock.Today,
                Status = SubconStatuses.New,
                Mode = SubconModes.Inventory,
                JobKind = initialJobKind,
                TargetQty = 1m
            };
            var probe = await Subcon.SearchAsync(new SubconListQuery { Take = 1 });
            CanUseProductionMode = probe.CanUseProductionMode;
            return;
        }

        if (string.IsNullOrWhiteSpace(DocNo))
        {
            ErrorMessage = SubconMessages.DispatchNotFound;
            return;
        }

        var result = await Subcon.GetDispatchAsync(DocNo);
        if (!result.Succeeded || result.Dispatch is null)
        {
            ErrorMessage = result.ErrorMessage ?? SubconMessages.DispatchNotFound;
            return;
        }

        CanUseProductionMode = result.CanUseProductionMode;
        Header = HeaderVm.From(result.Dispatch);
        Lines = result.Dispatch.Lines.Select(LineVm.From).ToList();
        _isDirty = false;
    }

    protected void OnTrxDateChanged(DateTime date)
    {
        Header.TrxDate = date;
        _isDirty = true;
    }

    protected void OnPromisedReturnDateChanged(DateTime? date)
    {
        Header.PromisedReturnDate = date;
        _isDirty = true;
    }

    protected void OnPoNoChanged(string? value)
    {
        Header.PoNo = value;
        _isDirty = true;
    }

    protected void OnRemarksChanged(string? value)
    {
        Header.Remarks = value;
        _isDirty = true;
    }

    protected void OnSupplierSelected(PoSupplierLookupRow row)
    {
        Header.SupplierCode = row.SuppCode;
        Header.SupplierName = row.SuppName;
        _isDirty = true;
    }

    protected void OnJobKindChanged(string jobKind)
    {
        Header.JobKind = string.IsNullOrWhiteSpace(jobKind) ? SubconJobKinds.Processing : jobKind;
        _isDirty = true;
    }

    protected void OnTargetItemSelected(IvStockMasterLookupRow row)
    {
        Header.TargetItemCode = row.ICode;
        Header.TargetItemName = row.IDesc;
        Header.TargetUom = row.StdUom;
        _isDirty = true;
    }

    protected void OnTargetQtyChanged(decimal qty)
    {
        Header.TargetQty = qty;
        _isDirty = true;
    }

    protected void OnManualKitChanged(bool value)
    {
        Header.IsManualKit = value;
        _isDirty = true;
    }

    protected async Task LoadBomAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(Header.TargetItemCode) || Header.TargetQty <= 0m)
        {
            ErrorMessage = SubconMessages.TargetItemRequired;
            return;
        }

        await EnsureMasterLookupsLoadedAsync();
        var fallbackWh = Warehouses.FirstOrDefault()?.Code;
        var bomResult = await BomReader.ExplodeForSubconAsync(new SubconBomExplosionRequest
        {
            ProdCode = Header.TargetItemCode,
            Quantity = Header.TargetQty
        });

        if (!bomResult.Succeeded || bomResult.Snapshot is null)
        {
            ErrorMessage = bomResult.ErrorMessage ?? "Unable to explode BOM.";
            return;
        }

        Header.TargetItemName = bomResult.Snapshot.ProdName;
        Header.TargetUom = bomResult.Snapshot.Uom;
        Header.DefinitionCode = bomResult.Snapshot.DefinitionCode;
        Header.BomVersion = bomResult.Snapshot.BomVersion;
        Lines = bomResult.Snapshot.Materials.Select(c => new LineVm
        {
            LineNo = c.LineNo,
            ItemCode = c.ItemCode,
            ItemName = c.ItemName,
            Uom = c.Uom,
            RequiredQty = c.RequiredQty,
            Qty = c.RequiredQty,
            WarehouseCode = c.WarehouseCode ?? fallbackWh,
            LocationCode = c.LocationCode
        }).ToList();
        _isDirty = true;
        StatusMessage = $"Loaded {Lines.Count} component(s) from BOM {Header.DefinitionCode} v{Header.BomVersion}.";
    }

    protected async Task OpenAddLinePopupAsync()
    {
        ErrorMessage = null;
        DraftLine = new LineVm();
        Locations = [];
        await EnsureMasterLookupsLoadedAsync();
        LinePopupVisible = true;
    }

    protected async Task OnBalLocSelectedAsync(IvBalLocLookupRow row)
    {
        DraftLine.BalLocId = row.Id;
        DraftLine.ItemCode = row.ICode;
        DraftLine.ItemName = row.IDesc;
        DraftLine.Uom = row.StdUom ?? DraftLine.Uom;
        DraftLine.WarehouseCode = row.WhCode;
        await LoadLocationsAsync(DraftLine.WarehouseCode);
        DraftLine.LocationCode = row.LocCode;
        if (DraftLine.Qty <= 0m && row.StdQty > 0m)
        {
            DraftLine.Qty = row.StdQty;
        }
    }

    protected async Task OnItemSelectedAsync(IvStockMasterLookupRow row)
    {
        DraftLine.ItemCode = row.ICode;
        DraftLine.ItemName = row.IDesc;
        DraftLine.Uom = row.StdUom ?? DraftLine.Uom;
        DraftLine.WarehouseCode = row.DefWarehouse ?? DraftLine.WarehouseCode;
        await LoadLocationsAsync(DraftLine.WarehouseCode);
        DraftLine.LocationCode = row.DefLocation;
    }

    protected async Task OnDraftWarehouseChangedAsync(string? warehouseCode)
    {
        DraftLine.WarehouseCode = warehouseCode;
        DraftLine.LocationCode = null;
        await LoadLocationsAsync(warehouseCode);
    }

    protected void CommitLine()
    {
        if (string.IsNullOrWhiteSpace(DraftLine.ItemCode))
        {
            ErrorMessage = SubconMessages.ItemRequired;
            return;
        }

        if (string.IsNullOrWhiteSpace(DraftLine.WarehouseCode))
        {
            ErrorMessage = SubconMessages.WarehouseRequired;
            return;
        }

        if (string.IsNullOrWhiteSpace(DraftLine.Uom))
        {
            ErrorMessage = SubconMessages.UomRequired;
            return;
        }

        if (DraftLine.Qty <= 0m)
        {
            ErrorMessage = SubconMessages.QtyRequired;
            return;
        }

        DraftLine.LineNo = Lines.Count == 0 ? 1 : Lines.Max(x => x.LineNo) + 1;
        Lines.Add(DraftLine);
        DraftLine = new LineVm();
        _isDirty = true;
        LinePopupVisible = false;
    }

    protected void RemoveLine(LineVm line)
    {
        Lines.Remove(line);
        var n = 1;
        foreach (var remaining in Lines)
        {
            remaining.LineNo = n++;
        }

        _isDirty = true;
    }

    protected async Task OnSaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        IsSubmitting = true;
        if (IsNew && IsAssemblyJob)
        {
            var jobResult = await Subcon.SaveJobDraftAsync(new SubconJobSaveCommand
            {
                TrxDate = Header.TrxDate,
                JobKind = SubconJobKinds.Assembly,
                Mode = SubconModes.Inventory,
                SupplierCode = Header.SupplierCode,
                TargetItemCode = Header.TargetItemCode,
                TargetUom = Header.TargetUom,
                TargetQty = Header.TargetQty,
                DefinitionCode = Header.DefinitionCode,
                BomVersion = Header.BomVersion,
                IsManualKit = Header.IsManualKit,
                PromisedReturnDate = Header.PromisedReturnDate,
                PoNo = Header.PoNo,
                Remarks = Header.Remarks,
                Materials = Lines.Select(x => new SubconJobMaterialSaveCommand
                {
                    LineNo = x.LineNo,
                    ItemCode = x.ItemCode,
                    Uom = x.Uom,
                    RequiredQty = x.RequiredQty > 0m ? x.RequiredQty : x.Qty,
                    SendNowQty = x.Qty,
                    WarehouseCode = x.WarehouseCode,
                    LocationCode = x.LocationCode,
                    IvBalLocId = x.BalLocId,
                    Remarks = x.Remarks
                }).ToArray()
            });
            IsSubmitting = false;
            if (!jobResult.Succeeded || jobResult.Job is null || string.IsNullOrWhiteSpace(jobResult.Job.PrimaryDispatchDocNo))
            {
                ErrorMessage = jobResult.ErrorMessage;
                return;
            }

            _isDirty = false;
            Navigation.NavigateTo($"/subcon/send-out/view/{jobResult.Job.PrimaryDispatchDocNo}");
            return;
        }

        var result = await Subcon.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            DocNo = IsNew ? null : Header.DocNo,
            TrxDate = Header.TrxDate,
            Mode = SubconModes.Inventory,
            SupplierCode = Header.SupplierCode,
            PromisedReturnDate = Header.PromisedReturnDate,
            PoNo = Header.PoNo,
            Remarks = Header.Remarks,
            RowVersion = Header.RowVersion,
            Lines = Lines.Select(x => new SubconDispatchLineSaveCommand
            {
                LineNo = x.LineNo,
                ItemCode = x.ItemCode,
                Uom = x.Uom,
                Qty = x.Qty,
                WarehouseCode = x.WarehouseCode,
                LocationCode = x.LocationCode,
                Remarks = x.Remarks
            }).ToArray()
        });
        IsSubmitting = false;
        if (!result.Succeeded || result.Dispatch is null)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        _isDirty = false;
        Navigation.NavigateTo($"/subcon/send-out/view/{result.Dispatch.DocNo}");
    }

    protected void OnCancel()
    {
        if (!IsView && _isDirty)
        {
            DiscardConfirmVisible = true;
            return;
        }

        Navigation.NavigateTo("/subcon/send-out");
    }

    protected void ConfirmDiscard()
    {
        DiscardConfirmVisible = false;
        _isDirty = false;
        Navigation.NavigateTo("/subcon/send-out");
    }

    private async Task EnsureMasterLookupsLoadedAsync()
    {
        if (Warehouses.Count == 0 && !IsLoadingWarehouses)
        {
            IsLoadingWarehouses = true;
            var wh = await InventoryLookups.ListActiveWarehousesAsync();
            if (wh.Succeeded)
            {
                Warehouses = wh.Rows;
            }
            IsLoadingWarehouses = false;
        }

        if (Uoms.Count == 0 && !IsLoadingUoms)
        {
            IsLoadingUoms = true;
            var uom = await InventoryLookups.ListActiveUomsAsync();
            if (uom.Succeeded)
            {
                Uoms = uom.Rows;
            }
            IsLoadingUoms = false;
        }
    }

    private async Task LoadLocationsAsync(string? warehouseCode)
    {
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            Locations = [];
            return;
        }

        IsLoadingLocations = true;
        var locs = await InventoryLookups.ListActiveLocationsAsync(warehouseCode.Trim());
        Locations = locs.Succeeded ? locs.Rows : [];
        IsLoadingLocations = false;
    }

    protected sealed record JobKindOption(string Code, string Caption);

    protected sealed class HeaderVm
    {
        public string DocNo { get; set; } = string.Empty;
        public DateTime TrxDate { get; set; }
        public DateTime? PromisedReturnDate { get; set; }
        public string Status { get; set; } = SubconStatuses.New;
        public string JobKind { get; set; } = SubconJobKinds.Processing;
        public string Mode { get; set; } = SubconModes.Inventory;
        public string SupplierCode { get; set; } = string.Empty;
        public string? SupplierName { get; set; }
        public string? TargetItemCode { get; set; }
        public string? TargetItemName { get; set; }
        public string? TargetUom { get; set; }
        public decimal TargetQty { get; set; } = 1m;
        public string? DefinitionCode { get; set; }
        public int? BomVersion { get; set; }
        public bool IsManualKit { get; set; }
        public string? PoNo { get; set; }
        public string? Remarks { get; set; }
        public byte[] RowVersion { get; set; } = [];
        public string PostBlockedReason { get; set; } = SubconBlockedReasons.NoAtomicInventorySeam;

        public static HeaderVm From(SubconDispatchDto dto) => new()
        {
            DocNo = dto.DocNo,
            TrxDate = dto.TrxDate,
            PromisedReturnDate = dto.PromisedReturnDate,
            Status = dto.Status,
            Mode = dto.Mode,
            SupplierCode = dto.SupplierCode,
            SupplierName = dto.SupplierName,
            PoNo = dto.PoNo,
            Remarks = dto.Remarks,
            RowVersion = dto.RowVersion,
            PostBlockedReason = dto.PostBlockedReason
        };
    }

    protected sealed class LineVm
    {
        public int LineNo { get; set; }
        public int? BalLocId { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public string? ItemName { get; set; }
        public string? WarehouseCode { get; set; }
        public string? LocationCode { get; set; }
        public decimal RequiredQty { get; set; }
        public decimal Qty { get; set; }
        public string Uom { get; set; } = string.Empty;
        public string? Remarks { get; set; }

        public static LineVm From(SubconDispatchLineDto dto) => new()
        {
            LineNo = dto.LineNo,
            ItemCode = dto.ItemCode,
            ItemName = dto.ItemName,
            WarehouseCode = dto.WarehouseCode,
            LocationCode = dto.LocationCode,
            RequiredQty = dto.Qty,
            Qty = dto.Qty,
            Uom = dto.Uom,
            Remarks = dto.Remarks
        };
    }
}
