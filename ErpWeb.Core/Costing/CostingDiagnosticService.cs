using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Costing;

public sealed class CostingDiagnosticService : ICostingDiagnosticService
{
    private const int FindingLimit = 200;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;

    public CostingDiagnosticService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
    }

    public async Task<CostingHealthPage> SearchAsync(
        CostingHealthQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new CostingHealthQuery();
        if (!await _access.CanAsync(MenuCodes.InventoryCostingCenter, PermissionCodes.Access, cancellationToken))
            return new CostingHealthPage { Denied = true, Error = "Not authorized." };
        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
            return new CostingHealthPage { Error = "A trusted company and branch are required." };

        var showMoney = await _access.CanAsync(MenuCodes.InventoryCostingCenter, PermissionCodes.ViewCost, cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var company = scope.CompanyCode;
        var branch = scope.BranchCode;
        var findings = new List<CostingFinding>();
        var coverage = await DescribeEpochAsync(db, company, branch, findings, cancellationToken);
        var rebuild = coverage == CostingEpochCoverage.V2;

        await AddUnsealedPostingsAsync(db, company, branch, query, findings, cancellationToken);
        await AddFactsWithoutSealedPostingAsync(db, company, branch, query, findings, cancellationToken);
        await AddBrokenReversalsAsync(db, company, branch, query, findings, cancellationToken);
        await AddHistoryWithoutFactsAsync(db, company, branch, query, findings, cancellationToken);
        if (rebuild)
            await AddCostStateFindingsAsync(db, company, branch, query, findings, showMoney, cancellationToken);

        if (!string.IsNullOrWhiteSpace(query.FindingCode))
        {
            findings = findings
                .Where(x => string.Equals(x.Code, query.FindingCode.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return new CostingHealthPage
        {
            MonetaryValuesVisible = showMoney,
            EpochCoverage = coverage,
            Findings = findings.Take(FindingLimit).Select(x => showMoney ? x : HideMoney(x)).ToList()
        };
    }

    public async Task<CostingCogsProof> ProveInvoiceCogsAsync(
        string invoiceNo,
        CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.InventoryCostingCenter, PermissionCodes.Access, cancellationToken))
            return new CostingCogsProof { Denied = true, Error = "Not authorized." };
        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
            return new CostingCogsProof { Error = "A trusted company and branch are required." };
        if (string.IsNullOrWhiteSpace(invoiceNo))
            return new CostingCogsProof { Error = "Invoice number is required." };

        var showMoney = await _access.CanAsync(MenuCodes.InventoryCostingCenter, PermissionCodes.ViewCost, cancellationToken);
        var no = invoiceNo.Trim();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var exists = await db.SaInvoices.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.InvNo == no, cancellationToken);
        if (!exists)
            return new CostingCogsProof { Error = $"Invoice '{no}' was not found in the current branch.", InvoiceNo = no };

        var lines = await db.SaInvoiceDetails.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.InvNo == no
                && x.StockControl
                && x.StdQty > 0m)
            .OrderBy(x => x.Line)
            .Select(x => new { x.Line, x.ICode, x.LinkDo, x.DoNo, x.DoLine, x.StdQty })
            .ToListAsync(cancellationToken);

        var ownerNos = lines.Select(x => x.LinkDo ? x.DoNo : no)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var facts = ownerNos.Length == 0
            ? []
            : await db.StockValuationFacts.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.Direction == -1
                    && x.ValuationStatus == StockValuationStatuses.Valued
                    && ownerNos.Contains(x.SourceDocumentNo)
                    && x.StockPosting!.SealedAtUtc != null
                    && !db.StockValuationFacts.Any(r => r.ReversesValuationFactId == x.Id))
                .Select(x => new
                {
                    x.SourceDocumentType, x.SourceDocumentNo, x.SourceDocumentLine,
                    x.ItemCode, x.BaseQty, x.CostAmount
                })
                .ToListAsync(cancellationToken);

        var proof = new List<CostingCogsProofLine>();
        var findings = new List<CostingFinding>();
        foreach (var line in lines)
        {
            var ownerType = line.LinkDo ? "SA_DO" : "SA_INVOICE";
            var ownerNo = line.LinkDo ? line.DoNo?.Trim() ?? string.Empty : no;
            var ownerLine = line.LinkDo ? line.DoLine?.ToString() : line.Line.ToString();
            var matches = ownerLine is null
                ? []
                : facts.Where(x =>
                    string.Equals(x.SourceDocumentType, ownerType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.SourceDocumentNo, ownerNo, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.SourceDocumentLine, ownerLine, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.ItemCode, line.ICode, StringComparison.OrdinalIgnoreCase)).ToArray();
            var actualQty = matches.Sum(x => x.BaseQty);
            var cogs = matches.Sum(x => x.CostAmount);
            var proven = matches.Length > 0 && actualQty == line.StdQty;
            var lineage = proven ? "PROVEN" : "LEGACY_OR_AMBIGUOUS_COGS_LINEAGE";
            proof.Add(new CostingCogsProofLine(
                line.Line,
                line.ICode ?? string.Empty,
                ownerType,
                ownerNo,
                ownerLine,
                line.StdQty,
                actualQty,
                showMoney ? cogs : null,
                proven,
                lineage));
            if (!proven)
            {
                findings.Add(new CostingFinding(
                    CostingFindingCodes.SalesCogsLineageIncomplete,
                    CostingFindingSeverity.Critical,
                    true,
                    line.ICode,
                    null, null, null, null,
                    ownerType,
                    ownerNo,
                    null, null,
                    "Sales COGS ownership or quantity cannot be proven.",
                    $"Invoice {no} line {line.Line} expected base quantity {line.StdQty} and found {actualQty} on exact owner {ownerType} {ownerNo} line {ownerLine}.",
                    line.StdQty,
                    actualQty,
                    null,
                    showMoney ? cogs : null,
                    "Trace the delivery order or direct invoice stock issue. Do not treat an item-only match as proven COGS."));
            }
        }

        return new CostingCogsProof
        {
            MonetaryValuesVisible = showMoney,
            InvoiceNo = no,
            IsFullyProven = proof.All(x => x.QuantityProven),
            Lines = proof,
            Findings = findings
        };
    }

    private static async Task<string> DescribeEpochAsync(
        AppDbContext db,
        string company,
        string branch,
        List<CostingFinding> findings,
        CancellationToken cancellationToken)
    {
        var epochs = await db.StockLedgerEpochs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch)
            .Select(x => new { x.Id, x.Status })
            .ToListAsync(cancellationToken);
        var active = epochs.Where(x => string.Equals(x.Status, StockLedgerEpochStatuses.Active, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (active.Length == 0)
        {
            findings.Add(Coverage(
                CostingFindingSeverity.Critical,
                "No active V2 stock-ledger epoch.",
                "Cost totals cannot be treated as authoritative until an active ledger epoch exists."));
            return CostingEpochCoverage.NoActiveEpoch;
        }

        var activeIds = active.Select(x => x.Id).ToArray();
        var retiredIds = epochs
            .Where(x => string.Equals(x.Status, StockLedgerEpochStatuses.Retired, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id)
            .ToArray();
        var retiredFacts = retiredIds.Length > 0 && await db.StockValuationFacts.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && retiredIds.Contains(x.LedgerEpochId)
            && x.StockPosting!.SealedAtUtc != null, cancellationToken);
        var activeFacts = await db.StockValuationFacts.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && activeIds.Contains(x.LedgerEpochId)
            && x.StockPosting!.SealedAtUtc != null, cancellationToken);
        if (retiredFacts && activeFacts)
        {
            findings.Add(Coverage(
                CostingFindingSeverity.Warning,
                "Retired and active epochs both contain sealed valuation facts.",
                "Cross-epoch totals and cost-state rebuild stay blocked until the epoch model is proven. Coverage is shown, not guessed."));
            return CostingEpochCoverage.UnresolvedCrossEpoch;
        }

        return CostingEpochCoverage.V2;
    }

    private static CostingFinding Coverage(CostingFindingSeverity severity, string summary, string explanation) =>
        new(CostingFindingCodes.EpochCoverage, severity, severity == CostingFindingSeverity.Critical,
            null, null, null, null, null, null, null, null, null,
            summary, explanation, null, null, null, null,
            "Resolve ledger-epoch coverage before trusting a branch total or running a repair.");

    private static async Task AddUnsealedPostingsAsync(
        AppDbContext db, string company, string branch, CostingHealthQuery query,
        List<CostingFinding> findings, CancellationToken cancellationToken)
    {
        var rows = await db.StockPostings.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.SealedAtUtc == null)
            .OrderBy(x => x.PostingSequence)
            .Take(50)
            .Select(x => new { x.Id, x.SourceDocumentType, x.SourceDocumentNo, x.EffectiveAt })
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            findings.Add(new CostingFinding(
                CostingFindingCodes.UnsealedPosting, CostingFindingSeverity.Critical, true,
                query.ItemCode, null, null, null, row.EffectiveAt,
                row.SourceDocumentType, row.SourceDocumentNo, row.Id, null,
                "A stock posting was started but not sealed.",
                "Costing may be incomplete. Do not manually change the stock rows; run posting reconciliation.",
                null, null, null, null,
                "Finish or reverse the unfinished posting through its module. Do not edit the ledger rows."));
        }
    }

    private static async Task AddFactsWithoutSealedPostingAsync(
        AppDbContext db, string company, string branch, CostingHealthQuery query,
        List<CostingFinding> findings, CancellationToken cancellationToken)
    {
        var rows = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && (query.ItemCode == null || x.ItemCode == query.ItemCode)
                && x.StockPosting!.SealedAtUtc == null)
            .OrderBy(x => x.Id)
            .Take(50)
            .Select(x => new { x.Id, x.ItemCode, x.SourceDocumentType, x.SourceDocumentNo, x.StockPostingId, x.EffectiveAt })
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            findings.Add(new CostingFinding(
                CostingFindingCodes.FactWithoutSealedPosting, CostingFindingSeverity.Critical, true,
                row.ItemCode, null, null, null, row.EffectiveAt,
                row.SourceDocumentType, row.SourceDocumentNo, row.StockPostingId, row.Id,
                "A valuation fact is not attached to a sealed posting.",
                "Monetary evidence is only trustworthy after the owning stock posting is sealed.",
                null, null, null, null,
                "Trace the posting and complete it through the module that started it."));
        }
    }

    private static async Task AddBrokenReversalsAsync(
        AppDbContext db, string company, string branch, CostingHealthQuery query,
        List<CostingFinding> findings, CancellationToken cancellationToken)
    {
        var reversals = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.ReversesValuationFactId != null
                && (query.ItemCode == null || x.ItemCode == query.ItemCode))
            .Select(x => new { x.Id, x.ItemCode, x.ReversesValuationFactId, x.SourceDocumentNo, x.StockPostingId })
            .Take(100)
            .ToListAsync(cancellationToken);
        if (reversals.Count == 0)
            return;
        var originalIds = reversals.Select(x => x.ReversesValuationFactId!.Value).Distinct().ToArray();
        var originals = await db.StockValuationFacts.AsNoTracking()
            .Where(x => originalIds.Contains(x.Id))
            .Select(x => new { x.Id, x.CompanyCode, x.BranchCode, Sealed = x.StockPosting!.SealedAtUtc != null })
            .ToListAsync(cancellationToken);
        foreach (var reversal in reversals)
        {
            var original = originals.FirstOrDefault(x => x.Id == reversal.ReversesValuationFactId);
            if (original is not null
                && original.CompanyCode == company
                && original.BranchCode == branch
                && original.Sealed)
                continue;
            findings.Add(new CostingFinding(
                CostingFindingCodes.ReversalLineageBroken, CostingFindingSeverity.Critical, true,
                reversal.ItemCode, null, null, null, null, null, reversal.SourceDocumentNo,
                reversal.StockPostingId, reversal.Id,
                "A valuation reversal does not point at a sealed original fact in this branch.",
                "Append-only reversal lineage is broken, so the cost state cannot be trusted.",
                null, null, null, null,
                "Do not edit the facts. Trace the original posting and repair it through a registered adapter."));
        }
    }

    private static async Task AddHistoryWithoutFactsAsync(
        AppDbContext db, string company, string branch, CostingHealthQuery query,
        List<CostingFinding> findings, CancellationToken cancellationToken)
    {
        var financial = new[]
        {
            IvTrxTypes.MiscellaneousReceipt, IvTrxTypes.CustomerReturn, IvTrxTypes.GoodsReceive,
            IvTrxTypes.VendorReturn, IvTrxTypes.StockTransfer, IvTrxTypes.MiscellaneousIssue,
            IvTrxTypes.Scrap, IvTrxTypes.StockAdjustment, IvTrxTypes.SalesOut, IvTrxTypes.FinishedGoods,
            IvTrxTypes.IssueToProduction
        };
        var rows = await db.StockPostings.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.SealedAtUtc != null
                && financial.Contains(x.SourceDocumentType)
                && !db.StockValuationFacts.Any(f => f.StockPostingId == x.Id))
            .OrderBy(x => x.PostingSequence)
            .Take(50)
            .Select(x => new { x.Id, x.SourceDocumentType, x.SourceDocumentNo, x.EffectiveAt })
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            findings.Add(new CostingFinding(
                CostingFindingCodes.HistoryMissingValuation, CostingFindingSeverity.Critical, true,
                query.ItemCode, null, null, null, row.EffectiveAt,
                row.SourceDocumentType, row.SourceDocumentNo, row.Id, null,
                "A sealed financial posting has no valuation fact.",
                "Quantity may have moved without an authoritative cost. Do not invent a cost in the inquiry.",
                null, null, null, null,
                "Open the source document and repair it through its module once quantity lineage is proven."));
        }
    }

    private static async Task AddCostStateFindingsAsync(
        AppDbContext db, string company, string branch, CostingHealthQuery query,
        List<CostingFinding> findings, bool showMoney, CancellationToken cancellationToken)
    {
        var states = await db.StockCostStates.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && (query.ItemCode == null || x.ItemCode == query.ItemCode))
            .ToListAsync(cancellationToken);
        var facts = await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.StockPosting!.SealedAtUtc != null
                && x.ValuationStatus == StockValuationStatuses.Valued
                && (query.ItemCode == null || x.ItemCode == query.ItemCode)
                && !db.StockValuationFacts.Any(r => r.ReversesValuationFactId == x.Id)
                && x.ReversesValuationFactId == null)
            .Select(x => new { x.ItemCode, x.CostMethod, x.BaseUom, x.Direction, x.BaseQty, x.CostAmount })
            .ToListAsync(cancellationToken);
        var grouped = facts.GroupBy(x => (Item: x.ItemCode, Method: x.CostMethod));
        foreach (var state in states)
        {
            if (state.OnHandBaseQty == 0m && state.InventoryValue != 0m)
            {
                findings.Add(StateFinding(CostingFindingCodes.ZeroQuantityResidue, state.ItemCode,
                    "Zero quantity still carries inventory value.",
                    "A cost pool with no quantity must not keep a residual value.",
                    0m, state.OnHandBaseQty, 0m, showMoney ? state.InventoryValue : null));
            }

            var rows = grouped.FirstOrDefault(x =>
                string.Equals(x.Key.Item, state.ItemCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Key.Method, state.CostMethod, StringComparison.OrdinalIgnoreCase));
            var qty = StockLedgerPrecision.Quantity(rows?.Sum(x => x.BaseQty * x.Direction) ?? 0m);
            var value = StockLedgerPrecision.Money(rows?.Sum(x => x.CostAmount * x.Direction) ?? 0m);
            if (qty != state.OnHandBaseQty)
            {
                findings.Add(StateFinding(CostingFindingCodes.CostStateQuantityMismatch, state.ItemCode,
                    "Cost-state quantity does not match sealed valuation facts.",
                    "The branch and item pool was rebuilt from sealed, non-reversed facts.",
                    qty, state.OnHandBaseQty, null, null));
            }
            if (value != state.InventoryValue)
            {
                findings.Add(StateFinding(CostingFindingCodes.CostStateValueMismatch, state.ItemCode,
                    "Cost-state value does not match sealed valuation facts.",
                    "The comparison uses the same 6-decimal money rounding as the valuation engine.",
                    null, null, showMoney ? value : null, showMoney ? state.InventoryValue : null));
            }

            var expectedAverage = qty == 0m ? 0m : StockLedgerPrecision.Money(value / qty);
            var actualAverage = qty == 0m ? state.InventoryValue == 0m ? 0m : state.AverageUnitCost : state.AverageUnitCost;
            if (expectedAverage != StockLedgerPrecision.Money(actualAverage))
            {
                findings.Add(StateFinding(CostingFindingCodes.CostStateAverageMismatch, state.ItemCode,
                    "Cost-state average does not match value divided by quantity.",
                    qty == 0m
                        ? "A zero-quantity pool must reconcile average and value to zero."
                        : "Expected average is inventory value divided by on-hand base quantity.",
                    null, null, showMoney ? expectedAverage : null, showMoney ? actualAverage : null));
            }
        }
    }

    private static CostingFinding StateFinding(
        string code, string item, string summary, string explanation,
        decimal? expectedQty, decimal? actualQty, decimal? expectedValue, decimal? actualValue) =>
        new(code, CostingFindingSeverity.Critical, true, item, null, null, null, null,
            null, null, null, null, summary, explanation, expectedQty, actualQty, expectedValue, actualValue,
            "Do not edit the cost state from this screen. Preview a repair only after quantity lineage is clean.");

    private static CostingFinding HideMoney(CostingFinding finding) => finding with
    {
        ExpectedValue = null,
        ActualValue = null
    };
}
