using ErpWeb.Core.Inventory;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Sales;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Costing;

public sealed class InventoryCostingRepairAdapter : ICostingRepairAdapter
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IIvInventoryPostingService _posting;

    public InventoryCostingRepairAdapter(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IIvInventoryPostingService posting)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _posting = posting;
    }

    public IReadOnlySet<string> OwnerTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        CostingRepairOwnerTypes.MiscReceipt,
        CostingRepairOwnerTypes.CustomerReturn,
        CostingRepairOwnerTypes.MiscIssue,
        CostingRepairOwnerTypes.Scrap,
        CostingRepairOwnerTypes.VendorReturn,
        CostingRepairOwnerTypes.Transfer,
        CostingRepairOwnerTypes.Adjustment
    };

    public Task<CostingRepairCapability> CanHandleAsync(
        CostingRepairNode node, CostingRepairOwner owner, CancellationToken cancellationToken) =>
        Task.FromResult(new CostingRepairCapability(OwnerTypes.Contains(owner.OwnerType), null));

    public Task<CostingRepairStepResult> ReverseAsync(
        CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken) =>
        RollbackBatchAsync(owner, cancellationToken);

    public Task<CostingRepairStepResult> RepostAsync(
        CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken) =>
        PostBatchAsync(owner, cancellationToken);

    private async Task<CostingRepairStepResult> RollbackBatchAsync(CostingRepairOwner owner, CancellationToken cancellationToken)
    {
        var gate = await RequirePostedBatchAsync(owner, cancellationToken);
        if (gate is not null)
            return gate;
        var batchNo = int.Parse(owner.PhysicalSourceDocumentId, System.Globalization.CultureInfo.InvariantCulture);
        var result = await _posting.RollbackAsync(owner.PhysicalSourceDocumentType, [batchNo], cancellationToken);
        return new CostingRepairStepResult(result.Succeeded, false, result.ErrorMessage, null);
    }

    private async Task<CostingRepairStepResult> PostBatchAsync(CostingRepairOwner owner, CancellationToken cancellationToken)
    {
        if (!int.TryParse(owner.PhysicalSourceDocumentId, out var batchNo))
            return new CostingRepairStepResult(false, false, "The inventory batch number is not numeric.", null);
        var result = await _posting.PostAsync(owner.PhysicalSourceDocumentType, [batchNo], cancellationToken);
        return new CostingRepairStepResult(result.Succeeded, false, result.ErrorMessage, null);
    }

    private async Task<CostingRepairStepResult?> RequirePostedBatchAsync(CostingRepairOwner owner, CancellationToken cancellationToken)
    {
        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null || !int.TryParse(owner.PhysicalSourceDocumentId, out var batchNo))
            return new CostingRepairStepResult(false, true, "The inventory batch could not be reloaded.", null);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var status = await db.IvTrxBatches.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                && x.BatchNo == batchNo && x.TrxType == owner.PhysicalSourceDocumentType)
            .Select(x => x.BatchStatus)
            .FirstOrDefaultAsync(cancellationToken);
        if (!string.Equals(status, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            return new CostingRepairStepResult(false, true, "STALE_PREVIEW", null);
        return null;
    }
}

public sealed class SalesInvoiceCostingRepairAdapter : ICostingRepairAdapter
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly ISaInvoiceService _invoices;

    public SalesInvoiceCostingRepairAdapter(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        ISaInvoiceService invoices)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _invoices = invoices;
    }

    public IReadOnlySet<string> OwnerTypes { get; } = new HashSet<string>(StringComparer.Ordinal) { CostingRepairOwnerTypes.SalesInvoice };

    public Task<CostingRepairCapability> CanHandleAsync(
        CostingRepairNode node, CostingRepairOwner owner, CancellationToken cancellationToken) =>
        Task.FromResult(new CostingRepairCapability(true, null));

    public async Task<CostingRepairStepResult> ReverseAsync(
        CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken)
    {
        var stale = await RequireStatusAsync(owner.OwnerDocumentNo, "POSTED", cancellationToken);
        if (stale)
            return new CostingRepairStepResult(false, true, "STALE_PREVIEW", null);
        var result = await _invoices.RollbackAsync([owner.OwnerDocumentNo], cancellationToken);
        return new CostingRepairStepResult(result.Succeeded, false, result.ErrorMessage, null);
    }

    public async Task<CostingRepairStepResult> RepostAsync(
        CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken)
    {
        var result = await _invoices.PostAsync([owner.OwnerDocumentNo], cancellationToken);
        return new CostingRepairStepResult(result.Succeeded, false, result.ErrorMessage, null);
    }

    private async Task<bool> RequireStatusAsync(string invNo, string expected, CancellationToken cancellationToken)
    {
        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
            return true;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var status = await db.SaInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.InvNo == invNo)
            .Select(x => x.Status)
            .FirstOrDefaultAsync(cancellationToken);
        return !string.Equals(status, expected, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SalesDeliveryOrderCostingRepairAdapter : ICostingRepairAdapter
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly ISaDoService _deliveryOrders;

    public SalesDeliveryOrderCostingRepairAdapter(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        ISaDoService deliveryOrders)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _deliveryOrders = deliveryOrders;
    }

    public IReadOnlySet<string> OwnerTypes { get; } = new HashSet<string>(StringComparer.Ordinal) { CostingRepairOwnerTypes.SalesDeliveryOrder };

    public Task<CostingRepairCapability> CanHandleAsync(
        CostingRepairNode node, CostingRepairOwner owner, CancellationToken cancellationToken) =>
        Task.FromResult(new CostingRepairCapability(true, null));

    public async Task<CostingRepairStepResult> ReverseAsync(
        CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(owner.OwnerDocumentNo, cancellationToken);
        if (current is null || !string.Equals(current.Value.Status, "POSTED", StringComparison.OrdinalIgnoreCase))
            return new CostingRepairStepResult(false, true, "STALE_PREVIEW", null);
        var result = await _deliveryOrders.RollbackAsync(
            [new SaDoKeyedRequest { DoNo = owner.OwnerDocumentNo, RowVersion = current.Value.RowVersion }],
            cancellationToken);
        return new CostingRepairStepResult(result.Succeeded, false, result.ErrorMessage, null);
    }

    public async Task<CostingRepairStepResult> RepostAsync(
        CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(owner.OwnerDocumentNo, cancellationToken);
        if (current is null)
            return new CostingRepairStepResult(false, true, "STALE_PREVIEW", null);
        var result = await _deliveryOrders.PostAsync(
            [new SaDoKeyedRequest { DoNo = owner.OwnerDocumentNo, RowVersion = current.Value.RowVersion }],
            cancellationToken);
        return new CostingRepairStepResult(result.Succeeded, false, result.ErrorMessage, null);
    }

    private async Task<(string Status, byte[] RowVersion)?> LoadAsync(string doNo, CancellationToken cancellationToken)
    {
        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
            return null;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.SaDos.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.DoNo == doNo)
            .Select(x => new { x.Status, x.RowVersion })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : (row.Status, row.RowVersion);
    }
}

public sealed class SalesCreditNoteCostingRepairAdapter : ICostingRepairAdapter
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly ISaCdnService _creditNotes;

    public SalesCreditNoteCostingRepairAdapter(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        ISaCdnService creditNotes)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _creditNotes = creditNotes;
    }

    public IReadOnlySet<string> OwnerTypes { get; } = new HashSet<string>(StringComparer.Ordinal) { CostingRepairOwnerTypes.SalesCreditNote };

    public Task<CostingRepairCapability> CanHandleAsync(
        CostingRepairNode node, CostingRepairOwner owner, CancellationToken cancellationToken) =>
        Task.FromResult(new CostingRepairCapability(true, null));

    public async Task<CostingRepairStepResult> ReverseAsync(
        CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(owner.OwnerDocumentNo, cancellationToken);
        if (current is null || !string.Equals(current.Value.Status, "POSTED", StringComparison.OrdinalIgnoreCase))
            return new CostingRepairStepResult(false, true, "STALE_PREVIEW", null);
        var result = await _creditNotes.RollbackAsync(
            [new SaCdnKeyedRequest { DocNo = owner.OwnerDocumentNo, RowVersion = current.Value.RowVersion }],
            cancellationToken);
        return new CostingRepairStepResult(result.Succeeded, false, result.ErrorMessage, null);
    }

    public async Task<CostingRepairStepResult> RepostAsync(
        CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(owner.OwnerDocumentNo, cancellationToken);
        if (current is null)
            return new CostingRepairStepResult(false, true, "STALE_PREVIEW", null);
        var result = await _creditNotes.PostAsync(
            [new SaCdnKeyedRequest { DocNo = owner.OwnerDocumentNo, RowVersion = current.Value.RowVersion }],
            cancellationToken);
        return new CostingRepairStepResult(result.Succeeded, false, result.ErrorMessage, null);
    }

    private async Task<(string Status, byte[] RowVersion)?> LoadAsync(string docNo, CancellationToken cancellationToken)
    {
        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
            return null;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.SaCdns.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.DocNo == docNo)
            .Select(x => new { x.Status, x.RowVersion })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : (row.Status, row.RowVersion);
    }
}

public sealed class PurchaseGoodsReceiptRepairAdapter : ICostingRepairAdapter
{
    private readonly IIvGoodsReceiptService _goodsReceipts;
    private readonly IIvInventoryPostingService _posting;

    public PurchaseGoodsReceiptRepairAdapter(
        IIvGoodsReceiptService goodsReceipts,
        IIvInventoryPostingService posting)
    {
        _goodsReceipts = goodsReceipts;
        _posting = posting;
    }

    public IReadOnlySet<string> OwnerTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        CostingRepairOwnerTypes.PurchaseGoodsReceipt
    };

    public Task<CostingRepairCapability> CanHandleAsync(
        CostingRepairNode node, CostingRepairOwner owner, CancellationToken cancellationToken) =>
        Task.FromResult(new CostingRepairCapability(true, null));

    public async Task<CostingRepairStepResult> ReverseAsync(
        CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken)
    {
        if (!int.TryParse(owner.PhysicalSourceDocumentId, out var batchNo))
            return new CostingRepairStepResult(false, false, "The goods-receipt batch number is not numeric.", null);
        if (string.Equals(owner.PhysicalSourceDocumentType, IvTrxTypes.GoodsReceive, StringComparison.OrdinalIgnoreCase))
        {
            var result = await _goodsReceipts.RollbackAsync([batchNo], cancellationToken);
            return new CostingRepairStepResult(result.Succeeded, false, result.ErrorMessage, null);
        }

        var posted = await _posting.RollbackAsync(owner.PhysicalSourceDocumentType, [batchNo], cancellationToken);
        return new CostingRepairStepResult(posted.Succeeded, false, posted.ErrorMessage, null);
    }

    public async Task<CostingRepairStepResult> RepostAsync(
        CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken)
    {
        if (!int.TryParse(owner.PhysicalSourceDocumentId, out var batchNo))
            return new CostingRepairStepResult(false, false, "The goods-receipt batch number is not numeric.", null);
        var result = await _posting.PostAsync(owner.PhysicalSourceDocumentType, [batchNo], cancellationToken);
        return new CostingRepairStepResult(result.Succeeded, false, result.ErrorMessage, null);
    }
}

public sealed class PurchaseCdnStockRepairAdapter : ICostingRepairAdapter
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPoCdnService _creditNotes;

    public PurchaseCdnStockRepairAdapter(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IPoCdnService creditNotes)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _creditNotes = creditNotes;
    }

    public IReadOnlySet<string> OwnerTypes { get; } = new HashSet<string>(StringComparer.Ordinal) { CostingRepairOwnerTypes.PurchaseCreditNote };

    public Task<CostingRepairCapability> CanHandleAsync(
        CostingRepairNode node, CostingRepairOwner owner, CancellationToken cancellationToken) =>
        Task.FromResult(new CostingRepairCapability(true, null));

    public async Task<CostingRepairStepResult> ReverseAsync(
        CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(owner.OwnerDocumentNo, cancellationToken);
        if (current is null || !string.Equals(current.Value.Status, "POSTED", StringComparison.OrdinalIgnoreCase))
            return new CostingRepairStepResult(false, true, "STALE_PREVIEW", null);
        var result = await _creditNotes.RollbackAsync(
            [new PoCdnKeyedRequest { DocNo = owner.OwnerDocumentNo, RowVersion = current.Value.RowVersion }],
            cancellationToken);
        return new CostingRepairStepResult(result.Succeeded, false, result.ErrorMessage, null);
    }

    public async Task<CostingRepairStepResult> RepostAsync(
        CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(owner.OwnerDocumentNo, cancellationToken);
        if (current is null)
            return new CostingRepairStepResult(false, true, "STALE_PREVIEW", null);
        var result = await _creditNotes.PostAsync(
            [new PoCdnKeyedRequest { DocNo = owner.OwnerDocumentNo, RowVersion = current.Value.RowVersion }],
            cancellationToken);
        return new CostingRepairStepResult(result.Succeeded, false, result.ErrorMessage, null);
    }

    private async Task<(string Status, byte[] RowVersion)?> LoadAsync(string docNo, CancellationToken cancellationToken)
    {
        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
            return null;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.PoCdns.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.DocNo == docNo)
            .Select(x => new { x.Status, x.RowVersion })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : (row.Status, row.RowVersion);
    }
}

public sealed class ProductionMaterialIssueRepairAdapter : PreviewOnlyRepairAdapter
{
    public ProductionMaterialIssueRepairAdapter()
        : base(CostingRepairOwnerTypes.ProductionMaterialIssue,
            "Material issue repair stays preview-only until the production document command is rebuilt from the posting link.")
    {
    }
}

public sealed class ProductionOutputRepairAdapter : PreviewOnlyRepairAdapter
{
    public ProductionOutputRepairAdapter()
        : base(CostingRepairOwnerTypes.ProductionOutput,
            "Daily production repair stays preview-only until the production document command is rebuilt from the posting link.")
    {
    }
}

public sealed class ProductionFinishedGoodReceiptRepairAdapter : PreviewOnlyRepairAdapter
{
    public ProductionFinishedGoodReceiptRepairAdapter()
        : base(CostingRepairOwnerTypes.ProductionFinishedGood,
            "Finished-good repair stays preview-only until the production document command is rebuilt from the posting link.")
    {
    }
}

public abstract class PreviewOnlyRepairAdapter : ICostingRepairAdapter
{
    private readonly string _reason;

    protected PreviewOnlyRepairAdapter(string ownerType, string reason)
    {
        OwnerTypes = new HashSet<string>(StringComparer.Ordinal) { ownerType };
        _reason = reason;
    }

    public IReadOnlySet<string> OwnerTypes { get; }

    public Task<CostingRepairCapability> CanHandleAsync(
        CostingRepairNode node, CostingRepairOwner owner, CancellationToken cancellationToken) =>
        Task.FromResult(new CostingRepairCapability(false, _reason));

    public Task<CostingRepairStepResult> ReverseAsync(
        CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new CostingRepairStepResult(false, false, _reason, null));

    public Task<CostingRepairStepResult> RepostAsync(
        CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new CostingRepairStepResult(false, false, _reason, null));
}
