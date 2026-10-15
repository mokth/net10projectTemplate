using ErpWeb.Core.Inventory;
using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ErpWeb.Tests.Other;
[Trait(TestCategories.Name, TestCategories.Shared)]
public sealed class StockLedgerInfrastructureTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly IDbContextFactory<AppDbContext> _factory;

    public StockLedgerInfrastructureTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new LocalFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public void Model_contains_tenant_keys_v2_indexes_and_conditional_fields()
    {
        var sqlOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(local);Database=StockLedgerModelOnly;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var db = new AppDbContext(sqlOptions);
        var posting = db.Model.FindEntityType(typeof(StockPosting))!;
        Assert.Contains(posting.GetKeys(), key =>
            key.Properties.Select(x => x.Name).SequenceEqual(["CompanyCode", "BranchCode", "Id"]));
        Assert.Contains(posting.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(x => x.Name)
                .SequenceEqual(["CompanyCode", "BranchCode", "CommandType", "RequestId"]));

        var productionMovement = db.Model.FindEntityType(typeof(ProductionBalLotMovement))!;
        Assert.Contains(productionMovement.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(x => x.Name)
                .SequenceEqual(["StockPostingId", "PostingLineNo"]));
        Assert.NotNull(db.Model.FindEntityType(typeof(ProductionMovementAllocation)));
        var valuation = db.Model.FindEntityType(typeof(StockValuationFact))!;
        Assert.Contains(valuation.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(x => x.Name)
                .SequenceEqual(["StockPostingId", "PostingLineNo", "SplitOrdinal"]));
        Assert.Equal(typeof(int?), valuation.FindProperty(nameof(StockValuationFact.InventoryHistoryId))!.ClrType);
        Assert.Equal(typeof(int?), valuation.FindProperty(nameof(StockValuationFact.FromBalLocId))!.ClrType);
        Assert.Equal(typeof(int?), valuation.FindProperty(nameof(StockValuationFact.LotId))!.ClrType);
        Assert.Equal(typeof(long), valuation.FindProperty(nameof(StockValuationFact.StockPostingId))!.ClrType);
        Assert.NotNull(db.Model.FindEntityType(typeof(StockCostState)));
        Assert.NotNull(db.Model.FindEntityType(typeof(StockValuationPeriodSnapshotHdr)));
        Assert.NotNull(db.Model.FindEntityType(typeof(StockCountCostBasis)));
        Assert.NotNull(db.Model.FindEntityType(typeof(StockCountReplayGeneration)));
        Assert.NotNull(db.Model.FindEntityType(typeof(StockReplayRun)));
        Assert.NotNull(db.Model.FindEntityType(typeof(StockReplayEffect)));
        Assert.NotNull(db.Model.FindEntityType(typeof(ProductionCostRestatementFact)));

        var generation = db.Model.FindEntityType(typeof(StockCountReplayGeneration))!;
        Assert.Contains(generation.GetIndexes(), index =>
            index.Properties.Select(x => x.Name).SequenceEqual(
                ["CompanyCode", "BranchCode", "EffectiveAt", "AnchorPostingSequence", "StockCountLineId"]));

        foreach (var type in new[]
                 {
                     typeof(StockPosting), typeof(IvTrxHistory),
                     typeof(ProductionBalLotMovement), typeof(ProductionMaterialMovement),
                     typeof(ProductionMovementAllocation), typeof(IvBalLoc), typeof(ProductionBalLot)
                 })
        {
            var entityType = db.Model.FindEntityType(type)
                ?? throw new InvalidOperationException($"{type.Name} is not mapped.");
            Assert.False(entityType.IsSqlOutputClauseUsed(), $"{type.Name} must not use SQL Server OUTPUT.");
        }
    }

    [Fact]
    public void Fingerprint_is_stable_across_json_property_order()
    {
        var left = StockPostingFingerprint.Canonicalize("""{"qty":2,"item":"RM1"}""");
        var right = StockPostingFingerprint.Canonicalize("""{"item":"RM1","qty":2}""");
        Assert.Equal(left, right);
        Assert.Equal(StockPostingFingerprint.Hash(left), StockPostingFingerprint.Hash(right));
    }

    [Fact]
    public async Task Count_anchor_command_requires_cost_basis_and_hashes_frozen_observation()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var date = new DateTime(2026, 10, 10);
        db.StockCostPolicyRevisions.Add(new StockCostPolicyRevision
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            CostMethod = StockCostMethods.MovingAverage,
            EffectiveFrom = new DateTime(2026, 10, 1),
            Status = StockCostPolicyStatuses.Active,
            ApprovedBy = "TEST", ApprovedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
        });
        var header = new IvStockCountHdr
        {
            CompanyCode = "DEMO", BranchCode = "HQ", CountNo = "CC-ANCHOR",
            CountDate = date, Status = IvStockCountStatuses.Counted
        };
        var positive = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = 7,
            ICode = "RM1", IStatus = "ACTIVE", PhysicalQty = 4m
        };
        var zero = new IvStockCountLine
        {
            Header = header, LineNumber = 2, BalLocId = 8,
            ICode = "RM2", IStatus = "ACTIVE", PhysicalQty = 0m
        };
        db.IvStockCountLines.AddRange(positive, zero);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var missing = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockCountAnchorCommandFactory.CreateAsync(db, "DEMO", "HQ", header.Id, []));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, missing.Error.Code);
        var basis = new StockCountCostBasis
        {
            CompanyCode = "DEMO", BranchCode = "HQ", StockCountLineId = positive.Id,
            FrozenFallbackUnitCost = 1.234567m,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = "Approved physical count", CostApprovedBy = "manager",
            CostApprovedAtUtc = DateTime.UtcNow
        };
        var first = await StockCountAnchorCommandFactory.CreateAsync(
            db, "DEMO", "HQ", header.Id, [basis]);
        var again = await StockCountAnchorCommandFactory.CreateAsync(
            db, "DEMO", "HQ", header.Id, [basis]);
        Assert.Equal(first.RequestId, again.RequestId);
        Assert.Equal(first.Evidence.RequestFingerprint, again.Evidence.RequestFingerprint);
        Assert.Equal(0, first.DocumentRevision);
        Assert.Equal("IV_STOCK_COUNT", first.SourceDocumentType);
        Assert.Equal(StockReplayEventKey.CountEndOfDay(date), first.EffectiveAt);

        positive = await db.IvStockCountLines.SingleAsync(l => l.Id == positive.Id);
        positive.PhysicalQty = 5m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var changed = await StockCountAnchorCommandFactory.CreateAsync(
            db, "DEMO", "HQ", header.Id, [basis]);
        Assert.Equal(first.RequestId, changed.RequestId);
        Assert.NotEqual(first.Evidence.RequestFingerprint, changed.Evidence.RequestFingerprint);
    }

    [Fact]
    public async Task Zero_adjustment_count_seals_anchor_and_initial_generation_without_batch()
    {
        await ActivateAsync();
        await using var db = await _factory.CreateDbContextAsync();
        // SQL Server decimal CHECK predicates are not portable to SQLite's
        // text-backed decimal provider in this model-only integration test.
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        db.IvStockMasters.Add(new IvStockMaster { CompanyCode = "DEMO", ICode = "RM0" });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN"
        });
        var balance = new IvBalLoc
        {
            CompanyCode = "DEMO", BranchCode = "HQ", ICode = "RM0",
            WhCode = "MAIN", LocCode = "", LotNo = "", IStatus = "ACTIVE",
            StdQty = 0m
        };
        db.IvBalLocs.Add(balance);
        await db.SaveChangesAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = "DEMO", BranchCode = "HQ", CountNo = "CC-ZERO",
            CountDate = new DateTime(2026, 10, 10), Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = balance.Id,
            ICode = "RM0", IStatus = "ACTIVE", PhysicalQty = 0m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, "DEMO", "HQ", header.Id);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, "DEMO", "HQ", header.Id, []);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = CreateCoordinator();
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var context = begin.Context!;
        try
        {
            var permit = await StockReplayPhysicalPlanner.PrepareCountSheetWithinRootAsync(
                context, sheet);
            var trackedHeader = await db.IvStockCountHdrs.SingleAsync(h => h.Id == header.Id);
            trackedHeader.Status = IvStockCountStatuses.Posted;
            trackedHeader.PostedBatchNo = 799;
            await db.SaveChangesAsync();
            var invalidZeroBatch = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockCountInitialGenerationWriter.AppendAsync(context, permit, 799));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                invalidZeroBatch.Error.Code);
            trackedHeader.PostedBatchNo = null;
            await db.SaveChangesAsync();
            var generations = await StockCountInitialGenerationWriter.AppendAsync(
                context, permit, adjustmentBatchNo: null);
            var generation = Assert.Single(generations);
            Assert.Equal(0m, generation.SignedAdjustmentQty);
            Assert.Null(generation.AdjustmentBatchNo);
            Assert.Null(generation.AdjustmentHistoryId);
            var duplicate = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockCountInitialGenerationWriter.AppendAsync(context, permit, null));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, duplicate.Error.Code);
            await coordinator.CompleteInTransactionAsync(context);
            await transaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(context);
            await transaction.RollbackAsync();
            throw;
        }

        db.ChangeTracker.Clear();
        var active = await StockCountGenerationResolver.ResolveActiveAsync(
            db, "DEMO", "HQ", [line.Id]);
        Assert.False(active[line.Id].IsRemoval);
        Assert.Equal(0m, active[line.Id].SignedAdjustmentQty);
        Assert.Equal(StockReplayEventKey.CountEndOfDay(header.CountDate),
            active[line.Id].EffectiveAt);
        Assert.Single(await db.StockPostings.Where(p => p.SealedAtUtc != null).ToListAsync());
        Assert.Empty(await db.IvTrxHistories.ToListAsync());
    }

    [Fact]
    public async Task Count_generation_reader_validates_anchor_and_adjustment_history()
    {
        // SQL Server stores decimal quantities numerically; the SQLite test model stores
        // them as text, so its copied SQL Server decimal CHECK expression is not portable.
        await using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA ignore_check_constraints = ON";
            await pragma.ExecuteNonQueryAsync();
        }
        await ActivateAsync();
        await using var db = await _factory.CreateDbContextAsync();
        var epochId = await db.StockLedgerEpochs.Select(x => x.Id).SingleAsync();
        var countDate = new DateTime(2026, 10, 10);
        var header = new IvStockCountHdr
        {
            CompanyCode = "DEMO", BranchCode = "HQ", CountNo = "CC000001",
            CountDate = countDate, Status = "POSTED"
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = 7,
            ICode = "RM1", IStatus = "ACTIVE", PhysicalQty = 4m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();

        var posting = Posting("DEMO", "HQ", epochId, 1, Guid.NewGuid());
        posting.SourceModule = "INVENTORY";
        posting.SourceDocumentType = "IV_STOCK_COUNT";
        posting.SourceDocumentId = header.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        posting.SourceDocumentNo = header.CountNo;
        posting.EffectiveAt = StockReplayEventKey.CountEndOfDay(countDate);
        posting.BusinessDate = countDate;
        db.StockPostings.Add(posting);
        await db.SaveChangesAsync();

        db.StockCountReplayGenerations.Add(new StockCountReplayGeneration
        {
            CompanyCode = "DEMO", BranchCode = "HQ", StockCountLineId = line.Id,
            AnchorPostingId = posting.Id, WritePostingId = posting.Id,
            AnchorPostingSequence = posting.PostingSequence,
            EffectiveAt = posting.EffectiveAt,
            SignedAdjustmentQty = 0m, CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var active = await StockCountGenerationResolver.ResolveActiveAsync(
            db, "DEMO", "HQ", [line.Id]);
        Assert.Single(active);
        Assert.Equal(0m, active[line.Id].SignedAdjustmentQty);

        db.IvStockMasters.Add(new IvStockMaster { CompanyCode = "DEMO", ICode = "RM2" });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN"
        });
        var balance = new IvBalLoc
        {
            CompanyCode = "DEMO", BranchCode = "HQ", ICode = "RM2", WhCode = "MAIN",
            LocCode = "", LotNo = "", IStatus = "ACTIVE", StdQty = 4m
        };
        db.IvBalLocs.Add(balance);
        var adjustmentLine = new IvStockCountLine
        {
            StockCountId = header.Id, LineNumber = 2, BalLocId = 8,
            ICode = "RM2", IStatus = "ACTIVE", PhysicalQty = 4m
        };
        db.IvStockCountLines.Add(adjustmentLine);
        await db.SaveChangesAsync();
        adjustmentLine.BalLocId = balance.Id;
        var history = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 41, TrxLineNo = 1,
            TrxDtTime = countDate, TrxType = "ADJ", BatchStatus = "POSTED",
            RefNo = header.CountNo,
            ICode = "RM2", ToBalLocId = balance.Id, ToStdQty = 4m,
            LedgerVersion = 2, LedgerEpochId = epochId, StockPostingId = posting.Id,
            PostingLineNo = 1, DocumentRevision = 1, EntryRole = "ORIGINAL"
        };
        db.IvTrxHistories.Add(history);
        await db.SaveChangesAsync();
        db.StockCountReplayGenerations.Add(new StockCountReplayGeneration
        {
            CompanyCode = "DEMO", BranchCode = "HQ", StockCountLineId = adjustmentLine.Id,
            AnchorPostingId = posting.Id, WritePostingId = posting.Id,
            AnchorPostingSequence = posting.PostingSequence, EffectiveAt = posting.EffectiveAt,
            SignedAdjustmentQty = 4m, AdjustmentBatchNo = 41,
            AdjustmentHistoryId = history.Id, CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        active = await StockCountGenerationResolver.ResolveActiveAsync(
            db, "DEMO", "HQ", [line.Id, adjustmentLine.Id]);
        Assert.Equal(2, active.Count);
        Assert.Equal(4m, active[adjustmentLine.Id].SignedAdjustmentQty);

        var ordinaryPosting = Posting("DEMO", "HQ", epochId, 2, Guid.NewGuid());
        ordinaryPosting.SourceModule = "INVENTORY";
        ordinaryPosting.SourceDocumentType = "MR";
        ordinaryPosting.SourceDocumentId = "101";
        ordinaryPosting.SourceDocumentNo = "MR101";
        ordinaryPosting.EffectiveAt = posting.EffectiveAt;
        ordinaryPosting.BusinessDate = countDate;
        db.StockPostings.Add(ordinaryPosting);
        await db.SaveChangesAsync();
        var ordinaryHistory = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 42, TrxLineNo = 1,
            TrxDtTime = ordinaryPosting.EffectiveAt, TrxType = "MR", BatchStatus = "POSTED",
            ICode = "RM2", ToBalLocId = balance.Id, ToStdQty = 1m,
            LedgerVersion = 2, LedgerEpochId = epochId, StockPostingId = ordinaryPosting.Id,
            PostingLineNo = 1, DocumentRevision = 1, EntryRole = "ORIGINAL"
        };
        db.IvTrxHistories.Add(ordinaryHistory);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var earlier = new StockReplayEventKey(countDate.AddDays(-1),
            StockReplayEventKey.OrdinaryClass, 1, "candidate", 0);
        var tail = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, "DEMO", "HQ", balance.Id, earlier);
        Assert.Equal(2, tail.Count);
        Assert.Equal(StockReplayEventKey.OrdinaryClass, tail[0].Key.ClassPriority);
        Assert.Equal(StockReplayEventKey.CountClass, tail[1].Key.ClassPriority);
        Assert.Equal(4m, tail[1].AbsoluteCountQty);
        Assert.Equal(-active[adjustmentLine.Id].Id, tail[1].EventId);

        var rollbackPosting = Posting("DEMO", "HQ", epochId, 3, Guid.NewGuid());
        rollbackPosting.SourceModule = "INVENTORY";
        rollbackPosting.SourceDocumentType = "MR_ROLLBACK";
        rollbackPosting.SourceDocumentId = "101";
        rollbackPosting.SourceDocumentNo = "MR101";
        rollbackPosting.EffectiveAt = countDate.AddDays(1);
        db.StockPostings.Add(rollbackPosting);
        await db.SaveChangesAsync();
        db.IvTrxHistories.Add(new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 42, TrxLineNo = 1,
            TrxDtTime = rollbackPosting.EffectiveAt, TrxType = "MR", BatchStatus = "POSTED",
            ICode = "RM2", FromBalLocId = balance.Id, FrStdQty = 1m,
            LedgerVersion = 2, LedgerEpochId = epochId, StockPostingId = rollbackPosting.Id,
            PostingLineNo = 1, DocumentRevision = 2, EntryRole = "REVERSAL",
            ReversesHistoryId = ordinaryHistory.Id
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        tail = await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, "DEMO", "HQ", balance.Id, earlier);
        Assert.Single(tail);
        Assert.Equal(StockReplayEventKey.CountClass, tail[0].Key.ClassPriority);

        db.StockPostingBranchSequences.Add(new StockPostingBranchSequence
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LastSequence = 3,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var candidate = new StockReplayPhysicalEvent(10_000,
            new StockReplayEventKey(countDate.AddDays(-1).AddHours(12),
                StockReplayEventKey.OrdinaryClass, 4, "candidate", 0), 2m);
        var plan = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            db, "DEMO", "HQ", balance.Id, candidate);
        Assert.Equal(3, plan.BranchWatermark);
        Assert.Equal(0m, plan.Projection.HistoricalQuantityBeforeCandidate);
        Assert.Equal(4m, plan.Projection.FinalQuantity);
        Assert.Equal(64, plan.PlanHash.Length);
        var wrongSequence = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayPhysicalPlanner.BuildInsertionAsync(db, "DEMO", "HQ", balance.Id,
                candidate with { Key = candidate.Key with { OriginalPostingSequence = 2 } }));
        Assert.Equal(StockLedgerErrorCodes.StaleDocument, wrongSequence.Error.Code);

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var coordinator = CreateCoordinator();
            var begin = await coordinator.BeginInTransactionAsync(db,
                Command(Guid.NewGuid()) with { EffectiveAt = candidate.Key.EffectiveAt });
            Assert.NotNull(begin.Context);
            await StockReplayPhysicalPlanner.ValidateWithinRootAsync(begin.Context!, plan);
            var correctionPermit = await StockReplayPhysicalPlanner
                .PrepareCountCorrectionsWithinRootAsync(begin.Context!, plan);
            var correction = Assert.Single(correctionPermit.Lines);
            Assert.Equal(adjustmentLine.Id, correction.LineId);
            Assert.Equal(active[adjustmentLine.Id].Id, correction.PreviousGenerationId);
            Assert.Equal(posting.Id, correction.AnchorPostingId);
            Assert.Equal(posting.PostingSequence, correction.AnchorPostingSequence);
            Assert.Equal(4m, correction.PhysicalQty);
            Assert.Equal(4m, correction.OldSignedAdjustmentQty);
            Assert.Equal(2m, correction.NewSignedAdjustmentQty);
            Assert.Equal(balance.Id, correctionPermit.BalLocId);
            Assert.Equal(4m, correctionPermit.FinalQuantity);
            var missingHistories = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockCountCorrectionGenerationWriter.AppendAsync(begin.Context!,
                    correctionPermit, []));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                missingHistories.Error.Code);
            balance = await db.IvBalLocs.SingleAsync(x => x.Id == balance.Id);
            balance.StdQty = 5m;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var stale = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayPhysicalPlanner.ValidateWithinRootAsync(begin.Context!, plan));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument, stale.Error.Code);
            Assert.True(stale.Error.Retryable);
            await coordinator.AbortInTransactionAsync(begin.Context!);
            await transaction.RollbackAsync();
        }

        db.ChangeTracker.Clear();
        await using (var correctionTransaction = await db.Database.BeginTransactionAsync())
        {
            var coordinator = CreateCoordinator();
            var begin = await coordinator.BeginInTransactionAsync(db,
                Command(Guid.NewGuid()) with { EffectiveAt = candidate.Key.EffectiveAt });
            Assert.NotNull(begin.Context);
            var context = begin.Context!;
            try
            {
                var permit = await StockReplayPhysicalPlanner
                    .PrepareCountCorrectionsWithinRootAsync(context, plan);
                var basis = new StockCountCostBasis
                {
                    CompanyCode = "DEMO", BranchCode = "HQ",
                    StockCountLineId = adjustmentLine.Id,
                    FrozenFallbackUnitCost = 2m,
                    CostBasisKind = StockCountCostBasisKinds.ManualApproved,
                    CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
                    CostOverrideReason = "Approved count cost",
                    CostApprovedBy = "TEST", CostApprovedAtUtc = DateTime.UtcNow,
                    CreatedAtUtc = DateTime.UtcNow
                };
                db.StockCountCostBases.Add(basis);
                db.IvTrxBatches.AddRange(
                    new IvTrxBatch
                    {
                        CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 960,
                        TrxDtTime = countDate, TrxType = "ADJ",
                        BatchStatus = "POSTED", RefNo = header.CountNo
                    },
                    new IvTrxBatch
                    {
                        CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 961,
                        TrxDtTime = countDate, TrxType = "ADJ",
                        BatchStatus = "POSTED", RefNo = header.CountNo
                    });
                await db.SaveChangesAsync();
                var correctionBatches = db.IvTrxBatches.Local
                    .Where(b => b.BatchNo is 960 or 961)
                    .ToDictionary(b => b.BatchNo);
                db.IvTrxBatchDetails.Add(new IvTrxBatchDetail
                {
                    BatchId = correctionBatches[960].Id,
                    CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 960,
                    TrxLineNo = 1, DocumentRevision = context.Posting.DocumentRevision,
                    TrxType = "ADJ", ICode = "RM2",
                    FromBalLocId = balance.Id, FrStdQty = 4m
                });
                var replacementDetail = new IvTrxBatchDetail
                {
                    BatchId = correctionBatches[961].Id,
                    CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 961,
                    TrxLineNo = 1, DocumentRevision = context.Posting.DocumentRevision,
                    TrxType = "ADJ", ICode = "RM2",
                    ToBalLocId = balance.Id, ToStdQty = 2m,
                    StockCountCostBasisId = basis.Id
                };
                var candidateHistory = new IvTrxHistory
                {
                    CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 959,
                    TrxLineNo = 1, TrxDtTime = candidate.Key.EffectiveAt,
                    TrxType = "MR", BatchStatus = "POSTED", ICode = "RM2",
                    ToBalLocId = balance.Id, ToStdQty = 2m, LedgerVersion = 2,
                    LedgerEpochId = epochId, StockPostingId = context.Posting.Id,
                    PostingLineNo = 1, DocumentRevision = context.Posting.DocumentRevision,
                    EntryRole = "ORIGINAL"
                };
                var reversal = Assert.Single(new IvInventoryHistoryWriter()
                    .AppendCountReversal(context, [history],
                        context.Posting.DocumentRevision, countDate, 960, 2));
                Assert.Equal(960, reversal.BatchNo);
                Assert.Equal(2, reversal.PostingLineNo);
                var replacement = new IvTrxHistory
                {
                    CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 961,
                    TrxLineNo = 1, TrxDtTime = countDate,
                    TrxType = "ADJ", BatchStatus = "POSTED", RefNo = header.CountNo,
                    ICode = "RM2", ToBalLocId = balance.Id, ToStdQty = 2m,
                    LedgerVersion = 2, LedgerEpochId = epochId,
                    StockPostingId = context.Posting.Id, PostingLineNo = 3,
                    DocumentRevision = context.Posting.DocumentRevision,
                    EntryRole = "ORIGINAL", StockCountCostBasisId = basis.Id
                };
                db.IvTrxHistories.AddRange(candidateHistory, replacement);
                await db.SaveChangesAsync();
                var correctionEvidence = new StockCountCorrectionHistories(
                    adjustmentLine.Id, reversal.Id, replacement.Id);
                var missingDetail = await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockCountCorrectionGenerationWriter.AppendAsync(
                        context, permit, [correctionEvidence]));
                Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                    missingDetail.Error.Code);
                db.IvTrxBatchDetails.Add(replacementDetail);
                await db.SaveChangesAsync();
                var generations = await StockCountCorrectionGenerationWriter.AppendAsync(
                    context, permit, [correctionEvidence]);
                var generation = Assert.Single(generations);
                Assert.Equal(active[adjustmentLine.Id].Id, generation.PreviousGenerationId);
                Assert.Equal(posting.Id, generation.AnchorPostingId);
                Assert.Equal(context.Posting.Id, generation.WritePostingId);
                Assert.Equal(2m, generation.SignedAdjustmentQty);
                Assert.Equal(reversal.Id, generation.ReversalHistoryId);
                Assert.Equal(replacement.Id, generation.AdjustmentHistoryId);
                Assert.Equal(posting.EffectiveAt, generation.EffectiveAt);
            }
            finally
            {
                await coordinator.AbortInTransactionAsync(context);
                await correctionTransaction.RollbackAsync();
            }
        }

        db.ChangeTracker.Clear();
        var countEvent = tail.Single();
        var removal = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            db, "DEMO", "HQ", balance.Id, countEvent);
        Assert.Equal(0m, removal.Projection.HistoricalQuantityBeforeRemovedEvent);
        Assert.Equal(0m, removal.Projection.FinalQuantity);
        var invented = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayPhysicalPlanner.BuildRemovalAsync(db, "DEMO", "HQ",
                balance.Id, countEvent with { SignedDelta = 3m }));
        Assert.Equal(StockLedgerErrorCodes.StaleDocument, invented.Error.Code);
        await using (var removalTransaction = await db.Database.BeginTransactionAsync())
        {
            var coordinator = CreateCoordinator();
            var begin = await coordinator.BeginInTransactionAsync(db,
                Command(Guid.NewGuid()) with { EffectiveAt = countEvent.Key.EffectiveAt });
            Assert.NotNull(begin.Context);
            await StockReplayPhysicalPlanner.ValidateWithinRootAsync(begin.Context!, removal);
            await coordinator.AbortInTransactionAsync(begin.Context!);
            await removalTransaction.RollbackAsync();
        }

        var nextCount = new IvStockCountHdr
        {
            CompanyCode = "DEMO", BranchCode = "HQ", CountNo = "CC-NEXT",
            CountDate = countDate, Status = IvStockCountStatuses.Counted
        };
        var nextLine = new IvStockCountLine
        {
            Header = nextCount, LineNumber = 1, BalLocId = balance.Id,
            ICode = "RM2", IStatus = "ACTIVE", PhysicalQty = 5m
        };
        db.IvStockCountLines.Add(nextLine);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var countInsertion = await StockReplayPhysicalPlanner.BuildCountInsertionAsync(
            db, "DEMO", "HQ", nextLine.Id);
        var sheetInsertion = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, "DEMO", "HQ", nextCount.Id);
        Assert.Single(sheetInsertion.Lines);
        Assert.Equal(countInsertion.PlanHash, sheetInsertion.Lines[0].PlanHash);
        Assert.Equal(4, countInsertion.Candidate.Key.OriginalPostingSequence);
        Assert.Equal(StockReplayEventKey.CountClass,
            countInsertion.Candidate.Key.ClassPriority);
        Assert.Equal(4m, countInsertion.Projection.HistoricalQuantityBeforeCandidate);
        Assert.Equal(1m, countInsertion.Projection.Candidate.SignedDelta);
        Assert.Equal(5m, countInsertion.Projection.FinalQuantity);
        await using (var countTransaction = await db.Database.BeginTransactionAsync())
        {
            var coordinator = CreateCoordinator();
            var begin = await coordinator.BeginInTransactionAsync(db,
                Command(Guid.NewGuid()) with
                {
                    EffectiveAt = countInsertion.Candidate.Key.EffectiveAt,
                    SourceModule = "INVENTORY",
                    SourceDocumentType = "IV_STOCK_COUNT",
                    SourceDocumentId = nextCount.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    SourceDocumentNo = nextCount.CountNo
                });
            Assert.NotNull(begin.Context);
            await StockReplayPhysicalPlanner.ValidateWithinRootAsync(begin.Context!, countInsertion);
            await StockReplayPhysicalPlanner.ValidateWithinRootAsync(begin.Context!, sheetInsertion);
            var editedLine = await db.IvStockCountLines.SingleAsync(l => l.Id == nextLine.Id);
            editedLine.PhysicalQty = 6m;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var staleCount = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayPhysicalPlanner.ValidateWithinRootAsync(begin.Context!, countInsertion));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument, staleCount.Error.Code);
            var staleSheet = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayPhysicalPlanner.ValidateWithinRootAsync(begin.Context!, sheetInsertion));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument, staleSheet.Error.Code);
            await coordinator.AbortInTransactionAsync(begin.Context!);
            await countTransaction.RollbackAsync();
        }

        history = await db.IvTrxHistories.SingleAsync(x => x.Id == history.Id);
        history.ToStdQty = 3m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var historyError = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockCountGenerationResolver.ResolveActiveAsync(db, "DEMO", "HQ", [adjustmentLine.Id]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, historyError.Error.Code);

        posting = await db.StockPostings.SingleAsync(x => x.Id == posting.Id);
        posting.SourceDocumentNo = "DIFFERENT";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var error = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockCountGenerationResolver.ResolveActiveAsync(db, "DEMO", "HQ", [line.Id]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, error.Error.Code);
    }

    [Fact]
    public async Task Physical_tail_orders_foreign_replacement_by_original_business_owner()
    {
        await ActivateAsync();
        await using var db = await _factory.CreateDbContextAsync();
        var epochId = await db.StockLedgerEpochs.Select(x => x.Id).SingleAsync();
        var ownerTime = new DateTime(2026, 10, 10, 9, 0, 0);
        var rootTime = ownerTime.AddDays(-1);
        var owner = Posting("DEMO", "HQ", epochId, 1, Guid.NewGuid());
        owner.EffectiveAt = ownerTime;
        var root = Posting("DEMO", "HQ", epochId, 2, Guid.NewGuid());
        root.EffectiveAt = rootTime;
        db.StockPostings.AddRange(owner, root);
        await db.SaveChangesAsync();
        db.IvStockMasters.Add(new IvStockMaster { CompanyCode = "DEMO", ICode = "RM1" });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN"
        });
        var balance = new IvBalLoc
        {
            CompanyCode = "DEMO", BranchCode = "HQ", ICode = "RM1",
            WhCode = "MAIN", LocCode = "", LotNo = "", IStatus = "ACTIVE",
            StdQty = 3m
        };
        db.IvBalLocs.Add(balance);
        await db.SaveChangesAsync();

        var original = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 20, TrxLineNo = 1,
            TrxDtTime = ownerTime, TrxType = "MR", BatchStatus = "POSTED",
            ICode = "RM1", ToBalLocId = balance.Id, ToStdQty = 2m,
            LedgerVersion = 2, LedgerEpochId = epochId, StockPostingId = owner.Id,
            PostingLineNo = 1, DocumentRevision = 1, EntryRole = "ORIGINAL"
        };
        db.IvTrxHistories.Add(original);
        await db.SaveChangesAsync();
        var reversal = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 21, TrxLineNo = 1,
            TrxDtTime = ownerTime, TrxType = "MR", BatchStatus = "POSTED",
            ICode = "RM1", FromBalLocId = balance.Id, FrStdQty = 2m,
            LedgerVersion = 2, LedgerEpochId = epochId, StockPostingId = root.Id,
            PostingLineNo = 1, DocumentRevision = 2, EntryRole = "REVERSAL",
            ReversesHistoryId = original.Id
        };
        var replacement = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 22, TrxLineNo = 1,
            TrxDtTime = ownerTime, TrxType = "MR", BatchStatus = "POSTED",
            ICode = "RM1", ToBalLocId = balance.Id, ToStdQty = 3m,
            LedgerVersion = 2, LedgerEpochId = epochId, StockPostingId = root.Id,
            PostingLineNo = 2, DocumentRevision = 3, EntryRole = "ORIGINAL"
        };
        db.IvTrxHistories.AddRange(reversal, replacement);
        await db.SaveChangesAsync();
        var run = new StockReplayRun
        {
            CompanyCode = "DEMO", BranchCode = "HQ", RootStockPostingId = root.Id,
            RootRequestId = root.RequestId, RootDocumentType = "MR",
            RootDocumentId = "21", EarliestEffectiveAt = rootTime,
            LatestEffectiveAt = ownerTime, PlanHash = new string('A', 64),
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = "test"
        };
        db.StockReplayRuns.Add(run);
        await db.SaveChangesAsync();
        var candidateKey = new StockReplayEventKey(rootTime,
            StockReplayEventKey.OrdinaryClass, 2, "candidate", 0);
        var missingOwner = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayPhysicalTimelineReader.ReadLaterAsync(db,
                "DEMO", "HQ", balance.Id, candidateKey));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, missingOwner.Error.Code);
        db.StockReplayEffects.Add(new StockReplayEffect
        {
            CompanyCode = "DEMO", BranchCode = "HQ", ReplayRunId = run.Id,
            RootStockPostingId = root.Id, BusinessOwnerPostingId = owner.Id,
            EffectKind = StockReplayEffectTypes.PhysicalReplacement,
            AffectedEntityType = StockReplayEffectTypes.InventoryHistory,
            AffectedEntityId = replacement.Id, OriginalEntityId = original.Id,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var tail = await StockReplayPhysicalTimelineReader.ReadLaterAsync(db,
            "DEMO", "HQ", balance.Id, candidateKey);
        var active = Assert.Single(tail);
        Assert.Equal(replacement.Id, active.EventId);
        Assert.Equal(ownerTime, active.Key.EffectiveAt);
        Assert.Equal(owner.PostingSequence, active.Key.OriginalPostingSequence);
        Assert.Equal($"H{original.Id:D10}", active.Key.LineIdentity);
        Assert.Equal(3m, active.SignedDelta);
    }

    [Fact]
    public async Task Wac_financial_tail_joins_active_history_and_value_only_fact()
    {
        await ActivateAsync();
        await using var db = await _factory.CreateDbContextAsync();
        var epochId = await db.StockLedgerEpochs.Select(e => e.Id).SingleAsync();
        var receiptAt = new DateTime(2026, 10, 2, 8, 0, 0);
        var valueAt = receiptAt.AddDays(1);
        var receiptOwner = Posting("DEMO", "HQ", epochId, 1, Guid.NewGuid());
        receiptOwner.EffectiveAt = receiptAt;
        receiptOwner.BusinessDate = receiptAt.Date;
        var valueOwner = Posting("DEMO", "HQ", epochId, 2, Guid.NewGuid());
        valueOwner.EffectiveAt = valueAt;
        valueOwner.BusinessDate = valueAt.Date;
        db.StockPostings.AddRange(receiptOwner, valueOwner);
        db.IvStockMasters.Add(new IvStockMaster { CompanyCode = "DEMO", ICode = "RM1" });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN"
        });
        var balance = new IvBalLoc
        {
            CompanyCode = "DEMO", BranchCode = "HQ", ICode = "RM1",
            WhCode = "MAIN", LocCode = "", LotNo = "", IStatus = "ACTIVE",
            StdQty = 5m
        };
        db.IvBalLocs.Add(balance);
        db.StockCostStates.Add(new StockCostState
        {
            CompanyCode = "DEMO", BranchCode = "HQ", ItemCode = "RM1",
            CostMethod = StockCostMethods.MovingAverage,
            OnHandBaseQty = 5m, InventoryValue = 11m,
            CurrentUnitCost = 2.2m, AverageUnitCost = 2.2m
        });
        await db.SaveChangesAsync();

        var receipt = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 20,
            TrxLineNo = 1, TrxDtTime = receiptAt, TrxType = "MR",
            BatchStatus = "POSTED", ICode = "RM1",
            ToBalLocId = balance.Id, ToStdQty = 5m,
            LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = receiptOwner.Id, PostingLineNo = 1,
            DocumentRevision = 1, EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        db.IvTrxHistories.Add(receipt);
        await db.SaveChangesAsync();
        db.StockValuationFacts.AddRange(
            new StockValuationFact
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                LedgerEpochId = epochId, StockPostingId = receiptOwner.Id,
                PostingLineNo = 1, InventoryHistoryId = receipt.Id,
                EffectiveAt = receiptAt, BusinessDate = receiptAt.Date,
                PeriodKey = "2026-10", ItemCode = "RM1",
                CostMethod = StockCostMethods.MovingAverage,
                ValuationStatus = StockValuationStatuses.Valued,
                Direction = 1, BaseQty = 5m, CostAmount = 10m
            },
            new StockValuationFact
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                LedgerEpochId = epochId, StockPostingId = valueOwner.Id,
                PostingLineNo = 1, EffectiveAt = valueAt,
                BusinessDate = valueAt.Date, PeriodKey = "2026-10",
                ItemCode = "RM1", CostMethod = StockCostMethods.MovingAverage,
                ValuationStatus = StockValuationStatuses.Valued,
                Direction = 1, BaseQty = 0m, CostAmount = 1m
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var after = new StockReplayEventKey(receiptAt.AddDays(-1),
            StockReplayEventKey.OrdinaryClass, 1, "candidate", 0);
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, "DEMO", "HQ", "RM1", after);
        Assert.Equal(5m, tail.CurrentQuantity);
        Assert.Equal(11m, tail.CurrentValue);
        Assert.Equal(2, tail.LaterEvents.Count);
        Assert.Equal(receipt.Id, tail.LaterEvents[0].EventId);
        Assert.StartsWith("V", tail.LaterEvents[1].Key.LineIdentity);
        Assert.Equal(1m, tail.LaterEvents[1].FixedSignedValue);

        db.StockPostingBranchSequences.Add(new StockPostingBranchSequence
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LastSequence = 2,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var candidateAt = new DateTime(2026, 10, 1, 10, 0, 0);
        var candidatePhysical = new StockReplayPhysicalEvent(1000,
            new StockReplayEventKey(candidateAt,
                StockReplayEventKey.OrdinaryClass, 3, "candidate", 0), 5m);
        var physicalPlan = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            db, "DEMO", "HQ", balance.Id, candidatePhysical);
        var candidateWac = new StockReplayWacEvent(1000,
            candidatePhysical.Key, 0m, 0m, 5m, FixedSignedValue: 20m);
        var financialPlan = await StockReplayWacInsertionPlanner.BuildAsync(
            db, "DEMO", "HQ", "RM1", physicalPlan, candidateWac);
        Assert.Equal(10m, financialPlan.Projection.FinalQuantity);
        Assert.Equal(31m, financialPlan.Projection.FinalValue);
        Assert.Equal(64, financialPlan.PlanHash.Length);

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var begin = await CreateCoordinator().BeginInTransactionAsync(db,
                Command(Guid.NewGuid()) with { EffectiveAt = candidateAt });
            Assert.NotNull(begin.Context);
            await StockReplayWacInsertionPlanner.ValidateWithinRootAsync(
                begin.Context!, financialPlan);
            var changedState = await db.StockCostStates.SingleAsync();
            changedState.InventoryValue = 12m;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var stale = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayWacInsertionPlanner.ValidateWithinRootAsync(
                    begin.Context!, financialPlan));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument, stale.Error.Code);
            await CreateCoordinator().AbortInTransactionAsync(begin.Context!);
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();

        var receiptFactId = await db.StockValuationFacts
            .Where(f => f.InventoryHistoryId == receipt.Id)
            .Select(f => f.Id).SingleAsync();
        await using (var reversalTransaction = await db.Database.BeginTransactionAsync())
        {
            var begin = await CreateCoordinator().BeginInTransactionAsync(db,
                Command(Guid.NewGuid()) with { EffectiveAt = candidateAt });
            Assert.NotNull(begin.Context);
            var reversal = Assert.Single(await StockReplayValuationReversalWriter.AppendAsync(
                begin.Context!, receipt.Id,
                [new StockReplayValuationReversalLine(receiptFactId, 10, 0)]));
            Assert.Equal(receiptFactId, reversal.ReversesValuationFactId);
            Assert.Equal(-1, reversal.Direction);
            Assert.Equal(receiptAt, reversal.EffectiveAt);
            Assert.Equal(5m, reversal.BaseQty);
            Assert.Equal(10m, reversal.CostAmount);
            await db.SaveChangesAsync();
            await CreateCoordinator().AbortInTransactionAsync(begin.Context!);
            await reversalTransaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();

        var state = await db.StockCostStates.SingleAsync();
        state.OnHandBaseQty = 4m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayWacFinancialTailReader.ReadLaterAsync(
                db, "DEMO", "HQ", "RM1", after));
    }

    [Fact]
    public void Movement_registry_has_consistent_explicit_inverses()
    {
        var registry = new StockMovementRegistry();
        foreach (var movement in registry.All.Where(x => x.InverseCode is not null))
        {
            var inverse = registry.GetRequired(movement.InverseCode!);
            Assert.Equal(-movement.Direction, inverse.Direction);
            Assert.Equal(movement.Code, inverse.InverseCode);
        }
    }

    [Fact]
    public void Contribution_allocator_uses_one_shared_fifo_budget()
    {
        var allocator = new ProductionContributionAllocator();
        var contributions = new[]
        {
            new ProductionContribution(1, 10, new DateTime(2026, 10, 1), 1, 1, 10m)
        };

        var exact = allocator.BuildPlan(contributions,
        [
            new("A", 6m),
            new("B", 4m)
        ]);
        Assert.Equal(10m, exact.Sum(x => x.BaseQty));

        var ex = Assert.Throws<StockLedgerException>(() => allocator.BuildPlan(contributions,
        [
            new("A", 6m),
            new("B", 6m)
        ]));
        Assert.Equal(StockLedgerErrorCodes.InsufficientBaseQty, ex.Error.Code);
    }

    [Fact]
    public async Task Production_writer_rolls_fact_and_envelope_back_atomically()
    {
        await ActivateAsync();
        var writer = new ProductionStockWriter(new StockMovementRegistry());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateCoordinator().ExecuteAsync<int>(Command(Guid.NewGuid()), async (context, ct) =>
            {
                var lot = new ProductionBalLot
                {
                    Uid = 999, CompanyCode = "DEMO", BranchCode = "HQ", Kind = "WIP",
                    ItemCode = "WIP-1", Qty = 10m, Uom = "EA", BaseQty = 10m,
                    BaseUom = "EA", ConversionFactorToBase = 1m,
                    WorkOrderId = 1, WorkOrderNo = "WO-1", LotNo = "POOL-1"
                };
                await writer.ApplyAsync(context,
                [
                    new(lot, "CONSUME", 4m, 4m, 1, null, null, null, 1,
                        "OUTPUT", "OUT-1", "LINE-1", 0)
                ], ct);
                throw new InvalidOperationException("fault injection");
#pragma warning disable CS0162
                return 0;
#pragma warning restore CS0162
            }));

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Empty(await verify.ProductionBalLotMovements.ToListAsync());
        Assert.Empty(await verify.StockPostings.ToListAsync());
    }

    [Fact]
    public async Task Coordinator_stays_disabled_without_active_epoch()
    {
        var coordinator = CreateCoordinator();
        var called = false;
        var result = await coordinator.ExecuteAsync(Command(Guid.NewGuid()), (_, _) =>
        {
            called = true;
            return Task.FromResult(1);
        });

        Assert.False(result.Succeeded);
        Assert.False(result.LedgerEnabled);
        Assert.Equal(StockLedgerErrorCodes.LedgerDisabled, result.Error!.Code);
        Assert.False(called);
    }

    [Fact]
    public async Task Coordinator_seals_once_replays_same_fingerprint_and_rejects_reuse()
    {
        await ActivateAsync();
        var coordinator = CreateCoordinator();
        var requestId = Guid.NewGuid();
        var command = Command(requestId);
        var calls = 0;

        var first = await coordinator.ExecuteAsync(command, (_, _) =>
            Task.FromResult(++calls));
        var replay = await coordinator.ExecuteAsync(command, (_, _) =>
            Task.FromResult(++calls));
        var changed = command with
        {
            Evidence = StockPostingFingerprint.Create(new { command = "TEST", qty = 2 }, new { line = 1 })
        };
        var rejected = await coordinator.ExecuteAsync(changed, (_, _) =>
            Task.FromResult(++calls));

        Assert.True(first.Succeeded);
        Assert.False(first.WasReplay);
        Assert.True(replay.Succeeded);
        Assert.True(replay.WasReplay);
        Assert.Equal(first.StockPostingId, replay.StockPostingId);
        Assert.Equal(1, calls);
        Assert.Equal(StockLedgerErrorCodes.RequestIdReused, rejected.Error!.Code);

        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.StockPostings.SingleAsync();
        Assert.NotNull(stored.SealedAtUtc);
        Assert.Equal(1, stored.PostingSequence);
    }

    [Fact]
    public async Task Caller_owned_abort_clears_context_and_rollback_removes_unsealed_root()
    {
        await ActivateAsync();
        var coordinator = CreateCoordinator();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var begin = await coordinator.BeginInTransactionAsync(db, Command(Guid.NewGuid()));
            Assert.NotNull(begin.Context);
            await coordinator.AbortInTransactionAsync(begin.Context!);
            await coordinator.AbortInTransactionAsync(begin.Context!);
            await transaction.RollbackAsync();
        }

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Empty(await verify.StockPostings.ToListAsync());
    }

    [Fact]
    public async Task Coordinator_rejects_cross_tenant_reversal_id()
    {
        await ActivateAsync();
        long foreignPostingId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var foreignEpoch = new StockLedgerEpoch
            {
                CompanyCode = "OTHER",
                BranchCode = "B1",
                EffectiveFrom = DateTime.UtcNow,
                Version = 2,
                Status = StockLedgerEpochStatuses.Active,
                MigrationBatchId = Guid.NewGuid(),
                ReconciliationManifestHash = new string('B', 64)
            };
            db.StockLedgerEpochs.Add(foreignEpoch);
            await db.SaveChangesAsync();
            var foreign = Posting("OTHER", "B1", foreignEpoch.Id, 1, Guid.NewGuid());
            db.StockPostings.Add(foreign);
            await db.SaveChangesAsync();
            foreignPostingId = foreign.Id;
        }

        var result = await CreateCoordinator().ExecuteAsync(
            Command(Guid.NewGuid()) with { ReversesPostingId = foreignPostingId },
            (_, _) => Task.FromResult(1));

        Assert.False(result.Succeeded);
        Assert.Equal(StockLedgerErrorCodes.InvalidStockIdentity, result.Error!.Code);
    }

    private StockPostingCoordinator CreateCoordinator() => new(
        _factory,
        new Tenant(),
        new BranchStockTransactionLock(),
        new StockPeriodGuard(),
        new NoActiveStockFreezeGuard());

    private async Task ActivateAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.StockLedgerEpochs.Add(new StockLedgerEpoch
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            EffectiveFrom = new DateTime(2026, 10, 1),
            Version = 2,
            Status = StockLedgerEpochStatuses.Active,
            MigrationBatchId = Guid.NewGuid(),
            ReconciliationManifestHash = new string('A', 64)
        });
        await db.SaveChangesAsync();
    }

    private static StockPostingCommand Command(Guid requestId) => new()
    {
        RequestId = requestId,
        CommandType = "TEST",
        SourceModule = "TEST",
        SourceDocumentType = "FIXTURE",
        SourceDocumentId = "1",
        SourceDocumentNo = "T-1",
        DocumentRevision = 1,
        EffectiveAt = new DateTime(2026, 10, 2, 8, 0, 0),
        Evidence = StockPostingFingerprint.Create(new { command = "TEST", qty = 1 }, new { line = 1 })
    };

    private static StockPosting Posting(
        string company, string branch, long epochId, long sequence, Guid requestId) => new()
    {
        CompanyCode = company,
        BranchCode = branch,
        LedgerEpochId = epochId,
        PostingSequence = sequence,
        RequestId = requestId,
        CommandType = "FOREIGN",
        RequestFingerprint = new string('C', 64),
        SourceModule = "TEST",
        SourceDocumentType = "FOREIGN",
        SourceDocumentId = requestId.ToString("N"),
        SourceDocumentNo = "FOREIGN",
        DocumentRevision = 1,
        PostingRole = "PRIMARY",
        SourceSnapshotJson = "{}",
        SourceSnapshotHash = new string('D', 64),
        SourceSnapshotSchemaVersion = 1,
        EffectiveAt = DateTime.UtcNow,
        BusinessDate = DateTime.UtcNow.Date,
        PeriodKey = "2026-10",
        PostedAtUtc = DateTime.UtcNow,
        PostedBy = "test",
        SealedAtUtc = DateTime.UtcNow
    };

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private sealed class Tenant : IInventoryTenantContext
    {
        private static readonly InventoryTenantScope Scope = new()
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            LocationCode = "SITE",
            UserId = "tester"
        };
        public InventoryTenantScope? TryCompanyScope() => Scope;
        public InventoryTenantScope? TryBranchScope() => Scope;
        public InventoryTenantScope? TryWriteScope() => Scope;
    }

    private sealed class LocalFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
