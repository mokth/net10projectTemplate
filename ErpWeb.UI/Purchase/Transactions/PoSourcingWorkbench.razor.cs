using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ErpWeb.Core.Purchase;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Transactions;

public partial class PoSourcingWorkbench : PageBase
{
    [Inject] private IPoSourcingService SourcingService { get; set; } = default!;

    [Parameter] public string Mode { get; set; } = "new";
    [Parameter] public long? SourcingId { get; set; }

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool IsDirty;
    protected int ActiveTabIndex;

    protected string? StatusMessage;
    protected IReadOnlyDictionary<string, string> ValidationErrors { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    protected PoSourcingLookupsDto Lookups { get; set; } = new();
    protected PoSourcingDocumentDto? Document { get; set; }
    protected PoSourcingComparisonDto? Comparison { get; set; }

    protected string SourceType { get; set; } = PoSourcingSourceTypes.Direct;
    protected DateTime RequestDate { get; set; } = DateTime.Today;
    protected DateTime? ResponseDueDate { get; set; }
    protected DateTime? RequiredEtaDate { get; set; }
    protected string Title { get; set; } = string.Empty;
    protected string? Requester { get; set; }
    protected string? BuyerCode { get; set; }
    protected string? DepartmentCode { get; set; }
    protected string? ProjectId { get; set; }
    protected string BaseCurrency { get; set; } = "MYR";
    protected string? DefaultToWarehouse { get; set; }
    protected string? Remarks { get; set; }
    protected string? InternalNotes { get; set; }
    protected byte[] RowVersion { get; set; } = Array.Empty<byte>();

    protected List<PoSourcingLineDto> Lines { get; set; } = [];
    protected List<PoSourcingSupplierDto> Suppliers { get; set; } = [];
    protected List<PoSourcingQuoteDto> Quotes { get; set; } = [];
    protected List<PoSourcingAwardDto> Awards { get; set; } = [];
    protected List<PoSourcingConversionDto> Conversions { get; set; } = [];

    protected bool LinePopupVisible;
    protected PoSourcingLineDto EditingLine { get; set; } = new();
    protected bool IsEditingExistingLine;

    protected bool SupplierPopupVisible;
    protected PoSourcingSupplierDto EditingSupplier { get; set; } = new();

    protected bool ConversionPopupVisible;
    protected string PendingConversionTargetType { get; set; } = PoSourcingConversionTargetTypes.Po;
    protected Guid PendingConversionRequestId { get; set; }
    protected PoSourcingConversionPreviewDto? ConversionPreview { get; set; }

    private readonly Dictionary<long, PoSourcingAwardLineInputDto> _awardDraftsByLineId = new();

    protected IReadOnlyList<CodeNameOption> SourceTypeOptions { get; } =
    [
        new(PoSourcingSourceTypes.Direct, "Direct RFQ"),
        new(PoSourcingSourceTypes.Pr, "PR Sourcing")
    ];

    protected bool IsNew => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase) || SourcingId is null || SourcingId <= 0;
    protected bool IsReadOnlyMode => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected bool CanEditDocument => !IsReadOnlyMode && (IsNew ? Lookups.CanAdd : (Document?.CanEdit ?? Lookups.CanEdit));
    protected bool CanEditFromView => IsReadOnlyMode && (Document?.CanEdit ?? false);
    protected bool CanApproveAward => !IsReadOnlyMode && Lookups.CanApprove;

    protected string PageHeading => "Purchase Sourcing (RFQ)";
    protected string SourcingNoDisplay => Document?.SourcingNo ?? "NEW";
    protected string StatusDisplay => Document?.Status ?? PoSourcingStatuses.Draft;
    protected string ModeChip => IsNew ? "NEW" : (IsReadOnlyMode ? "VIEW" : "EDIT");
    protected int ActiveQuoteCount => Quotes.Count(q => q.IsLatestRevision && q.Status == PoSourcingQuoteStatuses.Received);
    protected PoSourcingAwardDto? LatestApprovedAward =>
        Awards.FirstOrDefault(a => a.Status == PoSourcingAwardStatuses.Approved);

    protected override async Task OnParametersSetAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        ValidationErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var lookupRes = await SourcingService.GetLookupsAsync();
        if (!lookupRes.Succeeded || lookupRes.Lookups is null)
        {
            ErrorMessage = lookupRes.ErrorMessage ?? "Unable to load Purchase Sourcing lookups.";
            IsLoading = false;
            return;
        }

        Lookups = lookupRes.Lookups;
        BaseCurrency = Lookups.BaseCurrency;

        if (!IsNew && SourcingId is long id && id > 0)
        {
            await LoadExistingDocumentAsync(id);
        }
        else
        {
            InitializeNewDraft();
        }

        IsLoading = false;
    }

    private void InitializeNewDraft()
    {
        Document = null;
        Comparison = null;
        SourceType = PoSourcingSourceTypes.Direct;
        RequestDate = DateTime.Today;
        ResponseDueDate = DateTime.Today.AddDays(7);
        RequiredEtaDate = null;
        Title = string.Empty;
        BuyerCode = null;
        DepartmentCode = null;
        ProjectId = null;
        BaseCurrency = Lookups.BaseCurrency;
        DefaultToWarehouse = Lookups.Warehouses.FirstOrDefault()?.WarehouseCode;
        Remarks = null;
        InternalNotes = null;
        RowVersion = Array.Empty<byte>();
        Lines = [];
        Suppliers = [];
        Quotes = [];
        Awards = [];
        Conversions = [];
        ActiveTabIndex = 0;
        IsDirty = false;
    }

    private async Task LoadExistingDocumentAsync(long sourcingId)
    {
        var res = await SourcingService.GetAsync(sourcingId);
        if (!res.Succeeded || res.Document is null)
        {
            ErrorMessage = res.ErrorMessage ?? "Sourcing document was not found.";
            return;
        }

        BindDocument(res.Document);
        var compRes = await SourcingService.GetComparisonAsync(sourcingId);
        if (compRes.Succeeded)
        {
            Comparison = compRes.Comparison;
        }
    }

    private void BindDocument(PoSourcingDocumentDto doc)
    {
        Document = doc;
        SourceType = doc.SourceType;
        RequestDate = doc.RequestDate;
        ResponseDueDate = doc.ResponseDueDate;
        RequiredEtaDate = doc.RequiredEtaDate;
        Title = doc.Title;
        Requester = doc.Requester;
        BuyerCode = doc.BuyerCode;
        DepartmentCode = doc.DepartmentCode;
        ProjectId = doc.ProjectId;
        BaseCurrency = doc.BaseCurrency;
        DefaultToWarehouse = doc.DefaultToWarehouse;
        Remarks = doc.Remarks;
        InternalNotes = doc.InternalNotes;
        RowVersion = doc.RowVersion;

        Lines = doc.Lines.ToList();
        Suppliers = doc.Suppliers.ToList();
        Quotes = doc.Quotes.ToList();
        Awards = doc.Awards.ToList();
        Conversions = doc.Conversions.ToList();
        IsDirty = false;
    }

    protected async Task SelectTabAsync(int index)
    {
        ActiveTabIndex = index;
        if (index == 2 && SourcingId is long id && id > 0)
        {
            var compRes = await SourcingService.GetComparisonAsync(id);
            if (compRes.Succeeded)
            {
                Comparison = compRes.Comparison;
            }
        }
    }

    protected void MarkDirty() => IsDirty = true;
    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError()
    {
        ErrorMessage = null;
        ValidationErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    protected void OpenNewLinePopup()
    {
        IsEditingExistingLine = false;
        EditingLine = new PoSourcingLineDto
        {
            LineNo = (short)(Lines.Count + 1),
            RequestedPurchaseQty = 1m,
            RequestedPurchaseUom = "EA",
            PackSize = 1m,
            StdUom = "EA",
            ToWarehouse = DefaultToWarehouse
        };
        LinePopupVisible = true;
    }

    protected void OpenEditLinePopup(PoSourcingLineDto row)
    {
        IsEditingExistingLine = true;
        EditingLine = new PoSourcingLineDto
        {
            SourcingLineId = row.SourcingLineId,
            LineNo = row.LineNo,
            SourcePrNo = row.SourcePrNo,
            SourcePrLine = row.SourcePrLine,
            WorkOrderMaterialId = row.WorkOrderMaterialId,
            OneTimeItemYn = row.OneTimeItemYn,
            ItemCode = row.ItemCode,
            ItemDescription = row.ItemDescription,
            RequestedPurchaseQty = row.RequestedPurchaseQty,
            RequestedPurchaseUom = row.RequestedPurchaseUom,
            PackSize = row.PackSize,
            StdUom = row.StdUom,
            ToWarehouse = row.ToWarehouse,
            Remarks = row.Remarks
        };
        LinePopupVisible = true;
    }

    protected void CommitLinePopup()
    {
        if (string.IsNullOrWhiteSpace(EditingLine.ItemDescription) && string.IsNullOrWhiteSpace(EditingLine.ItemCode))
        {
            ErrorMessage = "Item code or description is required.";
            return;
        }

        if (IsEditingExistingLine)
        {
            var idx = Lines.FindIndex(x => x.LineNo == EditingLine.LineNo);
            if (idx >= 0)
            {
                Lines[idx] = EditingLine;
            }
        }
        else
        {
            EditingLine.LineNo = (short)(Lines.Count + 1);
            Lines.Add(EditingLine);
        }

        LinePopupVisible = false;
        IsDirty = true;
    }

    protected void RemoveLine(PoSourcingLineDto row)
    {
        Lines.Remove(row);
        short n = 1;
        foreach (var l in Lines)
        {
            l.LineNo = n++;
        }

        IsDirty = true;
    }

    protected async Task OpenImportPrLinesPopupAsync()
    {
        var res = await SourcingService.GetEligiblePrLinesAsync(null, null);
        if (!res.Succeeded || res.EligiblePrLines is null)
        {
            ErrorMessage = res.ErrorMessage ?? "Unable to load eligible PR lines.";
            return;
        }

        foreach (var prLine in res.EligiblePrLines.Take(10))
        {
            if (Lines.Any(l => l.SourcePrNo == prLine.PrNo && l.SourcePrLine == prLine.PrLine))
            {
                continue;
            }

            Lines.Add(new PoSourcingLineDto
            {
                LineNo = (short)(Lines.Count + 1),
                SourcePrNo = prLine.PrNo,
                SourcePrLine = prLine.PrLine,
                WorkOrderMaterialId = prLine.WorkOrderMaterialId,
                OneTimeItemYn = prLine.OneTimeItemYn == true,
                ItemCode = prLine.ICode,
                ItemDescription = prLine.IDesc ?? prLine.ICode ?? string.Empty,
                Category = prLine.Category,
                RequestedPurchaseQty = prLine.RemainingPurchaseQty,
                RequestedPurchaseUom = prLine.PurchaseUom,
                PackSize = prLine.PackSz,
                StdUom = prLine.StdUom,
                ToWarehouse = prLine.ToWarehouse ?? DefaultToWarehouse,
                RequiredEtaDate = prLine.EtaDt ?? RequiredEtaDate
            });
        }

        SourceType = PoSourcingSourceTypes.Pr;
        IsDirty = true;
    }

    protected void OpenNewSupplierPopup()
    {
        EditingSupplier = new PoSourcingSupplierDto
        {
            DefaultCurrency = BaseCurrency,
            ResponseStatus = PoSourcingSupplierResponseStatuses.Invited
        };
        SupplierPopupVisible = true;
    }

    protected void CommitSupplierPopup()
    {
        if (string.IsNullOrWhiteSpace(EditingSupplier.SupplierName))
        {
            ErrorMessage = "Supplier name is required.";
            return;
        }

        Suppliers.Add(EditingSupplier);
        SupplierPopupVisible = false;
        IsDirty = true;
    }

    protected void RemoveSupplier(PoSourcingSupplierDto row)
    {
        Suppliers.Remove(row);
        IsDirty = true;
    }

    protected void OpenNewQuotePopup()
    {
        StatusMessage = "Select a supplier row to record or revise a supplier quotation.";
    }

    protected void OpenEditQuotePopup(PoSourcingQuoteDto quote)
    {
        StatusMessage = $"Viewing quotation {quote.SupplierQuoteNo} (Rev {quote.QuoteRevNo}).";
    }

    protected PoSourcingAwardLineInputDto GetOrCreateAwardDraft(PoSourcingLineComparisonResult lineRow)
    {
        if (!_awardDraftsByLineId.TryGetValue(lineRow.SourcingLineId, out var draft))
        {
            var defaultQuoteLineId = lineRow.RecommendedQuoteLineId
                ?? lineRow.Offers.FirstOrDefault(c => c.IsComparable)?.QuoteLineId
                ?? 0;
            draft = new PoSourcingAwardLineInputDto
            {
                SourcingLineId = lineRow.SourcingLineId,
                QuoteLineId = defaultQuoteLineId,
                AwardedPurchaseQty = lineRow.RequestedQty
            };
            _awardDraftsByLineId[lineRow.SourcingLineId] = draft;
        }

        return draft;
    }

    protected void SelectAwardCell(long sourcingLineId, long quoteLineId, decimal requestedPurchaseQty)
    {
        var draft = _awardDraftsByLineId.TryGetValue(sourcingLineId, out var existing)
            ? existing
            : new PoSourcingAwardLineInputDto { SourcingLineId = sourcingLineId };
        draft.QuoteLineId = quoteLineId;
        draft.AwardedPurchaseQty = requestedPurchaseQty;
        _awardDraftsByLineId[sourcingLineId] = draft;
    }

    protected async Task SaveAndApproveAwardAsync()
    {
        if (IsSubmitting || SourcingId is null)
        {
            return;
        }

        using var blocking = BeginBlockingWork("Saving and approving sourcing award…");
        IsSubmitting = true;
        DismissError();
        try
        {
            var inputs = _awardDraftsByLineId.Values
                .Where(x => x.QuoteLineId > 0 && x.AwardedPurchaseQty > 0m)
                .ToList();

            var res = await SourcingService.SaveAwardAsync(new PoSourcingAwardRequest
            {
                SourcingId = SourcingId.Value,
                ApproveImmediately = true,
                SourcingRowVersion = RowVersion,
                Lines = inputs
            });

            if (!res.Succeeded || res.Document is null)
            {
                ErrorMessage = res.ErrorMessage ?? "Unable to approve award.";
                ValidationErrors = res.FieldErrors;
                return;
            }

            BindDocument(res.Document);
            StatusMessage = "Award approved.";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task OpenConversionPopupAsync(string targetType)
    {
        if (SourcingId is null || LatestApprovedAward is null)
        {
            return;
        }

        PendingConversionTargetType = targetType;
        PendingConversionRequestId = Guid.NewGuid();
        var previewRes = await SourcingService.PreviewConversionAsync(new PoSourcingConversionPreviewRequest
        {
            SourcingId = SourcingId.Value,
            AwardId = LatestApprovedAward.AwardId,
            TargetType = targetType
        });

        if (!previewRes.Succeeded || previewRes.ConversionPreview is null)
        {
            ErrorMessage = previewRes.ErrorMessage ?? "Unable to preview conversion.";
            return;
        }

        ConversionPreview = previewRes.ConversionPreview;
        ConversionPopupVisible = true;
    }

    protected async Task ExecuteConversionAsync()
    {
        if (IsSubmitting || SourcingId is null || LatestApprovedAward is null)
        {
            return;
        }

        using var blocking = BeginBlockingWork("Converting award…");
        IsSubmitting = true;
        DismissError();
        try
        {
            PoSourcingOperationResult res;
            if (PendingConversionTargetType == PoSourcingConversionTargetTypes.Pr)
            {
                res = await SourcingService.ConvertToPrAsync(new PoSourcingConvertToPrRequest
                {
                    SourcingId = SourcingId.Value,
                    AwardId = LatestApprovedAward.AwardId,
                    RequestId = PendingConversionRequestId
                });
            }
            else
            {
                res = await SourcingService.ConvertToPoAsync(new PoSourcingConvertToPoRequest
                {
                    SourcingId = SourcingId.Value,
                    AwardId = LatestApprovedAward.AwardId,
                    RequestId = PendingConversionRequestId
                });
            }

            if (!res.Succeeded)
            {
                ErrorMessage = res.ErrorMessage ?? "Conversion failed.";
                return;
            }

            ConversionPopupVisible = false;
            if (res.Document is not null)
            {
                BindDocument(res.Document);
            }

            var createdDocs = string.Join(", ", res.ConversionResult?.CreatedDocumentNumbers ?? Array.Empty<string>());
            StatusMessage = $"Created {PendingConversionTargetType}: {createdDocs}";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task OnSaveDraftAsync()
    {
        if (IsSubmitting)
        {
            return;
        }

        using var blocking = BeginBlockingWork("Saving sourcing RFQ…");
        IsSubmitting = true;
        DismissError();
        try
        {
            var res = await SourcingService.SaveDraftAsync(new PoSourcingSaveRequest
            {
                SourcingId = IsNew ? null : SourcingId,
                SourceType = SourceType,
                RequestDate = RequestDate,
                ResponseDueDate = ResponseDueDate,
                RequiredEtaDate = RequiredEtaDate,
                Title = Title,
                Requester = Requester,
                BuyerCode = BuyerCode,
                DepartmentCode = DepartmentCode,
                ProjectId = ProjectId,
                BaseCurrency = BaseCurrency,
                DefaultToWarehouse = DefaultToWarehouse,
                Remarks = Remarks,
                InternalNotes = InternalNotes,
                MarkAsOpen = true,
                RowVersion = RowVersion,
                Lines = Lines,
                Suppliers = Suppliers
            });

            if (!res.Succeeded || res.Document is null)
            {
                ErrorMessage = res.ErrorMessage ?? "Unable to save sourcing RFQ.";
                ValidationErrors = res.FieldErrors;
                return;
            }

            BindDocument(res.Document);
            StatusMessage = "Sourcing RFQ saved.";
            if (IsNew)
            {
                Navigation.NavigateTo($"/purchase/sourcing/edit/{res.Document.SourcingId}", replace: true);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void OnCancel() => Navigation.NavigateTo("/purchase/sourcing");
    protected void OnClose() => Navigation.NavigateTo("/purchase/sourcing");
    protected void OnEditFromView()
    {
        if (SourcingId is long id && id > 0)
        {
            Navigation.NavigateTo($"/purchase/sourcing/edit/{id}");
        }
    }

    protected sealed record CodeNameOption(string Code, string Name);
}
