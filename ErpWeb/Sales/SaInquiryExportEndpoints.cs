using System.Globalization;
using System.Text;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Inventory;
using Microsoft.AspNetCore.Mvc;

namespace ErpWeb.Sales;

/// <summary>
/// CSV export for the Sales Inquiry screens (plan-salesReportsAndInquiries Phase 1).
///
/// Each handler calls the SAME <see cref="ISaSalesInquiryService"/> method the grid calls, with the
/// same applied filter (never the grid's current page) and a zero-based <c>Skip</c> + the row-cap
/// <c>Take</c>. The ACCESS check lives in the service, so an export can never widen access — exactly
/// the analysis-screen pattern. A result set larger than the cap is refused, never truncated.
/// </summary>
public static class SaInquiryExportEndpoints
{
    private const int MaxExportRows = IvInquiryExportWorkbook.MaxExportRows;

    public static IEndpointRouteBuilder MapSaInquiryExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/sales/inquiry/customer/export", ExportCustomerTransactionsAsync).RequireAuthorization();
        endpoints.MapGet("/sales/inquiry/customer-history/export", ExportCustomerHistoryAsync).RequireAuthorization();
        endpoints.MapGet("/sales/inquiry/qt-status/export", ExportQtStatusAsync).RequireAuthorization();
        endpoints.MapGet("/sales/inquiry/so-outstanding/export", ExportSoOutstandingAsync).RequireAuthorization();
        endpoints.MapGet("/sales/inquiry/do-status/export", ExportDoStatusAsync).RequireAuthorization();
        endpoints.MapGet("/sales/inquiry/document-relationship/export", ExportDocumentRelationshipAsync).RequireAuthorization();
        endpoints.MapGet("/sales/inquiry/cdn/export", ExportCdnAsync).RequireAuthorization();
        endpoints.MapGet("/sales/inquiry/einvoice/export", ExportEInvoiceAsync).RequireAuthorization();
        endpoints.MapGet("/sales/inquiry/einvoice-reconciliation/export", ExportEInvoiceReconciliationAsync).RequireAuthorization();

        // Sales Monitor (plan-salesDecisionSupport.prompt.md, Phase A). Same service method as the grid,
        // same applied filter, same ACCESS check inside the service.
        endpoints.MapGet("/sales/monitor/so-ageing/export", ExportSoAgeingAsync).RequireAuthorization();
        endpoints.MapGet("/sales/monitor/delivered-not-fully-invoiced/export", ExportNotFullyInvoicedAsync).RequireAuthorization();
        endpoints.MapGet("/sales/monitor/quotation-expiry/export", ExportQtExpiryAsync).RequireAuthorization();
        endpoints.MapGet("/sales/monitor/einvoice-action/export", ExportEInvoiceActionAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportCustomerTransactionsAsync(
        [AsParameters] SaInquiryQuery query,
        [FromServices] ISaSalesInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetCustomerTransactionsAsync(
            MenuCodes.SalesCustomerTransaction, ForExport(query), cancellationToken);
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
            new[] { "Doc type", "Doc no.", "Doc date", "Customer", "Customer name", "Sales rep", "Status", "Amount" }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.DocType,
            x.DocNo,
            Date(x.DocDate),
            x.CustCode ?? string.Empty,
            x.CustName ?? string.Empty,
            x.SalesRep ?? string.Empty,
            x.Status ?? string.Empty,
            Money(x.TotAmnt)
        }));

        return Csv(rows, "SaCustomerTransactions");
    }

    private static async Task<IResult> ExportCustomerHistoryAsync(
        [AsParameters] SaInquiryQuery query,
        [FromServices] ISaSalesInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetCustomerSalesHistoryAsync(
            MenuCodes.SalesCustomerTransaction, query, cancellationToken);
        if (!result.Succeeded)
        {
            return ToProblem(result.ErrorCode, result.Message);
        }

        var rows = new List<string[]>
        {
            new[] { "Period", "Invoices", "Invoice total", "Credit notes", "Credit total", "Debit notes", "Debit total", "Net sales" }
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
            Money(x.NetSales)
        }));

        return Csv(rows, "SaCustomerSalesHistory");
    }

    private static async Task<IResult> ExportQtStatusAsync(
        [AsParameters] SaInquiryQuery query,
        [FromServices] ISaSalesInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetQtStatusAsync(MenuCodes.SalesQtStatus, ForExport(query), cancellationToken);
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
            new[] { "QT no.", "Rev", "Date", "Valid until", "Status", "Conversion", "Customer", "Customer name", "Sales rep", "Amount", "Expired" }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.QtNo,
            x.Rev.ToString(CultureInfo.InvariantCulture),
            Date(x.QtDate),
            Date(x.ValidUntil),
            x.Status ?? string.Empty,
            x.ConversionStatus ?? string.Empty,
            x.CustCode ?? string.Empty,
            x.CustName ?? string.Empty,
            x.SalesRep ?? string.Empty,
            Money(x.TotAmnt),
            x.IsExpired ? "Yes" : "No"
        }));

        return Csv(rows, "SaQtStatus");
    }

    private static async Task<IResult> ExportSoOutstandingAsync(
        [AsParameters] SaInquiryQuery query,
        [FromServices] ISaSalesInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetSoOutstandingAsync(MenuCodes.SalesSoOutstanding, ForExport(query), cancellationToken);
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
            new[] { "SO no.", "Rev", "Date", "Status", "Fulfillment", "Billing", "Customer", "Customer name", "Sales rep", "Amount",
             "Line", "Item", "Description", "Ordered", "Shipped", "Delivered", "Invoiced", "Balance", "Written off", "Delivery date" }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.SoNo,
            x.Rev.ToString(CultureInfo.InvariantCulture),
            Date(x.SoDate),
            x.Status ?? string.Empty,
            x.FulfillmentStatus ?? string.Empty,
            x.BillingStatus ?? string.Empty,
            x.CustCode ?? string.Empty,
            x.CustName ?? string.Empty,
            x.SalesRep ?? string.Empty,
            Money(x.TotAmnt),
            x.Line.ToString(CultureInfo.InvariantCulture),
            x.ICode ?? string.Empty,
            x.IDesc ?? string.Empty,
            Qty(x.OrderQty),
            Qty(x.ShippedQty),
            Qty(x.DeliveredQty),
            Qty(x.InvoicedQty),
            Qty(x.BalanceQty),
            Qty(x.WrittenOffQty),
            x.DeliveryDate is DateTime dd ? Date(dd) : string.Empty
        }));

        return Csv(rows, "SaSoOutstanding");
    }

    private static async Task<IResult> ExportDoStatusAsync(
        [AsParameters] SaInquiryQuery query,
        [FromServices] ISaSalesInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetDoStatusAsync(MenuCodes.SalesDoStatus, ForExport(query), cancellationToken);
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
            new[] { "DO no.", "Date", "Status", "Billing", "Customer", "Customer name", "Sales rep", "Amount",
             "Line", "Item", "Description", "Qty", "SO no.", "Invoice no." }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.DoNo,
            Date(x.DoDate),
            x.Status ?? string.Empty,
            x.BillingStatus ?? string.Empty,
            x.CustCode ?? string.Empty,
            x.CustName ?? string.Empty,
            x.SalesRep ?? string.Empty,
            Money(x.TotAmnt),
            x.Line.ToString(CultureInfo.InvariantCulture),
            x.ICode ?? string.Empty,
            x.IDesc ?? string.Empty,
            Qty(x.Qty),
            x.SoNo ?? string.Empty,
            x.InvNo ?? string.Empty
        }));

        return Csv(rows, "SaDoStatus");
    }

    private static async Task<IResult> ExportDocumentRelationshipAsync(
        [AsParameters] SaInquiryQuery query,
        [FromServices] ISaSalesInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetDocumentRelationshipAsync(
            MenuCodes.SalesInvVsDoc, ForExport(query), cancellationToken);
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
            new[] { "Relationship", "Source doc", "Target doc", "Qty", "Related SO", "Doc date", "Customer", "Customer name" }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.Relation,
            x.SourceDocNo ?? string.Empty,
            x.TargetDocNo ?? string.Empty,
            Qty(x.Qty),
            x.RelatedSoNo ?? string.Empty,
            x.DocDate is DateTime dd ? Date(dd) : string.Empty,
            x.CustCode ?? string.Empty,
            x.CustName ?? string.Empty
        }));

        return Csv(rows, "SaDocumentRelationship");
    }

    private static async Task<IResult> ExportCdnAsync(
        [AsParameters] SaInquiryQuery query,
        [FromServices] ISaSalesInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetCdnInquiryAsync(MenuCodes.SalesCdnInquiry, ForExport(query), cancellationToken);
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
            new[] { "Doc no.", "Date", "Type", "Status", "Invoice no.", "Customer", "Customer name", "Sales rep", "Reference", "Remarks", "Amount" }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.DocNo,
            Date(x.DocDate),
            x.Type ?? string.Empty,
            x.Status ?? string.Empty,
            x.InvNo ?? string.Empty,
            x.CustCode ?? string.Empty,
            x.CustName ?? string.Empty,
            x.SalesRep ?? string.Empty,
            x.RefNo ?? string.Empty,
            x.Remarks ?? string.Empty,
            Money(x.TotAmnt)
        }));

        return Csv(rows, "SaCdnInquiry");
    }

    private static async Task<IResult> ExportEInvoiceAsync(
        [AsParameters] SaInquiryQuery query,
        [FromServices] ISaSalesInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetEInvoiceStatusAsync(MenuCodes.SalesEInvoiceInquiry, ForExport(query), cancellationToken);
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
            new[] { "Doc type", "Doc no.", "Date", "Customer", "Customer name", "Status", "Status label", "UUID", "Submission ID", "Latest submission" }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.DocType,
            x.DocNo,
            Date(x.DocDate),
            x.CustCode ?? string.Empty,
            x.CustName ?? string.Empty,
            x.IrbmStatus ?? string.Empty,
            x.StatusLabel,
            x.IrbmUuid ?? string.Empty,
            x.IrbmSubmitId ?? string.Empty,
            x.LatestSubmissionStatus ?? string.Empty
        }));

        return Csv(rows, "SaEInvoiceStatus");
    }

    private static async Task<IResult> ExportEInvoiceReconciliationAsync(
        [AsParameters] SaInquiryQuery query,
        [FromServices] ISaSalesInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetEInvoiceReconciliationAsync(
            MenuCodes.SalesEInvoiceInquiry, ForExport(query), cancellationToken);
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

        return Csv(rows, "SaEInvoiceReconciliation");
    }

    private static SaInquiryQuery ForExport(SaInquiryQuery query)
    {
        query.Skip = 0;
        query.Take = MaxExportRows;
        return query;
    }

    // ================== Sales Monitor — Phase A exports ==================

    private static async Task<IResult> ExportSoAgeingAsync(
        [AsParameters] SaInquiryQuery query,
        [FromServices] ISaSalesInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetSoAgeingAsync(MenuCodes.SalesSoAgeing, ForExport(query), cancellationToken);
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
                "SO no.", "Rev", "SO date", "SO Age (days)", "Age bucket", "Status", "Fulfillment", "Billing",
                "Customer", "Customer name", "Sales rep", "Amount", "Line", "Item", "Description",
                "Ordered", "Delivered", "Invoiced", "Balance", "Written off",
                "Delivery Due Date", "Overdue", "Overdue Days"
            }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.SoNo,
            x.Rev.ToString(CultureInfo.InvariantCulture),
            Date(x.SoDate),
            x.AgeDays.ToString(CultureInfo.InvariantCulture),
            x.AgeBucket,
            x.Status ?? string.Empty,
            x.FulfillmentStatus ?? string.Empty,
            x.BillingStatus ?? string.Empty,
            x.CustCode ?? string.Empty,
            x.CustName ?? string.Empty,
            x.SalesRep ?? string.Empty,
            Money(x.TotAmnt),
            x.Line.ToString(CultureInfo.InvariantCulture),
            x.ICode ?? string.Empty,
            x.IDesc ?? string.Empty,
            Qty(x.OrderQty),
            Qty(x.DeliveredQty),
            Qty(x.InvoicedQty),
            Qty(x.BalanceQty),
            Qty(x.WrittenOffQty),
            x.DeliveryDate is DateTime due ? Date(due) : string.Empty,
            x.IsOverdueDelivery ? "Yes" : "No",
            x.OverdueDays.ToString(CultureInfo.InvariantCulture)
        }));

        return Csv(rows, "SaSoAgeing");
    }

    private static async Task<IResult> ExportNotFullyInvoicedAsync(
        [AsParameters] SaInquiryQuery query,
        [FromServices] ISaSalesInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetDeliveredNotFullyInvoicedAsync(
            MenuCodes.SalesDoNotFullyInvoiced, ForExport(query), cancellationToken);
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
                "DO no.", "DO date", "Posted", "Days since delivered", "Status", "Billing", "Line state",
                "Customer", "Customer name", "Sales rep", "Amount", "Line", "Item", "Description",
                "Delivered qty", "SO no.", "Line invoice no.", "Pending"
            }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.DoNo,
            Date(x.DoDate),
            x.PostedDate is DateTime posted ? Date(posted) : string.Empty,
            x.DaysSinceDelivered.ToString(CultureInfo.InvariantCulture),
            x.Status ?? string.Empty,
            x.BillingStatus ?? string.Empty,
            x.InvoiceState,
            x.CustCode ?? string.Empty,
            x.CustName ?? string.Empty,
            x.SalesRep ?? string.Empty,
            Money(x.TotAmnt),
            x.Line.ToString(CultureInfo.InvariantCulture),
            x.ICode ?? string.Empty,
            x.IDesc ?? string.Empty,
            Qty(x.Qty),
            x.SoNo ?? string.Empty,
            x.LineInvNo ?? string.Empty,
            x.IsPendingInvoice ? "Yes" : "No"
        }));

        return Csv(rows, "SaDoNotFullyInvoiced");
    }

    private static async Task<IResult> ExportQtExpiryAsync(
        [AsParameters] SaInquiryQuery query,
        [FromServices] ISaSalesInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetQtExpiryAsync(MenuCodes.SalesQtExpiry, ForExport(query), cancellationToken);
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
                "QT no.", "Rev", "QT date", "Valid until", "Days to expiry", "Expiry bucket", "Expired",
                "Expiring soon", "Status", "Conversion", "Customer", "Customer name", "Sales rep", "Amount"
            }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.QtNo,
            x.Rev.ToString(CultureInfo.InvariantCulture),
            Date(x.QtDate),
            Date(x.ValidUntil),
            x.DaysToExpiry.ToString(CultureInfo.InvariantCulture),
            x.ExpiryBucket,
            x.IsExpired ? "Yes" : "No",
            x.IsExpiringSoon ? "Yes" : "No",
            x.Status ?? string.Empty,
            x.ConversionStatus ?? string.Empty,
            x.CustCode ?? string.Empty,
            x.CustName ?? string.Empty,
            x.SalesRep ?? string.Empty,
            Money(x.TotAmnt)
        }));

        return Csv(rows, "SaQtExpiry");
    }

    private static async Task<IResult> ExportEInvoiceActionAsync(
        [AsParameters] SaInquiryQuery query,
        [FromServices] ISaSalesInquiryService inquiry,
        CancellationToken cancellationToken)
    {
        var result = await inquiry.GetEInvoiceActionQueueAsync(
            MenuCodes.SalesEInvoiceAction, ForExport(query), cancellationToken);
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
                "Doc type", "Doc no.", "Date", "Customer", "Customer name", "Status", "IRBM status",
                "Sent on", "Days since submitted", "Action", "Amount"
            }
        };
        rows.AddRange(page.Rows.Select(x => new[]
        {
            x.DocType,
            x.DocNo,
            Date(x.DocDate),
            x.CustCode ?? string.Empty,
            x.CustName ?? string.Empty,
            x.StatusLabel,
            x.IrbmStatus ?? string.Empty,
            x.IrbmSentOn is DateTime sent ? Date(sent) : string.Empty,
            x.DaysSinceSubmitted?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            x.ActionReason ?? string.Empty,
            Money(x.TotAmnt)
        }));

        return Csv(rows, "SaEInvoiceAction");
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
