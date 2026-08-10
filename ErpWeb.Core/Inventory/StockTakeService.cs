using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

public sealed class StockTakeService : IStockTakeService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICompanyContext _companyContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IAccessRightService _accessRights;
    private readonly IPostingEngine _postingEngine;
    private readonly ILogger<StockTakeService> _logger;

    public StockTakeService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICompanyContext companyContext,
        ICurrentUserService currentUser,
        IAccessRightService accessRights,
        IPostingEngine postingEngine,
        ILogger<StockTakeService> logger)
    {
        _dbFactory = dbFactory;
        _companyContext = companyContext;
        _currentUser = currentUser;
        _accessRights = accessRights;
        _postingEngine = postingEngine;
        _logger = logger;
    }

    public async Task<PostingResultDto> CreateAsync(
        DateTime countDate, long warehouseId, IList<StockTakeLineInput> lines, CancellationToken ct = default)
    {
        var gate = await GateAsync(PermissionCodes.Add, ct);
        if (gate is not null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidCompany, gate);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var companyId = _companyContext.CompanyId;
        var branchId = _companyContext.BranchId;
        var user = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);

        if (!await db.Warehouses.AnyAsync(w => w.Id == warehouseId && w.CompanyId == companyId && w.BranchId == branchId, ct))
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidWarehouse);

        foreach (var line in lines)
        {
            var variant = await db.ItemVariants.AsNoTracking().FirstOrDefaultAsync(v => v.Id == line.ItemVariantId && v.CompanyId == companyId, ct);
            if (variant is null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "Invalid variant.");
            var item = await db.Items.AsNoTracking().FirstAsync(i => i.Id == variant.ItemId, ct);
            if (item.IsBatchItem && line.LotId is null)
                return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "Batch stock take lines require LotId.");
            if (!item.IsBatchItem && line.LotId is not null)
                return PostingResultDto.Fail(InventoryErrorCodes.LotNotAllowedInPhase);
        }

        var no = await InventorySequenceHelper.NextStockTakeNoAsync(db, companyId, branchId, countDate, user, ct);
        var take = new StockTake
        {
            CompanyId = companyId,
            BranchId = branchId,
            StockTakeNo = no,
            CountDate = countDate.Date,
            WarehouseId = warehouseId,
            Status = StockTakeStatus.DRAFT,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = user
        };

        var lineNo = 1;
        foreach (var line in lines)
        {
            var systemQty = line.SystemQty;
            if (systemQty == 0)
            {
                if (line.LotId is long lotId)
                {
                    var lotBal = await db.LotBalances.AsNoTracking().FirstOrDefaultAsync(b =>
                        b.CompanyId == companyId && b.BranchId == branchId &&
                        b.WarehouseId == warehouseId && b.LocationId == line.LocationId &&
                        b.LotId == lotId, ct);
                    systemQty = lotBal?.QtyOnHand ?? 0;
                }
                else
                {
                    var bal = await db.StockBalances.AsNoTracking().FirstOrDefaultAsync(b =>
                        b.CompanyId == companyId && b.BranchId == branchId &&
                        b.WarehouseId == warehouseId && b.LocationId == line.LocationId &&
                        b.ItemVariantId == line.ItemVariantId, ct);
                    systemQty = bal?.QtyOnHand ?? 0;
                }
            }

            take.Lines.Add(new StockTakeLine
            {
                CompanyId = companyId,
                BranchId = branchId,
                LineNo = lineNo++,
                ItemVariantId = line.ItemVariantId,
                LocationId = line.LocationId,
                LotId = line.LotId,
                SystemQty = systemQty,
                CountedQty = line.CountedQty,
                VarianceQty = line.CountedQty - systemQty,
                ReasonCodeId = line.ReasonCodeId,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = user
            });
        }

        db.StockTakes.Add(take);
        await db.SaveChangesAsync(ct);
        return ToResult(take);
    }

    public async Task<PostingResultDto> StartCountingAsync(long stockTakeId, CancellationToken ct = default)
    {
        return await TransitionAsync(stockTakeId, StockTakeStatus.DRAFT, StockTakeStatus.COUNTING, PermissionCodes.Edit, ct);
    }

    public async Task<PostingResultDto> CompleteCountingAsync(
        long stockTakeId, IList<StockTakeLineInput> countedLines, CancellationToken ct = default)
    {
        var gate = await GateAsync(PermissionCodes.Edit, ct);
        if (gate is not null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidCompany, gate);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var take = await LoadAsync(db, stockTakeId, ct);
        if (take is null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument);
        if (take.Status is not (StockTakeStatus.DRAFT or StockTakeStatus.COUNTING))
            return PostingResultDto.Fail(InventoryErrorCodes.StockTakeNotEditable);

        foreach (var input in countedLines)
        {
            var line = take.Lines.FirstOrDefault(l =>
                l.ItemVariantId == input.ItemVariantId && l.LocationId == input.LocationId);
            if (line is null) continue;
            line.CountedQty = input.CountedQty;
            line.VarianceQty = input.CountedQty - line.SystemQty;
            if (input.ReasonCodeId is not null) line.ReasonCodeId = input.ReasonCodeId;
        }

        take.Status = StockTakeStatus.COMPLETED;
        await db.SaveChangesAsync(ct);
        return ToResult(take);
    }

    public async Task<PostingResultDto> SubmitForApprovalAsync(long stockTakeId, CancellationToken ct = default)
    {
        return await TransitionAsync(stockTakeId, StockTakeStatus.COMPLETED, StockTakeStatus.PENDING_APPROVAL, PermissionCodes.Submit, ct);
    }

    public async Task<PostingResultDto> ApproveAsync(long stockTakeId, string approvedBy, CancellationToken ct = default)
    {
        var gate = await GateAsync(PermissionCodes.Approve, ct);
        if (gate is not null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidCompany, gate);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var take = await LoadAsync(db, stockTakeId, ct);
        if (take is null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument);
        if (take.Status is not (StockTakeStatus.COMPLETED or StockTakeStatus.PENDING_APPROVAL))
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidStatus);

        take.Status = StockTakeStatus.APPROVED;
        take.ApprovedBy = InventoryServiceHelper.Truncate(approvedBy, 50);
        take.ApprovedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToResult(take);
    }

    public async Task<PostingResultDto> GenerateAdjustmentAsync(long stockTakeId, CancellationToken ct = default)
    {
        var gate = await GateAsync(PermissionCodes.Approve, ct);
        if (gate is not null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidCompany, gate);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var take = await LoadAsync(db, stockTakeId, ct);
        if (take is null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument);
        if (take.Status != StockTakeStatus.APPROVED)
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidStatus, "Stock take must be APPROVED.");
        if (take.GeneratedAdjustmentDocumentId is not null)
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidStatus, "Adjustment already generated.");

        var varianceLines = take.Lines.Where(l => l.VarianceQty != 0).OrderBy(l => l.LineNo).ToList();
        if (varianceLines.Count == 0)
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "No variance to adjust.");

        // Ensure reason codes for SA
        foreach (var line in varianceLines)
        {
            if (line.ReasonCodeId is null)
                return PostingResultDto.Fail(InventoryErrorCodes.ReasonCodeRequired);
        }

        var variants = varianceLines.Select(l => l.ItemVariantId).Distinct().ToList();
        var items = await (
            from v in db.ItemVariants.AsNoTracking()
            join i in db.Items.AsNoTracking() on v.ItemId equals i.Id
            where variants.Contains(v.Id)
            select new { v.Id, i.BaseUOMId }).ToListAsync(ct);

        var create = new CreateDocumentDto
        {
            DocType = DocumentType.SA,
            DocDate = take.CountDate,
            WarehouseId = take.WarehouseId,
            StockTakeId = take.Id,
            Remarks = $"Stock take {take.StockTakeNo}",
            Lines = varianceLines.Select(l =>
            {
                var uom = items.First(x => x.Id == l.ItemVariantId).BaseUOMId;
                var abs = Math.Abs(l.VarianceQty);
                return new CreateDocumentLineDto
                {
                    ItemVariantId = l.ItemVariantId,
                    UOMId = uom,
                    Qty = abs,
                    UnitCost = 0,
                    LocationId = l.LocationId,
                    LotId = l.LotId,
                    Direction = l.VarianceQty > 0 ? AdjustmentDirection.Increase : AdjustmentDirection.Decrease,
                    ReasonCodeId = l.ReasonCodeId
                };
            }).ToList()
        };

        // Fill increase unit cost from current MAV so zero-cost rule is not tripped unintentionally
        foreach (var line in create.Lines.Where(l => l.Direction == AdjustmentDirection.Increase))
        {
            var avg = await db.ItemCosts.AsNoTracking()
                .Where(c => c.CompanyId == take.CompanyId && c.BranchId == take.BranchId &&
                            c.WarehouseId == take.WarehouseId && c.ItemVariantId == line.ItemVariantId)
                .Select(c => c.AverageCost)
                .FirstOrDefaultAsync(ct);
            line.UnitCost = avg;
        }

        var created = await _postingEngine.CreateDocumentAsync(create, ct);
        if (!created.Succeeded) return created;

        take.GeneratedAdjustmentDocumentId = created.Document!.Id;
        take.Status = StockTakeStatus.ADJUSTMENT_GENERATED;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Generated SA {DocNo} from stock take {StockTakeNo}", created.Document.DocNo, take.StockTakeNo);
        return created;
    }

    public async Task<PostingResultDto> PostGeneratedAdjustmentAsync(long stockTakeId, string postedBy, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var take = await LoadAsync(db, stockTakeId, ct);
        if (take is null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument);
        if (take.Status != StockTakeStatus.ADJUSTMENT_GENERATED || take.GeneratedAdjustmentDocumentId is null)
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidStatus);

        var post = await _postingEngine.PostAsync(take.GeneratedAdjustmentDocumentId.Value, postedBy, ct);
        if (!post.Succeeded) return post;

        take.Status = StockTakeStatus.POSTED;
        await db.SaveChangesAsync(ct);
        return post;
    }

    public async Task<StockTake?> GetAsync(long stockTakeId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await LoadAsync(db, stockTakeId, ct);
    }

    private async Task<PostingResultDto> TransitionAsync(
        long stockTakeId, StockTakeStatus from, StockTakeStatus to, string permission, CancellationToken ct)
    {
        var gate = await GateAsync(permission, ct);
        if (gate is not null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidCompany, gate);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var take = await LoadAsync(db, stockTakeId, ct);
        if (take is null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument);
        if (take.Status != from)
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidStatus);
        if (take.Status >= StockTakeStatus.APPROVED && to < StockTakeStatus.APPROVED)
            return PostingResultDto.Fail(InventoryErrorCodes.StockTakeNotEditable);

        take.Status = to;
        await db.SaveChangesAsync(ct);
        return ToResult(take);
    }

    private async Task<StockTake?> LoadAsync(AppDbContext db, long id, CancellationToken ct) =>
        await db.StockTakes.Include(t => t.Lines)
            .FirstOrDefaultAsync(t =>
                t.Id == id &&
                t.CompanyId == _companyContext.CompanyId &&
                t.BranchId == _companyContext.BranchId, ct);

    private static PostingResultDto ToResult(StockTake take) =>
        PostingResultDto.Ok(new DocumentDto
        {
            Id = take.Id,
            DocNo = take.StockTakeNo,
            DocType = DocumentType.SA,
            DocDate = take.CountDate,
            Status = DocumentStatus.DRAFT,
            WarehouseId = take.WarehouseId,
            Remarks = take.Status.ToString()
        }, []);

    private async Task<string?> GateAsync(string permission, CancellationToken ct)
    {
        var resolve = await InventoryServiceHelper.ResolveCompanyAsync(_companyContext, ct);
        if (!resolve.Ok) return resolve.Error;
        var access = await InventoryServiceHelper.EnsureAccessAsync(_accessRights, MenuCodes.InvStockTake, permission, ct);
        return access.Ok ? null : access.Error;
    }
}
