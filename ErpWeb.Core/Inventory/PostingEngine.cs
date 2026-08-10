using System.Data;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Inventory;

public sealed class PostingEngine : IPostingEngine
{
    private const int MaxAttempts = 3;
    private static readonly TimeZoneInfo KlTimeZone = ResolveKlTimeZone();

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICompanyContext _companyContext;
    private readonly ICurrentUserService _currentUser;
    private readonly IAccessRightService _accessRights;
    private readonly ILogger<PostingEngine> _logger;

    public PostingEngine(
        IDbContextFactory<AppDbContext> dbFactory,
        ICompanyContext companyContext,
        ICurrentUserService currentUser,
        IAccessRightService accessRights,
        ILogger<PostingEngine> logger)
    {
        _dbFactory = dbFactory;
        _companyContext = companyContext;
        _currentUser = currentUser;
        _accessRights = accessRights;
        _logger = logger;
    }

    public async Task<PostingResultDto> CreateDocumentAsync(CreateDocumentDto dto, CancellationToken ct = default)
    {
        var gate = await GateAsync(MenuFor(dto.DocType), PermissionCodes.Add, ct);
        if (gate is not null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidCompany, gate);

        if (dto.Lines.Count == 0)
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "At least one line is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var companyId = _companyContext.CompanyId;
        var branchId = _companyContext.BranchId;
        var user = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);

        var validation = await ValidateCreateAsync(db, companyId, branchId, dto, ct);
        if (validation is not null) return validation;

        var docNo = await InventorySequenceHelper.NextDocNoAsync(db, companyId, branchId, dto.DocType, dto.DocDate, user, ct);
        var doc = new InventoryDocument
        {
            CompanyId = companyId,
            BranchId = branchId,
            DocNo = docNo,
            DocType = dto.DocType,
            DocDate = dto.DocDate.Date,
            WarehouseId = dto.WarehouseId,
            SourceWarehouseId = dto.SourceWarehouseId,
            DestinationWarehouseId = dto.DestinationWarehouseId,
            SourceLocationId = dto.SourceLocationId,
            DestinationLocationId = dto.DestinationLocationId,
            ReferenceNo = dto.ReferenceNo,
            Remarks = dto.Remarks,
            AllowZeroCost = dto.AllowZeroCost,
            StockTakeId = dto.StockTakeId,
            ReversalOfDocumentId = dto.ReversalOfDocumentId,
            Status = DocumentStatus.DRAFT,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = user
        };

        var lineNo = 1;
        foreach (var line in dto.Lines)
        {
            var docLine = new InventoryDocumentLine
            {
                CompanyId = companyId,
                BranchId = branchId,
                LineNo = lineNo++,
                ItemVariantId = line.ItemVariantId,
                UOMId = line.UOMId,
                Qty = line.Qty,
                UnitCost = line.UnitCost,
                LocationId = line.LocationId,
                LotNo = string.IsNullOrWhiteSpace(line.LotNo) ? null : line.LotNo.Trim().ToUpperInvariant(),
                LotId = line.LotId,
                Direction = line.Direction,
                ReasonCodeId = line.ReasonCodeId,
                Remarks = line.Remarks,
                ConversionRateUsed = 1m,
                QtyInBase = line.Qty,
                TotalCost = Math.Round(line.Qty * line.UnitCost, 4, MidpointRounding.AwayFromZero),
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = user
            };

            if (line.LotAllocations is { Count: > 0 })
            {
                foreach (var split in line.LotAllocations)
                {
                    docLine.LotSplits.Add(new InventoryDocumentLineLotSplit
                    {
                        CompanyId = companyId,
                        BranchId = branchId,
                        LotId = split.LotId > 0 ? split.LotId : null,
                        LotNo = string.IsNullOrWhiteSpace(split.LotNo) ? null : split.LotNo.Trim().ToUpperInvariant(),
                        Qty = split.Qty,
                        CreatedAtUtc = DateTime.UtcNow,
                        CreatedBy = user
                    });
                }

                docLine.Qty = line.LotAllocations.Sum(a => a.Qty);
                docLine.QtyInBase = docLine.Qty;
                docLine.TotalCost = Math.Round(docLine.Qty * line.UnitCost, 4, MidpointRounding.AwayFromZero);
            }

            doc.Lines.Add(docLine);
        }

        db.InventoryDocuments.Add(doc);
        await db.SaveChangesAsync(ct);

        var mapped = await MapDocumentAsync(db, doc.Id, includeCost: true, ct);
        return PostingResultDto.Ok(mapped!, []);
    }

    public async Task<PostingResultDto> SubmitForApprovalAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var doc = await LoadDocumentAsync(db, documentId, ct);
        if (doc is null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument);
        var gate = await GateAsync(MenuFor(doc.DocType), PermissionCodes.Submit, ct);
        if (gate is not null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidCompany, gate);
        if (doc.Status != DocumentStatus.DRAFT)
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidStatus);
        doc.Status = DocumentStatus.SUBMITTED;
        await db.SaveChangesAsync(ct);
        return PostingResultDto.Ok((await MapDocumentAsync(db, doc.Id, true, ct))!, []);
    }

    public async Task<PostingResultDto> ApproveAsync(long documentId, string approvedBy, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var doc = await LoadDocumentAsync(db, documentId, ct);
        if (doc is null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument);
        var gate = await GateAsync(MenuFor(doc.DocType), PermissionCodes.Approve, ct);
        if (gate is not null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidCompany, gate);
        if (doc.Status is not (DocumentStatus.DRAFT or DocumentStatus.SUBMITTED))
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidStatus);
        doc.Status = DocumentStatus.APPROVED;
        doc.ApprovedBy = InventoryServiceHelper.Truncate(approvedBy, 50);
        doc.ApprovedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return PostingResultDto.Ok((await MapDocumentAsync(db, doc.Id, true, ct))!, []);
    }

    public async Task<PostingResultDto> CancelAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var doc = await LoadDocumentAsync(db, documentId, ct);
        if (doc is null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument);
        var gate = await GateAsync(MenuFor(doc.DocType), PermissionCodes.Cancel, ct);
        if (gate is not null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidCompany, gate);
        if (doc.Status is DocumentStatus.POSTED or DocumentStatus.REVERSED)
            return PostingResultDto.Fail(InventoryErrorCodes.DocumentNotEditableWhenPosted);
        doc.Status = DocumentStatus.CANCELLED;
        await db.SaveChangesAsync(ct);
        return PostingResultDto.Ok((await MapDocumentAsync(db, doc.Id, true, ct))!, []);
    }

    public async Task<PostingResultDto> PostAsync(long documentId, string postedBy, CancellationToken ct = default)
    {
        PostingResultDto? lastDeadlock = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await PostOnceAsync(documentId, postedBy, ct);
            }
            catch (Exception ex) when (IsDeadlock(ex) && attempt < MaxAttempts)
            {
                _logger.LogWarning(ex, "PostAsync deadlock attempt {Attempt}/{Max} for document {DocumentId}", attempt, MaxAttempts, documentId);
                lastDeadlock = PostingResultDto.Fail(InventoryErrorCodes.DeadlockRetryExhausted);
            }
            catch (Exception ex) when (IsDeadlock(ex))
            {
                _logger.LogError(ex, "PostAsync deadlock exhausted for document {DocumentId}", documentId);
                return PostingResultDto.Fail(InventoryErrorCodes.DeadlockRetryExhausted);
            }
        }

        return lastDeadlock ?? PostingResultDto.Fail(InventoryErrorCodes.DeadlockRetryExhausted);
    }

    public async Task<PostingResultDto> ReverseAsync(long documentId, string reversedBy, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var original = await LoadDocumentAsync(db, documentId, ct);
        if (original is null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument);

        var gate = await GateAsync(MenuFor(original.DocType), PermissionCodes.Reverse, ct);
        if (gate is not null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidCompany, gate);

        if (original.Status != DocumentStatus.POSTED)
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidStatus, "Only posted documents can be reversed.");

        var already = await db.InventoryDocuments.AnyAsync(d =>
            d.CompanyId == original.CompanyId &&
            d.BranchId == original.BranchId &&
            d.ReversalOfDocumentId == original.Id &&
            d.Status != DocumentStatus.CANCELLED, ct);
        if (already)
            return PostingResultDto.Fail(InventoryErrorCodes.DocumentAlreadyReversed);

        var create = new CreateDocumentDto
        {
            DocType = original.DocType,
            DocDate = original.DocDate,
            WarehouseId = original.WarehouseId,
            SourceWarehouseId = original.SourceWarehouseId,
            DestinationWarehouseId = original.DestinationWarehouseId,
            SourceLocationId = original.SourceLocationId,
            DestinationLocationId = original.DestinationLocationId,
            ReferenceNo = $"REV-{original.DocNo}",
            Remarks = $"Reversal of {original.DocNo}",
            AllowZeroCost = true,
            ReversalOfDocumentId = original.Id,
            Lines = original.Lines.OrderBy(l => l.LineNo).Select(l => new CreateDocumentLineDto
            {
                ItemVariantId = l.ItemVariantId,
                UOMId = l.UOMId,
                Qty = l.Qty,
                UnitCost = l.UnitCost,
                LocationId = l.LocationId,
                LotNo = l.LotNo,
                LotId = l.LotId,
                Direction = InvertDirection(original.DocType, l.Direction),
                ReasonCodeId = l.ReasonCodeId,
                Remarks = l.Remarks,
                LotAllocations = l.LotSplits.Count > 0
                    ? l.LotSplits.Select(s => new LotAllocationInput
                    {
                        LotId = s.LotId ?? 0,
                        LotNo = s.LotNo,
                        Qty = s.Qty
                    }).ToList()
                    : null
            }).ToList()
        };

        // For ST reverse: swap source/destination so OUT/IN invert
        if (original.DocType == DocumentType.ST)
        {
            create.SourceWarehouseId = original.DestinationWarehouseId;
            create.DestinationWarehouseId = original.SourceWarehouseId;
            create.SourceLocationId = original.DestinationLocationId;
            create.DestinationLocationId = original.SourceLocationId;
            foreach (var line in create.Lines)
            {
                line.LocationId = original.DestinationLocationId
                    ?? throw new InvalidOperationException("Destination location required.");
            }
        }

        // Bypass Add gate for reverse create — Reverse permission already checked
        var created = await CreateReversalDocumentInternalAsync(db, create, ct);
        if (!created.Succeeded) return created;

        var post = await PostAsync(created.Document!.Id, reversedBy, ct);
        if (!post.Succeeded) return post;

        await using var db2 = await _dbFactory.CreateDbContextAsync(ct);
        var orig = await db2.InventoryDocuments.SingleAsync(d => d.Id == documentId, ct);
        orig.Status = DocumentStatus.REVERSED;
        orig.ModifiedAtUtc = DateTime.UtcNow;
        orig.ModifiedBy = InventoryServiceHelper.Truncate(reversedBy, 50);
        await db2.SaveChangesAsync(ct);

        return post;
    }

    public async Task<DocumentDto?> GetDocumentAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await MapDocumentAsync(db, documentId, includeCost: await CanViewCostAsync(ct), ct);
    }

    private async Task<PostingResultDto> CreateReversalDocumentInternalAsync(
        AppDbContext db, CreateDocumentDto dto, CancellationToken ct)
    {
        var companyId = _companyContext.CompanyId;
        var branchId = _companyContext.BranchId;
        var user = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);
        var validation = await ValidateCreateAsync(db, companyId, branchId, dto, ct);
        if (validation is not null) return validation;

        var docNo = await InventorySequenceHelper.NextDocNoAsync(db, companyId, branchId, dto.DocType, dto.DocDate, user, ct);
        var doc = new InventoryDocument
        {
            CompanyId = companyId,
            BranchId = branchId,
            DocNo = docNo,
            DocType = dto.DocType,
            DocDate = dto.DocDate.Date,
            WarehouseId = dto.WarehouseId,
            SourceWarehouseId = dto.SourceWarehouseId,
            DestinationWarehouseId = dto.DestinationWarehouseId,
            SourceLocationId = dto.SourceLocationId,
            DestinationLocationId = dto.DestinationLocationId,
            ReferenceNo = dto.ReferenceNo,
            Remarks = dto.Remarks,
            AllowZeroCost = true,
            ReversalOfDocumentId = dto.ReversalOfDocumentId,
            Status = DocumentStatus.DRAFT,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = user
        };

        var lineNo = 1;
        foreach (var line in dto.Lines)
        {
            var docLine = new InventoryDocumentLine
            {
                CompanyId = companyId,
                BranchId = branchId,
                LineNo = lineNo++,
                ItemVariantId = line.ItemVariantId,
                UOMId = line.UOMId,
                Qty = line.Qty,
                UnitCost = line.UnitCost,
                LocationId = line.LocationId,
                LotNo = line.LotNo,
                LotId = line.LotId,
                Direction = line.Direction,
                ReasonCodeId = line.ReasonCodeId,
                Remarks = line.Remarks,
                ConversionRateUsed = 1m,
                QtyInBase = line.Qty,
                TotalCost = Math.Round(line.Qty * line.UnitCost, 4, MidpointRounding.AwayFromZero),
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = user
            };
            if (line.LotAllocations is { Count: > 0 })
            {
                foreach (var split in line.LotAllocations)
                {
                    docLine.LotSplits.Add(new InventoryDocumentLineLotSplit
                    {
                        CompanyId = companyId,
                        BranchId = branchId,
                        LotId = split.LotId > 0 ? split.LotId : null,
                        LotNo = split.LotNo,
                        Qty = split.Qty,
                        CreatedAtUtc = DateTime.UtcNow,
                        CreatedBy = user
                    });
                }
            }
            doc.Lines.Add(docLine);
        }

        db.InventoryDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        return PostingResultDto.Ok((await MapDocumentAsync(db, doc.Id, true, ct))!, []);
    }

    private static AdjustmentDirection? InvertDirection(DocumentType docType, AdjustmentDirection? direction)
    {
        if (docType != DocumentType.SA || direction is null) return direction;
        return direction == AdjustmentDirection.Increase
            ? AdjustmentDirection.Decrease
            : AdjustmentDirection.Increase;
    }

    private async Task<PostingResultDto> PostOnceAsync(long documentId, string postedBy, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var doc = await db.InventoryDocuments
            .Include(d => d.Lines)
            .ThenInclude(l => l.LotSplits)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (doc is null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument);

        var gate = await GateAsync(MenuFor(doc.DocType), PermissionCodes.Post, ct);
        if (gate is not null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidCompany, gate);

        if (doc.CompanyId != _companyContext.CompanyId || doc.BranchId != _companyContext.BranchId)
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidBranch);

        if (doc.Status == DocumentStatus.POSTED)
        {
            var existingIds = await db.StockLedgers.Where(l => l.DocumentId == doc.Id).Select(l => l.Id).ToListAsync(ct);
            await tx.CommitAsync(ct);
            return PostingResultDto.Ok((await MapDocumentAsync(db, doc.Id, true, ct))!, existingIds);
        }

        if (doc.Status is DocumentStatus.CANCELLED or DocumentStatus.REVERSED)
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidStatus);

        // Zero-cost GRN / SA increase gate
        if (NeedsZeroCostApproval(doc))
        {
            var canApprove = await _accessRights.CanAsync(MenuFor(doc.DocType), PermissionCodes.Approve, ct)
                             || string.Equals(_currentUser.UserLevel, "SYSTEM_ADMIN", StringComparison.OrdinalIgnoreCase);
            if (!doc.AllowZeroCost || !canApprove)
                return PostingResultDto.Fail(InventoryErrorCodes.ZeroCostNotAllowed);
        }

        if (doc.DocType == DocumentType.OB && doc.Lines.Any(l => l.UnitCost == 0))
        {
            _logger.LogWarning(
                "AUDIT OB zero-cost receipt DocumentId={DocumentId} DocNo={DocNo} PostedBy={PostedBy}",
                doc.Id, doc.DocNo, postedBy);
        }

        var pre = await ValidateForPostAsync(db, doc, ct);
        if (pre is not null) return pre;

        var user = InventoryServiceHelper.Truncate(postedBy, 50);
        var postedAt = DateTime.UtcNow;

        // Acquire locks / ensure balance & cost rows in fixed order
        var lockKeys = BuildLockKeys(doc);
        foreach (var key in lockKeys)
        {
            await EnsureBalanceAndCostAsync(db, doc.CompanyId, doc.BranchId, key.WarehouseId, key.LocationId, key.ItemVariantId, ct);
            await LockBalanceAndCostAsync(db, doc.CompanyId, doc.BranchId, key.WarehouseId, key.LocationId, key.ItemVariantId, ct);
        }

        // Resolve lot identities for batch lines, ensure/lock LotBalance (LotId ascending)
        var lotResolve = await ResolveLotsForPostAsync(db, doc, user, ct);
        if (lotResolve is not null) return lotResolve;

        var lotLockKeys = BuildLotLockKeys(doc);
        foreach (var key in lotLockKeys.OrderBy(k => k.LotId).ThenBy(k => k.WarehouseId).ThenBy(k => k.LocationId))
        {
            await LotPostingHelper.EnsureLotBalanceAsync(db, doc.CompanyId, doc.BranchId, key.LotId, key.WarehouseId, key.LocationId, user, ct);
            await LotPostingHelper.LockLotBalanceAsync(db, doc.CompanyId, doc.BranchId, key.LotId, key.WarehouseId, key.LocationId, ct);
        }

        var ledgerIds = new List<long>();

        foreach (var line in doc.Lines.OrderBy(l => l.LineNo))
        {
            var conv = await ResolveConversionAsync(db, doc.CompanyId, line, ct);
            if (!conv.Ok) return PostingResultDto.Fail(conv.ErrorCode!, conv.ErrorMessage);
            line.ConversionRateUsed = conv.Rate;
            line.QtyInBase = conv.QtyInBase;

            var movements = await BuildMovementsAsync(db, doc, line, ct);
            if (!movements.Ok) return PostingResultDto.Fail(movements.ErrorCode!, movements.ErrorMessage);

            foreach (var move in movements.Items)
            {
                // Negative stock check for outbound
                if (move.QtyOutBase > 0)
                {
                    var bal = await GetBalanceAsync(db, doc.CompanyId, doc.BranchId, move.WarehouseId, move.LocationId, line.ItemVariantId, ct);
                    if (bal.AvailableQty < move.QtyOutBase)
                        return PostingResultDto.Fail(InventoryErrorCodes.InsufficientStock);

                    if (move.LotId is long outLotId)
                    {
                        var lotBal = await db.LotBalances.FirstOrDefaultAsync(b =>
                            b.CompanyId == doc.CompanyId && b.BranchId == doc.BranchId &&
                            b.LotId == outLotId && b.WarehouseId == move.WarehouseId && b.LocationId == move.LocationId, ct);
                        if (lotBal is null || lotBal.QtyOnHand < move.QtyOutBase)
                            return PostingResultDto.Fail(InventoryErrorCodes.InsufficientStock, "Insufficient lot quantity.");
                    }
                }

                // Backdate check
                var backdate = await CheckBackdateAsync(db, doc, move.WarehouseId, line.ItemVariantId, ct);
                if (backdate is not null) return backdate;

                var unitCost = move.UnitCost;
                var amount = Math.Round(move.QtyInBase > 0 ? move.QtyInBase * unitCost : move.QtyOutBase * unitCost, 4, MidpointRounding.AwayFromZero);
                line.UnitCost = unitCost;
                line.TotalCost = amount;

                var meta = await LoadLineMetaAsync(db, doc.CompanyId, line, move, ct);
                var ledgerSeq = await InventorySequenceHelper.NextLedgerSequenceAsync(db, doc.CompanyId, doc.BranchId, user, ct);

                var ledger = new StockLedger
                {
                    CompanyId = doc.CompanyId,
                    BranchId = doc.BranchId,
                    TransactionDate = doc.DocDate.Date,
                    LedgerSequence = ledgerSeq,
                    DocType = doc.DocType,
                    DocNo = doc.DocNo,
                    LineNo = line.LineNo,
                    DocumentId = doc.Id,
                    DocumentLineId = line.Id,
                    ReferenceNo = doc.ReferenceNo,
                    PostedBy = user,
                    PostedAtUtc = postedAt,
                    ItemVariantId = line.ItemVariantId,
                    SKU = meta.Sku,
                    ItemDescription = meta.ItemDescription,
                    UOMId = line.UOMId,
                    UOMCode = meta.UomCode,
                    UnitQty = move.QtyInBase > 0 ? move.QtyInBase : move.QtyOutBase,
                    ConversionRateUsed = line.ConversionRateUsed,
                    QtyInBase = move.QtyInBase,
                    QtyOutBase = move.QtyOutBase,
                    WarehouseId = move.WarehouseId,
                    WarehouseCode = meta.WarehouseCode,
                    LocationId = move.LocationId,
                    LocationCode = meta.LocationCode,
                    LotId = move.LotId,
                    LotNo = move.LotNo,
                    ReasonCodeId = line.ReasonCodeId,
                    ReasonCodeValue = meta.ReasonCode,
                    UnitCost = unitCost,
                    Amount = amount,
                    CurrencyCode = "MYR",
                    ExchangeRate = 1m,
                    BaseAmount = amount,
                    CostingMethod = CostingMethod.MOVING_AVG,
                    CreatedAtUtc = postedAt,
                    CreatedBy = user
                };
                db.StockLedgers.Add(ledger);
                await db.SaveChangesAsync(ct);

                db.StockMovementAllocations.Add(new StockMovementAllocation
                {
                    CompanyId = doc.CompanyId,
                    BranchId = doc.BranchId,
                    DocumentLineId = line.Id,
                    StockLedgerId = ledger.Id,
                    SourceLotId = move.QtyOutBase > 0 ? move.LotId : null,
                    TargetLotId = move.QtyInBase > 0 ? move.LotId : null,
                    Quantity = move.QtyInBase > 0 ? move.QtyInBase : move.QtyOutBase,
                    UnitCost = unitCost,
                    Amount = amount,
                    CreatedAtUtc = postedAt,
                    CreatedBy = user
                });

                await ApplyBalanceAsync(db, doc.CompanyId, doc.BranchId, move.WarehouseId, move.LocationId, line.ItemVariantId,
                    move.QtyInBase - move.QtyOutBase, postedAt, user, ct);
                await db.SaveChangesAsync(ct);
                await ApplyItemCostAsync(db, doc.CompanyId, doc.BranchId, move.WarehouseId, line.ItemVariantId,
                    move.QtyInBase, move.QtyOutBase, unitCost, doc.Id, postedAt, user, ct);

                if (move.LotId is long lotId)
                {
                    await LotPostingHelper.ApplyLotBalanceAsync(
                        db, doc.CompanyId, doc.BranchId, lotId, move.WarehouseId, move.LocationId,
                        move.QtyInBase - move.QtyOutBase, user, ct);
                }

                await db.SaveChangesAsync(ct);
                ledgerIds.Add(ledger.Id);
            }
        }

        doc.Status = DocumentStatus.POSTED;
        doc.PostedBy = user;
        doc.PostedAtUtc = postedAt;
        doc.ModifiedAtUtc = postedAt;
        doc.ModifiedBy = user;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return PostingResultDto.Ok((await MapDocumentAsync(db, doc.Id, true, ct))!, ledgerIds);
    }

    private static bool NeedsZeroCostApproval(InventoryDocument doc)
    {
        if (doc.DocType == DocumentType.GRN)
            return doc.Lines.Any(l => l.UnitCost == 0);
        if (doc.DocType == DocumentType.SA)
            return doc.Lines.Any(l => l.Direction == AdjustmentDirection.Increase && l.UnitCost == 0);
        return false;
    }

    private sealed record LockKey(long ItemVariantId, long WarehouseId, long LocationId);

    private static List<LockKey> BuildLockKeys(InventoryDocument doc)
    {
        var keys = new List<LockKey>();
        foreach (var line in doc.Lines)
        {
            if (doc.DocType == DocumentType.ST)
            {
                keys.Add(new LockKey(line.ItemVariantId, doc.SourceWarehouseId!.Value, line.LocationId));
                keys.Add(new LockKey(line.ItemVariantId, doc.DestinationWarehouseId!.Value, doc.DestinationLocationId!.Value));
            }
            else
            {
                keys.Add(new LockKey(line.ItemVariantId, doc.WarehouseId!.Value, line.LocationId));
            }
        }

        return keys
            .Distinct()
            .OrderBy(k => k.ItemVariantId)
            .ThenBy(k => k.WarehouseId)
            .ThenBy(k => k.LocationId)
            .ToList();
    }

    private async Task EnsureBalanceAndCostAsync(
        AppDbContext db, int companyId, long branchId, long warehouseId, long locationId, long itemVariantId, CancellationToken ct)
    {
        var user = InventoryServiceHelper.Truncate(_currentUser.UserId, 50);
        var bal = await db.StockBalances.FirstOrDefaultAsync(b =>
            b.CompanyId == companyId && b.BranchId == branchId &&
            b.WarehouseId == warehouseId && b.LocationId == locationId && b.ItemVariantId == itemVariantId, ct);
        if (bal is null)
        {
            var created = new StockBalance
            {
                CompanyId = companyId,
                BranchId = branchId,
                WarehouseId = warehouseId,
                LocationId = locationId,
                ItemVariantId = itemVariantId,
                QtyOnHand = 0,
                ReservedQty = 0,
                LastUpdatedAtUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = user
            };
            db.StockBalances.Add(created);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.Entry(created).State = EntityState.Detached;
            }
        }

        var cost = await db.ItemCosts.FirstOrDefaultAsync(c =>
            c.CompanyId == companyId && c.BranchId == branchId &&
            c.WarehouseId == warehouseId && c.ItemVariantId == itemVariantId, ct);
        if (cost is null)
        {
            var created = new ItemCost
            {
                CompanyId = companyId,
                BranchId = branchId,
                WarehouseId = warehouseId,
                ItemVariantId = itemVariantId,
                AverageCost = 0,
                LastCost = 0,
                LastUpdatedAtUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = user
            };
            db.ItemCosts.Add(created);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.Entry(created).State = EntityState.Detached;
            }
        }
    }

    private static async Task LockBalanceAndCostAsync(
        AppDbContext db, int companyId, long branchId, long warehouseId, long locationId, long itemVariantId, CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                SELECT Id FROM StockBalance WITH (UPDLOCK, HOLDLOCK)
                WHERE CompanyId={0} AND BranchId={1} AND WarehouseId={2} AND LocationId={3} AND ItemVariantId={4};
                SELECT Id FROM ItemCost WITH (UPDLOCK, HOLDLOCK)
                WHERE CompanyId={0} AND BranchId={1} AND WarehouseId={2} AND ItemVariantId={4};
                """,
                [companyId, branchId, warehouseId, locationId, itemVariantId], ct);
        }
        else
        {
            // SQLite: force tracked reload under the open transaction
            _ = await db.StockBalances.FirstAsync(b =>
                b.CompanyId == companyId && b.BranchId == branchId &&
                b.WarehouseId == warehouseId && b.LocationId == locationId && b.ItemVariantId == itemVariantId, ct);
            _ = await db.ItemCosts.FirstAsync(c =>
                c.CompanyId == companyId && c.BranchId == branchId &&
                c.WarehouseId == warehouseId && c.ItemVariantId == itemVariantId, ct);
        }
    }

    private sealed class MovementResult
    {
        public bool Ok { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
        public List<Movement> Items { get; init; } = [];
        public static MovementResult Fail(string code, string? msg = null) => new() { Ok = false, ErrorCode = code, ErrorMessage = msg };
        public static MovementResult Success(List<Movement> items) => new() { Ok = true, Items = items };
    }

    private sealed class Movement
    {
        public long WarehouseId { get; init; }
        public long LocationId { get; init; }
        public decimal QtyInBase { get; init; }
        public decimal QtyOutBase { get; init; }
        public decimal UnitCost { get; init; }
        public long? LotId { get; init; }
        public string? LotNo { get; init; }
    }

    private async Task<MovementResult> BuildMovementsAsync(
        AppDbContext db, InventoryDocument doc, InventoryDocumentLine line, CancellationToken ct)
    {
        var isReversal = doc.ReversalOfDocumentId is not null;
        var itemCtx = await LotPostingHelper.LoadItemContextAsync(db, doc.CompanyId, line.ItemVariantId, ct);
        if (itemCtx is null)
            return MovementResult.Fail(InventoryErrorCodes.InvalidDocument, "Item variant not found.");

        // Multi-lot splits → one movement set per split (Phase 3)
        if (line.LotSplits.Count > 0)
        {
            var all = new List<Movement>();
            foreach (var split in line.LotSplits.OrderBy(s => s.Id))
            {
                var splitQtyBase = Math.Round(split.Qty * line.ConversionRateUsed, 6, MidpointRounding.AwayFromZero);
                var clone = CloneLineForSplit(line, split.LotId, split.LotNo, splitQtyBase);
                var part = await BuildSingleLotMovementsAsync(db, doc, clone, splitQtyBase, isReversal, ct);
                if (!part.Ok) return part;
                all.AddRange(part.Items);
            }
            return MovementResult.Success(all);
        }

        return await BuildSingleLotMovementsAsync(db, doc, line, line.QtyInBase, isReversal, ct);
    }

    private static InventoryDocumentLine CloneLineForSplit(
        InventoryDocumentLine line, long? lotId, string? lotNo, decimal qtyInBase) =>
        new()
        {
            Id = line.Id,
            DocumentId = line.DocumentId,
            LineNo = line.LineNo,
            ItemVariantId = line.ItemVariantId,
            UOMId = line.UOMId,
            Qty = qtyInBase,
            QtyInBase = qtyInBase,
            ConversionRateUsed = line.ConversionRateUsed,
            UnitCost = line.UnitCost,
            LocationId = line.LocationId,
            LotId = lotId,
            LotNo = lotNo,
            Direction = line.Direction,
            ReasonCodeId = line.ReasonCodeId,
            CompanyId = line.CompanyId,
            BranchId = line.BranchId
        };

    private async Task<MovementResult> BuildSingleLotMovementsAsync(
        AppDbContext db,
        InventoryDocument doc,
        InventoryDocumentLine line,
        decimal qty,
        bool isReversal,
        CancellationToken ct)
    {
        long? lotId = line.LotId;
        string? lotNo = line.LotNo;

        switch (doc.DocType)
        {
            case DocumentType.OB:
            case DocumentType.GRN:
            {
                if (isReversal)
                {
                    return MovementResult.Success([new Movement
                    {
                        WarehouseId = doc.WarehouseId!.Value,
                        LocationId = line.LocationId,
                        QtyInBase = 0,
                        QtyOutBase = qty,
                        UnitCost = line.UnitCost,
                        LotId = lotId,
                        LotNo = lotNo
                    }]);
                }

                return MovementResult.Success([new Movement
                {
                    WarehouseId = doc.WarehouseId!.Value,
                    LocationId = line.LocationId,
                    QtyInBase = qty,
                    QtyOutBase = 0,
                    UnitCost = line.UnitCost,
                    LotId = lotId,
                    LotNo = lotNo
                }]);
            }
            case DocumentType.GI:
            {
                if (isReversal)
                {
                    return MovementResult.Success([new Movement
                    {
                        WarehouseId = doc.WarehouseId!.Value,
                        LocationId = line.LocationId,
                        QtyInBase = qty,
                        QtyOutBase = 0,
                        UnitCost = line.UnitCost,
                        LotId = lotId,
                        LotNo = lotNo
                    }]);
                }

                var avg = await GetWarehouseAverageAsync(db, doc.CompanyId, doc.BranchId, doc.WarehouseId!.Value, line.ItemVariantId, ct);
                return MovementResult.Success([new Movement
                {
                    WarehouseId = doc.WarehouseId!.Value,
                    LocationId = line.LocationId,
                    QtyInBase = 0,
                    QtyOutBase = qty,
                    UnitCost = avg,
                    LotId = lotId,
                    LotNo = lotNo
                }]);
            }
            case DocumentType.SA:
            {
                if (line.Direction is null)
                    return MovementResult.Fail(InventoryErrorCodes.InvalidDocument, "SA line requires Direction.");
                if (line.Direction == AdjustmentDirection.Increase)
                {
                    return MovementResult.Success([new Movement
                    {
                        WarehouseId = doc.WarehouseId!.Value,
                        LocationId = line.LocationId,
                        QtyInBase = qty,
                        QtyOutBase = 0,
                        UnitCost = line.UnitCost,
                        LotId = lotId,
                        LotNo = lotNo
                    }]);
                }

                var avg = isReversal
                    ? line.UnitCost
                    : await GetWarehouseAverageAsync(db, doc.CompanyId, doc.BranchId, doc.WarehouseId!.Value, line.ItemVariantId, ct);
                return MovementResult.Success([new Movement
                {
                    WarehouseId = doc.WarehouseId!.Value,
                    LocationId = line.LocationId,
                    QtyInBase = 0,
                    QtyOutBase = qty,
                    UnitCost = avg,
                    LotId = lotId,
                    LotNo = lotNo
                }]);
            }
            case DocumentType.ST:
            {
                var srcWh = doc.SourceWarehouseId!.Value;
                var dstWh = doc.DestinationWarehouseId!.Value;
                var srcLoc = line.LocationId;
                var dstLoc = doc.DestinationLocationId!.Value;
                var outCost = isReversal
                    ? line.UnitCost
                    : await GetWarehouseAverageAsync(db, doc.CompanyId, doc.BranchId, srcWh, line.ItemVariantId, ct);

                // Phase 3 batch transfer: preserve same LotId on OUT and IN
                return MovementResult.Success([
                    new Movement { WarehouseId = srcWh, LocationId = srcLoc, QtyInBase = 0, QtyOutBase = qty, UnitCost = outCost, LotId = lotId, LotNo = lotNo },
                    new Movement { WarehouseId = dstWh, LocationId = dstLoc, QtyInBase = qty, QtyOutBase = 0, UnitCost = outCost, LotId = lotId, LotNo = lotNo }
                ]);
            }
            default:
                return MovementResult.Fail(InventoryErrorCodes.InvalidDocument, "Unsupported document type.");
        }
    }

    private static async Task<decimal> GetWarehouseAverageAsync(
        AppDbContext db, int companyId, long branchId, long warehouseId, long itemVariantId, CancellationToken ct)
    {
        var cost = await db.ItemCosts.AsNoTracking().FirstOrDefaultAsync(c =>
            c.CompanyId == companyId && c.BranchId == branchId &&
            c.WarehouseId == warehouseId && c.ItemVariantId == itemVariantId, ct);
        return cost?.AverageCost ?? 0m;
    }

    private static async Task<StockBalance> GetBalanceAsync(
        AppDbContext db, int companyId, long branchId, long warehouseId, long locationId, long itemVariantId, CancellationToken ct)
    {
        return await db.StockBalances.FirstAsync(b =>
            b.CompanyId == companyId && b.BranchId == branchId &&
            b.WarehouseId == warehouseId && b.LocationId == locationId && b.ItemVariantId == itemVariantId, ct);
    }

    private static async Task ApplyBalanceAsync(
        AppDbContext db, int companyId, long branchId, long warehouseId, long locationId, long itemVariantId,
        decimal delta, DateTime utc, string? user, CancellationToken ct)
    {
        var bal = await GetBalanceAsync(db, companyId, branchId, warehouseId, locationId, itemVariantId, ct);
        bal.QtyOnHand += delta;
        if (bal.QtyOnHand < 0)
            throw new InvalidOperationException(InventoryErrorCodes.InsufficientStock);
        bal.LastUpdatedAtUtc = utc;
        bal.ModifiedAtUtc = utc;
        bal.ModifiedBy = user;
    }

    private static async Task ApplyItemCostAsync(
        AppDbContext db, int companyId, long branchId, long warehouseId, long itemVariantId,
        decimal qtyIn, decimal qtyOut, decimal unitCost, long documentId, DateTime utc, string? user, CancellationToken ct)
    {
        var cost = await db.ItemCosts.FirstAsync(c =>
            c.CompanyId == companyId && c.BranchId == branchId &&
            c.WarehouseId == warehouseId && c.ItemVariantId == itemVariantId, ct);

        var q = await db.StockBalances
            .Where(b => b.CompanyId == companyId && b.BranchId == branchId &&
                        b.WarehouseId == warehouseId && b.ItemVariantId == itemVariantId)
            .SumAsync(b => (decimal?)b.QtyOnHand, ct) ?? 0m;

        // q already includes the balance delta applied just before this call for the current location.
        // For MAV we need Q before this movement:
        var qBefore = q - qtyIn + qtyOut;
        var a = cost.AverageCost;

        if (qtyIn > 0)
        {
            var newQ = qBefore + qtyIn;
            var newV = qBefore * a + qtyIn * unitCost;
            cost.AverageCost = newQ == 0 ? 0 : Math.Round(newV / newQ, 6, MidpointRounding.AwayFromZero);
            cost.LastCost = unitCost;
        }
        else if (qtyOut > 0)
        {
            var newQ = qBefore - qtyOut;
            cost.AverageCost = newQ == 0 ? 0 : a;
        }

        cost.LastDocumentId = documentId;
        cost.LastUpdatedAtUtc = utc;
        cost.ModifiedAtUtc = utc;
        cost.ModifiedBy = user;
    }

    private sealed class ConvResult
    {
        public bool Ok { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
        public decimal Rate { get; init; }
        public decimal QtyInBase { get; init; }
        public static ConvResult Fail(string code, string? msg = null) => new() { Ok = false, ErrorCode = code, ErrorMessage = msg };
        public static ConvResult Success(decimal rate, decimal qty) => new() { Ok = true, Rate = rate, QtyInBase = qty };
    }

    private static async Task<ConvResult> ResolveConversionAsync(
        AppDbContext db, int companyId, InventoryDocumentLine line, CancellationToken ct)
    {
        if (line.Qty <= 0)
            return ConvResult.Fail(InventoryErrorCodes.ZeroQtyNotAllowed);

        var variant = await db.ItemVariants.AsNoTracking().FirstOrDefaultAsync(v => v.Id == line.ItemVariantId && v.CompanyId == companyId, ct);
        if (variant is null) return ConvResult.Fail(InventoryErrorCodes.InvalidDocument, "Item variant not found.");
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == variant.ItemId && i.CompanyId == companyId, ct);
        if (item is null) return ConvResult.Fail(InventoryErrorCodes.InvalidDocument, "Item not found.");

        if (line.UOMId == item.BaseUOMId)
            return ConvResult.Success(1m, line.Qty);

        var conv = await db.UOMConversions.AsNoTracking().FirstOrDefaultAsync(c =>
            c.CompanyId == companyId && c.ItemId == item.Id &&
            c.FromUOMId == line.UOMId && c.ToUOMId == item.BaseUOMId, ct);
        if (conv is null)
            return ConvResult.Fail(InventoryErrorCodes.InvalidConversion);

        var qtyBase = Math.Round(line.Qty * conv.ConversionRate, 6, MidpointRounding.AwayFromZero);
        return ConvResult.Success(conv.ConversionRate, qtyBase);
    }

    private async Task<PostingResultDto?> ValidateForPostAsync(AppDbContext db, InventoryDocument doc, CancellationToken ct)
    {
        if (doc.Lines.Count == 0)
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "No lines.");

        foreach (var line in doc.Lines)
        {
            if (line.Qty <= 0)
                return PostingResultDto.Fail(InventoryErrorCodes.ZeroQtyNotAllowed);

            var itemCtx = await LotPostingHelper.LoadItemContextAsync(db, doc.CompanyId, line.ItemVariantId, ct);
            if (itemCtx is null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "Variant missing.");

            var allocInputs = line.LotSplits.Select(s => new LotAllocationInput
            {
                LotId = s.LotId ?? 0,
                LotNo = s.LotNo,
                Qty = s.Qty
            }).ToList();

            var lotErr = LotPostingHelper.ValidateLineLotFields(
                doc.DocType, itemCtx.IsBatchItem, line.LotNo, line.LotId,
                allocInputs.Count > 0 ? allocInputs : null);
            if (lotErr is not null) return lotErr;
        }

        if (doc.DocType == DocumentType.SA)
        {
            foreach (var line in doc.Lines)
            {
                if (line.ReasonCodeId is null)
                    return PostingResultDto.Fail(InventoryErrorCodes.ReasonCodeRequired);
                if (line.Direction is null)
                    return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "SA Direction required.");
            }
        }

        if (doc.DocType == DocumentType.ST)
        {
            if (doc.SourceWarehouseId is null || doc.DestinationWarehouseId is null ||
                doc.SourceLocationId is null || doc.DestinationLocationId is null)
                return PostingResultDto.Fail(InventoryErrorCodes.InvalidWarehouse);

            var src = await db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Id == doc.SourceWarehouseId, ct);
            var dst = await db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Id == doc.DestinationWarehouseId, ct);
            if (src is null || dst is null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidWarehouse);
            if (src.BranchId != dst.BranchId || src.BranchId != doc.BranchId)
                return PostingResultDto.Fail(InventoryErrorCodes.CrossBranchTransferNotAllowed);
        }
        else if (doc.WarehouseId is null)
        {
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidWarehouse);
        }

        var period = await db.InventoryPeriods.AsNoTracking()
            .Where(p => p.CompanyId == doc.CompanyId &&
                        p.StartDate <= doc.DocDate && p.EndDate >= doc.DocDate)
            .OrderByDescending(p => p.FiscalYear).ThenByDescending(p => p.FiscalMonth)
            .FirstOrDefaultAsync(ct);
        if (period is null)
            return PostingResultDto.Fail(InventoryErrorCodes.PeriodClosed, "No open inventory period for DocDate.");
        if (period.IsClosed)
            return PostingResultDto.Fail(InventoryErrorCodes.PeriodClosed);

        var todayKl = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, KlTimeZone).Date;
        if (doc.DocDate.Date > todayKl)
            return PostingResultDto.Fail(InventoryErrorCodes.BackdatedPostingNotAllowed, "Future DocDate not allowed.");

        return null;
    }

    private static async Task<PostingResultDto?> CheckBackdateAsync(
        AppDbContext db, InventoryDocument doc, long warehouseId, long itemVariantId, CancellationToken ct)
    {
        // Option A: block if any posted ledger for same Company+Branch+Warehouse+ItemVariant
        // has TransactionDate > proposed DocDate (no V1 cost rebuild).
        var later = await db.StockLedgers.AsNoTracking().AnyAsync(l =>
            l.CompanyId == doc.CompanyId &&
            l.BranchId == doc.BranchId &&
            l.WarehouseId == warehouseId &&
            l.ItemVariantId == itemVariantId &&
            l.TransactionDate > doc.DocDate.Date, ct);

        return later ? PostingResultDto.Fail(InventoryErrorCodes.BackdatedPostingNotAllowed) : null;
    }

    private async Task<PostingResultDto?> ValidateCreateAsync(
        AppDbContext db, int companyId, long branchId, CreateDocumentDto dto, CancellationToken ct)
    {
        foreach (var line in dto.Lines)
        {
            if (line.Qty <= 0 && (line.LotAllocations is null || line.LotAllocations.Count == 0))
                return PostingResultDto.Fail(InventoryErrorCodes.ZeroQtyNotAllowed);

            var itemCtx = await LotPostingHelper.LoadItemContextAsync(db, companyId, line.ItemVariantId, ct);
            if (itemCtx is null)
                return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "Invalid item variant.");

            var lotErr = LotPostingHelper.ValidateLineLotFields(
                dto.DocType, itemCtx.IsBatchItem, line.LotNo, line.LotId, line.LotAllocations?.ToList());
            if (lotErr is not null) return lotErr;

            if (line.LotAllocations is { Count: > 0 })
            {
                var sum = line.LotAllocations.Sum(a => a.Qty);
                if (line.Qty > 0 && sum != line.Qty)
                    return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "Lot allocation qty must equal line qty.");
            }

            if (!await db.UOMs.AnyAsync(u => u.Id == line.UOMId && u.CompanyId == companyId, ct))
                return PostingResultDto.Fail(InventoryErrorCodes.InvalidUOM);
        }

        if (dto.DocType == DocumentType.ST)
        {
            if (dto.SourceWarehouseId is null || dto.DestinationWarehouseId is null)
                return PostingResultDto.Fail(InventoryErrorCodes.InvalidWarehouse);
            var src = await db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Id == dto.SourceWarehouseId && w.CompanyId == companyId, ct);
            var dst = await db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Id == dto.DestinationWarehouseId && w.CompanyId == companyId, ct);
            if (src is null || dst is null) return PostingResultDto.Fail(InventoryErrorCodes.InvalidWarehouse);
            if (src.BranchId != branchId || dst.BranchId != branchId || src.BranchId != dst.BranchId)
                return PostingResultDto.Fail(InventoryErrorCodes.CrossBranchTransferNotAllowed);
        }
        else if (dto.WarehouseId is null ||
                 !await db.Warehouses.AnyAsync(w => w.Id == dto.WarehouseId && w.CompanyId == companyId && w.BranchId == branchId, ct))
        {
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidWarehouse);
        }

        return null;
    }

    private sealed class LineMeta
    {
        public string Sku { get; init; } = "";
        public string ItemDescription { get; init; } = "";
        public string UomCode { get; init; } = "";
        public string WarehouseCode { get; init; } = "";
        public string LocationCode { get; init; } = "";
        public string? ReasonCode { get; init; }
    }

    private static async Task<LineMeta> LoadLineMetaAsync(
        AppDbContext db, int companyId, InventoryDocumentLine line, Movement move, CancellationToken ct)
    {
        var variant = await db.ItemVariants.AsNoTracking().FirstAsync(v => v.Id == line.ItemVariantId, ct);
        var item = await db.Items.AsNoTracking().FirstAsync(i => i.Id == variant.ItemId, ct);
        var uom = await db.UOMs.AsNoTracking().FirstAsync(u => u.Id == line.UOMId, ct);
        var wh = await db.Warehouses.AsNoTracking().FirstAsync(w => w.Id == move.WarehouseId, ct);
        var loc = await db.WarehouseLocations.AsNoTracking().FirstAsync(l => l.Id == move.LocationId, ct);
        string? reason = null;
        if (line.ReasonCodeId is long rid)
        {
            reason = (await db.ReasonCodes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid, ct))?.ReasonCodeValue;
        }

        return new LineMeta
        {
            Sku = variant.SKU,
            ItemDescription = item.ItemDescription,
            UomCode = uom.UOMCode,
            WarehouseCode = wh.WarehouseCode,
            LocationCode = loc.LocationCode,
            ReasonCode = reason
        };
    }

    private async Task<InventoryDocument?> LoadDocumentAsync(AppDbContext db, long id, CancellationToken ct) =>
        await db.InventoryDocuments
            .Include(d => d.Lines)
            .ThenInclude(l => l.LotSplits)
            .FirstOrDefaultAsync(d => d.Id == id && d.CompanyId == _companyContext.CompanyId && d.BranchId == _companyContext.BranchId, ct);

    private sealed record LotLockKey(long LotId, long WarehouseId, long LocationId);

    private static List<LotLockKey> BuildLotLockKeys(InventoryDocument doc)
    {
        var keys = new List<LotLockKey>();
        foreach (var line in doc.Lines)
        {
            var lots = new List<(long LotId, long Loc)>();
            if (line.LotSplits.Count > 0)
            {
                foreach (var s in line.LotSplits.Where(s => s.LotId is not null))
                    lots.Add((s.LotId!.Value, line.LocationId));
            }
            else if (line.LotId is long lid)
            {
                lots.Add((lid, line.LocationId));
            }

            foreach (var (lotId, loc) in lots)
            {
                if (doc.DocType == DocumentType.ST)
                {
                    keys.Add(new LotLockKey(lotId, doc.SourceWarehouseId!.Value, loc));
                    keys.Add(new LotLockKey(lotId, doc.DestinationWarehouseId!.Value, doc.DestinationLocationId!.Value));
                }
                else
                {
                    keys.Add(new LotLockKey(lotId, doc.WarehouseId!.Value, loc));
                }
            }
        }

        return keys.Distinct().ToList();
    }

    private async Task<PostingResultDto?> ResolveLotsForPostAsync(
        AppDbContext db, InventoryDocument doc, string? user, CancellationToken ct)
    {
        foreach (var line in doc.Lines)
        {
            var itemCtx = await LotPostingHelper.LoadItemContextAsync(db, doc.CompanyId, line.ItemVariantId, ct);
            if (itemCtx is null || !itemCtx.IsBatchItem) continue;

            if (line.LotSplits.Count > 0)
            {
                foreach (var split in line.LotSplits)
                {
                    var resolved = await ResolveSplitLotAsync(db, doc, line, split, itemCtx, user, ct);
                    if (resolved is not null) return resolved;
                }
                continue;
            }

            var isInbound = doc.DocType is DocumentType.OB or DocumentType.GRN
                            || (doc.DocType == DocumentType.SA && line.Direction == AdjustmentDirection.Increase)
                            || (doc.DocType == DocumentType.GI && doc.ReversalOfDocumentId is not null);

            if (isInbound && doc.ReversalOfDocumentId is null)
            {
                if (line.LotId is null)
                {
                    if (string.IsNullOrWhiteSpace(line.LotNo))
                        return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "Batch inbound requires LotNo.");
                    var lot = await LotPostingHelper.ResolveOrCreateLotAsync(
                        db, doc.CompanyId, line.ItemVariantId, line.LotNo, doc, line,
                        line.QtyInBase, line.UnitCost, user, ct);
                    line.LotId = lot.Id;
                    line.LotNo = lot.LotNo;
                }
                else
                {
                    var lot = await LotPostingHelper.FindLotAsync(db, doc.CompanyId, line.ItemVariantId, line.LotId, null, ct);
                    if (lot is null)
                        return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "Lot not found.");
                    line.LotNo = lot.LotNo;
                }
            }
            else
            {
                var lot = await LotPostingHelper.FindLotAsync(db, doc.CompanyId, line.ItemVariantId, line.LotId, line.LotNo, ct);
                if (lot is null)
                    return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "Lot not found for outbound/transfer.");
                line.LotId = lot.Id;
                line.LotNo = lot.LotNo;
            }
        }

        await db.SaveChangesAsync(ct);
        return null;
    }

    private async Task<PostingResultDto?> ResolveSplitLotAsync(
        AppDbContext db,
        InventoryDocument doc,
        InventoryDocumentLine line,
        InventoryDocumentLineLotSplit split,
        LotPostingHelper.ItemLotContext itemCtx,
        string? user,
        CancellationToken ct)
    {
        var isInbound = doc.DocType is DocumentType.OB or DocumentType.GRN
                        || (doc.DocType == DocumentType.SA && line.Direction == AdjustmentDirection.Increase);

        if (isInbound && doc.ReversalOfDocumentId is null && split.LotId is null)
        {
            if (string.IsNullOrWhiteSpace(split.LotNo))
                return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "Split requires LotNo.");
            var qtyBase = Math.Round(split.Qty * line.ConversionRateUsed, 6, MidpointRounding.AwayFromZero);
            var lot = await LotPostingHelper.ResolveOrCreateLotAsync(
                db, doc.CompanyId, line.ItemVariantId, split.LotNo, doc, line, qtyBase, line.UnitCost, user, ct);
            split.LotId = lot.Id;
            split.LotNo = lot.LotNo;
            return null;
        }

        var found = await LotPostingHelper.FindLotAsync(db, doc.CompanyId, line.ItemVariantId, split.LotId, split.LotNo, ct);
        if (found is null)
            return PostingResultDto.Fail(InventoryErrorCodes.InvalidDocument, "Lot split not found.");
        split.LotId = found.Id;
        split.LotNo = found.LotNo;
        return null;
    }

    private async Task<DocumentDto?> MapDocumentAsync(AppDbContext db, long id, bool includeCost, CancellationToken ct)
    {
        var doc = await db.InventoryDocuments.AsNoTracking().Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return null;

        var canView = includeCost && await CanViewCostAsync(ct);
        return new DocumentDto
        {
            Id = doc.Id,
            DocNo = doc.DocNo,
            DocType = doc.DocType,
            DocDate = doc.DocDate,
            Status = doc.Status,
            WarehouseId = doc.WarehouseId,
            SourceWarehouseId = doc.SourceWarehouseId,
            DestinationWarehouseId = doc.DestinationWarehouseId,
            SourceLocationId = doc.SourceLocationId,
            DestinationLocationId = doc.DestinationLocationId,
            ReferenceNo = doc.ReferenceNo,
            Remarks = doc.Remarks,
            AllowZeroCost = doc.AllowZeroCost,
            PostedBy = doc.PostedBy,
            PostedAtUtc = doc.PostedAtUtc,
            ReversalOfDocumentId = doc.ReversalOfDocumentId,
            StockTakeId = doc.StockTakeId,
            Lines = doc.Lines.OrderBy(l => l.LineNo).Select(l => new DocumentLineDto
            {
                Id = l.Id,
                LineNo = l.LineNo,
                ItemVariantId = l.ItemVariantId,
                UOMId = l.UOMId,
                Qty = l.Qty,
                ConversionRateUsed = l.ConversionRateUsed,
                QtyInBase = l.QtyInBase,
                UnitCost = canView ? l.UnitCost : null,
                TotalCost = canView ? l.TotalCost : null,
                LocationId = l.LocationId,
                LotNo = l.LotNo,
                LotId = l.LotId,
                Direction = l.Direction,
                ReasonCodeId = l.ReasonCodeId,
                Remarks = l.Remarks
            }).ToList()
        };
    }

    private async Task<bool> CanViewCostAsync(CancellationToken ct)
    {
        if (string.Equals(_currentUser.UserLevel, "SYSTEM_ADMIN", StringComparison.OrdinalIgnoreCase))
            return true;
        return await _accessRights.CanAsync(MenuCodes.Inventory, PermissionCodes.ViewCost, ct);
    }

    private async Task<string?> GateAsync(string menu, string permission, CancellationToken ct)
    {
        var resolve = await InventoryServiceHelper.ResolveCompanyAsync(_companyContext, ct);
        if (!resolve.Ok) return resolve.Error;
        var access = await InventoryServiceHelper.EnsureAccessAsync(_accessRights, menu, permission, ct);
        return access.Ok ? null : access.Error;
    }

    private static string MenuFor(DocumentType type) => type switch
    {
        DocumentType.OB => MenuCodes.InvOb,
        DocumentType.GRN => MenuCodes.InvGrn,
        DocumentType.GI => MenuCodes.InvGi,
        DocumentType.ST => MenuCodes.InvSt,
        DocumentType.SA => MenuCodes.InvSa,
        _ => MenuCodes.Inventory
    };

    private static bool IsDeadlock(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is SqlException sql && sql.Number == 1205) return true;
            if (e.Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static TimeZoneInfo ResolveKlTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuala_Lumpur"); }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Singapore Standard Time");
        }
    }
}
