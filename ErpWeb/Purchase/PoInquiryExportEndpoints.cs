using System.Globalization;
using System.Text;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Inventory;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Purchase;

/// <summary>
/// CSV export for the Purchase Inquiry screens (Phase 1).
///
/// Each handler calls the SAME <see cref="IPoPurchaseInquiryService"/> method the grid calls, with the
/// same applied filter (never the grid's current page) and a zero-based <c>Skip</c> + the row-cap
/// <c>Take</c>. The ACCESS check lives in the service, so an export can never widen access — exactly
/// the Sales Inquiry pattern. A result set larger than the cap is refused, never truncated.
/// </summary>
public static class PoInquiryExportEndpoints
{
    private const int MaxExportRows = IvInquiryExportWorkbook.MaxExportRows;

    public static IEndpointRouteBuilder MapPoInquiryExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/purchase/inquiry/po-outstanding/export", ExportPoOutstandingAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/inquiry/pr-status/export", ExportPrStatusAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/inquiry/supplier/export", ExportSupplierTransactionsAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/inquiry/supplier-history/export", ExportSupplierHistoryAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/inquiry/invoices/export", ExportInvoicesAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/inquiry/cdn/export", ExportCdnAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/inquiry/document-relationship/export", ExportDocumentRelationshipAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/inquiry/einvoice/export", ExportEInvoiceAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/inquiry/einvoice-reconciliation/export", ExportEInvoiceReconciliationAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/inquiry/price-history/export", ExportPriceHistoryAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/inquiry/matching/export", ExportMatchingAsync).RequireAuthorization();
        endpoints.MapGet("/purchase/inquiry/delivery-performance/export", ExportDeliveryPerformanceAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportPoOutstandingAsync(
        [AsParameters] PoInquiryQuery query,
        [FromServices] IPoPurchaseInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetPoOutstandingAsync(
            MenuCodes.PurchaseOrderOutstanding, ForExport(query), cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var page = result.Data!;
        if (page.TotalCount > MaxExportRows)
        {
            return TooManyRows(page.TotalCount);
        }

        var rows = new List<string[]>
        {
            new[] { "PO no.", "Rev", "Date", "Status", "Supplier", "Supplier name", "Buyer",
             "Line", "Item", "Description", "Ordered", "Received", "Returned", "Invoiced", "Invoiceable",
             "Balance", "Outstanding value", "ETA", "Days overdue", "Warehouse" }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.PoNo,
            x.PoRelNo.ToString(CultureInfo.InvariantCulture),
            x.PoDate is DateTime pd ? Date(pd) : string.Empty,
            x.Status ?? string.Empty,
            x.VendCode ?? string.Empty,
            x.VendName ?? string.Empty,
            x.Buyer ?? string.Empty,
            x.Line.ToString(CultureInfo.InvariantCulture),
            x.ICode ?? string.Empty,
            x.IDesc ?? string.Empty,
            Qty(x.PoPurQty),
            Qty(x.RecvQty),
            Qty(x.ReturnQty),
            Qty(x.InvoicedQty),
            Qty(x.InvoiceableQty),
            Qty(x.BalanceQty),
            Money(x.OutstandingValue),
            x.EtaDate is DateTime eta ? Date(eta) : string.Empty,
            x.DaysOverdue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            x.ToWarehouse ?? string.Empty
        }));

        return Csv(rows, "PoOrderOutstanding");
    }

    private static async Task<IResult> ExportPrStatusAsync(
        [AsParameters] PoInquiryQuery query,
        [FromServices] IPoPurchaseInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetPrStatusAsync(MenuCodes.PurchasePrStatus, ForExport(query), cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var page = result.Data!;
        if (page.TotalCount > MaxExportRows)
        {
            return TooManyRows(page.TotalCount);
        }

        var rows = new List<string[]>
        {
            new[] { "PR no.", "Date", "Status", "Requester", "Dept", "Line", "Item", "Description",
             "Purchase qty", "Consumed", "Remaining", "Linked POs", "Vendor", "Warehouse", "ETA", "Line status" }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.PrNo,
            Date(x.CreateDt),
            x.HeaderStatus ?? string.Empty,
            x.Requester ?? string.Empty,
            x.DeptCode ?? string.Empty,
            x.Line.ToString(CultureInfo.InvariantCulture),
            x.ICode ?? string.Empty,
            x.IDesc ?? string.Empty,
            Qty(x.PurchaseQty),
            Qty(x.ConsumedQty),
            Qty(x.RemainingQty),
            x.LinkedPoNos ?? string.Empty,
            x.VendorCd ?? string.Empty,
            x.ToWarehouse ?? string.Empty,
            x.EtaDt is DateTime eta ? Date(eta) : string.Empty,
            x.LineStatus ?? string.Empty
        }));

        return Csv(rows, "PoPrStatus");
    }

    private static async Task<IResult> ExportSupplierTransactionsAsync(
        [AsParameters] PoInquiryQuery query,
        [FromServices] IPoPurchaseInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetSupplierTransactionsAsync(
            MenuCodes.PurchaseSupplierTransaction, ForExport(query), cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var page = result.Data!;
        if (page.TotalCount > MaxExportRows)
        {
            return TooManyRows(page.TotalCount);
        }

        var rows = new List<string[]>
        {
            new[] { "Doc type", "Doc no.", "Doc date", "Supplier", "Supplier name", "Buyer", "Status", "Amount", "PO rev", "Extra" }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.DocType,
            x.DocNo,
            Date(x.DocDate),
            x.SuppCode ?? string.Empty,
            x.SuppName ?? string.Empty,
            x.Buyer ?? string.Empty,
            x.Status ?? string.Empty,
            Money(x.TotAmnt),
            x.PoRelNo?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            x.Extra ?? string.Empty
        }));

        return Csv(rows, "PoSupplierTransactions");
    }

    private static async Task<IResult> ExportSupplierHistoryAsync(
        [AsParameters] PoInquiryQuery query,
        [FromServices] IPoPurchaseInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetSupplierPurchaseHistoryAsync(
            MenuCodes.PurchaseSupplierTransaction, query, cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var rows = new List<string[]>
        {
            new[] { "Period", "Invoices", "Invoice total", "Credit notes", "Credit total", "Debit notes", "Debit total", "Net purchase" }
        };
        rows.AddRange((result.Data ?? []).Select(x => new[]
        {
            x.Period,
            x.InvoiceCount.ToString(CultureInfo.InvariantCulture),
            Money(x.InvoiceTotal),
            x.CreditNoteCount.ToString(CultureInfo.InvariantCulture),
            Money(x.CreditNoteTotal),
            x.DebitNoteCount.ToString(CultureInfo.InvariantCulture),
            Money(x.DebitNoteTotal),
            Money(x.NetPurchase)
        }));

        return Csv(rows, "PoSupplierPurchaseHistory");
    }

    private static async Task<IResult> ExportInvoicesAsync(
        [AsParameters] PoInquiryQuery query,
        [FromServices] IPoPurchaseInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetInvoiceInquiryAsync(
            MenuCodes.PurchaseInvoiceInquiry, ForExport(query), cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var page = result.Data!;
        if (page.TotalCount > MaxExportRows)
        {
            return TooManyRows(page.TotalCount);
        }

        var rows = new List<string[]>
        {
            new[] { "Doc no.", "Date", "Type", "Status", "Supplier", "Supplier name", "Amount", "Tax", "Currency", "External doc", "Location", "Posted" }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.DocNo,
            Date(x.DocDate),
            x.Type,
            x.Status ?? string.Empty,
            x.VendorCode ?? string.Empty,
            x.VendorName ?? string.Empty,
            Money(x.TotAmnt),
            Money(x.Taxes),
            x.Currency ?? string.Empty,
            x.ExternalDocNo ?? string.Empty,
            x.LocationCode ?? string.Empty,
            x.PostedDate is DateTime pd ? Date(pd) : string.Empty
        }));

        return Csv(rows, "PoInvoiceInquiry");
    }

    private static async Task<IResult> ExportCdnAsync(
        [AsParameters] PoInquiryQuery query,
        [FromServices] IPoPurchaseInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetCdnInquiryAsync(MenuCodes.PurchaseCdnInquiry, ForExport(query), cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var page = result.Data!;
        if (page.TotalCount > MaxExportRows)
        {
            return TooManyRows(page.TotalCount);
        }

        var rows = new List<string[]>
        {
            new[] { "Doc no.", "Date", "Type", "Status", "Invoice no.", "Supplier", "Supplier name", "Amount", "Return stock", "VR batch", "Tax group" }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.DocNo,
            Date(x.DocDate),
            x.Type,
            x.Status ?? string.Empty,
            x.InvNo ?? string.Empty,
            x.VendorCode ?? string.Empty,
            x.VendorName ?? string.Empty,
            Money(x.TotAmnt),
            x.ReturnStock ? "Yes" : "No",
            x.VrBatchNo ?? string.Empty,
            x.TaxGrCode ?? string.Empty
        }));

        return Csv(rows, "PoCdnInquiry");
    }

    private static async Task<IResult> ExportDocumentRelationshipAsync(
        [AsParameters] PoInquiryQuery query,
        [FromServices] IPoPurchaseInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetDocumentRelationshipAsync(
            MenuCodes.PurchaseDocRelationship, ForExport(query), cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var page = result.Data!;
        if (page.TotalCount > MaxExportRows)
        {
            return TooManyRows(page.TotalCount);
        }

        var rows = new List<string[]>
        {
            new[] { "Relationship", "Source doc", "Target doc", "Qty", "Related PO", "Related PR", "Doc date", "Supplier", "Supplier name" }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.Relation,
            x.SourceDocNo ?? string.Empty,
            x.TargetDocNo ?? string.Empty,
            Qty(x.Qty),
            x.RelatedPoNo ?? string.Empty,
            x.RelatedPrNo ?? string.Empty,
            x.DocDate is DateTime dd ? Date(dd) : string.Empty,
            x.VendCode ?? string.Empty,
            x.VendName ?? string.Empty
        }));

        return Csv(rows, "PoDocumentRelationship");
    }

    private static async Task<IResult> ExportEInvoiceAsync(
        [AsParameters] PoInquiryQuery query,
        [FromServices] IPoPurchaseInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetSbEInvoiceStatusAsync(
            MenuCodes.PurchaseSbEInvoiceInquiry, ForExport(query), cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var page = result.Data!;
        if (page.TotalCount > MaxExportRows)
        {
            return TooManyRows(page.TotalCount);
        }

        var rows = new List<string[]>
        {
            new[] { "Doc type", "Doc no.", "Date", "Supplier", "Supplier name", "Status", "Status label", "UUID", "Submission ID", "Latest submission" }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.DocType,
            x.DocNo,
            Date(x.DocDate),
            x.VendorCode ?? string.Empty,
            x.VendorName ?? string.Empty,
            x.IrbmStatus ?? string.Empty,
            x.StatusLabel,
            x.IrbmUuid ?? string.Empty,
            x.IrbmSubmitId ?? string.Empty,
            x.LatestSubmissionStatus ?? string.Empty
        }));

        return Csv(rows, "PoSbEInvoiceStatus");
    }

    private static async Task<IResult> ExportEInvoiceReconciliationAsync(
        [AsParameters] PoInquiryQuery query,
        [FromServices] IPoPurchaseInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetSbEInvoiceReconciliationAsync(
            MenuCodes.PurchaseSbEInvoiceInquiry, ForExport(query), cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var page = result.Data!;
        if (page.TotalCount > MaxExportRows)
        {
            return TooManyRows(page.TotalCount);
        }

        var rows = new List<string[]>
        {
            new[] { "Doc type", "Doc no.", "ERP status", "ERP UUID", "Registry status", "Finding" }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.DocType,
            x.DocNo,
            x.ErpStatus ?? string.Empty,
            x.ErpUuid ?? string.Empty,
            x.RegistryStatus ?? string.Empty,
            x.Finding
        }));

        return Csv(rows, "PoSbEInvoiceReconciliation");
    }

    private static async Task<IResult> ExportPriceHistoryAsync(
        [AsParameters] PoInquiryQuery query,
        [FromServices] IPoPurchaseInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetPurchasePriceHistoryAsync(
            MenuCodes.PurchasePriceHistory, ForExport(query), cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var page = result.Data!;
        if (page.TotalCount > MaxExportRows)
        {
            return TooManyRows(page.TotalCount);
        }

        var rows = new List<string[]>
        {
            new[]
            {
                "Date", "Invoice", "Supplier", "Supplier name", "Item", "Description", "PO no.",
                "Qty", "UOM", "Unit price", "Discount", "Net unit", "Currency", "Rate", "Local amount"
            }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            Date(x.DocDate),
            x.DocNo,
            x.VendorCode ?? string.Empty,
            x.VendorName ?? string.Empty,
            x.ICode ?? string.Empty,
            x.IDesc ?? string.Empty,
            x.PoNo ?? string.Empty,
            Qty(x.Qty),
            x.Uom ?? string.Empty,
            Qty(x.UnitPrice),
            Money(x.ItemDiscAmount),
            x.NetUnitPrice is decimal nup ? Qty(nup) : string.Empty,
            x.Currency ?? string.Empty,
            Qty(x.CurrRate),
            x.LocalAmount is decimal la ? Money(la) : string.Empty
        }));

        return Csv(rows, "PoPurchasePriceHistory");
    }

    private static async Task<IResult> ExportMatchingAsync(
        [AsParameters] PoInquiryQuery query,
        [FromServices] IPoPurchaseInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetPoMatchingAsync(
            MenuCodes.PurchaseMatching, ForExport(query), cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var page = result.Data!;
        if (page.TotalCount > MaxExportRows)
        {
            return TooManyRows(page.TotalCount);
        }

        var rows = new List<string[]>
        {
            new[]
            {
                "PO no.", "Rev", "Date", "Supplier", "Item", "Ordered", "Received", "Invoiced", "Balance",
                "PO amount", "Received amount", "Invoice amount", "Qty variance", "Status",
                "Price mismatch", "Multi INV", "Multi GR", "Warehouse"
            }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.PoNo,
            x.PoRelNo.ToString(CultureInfo.InvariantCulture),
            x.PoDate is DateTime pd ? Date(pd) : string.Empty,
            x.VendCode ?? string.Empty,
            x.ICode ?? string.Empty,
            Qty(x.PoPurQty),
            Qty(x.NetReceivedQty),
            Qty(x.InvoicedQty),
            Qty(x.BalanceQty),
            Money(x.PoAmount),
            Money(x.ReceivedAmount),
            Money(x.InvoiceAmount),
            Qty(x.QtyVariance),
            x.MatchingStatus,
            x.PriceMismatch ? "Y" : "N",
            x.HasMultipleInvoices ? "Y" : "N",
            x.HasMultipleGRs ? "Y" : "N",
            x.ToWarehouse ?? string.Empty
        }));

        return Csv(rows, "PoMatching");
    }

    private static async Task<IResult> ExportDeliveryPerformanceAsync(
        [AsParameters] PoInquiryQuery query,
        [FromServices] IPoPurchaseInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetSupplierDeliveryPerformanceAsync(
            MenuCodes.PurchaseDeliveryPerformance, ForExport(query), cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var page = result.Data!;
        if (page.TotalCount > MaxExportRows)
        {
            return TooManyRows(page.TotalCount);
        }

        var rows = new List<string[]>
        {
            new[]
            {
                "Supplier", "Name", "Purchase orders", "PO lines evaluated", "Ordered qty", "Received qty",
                "On-time lines", "Late lines", "On-time %", "Avg days late", "Max days late"
            }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.SuppCode ?? string.Empty,
            x.SuppName ?? string.Empty,
            x.PurchaseOrderCount.ToString(CultureInfo.InvariantCulture),
            x.PoLinesEvaluated.ToString(CultureInfo.InvariantCulture),
            Qty(x.OrderedQty),
            Qty(x.ReceivedQty),
            x.OnTimeLines.ToString(CultureInfo.InvariantCulture),
            x.LateLines.ToString(CultureInfo.InvariantCulture),
            x.OnTimePct is decimal pct ? Money(pct) : string.Empty,
            x.AvgDaysLate is decimal avg ? Money(avg) : string.Empty,
            x.MaxDaysLate?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
        }));

        return Csv(rows, "PoDeliveryPerformance");
    }

    private static PoInquiryQuery ForExport(PoInquiryQuery query)
    {
        query.Skip = 0;
        query.Take = MaxExportRows;
        return query;
    }

    private static IResult TooManyRows(int total) =>
        Results.BadRequest(
            $"Export is limited to {MaxExportRows:N0} rows. Refine the filters and try again. Matched: {total:N0}.");

    private static IResult ToProblem(IvMasterErrorCode code, string? message) =>
        code switch
        {
            IvMasterErrorCode.AccessDenied => Results.Forbid(),
            IvMasterErrorCode.InvalidScope => Results.BadRequest(message ?? "Invalid company context."),
            _ => Results.BadRequest(message ?? "Export failed.")
        };

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Qty(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Date(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static IResult Csv(List<string[]> rows, string filePrefix)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', row.Select(Escape)));
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
        return Results.File(bytes, "text/csv", $"{filePrefix}_{DateTime.Now:yyMMddHHmmss}.csv");
    }

    private static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        if (text.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return text;
        }

        return $"\"{text.Replace("\"", "\"\"")}\"";
    }
}
