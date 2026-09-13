using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Transactions;

public partial class PoInvoice : PageBase
{
    [Parameter] public string Mode { get; set; } = "new";
    [Parameter] public string? DocNo { get; set; }

    [Inject] private IPoInvoiceService Invoices { get; set; } = default!;

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected string? StatusMessage;
    protected PoInvoiceDocument Model { get; set; } = new()
    {
        Type = PoInvoiceTypes.Invoice,
        DocDate = DateTime.Today,
        Status = PoInvoiceStatuses.New,
        CurrRate = 1m
    };

    protected List<PoInvoiceVendorLookupRow> Vendors { get; set; } = [];
    protected List<LineEdit> EditLines { get; set; } = [];
    protected List<PoInvoicePoLinePickerRow> PoPickerRows { get; set; } = [];
    protected IReadOnlyList<object> SelectedPoLines { get; set; } = [];
    protected bool PoPickerVisible;
    protected decimal? PriceToleranceEdit
    {
        get => Model.PriceTolerance;
        set => Model.PriceTolerance = value;
    }

    protected bool IsNew => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsView => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditable => !IsView && (IsNew || Model.CanEdit);
    protected string PageHeading => IsNew
        ? "New Purchase Invoice"
        : $"{(Model.Type == PoInvoiceTypes.CreditNote ? "Credit Note" : "Invoice")} {Model.DocNo}";

    protected override async Task OnPageInitializedAsync()
    {
        var lookups = await Invoices.GetLookupsAsync();
        if (lookups.Succeeded && lookups.Lookups is not null)
        {
            Vendors = lookups.Lookups.Vendors.ToList();
        }

        if (!IsNew && !string.IsNullOrWhiteSpace(DocNo))
        {
            var result = await Invoices.GetAsync(DocNo);
            if (!result.Succeeded || result.Document is null)
            {
                ErrorMessage = result.ErrorMessage ?? "Document was not found.";
                IsLoading = false;
                return;
            }

            Model = result.Document;
            EditLines = Model.Lines.Select(LineEdit.FromDto).ToList();
        }
        else
        {
            Model.DocDate = DateTime.Today;
            Model.Type = PoInvoiceTypes.Invoice;
            Model.CanEdit = true;
        }

        IsLoading = false;
    }

    protected async Task OnVendorChanged(string vendorCode)
    {
        Model.VendorCode = vendorCode ?? string.Empty;
        if (!IsEditable || string.IsNullOrWhiteSpace(Model.VendorCode))
        {
            return;
        }

        var defaults = await Invoices.GetVendorDefaultsAsync(Model.VendorCode, Model.DocDate);
        if (!defaults.Succeeded || defaults.VendorDefaults is null)
        {
            return;
        }

        var d = defaults.VendorDefaults;
        Model.VendorName = d.VendorName;
        Model.Currency = d.Currency;
        Model.CurrRate = d.CurrRate;
        Model.PayCode = d.PayCode;
        Model.TaxGrCode = d.TaxGrCode;
        Model.InvAddress1 = d.InvAddress1;
        Model.InvAddress2 = d.InvAddress2;
        Model.InvAddress3 = d.InvAddress3;
        Model.InvAddress4 = d.InvAddress4;
        Model.City = d.City;
        Model.State = d.State;
        Model.PostalCode = d.PostalCode;
        Model.Country = d.Country;
        Model.Tel = d.Tel;
        Model.Fax = d.Fax;
    }

    protected async Task OpenPoPickerAsync()
    {
        if (string.IsNullOrWhiteSpace(Model.VendorCode))
        {
            ErrorMessage = "Select a vendor first.";
            return;
        }

        var result = await Invoices.SearchInvoiceablePoLinesAsync(Model.VendorCode, null);
        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        PoPickerRows = result.PoLinePickerRows.ToList();
        SelectedPoLines = [];
        PoPickerVisible = true;
    }

    protected void AddSelectedPoLines()
    {
        foreach (var obj in SelectedPoLines)
        {
            if (obj is not PoInvoicePoLinePickerRow row)
            {
                continue;
            }

            if (EditLines.Any(x =>
                    string.Equals(x.PoNo, row.PoNo, StringComparison.OrdinalIgnoreCase)
                    && x.PoRelNo == row.PoRelNo
                    && x.PoLineNo == row.Line))
            {
                continue;
            }

            EditLines.Add(new LineEdit
            {
                ClientKey = Guid.NewGuid().ToString("N"),
                ICode = row.ICode ?? string.Empty,
                IDesc = row.IDesc,
                Qty = row.InvoiceableQty,
                UnitPrice = row.PoUnitPrice,
                SellingUom = row.PurchaseUom,
                TaxGroup = row.TaxGroup,
                IsInclusive = row.IsInclusive,
                OneTime = row.OneTime,
                PoNo = row.PoNo,
                PoRelNo = row.PoRelNo,
                PoLineNo = row.Line,
                NetAmount = Math.Round(row.InvoiceableQty * row.PoUnitPrice, 2),
                Amount = Math.Round(row.InvoiceableQty * row.PoUnitPrice, 2)
            });
        }

        RecalcTotals();
        PoPickerVisible = false;
    }

    protected async Task CopyFromInvAsync()
    {
        if (string.IsNullOrWhiteSpace(Model.InvNo))
        {
            ErrorMessage = "Enter the purchase invoice document number first.";
            return;
        }

        var result = await Invoices.CopyFromInvoiceAsync(Model.InvNo);
        if (!result.Succeeded || result.Document is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to copy invoice.";
            return;
        }

        var src = result.Document;
        Model.VendorCode = src.VendorCode;
        Model.VendorName = src.VendorName;
        Model.Currency = src.Currency;
        Model.CurrRate = src.CurrRate;
        Model.PayCode = src.PayCode;
        Model.TaxGrCode = src.TaxGrCode;
        Model.PriceTolerance = src.PriceTolerance;
        Model.InvAddress1 = src.InvAddress1;
        Model.InvAddress2 = src.InvAddress2;
        Model.InvAddress3 = src.InvAddress3;
        Model.InvAddress4 = src.InvAddress4;
        Model.City = src.City;
        Model.State = src.State;
        Model.PostalCode = src.PostalCode;
        Model.Country = src.Country;
        Model.Tel = src.Tel;
        Model.Fax = src.Fax;
        Model.InvNo = src.InvNo;
        EditLines = src.Lines.Select(LineEdit.FromDto).ToList();
        RecalcTotals();
    }

    protected async Task SaveAsync()
    {
        if (IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var request = BuildRequest();
            var result = IsNew
                ? await Invoices.SaveNewAsync(request)
                : await Invoices.UpdateAsync(Model.DocNo, request);

            if (!result.Succeeded || result.Document is null)
            {
                ErrorMessage = result.ErrorMessage ?? "Save failed.";
                return;
            }

            StatusMessage = "Saved.";
            Navigation.NavigateTo($"/purchase/invoices/edit/{result.Document.DocNo}");
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task PostAsync()
    {
        if (IsSubmitting || string.IsNullOrWhiteSpace(Model.DocNo))
        {
            return;
        }

        IsSubmitting = true;
        try
        {
            var result = await Invoices.PostAsync(
            [
                new PoInvoiceKeyedRequest { DocNo = Model.DocNo, RowVersion = Model.RowVersion }
            ]);
            if (!result.Succeeded)
            {
                ErrorMessage = result.ErrorMessage ?? "Post failed.";
                return;
            }

            StatusMessage = "Posted.";
            await ReloadAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task RollbackAsync()
    {
        if (IsSubmitting || string.IsNullOrWhiteSpace(Model.DocNo))
        {
            return;
        }

        IsSubmitting = true;
        try
        {
            var result = await Invoices.RollbackAsync(
            [
                new PoInvoiceKeyedRequest { DocNo = Model.DocNo, RowVersion = Model.RowVersion }
            ]);
            if (!result.Succeeded)
            {
                ErrorMessage = result.ErrorMessage ?? "Rollback failed.";
                return;
            }

            StatusMessage = "Rolled back.";
            await ReloadAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task ReloadAsync()
    {
        var result = await Invoices.GetAsync(Model.DocNo);
        if (result.Succeeded && result.Document is not null)
        {
            Model = result.Document;
            EditLines = Model.Lines.Select(LineEdit.FromDto).ToList();
        }
    }

    private PoInvoiceSaveRequest BuildRequest() => new()
    {
        Type = Model.Type,
        DocDate = Model.DocDate,
        VendorCode = Model.VendorCode,
        InvNo = Model.InvNo,
        Currency = Model.Currency,
        CurrRate = Model.CurrRate,
        PayCode = Model.PayCode,
        TaxGrCode = Model.TaxGrCode,
        LocationCode = Model.LocationCode,
        ProjId = Model.ProjId,
        Dept = Model.Dept,
        Remarks = Model.Remarks,
        RefNo = Model.RefNo,
        ExternalDocNo = Model.ExternalDocNo,
        PriceTolerance = Model.PriceTolerance,
        InvAddress1 = Model.InvAddress1,
        InvAddress2 = Model.InvAddress2,
        InvAddress3 = Model.InvAddress3,
        InvAddress4 = Model.InvAddress4,
        City = Model.City,
        State = Model.State,
        PostalCode = Model.PostalCode,
        Country = Model.Country,
        Tel = Model.Tel,
        Fax = Model.Fax,
        RowVersion = Model.RowVersion,
        Lines = EditLines.Select(x => new PoInvoiceLineRequest
        {
            ICode = x.ICode,
            IDesc = x.IDesc,
            Qty = x.Qty,
            UnitPrice = x.UnitPrice,
            SellingUom = x.SellingUom,
            ItemDiscount = x.ItemDiscount,
            ItemDiscount1 = x.ItemDiscount1,
            IDiscountType = x.IDiscountType,
            IDiscountType1 = x.IDiscountType1,
            IsInclusive = x.IsInclusive,
            TaxGroup = x.TaxGroup,
            ItemGlCode = x.ItemGlCode,
            Remarks = x.Remarks,
            OneTime = x.OneTime,
            PoNo = x.PoNo,
            PoRelNo = x.PoRelNo,
            PoLineNo = x.PoLineNo
        }).ToList()
    };

    private void RecalcTotals()
    {
        Model.GrossAmnt = EditLines.Sum(x => x.NetAmount);
        Model.Taxes = EditLines.Sum(x => x.TaxAmt);
        Model.TotAmnt = Model.GrossAmnt + Model.Taxes;
    }

    protected void GoBack() => Navigation.NavigateTo("/purchase/invoices");
    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    protected sealed class LineEdit
    {
        public string ClientKey { get; set; } = Guid.NewGuid().ToString("N");
        public string ICode { get; set; } = string.Empty;
        public string? IDesc { get; set; }
        public decimal Qty { get; set; }
        public decimal UnitPrice { get; set; }
        public string? SellingUom { get; set; }
        public decimal ItemDiscount { get; set; }
        public decimal ItemDiscount1 { get; set; }
        public string? IDiscountType { get; set; }
        public string? IDiscountType1 { get; set; }
        public bool IsInclusive { get; set; }
        public string? TaxGroup { get; set; }
        public decimal TaxAmt { get; set; }
        public decimal NetAmount { get; set; }
        public decimal Amount { get; set; }
        public string? ItemGlCode { get; set; }
        public string? Remarks { get; set; }
        public bool? OneTime { get; set; }
        public string? PoNo { get; set; }
        public short? PoRelNo { get; set; }
        public short? PoLineNo { get; set; }

        public static LineEdit FromDto(PoInvoiceLineDto x) => new()
        {
            ClientKey = Guid.NewGuid().ToString("N"),
            ICode = x.ICode,
            IDesc = x.IDesc,
            Qty = x.Qty,
            UnitPrice = x.UnitPrice,
            SellingUom = x.SellingUom,
            ItemDiscount = x.ItemDiscount,
            ItemDiscount1 = x.ItemDiscount1,
            IDiscountType = x.IDiscountType,
            IDiscountType1 = x.IDiscountType1,
            IsInclusive = x.IsInclusive,
            TaxGroup = x.TaxGroup,
            TaxAmt = x.TaxAmt,
            NetAmount = x.NetAmount,
            Amount = x.Amount,
            ItemGlCode = x.ItemGlCode,
            Remarks = x.Remarks,
            OneTime = x.OneTime,
            PoNo = x.PoNo,
            PoRelNo = x.PoRelNo,
            PoLineNo = x.PoLineNo
        };
    }
}
