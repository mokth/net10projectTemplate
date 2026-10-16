using ErpWeb.Core.Inventory;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryStockCount)]
public sealed class IvStockCountLedgerAuthorityTests : IAsyncLifetime
{
    private const decimal PurchasePrice = 777m;
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvStockCountLedgerAuthorityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = InventoryLedgerTestFixture.CreateSqliteFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await InventoryLedgerTestFixture.SeedCompanyAsync(db);
        await InventoryLedgerTestFixture.SeedActiveEpochAsync(db);
        await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
        await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            WarehouseCode = "MAIN",
            IsActive = true
        });
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            WarehouseCode = "MAIN",
            LocCode = "BIN1",
            IsActive = true
        });
        db.MsUoms.Add(new MsUom
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            UomCode = "EA",
            IsActive = true
        });
        db.IvClasses.Add(new IvClass
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            IClassCode = "RAW",
            IsActive = true
        });
        db.IvStatuses.Add(new IvStatus
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            IStatus = "ACTIVE",
            IsActive = true
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            ICode = "A100",
            IDesc = "Stock item",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            PurchasePrice = PurchasePrice
        });
        db.IvBalLocs.Add(new IvBalLoc
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            ICode = "A100",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = string.Empty,
            IStatus = "ACTIVE",
            StdQty = 100m,
            StdUom = "EA",
            UnitPrice = null,
            TransDate = FixedToday
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Historical_count_candidates_filter_and_page_after_active_as_of_quantity()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        var epochId = await db.StockLedgerEpochs.Select(e => e.Id).SingleAsync();
        var oldPile = await db.IvBalLocs.SingleAsync();
        oldPile.StdQty = 0m;
        oldPile.TransDate = FixedToday.AddDays(1);
        foreach (var loc in new[] { "BIN2", "BIN3", "BIN4" })
        {
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = company, BranchCode = branch,
                WarehouseCode = "MAIN", LocCode = loc
            });
        }
        var futureReceipt = new IvBalLoc
        {
            CompanyCode = company, BranchCode = branch, ICode = "A100",
            WhCode = "MAIN", LocCode = "BIN2", LotNo = "",
            IStatus = "ACTIVE", StdQty = 5m, StdUom = "EA",
            TransDate = FixedToday.AddDays(1)
        };
        var zeroPile = new IvBalLoc
        {
            CompanyCode = company, BranchCode = branch, ICode = "A100",
            WhCode = "MAIN", LocCode = "BIN3", LotNo = "",
            IStatus = "ACTIVE", StdQty = 0m, StdUom = "EA",
            TransDate = null
        };
        var finalTickReceipt = new IvBalLoc
        {
            CompanyCode = company, BranchCode = branch, ICode = "A100",
            WhCode = "MAIN", LocCode = "BIN4", LotNo = "",
            IStatus = "ACTIVE", StdQty = 4m, StdUom = "EA",
            TransDate = FixedToday
        };
        db.IvBalLocs.AddRange(futureReceipt, zeroPile, finalTickReceipt);
        await db.SaveChangesAsync();

        var tomorrow = FixedToday.AddDays(1).AddHours(9);
        var endOfCountDay = StockReplayEventKey.CountEndOfDay(FixedToday);
        var nextPosting = new StockPosting
        {
            CompanyCode = company, BranchCode = branch, LedgerEpochId = epochId,
            PostingSequence = 1, RequestId = Guid.NewGuid(),
            CommandType = "TEST", SourceModule = "TEST",
            SourceDocumentType = "FIXTURE", SourceDocumentId = "NEXT",
            EffectiveAt = tomorrow, BusinessDate = tomorrow.Date,
            SealedAtUtc = DateTime.UtcNow
        };
        var sameDayPosting = new StockPosting
        {
            CompanyCode = company, BranchCode = branch, LedgerEpochId = epochId,
            PostingSequence = 2, RequestId = Guid.NewGuid(),
            CommandType = "TEST", SourceModule = "TEST",
            SourceDocumentType = "FIXTURE", SourceDocumentId = "SAME_DAY",
            EffectiveAt = endOfCountDay,
            BusinessDate = FixedToday, SealedAtUtc = DateTime.UtcNow
        };
        db.StockPostings.AddRange(nextPosting, sameDayPosting);
        await db.SaveChangesAsync();
        db.IvTrxHistories.AddRange(
            new IvTrxHistory
            {
                CompanyCode = company, BranchCode = branch, ICode = "A100",
                BatchNo = 801, TrxLineNo = 1, TrxDtTime = tomorrow,
                TrxType = IvTrxTypes.MiscellaneousIssue, BatchStatus = "POSTED",
                FromBalLocId = oldPile.Id, FrStdQty = 100m,
                LedgerVersion = 2, LedgerEpochId = epochId,
                StockPostingId = nextPosting.Id, PostingLineNo = 1,
                EntryRole = "ORIGINAL"
            },
            new IvTrxHistory
            {
                CompanyCode = company, BranchCode = branch, ICode = "A100",
                BatchNo = 802, TrxLineNo = 1, TrxDtTime = tomorrow,
                TrxType = IvTrxTypes.MiscellaneousReceipt, BatchStatus = "POSTED",
                ToBalLocId = futureReceipt.Id, ToStdQty = 5m,
                LedgerVersion = 2, LedgerEpochId = epochId,
                StockPostingId = nextPosting.Id, PostingLineNo = 2,
                EntryRole = "ORIGINAL"
            },
            new IvTrxHistory
            {
                CompanyCode = company, BranchCode = branch, ICode = "A100",
                BatchNo = 803, TrxLineNo = 1, TrxDtTime = endOfCountDay,
                TrxType = IvTrxTypes.MiscellaneousReceipt, BatchStatus = "POSTED",
                ToBalLocId = finalTickReceipt.Id, ToStdQty = 4m,
                LedgerVersion = 2, LedgerEpochId = epochId,
                StockPostingId = sameDayPosting.Id, PostingLineNo = 1,
                EntryRole = "ORIGINAL"
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var positiveScope = new IvStockCountScope
        {
            WHCode = "MAIN", IncludeZeroQty = false
        };
        var (positive, positiveTotal) = await StockReplayCountCandidateReader.ReadAsync(
            db, company, branch, positiveScope, FixedToday, 0, 10);
        Assert.Equal(2, positiveTotal);
        Assert.Equal(new[] { "BIN1", "BIN4" }, positive.Select(x => x.Balance.LocCode));
        Assert.Equal(new[] { 100m, 4m }, positive.Select(x => x.HistoricalQty));
        Assert.Equal(0m, positive[0].Balance.StdQty);

        var (page, total) = await StockReplayCountCandidateReader.ReadAsync(
            db, company, branch,
            new IvStockCountScope { WHCode = "MAIN", IncludeZeroQty = true },
            FixedToday, 1, 2);
        Assert.Equal(4, total);
        Assert.Equal(new[] { "BIN2", "BIN3" }, page.Select(x => x.Balance.LocCode));
        Assert.All(page, x => Assert.Equal(0m, x.HistoricalQty));

        var missingEpoch = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayCountCandidateReader.ReadAsync(db, company, branch,
                new IvStockCountScope { ICodes = ["NO_ITEM"] },
                InventoryLedgerTestFixture.PolicyEffectiveFrom.AddDays(-1), 0, 10));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, missingEpoch.Error.Code);

        db.StockPostingBranchSequences.Add(new StockPostingBranchSequence
        {
            CompanyCode = company, BranchCode = branch,
            LastSequence = 2, UpdatedAtUtc = DateTime.UtcNow
        });
        var sheet = new IvStockCountHdr
        {
            CompanyCode = company, BranchCode = branch,
            CountNo = "CC-HISTORICAL-PREVIEW", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var countedLine = new IvStockCountLine
        {
            Header = sheet, LineNumber = 1, BalLocId = oldPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 99m, PhysicalQty = 105m
        };
        var uncountedLine = new IvStockCountLine
        {
            Header = sheet, LineNumber = 2, BalLocId = futureReceipt.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 0m
        };
        db.IvStockCountLines.AddRange(countedLine, uncountedLine);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var preview = await StockReplayCountSheetPreviewReader.ReadAsync(
            db, company, branch, sheet.Id);
        Assert.Equal(2, preview.Lines.Count);
        Assert.Equal(100m, preview.Lines[0].HistoricalPreCountQty);
        Assert.Equal(0m, preview.Lines[0].LiveQty);
        Assert.Equal(5m, preview.Lines[0].SignedAdjustmentQty);
        Assert.Equal(5m, preview.Lines[0].ProjectedFinalQty);
        Assert.True(preview.Lines[0].IsGeneratedSnapshotStale);
        Assert.Equal(0m, preview.Lines[1].HistoricalPreCountQty);
        Assert.Equal(5m, preview.Lines[1].LiveQty);
        Assert.Null(preview.Lines[1].SignedAdjustmentQty);
        Assert.Equal(5m, preview.TotalIncrease);
        Assert.Equal(0m, preview.TotalDecrease);

        var changedLine = await db.IvStockCountLines.SingleAsync(l => l.Id == countedLine.Id);
        changedLine.LotNo = "WRONG";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var wrongSlice = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayCountSheetPreviewReader.ReadAsync(db, company, branch, sheet.Id));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, wrongSlice.Error.Code);
        var wrongPlan = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayPhysicalPlanner.BuildCountInsertionAsync(
                db, company, branch, countedLine.Id));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, wrongPlan.Error.Code);
    }

    [Fact]
    public async Task Positive_count_variance_uses_current_cost_not_purchase_price()
    {
        var service = InventoryLedgerTestFixture.CreateStockCount(_factory, FixedToday);
        var save = await service.SaveAsync(new IvStockCountSaveRequest
        {
            CountDate = FixedToday,
            WHCode = "MAIN",
            IncludeZeroQty = true
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await service.GenerateAsync(save.Id, discardCounts: false)).Succeeded);

        var sheet = await service.GetAsync(save.CountNo!);
        Assert.True(sheet.Succeeded, sheet.ErrorMessage);
        var line = Assert.Single(sheet.Document!.Lines);
        Assert.Equal(PurchasePrice, line.SnapshotUnitPrice);

        var counted = await service.SaveCountsAsync(
            sheet.Document.Id,
            [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 110m }],
            null);
        Assert.True(counted.Succeeded, counted.ErrorMessage);
        var post = await service.PostAsync(sheet.Document.Id);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "ADJUST_IN");
        Assert.Equal(2m, fact.UnitCost);
        Assert.Equal(20m, fact.CostAmount);
        Assert.Equal(StockValuationSources.MovingAverage, fact.ValuationSource);
        Assert.NotEqual(PurchasePrice, fact.UnitCost);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(110m, state.OnHandBaseQty);
        Assert.Equal(220m, state.InventoryValue);
    }

    [Fact]
    public async Task Standard_initial_count_replay_writes_revision_fact_and_reconciles_state()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
        await InventoryLedgerTestFixture.SeedCostStateAsync(
            db, "A100", 100m, 2m, StockCostMethods.Standard);
        await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 2m);
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        var pile = await db.IvBalLocs.SingleAsync();
        var revision = await db.ItemStandardCostRevisions.SingleAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = company, BranchCode = branch,
            CountNo = "CC-STD-REPLAY", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = pile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 100m, PhysicalQty = 110m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        var basis = new StockCountCostBasis
        {
            CompanyCode = company, BranchCode = branch,
            StockCountLineId = line.Id,
            CostBasisKind = StockCountCostBasisKinds.StandardRevision,
            StandardCostRevisionId = revision.Id,
            FrozenFallbackUnitCost = 2m,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.ChangeTracker.Clear();
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, company, branch, header.Id);
        var postingPlan = await StockReplayStandardCountSheetPostingWriter.BuildAsync(
            db, sheet, [basis]);
        Assert.Equal(220m, Assert.Single(postingPlan.Items).Projection.FinalValue);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, company, branch, header.Id, [basis]);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        try
        {
            var prepared = await StockReplayStandardCountSheetPostingWriter
                .PrepareWithinRootAsync(root, postingPlan);
            var result = await StockReplayStandardCountSheetPostingWriter
                .AppendWithinRootAsync(root, prepared, [basis],
                    new RunningNumberService());
            var fact = Assert.Single(result.InitialFacts);
            Assert.Equal(2m, fact.UnitCost);
            Assert.Equal(20m, fact.CostAmount);
            Assert.Equal(StockValuationSources.Standard, fact.ValuationSource);
            Assert.Single(result.RequiredActiveHistoryIds);
            await coordinator.CompleteReplayInTransactionAsync(root,
                result.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(root);
            await transaction.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        var state = await db.StockCostStates.SingleAsync(s =>
            s.CostMethod == StockCostMethods.Standard);
        Assert.Equal(110m, state.OnHandBaseQty);
        Assert.Equal(220m, state.InventoryValue);
        var tail = await StockReplayStandardFinancialTailReader.ReadLaterAsync(
            db, company, branch, "A100",
            new StockReplayEventKey(FixedToday.AddHours(12),
                StockReplayEventKey.OrdinaryClass, long.MaxValue,
                "\uffff", int.MaxValue));
        Assert.Equal(20m, Assert.Single(tail.LaterEvents).OldSignedValue);
    }

    [Theory]
    [InlineData(110, 20)]
    [InlineData(100, 0)]
    public async Task Standard_count_sheet_values_two_piles_at_one_revision(
        int firstPhysical, int firstAmount)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
        await InventoryLedgerTestFixture.SeedCostStateAsync(
            db, "A100", 100m, 2m, StockCostMethods.Standard);
        await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 2m);
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        var firstPile = await db.IvBalLocs.SingleAsync();
        var secondPile = new IvBalLoc
        {
            CompanyCode = company, BranchCode = branch,
            ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA", StdQty = 0m
        };
        db.IvBalLocs.Add(secondPile);
        await db.SaveChangesAsync();
        var revisionId = await db.ItemStandardCostRevisions
            .Select(r => r.Id).SingleAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = company, BranchCode = branch,
            CountNo = "CC-STD-TWO", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var firstLine = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = firstPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 100m, PhysicalQty = firstPhysical
        };
        var secondLine = new IvStockCountLine
        {
            Header = header, LineNumber = 2, BalLocId = secondPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 0m, PhysicalQty = 5m
        };
        db.IvStockCountLines.AddRange(firstLine, secondLine);
        await db.SaveChangesAsync();
        StockCountCostBasis Basis(int lineId) => new()
        {
            CompanyCode = company, BranchCode = branch,
            StockCountLineId = lineId,
            CostBasisKind = StockCountCostBasisKinds.StandardRevision,
            StandardCostRevisionId = revisionId,
            FrozenFallbackUnitCost = 2m,
            CreatedAtUtc = DateTime.UtcNow
        };
        var bases = new[] { Basis(firstLine.Id), Basis(secondLine.Id) };
        db.ChangeTracker.Clear();
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, company, branch, header.Id);
        var plan = await StockReplayStandardCountSheetPostingWriter.BuildAsync(
            db, sheet, bases);
        Assert.Equal(firstPhysical + 5m,
            Assert.Single(plan.Items).Projection.FinalQuantity);
        Assert.Equal(210m + firstAmount,
            plan.Items[0].Projection.FinalValue);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, company, branch, header.Id, bases);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        try
        {
            var prepared = await StockReplayStandardCountSheetPostingWriter
                .PrepareWithinRootAsync(root, plan);
            var result = await StockReplayStandardCountSheetPostingWriter
                .AppendWithinRootAsync(root, prepared, bases,
                    new RunningNumberService());
            Assert.Equal(firstAmount == 0 ? new[] { 10m }
                    : new[] { 20m, 10m }, result.InitialFacts
                .OrderBy(f => f.PostingLineNo).Select(f => f.CostAmount));
            Assert.Equal(firstAmount == 0 ? 1 : 2,
                result.RequiredActiveHistoryIds.Count);
            await coordinator.CompleteReplayInTransactionAsync(root,
                result.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(root);
            await transaction.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        var state = await db.StockCostStates.SingleAsync(s =>
            s.CostMethod == StockCostMethods.Standard);
        Assert.Equal(firstPhysical + 5m, state.OnHandBaseQty);
        Assert.Equal(210m + firstAmount, state.InventoryValue);
        Assert.Equal(2, await db.StockCountReplayGenerations.CountAsync());
    }

    [Theory]
    [InlineData(110, 120)]
    [InlineData(110, 110)]
    [InlineData(100, 120)]
    public async Task Backdated_standard_count_reconciles_changed_later_count(
        int laterPhysical, int earlierPhysical)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
        await InventoryLedgerTestFixture.SeedCostStateAsync(
            db, "A100", 100m, 2m, StockCostMethods.Standard);
        await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 2m);
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        var pileId = await db.IvBalLocs.Select(b => b.Id).SingleAsync();
        var revisionId = await db.ItemStandardCostRevisions
            .Select(r => r.Id).SingleAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);

        async Task<(int LineId, long RootId)> PostAsync(
            string countNo, DateTime countDate, decimal physicalQty)
        {
            var header = new IvStockCountHdr
            {
                CompanyCode = company, BranchCode = branch,
                CountNo = countNo, CountDate = countDate,
                Status = IvStockCountStatuses.Counted
            };
            var line = new IvStockCountLine
            {
                Header = header, LineNumber = 1, BalLocId = pileId,
                ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
                LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
                SystemQty = 100m, PhysicalQty = physicalQty
            };
            db.IvStockCountLines.Add(line);
            await db.SaveChangesAsync();
            var basis = new StockCountCostBasis
            {
                CompanyCode = company, BranchCode = branch,
                StockCountLineId = line.Id,
                CostBasisKind = StockCountCostBasisKinds.StandardRevision,
                StandardCostRevisionId = revisionId,
                FrozenFallbackUnitCost = 2m,
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ChangeTracker.Clear();
            var sheet = await StockReplayPhysicalPlanner
                .BuildCountSheetInsertionAsync(db, company, branch, header.Id);
            var plan = await StockReplayStandardCountSheetPostingWriter
                .BuildAsync(db, sheet, [basis]);
            var command = await StockCountAnchorCommandFactory.CreateAsync(
                db, company, branch, header.Id, [basis]);
            await using var transaction = await db.Database.BeginTransactionAsync();
            var begin = await coordinator.BeginInTransactionAsync(db, command);
            Assert.NotNull(begin.Context);
            var root = begin.Context!;
            try
            {
                var prepared = await StockReplayStandardCountSheetPostingWriter
                    .PrepareWithinRootAsync(root, plan);
                var written = await StockReplayStandardCountSheetPostingWriter
                    .AppendWithinRootAsync(root, prepared, [basis],
                        new RunningNumberService());
                await coordinator.CompleteReplayInTransactionAsync(root,
                    written.RequiredActiveHistoryIds);
                await transaction.CommitAsync();
                db.ChangeTracker.Clear();
                return (line.Id, root.Posting.Id);
            }
            catch
            {
                await coordinator.AbortInTransactionAsync(root);
                await transaction.RollbackAsync();
                throw;
            }
        }

        var later = await PostAsync("CC-STD-DESCENDANT",
            FixedToday.AddDays(1), laterPhysical);
        var earlier = await PostAsync("CC-STD-EARLIER", FixedToday,
            earlierPhysical);
        var state = await db.StockCostStates.SingleAsync(s =>
            s.CostMethod == StockCostMethods.Standard);
        Assert.Equal(laterPhysical, state.OnHandBaseQty);
        Assert.Equal(laterPhysical * 2m, state.InventoryValue);
        var generations = await db.StockCountReplayGenerations.AsNoTracking()
            .Where(g => g.StockCountLineId == later.LineId)
            .OrderBy(g => g.Id).ToArrayAsync();
        Assert.Equal(2, generations.Length);
        Assert.Equal(laterPhysical - 100m, generations[0].SignedAdjustmentQty);
        Assert.Equal(laterPhysical - earlierPhysical,
            generations[1].SignedAdjustmentQty);
        Assert.Equal(earlier.RootId, generations[1].WritePostingId);
        var rootFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.StockPostingId == earlier.RootId)
            .OrderBy(f => f.PostingLineNo).ToArrayAsync();
        Assert.Equal((earlierPhysical == 100 ? 0 : 1)
            + (laterPhysical == 100 ? 0 : 1)
            + (laterPhysical == earlierPhysical ? 0 : 1),
            rootFacts.Length);
        if (laterPhysical != 100)
        {
            var reversal = Assert.Single(rootFacts.Where(f =>
                f.ValuationStatus == StockValuationStatuses.Reversed));
            Assert.Equal(Math.Abs(laterPhysical - 100m) * 2m,
                reversal.CostAmount);
        }
        if (laterPhysical != earlierPhysical)
        {
            var replacement = Assert.Single(rootFacts.Where(f =>
                f.InventoryHistoryId == generations[1].AdjustmentHistoryId));
            Assert.Equal(Math.Abs(laterPhysical - earlierPhysical) * 2m,
                replacement.CostAmount);
            Assert.Equal(StockValuationStatuses.Valued,
                replacement.ValuationStatus);
        }
        var tail = await StockReplayStandardFinancialTailReader.ReadLaterAsync(
            db, company, branch, "A100",
            new StockReplayEventKey(FixedToday.AddHours(12),
                StockReplayEventKey.OrdinaryClass, long.MaxValue,
                "\uffff", int.MaxValue));
        Assert.Equal(new decimal[] { earlierPhysical - 100m,
                laterPhysical - earlierPhysical },
            tail.LaterEvents.Select(e => e.OldSignedQuantity));
        Assert.Equal(new decimal[] { (earlierPhysical - 100m) * 2m,
                (laterPhysical - earlierPhysical) * 2m },
            tail.LaterEvents.Select(e => e.OldSignedValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(110)]
    public async Task Initial_historical_count_writer_seals_outbound_zero_and_inbound_observations(
        int physicalQty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        var balanceId = await db.IvBalLocs.Select(b => b.Id).SingleAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = company, BranchCode = branch,
            CountNo = $"CC-INITIAL-{physicalQty}",
            CountDate = FixedToday, Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = balanceId,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1", LotNo = "",
            IStatus = "ACTIVE", StdUom = "EA", SystemQty = 100m,
            PhysicalQty = physicalQty
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        var basis = new StockCountCostBasis
        {
            CompanyCode = company, BranchCode = branch,
            StockCountLineId = line.Id,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            FrozenFallbackUnitCost = 2m,
            CostOverrideReason = "Approved initial count cost",
            CostApprovedBy = "manager", CostApprovedAtUtc = now,
            CreatedAtUtc = now
        };
        db.ChangeTracker.Clear();
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, company, branch, header.Id);
        var physicalPlan = Assert.Single(sheet.Lines);
        var wacCandidate = StockReplayWacCountEventFactory.CreateInitial(
            physicalPlan, physicalQty > 0 ? basis : null);
        var wacPlan = await StockReplayWacInsertionPlanner.BuildAsync(
            db, company, branch, "A100", physicalPlan, wacCandidate);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, company, branch, header.Id,
            physicalQty > 0 ? [basis] : []);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        try
        {
            var permit = await StockReplayPhysicalPlanner.PrepareCountSheetWithinRootAsync(
                root, sheet);
            var wacPermit = await StockReplayWacInsertionPlanner.PrepareWithinRootAsync(
                root, wacPlan);
            var pile = await db.IvBalLocs.SingleAsync(b => b.Id == balanceId);
            pile.StdQty = physicalQty;
            await db.SaveChangesAsync();
            if (physicalQty > 0)
            {
                var missingBasis = await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockCountInitialHistoryWriter.AppendAsync(
                        root, permit, new RunningNumberService()));
                Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, missingBasis.Error.Code);
                Assert.Empty(await db.IvTrxBatches.ToArrayAsync());
                db.StockCountCostBases.Add(basis);
                await db.SaveChangesAsync();
            }
            var written = await StockCountInitialHistoryWriter.AppendAsync(
                root, permit, new RunningNumberService());
            Assert.Equal(physicalQty == 100, written.AdjustmentBatchNo is null);
            var generation = Assert.Single(written.Generations);
            Assert.Equal(physicalQty - 100m, generation.SignedAdjustmentQty);
            Assert.Equal(physicalQty == 100, generation.AdjustmentHistoryId is null);
            if (physicalQty > 0)
            {
                var forgedPlan = wacPlan with
                {
                    Candidate = wacPlan.Candidate with
                    {
                        CountFallbackUnitCost = 3m
                    }
                };
                var changedBasis = await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayWacInitialCountValuationWriter.AppendAsync(
                        root, permit, wacPermit, forgedPlan, line.Id, 1));
                Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                    changedBasis.Error.Code);
            }
            var forgedPhysical = wacPlan with
            {
                PhysicalPlan = physicalPlan with
                {
                    Projection = physicalPlan.Projection with
                    {
                        FinalQuantity = physicalPlan.Projection.FinalQuantity + 1m
                    }
                }
            };
            var changedPhysical = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayWacInitialCountValuationWriter.AppendAsync(
                    root, permit, wacPermit, forgedPhysical, line.Id, 1));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                changedPhysical.Error.Code);
            var fact = await StockReplayWacInitialCountValuationWriter.AppendAsync(
                root, permit, wacPermit, wacPlan, line.Id, 1);
            Assert.Equal(physicalQty == 100, fact is null);
            var tail = await StockReplayWacTailRepricingWriter
                .AppendPreparedInsertionTailAsync(root, wacPermit, wacPlan, 10);
            Assert.Empty(tail.RepricedHistoryIds);
            await StockReplayWacCostStateWriter.ApplyAsync(root, wacPermit);
            await coordinator.CompleteReplayInTransactionAsync(root,
                generation.AdjustmentHistoryId is int historyId ? [historyId] : []);
            await transaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(root);
            await transaction.RollbackAsync();
            throw;
        }

        db.ChangeTracker.Clear();
        var posted = await db.IvStockCountHdrs.SingleAsync(h => h.Id == header.Id);
        Assert.Equal(IvStockCountStatuses.Posted, posted.Status);
        Assert.Equal(0, posted.PostedStaleLines);
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.StockPostingId != null && h.RefNo == header.CountNo)
            .ToArrayAsync();
        Assert.Equal(physicalQty == 100 ? 0 : 1, histories.Length);
        if (histories.Length == 1)
        {
            Assert.Equal(FixedToday, histories[0].TrxDtTime);
            Assert.Equal(physicalQty > 100, histories[0].ToBalLocId == balanceId);
            Assert.Equal(physicalQty > 100 ? basis.Id : null,
                histories[0].StockCountCostBasisId);
            Assert.NotNull(posted.PostedBatchNo);
            var details = await db.IvTrxBatchDetails.AsNoTracking()
                .Where(d => d.BatchNo == posted.PostedBatchNo)
                .ToArrayAsync();
            Assert.Single(details);
        }
        else
        {
            Assert.Null(posted.PostedBatchNo);
            Assert.Empty(await db.IvTrxBatchDetails.ToArrayAsync());
        }
        Assert.NotNull((await db.StockPostings.SingleAsync()).SealedAtUtc);
        Assert.Equal(physicalQty, (await db.StockCostStates.SingleAsync()).OnHandBaseQty);
        Assert.Equal(physicalQty * 2m, (await db.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Theory]
    [InlineData(5, 2)]
    [InlineData(0, 1)]
    public async Task Initial_historical_count_writer_uses_one_batch_for_multiple_adjusted_piles(
        int secondPhysical, int expectedFactCount)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        var firstPile = await db.IvBalLocs.SingleAsync();
        firstPile.StdQty = 0m;
        var initialState = await db.StockCostStates.SingleAsync();
        initialState.OnHandBaseQty = 0m;
        initialState.InventoryValue = 0m;
        initialState.AverageUnitCost = 0m;
        initialState.CurrentUnitCost = 0m;
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = company, BranchCode = branch,
            WarehouseCode = "MAIN", LocCode = "BIN2"
        });
        var secondPile = new IvBalLoc
        {
            CompanyCode = company, BranchCode = branch,
            ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA", StdQty = 0m
        };
        db.IvBalLocs.Add(secondPile);
        await db.SaveChangesAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = company, BranchCode = branch,
            CountNo = "CC-MULTI", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var firstLine = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = firstPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 0m, PhysicalQty = 10m
        };
        var secondLine = new IvStockCountLine
        {
            Header = header, LineNumber = 2, BalLocId = secondPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 0m, PhysicalQty = secondPhysical
        };
        db.IvStockCountLines.AddRange(firstLine, secondLine);
        await db.SaveChangesAsync();
        var approvedAt = DateTime.UtcNow;
        StockCountCostBasis Basis(int lineId, decimal fallback) => new()
        {
            CompanyCode = company, BranchCode = branch,
            StockCountLineId = lineId,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            FrozenFallbackUnitCost = fallback,
            CostOverrideReason = "Approved multiple pile count",
            CostApprovedBy = "manager", CostApprovedAtUtc = approvedAt,
            CreatedAtUtc = approvedAt
        };
        var bases = secondPhysical > 0
            ? new[] { Basis(firstLine.Id, 4m), Basis(secondLine.Id, 2m) }
            : new[] { Basis(firstLine.Id, 4m) };
        db.ChangeTracker.Clear();
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, company, branch, header.Id);
        var wacSheet = await StockReplayWacCountSheetInsertionPlanner.BuildAsync(
            db, sheet, "A100", bases);
        var postingPlan = await StockReplayWacCountSheetPostingWriter.BuildAsync(
            db, sheet, bases);
        Assert.Single(postingPlan.Items);
        Assert.Equal(10m + secondPhysical, wacSheet.Projection.FinalQuantity);
        Assert.Equal(40m + secondPhysical * 4m, wacSheet.Projection.FinalValue);
        Assert.Equal(40m, wacSheet.Projection.Candidate.SignedValue);
        Assert.Equal(secondPhysical * 4m,
            Assert.Single(wacSheet.Projection.LaterSteps).SignedValue);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, company, branch, header.Id, bases);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        try
        {
            var changedState = await db.StockCostStates.SingleAsync();
            changedState.InventoryValue = 1m;
            await db.SaveChangesAsync();
            var staleWac = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayWacCountSheetInsertionPlanner.ValidateWithinRootAsync(
                    root, wacSheet));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument, staleWac.Error.Code);
            changedState.InventoryValue = 0m;
            await db.SaveChangesAsync();
            var prepared = await StockReplayWacCountSheetPostingWriter
                .PrepareWithinRootAsync(root, postingPlan);
            var result = await StockReplayWacCountSheetPostingWriter.AppendWithinRootAsync(
                root, prepared, bases, new RunningNumberService());
            var written = result.History;
            Assert.NotNull(written.AdjustmentBatchNo);
            Assert.Equal(2, written.Generations.Count);
            Assert.Equal(new[] { 10m, (decimal)secondPhysical },
                written.Generations.OrderBy(g => g.StockCountLineId)
                    .Select(g => g.SignedAdjustmentQty));
            var facts = result.InitialFacts;
            Assert.Equal(secondPhysical > 0
                    ? new[] { 40m, 20m } : new[] { 40m },
                facts.OrderBy(f => f.PostingLineNo).Select(f => f.CostAmount));
            Assert.Empty(result.RepricedHistoryIds);
            Assert.Empty(result.RepricedCountLineIds);
            Assert.Equal(expectedFactCount, result.RequiredActiveHistoryIds.Count);
            await coordinator.CompleteReplayInTransactionAsync(root,
                result.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(root);
            await transaction.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        var batches = await db.IvTrxBatches.AsNoTracking().ToArrayAsync();
        var batch = Assert.Single(batches);
        var details = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchNo == batch.BatchNo)
            .OrderBy(d => d.TrxLineNo).ToArrayAsync();
        Assert.Equal(secondPhysical > 0
                ? new short[] { 1, 2 } : new short[] { 1 },
            details.Select(d => d.TrxLineNo));
        Assert.Equal(secondPhysical > 0
                ? new[] { firstPile.Id, secondPile.Id } : new[] { firstPile.Id },
            details.Select(d => d.ToBalLocId!.Value));
        Assert.Equal(10m + secondPhysical,
            (await db.StockCostStates.SingleAsync()).OnHandBaseQty);
        Assert.Equal(40m + secondPhysical * 4m,
            (await db.StockCostStates.SingleAsync()).InventoryValue);
        var finalPiles = await db.IvBalLocs.AsNoTracking()
            .ToDictionaryAsync(b => b.Id);
        Assert.Equal(StockReplayEventKey.CountEndOfDay(FixedToday),
            finalPiles[firstPile.Id].TransDate);
        Assert.Equal(StockReplayEventKey.CountEndOfDay(FixedToday),
            finalPiles[secondPile.Id].TransDate);
        Assert.Equal(expectedFactCount, await db.StockValuationFacts.CountAsync(f =>
            f.SourceDocumentNo == header.CountNo));
        Assert.NotNull((await db.StockPostings.SingleAsync()).SealedAtUtc);
        var timeline = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, company, branch, "A100",
            new StockReplayEventKey(FixedToday,
                StockReplayEventKey.OrdinaryClass, 0, "before-count", 0));
        Assert.Equal(10m + secondPhysical, timeline.CurrentQuantity);
        Assert.Equal(40m + secondPhysical * 4m, timeline.CurrentValue);
        Assert.Equal(new[] { 10m, (decimal)secondPhysical }, timeline.LaterEvents
            .Select(e => e.OldSignedQuantity));
    }

    [Fact]
    public async Task Initial_historical_WAC_sheet_covers_every_item_before_posting()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        await InventoryLedgerTestFixture.SeedCostStateAsync(db, "B200", 0m, 0m);
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = company, BranchCode = branch,
            WarehouseCode = "MAIN", LocCode = "BIN2"
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = company, ICode = "B200", IDesc = "Second item",
            IClassCode = "RAW", StdUom = "EA", StockControl = true,
            LotControl = false, IsActive = true
        });
        var secondPile = new IvBalLoc
        {
            CompanyCode = company, BranchCode = branch,
            ICode = "B200", WhCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA", StdQty = 0m
        };
        db.IvBalLocs.Add(secondPile);
        await db.SaveChangesAsync();
        var firstPile = await db.IvBalLocs.SingleAsync(b => b.ICode == "A100");
        var header = new IvStockCountHdr
        {
            CompanyCode = company, BranchCode = branch,
            CountNo = "CC-TWO-ITEMS", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var firstLine = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = firstPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 100m, PhysicalQty = 110m
        };
        var secondLine = new IvStockCountLine
        {
            Header = header, LineNumber = 2, BalLocId = secondPile.Id,
            ICode = "B200", WHCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 0m, PhysicalQty = 3m
        };
        db.IvStockCountLines.AddRange(firstLine, secondLine);
        await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        StockCountCostBasis Basis(int lineId, decimal fallback) => new()
        {
            CompanyCode = company, BranchCode = branch,
            StockCountLineId = lineId,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            FrozenFallbackUnitCost = fallback,
            CostOverrideReason = "Approved two-item count",
            CostApprovedBy = "manager", CostApprovedAtUtc = now,
            CreatedAtUtc = now
        };
        var bases = new[] { Basis(firstLine.Id, 3m), Basis(secondLine.Id, 5m) };
        db.ChangeTracker.Clear();
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, company, branch, header.Id);
        var plan = await StockReplayWacCountSheetPostingWriter.BuildAsync(
            db, sheet, bases);
        Assert.Equal(new[] { "A100", "B200" },
            plan.Items.Select(p => p.ItemCode));
        var missingItem = Assert.Throws<StockLedgerException>(() =>
            StockReplayWacCountSheetPostingWriter.ValidatePreparedPlan(
                plan with { Items = [plan.Items[0]] }));
        Assert.Equal(StockLedgerErrorCodes.StaleDocument, missingItem.Error.Code);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, company, branch, header.Id, bases);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        try
        {
            var prepared = await StockReplayWacCountSheetPostingWriter
                .PrepareWithinRootAsync(root, plan);
            var incompleteBasis = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayWacCountSheetPostingWriter.AppendWithinRootAsync(
                    root, prepared, [bases[0]], new RunningNumberService()));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                incompleteBasis.Error.Code);
            Assert.Equal(100m, (await db.IvBalLocs.SingleAsync(b =>
                b.Id == firstPile.Id)).StdQty);
            Assert.Equal(0m, (await db.IvBalLocs.SingleAsync(b =>
                b.Id == secondPile.Id)).StdQty);
            Assert.Empty(db.StockCountCostBases.Local);
            var result = await StockReplayWacCountSheetPostingWriter
                .AppendWithinRootAsync(root, prepared, bases,
                    new RunningNumberService());
            Assert.Equal(2, result.History.Generations.Count);
            Assert.Equal(2, result.InitialFacts.Count);
            Assert.Equal(2, result.RequiredActiveHistoryIds.Count);
            await coordinator.CompleteReplayInTransactionAsync(root,
                result.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(root);
            await transaction.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        var states = await db.StockCostStates.AsNoTracking()
            .OrderBy(s => s.ItemCode).ToArrayAsync();
        Assert.Equal(new[] { 110m, 3m },
            states.Select(s => s.OnHandBaseQty));
        Assert.Equal(new[] { 220m, 15m },
            states.Select(s => s.InventoryValue));
        Assert.Single(await db.IvTrxBatches.ToArrayAsync());
        Assert.NotNull((await db.StockPostings.SingleAsync()).SealedAtUtc);
    }

    [Fact]
    public async Task Initial_historical_multi_pile_WAC_count_reprices_later_issue_and_seals()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        var epochId = await db.StockLedgerEpochs.Select(e => e.Id).SingleAsync();
        var firstPile = await db.IvBalLocs.SingleAsync();
        firstPile.StdQty = 150m;
        var state = await db.StockCostStates.SingleAsync();
        state.OnHandBaseQty = 150m;
        state.InventoryValue = 450m;
        state.AverageUnitCost = 3m;
        state.CurrentUnitCost = 3m;
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = company, BranchCode = branch,
            WarehouseCode = "MAIN", LocCode = "BIN2"
        });
        var secondPile = new IvBalLoc
        {
            CompanyCode = company, BranchCode = branch,
            ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA", StdQty = 0m
        };
        db.IvBalLocs.Add(secondPile);
        var receiptAt = FixedToday.AddDays(1).AddHours(9);
        var issueAt = FixedToday.AddDays(2).AddHours(10);
        StockPosting Owner(long sequence, DateTime at, string id) => new()
        {
            CompanyCode = company, BranchCode = branch,
            LedgerEpochId = epochId, PostingSequence = sequence,
            RequestId = Guid.NewGuid(), CommandType = "TEST",
            SourceModule = "INVENTORY", SourceDocumentType = "IV_TRX",
            SourceDocumentId = id, SourceDocumentNo = id,
            EffectiveAt = at, BusinessDate = at.Date,
            PeriodKey = at.ToString("yyyy-MM"),
            SealedAtUtc = DateTime.UtcNow
        };
        var receiptOwner = Owner(1, receiptAt, "MULTI-RECEIPT");
        var issueOwner = Owner(2, issueAt, "MULTI-ISSUE");
        db.StockPostings.AddRange(receiptOwner, issueOwner);
        db.StockPostingBranchSequences.Add(new StockPostingBranchSequence
        {
            CompanyCode = company, BranchCode = branch,
            LastSequence = 2, UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var receipt = new IvTrxHistory
        {
            CompanyCode = company, BranchCode = branch,
            BatchNo = 911, TrxLineNo = 1, TrxDtTime = receiptAt,
            TrxType = IvTrxTypes.MiscellaneousReceipt, BatchStatus = "POSTED",
            ICode = "A100", ToBalLocId = firstPile.Id, ToStdQty = 100m,
            ToStdUom = "EA", ToWarehouse = "MAIN", ToLocation = "BIN1",
            IStatus = "ACTIVE", LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = receiptOwner.Id, PostingLineNo = 1,
            EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        var issue = new IvTrxHistory
        {
            CompanyCode = company, BranchCode = branch,
            BatchNo = 912, TrxLineNo = 1, TrxDtTime = issueAt,
            TrxType = IvTrxTypes.MiscellaneousIssue, BatchStatus = "POSTED",
            ICode = "A100", FromBalLocId = firstPile.Id, FrStdQty = 50m,
            FrStdUom = "EA", FrWarehouse = "MAIN", FrLocation = "BIN1",
            IStatus = "ACTIVE", LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = issueOwner.Id, PostingLineNo = 1,
            EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        db.IvTrxHistories.AddRange(receipt, issue);
        await db.SaveChangesAsync();
        StockValuationFact Fact(StockPosting owner, IvTrxHistory history,
            int direction, decimal quantity, decimal amount) => new()
        {
            CompanyCode = company, BranchCode = branch,
            LedgerEpochId = epochId, StockPostingId = owner.Id,
            PostingLineNo = 1, SplitOrdinal = 0,
            SourceLineId = $"IV_TRX:{owner.SourceDocumentId}:1:1:0",
            SourceDocumentType = owner.SourceDocumentType,
            SourceDocumentId = owner.SourceDocumentId,
            SourceDocumentNo = owner.SourceDocumentNo,
            SourceDocumentLine = "1", EffectiveAt = owner.EffectiveAt,
            BusinessDate = owner.BusinessDate, PeriodKey = owner.PeriodKey,
            ItemCode = "A100", WarehouseCode = "MAIN",
            LocationCode = "BIN1", ItemStatus = "ACTIVE", BaseUom = "EA",
            MovementCode = direction > 0 ? "RECEIPT" : "ISSUE",
            Direction = direction, BaseQty = quantity,
            CostMethod = StockCostMethods.MovingAverage,
            UnitCost = amount / quantity, CostAmount = amount,
            BaseCostAmount = amount,
            ValuationSource = StockValuationSources.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = 1, InventoryHistoryId = history.Id,
            FromBalLocId = history.FromBalLocId,
            ToBalLocId = history.ToBalLocId
        };
        db.StockValuationFacts.AddRange(
            Fact(receiptOwner, receipt, 1, 100m, 400m),
            Fact(issueOwner, issue, -1, 50m, 150m));
        var header = new IvStockCountHdr
        {
            CompanyCode = company, BranchCode = branch,
            CountNo = "CC-MULTI-LATE", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var firstLine = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = firstPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 100m, PhysicalQty = 110m
        };
        var secondLine = new IvStockCountLine
        {
            Header = header, LineNumber = 2, BalLocId = secondPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 0m, PhysicalQty = 5m
        };
        db.IvStockCountLines.AddRange(firstLine, secondLine);
        await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        StockCountCostBasis Basis(int lineId) => new()
        {
            CompanyCode = company, BranchCode = branch,
            StockCountLineId = lineId,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            FrozenFallbackUnitCost = 4m,
            CostOverrideReason = "Approved historical count",
            CostApprovedBy = "manager", CostApprovedAtUtc = now,
            CreatedAtUtc = now
        };
        var bases = new[] { Basis(firstLine.Id), Basis(secondLine.Id) };
        db.ChangeTracker.Clear();
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, company, branch, header.Id);
        var wacSheet = await StockReplayWacCountSheetInsertionPlanner.BuildAsync(
            db, sheet, "A100", bases);
        Assert.Equal(165m, wacSheet.Projection.FinalQuantity);
        Assert.Equal(483.488350m, wacSheet.Projection.FinalValue);
        var postingPlan = await StockReplayWacCountSheetPostingWriter.BuildAsync(
            db, sheet, bases);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, company, branch, header.Id, bases);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        try
        {
            var prepared = await StockReplayWacCountSheetPostingWriter
                .PrepareWithinRootAsync(root, postingPlan);
            var result = await StockReplayWacCountSheetPostingWriter.AppendWithinRootAsync(
                root, prepared, bases, new RunningNumberService());
            Assert.Equal(2, result.History.Generations.Count);
            var facts = result.InitialFacts;
            Assert.Equal(new[] { 20m, 10m },
                facts.OrderBy(f => f.PostingLineNo).Select(f => f.CostAmount));
            Assert.Equal(new[] { issue.Id }, result.RepricedHistoryIds);
            Assert.Equal(3, result.RequiredActiveHistoryIds.Count);
            await coordinator.CompleteReplayInTransactionAsync(root,
                result.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(root);
            await transaction.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        var finalState = await db.StockCostStates.SingleAsync();
        Assert.Equal(165m, finalState.OnHandBaseQty);
        Assert.Equal(483.488350m, finalState.InventoryValue);
        var finalPiles = await db.IvBalLocs.AsNoTracking()
            .ToDictionaryAsync(b => b.Id);
        Assert.Equal(issueAt, finalPiles[firstPile.Id].TransDate);
        Assert.Equal(StockReplayEventKey.CountEndOfDay(FixedToday),
            finalPiles[secondPile.Id].TransDate);
        var active = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, company, branch, [issue]);
        Assert.Equal(146.511650m,
            Assert.Single(active[issue.Id]).CostAmount);
        Assert.NotNull((await db.StockPostings.SingleAsync(p =>
            p.SourceDocumentNo == header.CountNo)).SealedAtUtc);
        var timeline = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, company, branch, "A100",
            new StockReplayEventKey(FixedToday,
                StockReplayEventKey.OrdinaryClass, 0, "before-count", 0));
        Assert.Equal(4, timeline.LaterEvents.Count);
        Assert.Equal(165m, timeline.CurrentQuantity);
        Assert.Equal(483.488350m, timeline.CurrentValue);
    }

    [Fact]
    public async Task Initial_historical_WAC_count_reprices_a_later_issue_and_seals()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        var epochId = await db.StockLedgerEpochs.Select(e => e.Id).SingleAsync();
        var pile = await db.IvBalLocs.SingleAsync();
        pile.StdQty = 150m;
        var state = await db.StockCostStates.SingleAsync();
        state.OnHandBaseQty = 150m;
        state.InventoryValue = 450m;
        state.AverageUnitCost = 3m;
        state.CurrentUnitCost = 3m;
        var receiptAt = FixedToday.AddDays(1).AddHours(9);
        var issueAt = FixedToday.AddDays(2).AddHours(10);
        StockPosting Owner(long sequence, DateTime at, string id) => new()
        {
            CompanyCode = company, BranchCode = branch,
            LedgerEpochId = epochId, PostingSequence = sequence,
            RequestId = Guid.NewGuid(), CommandType = "TEST",
            SourceModule = "INVENTORY", SourceDocumentType = "IV_TRX",
            SourceDocumentId = id, SourceDocumentNo = id,
            EffectiveAt = at, BusinessDate = at.Date,
            PeriodKey = at.ToString("yyyy-MM"),
            SealedAtUtc = DateTime.UtcNow
        };
        var receiptOwner = Owner(1, receiptAt, "LATER-RECEIPT");
        var issueOwner = Owner(2, issueAt, "LATER-ISSUE");
        db.StockPostings.AddRange(receiptOwner, issueOwner);
        db.StockPostingBranchSequences.Add(new StockPostingBranchSequence
        {
            CompanyCode = company, BranchCode = branch,
            LastSequence = 2, UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var receipt = new IvTrxHistory
        {
            CompanyCode = company, BranchCode = branch,
            BatchNo = 901, TrxLineNo = 1, TrxDtTime = receiptAt,
            TrxType = IvTrxTypes.MiscellaneousReceipt, BatchStatus = "POSTED",
            ICode = "A100", ToBalLocId = pile.Id, ToStdQty = 100m,
            ToStdUom = "EA", ToWarehouse = "MAIN", ToLocation = "BIN1",
            IStatus = "ACTIVE", LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = receiptOwner.Id, PostingLineNo = 1,
            EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        var issue = new IvTrxHistory
        {
            CompanyCode = company, BranchCode = branch,
            BatchNo = 902, TrxLineNo = 1, TrxDtTime = issueAt,
            TrxType = IvTrxTypes.MiscellaneousIssue, BatchStatus = "POSTED",
            ICode = "A100", FromBalLocId = pile.Id, FrStdQty = 50m,
            FrStdUom = "EA", FrWarehouse = "MAIN", FrLocation = "BIN1",
            IStatus = "ACTIVE", LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = issueOwner.Id, PostingLineNo = 1,
            EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        db.IvTrxHistories.AddRange(receipt, issue);
        await db.SaveChangesAsync();
        StockValuationFact Fact(StockPosting owner, IvTrxHistory history,
            int direction, decimal quantity, decimal amount) => new()
        {
            CompanyCode = company, BranchCode = branch,
            LedgerEpochId = epochId, StockPostingId = owner.Id,
            PostingLineNo = 1, SplitOrdinal = 0,
            SourceLineId = $"IV_TRX:{owner.SourceDocumentId}:1:1:0",
            SourceDocumentType = owner.SourceDocumentType,
            SourceDocumentId = owner.SourceDocumentId,
            SourceDocumentNo = owner.SourceDocumentNo,
            SourceDocumentLine = "1", EffectiveAt = owner.EffectiveAt,
            BusinessDate = owner.BusinessDate, PeriodKey = owner.PeriodKey,
            ItemCode = "A100", WarehouseCode = "MAIN",
            LocationCode = "BIN1", ItemStatus = "ACTIVE", BaseUom = "EA",
            MovementCode = direction > 0 ? "RECEIPT" : "ISSUE",
            Direction = direction, BaseQty = quantity,
            CostMethod = StockCostMethods.MovingAverage,
            UnitCost = amount / quantity, CostAmount = amount,
            BaseCostAmount = amount,
            ValuationSource = StockValuationSources.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            ValuationVersion = 1, InventoryHistoryId = history.Id,
            FromBalLocId = history.FromBalLocId,
            ToBalLocId = history.ToBalLocId
        };
        db.StockValuationFacts.AddRange(
            Fact(receiptOwner, receipt, 1, 100m, 400m),
            Fact(issueOwner, issue, -1, 50m, 150m));
        var header = new IvStockCountHdr
        {
            CompanyCode = company, BranchCode = branch,
            CountNo = "CC-LATE-ISSUE", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = pile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 100m, PhysicalQty = 110m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        var basis = new StockCountCostBasis
        {
            CompanyCode = company, BranchCode = branch,
            StockCountLineId = line.Id,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            FrozenFallbackUnitCost = 4m,
            CostOverrideReason = "Approved historical count",
            CostApprovedBy = "manager", CostApprovedAtUtc = now,
            CreatedAtUtc = now
        };
        db.ChangeTracker.Clear();
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, company, branch, header.Id);
        var physicalPlan = Assert.Single(sheet.Lines);
        var candidate = StockReplayWacCountEventFactory.CreateInitial(
            physicalPlan, basis);
        var financial = await StockReplayWacInsertionPlanner.BuildAsync(
            db, company, branch, "A100", physicalPlan, candidate);
        Assert.Equal(160m, financial.Projection.FinalQuantity);
        Assert.Equal(472.380950m, financial.Projection.FinalValue);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, company, branch, header.Id, [basis]);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        try
        {
            var countPermit = await StockReplayPhysicalPlanner
                .PrepareCountSheetWithinRootAsync(root, sheet);
            var wacPermit = await StockReplayWacInsertionPlanner
                .PrepareWithinRootAsync(root, financial);
            db.StockCountCostBases.Add(basis);
            (await db.IvBalLocs.SingleAsync(b => b.Id == pile.Id)).StdQty = 160m;
            await db.SaveChangesAsync();
            var written = await StockCountInitialHistoryWriter.AppendAsync(
                root, countPermit, new RunningNumberService());
            var countHistoryId = Assert.Single(written.Generations)
                .AdjustmentHistoryId!.Value;
            await StockReplayWacInitialCountValuationWriter.AppendAsync(
                root, countPermit, wacPermit, financial, line.Id, 1);
            var tail = await StockReplayWacTailRepricingWriter
                .AppendPreparedInsertionTailAsync(root, wacPermit, financial, 10);
            Assert.Equal(new[] { issue.Id }, tail.RepricedHistoryIds);
            await StockReplayWacCostStateWriter.ApplyAsync(root, wacPermit);
            await coordinator.CompleteReplayInTransactionAsync(root,
                [countHistoryId, issue.Id]);
            await transaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(root);
            await transaction.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        var finalState = await db.StockCostStates.SingleAsync();
        Assert.Equal(160m, finalState.OnHandBaseQty);
        Assert.Equal(472.380950m, finalState.InventoryValue);
        var facts = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, company, branch, [issue]);
        Assert.Equal(147.619050m,
            Assert.Single(facts[issue.Id]).CostAmount);
        Assert.NotNull((await db.StockPostings.SingleAsync(p =>
            p.SourceDocumentType == "IV_STOCK_COUNT")).SealedAtUtc);
        var timeline = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, company, branch, "A100",
            new StockReplayEventKey(FixedToday,
                StockReplayEventKey.OrdinaryClass, 0, "before-count", 0));
        Assert.Equal(3, timeline.LaterEvents.Count);
        Assert.True(timeline.LaterEvents[0].EventId < 0);
        Assert.Equal(20m, timeline.LaterEvents[0].OldSignedValue);
        Assert.Equal(-147.619050m, timeline.LaterEvents[2].OldSignedValue);
    }

    [Fact]
    public async Task Initial_historical_WAC_count_uses_approved_fallback_on_empty_pool()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        var pile = await db.IvBalLocs.SingleAsync();
        pile.StdQty = 0m;
        var state = await db.StockCostStates.SingleAsync();
        state.OnHandBaseQty = 0m;
        state.InventoryValue = 0m;
        state.AverageUnitCost = 0m;
        state.CurrentUnitCost = 0m;
        var header = new IvStockCountHdr
        {
            CompanyCode = company, BranchCode = branch,
            CountNo = "CC-EMPTY-WAC", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = pile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 0m, PhysicalQty = 10m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        var basis = new StockCountCostBasis
        {
            CompanyCode = company, BranchCode = branch,
            StockCountLineId = line.Id,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            FrozenFallbackUnitCost = 4m,
            CostOverrideReason = "Approved empty-pool count",
            CostApprovedBy = "manager", CostApprovedAtUtc = now,
            CreatedAtUtc = now
        };
        db.ChangeTracker.Clear();
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, company, branch, header.Id);
        var physical = Assert.Single(sheet.Lines);
        var missingBasis = Assert.Throws<StockLedgerException>(() =>
            StockReplayWacCountEventFactory.CreateInitial(physical, null));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, missingBasis.Error.Code);
        var candidate = StockReplayWacCountEventFactory.CreateInitial(
            physical, basis);
        var financial = await StockReplayWacInsertionPlanner.BuildAsync(
            db, company, branch, "A100", physical, candidate);
        Assert.Equal(40m, financial.Projection.Candidate.SignedValue);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, company, branch, header.Id, [basis]);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        try
        {
            var countPermit = await StockReplayPhysicalPlanner
                .PrepareCountSheetWithinRootAsync(root, sheet);
            var wacPermit = await StockReplayWacInsertionPlanner
                .PrepareWithinRootAsync(root, financial);
            db.StockCountCostBases.Add(basis);
            (await db.IvBalLocs.SingleAsync(b => b.Id == pile.Id)).StdQty = 10m;
            await db.SaveChangesAsync();
            var written = await StockCountInitialHistoryWriter.AppendAsync(
                root, countPermit, new RunningNumberService());
            var historyId = Assert.Single(written.Generations)
                .AdjustmentHistoryId!.Value;
            var fact = await StockReplayWacInitialCountValuationWriter.AppendAsync(
                root, countPermit, wacPermit, financial, line.Id, 1);
            Assert.NotNull(fact);
            Assert.Equal(StockValuationSources.ManualApproved, fact.ValuationSource);
            Assert.Equal(40m, fact.CostAmount);
            await StockReplayWacCostStateWriter.ApplyAsync(root, wacPermit);
            await coordinator.CompleteReplayInTransactionAsync(root, [historyId]);
            await transaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(root);
            await transaction.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        Assert.Equal(10m, (await db.StockCostStates.SingleAsync()).OnHandBaseQty);
        Assert.Equal(40m, (await db.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Fact]
    public async Task Count_owned_fast_adjustment_keeps_midnight_history_under_end_of_day_anchor()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        var balanceId = await db.IvBalLocs.AsNoTracking().Select(b => b.Id).SingleAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            CountNo = "CC-FAST", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = balanceId,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1", LotNo = "",
            IStatus = "ACTIVE", PhysicalQty = 90m,
            SystemQty = 100m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        var basis = new StockCountCostBasis
        {
            CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
            StockCountLineId = line.Id, FrozenFallbackUnitCost = 2m,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = "Verified count cost", CostApprovedBy = "manager",
            CostApprovedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow
        };
        db.ChangeTracker.Clear();
        var sheetPlan = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        Assert.Equal(-10m, Assert.Single(sheetPlan.Lines).Projection.Candidate.SignedDelta);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, header.CompanyCode, header.BranchCode, header.Id, [basis]);
        const int batchNo = 700;
        var batch = new IvTrxBatch
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            BatchNo = batchNo, TrxDtTime = FixedToday,
            TrxType = IvTrxTypes.StockAdjustment, BatchStatus = IvBatchStatuses.New,
            RefNo = "CC-FAST"
        };
        batch.Details.Add(new IvTrxBatchDetail
        {
            CompanyCode = batch.CompanyCode, BranchCode = batch.BranchCode,
            BatchNo = batchNo, TrxLineNo = 1, TrxType = IvTrxTypes.StockAdjustment,
            ICode = "A100", IStatus = "ACTIVE", IClassCode = "RAW",
            FromBalLocId = balanceId, FrWarehouse = "MAIN", FrLocation = "BIN1",
            FrLotNo = "", FrStdQty = 10m, FrStdUom = "EA",
            Remarks = IvStockAdjustmentLineInvariant.CombineRemarks(
                IvAdjustmentReasons.Count, "CC-FAST")
        });
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var posting = InventoryLedgerTestFixture.CreateInventoryPosting(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        try
        {
            var permit = await StockReplayPhysicalPlanner.PrepareCountSheetWithinRootAsync(
                begin.Context!, sheetPlan);
            db.StockCountCostBases.Add(basis);
            db.IvTrxBatches.Add(batch);
            var trackedHeader = await db.IvStockCountHdrs.SingleAsync(h => h.Id == header.Id);
            trackedHeader.Status = IvStockCountStatuses.Posted;
            trackedHeader.PostedBatchNo = batchNo;
            await db.SaveChangesAsync();
            var result = await posting.PostStockCountAdjustmentInTransactionAsync(
                begin.Context!, db, batch.CompanyCode, batch.BranchCode,
                "TEST", batchNo, FixedToday);
            Assert.True(result.Succeeded, result.ErrorMessage);
            var history = Assert.Single(db.ChangeTracker.Entries<IvTrxHistory>()
                .Where(e => e.State == EntityState.Added).Select(e => e.Entity));
            Assert.Equal(FixedToday, history.TrxDtTime);
            Assert.Equal(begin.Context!.Posting.Id, history.StockPostingId);
            Assert.Equal(0, history.DocumentRevision);
            Assert.Equal("ORIGINAL", history.EntryRole);
            await db.SaveChangesAsync();
            var generations = await StockCountInitialGenerationWriter.AppendAsync(
                begin.Context!, permit, batchNo);
            var generation = Assert.Single(generations);
            Assert.Equal(-10m, generation.SignedAdjustmentQty);
            Assert.Equal(history.Id, generation.AdjustmentHistoryId);
            Assert.Equal(batchNo, generation.AdjustmentBatchNo);
        }
        finally
        {
            await coordinator.AbortInTransactionAsync(begin.Context!);
            await transaction.RollbackAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Count_inbound_generation_values_and_seals_with_approved_frozen_cost(
        bool standardCost)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        long? standardRevisionId = null;
        if (standardCost)
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(
                db, "A100", 100m, 2m, StockCostMethods.Standard);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 2m);
            standardRevisionId = await db.ItemStandardCostRevisions.AsNoTracking()
                .Select(r => r.Id).SingleAsync();
        }
        var balanceId = await db.IvBalLocs.AsNoTracking().Select(b => b.Id).SingleAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            CountNo = "CC-INBOUND", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = balanceId,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1", LotNo = "",
            IStatus = "ACTIVE", PhysicalQty = 110m,
            SystemQty = 100m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        var approvedAt = DateTime.UtcNow;
        var basis = new StockCountCostBasis
        {
            CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
            StockCountLineId = line.Id, FrozenFallbackUnitCost = 2m,
            CostBasisKind = standardCost
                ? StockCountCostBasisKinds.StandardRevision
                : StockCountCostBasisKinds.ManualApproved,
            StandardCostRevisionId = standardRevisionId,
            CostEvidenceType = standardCost ? null : StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = standardCost ? null : "Verified count cost",
            CostApprovedBy = standardCost ? null : "manager",
            CostApprovedAtUtc = standardCost ? null : approvedAt,
            CreatedAtUtc = approvedAt
        };
        db.ChangeTracker.Clear();
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        if (standardCost)
        {
            basis.FrozenFallbackUnitCost = 3m;
            var incorrectRevisionCost = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockCountAnchorCommandFactory.CreateAsync(
                    db, header.CompanyCode, header.BranchCode, header.Id, [basis]));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                incorrectRevisionCost.Error.Code);
            basis.FrozenFallbackUnitCost = 2m;
        }
        StockReplayStandardCountSheetInsertionPlan? standardPlan = null;
        if (standardCost)
        {
            standardPlan = await StockReplayStandardCountSheetInsertionPlanner
                .BuildAsync(db, sheet, "A100", [basis]);
            Assert.Equal(110m, standardPlan.Projection.FinalQuantity);
            Assert.Equal(220m, standardPlan.Projection.FinalValue);
        }
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, header.CompanyCode, header.BranchCode, header.Id, [basis]);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var posting = InventoryLedgerTestFixture.CreateInventoryPosting(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        try
        {
            var permit = await StockReplayPhysicalPlanner.PrepareCountSheetWithinRootAsync(root, sheet);
            if (standardPlan is not null)
            {
                root.CostMethod = StockCostMethods.Standard;
                var standardPermit = await StockReplayStandardCountSheetInsertionPlanner
                    .PrepareWithinRootAsync(root, standardPlan);
                Assert.Equal(standardPlan.PlanHash, standardPermit.PlanHash);
                Assert.Equal(110m, standardPermit.ChangedPiles[balanceId]);
                var missingFacts = await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayStandardCostStateWriter.ApplyAsync(root, standardPermit));
                Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, missingFacts.Error.Code);
                var standardState = await db.StockCostStates.SingleAsync(s =>
                    s.CostMethod == StockCostMethods.Standard);
                standardState.InventoryValue = 201m;
                await db.SaveChangesAsync();
                var staleStandard = await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayStandardCountSheetInsertionPlanner
                        .PrepareWithinRootAsync(root, standardPlan));
                Assert.Equal(StockLedgerErrorCodes.StaleDocument, staleStandard.Error.Code);
                standardState.InventoryValue = 200m;
                await db.SaveChangesAsync();
            }
            basis.FrozenFallbackUnitCost = 3m;
            Assert.False(StockCountAnchorCommandFactory.MatchesPersistedBasisSnapshot(root, [basis]));
            basis.FrozenFallbackUnitCost = 2m;
            db.StockCountCostBases.Add(basis);
            await db.SaveChangesAsync();
            const int batchNo = 701;
            var batch = new IvTrxBatch
            {
                CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
                BatchNo = batchNo, TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.StockAdjustment, BatchStatus = IvBatchStatuses.New,
                RefNo = header.CountNo
            };
            batch.Details.Add(new IvTrxBatchDetail
            {
                CompanyCode = batch.CompanyCode, BranchCode = batch.BranchCode,
                BatchNo = batchNo, TrxLineNo = 1, TrxType = IvTrxTypes.StockAdjustment,
                ICode = "A100", IStatus = "ACTIVE", IClassCode = "RAW",
                ToBalLocId = balanceId, ToWarehouse = "MAIN", ToLocation = "BIN1",
                ToLotNo = "", ToStdQty = 10m, ToStdUom = "EA",
                UnitPrice = 2m, StockCountCostBasisId = basis.Id,
                CostEvidenceType = standardCost ? null : InventoryCostEvidenceTypes.ManualApproved,
                CostOverrideReason = basis.CostOverrideReason,
                CostApprovedBy = basis.CostApprovedBy,
                CostApprovedAtUtc = basis.CostApprovedAtUtc,
                Remarks = IvStockAdjustmentLineInvariant.CombineRemarks(
                    IvAdjustmentReasons.Count, header.CountNo)
            });
            db.IvTrxBatches.Add(batch);
            var trackedHeader = await db.IvStockCountHdrs.SingleAsync(h => h.Id == header.Id);
            trackedHeader.Status = IvStockCountStatuses.Posted;
            trackedHeader.PostedBatchNo = batchNo;
            await db.SaveChangesAsync();

            var result = await posting.PostStockCountAdjustmentInTransactionAsync(
                root, db, batch.CompanyCode, batch.BranchCode,
                "TEST", batchNo, FixedToday);
            Assert.True(result.Succeeded, result.ErrorMessage);
            await db.SaveChangesAsync();
            var countHistory = await db.IvTrxHistories.AsNoTracking()
                .SingleAsync(h => h.StockPostingId == root.Posting.Id);
            var orphan = await Assert.ThrowsAsync<StockLedgerException>(() =>
                coordinator.CompleteReplayInTransactionAsync(root,
                    [countHistory.Id]));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, orphan.Error.Code);
            Assert.Null(root.Posting.SealedAtUtc);
            var generations = await StockCountInitialGenerationWriter.AppendAsync(
                root, permit, batchNo);
            Assert.Equal(10m, Assert.Single(generations).SignedAdjustmentQty);
            await coordinator.CompleteInTransactionAsync(root);
            await transaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(root);
            await transaction.RollbackAsync();
            throw;
        }

        db.ChangeTracker.Clear();
        var fact = await db.StockValuationFacts.SingleAsync(f => f.MovementCode == "ADJUST_IN");
        Assert.Equal(10m, fact.BaseQty);
        Assert.Equal(2m, fact.UnitCost);
        Assert.Equal(20m, fact.CostAmount);
        // Existing on-hand WAC is authoritative; its approved basis remains
        // frozen fallback evidence. Standard uses the event-date revision.
        Assert.Equal(standardCost ? StockValuationSources.Standard
            : StockValuationSources.MovingAverage, fact.ValuationSource);
        Assert.Equal(110m, (await db.StockCostStates.SingleAsync(s =>
            s.CostMethod == (standardCost ? StockCostMethods.Standard
                : StockCostMethods.MovingAverage))).OnHandBaseQty);
        Assert.NotNull((await db.StockPostings.SingleAsync()).SealedAtUtc);
        var beforeCount = await StockReplayPhysicalAsOfReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, [balanceId],
            FixedToday.AddHours(12));
        var afterCount = await StockReplayPhysicalAsOfReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, [balanceId],
            StockReplayEventKey.CountEndOfDay(FixedToday));
        Assert.Equal(100m, beforeCount[balanceId].AsOfBaseQty);
        Assert.Equal(110m, afterCount[balanceId].AsOfBaseQty);
        if (standardCost)
        {
            var tail = await StockReplayStandardFinancialTailReader.ReadLaterAsync(
                db, header.CompanyCode, header.BranchCode, "A100",
                new StockReplayEventKey(FixedToday.AddHours(12),
                    StockReplayEventKey.OrdinaryClass, long.MaxValue,
                    "\uffff", int.MaxValue));
            var countEvent = Assert.Single(tail.LaterEvents);
            Assert.Equal(StockReplayStandardEventKinds.Count, countEvent.Kind);
            Assert.Equal(10m, countEvent.OldSignedQuantity);
            Assert.Equal(20m, countEvent.OldSignedValue);
            Assert.Equal(standardRevisionId, countEvent.StandardCostRevisionId);

            var nextDay = FixedToday.AddDays(1);
            var revision = await db.ItemStandardCostRevisions.SingleAsync();
            revision.EffectiveTo = nextDay;
            db.ItemStandardCostRevisions.Add(new ItemStandardCostRevision
            {
                CompanyCode = header.CompanyCode,
                BranchCode = header.BranchCode,
                ItemCode = "A100", EffectiveFrom = nextDay,
                MaterialCost = 3m, TotalStandardCost = 3m,
                Revision = 2, Status = ItemStandardCostRevisionStatuses.Approved,
                ApprovedBy = "TEST", ApprovedAtUtc = DateTime.UtcNow,
                CreatedBy = "TEST", CreatedAtUtc = DateTime.UtcNow,
                RowVersion = [1]
            });
            var countPosting = await db.StockPostings.SingleAsync();
            var at = nextDay.AddHours(8);
            var revaluation = new StockPosting
            {
                CompanyCode = header.CompanyCode,
                BranchCode = header.BranchCode,
                LedgerEpochId = countPosting.LedgerEpochId,
                PostingSequence = countPosting.PostingSequence + 1,
                RequestId = Guid.NewGuid(),
                CommandType = "STANDARD_COST_REVALUATION",
                RequestFingerprint = new string('A', 64),
                SourceModule = "INVENTORY_COSTING",
                SourceDocumentType = "STANDARD_COST_REVALUATION",
                SourceDocumentId = "TEST-REVALUATION",
                SourceDocumentNo = "TEST-REVALUATION",
                SourceSnapshotJson = "{}",
                SourceSnapshotHash = new string('B', 64),
                EffectiveAt = at, BusinessDate = nextDay,
                PeriodKey = countPosting.PeriodKey,
                PostedAtUtc = DateTime.UtcNow, PostedBy = "TEST",
                SealedAtUtc = DateTime.UtcNow
            };
            db.StockPostings.Add(revaluation);
            await db.SaveChangesAsync();
            db.StockValuationFacts.Add(new StockValuationFact
            {
                CompanyCode = header.CompanyCode,
                BranchCode = header.BranchCode,
                LedgerEpochId = countPosting.LedgerEpochId,
                StockPostingId = revaluation.Id,
                PostingLineNo = 1, SplitOrdinal = 0,
                SourceLineId = "STANDARD_COST_REVALUATION:TEST-REVALUATION:1:0",
                SourceDocumentType = revaluation.SourceDocumentType,
                SourceDocumentId = revaluation.SourceDocumentId,
                SourceDocumentNo = revaluation.SourceDocumentNo,
                SourceDocumentLine = "1",
                EffectiveAt = at, BusinessDate = nextDay,
                PeriodKey = revaluation.PeriodKey,
                ItemCode = "A100", BaseUom = "EA",
                MovementCode = "STANDARD_REVALUE_IN",
                Direction = 1, BaseQty = 0m,
                CostMethod = StockCostMethods.Standard,
                UnitCost = 0m, CostAmount = 110m,
                BaseCostAmount = 110m,
                ValuationSource = StockValuationSources.StandardRevaluation,
                ValuationStatus = StockValuationStatuses.Valued,
                CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
            });
            var standardState = await db.StockCostStates.SingleAsync(s =>
                s.CostMethod == StockCostMethods.Standard);
            standardState.InventoryValue = 330m;
            standardState.CurrentUnitCost = 3m;
            standardState.AverageUnitCost = 3m;
            standardState.LastPostingSequence = revaluation.PostingSequence;
            await db.SaveChangesAsync();
            var issueAt = nextDay.AddHours(10);
            var issuePosting = new StockPosting
            {
                CompanyCode = header.CompanyCode,
                BranchCode = header.BranchCode,
                LedgerEpochId = countPosting.LedgerEpochId,
                PostingSequence = revaluation.PostingSequence + 1,
                RequestId = Guid.NewGuid(),
                CommandType = "IV_MI_POST",
                RequestFingerprint = new string('C', 64),
                SourceModule = "INVENTORY",
                SourceDocumentType = "IV_TRX",
                SourceDocumentId = "TEST-STD-ISSUE",
                SourceDocumentNo = "TEST-STD-ISSUE",
                SourceSnapshotJson = "{}",
                SourceSnapshotHash = new string('D', 64),
                EffectiveAt = issueAt, BusinessDate = nextDay,
                PeriodKey = countPosting.PeriodKey,
                PostedAtUtc = DateTime.UtcNow, PostedBy = "TEST",
                SealedAtUtc = DateTime.UtcNow
            };
            db.StockPostings.Add(issuePosting);
            await db.SaveChangesAsync();
            var laterIssue = new IvTrxHistory
            {
                CompanyCode = header.CompanyCode,
                BranchCode = header.BranchCode,
                BatchNo = 702, TrxLineNo = 1, TrxDtTime = issueAt,
                TrxType = IvTrxTypes.MiscellaneousIssue,
                BatchStatus = IvBatchStatuses.Posted,
                ICode = "A100", FromBalLocId = balanceId,
                FrStdQty = 10m, FrStdUom = "EA",
                FrWarehouse = "MAIN", FrLocation = "BIN1",
                IStatus = "ACTIVE", LedgerVersion = 2,
                LedgerEpochId = countPosting.LedgerEpochId,
                StockPostingId = issuePosting.Id, PostingLineNo = 1,
                EntryRole = "ORIGINAL",
                ValuationStatus = StockValuationStatuses.Valued
            };
            db.IvTrxHistories.Add(laterIssue);
            await db.SaveChangesAsync();
            db.StockValuationFacts.Add(new StockValuationFact
            {
                CompanyCode = header.CompanyCode,
                BranchCode = header.BranchCode,
                LedgerEpochId = countPosting.LedgerEpochId,
                StockPostingId = issuePosting.Id,
                PostingLineNo = 1, SplitOrdinal = 0,
                SourceLineId = "IV_TRX:TEST-STD-ISSUE:1:1:0",
                SourceDocumentType = issuePosting.SourceDocumentType,
                SourceDocumentId = issuePosting.SourceDocumentId,
                SourceDocumentNo = issuePosting.SourceDocumentNo,
                SourceDocumentLine = "1",
                EffectiveAt = issueAt, BusinessDate = nextDay,
                PeriodKey = issuePosting.PeriodKey,
                ItemCode = "A100", WarehouseCode = "MAIN",
                LocationCode = "BIN1", ItemStatus = "ACTIVE",
                BaseUom = "EA", MovementCode = "ISSUE",
                Direction = -1, BaseQty = 10m,
                CostMethod = StockCostMethods.Standard,
                UnitCost = 3m, CostAmount = 30m,
                BaseCostAmount = 30m,
                ValuationSource = StockValuationSources.Standard,
                ValuationStatus = StockValuationStatuses.Valued,
                InventoryHistoryId = laterIssue.Id,
                FromBalLocId = balanceId,
                CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
            });
            var liveBeforeReplay = await db.IvBalLocs.SingleAsync(b =>
                b.Id == balanceId);
            liveBeforeReplay.StdQty = 100m;
            liveBeforeReplay.TransDate = issueAt;
            standardState.InventoryValue = 300m;
            standardState.OnHandBaseQty = 100m;
            standardState.LastPostingSequence = issuePosting.PostingSequence;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var later = await StockReplayStandardFinancialTailReader.ReadLaterAsync(
                db, header.CompanyCode, header.BranchCode, "A100",
                new StockReplayEventKey(FixedToday.AddHours(12),
                    StockReplayEventKey.OrdinaryClass, long.MaxValue,
                    "\uffff", int.MaxValue));
            Assert.Equal(3, later.LaterEvents.Count);
            Assert.Equal(StockReplayStandardEventKinds.Revaluation,
                later.LaterEvents[1].Kind);
            Assert.Equal(110m, later.LaterEvents[1].OldSignedValue);
            Assert.Equal(3m, later.LaterEvents[1].UnitCost);
            Assert.Equal(-30m, later.LaterEvents[2].OldSignedValue);

            var originalRevaluation = await db.StockValuationFacts
                .SingleAsync(f => f.StockPostingId == revaluation.Id);
            var branchSequence = await db.StockPostingBranchSequences.SingleAsync();
            branchSequence.LastSequence = issuePosting.PostingSequence;
            await db.SaveChangesAsync();
            var secondHeader = new IvStockCountHdr
            {
                CompanyCode = header.CompanyCode,
                BranchCode = header.BranchCode,
                CountNo = "CC-STD-LATE", CountDate = FixedToday,
                Status = IvStockCountStatuses.Counted
            };
            var secondLine = new IvStockCountLine
            {
                Header = secondHeader, LineNumber = 1, BalLocId = balanceId,
                ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
                LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
                SystemQty = 110m, PhysicalQty = 120m
            };
            db.IvStockCountLines.Add(secondLine);
            await db.SaveChangesAsync();
            var secondBasis = new StockCountCostBasis
            {
                CompanyCode = header.CompanyCode,
                BranchCode = header.BranchCode,
                StockCountLineId = secondLine.Id,
                CostBasisKind = StockCountCostBasisKinds.StandardRevision,
                StandardCostRevisionId = standardRevisionId,
                FrozenFallbackUnitCost = 2m,
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ChangeTracker.Clear();
            var lateSheet = await StockReplayPhysicalPlanner
                .BuildCountSheetInsertionAsync(db, header.CompanyCode,
                    header.BranchCode, secondHeader.Id);
            var latePostingPlan = await StockReplayStandardCountSheetPostingWriter
                .BuildAsync(db, lateSheet, [secondBasis]);
            var latePlan = Assert.Single(latePostingPlan.Items);
            Assert.Equal(110m, latePlan.Projection.FinalQuantity);
            Assert.Equal(330m, latePlan.Projection.FinalValue);
            var lateCommand = await StockCountAnchorCommandFactory.CreateAsync(
                db, header.CompanyCode, header.BranchCode,
                secondHeader.Id, [secondBasis]);
            await using var correctionTransaction =
                await db.Database.BeginTransactionAsync();
            var correctionCoordinator = InventoryLedgerTestFixture
                .CreateCoordinator(_factory);
            var correction = await correctionCoordinator.BeginInTransactionAsync(
                db, lateCommand);
            Assert.NotNull(correction.Context);
            var correctionRoot = correction.Context!;
            try
            {
                var prepared = await StockReplayStandardCountSheetPostingWriter
                    .PrepareWithinRootAsync(correctionRoot, latePostingPlan);
                var standardPermit = prepared.ItemPermits["A100"];
                var forgedSteps = latePlan.Projection.LaterSteps
                    .Select(s => s.EventId == long.MaxValue - originalRevaluation.Id
                        ? s with { SignedValue = s.SignedValue + 1m } : s)
                    .ToArray();
                var forged = latePlan with
                {
                    Projection = latePlan.Projection with { LaterSteps = forgedSteps }
                };
                var forgedRefusal = await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayStandardRevaluationRepricingWriter.AppendAsync(
                        correctionRoot, standardPermit, forged,
                        originalRevaluation.Id, 10, 11));
                Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                    forgedRefusal.Error.Code);
                var written = await StockReplayStandardCountSheetPostingWriter
                    .AppendWithinRootAsync(correctionRoot, prepared,
                        [secondBasis], new RunningNumberService());
                Assert.Equal(20m, Assert.Single(written.InitialFacts).CostAmount);
                Assert.Equal([originalRevaluation.Id],
                    written.RepricedRevaluationFactIds);
                var correctedFacts = await db.StockValuationFacts.AsNoTracking()
                    .Where(f => f.StockPostingId == correctionRoot.Posting.Id
                        && f.InventoryHistoryId == null)
                    .OrderBy(f => f.PostingLineNo).ToArrayAsync();
                Assert.Equal(new[] { 110m, 120m },
                    correctedFacts.Select(f => f.CostAmount));
                Assert.Equal(originalRevaluation.Id,
                    correctedFacts[1].OriginalValuationFactId);
                await correctionCoordinator.CompleteReplayInTransactionAsync(
                    correctionRoot, written.RequiredActiveHistoryIds);
                await correctionTransaction.CommitAsync();
                db.ChangeTracker.Clear();
                var corrected = await StockReplayStandardFinancialTailReader
                    .ReadLaterAsync(db, header.CompanyCode, header.BranchCode,
                        "A100", new StockReplayEventKey(FixedToday.AddHours(12),
                            StockReplayEventKey.OrdinaryClass, long.MaxValue,
                            "\uffff", int.MaxValue));
                Assert.Equal(120m, corrected.LaterEvents[2].OldSignedValue);
                Assert.Equal(later.LaterEvents[1].Key,
                    corrected.LaterEvents[2].Key);
                Assert.Equal(-30m, corrected.LaterEvents[3].OldSignedValue);
                Assert.Equal(issueAt, (await db.IvBalLocs.SingleAsync(b =>
                    b.Id == balanceId)).TransDate);
            }
            catch
            {
                await correctionCoordinator.AbortInTransactionAsync(correctionRoot);
                await correctionTransaction.RollbackAsync();
                throw;
            }
        }
    }

    [Fact]
    public async Task Generic_adjustment_post_and_rollback_reject_a_count_generation_batch()
    {
        var service = InventoryLedgerTestFixture.CreateStockCount(_factory, FixedToday);
        var saved = await service.SaveAsync(new IvStockCountSaveRequest
        {
            CountDate = FixedToday, WHCode = "MAIN", IncludeZeroQty = true
        });
        Assert.True(saved.Succeeded, saved.ErrorMessage);
        Assert.True((await service.GenerateAsync(saved.Id, discardCounts: false)).Succeeded);
        var sheet = (await service.GetAsync(saved.CountNo!)).Document!;
        var line = Assert.Single(sheet.Lines);
        Assert.True((await service.SaveCountsAsync(sheet.Id,
            [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 110m }],
            null)).Succeeded);
        var posted = await service.PostAsync(sheet.Id);
        Assert.True(posted.Succeeded, posted.ErrorMessage);
        var batchNo = posted.PostedBatchNo!.Value;

        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
            var history = await db.IvTrxHistories.SingleAsync(h => h.BatchNo == batchNo);
            var anchor = await db.StockPostings.SingleAsync(p => p.Id == history.StockPostingId);
            db.StockCountReplayGenerations.Add(new StockCountReplayGeneration
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                StockCountLineId = line.Id,
                AnchorPostingId = anchor.Id, WritePostingId = anchor.Id,
                AnchorPostingSequence = anchor.PostingSequence,
                EffectiveAt = StockReplayEventKey.CountEndOfDay(FixedToday),
                SignedAdjustmentQty = 10m, AdjustmentBatchNo = batchNo,
                AdjustmentHistoryId = history.Id, CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var posting = InventoryLedgerTestFixture.CreateInventoryPosting(_factory);
        var repost = await posting.PostAsync(IvTrxTypes.StockAdjustment, [batchNo]);
        Assert.False(repost.Succeeded);
        Assert.Contains("Stock Count workflow", repost.ErrorMessage);
        var rollback = await posting.RollbackAsync(IvTrxTypes.StockAdjustment, [batchNo]);
        Assert.False(rollback.Succeeded);
        Assert.Contains("Stock Count workflow", rollback.ErrorMessage);
        var countRollback = await service.RollbackAsync(sheet.Id, "recount");
        Assert.False(countRollback.Succeeded);
        Assert.Contains("Replay-aware rollback", countRollback.ErrorMessage);
        var recover = await service.RecoverAsync(sheet.Id);
        Assert.False(recover.Succeeded);
        Assert.Contains("Reconcile active lineage", recover.ErrorMessage);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(IvBatchStatuses.Posted,
            (await verify.IvTrxBatches.SingleAsync(b => b.BatchNo == batchNo)).BatchStatus);
        Assert.Equal(IvStockCountStatuses.Posted,
            (await verify.IvStockCountHdrs.SingleAsync(h => h.Id == sheet.Id)).Status);

        // Even a stale ROLLED_BACK header cannot invite legacy edits, cancel,
        // or repost while an append-only V2 generation still owns the sheet.
        var staleHeader = await verify.IvStockCountHdrs.SingleAsync(h => h.Id == sheet.Id);
        staleHeader.Status = IvStockCountStatuses.RolledBack;
        await verify.SaveChangesAsync();
        var recount = await service.SaveCountsAsync(sheet.Id,
            [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 111m }],
            null);
        Assert.False(recount.Succeeded);
        Assert.Contains("posted count evidence", recount.ErrorMessage);
        var cancel = await service.CancelAsync([sheet.Id]);
        Assert.False(cancel.Succeeded);
        Assert.Contains("V2 count generations", cancel.ErrorMessage);
        var countRepost = await service.PostAsync(sheet.Id);
        Assert.False(countRepost.Succeeded);
        Assert.Contains("Replay-aware repost", countRepost.ErrorMessage);
    }
}
