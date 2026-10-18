using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Moq;

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
    public async Task Count_cost_approval_proposals_require_permission_and_cover_positive_lines()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var now = new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);
        db.StockCostPolicyRevisions.Add(new StockCostPolicyRevision
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            CostMethod = StockCostMethods.MovingAverage,
            EffectiveFrom = new DateTime(2026, 10, 1),
            Status = StockCostPolicyStatuses.Active,
            ApprovedBy = "TEST", ApprovedAtUtc = now,
            CreatedAtUtc = now, CreatedBy = "TEST"
        });
        var header = new IvStockCountHdr
        {
            CompanyCode = "DEMO", BranchCode = "HQ", CountNo = "CC-APPROVAL",
            CountDate = new DateTime(2026, 10, 10), Status = IvStockCountStatuses.Counted
        };
        var manual = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = 7,
            ICode = "RM1", IStatus = "ACTIVE", PhysicalQty = 4m
        };
        var zero = new IvStockCountLine
        {
            Header = header, LineNumber = 2, BalLocId = 8,
            ICode = "RM2", IStatus = "ACTIVE", PhysicalQty = 1m
        };
        var uncounted = new IvStockCountLine
        {
            Header = header, LineNumber = 3, BalLocId = 9,
            ICode = "RM3", IStatus = "ACTIVE", PhysicalQty = 0m
        };
        db.IvStockCountLines.AddRange(manual, zero, uncounted);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var approvals = new[]
        {
            new StockCountCostApprovalRequest(manual.Id, "MANUAL_APPROVED", 1.234567m, "Count manager approved"),
            new StockCountCostApprovalRequest(zero.Id, "ZERO_COST_APPROVED", 0m, "Sample stock at no cost")
        };
        var access = new Mock<IAccessRightService>();
        var denied = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockCountCostBasisProposalFactory.CreateAsync(db, access.Object,
                "DEMO", "HQ", header.Id, approvals, "manager", now));
        Assert.Equal(StockLedgerErrorCodes.ValuationRequired, denied.Error.Code);
        access.Setup(a => a.CanAsync(MenuCodes.InventoryStockCount,
                PermissionCodes.PriceOverride, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var missingCost = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockCountCostBasisProposalFactory.CreateAsync(db, access.Object,
                "DEMO", "HQ", header.Id, [approvals[0]], "manager", now));
        Assert.Equal(StockLedgerErrorCodes.ValuationRequired, missingCost.Error.Code);

        var bases = await StockCountCostBasisProposalFactory.CreateAsync(db,
            access.Object, "DEMO", "HQ", header.Id, approvals, "manager", now);
        Assert.Equal(2, bases.Count);
        Assert.Equal(1.234567m, bases.Single(b => b.StockCountLineId == manual.Id).FrozenFallbackUnitCost);
        Assert.Equal(StockCountCostBasisKinds.ZeroCostApproved,
            bases.Single(b => b.StockCountLineId == zero.Id).CostBasisKind);
        Assert.All(bases, b => Assert.Equal("manager", b.CostApprovedBy));
        Assert.Empty(db.ChangeTracker.Entries<StockCountCostBasis>());
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, "DEMO", "HQ", header.Id, bases);
        Assert.Equal("IV_STOCK_COUNT", command.SourceDocumentType);

        var invalid = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockCountCostBasisProposalFactory.CreateAsync(db, access.Object,
                "DEMO", "HQ", header.Id,
                [new StockCountCostApprovalRequest(uncounted.Id, "ZERO_COST_APPROVED", 0m, "Invalid line")],
                "manager", now));
        Assert.Equal(StockLedgerErrorCodes.ValuationRequired, invalid.Error.Code);
        var imprecise = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockCountCostBasisProposalFactory.CreateAsync(db, access.Object,
                "DEMO", "HQ", header.Id,
                [new StockCountCostApprovalRequest(manual.Id, "MANUAL_APPROVED", 1.2345678m, "Too precise"),
                 approvals[1]], "manager", now));
        Assert.Equal(StockLedgerErrorCodes.ValuationRequired, imprecise.Error.Code);
    }

    [Fact]
    public async Task Count_cost_proposal_uses_event_date_standard_revision_without_override()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var now = new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);
        db.StockCostPolicyRevisions.Add(new StockCostPolicyRevision
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            CostMethod = StockCostMethods.Standard,
            EffectiveFrom = new DateTime(2026, 10, 1),
            Status = StockCostPolicyStatuses.Active,
            ApprovedBy = "TEST", ApprovedAtUtc = now,
            CreatedAtUtc = now, CreatedBy = "TEST"
        });
        var revision = new ItemStandardCostRevision
        {
            CompanyCode = "DEMO", BranchCode = "HQ", ItemCode = "RM1",
            EffectiveFrom = new DateTime(2026, 10, 1),
            MaterialCost = 2.123456m, TotalStandardCost = 2.123456m,
            Revision = 1, ApprovedBy = "TEST", ApprovedAtUtc = now,
            CreatedAtUtc = now, CreatedBy = "TEST"
        };
        db.ItemStandardCostRevisions.Add(revision);
        var header = new IvStockCountHdr
        {
            CompanyCode = "DEMO", BranchCode = "HQ", CountNo = "CC-STANDARD-BASIS",
            CountDate = new DateTime(2026, 10, 10), Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = 7,
            ICode = "RM1", IStatus = "ACTIVE", PhysicalQty = 4m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var access = new Mock<IAccessRightService>(MockBehavior.Strict);
        var bases = await StockCountCostBasisProposalFactory.CreateAsync(db,
            access.Object, "DEMO", "HQ", header.Id, [], "unused", now);
        var basis = Assert.Single(bases);
        Assert.Equal(StockCountCostBasisKinds.StandardRevision, basis.CostBasisKind);
        Assert.Equal(revision.Id, basis.StandardCostRevisionId);
        Assert.Equal(2.123456m, basis.FrozenFallbackUnitCost);
        Assert.Null(basis.CostApprovedBy);
        Assert.Empty(db.ChangeTracker.Entries<StockCountCostBasis>());

        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, "DEMO", "HQ", header.Id, bases);
        Assert.Equal("IV_STOCK_COUNT", command.SourceDocumentType);
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
            ICode = "RM0", WHCode = "MAIN", LocCode = "", LotNo = "",
            IStatus = "ACTIVE", PhysicalQty = 0m
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
            var changedObservation = await db.IvStockCountLines.SingleAsync(l => l.Id == line.Id);
            changedObservation.LotNo = "WRONG";
            await db.SaveChangesAsync();
            var staleSlice = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayPhysicalPlanner.PrepareCountSheetWithinRootAsync(context, sheet));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument, staleSlice.Error.Code);
            changedObservation.LotNo = "";
            await db.SaveChangesAsync();
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
            trackedHeader.Status = IvStockCountStatuses.Counted;
            trackedHeader.PostedBatchNo = null;
            await db.SaveChangesAsync();
            var written = await StockCountInitialHistoryWriter.AppendAsync(
                context, permit, new RunningNumberService());
            Assert.Null(written.AdjustmentBatchNo);
            var generations = written.Generations;
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
            ICode = "RM2", WHCode = "MAIN", LocCode = "", LotNo = "",
            IStatus = "ACTIVE", PhysicalQty = 5m
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
                Direction = 1, BaseQty = 2m, CostAmount = 4m,
                BaseCostAmount = 4m, TransactionCurrency = "USD",
                TransactionCostAmount = 4m, ExchangeRate = 1m
            },
            new StockValuationFact
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                LedgerEpochId = epochId, StockPostingId = receiptOwner.Id,
                PostingLineNo = 1, SplitOrdinal = 1, InventoryHistoryId = receipt.Id,
                EffectiveAt = receiptAt, BusinessDate = receiptAt.Date,
                PeriodKey = "2026-10", ItemCode = "RM1",
                CostMethod = StockCostMethods.MovingAverage,
                ValuationStatus = StockValuationStatuses.Valued,
                Direction = 1, BaseQty = 3m, CostAmount = 6m,
                BaseCostAmount = 6m
            },
            new StockValuationFact
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                LedgerEpochId = epochId, StockPostingId = valueOwner.Id,
                PostingLineNo = 1, EffectiveAt = valueAt,
                BusinessDate = valueAt.Date, PeriodKey = "2026-10",
                ItemCode = "RM1", CostMethod = StockCostMethods.MovingAverage,
                ValuationStatus = StockValuationStatuses.Valued,
                Direction = 1, BaseQty = 0m, CostAmount = 1m,
                BaseCostAmount = 1m
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

        await using (var stateTransaction = await db.Database.BeginTransactionAsync())
        {
            var coordinator = CreateCoordinator();
            var begin = await coordinator.BeginInTransactionAsync(db,
                Command(Guid.NewGuid()) with { EffectiveAt = candidateAt });
            Assert.NotNull(begin.Context);
            var permit = await StockReplayWacInsertionPlanner.PrepareWithinRootAsync(
                begin.Context!, financialPlan);
            var changedBalance = await db.IvBalLocs.SingleAsync(b => b.Id == balance.Id);
            changedBalance.StdQty = 10m;
            await db.SaveChangesAsync();
            var missingFacts = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayWacCostStateWriter.ApplyAsync(begin.Context!, permit));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, missingFacts.Error.Code);
            var insertedHistory = new IvTrxHistory
            {
                Id = 1000, CompanyCode = "DEMO", BranchCode = "HQ",
                BatchNo = 99, TrxLineNo = 1, TrxDtTime = candidateAt,
                TrxType = "MR", BatchStatus = "POSTED", ICode = "RM1",
                ToBalLocId = balance.Id, ToStdQty = 5m,
                LedgerVersion = 2, LedgerEpochId = epochId,
                StockPostingId = begin.Context!.Posting.Id,
                PostingLineNo = 1, DocumentRevision = 1,
                EntryRole = "ORIGINAL",
                ValuationStatus = StockValuationStatuses.Valued
            };
            db.IvTrxHistories.Add(insertedHistory);
            db.StockValuationFacts.Add(new StockValuationFact
            {
                CompanyCode = "DEMO", BranchCode = "HQ",
                LedgerEpochId = epochId,
                StockPostingId = begin.Context!.Posting.Id,
                PostingLineNo = 1, InventoryHistory = insertedHistory,
                EffectiveAt = candidateAt, BusinessDate = candidateAt.Date,
                PeriodKey = "2026-10", ItemCode = "RM1",
                CostMethod = StockCostMethods.MovingAverage,
                ValuationStatus = StockValuationStatuses.Valued,
                Direction = 1, BaseQty = 5m, CostAmount = 20m,
                BaseCostAmount = 20m
            });
            await StockReplayWacCostStateWriter.ApplyAsync(begin.Context!, permit);
            var updatedState = await db.StockCostStates.SingleAsync();
            Assert.Equal(10m, updatedState.OnHandBaseQty);
            Assert.Equal(31m, updatedState.InventoryValue);
            Assert.Equal(3.1m, updatedState.AverageUnitCost);
            Assert.Equal(begin.Context!.Posting.PostingSequence,
                updatedState.LastPostingSequence);
            await coordinator.AbortInTransactionAsync(begin.Context!);
            await stateTransaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();

        var receiptFactIds = await db.StockValuationFacts
            .Where(f => f.InventoryHistoryId == receipt.Id)
            .OrderBy(f => f.SplitOrdinal).Select(f => f.Id).ToArrayAsync();
        await using (var reversalTransaction = await db.Database.BeginTransactionAsync())
        {
            var begin = await CreateCoordinator().BeginInTransactionAsync(db,
                Command(Guid.NewGuid()) with { EffectiveAt = candidateAt });
            Assert.NotNull(begin.Context);
            var reversals = await StockReplayValuationReversalWriter.AppendAsync(
                begin.Context!, receipt.Id,
                [new StockReplayValuationReversalLine(receiptFactIds[0], 10, 0),
                    new StockReplayValuationReversalLine(receiptFactIds[1], 10, 1)]);
            Assert.Equal(2, reversals.Count);
            Assert.Equal(receiptFactIds, reversals.Select(f => f.ReversesValuationFactId!.Value).ToArray());
            Assert.All(reversals, f =>
            {
                Assert.Equal(-1, f.Direction);
                Assert.Equal(receiptAt, f.EffectiveAt);
            });
            Assert.Equal(5m, reversals.Sum(f => f.BaseQty));
            Assert.Equal(10m, reversals.Sum(f => f.CostAmount));
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

        state = await db.StockCostStates.SingleAsync();
        state.OnHandBaseQty = 5m;
        state.InventoryValue = 12m;
        state.AverageUnitCost = 2.4m;
        state.CurrentUnitCost = 2.4m;
        var valueOriginal = await db.StockValuationFacts.SingleAsync(f =>
            f.StockPostingId == valueOwner.Id);
        var valueCorrection = Posting("DEMO", "HQ", epochId, 3, Guid.NewGuid());
        valueCorrection.EffectiveAt = receiptAt.AddHours(1);
        valueCorrection.BusinessDate = receiptAt.Date;
        db.StockPostings.Add(valueCorrection);
        await db.SaveChangesAsync();
        db.StockValuationFacts.AddRange(
            new StockValuationFact
            {
                CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId,
                StockPostingId = valueCorrection.Id, PostingLineNo = 1,
                EffectiveAt = valueAt, BusinessDate = valueAt.Date,
                PeriodKey = "2026-10", ItemCode = "RM1",
                CostMethod = StockCostMethods.MovingAverage,
                ValuationStatus = StockValuationStatuses.Reversed,
                Direction = -1, BaseQty = 0m, CostAmount = 1m,
                BaseCostAmount = 1m,
                ReversesValuationFactId = valueOriginal.Id,
                OriginalValuationFactId = valueOriginal.Id
            },
            new StockValuationFact
            {
                CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId,
                StockPostingId = valueCorrection.Id, PostingLineNo = 2,
                EffectiveAt = valueAt, BusinessDate = valueAt.Date,
                PeriodKey = "2026-10", ItemCode = "RM1",
                CostMethod = StockCostMethods.MovingAverage,
                ValuationStatus = StockValuationStatuses.Valued,
                Direction = 1, BaseQty = 0m, CostAmount = 2m,
                BaseCostAmount = 2m,
                OriginalValuationFactId = valueOriginal.Id
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var correctedTail = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, "DEMO", "HQ", "RM1", after);
        var correctedValue = Assert.Single(correctedTail.LaterEvents.Where(e =>
            e.Key.LineIdentity.StartsWith('V')));
        Assert.Equal(tail.LaterEvents[1].EventId, correctedValue.EventId);
        Assert.Equal(tail.LaterEvents[1].Key, correctedValue.Key);
        Assert.Equal(2m, correctedValue.FixedSignedValue);

        var branchSequence = await db.StockPostingBranchSequences.SingleAsync();
        branchSequence.LastSequence = 3;
        await db.SaveChangesAsync();
        await using var valueTransaction = await db.Database.BeginTransactionAsync();
        var valueCoordinator = CreateCoordinator();
        var valueBegin = await valueCoordinator.BeginInTransactionAsync(db,
            Command(Guid.NewGuid()) with { EffectiveAt = receiptAt });
        Assert.NotNull(valueBegin.Context);
        var staleValue = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayWacValueRepricingWriter.AppendAsync(valueBegin.Context!,
                valueOriginal.Id, 1m, 3m, 10, 11));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, staleValue.Error.Code);
        var rewritten = await StockReplayWacValueRepricingWriter.AppendAsync(
            valueBegin.Context!, valueOriginal.Id, 2m, 3m, 10, 11);
        Assert.NotNull(rewritten);
        Assert.Equal(2m, rewritten!.Reversal.CostAmount);
        Assert.Equal(3m, rewritten.Replacement.CostAmount);
        Assert.Equal(valueOriginal.Id, rewritten.Replacement.OriginalValuationFactId);
        state = await db.StockCostStates.SingleAsync();
        state.InventoryValue = 13m;
        state.AverageUnitCost = 2.6m;
        state.CurrentUnitCost = 2.6m;
        valueBegin.Context!.Posting.SealedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var twiceCorrected = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, "DEMO", "HQ", "RM1", after);
        var finalValue = Assert.Single(twiceCorrected.LaterEvents.Where(e =>
            e.Key.LineIdentity.StartsWith('V')));
        Assert.Equal(3m, finalValue.FixedSignedValue);
        Assert.Equal(correctedValue.Key, finalValue.Key);
        await valueCoordinator.AbortInTransactionAsync(valueBegin.Context!);
        await valueTransaction.RollbackAsync();
        db.ChangeTracker.Clear();

        state = await db.StockCostStates.SingleAsync();
        state.InventoryValue = 14m;
        state.AverageUnitCost = 2.8m;
        state.CurrentUnitCost = 2.8m;
        branchSequence = await db.StockPostingBranchSequences.SingleAsync();
        branchSequence.LastSequence = 4;
        var receiptRestatementOwner = Posting(
            "DEMO", "HQ", epochId, 4, Guid.NewGuid());
        receiptRestatementOwner.EffectiveAt = receiptAt.AddHours(-1);
        receiptRestatementOwner.BusinessDate = receiptAt.Date;
        db.StockPostings.Add(receiptRestatementOwner);
        await db.SaveChangesAsync();
        db.StockValuationFacts.Add(new StockValuationFact
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId,
            StockPostingId = receiptRestatementOwner.Id, PostingLineNo = 1,
            OriginalValuationFactId = receiptFactIds[0],
            EffectiveAt = receiptAt, BusinessDate = receiptAt.Date,
            PeriodKey = "2026-10", ItemCode = "RM1",
            CostMethod = StockCostMethods.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            Direction = 1, BaseQty = 0m, CostAmount = 2m,
            BaseCostAmount = 2m
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var attachedTail = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, "DEMO", "HQ", "RM1", after);
        Assert.Equal(3, attachedTail.LaterEvents.Count);
        Assert.Equal(receipt.Id, attachedTail.LaterEvents[0].EventId);
        Assert.Equal($"H{receipt.Id:D10}", attachedTail.LaterEvents[1].Key.LineIdentity);
        Assert.Equal(1, attachedTail.LaterEvents[1].Key.AttachedSuborder);
        Assert.Equal(2m, attachedTail.LaterEvents[1].FixedSignedValue);
        Assert.StartsWith("V", attachedTail.LaterEvents[2].Key.LineIdentity);
        var attachedProjection = StockReplayWacTailProjector.SimulateSinglePileInsertion(
            attachedTail, physicalPlan, candidateWac);
        Assert.Equal(10m, attachedProjection.FinalQuantity);
        Assert.Equal(34m, attachedProjection.FinalValue);

        await using var receiptTransaction = await db.Database.BeginTransactionAsync();
        var receiptCoordinator = CreateCoordinator();
        var receiptBegin = await receiptCoordinator.BeginInTransactionAsync(db,
            Command(Guid.NewGuid()) with { EffectiveAt = receiptAt });
        Assert.NotNull(receiptBegin.Context);
        var unchanged = await StockReplayWacReceiptRestatementWriter.AppendAsync(
            receiptBegin.Context!, receipt.Id, receiptFactIds[0], 6m,
            StockValuationSources.PurchasePriceVariance, 20, 21);
        Assert.Null(unchanged);
        var nextDelta = await StockReplayWacReceiptRestatementWriter.AppendAsync(
            receiptBegin.Context!, receipt.Id, receiptFactIds[0], 7m,
            StockValuationSources.PurchasePriceVariance, 20, 21);
        Assert.NotNull(nextDelta);
        Assert.Equal(2m, nextDelta!.Reversal!.CostAmount);
        Assert.Equal(3m, nextDelta.ActiveDelta!.CostAmount);
        state = await db.StockCostStates.SingleAsync();
        state.InventoryValue = 15m;
        state.AverageUnitCost = 3m;
        state.CurrentUnitCost = 3m;
        receiptBegin.Context!.Posting.SealedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var replacedReceiptTail = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, "DEMO", "HQ", "RM1", after);
        Assert.Equal(3m, replacedReceiptTail.LaterEvents[1].FixedSignedValue);
        Assert.Equal(attachedTail.LaterEvents[1].Key,
            replacedReceiptTail.LaterEvents[1].Key);
        await receiptCoordinator.AbortInTransactionAsync(receiptBegin.Context!);
        await receiptTransaction.RollbackAsync();
        db.ChangeTracker.Clear();

        await using var removeDeltaTransaction = await db.Database.BeginTransactionAsync();
        var removeDeltaCoordinator = CreateCoordinator();
        var removeDeltaBegin = await removeDeltaCoordinator.BeginInTransactionAsync(db,
            Command(Guid.NewGuid()) with { EffectiveAt = receiptAt });
        Assert.NotNull(removeDeltaBegin.Context);
        var removedDelta = await StockReplayWacReceiptRestatementWriter.AppendAsync(
            removeDeltaBegin.Context!, receipt.Id, receiptFactIds[0], 4m,
            StockValuationSources.PurchasePriceVariance, 20, 21);
        Assert.NotNull(removedDelta);
        Assert.NotNull(removedDelta!.Reversal);
        Assert.Null(removedDelta.ActiveDelta);
        state = await db.StockCostStates.SingleAsync();
        state.InventoryValue = 12m;
        state.AverageUnitCost = 2.4m;
        state.CurrentUnitCost = 2.4m;
        removeDeltaBegin.Context!.Posting.SealedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var noReceiptDelta = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, "DEMO", "HQ", "RM1", after);
        Assert.Equal(2, noReceiptDelta.LaterEvents.Count);
        Assert.DoesNotContain(noReceiptDelta.LaterEvents,
            e => e.Key.LineIdentity == $"H{receipt.Id:D10}"
                && e.Key.AttachedSuborder == 1);
        await removeDeltaCoordinator.AbortInTransactionAsync(removeDeltaBegin.Context!);
        await removeDeltaTransaction.RollbackAsync();
    }

    [Fact]
    public async Task Wac_issue_repricing_reverses_every_old_split_and_appends_complete_replacement()
    {
        await ActivateAsync();
        await using var db = await _factory.CreateDbContextAsync();
        var epochId = await db.StockLedgerEpochs.Select(e => e.Id).SingleAsync();
        var issueAt = new DateTime(2026, 10, 3, 9, 0, 0);
        var owner = Posting("DEMO", "HQ", epochId, 1, Guid.NewGuid());
        owner.EffectiveAt = issueAt;
        owner.BusinessDate = issueAt.Date;
        db.StockPostings.Add(owner);
        db.StockPostingBranchSequences.Add(new StockPostingBranchSequence
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LastSequence = 1,
            UpdatedAtUtc = DateTime.UtcNow
        });
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
            OnHandBaseQty = 5m, InventoryValue = 10m,
            CurrentUnitCost = 2m, AverageUnitCost = 2m
        });
        await db.SaveChangesAsync();
        var history = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 30,
            TrxLineNo = 1, TrxDtTime = issueAt, TrxType = "MI",
            BatchStatus = "POSTED", ICode = "RM1",
            FromBalLocId = balance.Id, FrStdQty = 5m,
            LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = owner.Id, PostingLineNo = 1,
            DocumentRevision = 1, EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        db.IvTrxHistories.Add(history);
        await db.SaveChangesAsync();
        db.StockValuationFacts.AddRange(
            new StockValuationFact
            {
                CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId,
                StockPostingId = owner.Id, PostingLineNo = 1,
                InventoryHistoryId = history.Id, EffectiveAt = issueAt,
                BusinessDate = issueAt.Date, PeriodKey = "2026-10",
                ItemCode = "RM1", CostMethod = StockCostMethods.MovingAverage,
                ValuationSource = StockValuationSources.MovingAverage,
                ValuationStatus = StockValuationStatuses.Valued,
                Direction = -1, BaseQty = 2m, CostAmount = 4m,
                BaseCostAmount = 4m
            },
            new StockValuationFact
            {
                CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId,
                StockPostingId = owner.Id, PostingLineNo = 1, SplitOrdinal = 1,
                InventoryHistoryId = history.Id, EffectiveAt = issueAt,
                BusinessDate = issueAt.Date, PeriodKey = "2026-10",
                ItemCode = "RM1", CostMethod = StockCostMethods.MovingAverage,
                ValuationSource = StockValuationSources.MovingAverage,
                ValuationStatus = StockValuationStatuses.Valued,
                Direction = -1, BaseQty = 3m, CostAmount = 6m,
                BaseCostAmount = 6m
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var key = new StockReplayEventKey(issueAt,
            StockReplayEventKey.OrdinaryClass, 1, $"H{history.Id:D10}", 0);
        var active = new StockReplayWacEvent(history.Id, key,
            -5m, -10m, -5m);
        var corrected = new StockReplayWacStep(history.Id,
            10m, 25m, -5m, -12.5m, 5m, 12.5m);
        var physicalRemoval = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            db, "DEMO", "HQ", balance.Id,
            new StockReplayPhysicalEvent(history.Id, key, -5m));
        var financialRemoval = await StockReplayWacRemovalPlanner.BuildAsync(
            db, "DEMO", "HQ", "RM1", physicalRemoval);
        Assert.Equal(10m, financialRemoval.Projection.FinalQuantity);
        Assert.Equal(20m, financialRemoval.Projection.FinalValue);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = CreateCoordinator();
        var begin = await coordinator.BeginInTransactionAsync(db,
            Command(Guid.NewGuid()) with { EffectiveAt = issueAt });
        Assert.NotNull(begin.Context);
        await StockReplayWacRemovalPlanner.ValidateWithinRootAsync(
            begin.Context!, financialRemoval);
        var changedCost = await db.StockCostStates.SingleAsync();
        changedCost.InventoryValue = 11m;
        await db.SaveChangesAsync();
        var staleRemoval = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayWacRemovalPlanner.ValidateWithinRootAsync(
                begin.Context!, financialRemoval));
        Assert.Equal(StockLedgerErrorCodes.StaleDocument, staleRemoval.Error.Code);
        changedCost.InventoryValue = 10m;
        await db.SaveChangesAsync();
        await StockReplayWacRemovalPlanner.ValidateWithinRootAsync(
            begin.Context!, financialRemoval);
        var wrong = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayWacIssueRepricingWriter.AppendAsync(begin.Context!,
                history.Id, active, corrected with { SignedValue = -12m }, 10, 11));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, wrong.Error.Code);
        var replacements = await StockReplayWacIssueRepricingWriter.AppendAsync(
            begin.Context!, history.Id, active, corrected, 10, 11);
        Assert.Equal(2, replacements.Count);
        Assert.Equal(new[] { 5m, 7.5m }, replacements.Select(f => f.CostAmount));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var activeGeneration = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, "DEMO", "HQ", [history]);
        Assert.Equal(2, activeGeneration[history.Id].Count);
        Assert.Equal(12.5m, activeGeneration[history.Id].Sum(f => f.CostAmount));
        Assert.Equal(2, await db.StockValuationFacts.CountAsync(f =>
            f.StockPostingId == begin.Context!.Posting.Id
            && f.ReversesValuationFactId != null));
        await coordinator.AbortInTransactionAsync(begin.Context!);
        await transaction.RollbackAsync();
        db.ChangeTracker.Clear();

        var candidateAt = issueAt.AddDays(-1);
        var candidatePhysical = new StockReplayPhysicalEvent(1000,
            new StockReplayEventKey(candidateAt,
                StockReplayEventKey.OrdinaryClass, 2, "H0000001000", 0), 5m);
        var physicalInsertion = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            db, "DEMO", "HQ", balance.Id, candidatePhysical);
        var financialInsertion = await StockReplayWacInsertionPlanner.BuildAsync(
            db, "DEMO", "HQ", "RM1", physicalInsertion,
            new StockReplayWacEvent(1000, candidatePhysical.Key,
                0m, 0m, 5m, FixedSignedValue: 20m));
        await using var replayTransaction = await db.Database.BeginTransactionAsync();
        var replayCoordinator = CreateCoordinator();
        var replayBegin = await replayCoordinator.BeginInTransactionAsync(db,
            Command(Guid.NewGuid()) with { EffectiveAt = candidateAt });
        Assert.NotNull(replayBegin.Context);
        var replayedTail = await StockReplayWacTailRepricingWriter
            .AppendInsertionTailAsync(replayBegin.Context!, financialInsertion, 10);
        Assert.Equal(new[] { history.Id }, replayedTail.RepricedHistoryIds);
        var replayBalance = await db.IvBalLocs.SingleAsync(b => b.Id == balance.Id);
        replayBalance.StdQty = 10m;
        var lateReceipt = new IvTrxHistory
        {
            Id = 1000, CompanyCode = "DEMO", BranchCode = "HQ",
            BatchNo = 90, TrxLineNo = 1, TrxDtTime = candidateAt,
            TrxType = "MR", BatchStatus = "POSTED", ICode = "RM1",
            ToBalLocId = balance.Id, ToStdQty = 5m,
            LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = replayBegin.Context!.Posting.Id,
            PostingLineNo = 1, DocumentRevision = 1,
            EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        db.IvTrxHistories.Add(lateReceipt);
        db.StockValuationFacts.Add(new StockValuationFact
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId,
            StockPostingId = replayBegin.Context!.Posting.Id,
            PostingLineNo = 1, InventoryHistory = lateReceipt,
            EffectiveAt = candidateAt, BusinessDate = candidateAt.Date,
            PeriodKey = "2026-10", ItemCode = "RM1",
            CostMethod = StockCostMethods.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            Direction = 1, BaseQty = 5m, CostAmount = 20m,
            BaseCostAmount = 20m
        });
        await StockReplayWacCostStateWriter.ApplyAsync(
            replayBegin.Context!, replayedTail.Permit);
        var replayState = await db.StockCostStates.SingleAsync();
        Assert.Equal(10m, replayState.OnHandBaseQty);
        Assert.Equal(26.666665m, replayState.InventoryValue);
        Assert.Equal(2.666667m, replayState.AverageUnitCost);
        replayBegin.Context!.Posting.SealedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var insertedFinancialTimeline = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, "DEMO", "HQ", "RM1",
            new StockReplayEventKey(candidateAt.AddTicks(-1),
                StockReplayEventKey.OrdinaryClass, 0, "", 0));
        Assert.Equal(2, insertedFinancialTimeline.LaterEvents.Count);
        Assert.Equal(1000, insertedFinancialTimeline.LaterEvents[0].EventId);
        Assert.Equal(20m, insertedFinancialTimeline.LaterEvents[0].OldSignedValue);
        Assert.Equal(history.Id, insertedFinancialTimeline.LaterEvents[1].EventId);
        Assert.Equal(-13.333335m,
            insertedFinancialTimeline.LaterEvents[1].OldSignedValue);
        await replayCoordinator.AbortInTransactionAsync(replayBegin.Context!);
        await replayTransaction.RollbackAsync();
        db.ChangeTracker.Clear();

        // Removing an earlier receipt leaves the later issue physically intact,
        // but changes its WAC value. The rollback tail must reprice that issue.
        var priorOwner = Posting("DEMO", "HQ", epochId, 2, Guid.NewGuid());
        priorOwner.EffectiveAt = candidateAt;
        priorOwner.BusinessDate = candidateAt.Date;
        db.StockPostings.Add(priorOwner);
        var branchSequence = await db.StockPostingBranchSequences.SingleAsync();
        branchSequence.LastSequence = 2;
        await db.SaveChangesAsync();
        var priorReceipt = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 91,
            TrxLineNo = 1, TrxDtTime = candidateAt, TrxType = "MR",
            BatchStatus = "POSTED", ICode = "RM1",
            ToBalLocId = balance.Id, ToStdQty = 5m,
            LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = priorOwner.Id, PostingLineNo = 1,
            DocumentRevision = 1, EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        db.IvTrxHistories.Add(priorReceipt);
        db.StockValuationFacts.Add(new StockValuationFact
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId,
            StockPosting = priorOwner, PostingLineNo = 1,
            InventoryHistory = priorReceipt,
            EffectiveAt = candidateAt, BusinessDate = candidateAt.Date,
            PeriodKey = "2026-10", ItemCode = "RM1",
            CostMethod = StockCostMethods.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            Direction = 1, BaseQty = 5m, CostAmount = 15m,
            BaseCostAmount = 15m
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var priorKey = new StockReplayEventKey(candidateAt,
            StockReplayEventKey.OrdinaryClass, 2,
            $"H{priorReceipt.Id:D10}", 0);
        var priorPhysicalRemoval = await StockReplayPhysicalPlanner.BuildRemovalAsync(
            db, "DEMO", "HQ", balance.Id,
            new StockReplayPhysicalEvent(priorReceipt.Id, priorKey, 5m));
        var priorFinancialRemoval = await StockReplayWacRemovalPlanner.BuildAsync(
            db, "DEMO", "HQ", "RM1", priorPhysicalRemoval);
        Assert.Equal(0m, priorFinancialRemoval.Projection.FinalQuantity);
        Assert.Equal(0m, priorFinancialRemoval.Projection.FinalValue);
        await using var removalTransaction = await db.Database.BeginTransactionAsync();
        var removalCoordinator = CreateCoordinator();
        var removalBegin = await removalCoordinator.BeginInTransactionAsync(db,
            Command(Guid.NewGuid()) with { EffectiveAt = candidateAt });
        Assert.NotNull(removalBegin.Context);
        var removedTail = await StockReplayWacTailRepricingWriter
            .AppendRemovalTailAsync(removalBegin.Context!, priorFinancialRemoval, 20);
        Assert.Equal(new[] { history.Id }, removedTail.RepricedHistoryIds);
        var reversalHistory = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 92,
            TrxLineNo = 1, TrxDtTime = candidateAt, TrxType = "MR",
            BatchStatus = "POSTED", ICode = "RM1",
            FromBalLocId = balance.Id, FrStdQty = 5m,
            LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = removalBegin.Context!.Posting.Id,
            PostingLineNo = 22, DocumentRevision = 1,
            EntryRole = "REVERSAL", ReversesHistoryId = priorReceipt.Id,
            ValuationStatus = StockValuationStatuses.Reversed
        };
        db.IvTrxHistories.Add(reversalHistory);
        var removalBalance = await db.IvBalLocs.SingleAsync(b => b.Id == balance.Id);
        removalBalance.StdQty = 0m;
        await db.SaveChangesAsync();
        var originalReceiptFact = await db.StockValuationFacts.SingleAsync(f =>
            f.InventoryHistoryId == priorReceipt.Id);
        await StockReplayValuationReversalWriter.AppendAsync(
            removalBegin.Context!, priorReceipt.Id,
            [new StockReplayValuationReversalLine(
                originalReceiptFact.Id, 22, 0, reversalHistory.Id)]);
        await StockReplayWacCostStateWriter.ApplyAsync(
            removalBegin.Context!, removedTail.Permit);
        var removalState = await db.StockCostStates.SingleAsync();
        Assert.Equal(0m, removalState.OnHandBaseQty);
        Assert.Equal(0m, removalState.InventoryValue);
        removalBegin.Context!.Posting.SealedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var removedFinancialTimeline = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, "DEMO", "HQ", "RM1",
            new StockReplayEventKey(candidateAt.AddTicks(-1),
                StockReplayEventKey.OrdinaryClass, 0, "", 0));
        Assert.Single(removedFinancialTimeline.LaterEvents);
        Assert.Equal(history.Id, removedFinancialTimeline.LaterEvents[0].EventId);
        Assert.Equal(-5m, removedFinancialTimeline.LaterEvents[0].OldSignedValue);
        Assert.Equal(-5m, (await StockReplayValuationGenerationResolver.ResolveAsync(
            db, "DEMO", "HQ", [history]))[history.Id]
            .Sum(f => f.SignedValue));
        await removalCoordinator.AbortInTransactionAsync(removalBegin.Context!);
        await removalTransaction.RollbackAsync();
    }

    [Theory]
    [InlineData(1, 1, 2.25, 11.25)]
    [InlineData(2, 0, 0, 12)]
    [InlineData(3, -1, -2.5, 12.5)]
    public async Task Wac_tail_resolves_financial_only_count_repricing_to_original_count_anchor(
        int candidateQty, int correctedCountQty, double correctedCountValue,
        double finalInventoryValue)
    {
        await ActivateAsync();
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        var epochId = await db.StockLedgerEpochs.Select(e => e.Id).SingleAsync();
        var countDate = new DateTime(2026, 10, 3);
        var countAt = StockReplayEventKey.CountEndOfDay(countDate);
        var header = new IvStockCountHdr
        {
            CompanyCode = "DEMO", BranchCode = "HQ", CountNo = "CC-WAC",
            CountDate = countDate, Status = IvStockCountStatuses.Posted
        };
        db.IvStockCountHdrs.Add(header);
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
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = balance.Id,
            ICode = "RM1", IStatus = "ACTIVE", WHCode = "MAIN",
            LocCode = "", LotNo = "", PhysicalQty = 5m,
            StdUom = "EA"
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        var basis = new StockCountCostBasis
        {
            CompanyCode = "DEMO", BranchCode = "HQ", StockCountLineId = line.Id,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            FrozenFallbackUnitCost = 2m,
            CostOverrideReason = "Approved count cost",
            CostApprovedBy = "TEST", CostApprovedAtUtc = DateTime.UtcNow
        };
        db.StockCountCostBases.Add(basis);
        var anchor = Posting("DEMO", "HQ", epochId, 1, Guid.NewGuid());
        anchor.SourceModule = "INVENTORY";
        anchor.SourceDocumentType = "IV_STOCK_COUNT";
        anchor.SourceDocumentId = header.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        anchor.SourceDocumentNo = header.CountNo;
        anchor.EffectiveAt = countAt;
        anchor.BusinessDate = countDate;
        var correction = Posting("DEMO", "HQ", epochId, 2, Guid.NewGuid());
        correction.EffectiveAt = countAt.AddDays(1);
        correction.BusinessDate = correction.EffectiveAt.Date;
        db.StockPostings.AddRange(anchor, correction);
        await db.SaveChangesAsync();
        var history = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 41,
            TrxLineNo = 1, TrxDtTime = countDate, TrxType = "ADJ",
            BatchStatus = "POSTED", RefNo = header.CountNo,
            ICode = "RM1", ToBalLocId = balance.Id, ToStdQty = 2m,
            StockCountCostBasisId = basis.Id,
            LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = anchor.Id, PostingLineNo = 1,
            DocumentRevision = 0, EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        db.IvTrxHistories.Add(history);
        await db.SaveChangesAsync();
        db.StockCountReplayGenerations.Add(new StockCountReplayGeneration
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            StockCountLineId = line.Id, AnchorPostingId = anchor.Id,
            WritePostingId = anchor.Id, AnchorPostingSequence = 1,
            EffectiveAt = countAt, SignedAdjustmentQty = 2m,
            AdjustmentBatchNo = 41, AdjustmentHistoryId = history.Id,
            CreatedAtUtc = DateTime.UtcNow
        });
        var original = new StockValuationFact
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId,
            StockPostingId = anchor.Id, PostingLineNo = 1,
            InventoryHistoryId = history.Id, EffectiveAt = countAt,
            BusinessDate = countDate, PeriodKey = "2026-10", ItemCode = "RM1",
            CostMethod = StockCostMethods.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            Direction = 1, BaseQty = 2m, CostAmount = 4m,
            BaseCostAmount = 4m
        };
        db.StockValuationFacts.Add(original);
        await db.SaveChangesAsync();
        db.StockValuationFacts.AddRange(
            new StockValuationFact
            {
                CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId,
                StockPostingId = correction.Id, PostingLineNo = 1,
                InventoryHistoryId = history.Id, EffectiveAt = countAt,
                BusinessDate = countDate, PeriodKey = "2026-10", ItemCode = "RM1",
                CostMethod = StockCostMethods.MovingAverage,
                ValuationStatus = StockValuationStatuses.Reversed,
                Direction = -1, BaseQty = 2m, CostAmount = 4m,
                BaseCostAmount = 4m,
                ReversesValuationFactId = original.Id,
                OriginalValuationFactId = original.Id
            },
            new StockValuationFact
            {
                CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId,
                StockPostingId = correction.Id, PostingLineNo = 2,
                InventoryHistoryId = history.Id, EffectiveAt = countAt,
                BusinessDate = countDate, PeriodKey = "2026-10", ItemCode = "RM1",
                CostMethod = StockCostMethods.MovingAverage,
                ValuationStatus = StockValuationStatuses.Valued,
                Direction = 1, BaseQty = 2m, CostAmount = 5m,
                BaseCostAmount = 5m,
                OriginalValuationFactId = original.Id
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var before = new StockReplayEventKey(countDate.AddDays(-1),
            StockReplayEventKey.OrdinaryClass, 1, "before", 0);
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, "DEMO", "HQ", "RM1", before);
        var activeCount = Assert.Single(tail.LaterEvents);
        Assert.Equal(2m, activeCount.OldSignedQuantity);
        Assert.Equal(5m, activeCount.OldSignedValue);
        Assert.Equal(countAt, activeCount.Key.EffectiveAt);
        Assert.Equal(anchor.PostingSequence,
            activeCount.Key.OriginalPostingSequence);

        db.StockPostingBranchSequences.Add(new StockPostingBranchSequence
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LastSequence = 2,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = CreateCoordinator();
        var begin = await coordinator.BeginInTransactionAsync(db,
            Command(Guid.NewGuid()) with { EffectiveAt = countAt });
        Assert.NotNull(begin.Context);
        var projected = new StockReplayWacStep(activeCount.EventId,
            3m, 9m, 2m, 6m, 5m, 15m);
        var wrong = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayWacCountRepricingWriter.AppendAsync(begin.Context!,
                line.Id, activeCount, projected with { SignedValue = 7m }, 10, 11));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, wrong.Error.Code);
        var impossiblePile = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayWacCountRepricingWriter.AppendAsync(begin.Context!,
                line.Id, activeCount,
                new StockReplayWacStep(activeCount.EventId,
                    2m, 6m, 2m, 6m, 4m, 12m), 10, 11));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, impossiblePile.Error.Code);
        var replacement = await StockReplayWacCountRepricingWriter.AppendAsync(
            begin.Context!, line.Id, activeCount, projected, 10, 11);
        Assert.NotNull(replacement);
        Assert.Equal(original.Id, replacement!.OriginalValuationFactId);
        Assert.Equal(6m, replacement.CostAmount);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var activeGeneration = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, "DEMO", "HQ", [history]);
        Assert.Equal(6m, Assert.Single(activeGeneration[history.Id]).CostAmount);
        await coordinator.AbortInTransactionAsync(begin.Context!);
        await transaction.RollbackAsync();
        db.ChangeTracker.Clear();

        var candidateAt = countDate.AddDays(-1).AddHours(9);
        var candidate = new StockReplayPhysicalEvent(1000,
            new StockReplayEventKey(candidateAt,
                StockReplayEventKey.OrdinaryClass, 3, "H0000001000", 0), candidateQty);
        var physicalPlan = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            db, "DEMO", "HQ", balance.Id, candidate);
        var wacPlan = await StockReplayWacInsertionPlanner.BuildAsync(
            db, "DEMO", "HQ", "RM1", physicalPlan,
            new StockReplayWacEvent(1000, candidate.Key, 0m, 0m, candidateQty,
                FixedSignedValue: candidateQty * 3m));
        var changedCount = Assert.Single(wacPlan.Projection.LaterSteps);
        Assert.Equal(correctedCountQty, changedCount.SignedQuantity);
        Assert.Equal((decimal)correctedCountValue, changedCount.SignedValue);
        await using var changedTransaction = await db.Database.BeginTransactionAsync();
        var changedCoordinator = CreateCoordinator(withValuation: true);
        var changedBegin = await changedCoordinator.BeginInTransactionAsync(db,
            Command(Guid.NewGuid()) with { EffectiveAt = candidateAt });
        Assert.NotNull(changedBegin.Context);
        var root = changedBegin.Context!;
        var wacPermit = await StockReplayWacInsertionPlanner
            .PrepareWithinRootAsync(root, wacPlan);
        var countPermit = await StockReplayPhysicalPlanner
            .PrepareCountCorrectionsWithinRootAsync(root, physicalPlan);
        Assert.Equal(correctedCountQty,
            Assert.Single(countPermit.Lines).NewSignedAdjustmentQty);

        var reversalBatch = new IvTrxBatch
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 960,
            TrxDtTime = countDate, TrxType = "ADJ", BatchStatus = "POSTED",
            RefNo = header.CountNo
        };
        var replacementBatch = new IvTrxBatch
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 961,
            TrxDtTime = countDate, TrxType = "ADJ", BatchStatus = "POSTED",
            RefNo = header.CountNo
        };
        db.IvTrxBatches.Add(reversalBatch);
        if (correctedCountQty != 0)
            db.IvTrxBatches.Add(replacementBatch);
        await db.SaveChangesAsync();
        db.IvTrxBatchDetails.Add(new IvTrxBatchDetail
        {
            BatchId = reversalBatch.Id, CompanyCode = "DEMO",
            BranchCode = "HQ", BatchNo = 960, TrxLineNo = 1,
            DocumentRevision = root.Posting.DocumentRevision,
            TrxType = "ADJ", ICode = "RM1",
            FromBalLocId = balance.Id, FrStdQty = 2m,
            StockCountCostBasisId = basis.Id
        });
        if (correctedCountQty != 0)
            db.IvTrxBatchDetails.Add(new IvTrxBatchDetail
            {
                BatchId = replacementBatch.Id, CompanyCode = "DEMO",
                BranchCode = "HQ", BatchNo = 961, TrxLineNo = 1,
                DocumentRevision = root.Posting.DocumentRevision,
                TrxType = "ADJ", ICode = "RM1",
                FromBalLocId = correctedCountQty < 0 ? balance.Id : null,
                FrStdQty = correctedCountQty < 0 ? -correctedCountQty : null,
                ToBalLocId = correctedCountQty > 0 ? balance.Id : null,
                ToStdQty = correctedCountQty > 0 ? correctedCountQty : null,
                StockCountCostBasisId = correctedCountQty > 0 ? basis.Id : null
            });
        var candidateHistory = new IvTrxHistory
        {
            Id = 1000, CompanyCode = "DEMO", BranchCode = "HQ",
            BatchNo = 959, TrxLineNo = 1, TrxDtTime = candidateAt,
            TrxType = "MR", BatchStatus = "POSTED", ICode = "RM1",
            ToBalLocId = balance.Id, ToStdQty = candidateQty,
            LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = root.Posting.Id, PostingLineNo = 1,
            DocumentRevision = root.Posting.DocumentRevision,
            EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        var reversalHistory = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 960,
            TrxLineNo = 1, TrxDtTime = countDate, TrxType = "ADJ",
            BatchStatus = "POSTED", RefNo = header.CountNo,
            ICode = "RM1", FromBalLocId = balance.Id, FrStdQty = 2m,
            StockCountCostBasisId = basis.Id,
            LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = root.Posting.Id, PostingLineNo = 2,
            DocumentRevision = root.Posting.DocumentRevision,
            EntryRole = "REVERSAL", ReversesHistoryId = history.Id
        };
        var replacementHistory = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 961,
            TrxLineNo = 1, TrxDtTime = countDate, TrxType = "ADJ",
            BatchStatus = "POSTED", RefNo = header.CountNo,
            ICode = "RM1",
            FromBalLocId = correctedCountQty < 0 ? balance.Id : null,
            FrStdQty = correctedCountQty < 0 ? -correctedCountQty : null,
            FrStdUom = correctedCountQty < 0 ? "EA" : null,
            ToBalLocId = correctedCountQty > 0 ? balance.Id : null,
            ToStdQty = correctedCountQty > 0 ? correctedCountQty : null,
            ToStdUom = correctedCountQty > 0 ? "EA" : null,
            StockCountCostBasisId = correctedCountQty > 0 ? basis.Id : null,
            LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = root.Posting.Id, PostingLineNo = 3,
            DocumentRevision = root.Posting.DocumentRevision,
            EntryRole = "ORIGINAL"
        };
        db.IvTrxHistories.AddRange(candidateHistory, reversalHistory);
        if (correctedCountQty != 0)
            db.IvTrxHistories.Add(replacementHistory);
        db.StockValuationFacts.Add(new StockValuationFact
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId,
            StockPostingId = root.Posting.Id, PostingLineNo = 1,
            InventoryHistory = candidateHistory,
            EffectiveAt = candidateAt, BusinessDate = candidateAt.Date,
            PeriodKey = "2026-10", ItemCode = "RM1",
            CostMethod = StockCostMethods.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            Direction = 1, BaseQty = candidateQty,
            CostAmount = candidateQty * 3m,
            BaseCostAmount = candidateQty * 3m
        });
        await db.SaveChangesAsync();
        if (candidateQty == 1)
        {
            reversalHistory.FrWarehouse = "OTHER";
            await db.SaveChangesAsync();
            var wrongSlice = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockCountCorrectionGenerationWriter.AppendAsync(root,
                    countPermit,
                    [new StockCountCorrectionHistories(line.Id,
                        reversalHistory.Id, replacementHistory.Id)]));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                wrongSlice.Error.Code);
            reversalHistory.FrWarehouse = null;
            await db.SaveChangesAsync();
            var replacementDetail = await db.IvTrxBatchDetails.SingleAsync(d =>
                d.BatchNo == 961);
            replacementDetail.StockCountCostBasisId = null;
            replacementHistory.StockCountCostBasisId = null;
            await db.SaveChangesAsync();
            var missingBasis = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockCountCorrectionGenerationWriter.AppendAsync(root,
                    countPermit,
                    [new StockCountCorrectionHistories(line.Id,
                        reversalHistory.Id, replacementHistory.Id)]));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                missingBasis.Error.Code);
            replacementDetail.StockCountCostBasisId = basis.Id;
            replacementHistory.StockCountCostBasisId = basis.Id;
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE StockCountCostBasis SET BranchCode = {"XX"} WHERE Id = {basis.Id}");
            var foreignBasis = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockCountCorrectionGenerationWriter.AppendAsync(root,
                    countPermit,
                    [new StockCountCorrectionHistories(line.Id,
                        reversalHistory.Id, replacementHistory.Id)]));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                foreignBasis.Error.Code);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE StockCountCostBasis SET BranchCode = {"HQ"} WHERE Id = {basis.Id}");
        }
        var generations = await StockCountCorrectionGenerationWriter.AppendAsync(
            root, countPermit,
            [new StockCountCorrectionHistories(line.Id,
                reversalHistory.Id,
                correctedCountQty == 0 ? null : replacementHistory.Id)]);
        Assert.Equal(correctedCountQty,
            Assert.Single(generations).SignedAdjustmentQty);
        var valuedTail = await StockReplayWacTailRepricingWriter
            .AppendPreparedInsertionTailAsync(root, wacPermit,
                wacPlan, countPermit, 2);
        Assert.Equal(new[] { line.Id }, valuedTail.RepricedCountLineIds);
        var reversalFact = db.ChangeTracker.Entries<StockValuationFact>()
            .Single(e => e.Entity.StockPostingId == root.Posting.Id
                && e.Entity.PostingLineNo == 2).Entity;
        Assert.Equal(5m, reversalFact.CostAmount);
        var replacementFacts = db.ChangeTracker.Entries<StockValuationFact>()
            .Where(e => e.Entity.StockPostingId == root.Posting.Id
                && e.Entity.PostingLineNo == 3).Select(e => e.Entity).ToArray();
        if (correctedCountQty == 0)
            Assert.Empty(replacementFacts);
        else
        {
            var replacementFact = Assert.Single(replacementFacts);
            Assert.Equal(Math.Abs((decimal)correctedCountValue),
                replacementFact.CostAmount);
            Assert.Equal(Math.Sign(correctedCountQty),
                replacementFact.Direction);
        }
        await StockReplayWacCostStateWriter.ApplyAsync(root, wacPermit);
        Assert.Equal((decimal)finalInventoryValue,
            (await db.StockCostStates.SingleAsync()).InventoryValue);
        var requiredHistoryIds = correctedCountQty == 0
            ? new[] { candidateHistory.Id }
            : new[] { candidateHistory.Id, replacementHistory.Id };
        await changedCoordinator.CompleteReplayInTransactionAsync(root,
            requiredHistoryIds);
        db.ChangeTracker.Clear();
        var correctedTimeline = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, "DEMO", "HQ", "RM1",
            new StockReplayEventKey(candidateAt.AddTicks(-1),
                StockReplayEventKey.OrdinaryClass, 0, "", 0));
        Assert.Equal(2, correctedTimeline.LaterEvents.Count);
        Assert.Equal(-generations[0].Id, correctedTimeline.LaterEvents[1].EventId);
        Assert.Equal(correctedCountQty,
            correctedTimeline.LaterEvents[1].OldSignedQuantity);
        Assert.Equal((decimal)correctedCountValue,
            correctedTimeline.LaterEvents[1].OldSignedValue);
        await changedCoordinator.AbortInTransactionAsync(root);
        await changedTransaction.RollbackAsync();
        db.ChangeTracker.Clear();

        await using var generatedTransaction = await db.Database.BeginTransactionAsync();
        var generatedCoordinator = CreateCoordinator(withValuation: true);
        var generatedBegin = await generatedCoordinator.BeginInTransactionAsync(db,
            Command(Guid.NewGuid()) with { EffectiveAt = candidateAt });
        Assert.NotNull(generatedBegin.Context);
        var generatedRoot = generatedBegin.Context!;
        var generatedWacPermit = await StockReplayWacInsertionPlanner
            .PrepareWithinRootAsync(generatedRoot, wacPlan);
        var generatedCountPermit = await StockReplayPhysicalPlanner
            .PrepareCountCorrectionsWithinRootAsync(generatedRoot, physicalPlan);
        var generatedCandidateHistory = new IvTrxHistory
        {
            Id = 1000, CompanyCode = "DEMO", BranchCode = "HQ",
            BatchNo = 959, TrxLineNo = 1, TrxDtTime = candidateAt,
            TrxType = "MR", BatchStatus = "POSTED", ICode = "RM1",
            ToBalLocId = balance.Id, ToStdQty = candidateQty,
            LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = generatedRoot.Posting.Id, PostingLineNo = 1,
            DocumentRevision = generatedRoot.Posting.DocumentRevision,
            EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        db.IvTrxHistories.Add(generatedCandidateHistory);
        db.StockValuationFacts.Add(new StockValuationFact
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId,
            StockPostingId = generatedRoot.Posting.Id, PostingLineNo = 1,
            InventoryHistory = generatedCandidateHistory,
            EffectiveAt = candidateAt, BusinessDate = candidateAt.Date,
            PeriodKey = "2026-10", ItemCode = "RM1",
            CostMethod = StockCostMethods.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            Direction = 1, BaseQty = candidateQty,
            CostAmount = candidateQty * 3m,
            BaseCostAmount = candidateQty * 3m
        });
        await db.SaveChangesAsync();
        var generated = await StockCountCorrectionHistoryWriter.AppendAsync(
            generatedRoot, generatedCountPermit,
            [new StockCountCorrectionPostingLines(line.Id, 2, 3)],
            new RunningNumberService());
        Assert.Equal(correctedCountQty,
            Assert.Single(generated).SignedAdjustmentQty);
        await StockReplayWacTailRepricingWriter.AppendPreparedInsertionTailAsync(
            generatedRoot, generatedWacPermit, wacPlan,
            generatedCountPermit, 2);
        await StockReplayWacCostStateWriter.ApplyAsync(
            generatedRoot, generatedWacPermit);
        var generatedRequired = generated[0].AdjustmentHistoryId is int adjustedId
            ? new[] { generatedCandidateHistory.Id, adjustedId }
            : new[] { generatedCandidateHistory.Id };
        await generatedCoordinator.CompleteReplayInTransactionAsync(
            generatedRoot, generatedRequired);
        db.ChangeTracker.Clear();
        var generatedTimeline = await StockReplayWacFinancialTailReader
            .ReadLaterAsync(db, "DEMO", "HQ", "RM1",
                new StockReplayEventKey(candidateAt.AddTicks(-1),
                    StockReplayEventKey.OrdinaryClass, 0, "", 0));
        Assert.Equal(-generated[0].Id,
            generatedTimeline.LaterEvents[1].EventId);
        Assert.Equal(correctedCountQty,
            generatedTimeline.LaterEvents[1].OldSignedQuantity);
        await generatedCoordinator.AbortInTransactionAsync(generatedRoot);
        await generatedTransaction.RollbackAsync();
    }

    [Fact]
    public async Task Wac_count_zero_adjustment_can_become_positive_after_late_issue()
    {
        await ActivateAsync();
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        var epochId = await db.StockLedgerEpochs.Select(e => e.Id).SingleAsync();
        var countDate = new DateTime(2026, 10, 3);
        var countAt = StockReplayEventKey.CountEndOfDay(countDate);
        var candidateAt = countDate.AddDays(-1).AddHours(9);
        var header = new IvStockCountHdr
        {
            CompanyCode = "DEMO", BranchCode = "HQ", CountNo = "CC-ZERO",
            CountDate = countDate, Status = IvStockCountStatuses.Posted
        };
        db.IvStockCountHdrs.Add(header);
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO", ICode = "RM1"
        });
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
            OnHandBaseQty = 5m, InventoryValue = 10m,
            CurrentUnitCost = 2m, AverageUnitCost = 2m
        });
        await db.SaveChangesAsync();
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = balance.Id,
            ICode = "RM1", IStatus = "ACTIVE", WHCode = "MAIN",
            LocCode = "", LotNo = "", PhysicalQty = 5m,
            StdUom = "EA"
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        var basis = new StockCountCostBasis
        {
            CompanyCode = "DEMO", BranchCode = "HQ", StockCountLineId = line.Id,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            FrozenFallbackUnitCost = 2m,
            CostOverrideReason = "Approved count cost",
            CostApprovedBy = "TEST", CostApprovedAtUtc = DateTime.UtcNow
        };
        db.StockCountCostBases.Add(basis);
        var anchor = Posting("DEMO", "HQ", epochId, 1, Guid.NewGuid());
        anchor.SourceModule = "INVENTORY";
        anchor.SourceDocumentType = "IV_STOCK_COUNT";
        anchor.SourceDocumentId = header.Id.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        anchor.SourceDocumentNo = header.CountNo;
        anchor.EffectiveAt = countAt;
        anchor.BusinessDate = countDate;
        db.StockPostings.Add(anchor);
        await db.SaveChangesAsync();
        db.StockCountReplayGenerations.Add(new StockCountReplayGeneration
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            StockCountLineId = line.Id, AnchorPostingId = anchor.Id,
            WritePostingId = anchor.Id, AnchorPostingSequence = 1,
            EffectiveAt = countAt, SignedAdjustmentQty = 0m,
            CreatedAtUtc = DateTime.UtcNow
        });
        db.StockPostingBranchSequences.Add(new StockPostingBranchSequence
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LastSequence = 1,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var candidate = new StockReplayPhysicalEvent(1000,
            new StockReplayEventKey(candidateAt,
                StockReplayEventKey.OrdinaryClass, 2, "H0000001000", 0), -1m);
        var physicalPlan = await StockReplayPhysicalPlanner.BuildInsertionAsync(
            db, "DEMO", "HQ", balance.Id, candidate);
        var wacPlan = await StockReplayWacInsertionPlanner.BuildAsync(
            db, "DEMO", "HQ", "RM1", physicalPlan,
            new StockReplayWacEvent(1000, candidate.Key, 0m, 0m, -1m));
        Assert.Equal(1m,
            Assert.Single(wacPlan.Projection.LaterSteps).SignedQuantity);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = CreateCoordinator(withValuation: true);
        var begin = await coordinator.BeginInTransactionAsync(db,
            Command(Guid.NewGuid()) with { EffectiveAt = candidateAt });
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        var wacPermit = await StockReplayWacInsertionPlanner
            .PrepareWithinRootAsync(root, wacPlan);
        var countPermit = await StockReplayPhysicalPlanner
            .PrepareCountCorrectionsWithinRootAsync(root, physicalPlan);
        Assert.Equal(1m, Assert.Single(countPermit.Lines).NewSignedAdjustmentQty);

        var candidateHistory = new IvTrxHistory
        {
            Id = 1000, CompanyCode = "DEMO", BranchCode = "HQ",
            BatchNo = 958, TrxLineNo = 1, TrxDtTime = candidateAt,
            TrxType = "MI", BatchStatus = "POSTED", ICode = "RM1",
            FromBalLocId = balance.Id, FrStdQty = 1m,
            LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = root.Posting.Id, PostingLineNo = 1,
            DocumentRevision = root.Posting.DocumentRevision,
            EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        db.IvTrxHistories.Add(candidateHistory);
        db.StockValuationFacts.Add(new StockValuationFact
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId,
            StockPostingId = root.Posting.Id, PostingLineNo = 1,
            InventoryHistory = candidateHistory,
            EffectiveAt = candidateAt, BusinessDate = candidateAt.Date,
            PeriodKey = "2026-10", ItemCode = "RM1",
            CostMethod = StockCostMethods.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            Direction = -1, BaseQty = 1m, CostAmount = 2m,
            BaseCostAmount = 2m
        });
        await db.SaveChangesAsync();
        var generations = await StockCountCorrectionHistoryWriter.AppendAsync(
            root, countPermit,
            [new StockCountCorrectionPostingLines(line.Id, 2, 3)],
            new RunningNumberService());
        var replacementHistory = await db.IvTrxHistories.SingleAsync(h =>
            h.Id == Assert.Single(generations).AdjustmentHistoryId);
        var valuedTail = await StockReplayWacTailRepricingWriter
            .AppendPreparedInsertionTailAsync(root, wacPermit,
                wacPlan, countPermit, 2);
        Assert.Equal(new[] { line.Id }, valuedTail.RepricedCountLineIds);
        Assert.DoesNotContain(db.ChangeTracker.Entries<StockValuationFact>(),
            e => e.Entity.StockPostingId == root.Posting.Id
                && e.Entity.PostingLineNo == 2);
        var replacementFact = db.ChangeTracker.Entries<StockValuationFact>()
            .Single(e => e.Entity.StockPostingId == root.Posting.Id
                && e.Entity.PostingLineNo == 3).Entity;
        Assert.Equal(2m, replacementFact.CostAmount);
        await StockReplayWacCostStateWriter.ApplyAsync(root, wacPermit);
        Assert.Equal(10m,
            (await db.StockCostStates.SingleAsync()).InventoryValue);
        await coordinator.CompleteReplayInTransactionAsync(root,
            [candidateHistory.Id, replacementHistory.Id]);
        db.ChangeTracker.Clear();
        var corrected = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, "DEMO", "HQ", "RM1",
            new StockReplayEventKey(candidateAt.AddTicks(-1),
                StockReplayEventKey.OrdinaryClass, 0, "", 0));
        Assert.Equal(2, corrected.LaterEvents.Count);
        Assert.Equal(-generations[0].Id, corrected.LaterEvents[1].EventId);
        Assert.Equal(1m, corrected.LaterEvents[1].OldSignedQuantity);
        Assert.Equal(2m, corrected.LaterEvents[1].OldSignedValue);
        await coordinator.AbortInTransactionAsync(root);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Wac_transfer_repricing_replaces_both_legs_without_item_value_change()
    {
        await ActivateAsync();
        await using var db = await _factory.CreateDbContextAsync();
        var epochId = await db.StockLedgerEpochs.Select(e => e.Id).SingleAsync();
        var at = new DateTime(2026, 10, 4, 9, 0, 0);
        var owner = Posting("DEMO", "HQ", epochId, 1, Guid.NewGuid());
        owner.EffectiveAt = at;
        owner.BusinessDate = at.Date;
        db.StockPostings.Add(owner);
        db.StockPostingBranchSequences.Add(new StockPostingBranchSequence
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LastSequence = 1,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.IvStockMasters.Add(new IvStockMaster { CompanyCode = "DEMO", ICode = "RM1" });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN"
        });
        var from = new IvBalLoc
        {
            CompanyCode = "DEMO", BranchCode = "HQ", ICode = "RM1",
            WhCode = "MAIN", LocCode = "A", LotNo = "", IStatus = "ACTIVE",
            StdQty = 3m
        };
        var to = new IvBalLoc
        {
            CompanyCode = "DEMO", BranchCode = "HQ", ICode = "RM1",
            WhCode = "MAIN", LocCode = "B", LotNo = "", IStatus = "ACTIVE",
            StdQty = 2m
        };
        db.IvBalLocs.AddRange(from, to);
        await db.SaveChangesAsync();
        var history = new IvTrxHistory
        {
            CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 70,
            TrxLineNo = 1, TrxDtTime = at, TrxType = "TR",
            BatchStatus = "POSTED", ICode = "RM1",
            FromBalLocId = from.Id, FrStdQty = 2m,
            ToBalLocId = to.Id, ToStdQty = 2m,
            LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = owner.Id, PostingLineNo = 1,
            DocumentRevision = 1, EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        db.IvTrxHistories.Add(history);
        await db.SaveChangesAsync();
        db.StockValuationFacts.AddRange(
            new StockValuationFact
            {
                CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId,
                StockPostingId = owner.Id, PostingLineNo = 1,
                InventoryHistoryId = history.Id, EffectiveAt = at,
                BusinessDate = at.Date, PeriodKey = "2026-10",
                ItemCode = "RM1", CostMethod = StockCostMethods.MovingAverage,
                ValuationSource = StockValuationSources.MovingAverage,
                ValuationStatus = StockValuationStatuses.Valued,
                Direction = -1, BaseQty = 2m, CostAmount = 4m,
                BaseCostAmount = 4m
            },
            new StockValuationFact
            {
                CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId,
                StockPostingId = owner.Id, PostingLineNo = 1, SplitOrdinal = 1,
                InventoryHistoryId = history.Id, EffectiveAt = at,
                BusinessDate = at.Date, PeriodKey = "2026-10",
                ItemCode = "RM1", CostMethod = StockCostMethods.MovingAverage,
                ValuationSource = StockValuationSources.MovingAverage,
                ValuationStatus = StockValuationStatuses.Valued,
                Direction = 1, BaseQty = 2m, CostAmount = 4m,
                BaseCostAmount = 4m
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var key = new StockReplayEventKey(at,
            StockReplayEventKey.OrdinaryClass, 1, $"H{history.Id:D10}", 0);
        var active = new StockReplayWacEvent(history.Id, key,
            0m, 0m, 0m, FixedSignedValue: 0m);
        var projected = new StockReplayWacStep(history.Id,
            5m, 15m, 0m, 0m, 5m, 15m);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = CreateCoordinator();
        var begin = await coordinator.BeginInTransactionAsync(db,
            Command(Guid.NewGuid()) with { EffectiveAt = at });
        Assert.NotNull(begin.Context);
        var wrong = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayWacTransferRepricingWriter.AppendAsync(begin.Context!,
                history.Id, active,
                projected with { ValueAfter = 14m }, 10, 11));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, wrong.Error.Code);
        var replacements = await StockReplayWacTransferRepricingWriter.AppendAsync(
            begin.Context!, history.Id, active, projected, 10, 11);
        Assert.Equal(2, replacements.Count);
        Assert.Equal(6m, replacements.Single(f => f.Direction < 0).CostAmount);
        Assert.Equal(6m, replacements.Single(f => f.Direction > 0).CostAmount);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var activeGeneration = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, "DEMO", "HQ", [history]);
        Assert.Equal(0m, activeGeneration[history.Id].Sum(f => f.SignedValue));
        Assert.Equal(2, await db.StockValuationFacts.CountAsync(f =>
            f.StockPostingId == begin.Context!.Posting.Id
            && f.ReversesValuationFactId != null));
        await coordinator.AbortInTransactionAsync(begin.Context!);
        await transaction.RollbackAsync();
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

    private StockPostingCoordinator CreateCoordinator(bool withValuation = false) => new(
        _factory,
        new Tenant(),
        new BranchStockTransactionLock(),
        new StockPeriodGuard(),
        new NoActiveStockFreezeGuard(),
        withValuation ? new InventoryValuationService() : null);

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
