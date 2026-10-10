using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Sales Inquiry workbench extensions — Invoice Inquiry, SO Transactions, Price History.
/// </summary>
public sealed partial class SaSalesInquiryService
{
    // ============================ Sales Invoice Inquiry ============================

    public async Task<IvMasterOperationResult<SaInquiryPage<SaInvoiceInquiryRow>>> GetInvoiceInquiryAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<SaInquiryPage<SaInvoiceInquiryRow>>(gate);
        }

        var prepared = query ?? new SaInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var invs = db.SaInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch);

        invs = ApplyDate(invs, x => x.InvDate, prepared);
        invs = ApplyText(invs, x => x.CustCode, prepared.CustCode);
        invs = ApplyText(invs, x => x.SalesmanCode, prepared.SalesmanCode);
        invs = ApplyText(invs, x => x.Status, prepared.Status);

        var search = Normalize(prepared.SearchText);
        if (search is not null)
        {
            invs = invs.Where(x =>
                x.InvNo.Contains(search)
                || (x.PoNo != null && x.PoNo.Contains(search))
                || (x.CustName != null && x.CustName.Contains(search)));
        }

        var total = await invs.CountAsync(cancellationToken);
        var rows = await invs
            .OrderByDescending(x => x.InvDate)
            .ThenByDescending(x => x.InvNo)
            .Skip(skip)
            .Take(take)
            .Select(x => new SaInvoiceInquiryRow
            {
                InvNo = x.InvNo,
                InvDate = x.InvDate,
                CustCode = x.CustCode,
                CustName = x.CustName,
                SalesmanCode = x.SalesmanCode,
                LocationCode = x.LocationCode,
                PoNo = x.PoNo,
                Currency = x.Currency,
                GrossAmnt = x.GrossAmnt,
                Taxes = x.Taxes,
                TotAmnt = x.TotAmnt,
                Status = x.Status,
                DueDate = x.DueDate,
                IrbmStatus = x.IrbmStatus
            })
            .ToListAsync(cancellationToken);

        await AttachInvoiceRelatedDocsAsync(db, company, branch, rows, cancellationToken);
        await DecorateInvoiceEInvoiceAsync(db, company, rows, cancellationToken);

        return IvMasterOperationResult<SaInquiryPage<SaInvoiceInquiryRow>>.Ok(
            new SaInquiryPage<SaInvoiceInquiryRow> { Rows = rows, TotalCount = total });
    }

    private static async Task AttachInvoiceRelatedDocsAsync(
        AppDbContext db,
        string company,
        string branch,
        List<SaInvoiceInquiryRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var invNos = rows.Select(x => x.InvNo).Distinct().ToList();
        var links = await db.SaInvoiceDetails.AsNoTracking()
            .Where(x => x.CompanyCode == company
                && x.BranchCode == branch
                && invNos.Contains(x.InvNo))
            .OrderBy(x => x.Line)
            .Select(x => new { x.InvNo, x.SoNo, x.CustRel, x.DoNo, x.Line })
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            var firstSo = links.FirstOrDefault(x =>
                string.Equals(x.InvNo, row.InvNo, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(x.SoNo));
            if (firstSo is not null)
            {
                row.RelatedSoNo = firstSo.SoNo;
                row.RelatedSoCustRel = firstSo.CustRel;
            }

            var firstDo = links.FirstOrDefault(x =>
                string.Equals(x.InvNo, row.InvNo, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(x.DoNo));
            if (firstDo is not null)
            {
                row.RelatedDoNo = firstDo.DoNo;
            }
        }
    }

    private async Task DecorateInvoiceEInvoiceAsync(
        AppDbContext db,
        string company,
        List<SaInvoiceInquiryRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var statusRows = rows.Select(x => new SaEInvoiceStatusRow
        {
            DocType = EInvoiceDocumentTypes.Invoice,
            DocNo = x.InvNo,
            DocDate = x.InvDate,
            CustCode = x.CustCode,
            CustName = x.CustName,
            IrbmStatus = x.IrbmStatus,
            TotAmnt = x.TotAmnt
        }).ToList();

        await DecorateEInvoiceRowsAsync(db, company, statusRows, cancellationToken);

        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].EInvoiceStatusLabel = statusRows[i].StatusLabel;
        }
    }

    // ============================ Sales Order Transactions ============================

    public async Task<IvMasterOperationResult<SaInquiryPage<SaSoTransactionRow>>> GetSoTransactionsAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<SaInquiryPage<SaSoTransactionRow>>(gate);
        }

        var prepared = query ?? new SaInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var slice =
            from h in db.SaSos.AsNoTracking()
            from l in h.Details
            where h.CompanyCode == company && h.BranchCode == branch
            select new { h, l };

        if (prepared.CurrentOnly)
        {
            slice = slice.Where(x => x.h.IsCurrent);
        }

        slice = ApplyDate(slice, x => x.h.SoDate, prepared);
        slice = ApplyText(slice, x => x.h.CustCode, prepared.CustCode);
        slice = ApplyText(slice, x => x.h.SalesRep, prepared.SalesmanCode);
        slice = ApplyText(slice, x => x.h.Status, prepared.Status);

        var search = Normalize(prepared.SearchText);
        if (search is not null)
        {
            slice = slice.Where(x =>
                x.h.SoNo.Contains(search)
                || (x.l.ICode != null && x.l.ICode.Contains(search))
                || (x.l.IDesc != null && x.l.IDesc.Contains(search)));
        }

        var total = await slice.CountAsync(cancellationToken);
        var rows = await slice
            .OrderBy(x => x.h.SoNo)
            .ThenByDescending(x => x.h.CustRel)
            .ThenBy(x => x.l.Line)
            .Skip(skip)
            .Take(take)
            .Select(x => new SaSoTransactionRow
            {
                SoNo = x.h.SoNo,
                Rev = x.h.CustRel,
                IsCurrent = x.h.IsCurrent,
                SoDate = x.h.SoDate,
                Status = x.h.Status,
                FulfillmentStatus = x.h.FulfillmentStatus,
                BillingStatus = x.h.BillingStatus,
                CustCode = x.h.CustCode,
                CustName = x.h.CustName,
                SalesRep = x.h.SalesRep,
                TotAmnt = x.h.TotAmnt,
                QtNo = x.h.QtNo,
                QtCustRel = x.h.QtCustRel,
                Line = x.l.Line,
                ICode = x.l.ICode,
                IDesc = x.l.IDesc,
                OrderQty = x.l.OrderQty,
                ShippedQty = x.l.ShippedQty,
                DeliveredQty = x.l.DeliveredQty,
                InvoicedQty = x.l.InvoicedQty,
                BalanceQty = x.l.BalanceQty,
                WrittenOffQty = x.l.WrittenOffQty,
                DeliveryDate = x.l.DeliveryDate
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<SaInquiryPage<SaSoTransactionRow>>.Ok(
            new SaInquiryPage<SaSoTransactionRow> { Rows = rows, TotalCount = total });
    }

    // ============================ Sales Price History ============================

    public async Task<IvMasterOperationResult<SaInquiryPage<SaSalesPriceHistoryRow>>> GetSalesPriceHistoryAsync(
        string menuCode,
        SaInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        var gate = await GateAsync(menuCode, cancellationToken);
        if (!gate.Succeeded)
        {
            return Fail<SaInquiryPage<SaSalesPriceHistoryRow>>(gate);
        }

        var prepared = query ?? new SaInquiryQuery();
        var (skip, take) = Page(prepared);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = gate.CompanyCode!;
        var branch = gate.BranchCode!;

        var slice =
            from h in db.SaInvoices.AsNoTracking()
            from l in h.Details
            where h.CompanyCode == company
                  && h.BranchCode == branch
                  && h.Status == SaInvoiceStatuses.Posted
            select new { h, l };

        slice = ApplyDate(slice, x => x.h.InvDate, prepared);
        slice = ApplyText(slice, x => x.h.CustCode, prepared.CustCode);
        slice = ApplyText(slice, x => x.h.SalesmanCode, prepared.SalesmanCode);
        slice = ApplyText(slice, x => x.l.ICode, prepared.ItemCode);

        var total = await slice.CountAsync(cancellationToken);
        var materialised = await slice
            .OrderByDescending(x => x.h.InvDate)
            .ThenBy(x => x.h.CustCode)
            .ThenBy(x => x.l.ICode)
            .ThenBy(x => x.l.Line)
            .Skip(skip)
            .Take(take)
            .Select(x => new
            {
                x.h.InvDate,
                x.h.InvNo,
                x.h.CustCode,
                x.h.CustName,
                x.l.ICode,
                x.l.IDesc,
                x.l.Qty,
                x.l.UnitPrice,
                x.l.ItemDiscAmount,
                x.l.NetAmount,
                x.h.Currency,
                x.l.StdUom,
                x.h.SalesmanCode,
                x.l.FrWarehouse,
                x.l.PricingSource,
                x.l.PricingRef,
                x.l.OriginalUnitPrice,
                x.l.OverrideReason
            })
            .ToListAsync(cancellationToken);

        var rows = materialised.Select(x => new SaSalesPriceHistoryRow
        {
            InvDate = x.InvDate,
            InvNo = x.InvNo,
            CustCode = x.CustCode,
            CustName = x.CustName,
            ICode = x.ICode,
            IDesc = x.IDesc,
            Qty = x.Qty,
            UnitPrice = x.UnitPrice,
            ItemDiscAmount = x.ItemDiscAmount,
            NetAmount = x.NetAmount,
            NetUnitPrice = x.Qty != 0m ? x.NetAmount / x.Qty : null,
            Currency = x.Currency,
            Uom = x.StdUom,
            SalesmanCode = x.SalesmanCode,
            FrWarehouse = x.FrWarehouse,
            PricingSource = x.PricingSource,
            PricingRef = x.PricingRef,
            OriginalUnitPrice = x.OriginalUnitPrice,
            OverrideReason = x.OverrideReason
        }).ToList();

        return IvMasterOperationResult<SaInquiryPage<SaSalesPriceHistoryRow>>.Ok(
            new SaInquiryPage<SaSalesPriceHistoryRow> { Rows = rows, TotalCount = total });
    }
}
