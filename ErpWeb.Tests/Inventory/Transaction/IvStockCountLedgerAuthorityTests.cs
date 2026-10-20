using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

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
        db.ChangeTracker.Clear();
        var access = new Mock<IAccessRightService>(MockBehavior.Strict);
        var rootPlan = await StockReplayCountSheetRootWriter.BuildAsync(
            db, access.Object, company, branch, header.Id, [], "unused", DateTime.UtcNow);
        var postingPlan = Assert.IsType<StockReplayStandardCountSheetPostingPlan>(rootPlan.Standard);
        Assert.Equal(220m, Assert.Single(postingPlan.Items).Projection.FinalValue);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, rootPlan.Command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        try
        {
            var written = await StockReplayCountSheetRootWriter.AppendWithinRootAsync(
                root, rootPlan, new RunningNumberService());
            var result = Assert.IsType<StockReplayStandardCountSheetPostResult>(written.Standard);
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

        var firstBatch = (await db.IvStockCountHdrs.SingleAsync()).PostedBatchNo;
        var firstRemoval = await StockReplayCountSheetRemovalRootWriter
            .BuildAsync(db, company, branch, header.Id);
        await using (var rollbackTx = await db.Database.BeginTransactionAsync())
        {
            var rollbackBegin = await coordinator.BeginInTransactionAsync(
                db, firstRemoval.Command);
            Assert.NotNull(rollbackBegin.Context);
            try
            {
                var removed = await StockReplayCountSheetRemovalRootWriter
                    .AppendWithinRootAsync(rollbackBegin.Context!, firstRemoval,
                        new RunningNumberService(), "Count entered in error",
                        DateTime.UtcNow);
                await coordinator.CompleteReplayInTransactionAsync(
                    rollbackBegin.Context!, removed.RequiredActiveHistoryIds);
                await rollbackTx.CommitAsync();
            }
            catch
            {
                await coordinator.AbortInTransactionAsync(rollbackBegin.Context!);
                await rollbackTx.RollbackAsync();
                throw;
            }
        }
        db.ChangeTracker.Clear();
        var repost = await StockReplayCountSheetRootWriter.BuildAsync(
            db, access.Object, company, branch, header.Id, [],
            "unused", DateTime.UtcNow);
        Assert.Equal(1, repost.Command.DocumentRevision);
        Assert.Equal((await db.StockCountCostBases.SingleAsync()).Id,
            Assert.Single(repost.FrozenBases).Id);
        await using (var repostTx = await db.Database.BeginTransactionAsync())
        {
            var repostBegin = await coordinator.BeginInTransactionAsync(
                db, repost.Command);
            Assert.NotNull(repostBegin.Context);
            try
            {
                var written = await StockReplayCountSheetRootWriter
                    .AppendWithinRootAsync(repostBegin.Context!, repost,
                        new RunningNumberService());
                Assert.Single(written.RequiredActiveHistoryIds);
                await coordinator.CompleteReplayInTransactionAsync(
                    repostBegin.Context!, written.RequiredActiveHistoryIds);
                await repostTx.CommitAsync();
            }
            catch
            {
                await coordinator.AbortInTransactionAsync(repostBegin.Context!);
                await repostTx.RollbackAsync();
                throw;
            }
        }
        db.ChangeTracker.Clear();
        Assert.Equal(firstBatch,
            (await db.IvStockCountHdrs.SingleAsync()).PostedBatchNo);
        var repostLeaf = (await StockCountGenerationResolver.ResolveActiveAsync(
            db, company, branch, [line.Id]))[line.Id];
        Assert.False(repostLeaf.IsRemoval);
        Assert.NotEqual(firstBatch, repostLeaf.AdjustmentBatchNo);
        Assert.Equal(220m,
            (await db.StockCostStates.SingleAsync(s =>
                s.CostMethod == StockCostMethods.Standard)).InventoryValue);

        var secondRemoval = await StockReplayCountSheetRemovalRootWriter
            .BuildAsync(db, company, branch, header.Id);
        await using (var secondRollbackTx = await db.Database.BeginTransactionAsync())
        {
            var secondBegin = await coordinator.BeginInTransactionAsync(
                db, secondRemoval.Command);
            Assert.NotNull(secondBegin.Context);
            try
            {
                var removed = await StockReplayCountSheetRemovalRootWriter
                    .AppendWithinRootAsync(secondBegin.Context!, secondRemoval,
                        new RunningNumberService(), "Reposted count entered in error",
                        DateTime.UtcNow);
                await coordinator.CompleteReplayInTransactionAsync(
                    secondBegin.Context!, removed.RequiredActiveHistoryIds);
                await secondRollbackTx.CommitAsync();
            }
            catch
            {
                await coordinator.AbortInTransactionAsync(secondBegin.Context!);
                await secondRollbackTx.RollbackAsync();
                throw;
            }
        }
        db.ChangeTracker.Clear();
        Assert.Equal(firstBatch,
            (await db.IvStockCountHdrs.SingleAsync()).PostedBatchNo);
        Assert.True((await StockCountGenerationResolver.ResolveActiveAsync(
            db, company, branch, [line.Id]))[line.Id].IsRemoval);
        Assert.Equal(200m,
            (await db.StockCostStates.SingleAsync(s =>
                s.CostMethod == StockCostMethods.Standard)).InventoryValue);
    }

    [Theory]
    [InlineData(false, 110, 98, 196)]
    [InlineData(true, 110, 102, 204)]
    [InlineData(false, 100, 98, 196)]
    [InlineData(true, 100, 102, 204)]
    public async Task Standard_count_rollback_preserves_later_movement_and_its_value(
        bool receipt, int physicalQty, int expectedQuantity, int expectedValue)
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
        var header = new IvStockCountHdr
        {
            CompanyCode = company, BranchCode = branch,
            CountNo = "CC-STD-ISSUE-REMOVE", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        db.IvStockCountLines.Add(new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = pile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 100m, PhysicalQty = physicalQty
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var access = new Mock<IAccessRightService>(MockBehavior.Strict);
        var postingPlan = await StockReplayCountSheetRootWriter.BuildAsync(
            db, access.Object, company, branch, header.Id, [],
            "unused", DateTime.UtcNow);
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var begin = await coordinator.BeginInTransactionAsync(
                db, postingPlan.Command);
            Assert.NotNull(begin.Context);
            try
            {
                var written = await StockReplayCountSheetRootWriter
                    .AppendWithinRootAsync(begin.Context!, postingPlan,
                        new RunningNumberService());
                await coordinator.CompleteReplayInTransactionAsync(
                    begin.Context!, written.RequiredActiveHistoryIds);
                await transaction.CommitAsync();
            }
            catch
            {
                await coordinator.AbortInTransactionAsync(begin.Context!);
                await transaction.RollbackAsync();
                throw;
            }
        }
        db.ChangeTracker.Clear();
        var countPosting = await db.StockPostings.SingleAsync();
        var issueAt = FixedToday.AddDays(1).AddHours(9);
        var sourceNo = receipt ? "STD-RECEIPT" : "STD-ISSUE";
        var issuePosting = new StockPosting
        {
            CompanyCode = company, BranchCode = branch,
            LedgerEpochId = countPosting.LedgerEpochId,
            PostingSequence = countPosting.PostingSequence + 1,
            RequestId = Guid.NewGuid(),
            CommandType = receipt ? "IV_MR_POST" : "IV_MI_POST",
            RequestFingerprint = new string('C', 64),
            SourceModule = "INVENTORY", SourceDocumentType = "IV_TRX",
            SourceDocumentId = sourceNo, SourceDocumentNo = sourceNo,
            SourceSnapshotJson = "{}",
            SourceSnapshotHash = new string('D', 64),
            EffectiveAt = issueAt, BusinessDate = issueAt.Date,
            PeriodKey = issueAt.ToString("yyyy-MM"),
            PostedAtUtc = DateTime.UtcNow, PostedBy = "TEST",
            SealedAtUtc = DateTime.UtcNow
        };
        db.StockPostings.Add(issuePosting);
        await db.SaveChangesAsync();
        var issue = new IvTrxHistory
        {
            CompanyCode = company, BranchCode = branch,
            BatchNo = 902, TrxLineNo = 1, TrxDtTime = issueAt,
            TrxType = receipt ? IvTrxTypes.MiscellaneousReceipt
                : IvTrxTypes.MiscellaneousIssue,
            BatchStatus = IvBatchStatuses.Posted,
            ICode = "A100",
            FromBalLocId = receipt ? null : pile.Id,
            ToBalLocId = receipt ? pile.Id : null,
            FrStdQty = receipt ? null : 2m,
            ToStdQty = receipt ? 2m : null,
            FrStdUom = receipt ? null : "EA",
            ToStdUom = receipt ? "EA" : null,
            FrWarehouse = receipt ? null : "MAIN",
            ToWarehouse = receipt ? "MAIN" : null,
            FrLocation = receipt ? null : "BIN1",
            ToLocation = receipt ? "BIN1" : null,
            IStatus = "ACTIVE", LedgerVersion = 2,
            LedgerEpochId = countPosting.LedgerEpochId,
            StockPostingId = issuePosting.Id, PostingLineNo = 1,
            EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        db.IvTrxHistories.Add(issue);
        await db.SaveChangesAsync();
        db.StockValuationFacts.Add(new StockValuationFact
        {
            CompanyCode = company, BranchCode = branch,
            LedgerEpochId = countPosting.LedgerEpochId,
            StockPostingId = issuePosting.Id,
            PostingLineNo = 1, SplitOrdinal = 0,
            SourceLineId = $"IV_TRX:{sourceNo}:1:1:0",
            SourceDocumentType = issuePosting.SourceDocumentType,
            SourceDocumentId = issuePosting.SourceDocumentId,
            SourceDocumentNo = issuePosting.SourceDocumentNo,
            SourceDocumentLine = "1",
            EffectiveAt = issueAt, BusinessDate = issueAt.Date,
            PeriodKey = issuePosting.PeriodKey,
            ItemCode = "A100", WarehouseCode = "MAIN",
            LocationCode = "BIN1", ItemStatus = "ACTIVE",
            BaseUom = "EA", MovementCode = receipt ? "RECEIPT" : "ISSUE",
            Direction = receipt ? 1 : -1, BaseQty = 2m,
            CostMethod = StockCostMethods.Standard,
            UnitCost = 2m, CostAmount = 4m,
            BaseCostAmount = 4m,
            ValuationSource = StockValuationSources.Standard,
            ValuationStatus = StockValuationStatuses.Valued,
            InventoryHistoryId = issue.Id,
            FromBalLocId = receipt ? null : pile.Id,
            ToBalLocId = receipt ? pile.Id : null,
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
        });
        pile = await db.IvBalLocs.SingleAsync(b => b.Id == pile.Id);
        pile.StdQty = physicalQty + (receipt ? 2m : -2m);
        pile.TransDate = issueAt;
        var state = await db.StockCostStates.SingleAsync(s =>
            s.CostMethod == StockCostMethods.Standard);
        state.OnHandBaseQty = pile.StdQty;
        state.InventoryValue = physicalQty * 2m + (receipt ? 4m : -4m);
        state.LastPostingSequence = issuePosting.PostingSequence;
        var sequence = await db.StockPostingBranchSequences.SingleAsync();
        sequence.LastSequence = issuePosting.PostingSequence;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var plan = await StockReplayCountSheetRemovalRootWriter.BuildAsync(
            db, company, branch, header.Id);
        if (physicalQty == 100)
        {
            var zero = Assert.IsType<StockReplayZeroCountRemovalRootPlan>(
                plan.Zero);
            Assert.Single(Assert.Single(zero.Removal.Lines).Physical.LaterEvents);
        }
        else
        {
            var item = Assert.Single(
                Assert.IsType<StockReplayStandardCountSheetRemovalPlan>(
                    plan.Standard).Items);
            Assert.Equal(expectedQuantity, item.FinalQuantity);
            Assert.Equal(expectedValue, item.FinalValue);
        }
        await using var rollback = await db.Database.BeginTransactionAsync();
        var reversal = await coordinator.BeginInTransactionAsync(db, plan.Command);
        Assert.NotNull(reversal.Context);
        try
        {
            if (receipt)
            {
                var pendingFact = await db.StockValuationFacts.SingleAsync(f =>
                    f.InventoryHistoryId == issue.Id);
                pendingFact.CostAmount = 5m;
                var pending = await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayCountSheetRemovalRootWriter.AppendWithinRootAsync(
                        reversal.Context!, plan, new RunningNumberService(),
                        "Count entered in error", DateTime.UtcNow));
                Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                    pending.Error.Code);
                Assert.Equal(physicalQty == 100 ? 0 : 1,
                    await db.IvTrxBatches.CountAsync());
                await db.Entry(pendingFact).ReloadAsync();
            }
            var removed = await StockReplayCountSheetRemovalRootWriter
                .AppendWithinRootAsync(reversal.Context!, plan,
                    new RunningNumberService(), "Count entered in error",
                    DateTime.UtcNow);
            Assert.Equal(physicalQty == 100 ? 0 : 1,
                removed.ReversalHistoryIds.Count);
            Assert.Single(removed.RemovalGenerationIds);
            await coordinator.CompleteReplayInTransactionAsync(
                reversal.Context!, []);
            await rollback.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(reversal.Context!);
            await rollback.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        Assert.Equal(expectedQuantity,
            (await db.IvBalLocs.SingleAsync()).StdQty);
        var restored = await db.StockCostStates.SingleAsync(s =>
            s.CostMethod == StockCostMethods.Standard);
        Assert.Equal(expectedQuantity, restored.OnHandBaseQty);
        Assert.Equal(expectedValue, restored.InventoryValue);
        var activeIssue = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, company, branch, [issue]);
        Assert.Equal(receipt ? 4m : -4m,
            Assert.Single(activeIssue[issue.Id]).SignedValue);
        var tail = await StockReplayStandardFinancialTailReader.ReadLaterAsync(
            db, company, branch, "A100",
            new StockReplayEventKey(FixedToday,
                StockReplayEventKey.OrdinaryClass, long.MaxValue,
                "\uffff", int.MaxValue));
        Assert.Equal(StockReplayStandardEventKinds.Movement,
            Assert.Single(tail.LaterEvents).Kind);
    }

    [Theory]
    [InlineData(110, false)]
    [InlineData(100, false)]
    [InlineData(110, true)]
    public async Task Wac_count_rollback_preserves_later_misc_receipt_and_its_value(
        int physicalQty, bool withLaterIssue)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        var pile = await db.IvBalLocs.SingleAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = company, BranchCode = branch,
            CountNo = "CC-WAC-MR-REMOVE", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = pile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 100m, PhysicalQty = physicalQty
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var access = new Mock<IAccessRightService>();
        access.Setup(a => a.CanAsync(MenuCodes.InventoryStockCount,
                PermissionCodes.PriceOverride, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var post = await StockReplayCountSheetRootWriter.BuildAsync(
            db, access.Object, company, branch, header.Id,
            [new StockCountCostApprovalRequest(line.Id,
                StockCountCostBasisKinds.ManualApproved, 2m,
                "Approved count basis")], "manager", DateTime.UtcNow);
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var begin = await coordinator.BeginInTransactionAsync(db, post.Command);
            Assert.NotNull(begin.Context);
            try
            {
                var written = await StockReplayCountSheetRootWriter
                    .AppendWithinRootAsync(begin.Context!, post,
                        new RunningNumberService());
                await coordinator.CompleteReplayInTransactionAsync(
                    begin.Context!, written.RequiredActiveHistoryIds);
                await transaction.CommitAsync();
            }
            catch
            {
                await coordinator.AbortInTransactionAsync(begin.Context!);
                await transaction.RollbackAsync();
                throw;
            }
        }
        db.ChangeTracker.Clear();
        var countPosting = await db.StockPostings.SingleAsync();
        var receiptAt = FixedToday.AddDays(1).AddHours(9);
        var receiptPosting = new StockPosting
        {
            CompanyCode = company, BranchCode = branch,
            LedgerEpochId = countPosting.LedgerEpochId,
            PostingSequence = countPosting.PostingSequence + 1,
            RequestId = Guid.NewGuid(), CommandType = "IV_MR_POST",
            RequestFingerprint = new string('C', 64),
            SourceModule = "INVENTORY", SourceDocumentType = "IV_TRX",
            SourceDocumentId = "WAC-MR", SourceDocumentNo = "WAC-MR",
            SourceSnapshotJson = "{}", SourceSnapshotHash = new string('D', 64),
            EffectiveAt = receiptAt, BusinessDate = receiptAt.Date,
            PeriodKey = receiptAt.ToString("yyyy-MM"),
            PostedAtUtc = DateTime.UtcNow, PostedBy = "TEST",
            SealedAtUtc = DateTime.UtcNow
        };
        db.StockPostings.Add(receiptPosting);
        await db.SaveChangesAsync();
        var receipt = new IvTrxHistory
        {
            CompanyCode = company, BranchCode = branch,
            BatchNo = 902, TrxLineNo = 1, TrxDtTime = receiptAt,
            TrxType = IvTrxTypes.MiscellaneousReceipt,
            BatchStatus = IvBatchStatuses.Posted, ICode = "A100",
            ToBalLocId = pile.Id, ToStdQty = 2m, ToStdUom = "EA",
            ToWarehouse = "MAIN", ToLocation = "BIN1", IStatus = "ACTIVE",
            LedgerVersion = 2, LedgerEpochId = countPosting.LedgerEpochId,
            StockPostingId = receiptPosting.Id, PostingLineNo = 1,
            EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        db.IvTrxHistories.Add(receipt);
        await db.SaveChangesAsync();
        db.StockValuationFacts.Add(new StockValuationFact
        {
            CompanyCode = company, BranchCode = branch,
            LedgerEpochId = countPosting.LedgerEpochId,
            StockPostingId = receiptPosting.Id,
            PostingLineNo = 1, SplitOrdinal = 0,
            SourceLineId = "IV_TRX:WAC-MR:1:1:0",
            SourceDocumentType = "IV_TRX", SourceDocumentId = "WAC-MR",
            SourceDocumentNo = "WAC-MR", SourceDocumentLine = "1",
            EffectiveAt = receiptAt, BusinessDate = receiptAt.Date,
            PeriodKey = receiptPosting.PeriodKey,
            ItemCode = "A100", WarehouseCode = "MAIN",
            LocationCode = "BIN1", ItemStatus = "ACTIVE",
            BaseUom = "EA", MovementCode = "RECEIPT",
            Direction = 1, BaseQty = 2m,
            CostMethod = StockCostMethods.MovingAverage,
            UnitCost = 3m, CostAmount = 6m, BaseCostAmount = 6m,
            ValuationSource = StockValuationSources.ReceiptActual,
            ValuationStatus = StockValuationStatuses.Valued,
            InventoryHistoryId = receipt.Id, ToBalLocId = pile.Id,
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
        });
        IvTrxHistory? issue = null;
        StockPosting? issuePosting = null;
        var issueOldCost = StockLedgerPrecision.Money(2m
            * StockLedgerPrecision.Money(
                (physicalQty * 2m + 6m) / (physicalQty + 2m)));
        if (withLaterIssue)
        {
            var issueAt = receiptAt.AddHours(1);
            issuePosting = new StockPosting
            {
                CompanyCode = company, BranchCode = branch,
                LedgerEpochId = countPosting.LedgerEpochId,
                PostingSequence = receiptPosting.PostingSequence + 1,
                RequestId = Guid.NewGuid(), CommandType = "IV_MI_POST",
                RequestFingerprint = new string('E', 64),
                SourceModule = "INVENTORY", SourceDocumentType = "IV_TRX",
                SourceDocumentId = "WAC-MI", SourceDocumentNo = "WAC-MI",
                SourceSnapshotJson = "{}", SourceSnapshotHash = new string('F', 64),
                EffectiveAt = issueAt, BusinessDate = issueAt.Date,
                PeriodKey = issueAt.ToString("yyyy-MM"),
                PostedAtUtc = DateTime.UtcNow, PostedBy = "TEST",
                SealedAtUtc = DateTime.UtcNow
            };
            db.StockPostings.Add(issuePosting);
            await db.SaveChangesAsync();
            issue = new IvTrxHistory
            {
                CompanyCode = company, BranchCode = branch,
                BatchNo = 903, TrxLineNo = 1, TrxDtTime = issueAt,
                TrxType = IvTrxTypes.MiscellaneousIssue,
                BatchStatus = IvBatchStatuses.Posted, ICode = "A100",
                FromBalLocId = pile.Id, FrStdQty = 2m, FrStdUom = "EA",
                FrWarehouse = "MAIN", FrLocation = "BIN1", IStatus = "ACTIVE",
                LedgerVersion = 2, LedgerEpochId = countPosting.LedgerEpochId,
                StockPostingId = issuePosting.Id, PostingLineNo = 1,
                EntryRole = "ORIGINAL",
                ValuationStatus = StockValuationStatuses.Valued
            };
            db.IvTrxHistories.Add(issue);
            await db.SaveChangesAsync();
            db.StockValuationFacts.Add(new StockValuationFact
            {
                CompanyCode = company, BranchCode = branch,
                LedgerEpochId = countPosting.LedgerEpochId,
                StockPostingId = issuePosting.Id,
                PostingLineNo = 1, SplitOrdinal = 0,
                SourceLineId = "IV_TRX:WAC-MI:1:1:0",
                SourceDocumentType = "IV_TRX", SourceDocumentId = "WAC-MI",
                SourceDocumentNo = "WAC-MI", SourceDocumentLine = "1",
                EffectiveAt = issueAt, BusinessDate = issueAt.Date,
                PeriodKey = issuePosting.PeriodKey,
                ItemCode = "A100", WarehouseCode = "MAIN",
                LocationCode = "BIN1", ItemStatus = "ACTIVE",
                BaseUom = "EA", MovementCode = "ISSUE_OUT",
                Direction = -1, BaseQty = 2m,
                CostMethod = StockCostMethods.MovingAverage,
                UnitCost = StockLedgerPrecision.Money(issueOldCost / 2m),
                CostAmount = issueOldCost, BaseCostAmount = issueOldCost,
                ValuationSource = StockValuationSources.MovingAverage,
                ValuationStatus = StockValuationStatuses.Valued,
                InventoryHistoryId = issue.Id, FromBalLocId = pile.Id,
                CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
            });
        }
        pile = await db.IvBalLocs.SingleAsync(b => b.Id == pile.Id);
        pile.StdQty = physicalQty + (withLaterIssue ? 0m : 2m);
        pile.TransDate = issue?.TrxDtTime ?? receiptAt;
        var state = await db.StockCostStates.SingleAsync();
        state.OnHandBaseQty = pile.StdQty;
        state.InventoryValue = physicalQty * 2m + 6m
            - (withLaterIssue ? issueOldCost : 0m);
        state.AverageUnitCost = StockLedgerPrecision.Money(
            state.InventoryValue / state.OnHandBaseQty);
        state.CurrentUnitCost = state.AverageUnitCost;
        state.LastPostingSequence = issuePosting?.PostingSequence
            ?? receiptPosting.PostingSequence;
        (await db.StockPostingBranchSequences.SingleAsync()).LastSequence =
            state.LastPostingSequence;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        if (physicalQty != 100)
        {
            var foreignOwner = await db.StockPostings.SingleAsync(p =>
                p.Id == receiptPosting.Id);
            foreignOwner.SourceModule = "PRODUCTION";
            await db.SaveChangesAsync();
            var refused = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayCountSheetRemovalRootWriter.BuildAsync(
                    db, company, branch, header.Id));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, refused.Error.Code);
            foreignOwner.SourceModule = "INVENTORY";
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        if (withLaterIssue)
        {
            var foreignIssueOwner = await db.StockPostings.SingleAsync(p =>
                p.Id == issuePosting!.Id);
            foreignIssueOwner.SourceModule = "PRODUCTION";
            await db.SaveChangesAsync();
            var refusedIssue = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayCountSheetRemovalRootWriter.BuildAsync(
                    db, company, branch, header.Id));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                refusedIssue.Error.Code);
            foreignIssueOwner.SourceModule = "INVENTORY";
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        var removal = await StockReplayCountSheetRemovalRootWriter.BuildAsync(
            db, company, branch, header.Id);
        if (physicalQty == 100)
        {
            var zero = Assert.IsType<StockReplayZeroCountRemovalRootPlan>(
                removal.Zero);
            Assert.Single(Assert.Single(zero.Removal.Lines).Physical.LaterEvents);
        }
        else
        {
            var item = Assert.Single(
                Assert.IsType<StockReplayWacCountSheetRemovalPlan>(
                    removal.Wac).Items);
            Assert.Equal(withLaterIssue ? 100m : 102m,
                item.FinalQuantity);
            Assert.Equal(withLaterIssue ? 201.960784m : 206m,
                item.FinalValue);
            Assert.Equal(withLaterIssue, item.Repricing is not null);
        }
        await using var rollback = await db.Database.BeginTransactionAsync();
        var reverse = await coordinator.BeginInTransactionAsync(db, removal.Command);
        Assert.NotNull(reverse.Context);
        try
        {
            if (physicalQty == 100)
            {
                var changedFact = await db.StockValuationFacts.SingleAsync(f =>
                    f.InventoryHistoryId == receipt.Id);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE StockValuationFact SET CostAmount = {7m} WHERE Id = {changedFact.Id}");
                var staleValue = await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayCountSheetRemovalRootWriter.AppendWithinRootAsync(
                        reverse.Context!, removal, new RunningNumberService(),
                        "Count entered in error", DateTime.UtcNow));
                Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                    staleValue.Error.Code);
                Assert.Empty(await db.IvTrxBatches.ToArrayAsync());
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE StockValuationFact SET CostAmount = {6m} WHERE Id = {changedFact.Id}");
            }
            if (physicalQty != 100)
            {
                var changedOwner = await db.StockPostings.SingleAsync(p =>
                    p.Id == receiptPosting.Id);
                changedOwner.SourceModule = "PRODUCTION";
                await db.SaveChangesAsync();
                var stale = await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayCountSheetRemovalRootWriter.AppendWithinRootAsync(
                        reverse.Context!, removal, new RunningNumberService(),
                        "Count entered in error", DateTime.UtcNow));
                Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                    stale.Error.Code);
                Assert.Single(await db.IvTrxBatches.ToArrayAsync());
                changedOwner.SourceModule = "INVENTORY";
                await db.SaveChangesAsync();
            }
            if (withLaterIssue)
            {
                var changedIssueOwner = await db.StockPostings.SingleAsync(p =>
                    p.Id == issuePosting!.Id);
                changedIssueOwner.SourceModule = "PRODUCTION";
                await db.SaveChangesAsync();
                var staleIssue = await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayCountSheetRemovalRootWriter.AppendWithinRootAsync(
                        reverse.Context!, removal, new RunningNumberService(),
                        "Count entered in error", DateTime.UtcNow));
                Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                    staleIssue.Error.Code);
                changedIssueOwner.SourceModule = "INVENTORY";
                await db.SaveChangesAsync();
            }
            var pendingFact = await db.StockValuationFacts.SingleAsync(f =>
                f.InventoryHistoryId == (withLaterIssue ? issue!.Id : receipt.Id));
            pendingFact.CostAmount = 7m;
            var pending = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayCountSheetRemovalRootWriter.AppendWithinRootAsync(
                    reverse.Context!, removal, new RunningNumberService(),
                    "Count entered in error", DateTime.UtcNow));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                pending.Error.Code);
            Assert.Equal(physicalQty == 100 ? 0 : 1,
                await db.IvTrxBatches.CountAsync());
            await db.Entry(pendingFact).ReloadAsync();
            var written = await StockReplayCountSheetRemovalRootWriter
                .AppendWithinRootAsync(reverse.Context!, removal,
                    new RunningNumberService(), "Count entered in error",
                    DateTime.UtcNow);
            Assert.Equal(physicalQty == 100 ? 0 : 1,
                written.ReversalHistoryIds.Count);
            Assert.Single(written.RemovalGenerationIds);
            Assert.Equal(withLaterIssue ? [issue!.Id] : [],
                written.RequiredActiveHistoryIds);
            await coordinator.CompleteReplayInTransactionAsync(
                reverse.Context!, written.RequiredActiveHistoryIds);
            await rollback.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(reverse.Context!);
            await rollback.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        Assert.Equal(withLaterIssue ? 100m : 102m,
            (await db.IvBalLocs.SingleAsync()).StdQty);
        var finalState = await db.StockCostStates.SingleAsync();
        Assert.Equal(withLaterIssue ? 100m : 102m,
            finalState.OnHandBaseQty);
        Assert.Equal(withLaterIssue ? 201.960784m : 206m,
            finalState.InventoryValue);
        var active = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, company, branch, [receipt]);
        Assert.Equal(6m, Assert.Single(active[receipt.Id]).SignedValue);
        if (withLaterIssue)
        {
            var issueFacts = await StockReplayValuationGenerationResolver
                .ResolveAsync(db, company, branch, [issue!]);
            var corrected = Assert.Single(issueFacts[issue!.Id]);
            Assert.Equal(4.039216m, corrected.CostAmount);
            Assert.Equal(issueOldCost,
                (await db.StockValuationFacts.SingleAsync(f =>
                    f.StockPostingId == issuePosting!.Id)).CostAmount);
        }
        var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, company, branch, "A100",
            new StockReplayEventKey(FixedToday,
                StockReplayEventKey.OrdinaryClass, long.MaxValue,
                "\uffff", int.MaxValue));
        Assert.Equal(withLaterIssue ? 2 : 1, tail.LaterEvents.Count);
    }

    [Theory]
    [InlineData(false, 206, 6)]
    [InlineData(true, 204, 4)]
    public async Task Count_rollback_keeps_ordinary_receipt_at_final_count_tick(
        bool standard, int expectedValue, int receiptValue)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        if (standard)
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(
                db, "A100", 100m, 2m, StockCostMethods.Standard);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 2m);
        }
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        var pile = await db.IvBalLocs.SingleAsync();
        var epochId = await db.StockLedgerEpochs.Select(e => e.Id).SingleAsync();
        var receiptAt = StockReplayEventKey.CountEndOfDay(FixedToday);
        var receiptPosting = new StockPosting
        {
            CompanyCode = company, BranchCode = branch,
            LedgerEpochId = epochId, PostingSequence = 1,
            RequestId = Guid.NewGuid(), CommandType = "IV_MR_POST",
            RequestFingerprint = new string('C', 64),
            SourceModule = "INVENTORY", SourceDocumentType = "IV_TRX",
            SourceDocumentId = "WAC-FINAL-TICK-MR",
            SourceDocumentNo = "WAC-FINAL-TICK-MR",
            SourceSnapshotJson = "{}", SourceSnapshotHash = new string('D', 64),
            EffectiveAt = receiptAt, BusinessDate = FixedToday,
            PeriodKey = FixedToday.ToString("yyyy-MM"),
            PostedAtUtc = DateTime.UtcNow, PostedBy = "TEST",
            SealedAtUtc = DateTime.UtcNow
        };
        db.StockPostings.Add(receiptPosting);
        await db.SaveChangesAsync();
        var receipt = new IvTrxHistory
        {
            CompanyCode = company, BranchCode = branch,
            BatchNo = 902, TrxLineNo = 1, TrxDtTime = receiptAt,
            TrxType = IvTrxTypes.MiscellaneousReceipt,
            BatchStatus = IvBatchStatuses.Posted, ICode = "A100",
            ToBalLocId = pile.Id, ToStdQty = 2m, ToStdUom = "EA",
            ToWarehouse = "MAIN", ToLocation = "BIN1", IStatus = "ACTIVE",
            LedgerVersion = 2, LedgerEpochId = epochId,
            StockPostingId = receiptPosting.Id, PostingLineNo = 1,
            EntryRole = "ORIGINAL",
            ValuationStatus = StockValuationStatuses.Valued
        };
        db.IvTrxHistories.Add(receipt);
        await db.SaveChangesAsync();
        db.StockValuationFacts.Add(new StockValuationFact
        {
            CompanyCode = company, BranchCode = branch,
            LedgerEpochId = epochId, StockPostingId = receiptPosting.Id,
            PostingLineNo = 1, SplitOrdinal = 0,
            SourceLineId = "IV_TRX:WAC-FINAL-TICK-MR:1:1:0",
            SourceDocumentType = "IV_TRX",
            SourceDocumentId = "WAC-FINAL-TICK-MR",
            SourceDocumentNo = "WAC-FINAL-TICK-MR",
            SourceDocumentLine = "1", EffectiveAt = receiptAt,
            BusinessDate = FixedToday,
            PeriodKey = receiptPosting.PeriodKey,
            ItemCode = "A100", WarehouseCode = "MAIN",
            LocationCode = "BIN1", ItemStatus = "ACTIVE",
            BaseUom = "EA", MovementCode = "RECEIPT",
            Direction = 1, BaseQty = 2m,
            CostMethod = standard ? StockCostMethods.Standard
                : StockCostMethods.MovingAverage,
            UnitCost = standard ? 2m : 3m,
            CostAmount = receiptValue, BaseCostAmount = receiptValue,
            ValuationSource = standard ? StockValuationSources.Standard
                : StockValuationSources.ReceiptActual,
            ValuationStatus = StockValuationStatuses.Valued,
            InventoryHistoryId = receipt.Id, ToBalLocId = pile.Id,
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
        });
        pile.StdQty = 102m;
        pile.TransDate = receiptAt;
        var state = await db.StockCostStates.SingleAsync(s =>
            s.CostMethod == (standard ? StockCostMethods.Standard
                : StockCostMethods.MovingAverage));
        state.OnHandBaseQty = 102m;
        state.InventoryValue = expectedValue;
        state.LastPostingSequence = 1;
        db.StockPostingBranchSequences.Add(new StockPostingBranchSequence
        {
            CompanyCode = company, BranchCode = branch,
            LastSequence = 1, UpdatedAtUtc = DateTime.UtcNow
        });
        var header = new IvStockCountHdr
        {
            CompanyCode = company, BranchCode = branch,
            CountNo = "CC-FINAL-TICK", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = pile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 102m, PhysicalQty = 110m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var access = new Mock<IAccessRightService>();
        access.Setup(a => a.CanAsync(MenuCodes.InventoryStockCount,
                PermissionCodes.PriceOverride, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        StockCountCostApprovalRequest[] approvals = standard ? [] :
        [
            new StockCountCostApprovalRequest(line.Id,
                StockCountCostBasisKinds.ManualApproved, 2m,
                "Approved count basis")
        ];
        var post = await StockReplayCountSheetRootWriter.BuildAsync(
            db, access.Object, company, branch, header.Id,
            approvals, "manager", DateTime.UtcNow);
        Assert.Equal(8m, Assert.Single(post.PhysicalSheet.Lines)
            .Projection.Candidate.SignedDelta);
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var begin = await coordinator.BeginInTransactionAsync(db, post.Command);
            Assert.NotNull(begin.Context);
            try
            {
                var written = await StockReplayCountSheetRootWriter
                    .AppendWithinRootAsync(begin.Context!, post,
                        new RunningNumberService());
                await coordinator.CompleteReplayInTransactionAsync(
                    begin.Context!, written.RequiredActiveHistoryIds);
                await transaction.CommitAsync();
            }
            catch
            {
                await coordinator.AbortInTransactionAsync(begin.Context!);
                await transaction.RollbackAsync();
                throw;
            }
        }
        db.ChangeTracker.Clear();
        var removal = await StockReplayCountSheetRemovalRootWriter.BuildAsync(
            db, company, branch, header.Id);
        var finalQuantity = standard
            ? Assert.Single(Assert.IsType<StockReplayStandardCountSheetRemovalPlan>(
                removal.Standard).Items).FinalQuantity
            : Assert.Single(Assert.IsType<StockReplayWacCountSheetRemovalPlan>(
                removal.Wac).Items).FinalQuantity;
        var finalValue = standard
            ? Assert.Single(removal.Standard!.Items).FinalValue
            : Assert.Single(removal.Wac!.Items).FinalValue;
        Assert.Equal(102m, finalQuantity);
        Assert.Equal(expectedValue, finalValue);
        await using var rollback = await db.Database.BeginTransactionAsync();
        var reverse = await coordinator.BeginInTransactionAsync(db, removal.Command);
        Assert.NotNull(reverse.Context);
        try
        {
            await StockReplayCountSheetRemovalRootWriter.AppendWithinRootAsync(
                reverse.Context!, removal, new RunningNumberService(),
                "Count entered in error", DateTime.UtcNow);
            await coordinator.CompleteReplayInTransactionAsync(
                reverse.Context!, []);
            await rollback.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(reverse.Context!);
            await rollback.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        Assert.Equal(102m, (await db.IvBalLocs.SingleAsync()).StdQty);
        Assert.Equal(expectedValue,
            (await db.StockCostStates.SingleAsync(s =>
                s.CostMethod == (standard ? StockCostMethods.Standard
                    : StockCostMethods.MovingAverage))).InventoryValue);
        var active = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, company, branch, [receipt]);
        Assert.Equal(receiptValue,
            Assert.Single(active[receipt.Id]).SignedValue);
    }

    [Theory]
    [InlineData(110, 220)]
    [InlineData(90, 180)]
    public async Task Wac_count_root_freezes_approved_basis_and_seals_adjustment(
        int physicalQty, int expectedValue)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        var pile = await db.IvBalLocs.SingleAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = company, BranchCode = branch,
            CountNo = "CC-WAC-ROOT", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = pile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 100m, PhysicalQty = physicalQty
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var approvedAt = DateTime.UtcNow;
        var approvals = new[]
        {
            new StockCountCostApprovalRequest(line.Id,
                StockCountCostBasisKinds.ManualApproved, 2.123456m,
                "Approved fallback for count")
        };
        var access = new Mock<IAccessRightService>();
        var denied = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayCountSheetRootWriter.BuildAsync(db, access.Object,
                company, branch, header.Id, approvals, "manager", approvedAt));
        Assert.Equal(StockLedgerErrorCodes.ValuationRequired, denied.Error.Code);
        access.Setup(a => a.CanAsync(ErpWeb.Core.Menus.MenuCodes.InventoryStockCount,
                ErpWeb.Core.Menus.PermissionCodes.PriceOverride,
                It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var rootPlan = await StockReplayCountSheetRootWriter.BuildAsync(
            db, access.Object, company, branch, header.Id,
            approvals, "manager", approvedAt);
        Assert.NotNull(rootPlan.Wac);
        Assert.Equal(2.123456m, Assert.Single(rootPlan.FrozenBases).FrozenFallbackUnitCost);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, rootPlan.Command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        try
        {
            var policy = await db.StockCostPolicyRevisions.SingleAsync(p =>
                p.Status == StockCostPolicyStatuses.Active);
            policy.CostMethod = StockCostMethods.Fifo;
            await db.SaveChangesAsync();
            var changedPolicy = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayCountSheetRootWriter.AppendWithinRootAsync(
                    root, rootPlan, new RunningNumberService()));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, changedPolicy.Error.Code);
            Assert.False(await db.StockCountCostBases.AnyAsync());
            policy.CostMethod = StockCostMethods.MovingAverage;
            await db.SaveChangesAsync();
            var written = await StockReplayCountSheetRootWriter.AppendWithinRootAsync(
                root, rootPlan, new RunningNumberService());
            var wac = Assert.IsType<StockReplayWacCountSheetPostResult>(written.Wac);
            Assert.Equal(20m, Assert.Single(wac.InitialFacts).CostAmount);
            Assert.Single(written.RequiredActiveHistoryIds);
            await coordinator.CompleteReplayInTransactionAsync(root,
                written.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(root);
            await transaction.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        var basis = await db.StockCountCostBases.SingleAsync();
        Assert.Equal(2.123456m, basis.FrozenFallbackUnitCost);
        Assert.Equal("manager", basis.CostApprovedBy);
        Assert.Equal(IvStockCountStatuses.Posted,
            (await db.IvStockCountHdrs.SingleAsync()).Status);
        var state = await db.StockCostStates.SingleAsync();
        Assert.Equal(physicalQty, state.OnHandBaseQty);
        Assert.Equal(expectedValue, state.InventoryValue);
        Assert.NotNull((await db.StockPostings.SingleAsync()).SealedAtUtc);
        var removalPlan = await StockReplayWacSingleCountRemovalPlanner.BuildAsync(
            db, company, branch, header.Id);
        Assert.Equal(100m, removalPlan.Financial.Projection.FinalQuantity);
        Assert.Equal(200m, removalPlan.Financial.Projection.FinalValue);
        await using var removalTransaction = await db.Database.BeginTransactionAsync();
        var removalBegin = await coordinator.BeginInTransactionAsync(
            db, removalPlan.Command);
        Assert.NotNull(removalBegin.Context);
        try
        {
            var policy = await db.StockCostPolicyRevisions.SingleAsync(p =>
                p.Status == StockCostPolicyStatuses.Active);
            policy.CostMethod = StockCostMethods.Fifo;
            await db.SaveChangesAsync();
            var changedRemovalPolicy = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayWacSingleCountRemovalPlanner.ValidateWithinRootAsync(
                    removalBegin.Context!, removalPlan));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                changedRemovalPolicy.Error.Code);
            policy.CostMethod = StockCostMethods.MovingAverage;
            await db.SaveChangesAsync();
            await StockReplayWacSingleCountRemovalPlanner.ValidateWithinRootAsync(
                removalBegin.Context!, removalPlan);
            var originalDetail = await db.IvTrxBatchDetails.SingleAsync();
            if (physicalQty > 100)
                originalDetail.ToStdQty = 9m;
            else
                originalDetail.FrStdQty = 9m;
            var dirtyDetail = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayWacSingleCountRemovalRootWriter.AppendWithinRootAsync(
                    removalBegin.Context!, removalPlan,
                    new RunningNumberService(), "Count entered in error",
                    DateTime.UtcNow));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                dirtyDetail.Error.Code);
            Assert.Single(await db.IvTrxBatches.ToArrayAsync());
            await db.SaveChangesAsync();
            var changedDetail = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayWacSingleCountRemovalRootWriter.AppendWithinRootAsync(
                    removalBegin.Context!, removalPlan,
                    new RunningNumberService(), "Count entered in error",
                    DateTime.UtcNow));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                changedDetail.Error.Code);
            Assert.Single(await db.IvTrxBatches.ToArrayAsync());
            if (physicalQty > 100)
                originalDetail.ToStdQty = 10m;
            else
                originalDetail.FrStdQty = 10m;
            await db.SaveChangesAsync();
            var removed = await StockReplayWacSingleCountRemovalRootWriter
                .AppendWithinRootAsync(removalBegin.Context!, removalPlan,
                    new RunningNumberService(), "Count entered in error",
                    DateTime.UtcNow);
            Assert.True(removed.ReversalHistoryId > 0);
            Assert.True(removed.RemovalGenerationId > 0);
            await coordinator.CompleteReplayInTransactionAsync(
                removalBegin.Context!, []);
            await removalTransaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(removalBegin.Context!);
            await removalTransaction.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        var rolledBackHeader = await db.IvStockCountHdrs.SingleAsync();
        Assert.Equal(IvStockCountStatuses.RolledBack, rolledBackHeader.Status);
        Assert.NotNull(rolledBackHeader.PostedBatchNo);
        Assert.True((await StockCountGenerationResolver.ResolveActiveAsync(
            db, company, branch, [line.Id]))[line.Id].IsRemoval);
        Assert.Equal(100m, (await db.IvBalLocs.SingleAsync()).StdQty);
        var restored = await db.StockCostStates.SingleAsync();
        Assert.Equal(100m, restored.OnHandBaseQty);
        Assert.Equal(200m, restored.InventoryValue);
        var histories = await db.IvTrxHistories.ToArrayAsync();
        var originalHistory = Assert.Single(histories.Where(h =>
            h.EntryRole == "ORIGINAL"));
        var reversalHistory = Assert.Single(histories.Where(h =>
            h.EntryRole == "REVERSAL"));
        Assert.Equal(originalHistory.Id, reversalHistory.ReversesHistoryId);
        Assert.NotEqual(rolledBackHeader.PostedBatchNo, reversalHistory.BatchNo);
        var facts = await db.StockValuationFacts.ToArrayAsync();
        var originalFact = Assert.Single(facts.Where(f =>
            f.ReversesValuationFactId is null));
        var reversalFact = Assert.Single(facts.Where(f =>
            f.ReversesValuationFactId is not null));
        Assert.Equal(originalFact.Id, reversalFact.ReversesValuationFactId);
        Assert.Equal(reversalHistory.Id, reversalFact.InventoryHistoryId);
        Assert.Empty(await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, company, branch, pile.Id,
            new StockReplayEventKey(FixedToday,
                StockReplayEventKey.OrdinaryClass, 0, "before-count", 0)));
        var activeFinancial = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, company, branch, "A100",
            new StockReplayEventKey(FixedToday,
                StockReplayEventKey.OrdinaryClass, 0, "before-count", 0));
        Assert.Empty(activeFinancial.LaterEvents);
        Assert.Equal(100m, activeFinancial.CurrentQuantity);
        Assert.Equal(200m, activeFinancial.CurrentValue);

        var firstBatchPointer = rolledBackHeader.PostedBatchNo;
        var changedApproval = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayCountSheetRootWriter.BuildAsync(db, access.Object,
                company, branch, header.Id, approvals, "manager",
                DateTime.UtcNow));
        Assert.Equal(StockLedgerErrorCodes.ValuationRequired,
            changedApproval.Error.Code);
        var repost = await StockReplayCountSheetRootWriter.BuildAsync(
            db, access.Object, company, branch, header.Id, [],
            "manager", DateTime.UtcNow);
        Assert.Equal(1, repost.Command.DocumentRevision);
        Assert.Equal(basis.Id, Assert.Single(repost.FrozenBases).Id);
        await using (var repostTx = await db.Database.BeginTransactionAsync())
        {
            var repostBegin = await coordinator.BeginInTransactionAsync(
                db, repost.Command);
            Assert.NotNull(repostBegin.Context);
            try
            {
                var mutableBasis = await db.StockCountCostBases.SingleAsync();
                mutableBasis.CostOverrideReason = "Forged repost basis";
                var staleBasis = await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayCountSheetRootWriter.AppendWithinRootAsync(
                        repostBegin.Context!, repost,
                        new RunningNumberService()));
                Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                    staleBasis.Error.Code);
                await db.Entry(mutableBasis).ReloadAsync();
                var pendingPile = await db.IvBalLocs.SingleAsync();
                await db.Entry(pendingPile).ReloadAsync();
                var written = await StockReplayCountSheetRootWriter
                    .AppendWithinRootAsync(repostBegin.Context!, repost,
                        new RunningNumberService());
                Assert.Single(written.RequiredActiveHistoryIds);
                await coordinator.CompleteReplayInTransactionAsync(
                    repostBegin.Context!, written.RequiredActiveHistoryIds);
                await repostTx.CommitAsync();
            }
            catch
            {
                await coordinator.AbortInTransactionAsync(repostBegin.Context!);
                await repostTx.RollbackAsync();
                throw;
            }
        }
        db.ChangeTracker.Clear();
        var repostHeader = await db.IvStockCountHdrs.SingleAsync();
        Assert.Equal(IvStockCountStatuses.Posted, repostHeader.Status);
        Assert.Equal(firstBatchPointer, repostHeader.PostedBatchNo);
        Assert.Single(await db.StockCountCostBases.ToArrayAsync());
        var activeRepost = (await StockCountGenerationResolver.ResolveActiveAsync(
            db, company, branch, [line.Id]))[line.Id];
        Assert.False(activeRepost.IsRemoval);
        Assert.NotEqual(firstBatchPointer, activeRepost.AdjustmentBatchNo);
        Assert.Equal(physicalQty,
            (await db.StockCostStates.SingleAsync()).OnHandBaseQty);

        var secondRemoval = await StockReplayCountSheetRemovalRootWriter
            .BuildAsync(db, company, branch, header.Id);
        Assert.Equal(1, secondRemoval.Command.DocumentRevision);
        await using (var secondRollbackTx = await db.Database.BeginTransactionAsync())
        {
            var secondBegin = await coordinator.BeginInTransactionAsync(
                db, secondRemoval.Command);
            Assert.NotNull(secondBegin.Context);
            try
            {
                var removed = await StockReplayCountSheetRemovalRootWriter
                    .AppendWithinRootAsync(secondBegin.Context!, secondRemoval,
                        new RunningNumberService(), "Reposted count entered in error",
                        DateTime.UtcNow);
                Assert.Single(removed.ReversalHistoryIds);
                await coordinator.CompleteReplayInTransactionAsync(
                    secondBegin.Context!, removed.RequiredActiveHistoryIds);
                await secondRollbackTx.CommitAsync();
            }
            catch
            {
                await coordinator.AbortInTransactionAsync(secondBegin.Context!);
                await secondRollbackTx.RollbackAsync();
                throw;
            }
        }
        db.ChangeTracker.Clear();
        Assert.Equal(firstBatchPointer,
            (await db.IvStockCountHdrs.SingleAsync()).PostedBatchNo);
        Assert.True((await StockCountGenerationResolver.ResolveActiveAsync(
            db, company, branch, [line.Id]))[line.Id].IsRemoval);
        Assert.Equal(100m, (await db.StockCostStates.SingleAsync()).OnHandBaseQty);
        Assert.Equal(200m, (await db.StockCostStates.SingleAsync()).InventoryValue);
    }

    [Fact]
    public async Task Wac_zero_variance_count_root_seals_positive_line_without_adjustment_batch()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        var pile = await db.IvBalLocs.SingleAsync();
        var emptyPile = new IvBalLoc
        {
            CompanyCode = company, BranchCode = branch,
            ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA", StdQty = 0m
        };
        db.IvBalLocs.Add(emptyPile);
        await db.SaveChangesAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = company, BranchCode = branch,
            CountNo = "CC-WAC-ZERO-ROOT", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = pile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 100m, PhysicalQty = 100m
        };
        var emptyLine = new IvStockCountLine
        {
            Header = header, LineNumber = 2, BalLocId = emptyPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 0m, PhysicalQty = 0m
        };
        db.IvStockCountLines.AddRange(line, emptyLine);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var access = new Mock<IAccessRightService>();
        access.Setup(a => a.CanAsync(ErpWeb.Core.Menus.MenuCodes.InventoryStockCount,
                ErpWeb.Core.Menus.PermissionCodes.PriceOverride,
                It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var rootPlan = await StockReplayCountSheetRootWriter.BuildAsync(
            db, access.Object, company, branch, header.Id,
            [new StockCountCostApprovalRequest(line.Id,
                StockCountCostBasisKinds.ManualApproved, 2m, "Approved count fallback")],
            "manager", DateTime.UtcNow);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, rootPlan.Command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        try
        {
            var written = await StockReplayCountSheetRootWriter.AppendWithinRootAsync(
                root, rootPlan, new RunningNumberService());
            var wac = Assert.IsType<StockReplayWacCountSheetPostResult>(written.Wac);
            Assert.Equal(2, wac.History.Generations.Count);
            Assert.Empty(wac.InitialFacts);
            Assert.Empty(written.RequiredActiveHistoryIds);
            await coordinator.CompleteReplayInTransactionAsync(root,
                written.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(root);
            await transaction.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        Assert.Equal(2m, (await db.StockCountCostBases.SingleAsync()).FrozenFallbackUnitCost);
        Assert.All(await db.StockCountReplayGenerations.ToArrayAsync(),
            generation => Assert.Equal(0m, generation.SignedAdjustmentQty));
        Assert.Empty(await db.IvTrxBatches.ToArrayAsync());
        Assert.Empty(await db.StockValuationFacts.ToArrayAsync());
        Assert.NotNull((await db.StockPostings.SingleAsync()).SealedAtUtc);

        var removal = await StockReplayZeroCountRemovalRootWriter.BuildAsync(
            db, company, branch, header.Id);
        var selectedZero = await StockReplayCountSheetRemovalRootWriter.BuildAsync(
            db, company, branch, header.Id);
        Assert.NotNull(selectedZero.Zero);
        Assert.Equal(removal.Command.RequestId,
            selectedZero.Command.RequestId);
        Assert.Equal(2, removal.Removal.Lines.Count);
        Assert.All(removal.Removal.Lines,
            planned => Assert.Empty(planned.Physical.LaterEvents));
        await using var rollback = await db.Database.BeginTransactionAsync();
        var rollbackBegin = await coordinator.BeginInTransactionAsync(
            db, removal.Command);
        Assert.NotNull(rollbackBegin.Context);
        var rollbackRoot = rollbackBegin.Context!;
        try
        {
            var removed = await StockReplayCountSheetRemovalRootWriter
                .AppendWithinRootAsync(rollbackRoot, selectedZero,
                    new RunningNumberService(), "Count entered in error",
                    DateTime.UtcNow);
            Assert.Empty(removed.ReversalHistoryIds);
            Assert.Equal(2, removed.RemovalGenerationIds.Count);
            var removalLeaves = db.StockCountReplayGenerations.Local
                .Where(g => removed.RemovalGenerationIds.Contains(g.Id))
                .ToArray();
            Assert.Equal(2, removalLeaves.Length);
            Assert.All(removalLeaves, generation =>
            {
                Assert.True(generation.IsRemoval);
                Assert.Null(generation.ReversalHistoryId);
            });
            await coordinator.CompleteReplayInTransactionAsync(
                rollbackRoot, []);
            await rollback.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(rollbackRoot);
            await rollback.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        Assert.Equal(IvStockCountStatuses.RolledBack,
            (await db.IvStockCountHdrs.SingleAsync()).Status);
        Assert.Equal(4,
            await db.StockCountReplayGenerations.CountAsync());
        var activeRemoval = await StockCountGenerationResolver.ResolveActiveAsync(
            db, company, branch, [line.Id, emptyLine.Id]);
        Assert.All(activeRemoval.Values, generation => Assert.True(generation.IsRemoval));
        Assert.Equal(100m, (await db.IvBalLocs.SingleAsync(p => p.Id == pile.Id)).StdQty);
        Assert.Equal(0m, (await db.IvBalLocs.SingleAsync(p => p.Id == emptyPile.Id)).StdQty);
        var beforeCount = new StockReplayEventKey(FixedToday,
            StockReplayEventKey.OrdinaryClass, 0, "before-count", 0);
        Assert.Empty(await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, company, branch, pile.Id, beforeCount));
        Assert.Empty(await StockReplayPhysicalTimelineReader.ReadLaterAsync(
            db, company, branch, emptyPile.Id, beforeCount));
        Assert.Empty(await db.IvTrxHistories.ToArrayAsync());
        Assert.Empty(await db.StockValuationFacts.ToArrayAsync());
        Assert.Equal(2, await db.StockPostings.CountAsync());

        var repost = await StockReplayCountSheetRootWriter.BuildAsync(
            db, access.Object, company, branch, header.Id, [],
            "manager", DateTime.UtcNow);
        Assert.Equal(1, repost.Command.DocumentRevision);
        Assert.Equal((await db.StockCountCostBases.SingleAsync()).Id,
            Assert.Single(repost.FrozenBases).Id);
        await using (var repostTx = await db.Database.BeginTransactionAsync())
        {
            var repostBegin = await coordinator.BeginInTransactionAsync(
                db, repost.Command);
            Assert.NotNull(repostBegin.Context);
            try
            {
                var written = await StockReplayCountSheetRootWriter
                    .AppendWithinRootAsync(repostBegin.Context!, repost,
                        new RunningNumberService());
                Assert.Empty(written.RequiredActiveHistoryIds);
                await coordinator.CompleteReplayInTransactionAsync(
                    repostBegin.Context!, written.RequiredActiveHistoryIds);
                await repostTx.CommitAsync();
            }
            catch
            {
                await coordinator.AbortInTransactionAsync(repostBegin.Context!);
                await repostTx.RollbackAsync();
                throw;
            }
        }
        db.ChangeTracker.Clear();
        Assert.Equal(IvStockCountStatuses.Posted,
            (await db.IvStockCountHdrs.SingleAsync()).Status);
        Assert.Null((await db.IvStockCountHdrs.SingleAsync()).PostedBatchNo);
        Assert.Single(await db.StockCountCostBases.ToArrayAsync());
        var repostOwner = await db.StockPostings.SingleAsync(p =>
            p.SourceDocumentType == "IV_STOCK_COUNT"
            && p.SourceDocumentId == header.Id.ToString()
            && p.PostingRole == "PRIMARY"
            && p.DocumentRevision == 1);
        var repostLeaves = await StockCountGenerationResolver.ResolveActiveAsync(
            db, company, branch, [line.Id, emptyLine.Id]);
        Assert.All(repostLeaves.Values, generation =>
        {
            Assert.False(generation.IsRemoval);
            Assert.NotNull(generation.PreviousGenerationId);
            Assert.Equal(repostOwner.Id, generation.AnchorPostingId);
        });

        var repostRemoval = await StockReplayCountSheetRemovalRootWriter
            .BuildAsync(db, company, branch, header.Id);
        await using (var repostRollbackTx = await db.Database.BeginTransactionAsync())
        {
            var repostRollback = await coordinator.BeginInTransactionAsync(
                db, repostRemoval.Command);
            Assert.NotNull(repostRollback.Context);
            try
            {
                var removed = await StockReplayCountSheetRemovalRootWriter
                    .AppendWithinRootAsync(repostRollback.Context!,
                        repostRemoval, new RunningNumberService(),
                        "Second count entered in error", DateTime.UtcNow);
                Assert.Empty(removed.RequiredActiveHistoryIds);
                await coordinator.CompleteReplayInTransactionAsync(
                    repostRollback.Context!, []);
                await repostRollbackTx.CommitAsync();
            }
            catch
            {
                await coordinator.AbortInTransactionAsync(
                    repostRollback.Context!);
                await repostRollbackTx.RollbackAsync();
                throw;
            }
        }
        db.ChangeTracker.Clear();
        Assert.Equal(IvStockCountStatuses.RolledBack,
            (await db.IvStockCountHdrs.SingleAsync()).Status);
        Assert.Equal(8, await db.StockCountReplayGenerations.CountAsync());
        Assert.Equal(4, await db.StockPostings.CountAsync());
    }

    [Theory]
    [InlineData(110, 20, 0, 5)]
    [InlineData(100, 0, 0, 5)]
    [InlineData(110, 20, 5, 0)]
    public async Task Standard_count_sheet_values_two_piles_at_one_revision(
        int firstPhysical, int firstAmount, int secondSystem,
        int secondPhysical)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
        await InventoryLedgerTestFixture.SeedCostStateAsync(
            db, "A100", 100m + secondSystem, 2m,
            StockCostMethods.Standard);
        await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 2m);
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        var firstPile = await db.IvBalLocs.SingleAsync();
        var secondPile = new IvBalLoc
        {
            CompanyCode = company, BranchCode = branch,
            ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            StdQty = secondSystem
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
            SystemQty = secondSystem, PhysicalQty = secondPhysical
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
        var bases = secondPhysical > 0
            ? new[] { Basis(firstLine.Id), Basis(secondLine.Id) }
            : new[] { Basis(firstLine.Id) };
        db.ChangeTracker.Clear();
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, company, branch, header.Id);
        var plan = await StockReplayStandardCountSheetPostingWriter.BuildAsync(
            db, sheet, bases);
        Assert.Equal(firstPhysical + secondPhysical,
            Assert.Single(plan.Items).Projection.FinalQuantity);
        Assert.Equal((firstPhysical + secondPhysical) * 2m,
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
        Assert.Equal(firstPhysical + secondPhysical, state.OnHandBaseQty);
        Assert.Equal((firstPhysical + secondPhysical) * 2m,
            state.InventoryValue);
        Assert.Equal(2, await db.StockCountReplayGenerations.CountAsync());
        var removal = await StockReplayStandardCountSheetRemovalPlanner.BuildAsync(
            db, company, branch, header.Id);
        var selectedRemoval = await StockReplayCountSheetRemovalRootWriter
            .BuildAsync(db, company, branch, header.Id);
        Assert.NotNull(selectedRemoval.Standard);
        Assert.Equal(removal.Command.RequestId,
            selectedRemoval.Command.RequestId);
        var removedItem = Assert.Single(removal.Items);
        Assert.Equal(2, removedItem.CountLineIds.Count);
        Assert.Equal(100m + secondSystem, removedItem.FinalQuantity);
        Assert.Equal((100m + secondSystem) * 2m,
            removedItem.FinalValue);
        Assert.Equal(new[] { (decimal)(firstPhysical - 100),
                (decimal)(secondPhysical - secondSystem) },
            removedItem.RemovedEvents.Select(e => e.OldSignedQuantity));
        await using var removalTransaction = await db.Database.BeginTransactionAsync();
        var removalBegin = await coordinator.BeginInTransactionAsync(
            db, removal.Command);
        Assert.NotNull(removalBegin.Context);
        try
        {
            Assert.Equal(removal.Command.RequestId,
                removalBegin.Context!.Posting.RequestId);
            Assert.Equal(removal.Command.Evidence.RequestFingerprint,
                removalBegin.Context.Posting.RequestFingerprint);
            await StockReplayStandardCountSheetRemovalPlanner
                .ValidateWithinRootAsync(removalBegin.Context!, removal);
            Assert.Equal(StockCostMethods.Standard,
                removalBegin.Context!.CostMethod);
            state.InventoryValue += 1m;
            await db.SaveChangesAsync();
            var stale = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayStandardCountSheetRemovalPlanner
                    .ValidateWithinRootAsync(removalBegin.Context!, removal));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                stale.Error.Code);
            state.InventoryValue -= 1m;
            await db.SaveChangesAsync();
            var detail = await db.IvTrxBatchDetails
                .OrderBy(d => d.TrxLineNo).FirstAsync();
            detail.ToStdQty += 1m;
            var dirtyDetail = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayStandardCountSheetRemovalRootWriter
                    .AppendWithinRootAsync(removalBegin.Context!, removal,
                        new RunningNumberService(), "Count entered in error",
                        DateTime.UtcNow));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                dirtyDetail.Error.Code);
            await db.Entry(detail).ReloadAsync();
            var written = await StockReplayCountSheetRemovalRootWriter
                .AppendWithinRootAsync(removalBegin.Context!, selectedRemoval,
                    new RunningNumberService(), "Count entered in error",
                    DateTime.UtcNow);
            Assert.Equal(firstAmount == 0 ? 1 : 2,
                written.ReversalHistoryIds.Count);
            Assert.Equal(2, written.RemovalGenerationIds.Count);
            await coordinator.CompleteReplayInTransactionAsync(
                removalBegin.Context!, []);
            await removalTransaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(removalBegin.Context!);
            await removalTransaction.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        Assert.Equal(IvStockCountStatuses.RolledBack,
            (await db.IvStockCountHdrs.SingleAsync()).Status);
        Assert.All((await StockCountGenerationResolver.ResolveActiveAsync(
            db, company, branch, [firstLine.Id, secondLine.Id])).Values,
            generation => Assert.True(generation.IsRemoval));
        var restored = await db.StockCostStates.SingleAsync(s =>
            s.CostMethod == StockCostMethods.Standard);
        Assert.Equal(100m + secondSystem, restored.OnHandBaseQty);
        Assert.Equal((100m + secondSystem) * 2m,
            restored.InventoryValue);
        var restoredPiles = await db.IvBalLocs.AsNoTracking()
            .ToDictionaryAsync(b => b.Id);
        Assert.Equal(100m, restoredPiles[firstPile.Id].StdQty);
        Assert.Equal(secondSystem, restoredPiles[secondPile.Id].StdQty);
        var beforeCount = new StockReplayEventKey(FixedToday,
            StockReplayEventKey.OrdinaryClass, 0, "before-count", 0);
        var finalTail = await StockReplayStandardFinancialTailReader
            .ReadLaterAsync(db, company, branch, "A100", beforeCount);
        Assert.Empty(finalTail.LaterEvents);
        var reversalFacts = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.ValuationStatus == StockValuationStatuses.Reversed)
            .ToArrayAsync();
        Assert.Equal(firstAmount == 0 ? 1 : 2,
            reversalFacts.Length);
        Assert.All(reversalFacts, fact =>
            Assert.Equal(StockCostMethods.Standard, fact.CostMethod));
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
        var earlierHeaderId = await db.IvStockCountLines
            .Where(l => l.Id == earlier.LineId)
            .Select(l => l.StockCountId).SingleAsync();
        var laterCountNeedsCorrection = await Assert.ThrowsAsync<
            StockLedgerException>(() =>
            StockReplayCountSheetRemovalRootWriter.BuildAsync(
                db, company, branch, earlierHeaderId));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            laterCountNeedsCorrection.Error.Code);
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
    [InlineData(5, 2, 0, false, false)]
    [InlineData(0, 1, 0, false, false)]
    [InlineData(0, 2, 5, false, false)]
    [InlineData(5, 2, 0, true, false)]
    [InlineData(5, 2, 0, true, true)]
    public async Task Initial_historical_count_writer_uses_one_batch_for_multiple_adjusted_piles(
        int secondPhysical, int expectedFactCount, int secondSystem,
        bool withLaterIssue, bool splitLaterIssue)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        var company = InventoryLedgerTestFixture.CompanyCode;
        var branch = InventoryLedgerTestFixture.BranchCode;
        var firstPile = await db.IvBalLocs.SingleAsync();
        firstPile.StdQty = 0m;
        var initialState = await db.StockCostStates.SingleAsync();
        initialState.OnHandBaseQty = secondSystem;
        initialState.InventoryValue = secondSystem * 2m;
        initialState.AverageUnitCost = secondSystem > 0 ? 2m : 0m;
        initialState.CurrentUnitCost = secondSystem > 0 ? 2m : 0m;
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = company, BranchCode = branch,
            WarehouseCode = "MAIN", LocCode = "BIN2"
        });
        var secondPile = new IvBalLoc
        {
            CompanyCode = company, BranchCode = branch,
            ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            StdQty = secondSystem
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
            SystemQty = secondSystem, PhysicalQty = secondPhysical
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
        if (secondSystem == 0)
            Assert.Equal(40m + secondPhysical * 4m,
                wacSheet.Projection.FinalValue);
        Assert.Equal(secondSystem > 0 ? 20m : 40m,
            wacSheet.Projection.Candidate.SignedValue);
        if (secondSystem == 0)
            Assert.Equal(secondPhysical * 4m,
                Assert.Single(wacSheet.Projection.LaterSteps).SignedValue);
        else
            Assert.True(Assert.Single(wacSheet.Projection.LaterSteps)
                .SignedValue < 0m);
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
            changedState.InventoryValue = secondSystem * 2m;
            await db.SaveChangesAsync();
            var prepared = await StockReplayWacCountSheetPostingWriter
                .PrepareWithinRootAsync(root, postingPlan);
            var result = await StockReplayWacCountSheetPostingWriter.AppendWithinRootAsync(
                root, prepared, bases, new RunningNumberService());
            var written = result.History;
            Assert.NotNull(written.AdjustmentBatchNo);
            Assert.Equal(2, written.Generations.Count);
            Assert.Equal(new[] { 10m,
                    (decimal)(secondPhysical - secondSystem) },
                written.Generations.OrderBy(g => g.StockCountLineId)
                    .Select(g => g.SignedAdjustmentQty));
            var facts = result.InitialFacts;
            Assert.Equal(expectedFactCount, facts.Count);
            Assert.Equal(secondSystem > 0 ? 20m : 40m,
                facts.OrderBy(f => f.PostingLineNo)
                .First().CostAmount);
            if (secondSystem > 0)
                Assert.Equal(-1, facts.OrderBy(f => f.PostingLineNo)
                    .Last().Direction);
            else if (secondPhysical > 0)
                Assert.Equal(20m, facts.OrderBy(f => f.PostingLineNo)
                    .Last().CostAmount);
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
        Assert.Equal(expectedFactCount == 2
                ? new short[] { 1, 2 } : new short[] { 1 },
            details.Select(d => d.TrxLineNo));
        Assert.Equal(firstPile.Id, details[0].ToBalLocId);
        if (expectedFactCount == 2)
            Assert.Equal(secondSystem > 0 ? secondPile.Id : null,
                details[1].FromBalLocId);
        Assert.Equal(10m + secondPhysical,
            (await db.StockCostStates.SingleAsync()).OnHandBaseQty);
        Assert.Equal(wacSheet.Projection.FinalValue,
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
        Assert.Equal(wacSheet.Projection.FinalValue, timeline.CurrentValue);
        Assert.Equal(new[] { 10m,
                (decimal)(secondPhysical - secondSystem) }, timeline.LaterEvents
            .Select(e => e.OldSignedQuantity));
        IvTrxHistory? laterIssue = null;
        if (withLaterIssue)
        {
            var countPosting = await db.StockPostings.SingleAsync();
            var receiptAt = FixedToday.AddDays(1).AddHours(9);
            var issueAt = receiptAt.AddHours(1);
            async Task<IvTrxHistory> SeedMovementAsync(bool receipt,
                DateTime at, long sequence, decimal amount)
            {
                var document = receipt ? "SHEET-MR" : "SHEET-MI";
                var owner = new StockPosting
                {
                    CompanyCode = company, BranchCode = branch,
                    LedgerEpochId = countPosting.LedgerEpochId,
                    PostingSequence = sequence,
                    RequestId = Guid.NewGuid(),
                    CommandType = receipt ? "IV_MR_POST" : "IV_MI_POST",
                    RequestFingerprint = new string(receipt ? 'C' : 'E', 64),
                    SourceModule = "INVENTORY",
                    SourceDocumentType = "IV_TRX",
                    SourceDocumentId = document,
                    SourceDocumentNo = document,
                    SourceSnapshotJson = "{}",
                    SourceSnapshotHash = new string(receipt ? 'D' : 'F', 64),
                    EffectiveAt = at, BusinessDate = at.Date,
                    PeriodKey = at.ToString("yyyy-MM"),
                    PostedAtUtc = DateTime.UtcNow, PostedBy = "TEST",
                    SealedAtUtc = DateTime.UtcNow
                };
                db.StockPostings.Add(owner);
                await db.SaveChangesAsync();
                var movement = new IvTrxHistory
                {
                    CompanyCode = company, BranchCode = branch,
                    BatchNo = receipt ? 912 : 913, TrxLineNo = 1,
                    TrxDtTime = at,
                    TrxType = receipt
                        ? IvTrxTypes.MiscellaneousReceipt
                        : IvTrxTypes.MiscellaneousIssue,
                    BatchStatus = IvBatchStatuses.Posted, ICode = "A100",
                    ToBalLocId = receipt ? secondPile.Id : null,
                    ToStdQty = receipt ? 2m : null,
                    ToStdUom = receipt ? "EA" : null,
                    ToWarehouse = receipt ? "MAIN" : null,
                    ToLocation = receipt ? "BIN2" : null,
                    FromBalLocId = receipt ? null : secondPile.Id,
                    FrStdQty = receipt ? null : 2m,
                    FrStdUom = receipt ? null : "EA",
                    FrWarehouse = receipt ? null : "MAIN",
                    FrLocation = receipt ? null : "BIN2",
                    IStatus = "ACTIVE", LedgerVersion = 2,
                    LedgerEpochId = countPosting.LedgerEpochId,
                    StockPostingId = owner.Id, PostingLineNo = 1,
                    EntryRole = "ORIGINAL",
                    ValuationStatus = StockValuationStatuses.Valued
                };
                db.IvTrxHistories.Add(movement);
                await db.SaveChangesAsync();
                var splitCount = !receipt && splitLaterIssue ? 2 : 1;
                for (var split = 0; split < splitCount; split++)
                {
                    db.StockValuationFacts.Add(new StockValuationFact
                    {
                        CompanyCode = company, BranchCode = branch,
                        LedgerEpochId = countPosting.LedgerEpochId,
                        StockPostingId = owner.Id,
                        PostingLineNo = 1, SplitOrdinal = split,
                        SourceLineId = $"IV_TRX:{document}:1:1:{split}",
                        SourceDocumentType = "IV_TRX",
                        SourceDocumentId = document,
                        SourceDocumentNo = document, SourceDocumentLine = "1",
                        EffectiveAt = at, BusinessDate = at.Date,
                        PeriodKey = owner.PeriodKey,
                        ItemCode = "A100", WarehouseCode = "MAIN",
                        LocationCode = "BIN2", ItemStatus = "ACTIVE",
                        BaseUom = "EA",
                        MovementCode = receipt ? "RECEIPT" : "ISSUE_OUT",
                        Direction = receipt ? 1 : -1,
                        BaseQty = 2m / splitCount,
                        CostMethod = StockCostMethods.MovingAverage,
                        UnitCost = StockLedgerPrecision.Money(amount / 2m),
                        CostAmount = amount / splitCount,
                        BaseCostAmount = amount / splitCount,
                        ValuationSource = receipt
                            ? StockValuationSources.ReceiptActual
                            : StockValuationSources.MovingAverage,
                        ValuationStatus = StockValuationStatuses.Valued,
                        InventoryHistoryId = movement.Id,
                        ToBalLocId = receipt ? secondPile.Id : null,
                        FromBalLocId = receipt ? null : secondPile.Id,
                        CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
                    });
                }
                await db.SaveChangesAsync();
                return movement;
            }
            await SeedMovementAsync(true, receiptAt,
                countPosting.PostingSequence + 1, 12m);
            laterIssue = await SeedMovementAsync(false, issueAt,
                countPosting.PostingSequence + 2, 8.470588m);
            var livePile = await db.IvBalLocs.SingleAsync(p =>
                p.Id == secondPile.Id);
            livePile.TransDate = issueAt;
            var liveState = await db.StockCostStates.SingleAsync();
            liveState.InventoryValue = 63.529412m;
            liveState.AverageUnitCost = StockLedgerPrecision.Money(
                liveState.InventoryValue / liveState.OnHandBaseQty);
            liveState.CurrentUnitCost = liveState.AverageUnitCost;
            liveState.LastPostingSequence = countPosting.PostingSequence + 2;
            (await db.StockPostingBranchSequences.SingleAsync()).LastSequence =
                liveState.LastPostingSequence;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        var removal = await StockReplayWacCountSheetRemovalPlanner.BuildAsync(
            db, company, branch, header.Id);
        var itemRemoval = Assert.Single(removal.Items);
        Assert.Equal(2, itemRemoval.CountLineIds.Count);
        Assert.Equal(secondSystem, itemRemoval.FinalQuantity);
        Assert.Equal(secondSystem * 2m, itemRemoval.FinalValue);
        Assert.Equal(withLaterIssue, itemRemoval.Repricing is not null);
        await using var rollbackTransaction = await db.Database.BeginTransactionAsync();
        var rollbackBegin = await coordinator.BeginInTransactionAsync(
            db, removal.Command);
        Assert.NotNull(rollbackBegin.Context);
        try
        {
            if (withLaterIssue)
            {
                var forgedItem = itemRemoval with
                {
                    Repricing = itemRemoval.Repricing! with
                    {
                        FinalValue = itemRemoval.Repricing.FinalValue + 1m
                    }
                };
                var forgedPlan = removal with { Items = [forgedItem] };
                var forged = await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayWacCountSheetRemovalPlanner.ValidateWithinRootAsync(
                        rollbackBegin.Context!, forgedPlan));
                Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                    forged.Error.Code);
            }
            await StockReplayWacCountSheetRemovalPlanner.ValidateWithinRootAsync(
                rollbackBegin.Context!, removal);
            var currentState = await db.StockCostStates.SingleAsync();
            currentState.InventoryValue += 1m;
            await db.SaveChangesAsync();
            var staleRemoval = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayWacCountSheetRemovalPlanner.ValidateWithinRootAsync(
                    rollbackBegin.Context!, removal));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                staleRemoval.Error.Code);
            currentState.InventoryValue -= 1m;
            await db.SaveChangesAsync();
            if (withLaterIssue)
            {
                var owner = await db.StockPostings.SingleAsync(p =>
                    p.Id == laterIssue!.StockPostingId);
                owner.SourceModule = "PRODUCTION";
                await db.SaveChangesAsync();
                var staleOwner = await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayWacCountSheetRemovalRootWriter
                        .AppendWithinRootAsync(rollbackBegin.Context!, removal,
                            new RunningNumberService(), "Count entered in error",
                            DateTime.UtcNow));
                Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                    staleOwner.Error.Code);
                Assert.Single(await db.IvTrxBatches.ToArrayAsync());
                owner.SourceModule = "INVENTORY";
                await db.SaveChangesAsync();
            }
            if (splitLaterIssue)
            {
                var splitFacts = await db.StockValuationFacts.AsNoTracking()
                    .Where(f => f.InventoryHistoryId == laterIssue!.Id)
                    .OrderBy(f => f.SplitOrdinal).ToArrayAsync();
                Assert.Equal(2, splitFacts.Length);
                var raised = splitFacts[0].CostAmount + 0.1m;
                var lowered = splitFacts[1].CostAmount - 0.1m;
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE StockValuationFact SET CostAmount = {raised}, BaseCostAmount = {raised}, UnitCost = {raised} WHERE Id = {splitFacts[0].Id}");
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE StockValuationFact SET CostAmount = {lowered}, BaseCostAmount = {lowered}, UnitCost = {lowered} WHERE Id = {splitFacts[1].Id}");
                var staleSplits = await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayWacCountSheetRemovalPlanner.ValidateWithinRootAsync(
                        rollbackBegin.Context!, removal));
                Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                    staleSplits.Error.Code);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE StockValuationFact SET CostAmount = {splitFacts[0].CostAmount}, BaseCostAmount = {splitFacts[0].BaseCostAmount}, UnitCost = {splitFacts[0].UnitCost} WHERE Id = {splitFacts[0].Id}");
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE StockValuationFact SET CostAmount = {splitFacts[1].CostAmount}, BaseCostAmount = {splitFacts[1].BaseCostAmount}, UnitCost = {splitFacts[1].UnitCost} WHERE Id = {splitFacts[1].Id}");
            }
            var removed = await StockReplayWacCountSheetRemovalRootWriter
                .AppendWithinRootAsync(rollbackBegin.Context!, removal,
                    new RunningNumberService(), "Count entered in error",
                    DateTime.UtcNow);
            Assert.Equal(expectedFactCount, removed.ReversalHistoryIds.Count);
            Assert.Equal(2, removed.RemovalGenerationIds.Count);
            Assert.Equal(withLaterIssue ? [laterIssue!.Id] : [],
                removed.RequiredActiveHistoryIds);
            await coordinator.CompleteReplayInTransactionAsync(
                rollbackBegin.Context!, removed.RequiredActiveHistoryIds);
            await rollbackTransaction.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(rollbackBegin.Context!);
            await rollbackTransaction.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        Assert.Equal(IvStockCountStatuses.RolledBack,
            (await db.IvStockCountHdrs.SingleAsync()).Status);
        Assert.All((await StockCountGenerationResolver.ResolveActiveAsync(
            db, company, branch, [firstLine.Id, secondLine.Id])).Values,
            generation => Assert.True(generation.IsRemoval));
        var restoredPiles = await db.IvBalLocs.AsNoTracking()
            .ToDictionaryAsync(b => b.Id);
        Assert.Equal(0m, restoredPiles[firstPile.Id].StdQty);
        Assert.Equal(secondSystem, restoredPiles[secondPile.Id].StdQty);
        var restoredState = await db.StockCostStates.SingleAsync();
        Assert.Equal(secondSystem, restoredState.OnHandBaseQty);
        Assert.Equal(secondSystem * 2m, restoredState.InventoryValue);
        Assert.Equal(2, await db.IvTrxBatches.CountAsync());
        Assert.Equal(expectedFactCount * 2
                + (withLaterIssue ? splitLaterIssue ? 7 : 4 : 0),
            await db.StockValuationFacts.CountAsync());
        if (withLaterIssue)
        {
            var activeIssue = await StockReplayValuationGenerationResolver
                .ResolveAsync(db, company, branch, [laterIssue!]);
            Assert.Equal(splitLaterIssue ? 2 : 1,
                activeIssue[laterIssue!.Id].Count);
            Assert.Equal(12m, activeIssue[laterIssue.Id].Sum(f => f.CostAmount));
        }
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
        var removal = await StockReplayWacCountSheetRemovalPlanner.BuildAsync(
            db, company, branch, header.Id);
        Assert.Equal(new[] { "A100", "B200" },
            removal.Items.Select(i => i.ItemCode));
        var selectedRemoval = await StockReplayCountSheetRemovalRootWriter
            .BuildAsync(db, company, branch, header.Id);
        Assert.NotNull(selectedRemoval.Wac);
        Assert.Equal(removal.Command.RequestId,
            selectedRemoval.Command.RequestId);
        Assert.Equal(new[] { 100m, 0m },
            removal.Items.Select(i => i.FinalQuantity));
        Assert.Equal(new[] { 200m, 0m },
            removal.Items.Select(i => i.FinalValue));
        await using var rollback = await db.Database.BeginTransactionAsync();
        var rollbackBegin = await coordinator.BeginInTransactionAsync(
            db, removal.Command);
        Assert.NotNull(rollbackBegin.Context);
        try
        {
            await StockReplayWacCountSheetRemovalPlanner.ValidateWithinRootAsync(
                rollbackBegin.Context!, removal);
            var originalDetail = await db.IvTrxBatchDetails
                .OrderBy(d => d.TrxLineNo).FirstAsync();
            originalDetail.ToStdQty += 1m;
            var dirtyDetail = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayWacCountSheetRemovalRootWriter
                    .AppendWithinRootAsync(rollbackBegin.Context!, removal,
                        new RunningNumberService(), "Count entered in error",
                        DateTime.UtcNow));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                dirtyDetail.Error.Code);
            Assert.Equal(1, await db.IvTrxBatches.CountAsync());
            await db.SaveChangesAsync();
            var changedDetail = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayWacCountSheetRemovalRootWriter
                    .AppendWithinRootAsync(rollbackBegin.Context!, removal,
                        new RunningNumberService(), "Count entered in error",
                        DateTime.UtcNow));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                changedDetail.Error.Code);
            originalDetail.ToStdQty -= 1m;
            await db.SaveChangesAsync();
            var removed = await StockReplayCountSheetRemovalRootWriter
                .AppendWithinRootAsync(rollbackBegin.Context!, selectedRemoval,
                    new RunningNumberService(), "Count entered in error",
                    DateTime.UtcNow);
            Assert.Equal(2, removed.ReversalHistoryIds.Count);
            Assert.Equal(2, removed.RemovalGenerationIds.Count);
            await coordinator.CompleteReplayInTransactionAsync(
                rollbackBegin.Context!, []);
            await rollback.CommitAsync();
        }
        catch
        {
            await coordinator.AbortInTransactionAsync(rollbackBegin.Context!);
            await rollback.RollbackAsync();
            throw;
        }
        db.ChangeTracker.Clear();
        Assert.Equal(IvStockCountStatuses.RolledBack,
            (await db.IvStockCountHdrs.SingleAsync()).Status);
        Assert.All((await StockCountGenerationResolver.ResolveActiveAsync(
            db, company, branch, [firstLine.Id, secondLine.Id])).Values,
            generation => Assert.True(generation.IsRemoval));
        var finalStates = await db.StockCostStates.AsNoTracking()
            .OrderBy(s => s.ItemCode).ToArrayAsync();
        Assert.Equal(new[] { 100m, 0m },
            finalStates.Select(s => s.OnHandBaseQty));
        Assert.Equal(new[] { 200m, 0m },
            finalStates.Select(s => s.InventoryValue));
        Assert.Equal(2, await db.IvTrxBatches.CountAsync());
        Assert.Equal(4, await db.StockValuationFacts.CountAsync());
        var finalHistories = await db.IvTrxHistories.AsNoTracking()
            .OrderBy(h => h.Id).ToArrayAsync();
        Assert.Equal(4, finalHistories.Length);
        var reversalHistories = finalHistories.Where(h =>
            h.ReversesHistoryId.HasValue).ToArray();
        Assert.Equal(2, reversalHistories.Length);
        Assert.All(reversalHistories, reversal =>
            Assert.Contains(finalHistories, original =>
                original.Id == reversal.ReversesHistoryId
                && original.StockPostingId != reversal.StockPostingId));
        foreach (var item in new[] { "A100", "B200" })
        {
            var tail = await StockReplayWacFinancialTailReader.ReadLaterAsync(
                db, company, branch, item,
                new StockReplayEventKey(FixedToday,
                    StockReplayEventKey.OrdinaryClass, 0, "before-count", 0));
            Assert.Empty(tail.LaterEvents);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_historical_multi_pile_WAC_count_reprices_later_issue_and_seals(
        bool splitIssue)
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
            RequestId = Guid.NewGuid(),
            CommandType = id == "MULTI-RECEIPT"
                ? "IV_MR_POST" : "IV_MI_POST",
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
        var originalIssueFacts = splitIssue
            ? new[]
            {
                Fact(issueOwner, issue, -1, 25m, 75m),
                Fact(issueOwner, issue, -1, 25m, 75m)
            }
            : new[] { Fact(issueOwner, issue, -1, 50m, 150m) };
        if (splitIssue)
        {
            originalIssueFacts[1].SplitOrdinal = 1;
            originalIssueFacts[1].SourceLineId =
                $"IV_TRX:{issueOwner.SourceDocumentId}:1:1:1";
        }
        db.StockValuationFacts.Add(Fact(receiptOwner, receipt, 1, 100m, 400m));
        db.StockValuationFacts.AddRange(originalIssueFacts);
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
        var changedIssue = await db.IvTrxHistories.SingleAsync(h => h.Id == issue.Id);
        changedIssue.TrxType = IvTrxTypes.IssueToProduction;
        await db.SaveChangesAsync();
        var unsupportedBuild = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayWacCountSheetPostingWriter.BuildAsync(db, sheet, bases));
        Assert.Equal(StockLedgerErrorCodes.ValuationRequired,
            unsupportedBuild.Error.Code);
        changedIssue.TrxType = IvTrxTypes.MiscellaneousIssue;
        await db.SaveChangesAsync();
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
            var laterIssue = await db.IvTrxHistories.SingleAsync(h => h.Id == issue.Id);
            laterIssue.TrxType = IvTrxTypes.IssueToProduction;
            await db.SaveChangesAsync();
            var unsupportedRoot = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayWacCountSheetPostingWriter.PrepareWithinRootAsync(
                    root, postingPlan));
            Assert.Equal(StockLedgerErrorCodes.ValuationRequired,
                unsupportedRoot.Error.Code);
            Assert.Empty(await db.StockCountCostBases.ToArrayAsync());
            laterIssue.TrxType = IvTrxTypes.MiscellaneousIssue;
            await db.SaveChangesAsync();
            var laterOwner = await db.StockPostings.SingleAsync(p =>
                p.Id == issueOwner.Id);
            laterOwner.SourceModule = "PRODUCTION";
            await db.SaveChangesAsync();
            var foreignOwner = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayWacCountSheetPostingWriter.PrepareWithinRootAsync(
                    root, postingPlan));
            Assert.Equal(StockLedgerErrorCodes.ValuationRequired,
                foreignOwner.Error.Code);
            laterOwner.SourceModule = "INVENTORY";
            await db.SaveChangesAsync();
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
        var correctedFacts = active[issue.Id];
        Assert.Equal(splitIssue ? 2 : 1, correctedFacts.Count);
        Assert.Equal(146.511650m,
            correctedFacts.Sum(f => f.CostAmount));
        var correctedFact = correctedFacts[0];
        Assert.NotNull((await db.StockPostings.SingleAsync(p =>
            p.SourceDocumentNo == header.CountNo)).SealedAtUtc);
        var timeline = await StockReplayWacFinancialTailReader.ReadLaterAsync(
            db, company, branch, "A100",
            new StockReplayEventKey(FixedToday,
                StockReplayEventKey.OrdinaryClass, 0, "before-count", 0));
        Assert.Equal(4, timeline.LaterEvents.Count);
        Assert.Equal(165m, timeline.CurrentQuantity);
        Assert.Equal(483.488350m, timeline.CurrentValue);
        var removal = await StockReplayCountSheetRemovalPlanner.BuildAsync(db,
            company, branch, header.Id);
        Assert.Equal(2, removal.Lines.Count);
        var multiLine = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayWacSingleCountRemovalPlanner.BuildAsync(
                db, company, branch, header.Id));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, multiLine.Error.Code);
        var needsFinancialReplay = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayZeroCountRemovalRootWriter.BuildAsync(
                db, company, branch, header.Id));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            needsFinancialReplay.Error.Code);
        Assert.Equal(StockReplayEventKey.CountEndOfDay(FixedToday),
            removal.EffectiveAt);
        Assert.Equal(150m, removal.Lines.Sum(l =>
            l.Physical.Projection.FinalQuantity));
        Assert.Throws<StockLedgerException>(() =>
            StockReplayCountSheetRemovalPlanner.ValidatePreparedPlan(
                removal with { Lines = removal.Lines.Take(1).ToArray() }));
        var rollbackCommand = await StockCountRollbackCommandFactory.CreateAsync(
            db, removal);
        var sameCommand = await StockCountRollbackCommandFactory.CreateAsync(
            db, removal);
        Assert.Equal(rollbackCommand.RequestId, sameCommand.RequestId);
        Assert.Equal(rollbackCommand.Evidence.RequestFingerprint,
            sameCommand.Evidence.RequestFingerprint);
        Assert.Equal(removal.AnchorPostingId,
            rollbackCommand.ReversesPostingId);
        await using var rollbackTx = await db.Database.BeginTransactionAsync();
        var rollbackBegin = await coordinator.BeginInTransactionAsync(
            db, rollbackCommand);
        Assert.NotNull(rollbackBegin.Context);
        try
        {
            await StockReplayCountSheetRemovalPlanner.ValidateWithinRootAsync(
                rollbackBegin.Context!, removal);
            var changedLine = await db.IvStockCountLines.SingleAsync(l =>
                l.Id == secondLine.Id);
            changedLine.PhysicalQty = 4m;
            await db.SaveChangesAsync();
            var staleCommand = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockCountRollbackCommandFactory.CreateAsync(db, removal));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                staleCommand.Error.Code);
            var stale = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayCountSheetRemovalPlanner.ValidateWithinRootAsync(
                    rollbackBegin.Context!, removal));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                stale.Error.Code);
        }
        finally
        {
            await coordinator.AbortInTransactionAsync(rollbackBegin.Context!);
            await rollbackTx.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        var receiptOriginId = await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.InventoryHistoryId == receipt.Id
                && f.ReversesValuationFactId == null)
            .Select(f => f.Id).SingleAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE StockValuationFact SET OriginalValuationFactId = {receiptOriginId} WHERE Id = {correctedFact.Id}");
        var foreignOrigin = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayCountSheetRemovalRootWriter.BuildAsync(
                db, company, branch, header.Id));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            foreignOrigin.Error.Code);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE StockValuationFact SET OriginalValuationFactId = {correctedFact.OriginalValuationFactId} WHERE Id = {correctedFact.Id}");
        var financialRemoval = await StockReplayCountSheetRemovalRootWriter
            .BuildAsync(db, company, branch, header.Id);
        var projectedRemoval = Assert.Single(
            Assert.IsType<StockReplayWacCountSheetRemovalPlan>(
                financialRemoval.Wac).Items);
        Assert.Equal(150m, projectedRemoval.FinalQuantity);
        Assert.Equal(450m, projectedRemoval.FinalValue);
        Assert.Single(projectedRemoval.Repricing!.LaterSteps
            .Where(step => step.EventId == issue.Id));
        await using (var staleOwnerTx = await db.Database.BeginTransactionAsync())
        {
            var staleBegin = await coordinator.BeginInTransactionAsync(
                db, financialRemoval.Command);
            Assert.NotNull(staleBegin.Context);
            try
            {
                var correctionOwner = await db.StockPostings.SingleAsync(p =>
                    p.Id == correctedFact.StockPostingId);
                correctionOwner.SourceSnapshotHash = "FORGED";
                await db.SaveChangesAsync();
                var batchesBefore = await db.IvTrxBatches.CountAsync();
                var staleOwner = await Assert.ThrowsAsync<StockLedgerException>(() =>
                    StockReplayCountSheetRemovalRootWriter.AppendWithinRootAsync(
                        staleBegin.Context!, financialRemoval,
                        new RunningNumberService(),
                        "Count entered in error", DateTime.UtcNow));
                Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                    staleOwner.Error.Code);
                Assert.Equal(batchesBefore, await db.IvTrxBatches.CountAsync());
            }
            finally
            {
                await coordinator.AbortInTransactionAsync(staleBegin.Context!);
                await staleOwnerTx.RollbackAsync();
            }
        }
        db.ChangeTracker.Clear();
        await using (var financialRollback = await db.Database.BeginTransactionAsync())
        {
            var financialBegin = await coordinator.BeginInTransactionAsync(
                db, financialRemoval.Command);
            Assert.NotNull(financialBegin.Context);
            try
            {
                var removed = await StockReplayCountSheetRemovalRootWriter
                    .AppendWithinRootAsync(financialBegin.Context!,
                        financialRemoval, new RunningNumberService(),
                        "Count entered in error", DateTime.UtcNow);
                Assert.Equal(2, removed.ReversalHistoryIds.Count);
                Assert.Equal(2, removed.RemovalGenerationIds.Count);
                Assert.Equal([issue.Id], removed.RequiredActiveHistoryIds);
                await coordinator.CompleteReplayInTransactionAsync(
                    financialBegin.Context!, removed.RequiredActiveHistoryIds);
                await financialRollback.CommitAsync();
            }
            catch
            {
                await coordinator.AbortInTransactionAsync(
                    financialBegin.Context!);
                await financialRollback.RollbackAsync();
                throw;
            }
        }
        db.ChangeTracker.Clear();
        var restoredState = await db.StockCostStates.SingleAsync();
        Assert.Equal(150m, restoredState.OnHandBaseQty);
        Assert.Equal(450m, restoredState.InventoryValue);
        var restoredIssue = await StockReplayValuationGenerationResolver
            .ResolveAsync(db, company, branch, [issue]);
        var restoredFacts = restoredIssue[issue.Id];
        Assert.Equal(splitIssue ? 2 : 1, restoredFacts.Count);
        Assert.Equal(150m, restoredFacts.Sum(f => f.CostAmount));
        var originalIds = (await db.StockValuationFacts.AsNoTracking()
            .Where(f => f.StockPostingId == issueOwner.Id)
            .Select(f => f.Id).ToArrayAsync()).Order().ToArray();
        Assert.Equal(originalIds, restoredFacts
            .Select(f => f.OriginalValuationFactId!.Value).Order());
        Assert.Equal(IvStockCountStatuses.RolledBack,
            (await db.IvStockCountHdrs.SingleAsync()).Status);
        var secondHeader = new IvStockCountHdr
        {
            CompanyCode = company, BranchCode = branch,
            CountNo = "CC-MULTI-SECOND", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var secondFirstLine = new IvStockCountLine
        {
            Header = secondHeader, LineNumber = 1, BalLocId = firstPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 100m, PhysicalQty = 110m
        };
        var secondSecondLine = new IvStockCountLine
        {
            Header = secondHeader, LineNumber = 2, BalLocId = secondPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdUom = "EA",
            SystemQty = 0m, PhysicalQty = 5m
        };
        db.IvStockCountLines.AddRange(secondFirstLine, secondSecondLine);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var secondBases = new[]
        {
            Basis(secondFirstLine.Id), Basis(secondSecondLine.Id)
        };
        var secondSheet = await StockReplayPhysicalPlanner
            .BuildCountSheetInsertionAsync(db, company, branch, secondHeader.Id);
        var secondPosting = await StockReplayWacCountSheetPostingWriter
            .BuildAsync(db, secondSheet, secondBases);
        var secondCommand = await StockCountAnchorCommandFactory.CreateAsync(
            db, company, branch, secondHeader.Id, secondBases);
        await using (var secondTx = await db.Database.BeginTransactionAsync())
        {
            var secondBegin = await coordinator.BeginInTransactionAsync(
                db, secondCommand);
            Assert.NotNull(secondBegin.Context);
            try
            {
                var prepared = await StockReplayWacCountSheetPostingWriter
                    .PrepareWithinRootAsync(secondBegin.Context!, secondPosting);
                var inserted = await StockReplayWacCountSheetPostingWriter
                    .AppendWithinRootAsync(secondBegin.Context!, prepared,
                        secondBases, new RunningNumberService());
                Assert.Equal([issue.Id], inserted.RepricedHistoryIds);
                await coordinator.CompleteReplayInTransactionAsync(
                    secondBegin.Context!, inserted.RequiredActiveHistoryIds);
                await secondTx.CommitAsync();
            }
            catch
            {
                await coordinator.AbortInTransactionAsync(secondBegin.Context!);
                await secondTx.RollbackAsync();
                throw;
            }
        }
        db.ChangeTracker.Clear();
        var secondIssue = await StockReplayValuationGenerationResolver
            .ResolveAsync(db, company, branch, [issue]);
        Assert.Equal(146.511650m,
            secondIssue[issue.Id].Sum(f => f.CostAmount));
        Assert.Equal(originalIds, secondIssue[issue.Id]
            .Select(f => f.OriginalValuationFactId!.Value).Order());
        Assert.Equal(483.488350m,
            (await db.StockCostStates.SingleAsync()).InventoryValue);
        var secondRemoval = await StockReplayCountSheetRemovalRootWriter
            .BuildAsync(db, company, branch, secondHeader.Id);
        await using (var secondRollbackTx = await db.Database.BeginTransactionAsync())
        {
            var secondRollbackBegin = await coordinator.BeginInTransactionAsync(
                db, secondRemoval.Command);
            Assert.NotNull(secondRollbackBegin.Context);
            try
            {
                var removed = await StockReplayCountSheetRemovalRootWriter
                    .AppendWithinRootAsync(secondRollbackBegin.Context!,
                        secondRemoval, new RunningNumberService(),
                        "Count entered in error", DateTime.UtcNow);
                Assert.Equal([issue.Id], removed.RequiredActiveHistoryIds);
                await coordinator.CompleteReplayInTransactionAsync(
                    secondRollbackBegin.Context!, removed.RequiredActiveHistoryIds);
                await secondRollbackTx.CommitAsync();
            }
            catch
            {
                await coordinator.AbortInTransactionAsync(
                    secondRollbackBegin.Context!);
                await secondRollbackTx.RollbackAsync();
                throw;
            }
        }
        db.ChangeTracker.Clear();
        var finalIssue = await StockReplayValuationGenerationResolver
            .ResolveAsync(db, company, branch, [issue]);
        Assert.Equal(150m, finalIssue[issue.Id].Sum(f => f.CostAmount));
        Assert.Equal(originalIds, finalIssue[issue.Id]
            .Select(f => f.OriginalValuationFactId!.Value).Order());
        var finalCost = await db.StockCostStates.SingleAsync();
        Assert.Equal(150m, finalCost.OnHandBaseQty);
        Assert.Equal(450m, finalCost.InventoryValue);
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
        var asOfService = new InventoryAsOfStockService();
        var publicBefore = await asOfService.GetAsync(db,
            header.CompanyCode, header.BranchCode, [balanceId],
            FixedToday.AddHours(12));
        var publicAfter = await asOfService.GetAsync(db,
            header.CompanyCode, header.BranchCode, [balanceId],
            StockReplayEventKey.CountEndOfDay(FixedToday));
        Assert.Equal(100m, publicBefore[balanceId].AsOfBaseQty);
        Assert.Equal(110m, publicAfter[balanceId].AsOfBaseQty);
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
            var revaluationNeedsCorrection = await Assert.ThrowsAsync<
                StockLedgerException>(() =>
                StockReplayCountSheetRemovalRootWriter.BuildAsync(db,
                    header.CompanyCode, header.BranchCode, header.Id));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                revaluationNeedsCorrection.Error.Code);
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
    [Fact]
    public async Task Fifo_count_sheet_plan_checks_frozen_basis_and_rechecks_pool_inside_root()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
        var state = await db.StockCostStates.SingleAsync();
        state.CostMethod = StockCostMethods.Fifo;
        state.OnHandBaseQty = 0m;
        state.InventoryValue = 0m;
        state.AverageUnitCost = 0m;
        state.CurrentUnitCost = 0m;
        var balance = await db.IvBalLocs.SingleAsync();
        balance.StdQty = 0m;
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = balance.CompanyCode, BranchCode = balance.BranchCode,
            WarehouseCode = "MAIN", LocCode = "BIN2", IsActive = true
        });
        var secondBalance = new IvBalLoc
        {
            CompanyCode = balance.CompanyCode, BranchCode = balance.BranchCode,
            ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdQty = 0m,
            StdUom = "EA", TransDate = FixedToday
        };
        db.IvBalLocs.Add(secondBalance);
        await db.SaveChangesAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            CountNo = "CC-FIFO-PLAN", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = balance.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", PhysicalQty = 2m,
            SystemQty = 0m
        };
        db.IvStockCountLines.Add(line);
        var secondLine = new IvStockCountLine
        {
            Header = header, LineNumber = 2, BalLocId = secondBalance.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", PhysicalQty = 1m,
            SystemQty = 0m
        };
        db.IvStockCountLines.Add(secondLine);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var basis = new StockCountCostBasis
        {
            CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
            StockCountLineId = line.Id, FrozenFallbackUnitCost = 3m,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = "Verified count cost", CostApprovedBy = "manager",
            CostApprovedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow
        };
        var secondBasis = new StockCountCostBasis
        {
            CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
            StockCountLineId = secondLine.Id, FrozenFallbackUnitCost = 4m,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = "Verified second count cost",
            CostApprovedBy = "manager",
            CostApprovedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow
        };
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        var missingBasis = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayFifoCountSheetInsertionPlanner.BuildAsync(db, sheet, "A100", []));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, missingBasis.Error.Code);
        var plan = await StockReplayFifoCountSheetInsertionPlanner.BuildAsync(
            db, sheet, "A100", [basis, secondBasis]);
        Assert.Equal(3m, plan.Projection.FinalQuantity);
        Assert.Equal(9m, plan.Projection.FinalValue);
        Assert.Equal(3m, plan.Projection.Counts[0].Receipt!.CurrentUnitCost);
        Assert.Equal(3m, plan.Projection.Counts[1].Receipt!.CurrentUnitCost);
        var wholeSheet = await StockReplayFifoCountSheetPostingPlanner.BuildAsync(
            db, sheet, [basis, secondBasis]);
        Assert.Equal(plan.PlanHash, Assert.Single(wholeSheet.Items).PlanHash);
        var incompleteSheet = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayFifoCountSheetPostingPlanner.BuildAsync(
                db, sheet, [basis]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            incompleteSheet.Error.Code);
        var changedCandidate = plan with
        {
            Candidates = [plan.Candidates[0] with { SignedAdjustmentQuantity = 3m }]
        };
        var forged = Assert.Throws<StockLedgerException>(() =>
            StockReplayFifoCountSheetInsertionPlanner.ValidatePreparedPlan(
                changedCandidate));
        Assert.Equal(StockLedgerErrorCodes.StaleDocument, forged.Error.Code);

        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, header.CompanyCode, header.BranchCode, header.Id,
            [basis, secondBasis]);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        var committed = false;
        try
        {
            var prepared = await StockReplayFifoCountSheetPostingPlanner
                .PrepareWithinRootAsync(root, wholeSheet);
            Assert.Equal(root.Posting.Id, prepared.PhysicalPermit.RootPostingId);
            var permit = prepared.ItemPermits["A100"];
            Assert.Equal(root.Posting.Id, permit.RootPostingId);
            Assert.Equal(plan.PlanHash, permit.PlanHash);
            Assert.Equal(9m, permit.FinalValue);
            secondBasis.FrozenFallbackUnitCost = 5m;
            var changedBasis = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayFifoCountSheetPostingPlanner.PrepareWithinRootAsync(
                    root, wholeSheet));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                changedBasis.Error.Code);
            secondBasis.FrozenFallbackUnitCost = 4m;
            var costState = await db.StockCostStates.SingleAsync();
            costState.InventoryValue = 1m;
            await db.SaveChangesAsync();
            var stale = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayFifoCountSheetInsertionPlanner.PrepareWithinRootAsync(
                    root, plan));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument, stale.Error.Code);
            costState.InventoryValue = 0m;
            await db.SaveChangesAsync();
            var posted = await StockReplayFifoInitialCountSheetWriter
                .AppendWithinRootAsync(root, prepared, new RunningNumberService());
            Assert.Equal(2, posted.Facts.Count);
            Assert.Equal(2, posted.RequiredActiveHistoryIds.Count);
            Assert.Equal(3m, posted.Facts[1].UnitCost);
            Assert.Equal(StockValuationSources.ManualApproved,
                posted.Facts[0].ValuationSource);
            Assert.Equal(StockValuationSources.Fifo,
                posted.Facts[1].ValuationSource);
            await coordinator.CompleteReplayInTransactionAsync(root,
                posted.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                await coordinator.AbortInTransactionAsync(root);
                await transaction.RollbackAsync();
            }
        }
        db.ChangeTracker.Clear();
        var final = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, "A100");
        Assert.Equal(3m, final.CurrentQuantity);
        Assert.Equal(9m, final.CurrentValue);
        Assert.Equal(2, final.Layers.Count);
        Assert.All(final.Layers, layer => Assert.Equal(
            StockReplayEventKey.CountClass,
            layer.ReceiptKey!.Value.ClassPriority));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Fifo_empty_pool_zero_count_seals_without_valuation_or_layer(
        bool laterReceipt, bool laterIssue, bool secondPile)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
        var state = await db.StockCostStates.SingleAsync();
        state.CostMethod = StockCostMethods.Fifo;
        state.OnHandBaseQty = 0m;
        state.InventoryValue = 0m;
        state.AverageUnitCost = 0m;
        state.CurrentUnitCost = 0m;
        var balance = await db.IvBalLocs.SingleAsync();
        balance.StdQty = 0m;
        IvBalLoc? otherBalance = null;
        if (secondPile)
        {
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = balance.CompanyCode,
                BranchCode = balance.BranchCode,
                WarehouseCode = "MAIN", LocCode = "BIN2", IsActive = true
            });
            otherBalance = new IvBalLoc
            {
                CompanyCode = balance.CompanyCode,
                BranchCode = balance.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = "", IStatus = "ACTIVE", StdQty = 0m,
                StdUom = "EA"
            };
            db.IvBalLocs.Add(otherBalance);
        }
        await db.SaveChangesAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            CountNo = "CC-FIFO-ZERO", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        db.IvStockCountLines.Add(new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = balance.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", PhysicalQty = 0m,
            SystemQty = 0m
        });
        if (otherBalance is not null)
            db.IvStockCountLines.Add(new IvStockCountLine
            {
                Header = header, LineNumber = 2, BalLocId = otherBalance.Id,
                ICode = "A100", WHCode = "MAIN", LocCode = "BIN2",
                LotNo = "", IStatus = "ACTIVE", PhysicalQty = 0m,
                SystemQty = 0m
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        var plan = await StockReplayFifoCountSheetPostingPlanner.BuildAsync(
            db, sheet, []);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, header.CompanyCode, header.BranchCode, header.Id, []);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        var committed = false;
        try
        {
            var prepared = await StockReplayFifoCountSheetPostingPlanner
                .PrepareWithinRootAsync(root, plan);
            var posted = await StockReplayFifoInitialCountSheetWriter
                .AppendWithinRootAsync(root, prepared, new RunningNumberService());
            Assert.Empty(posted.Facts);
            Assert.Empty(posted.RequiredActiveHistoryIds);
            Assert.Equal(secondPile ? 2 : 1,
                posted.History.Generations.Count);
            await coordinator.CompleteReplayInTransactionAsync(root,
                posted.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                await coordinator.AbortInTransactionAsync(root);
                await transaction.RollbackAsync();
            }
        }
        db.ChangeTracker.Clear();
        var final = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, "A100");
        Assert.Equal(0m, final.CurrentQuantity);
        Assert.Equal(0m, final.CurrentValue);
        Assert.Empty(final.Layers);
        if (laterReceipt)
        {
            var receiptAt = FixedToday.AddDays(1);
            var receiptPile = otherBalance ?? balance;
            var receiptCommand = new StockPostingCommand
            {
                RequestId = Guid.NewGuid(),
                CommandType = "MR_AFTER_ZERO_COUNT_TEST",
                SourceModule = "INVENTORY",
                SourceDocumentType = IvTrxTypes.MiscellaneousReceipt,
                SourceDocumentId = "708", SourceDocumentNo = "708",
                DocumentRevision = 1, PostingRole = "PRIMARY",
                EffectiveAt = receiptAt,
                Evidence = StockPostingFingerprint.Create(
                    new { BatchNo = 708, Item = "A100", Quantity = 2m },
                    new { EffectiveAt = receiptAt })
            };
            var receiptResult = await coordinator.ExecuteAsync(receiptCommand,
                async (context, _) =>
                {
                    var pile = await context.Db.IvBalLocs.SingleAsync(p =>
                        p.Id == receiptPile.Id);
                    pile.StdQty += 2m;
                    pile.TransDate = receiptAt;
                    var history = new IvTrxHistory
                    {
                        CompanyCode = header.CompanyCode,
                        BranchCode = header.BranchCode,
                        BatchNo = 708, TrxLineNo = 1,
                        TrxType = IvTrxTypes.MiscellaneousReceipt,
                        BatchStatus = IvBatchStatuses.Posted,
                        ICode = "A100", IStatus = "ACTIVE",
                        ToBalLocId = receiptPile.Id,
                        ToWarehouse = "MAIN", ToLocation = receiptPile.LocCode,
                        ToStdQty = 2m, ToStdUom = "EA",
                        UnitPrice = 3m,
                        PriceEvidence = InventoryCostEvidenceTypes.ManualApproved
                    };
                    context.Db.IvTrxHistories.Add(history);
                    new IvInventoryHistoryWriter().StampGeneration(
                        context, [history], 1);
                    return 0;
                });
            Assert.True(receiptResult.Succeeded,
                receiptResult.Error?.Message);
            db.ChangeTracker.Clear();
            var withReceipt = await StockReplayFifoActiveLayerReader.ReadAsync(
                db, header.CompanyCode, header.BranchCode, "A100");
            Assert.Equal(2m, withReceipt.CurrentQuantity);
            Assert.Equal(6m, withReceipt.CurrentValue);
            Assert.Single(withReceipt.Layers);
        }
        StockReplayFifoConsumptionPosition? originalIssue = null;
        if (laterIssue)
        {
            var issueAt = FixedToday.AddDays(2);
            var issuePile = otherBalance ?? balance;
            var issueCommand = new StockPostingCommand
            {
                RequestId = Guid.NewGuid(),
                CommandType = "MI_AFTER_ZERO_COUNT_MR_TEST",
                SourceModule = "INVENTORY",
                SourceDocumentType = IvTrxTypes.MiscellaneousIssue,
                SourceDocumentId = "709", SourceDocumentNo = "709",
                DocumentRevision = 1, PostingRole = "PRIMARY",
                EffectiveAt = issueAt,
                Evidence = StockPostingFingerprint.Create(
                    new { BatchNo = 709, Item = "A100", Quantity = 1m },
                    new { EffectiveAt = issueAt })
            };
            var issueResult = await coordinator.ExecuteAsync(issueCommand,
                async (context, _) =>
                {
                    var pile = await context.Db.IvBalLocs.SingleAsync(p =>
                        p.Id == issuePile.Id);
                    pile.StdQty -= 1m;
                    pile.TransDate = issueAt;
                    var history = new IvTrxHistory
                    {
                        CompanyCode = header.CompanyCode,
                        BranchCode = header.BranchCode,
                        BatchNo = 709, TrxLineNo = 1,
                        TrxType = IvTrxTypes.MiscellaneousIssue,
                        BatchStatus = IvBatchStatuses.Posted,
                        ICode = "A100", IStatus = "ACTIVE",
                        FromBalLocId = issuePile.Id,
                        FrWarehouse = "MAIN", FrLocation = issuePile.LocCode,
                        FrStdQty = 1m, FrStdUom = "EA"
                    };
                    context.Db.IvTrxHistories.Add(history);
                    new IvInventoryHistoryWriter().StampGeneration(
                        context, [history], 1);
                    return 0;
                });
            Assert.True(issueResult.Succeeded, issueResult.Error?.Message);
            db.ChangeTracker.Clear();
            originalIssue = Assert.Single((await
                StockReplayFifoActiveLayerReader.ReadAsync(db,
                    header.CompanyCode, header.BranchCode,
                    "A100")).ActiveConsumptions);
        }

        var removal = await StockReplayCountSheetRemovalRootWriter.BuildAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        Assert.Equal(StockCostMethods.Fifo, removal.CostMethod);
        Assert.False(string.IsNullOrWhiteSpace(
            Assert.IsType<StockReplayZeroCountRemovalRootPlan>(removal.Zero)
                .FinancialHash));
        await using var rollback = await db.Database.BeginTransactionAsync();
        var reverse = await coordinator.BeginInTransactionAsync(db,
            removal.Command);
        Assert.NotNull(reverse.Context);
        var rollbackRoot = reverse.Context!;
        var pendingState = await db.StockCostStates.SingleAsync();
        pendingState.CurrentUnitCost = 1m;
        var pendingEdit = await Assert.ThrowsAsync<StockLedgerException>(() =>
            StockReplayCountSheetRemovalRootWriter.AppendWithinRootAsync(
                rollbackRoot, removal, new RunningNumberService(),
                "Count entered in error", DateTime.UtcNow));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            pendingEdit.Error.Code);
        Assert.Equal(secondPile ? 2 : 1,
            await db.StockCountReplayGenerations.CountAsync());
        await db.Entry(pendingState).ReloadAsync();
        if (laterReceipt)
        {
            await db.IvTrxHistories.Where(h => h.BatchNo == 708)
                .ExecuteUpdateAsync(s => s.SetProperty(h =>
                    h.TrxLineNo, 99));
            var changedTail = await Assert.ThrowsAsync<StockLedgerException>(
                () => StockReplayCountSheetRemovalRootWriter
                    .AppendWithinRootAsync(rollbackRoot, removal,
                        new RunningNumberService(),
                        "Count entered in error", DateTime.UtcNow));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                changedTail.Error.Code);
            Assert.Equal(secondPile ? 2 : 1,
                await db.StockCountReplayGenerations.CountAsync());
            await db.IvTrxHistories.Where(h => h.BatchNo == 708)
                .ExecuteUpdateAsync(s => s.SetProperty(h =>
                    h.TrxLineNo, 1));
        }
        var removed = await StockReplayCountSheetRemovalRootWriter
            .AppendWithinRootAsync(rollbackRoot, removal,
                new RunningNumberService(), "Count entered in error",
                DateTime.UtcNow);
        Assert.Empty(removed.ReversalHistoryIds);
        Assert.Equal(secondPile ? 2 : 1,
            removed.RemovalGenerationIds.Count);
        Assert.Empty(removed.RequiredActiveHistoryIds);
        await coordinator.CompleteReplayInTransactionAsync(rollbackRoot,
            removed.RequiredActiveHistoryIds);
        await rollback.CommitAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(IvStockCountStatuses.RolledBack,
            (await db.IvStockCountHdrs.SingleAsync()).Status);
        Assert.True(Assert.Single(await db.StockCountReplayGenerations
            .OrderByDescending(g => g.Id).Take(1).ToArrayAsync()).IsRemoval);
        Assert.Equal((laterReceipt ? 1 : 0) + (laterIssue ? 1 : 0),
            await db.StockValuationFacts.CountAsync());
        var afterRemoval = await StockReplayFifoActiveLayerReader.ReadAsync(
            db, header.CompanyCode, header.BranchCode, "A100");
        Assert.Equal(laterIssue ? 1m : laterReceipt ? 2m : 0m,
            afterRemoval.CurrentQuantity);
        Assert.Equal(laterIssue ? 3m : laterReceipt ? 6m : 0m,
            afterRemoval.CurrentValue);
        Assert.Equal(laterReceipt ? 1 : 0, afterRemoval.Layers.Count);
        if (laterIssue)
            Assert.Equal(originalIssue,
                Assert.Single(afterRemoval.ActiveConsumptions));
        else
            Assert.Empty(afterRemoval.ActiveConsumptions);
        Assert.Equal(secondPile ? 0m
                : laterIssue ? 1m : laterReceipt ? 2m : 0m,
            (await db.IvBalLocs.SingleAsync(p =>
                p.Id == balance.Id)).StdQty);
        if (otherBalance is not null)
            Assert.Equal(laterIssue ? 1m : 2m,
                (await db.IvBalLocs.SingleAsync(p =>
                    p.Id == otherBalance.Id)).StdQty);
    }

    [Theory]
    [InlineData(1, -1, 1, 3)]
    [InlineData(3, 1, 3, 9)]
    public async Task Fifo_existing_pool_count_writes_projected_layer_effect(
        decimal physicalQty, decimal signedAdjustment,
        decimal finalQuantity, decimal finalValue)
    {
        await using var db = await _factory.CreateDbContextAsync();
        // SQLite stores EF decimals as TEXT; its SQL Server check expression
        // compares that text to numeric zero. Other committed count fixtures
        // use the same provider adjustment.
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
        db.StockCostStates.Remove(await db.StockCostStates.SingleAsync());
        await db.SaveChangesAsync();
        await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [new FifoLayerSeed(2m, 3m, FixedToday.AddDays(-1))]);
        var balance = await db.IvBalLocs.SingleAsync();
        balance.StdQty = 2m;
        var header = new IvStockCountHdr
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            CountNo = "CC-FIFO-NONEMPTY", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = balance.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", PhysicalQty = physicalQty,
            SystemQty = 2m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var basis = new StockCountCostBasis
        {
            CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
            StockCountLineId = line.Id, FrozenFallbackUnitCost = 4m,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = "Verified count cost", CostApprovedBy = "manager",
            CostApprovedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow
        };
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        var plan = await StockReplayFifoCountSheetPostingPlanner.BuildAsync(
            db, sheet, [basis]);
        Assert.Equal(signedAdjustment, Assert.Single(plan.Items).Projection.Counts[0]
            .SignedAdjustmentQuantity);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, header.CompanyCode, header.BranchCode, header.Id, [basis]);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        var committed = false;
        try
        {
            var prepared = await StockReplayFifoCountSheetPostingPlanner
                .PrepareWithinRootAsync(root, plan);
            var posted = await StockReplayFifoInitialCountSheetWriter
                .AppendWithinRootAsync(root, prepared, new RunningNumberService());
            var fact = Assert.Single(posted.Facts);
            Assert.Equal(Math.Sign(signedAdjustment), fact.Direction);
            Assert.Equal(3m, fact.CostAmount);
            Assert.Equal(finalQuantity, (await db.IvBalLocs.SingleAsync()).StdQty);
            await coordinator.CompleteReplayInTransactionAsync(root,
                posted.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                await coordinator.AbortInTransactionAsync(root);
                await transaction.RollbackAsync();
            }
        }
        db.ChangeTracker.Clear();
        var final = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, "A100");
        Assert.Equal(finalQuantity, final.CurrentQuantity);
        Assert.Equal(finalValue, final.CurrentValue);
        if (signedAdjustment < 0m)
        {
            Assert.Equal(1m, Assert.Single(final.Layers).RemainingQuantity);
            Assert.Equal(StockReplayEventKey.CountClass,
                Assert.Single(final.ActiveConsumptions).IssueKey.ClassPriority);
        }
        else
        {
            Assert.Equal(2, final.Layers.Count);
            Assert.Empty(final.ActiveConsumptions);
            Assert.Equal(StockReplayEventKey.CountClass,
                Assert.Single(final.Layers.Where(l =>
                    l.ReceiptKey?.ClassPriority == StockReplayEventKey.CountClass))
                    .ReceiptKey!.Value.ClassPriority);
        }
        var countKey = signedAdjustment < 0m
            ? Assert.Single(final.ActiveConsumptions).IssueKey
            : Assert.Single(final.Layers.Where(l =>
                l.ReceiptKey?.ClassPriority == StockReplayEventKey.CountClass))
                .ReceiptKey!.Value;
        var removal = StockReplayFifoCountSheetProjector.Remove(final,
            [countKey]);
        Assert.Equal(signedAdjustment,
            Assert.Single(removal.RemovedCounts).SignedAdjustmentQuantity);
        Assert.Equal(2m, removal.FinalQuantity);
        Assert.Equal(6m, removal.FinalValue);

        var removalPlan = await StockReplayFifoCountSheetRemovalPlanner
            .BuildAsync(db, header.CompanyCode, header.BranchCode, header.Id);
        var plannedItem = Assert.Single(removalPlan.Items);
        Assert.Equal(2m, plannedItem.Projection!.FinalQuantity);
        Assert.Equal(6m, plannedItem.Projection.FinalValue);
        Assert.Same(plannedItem,
            StockReplayFifoCountSheetRemovalPlanner
                .RequireIsolatedAdjustment(removalPlan));
        Assert.Equal(StockLedgerErrorCodes.StaleDocument,
            Assert.Throws<StockLedgerException>(() =>
                StockReplayFifoCountSheetRemovalPlanner.ValidatePreparedPlan(
                    removalPlan with
                    {
                        Items = [plannedItem with { ItemHash = "altered" }]
                    })).Error.Code);
        await using var rollback = await db.Database.BeginTransactionAsync();
        var reverse = await coordinator.BeginInTransactionAsync(db,
            removalPlan.Command);
        Assert.NotNull(reverse.Context);
        var rollbackRoot = reverse.Context!;
        try
        {
            await StockReplayFifoCountSheetRemovalPlanner
                .ValidateWithinRootAsync(rollbackRoot, removalPlan);
            var pendingState = await db.StockCostStates.SingleAsync();
            pendingState.CurrentUnitCost += 1m;
            var stale = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayFifoCountSheetRemovalPlanner
                    .ValidateWithinRootAsync(rollbackRoot, removalPlan));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                stale.Error.Code);
            pendingState.InventoryValue += 1m;
            await db.SaveChangesAsync();
            stale = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayFifoCountSheetRemovalPlanner
                    .ValidateWithinRootAsync(rollbackRoot, removalPlan));
            Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                stale.Error.Code);
        }
        finally
        {
            await coordinator.AbortInTransactionAsync(rollbackRoot);
            await rollback.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        var rootPlan = await StockReplayCountSheetRemovalRootWriter
            .BuildAsync(db, header.CompanyCode, header.BranchCode,
                header.Id);
        Assert.NotNull(rootPlan.Fifo);
        await using var committedRemoval =
            await db.Database.BeginTransactionAsync();
        var started = await coordinator.BeginInTransactionAsync(db,
            rootPlan.Command);
        Assert.NotNull(started.Context);
        var reversalRoot = started.Context!;
        var removalCommitted = false;
        try
        {
            var written = await StockReplayCountSheetRemovalRootWriter
                .AppendWithinRootAsync(reversalRoot, rootPlan,
                    new RunningNumberService(), "Correct count", DateTime.UtcNow);
            Assert.Single(written.ReversalHistoryIds);
            Assert.Single(written.RemovalGenerationIds);
            await coordinator.CompleteReplayInTransactionAsync(reversalRoot,
                written.RequiredActiveHistoryIds);
            await committedRemoval.CommitAsync();
            removalCommitted = true;
        }
        finally
        {
            if (!removalCommitted)
            {
                await coordinator.AbortInTransactionAsync(reversalRoot);
                await committedRemoval.RollbackAsync();
            }
        }
        db.ChangeTracker.Clear();
        var afterRemoval = await StockReplayFifoActiveLayerReader.ReadAsync(
            db, header.CompanyCode, header.BranchCode, "A100");
        Assert.Equal(2m, afterRemoval.CurrentQuantity);
        Assert.Equal(6m, afterRemoval.CurrentValue);
        Assert.Single(afterRemoval.Layers);
        Assert.Empty(afterRemoval.ActiveConsumptions);
        Assert.Equal(IvStockCountStatuses.RolledBack,
            (await db.IvStockCountHdrs.SingleAsync()).Status);
        if (signedAdjustment > 0m)
            Assert.Equal(StockFifoLayerStatuses.Closed,
                (await db.StockFifoLayers.SingleAsync(l =>
                    l.OriginStockPostingId == root.Posting.Id)).Status);
        else
            Assert.Equal(StockFifoLayerStatuses.Open,
                (await db.StockFifoLayers.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public async Task Fifo_count_removal_records_unchanged_zero_line_beside_adjustment(
        decimal countedQuantity)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
        db.StockCostStates.Remove(await db.StockCostStates.SingleAsync());
        await db.SaveChangesAsync();
        await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [new FifoLayerSeed(2m, 3m, FixedToday.AddDays(-1))]);
        var pile = await db.IvBalLocs.SingleAsync();
        pile.StdQty = 2m;
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = pile.CompanyCode, BranchCode = pile.BranchCode,
            WarehouseCode = "MAIN", LocCode = "BIN2", IsActive = true
        });
        var zeroPile = new IvBalLoc
        {
            CompanyCode = pile.CompanyCode, BranchCode = pile.BranchCode,
            ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdQty = 0m,
            StdUom = "EA", TransDate = FixedToday
        };
        db.IvBalLocs.Add(zeroPile);
        await db.SaveChangesAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = pile.CompanyCode, BranchCode = pile.BranchCode,
            CountNo = "CC-FIFO-POSITIVE-WITH-ZERO", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var adjustedLine = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = pile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", PhysicalQty = countedQuantity,
            SystemQty = 2m
        };
        var zeroLine = new IvStockCountLine
        {
            Header = header, LineNumber = 2, BalLocId = zeroPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", PhysicalQty = 0m,
            SystemQty = 0m
        };
        db.IvStockCountLines.AddRange(adjustedLine, zeroLine);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var access = new Mock<IAccessRightService>();
        access.Setup(a => a.CanAsync(
                ErpWeb.Core.Menus.MenuCodes.InventoryStockCount,
                ErpWeb.Core.Menus.PermissionCodes.PriceOverride,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        IReadOnlyList<StockCountCostApprovalRequest> approvals =
            countedQuantity > 2m
                ? [new(adjustedLine.Id,
                    StockCountCostBasisKinds.ManualApproved, 4m,
                    "Verified count cost")]
                : [];
        var post = await StockReplayCountSheetRootWriter.BuildAsync(
            db, access.Object, header.CompanyCode, header.BranchCode,
            header.Id, approvals, "manager", DateTime.UtcNow);
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var begin = await coordinator.BeginInTransactionAsync(db, post.Command);
            Assert.NotNull(begin.Context);
            var result = await StockReplayCountSheetRootWriter.AppendWithinRootAsync(
                begin.Context!, post, new RunningNumberService());
            await coordinator.CompleteReplayInTransactionAsync(begin.Context!,
                result.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var removal = await StockReplayCountSheetRemovalRootWriter.BuildAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        Assert.NotNull(removal.Fifo);
        Assert.Same(Assert.Single(removal.Fifo.Items),
            StockReplayFifoCountSheetRemovalPlanner
                .RequireIsolatedAdjustment(removal.Fifo));
        await using (var staleTransaction = await db.Database.BeginTransactionAsync())
        {
            var staleBegin = await coordinator.BeginInTransactionAsync(db,
                removal.Command);
            Assert.NotNull(staleBegin.Context);
            var pendingZero = await db.IvBalLocs.SingleAsync(b =>
                b.Id == zeroPile.Id);
            pendingZero.StdQty = 1m;
            var error = await Assert.ThrowsAsync<StockLedgerException>(() =>
                StockReplayCountSheetRemovalRootWriter.AppendWithinRootAsync(
                    staleBegin.Context!, removal, new RunningNumberService(),
                    "Correct count", DateTime.UtcNow));
            Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                error.Error.Code);
            await coordinator.AbortInTransactionAsync(staleBegin.Context!);
            await staleTransaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var begin = await coordinator.BeginInTransactionAsync(db,
                removal.Command);
            Assert.NotNull(begin.Context);
            var written = await StockReplayCountSheetRemovalRootWriter
                .AppendWithinRootAsync(begin.Context!, removal,
                    new RunningNumberService(), "Correct count", DateTime.UtcNow);
            Assert.Single(written.ReversalHistoryIds);
            Assert.Equal(2, written.RemovalGenerationIds.Count);
            await coordinator.CompleteReplayInTransactionAsync(begin.Context!,
                written.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var active = await StockCountGenerationResolver.ResolveActiveAsync(db,
            header.CompanyCode, header.BranchCode,
            [adjustedLine.Id, zeroLine.Id]);
        Assert.Equal(2, active.Count);
        Assert.All(active.Values, g => Assert.True(g.IsRemoval));
        var final = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, "A100");
        Assert.Equal(2m, final.CurrentQuantity);
        Assert.Equal(6m, final.CurrentValue);
        Assert.Equal(2m, (await db.IvBalLocs.SingleAsync(b => b.Id == pile.Id)).StdQty);
        Assert.Equal(0m, (await db.IvBalLocs.SingleAsync(b => b.Id == zeroPile.Id)).StdQty);
        Assert.Equal(IvStockCountStatuses.RolledBack,
            (await db.IvStockCountHdrs.SingleAsync(h => h.Id == header.Id)).Status);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(2, 1)]
    public async Task Fifo_count_removal_preserves_earlier_issue(
        decimal countedQuantity, decimal adjustment)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
        db.StockCostStates.Remove(await db.StockCostStates.SingleAsync());
        await db.SaveChangesAsync();
        await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [new FifoLayerSeed(2m, 3m, FixedToday.AddDays(-2))]);
        var pile = await db.IvBalLocs.SingleAsync();
        pile.StdQty = 2m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var issueDate = FixedToday.AddDays(-1);
        var issueCommand = new StockPostingCommand
        {
            RequestId = Guid.NewGuid(), CommandType = "MI_BEFORE_COUNT_TEST",
            SourceModule = "INVENTORY",
            SourceDocumentType = IvTrxTypes.MiscellaneousIssue,
            SourceDocumentId = "811", SourceDocumentNo = "811",
            DocumentRevision = 1, PostingRole = "PRIMARY",
            EffectiveAt = issueDate,
            Evidence = StockPostingFingerprint.Create(
                new { BatchNo = 811, Item = "A100", Quantity = 1m },
                new { EffectiveAt = issueDate })
        };
        var issued = await coordinator.ExecuteAsync(issueCommand,
            async (context, _) =>
            {
                var currentPile = await context.Db.IvBalLocs.SingleAsync(p =>
                    p.Id == pile.Id);
                currentPile.StdQty = 1m;
                currentPile.TransDate = issueDate;
                var history = new IvTrxHistory
                {
                    CompanyCode = pile.CompanyCode,
                    BranchCode = pile.BranchCode,
                    BatchNo = 811, TrxLineNo = 1,
                    TrxType = IvTrxTypes.MiscellaneousIssue,
                    BatchStatus = IvBatchStatuses.Posted,
                    ICode = "A100", IStatus = "ACTIVE",
                    FromBalLocId = pile.Id,
                    FrWarehouse = "MAIN", FrLocation = pile.LocCode,
                    FrStdQty = 1m, FrStdUom = "EA"
                };
                context.Db.IvTrxHistories.Add(history);
                new IvInventoryHistoryWriter().StampGeneration(
                    context, [history], 1);
                return 0;
            });
        Assert.True(issued.Succeeded, issued.Error?.Message);
        db.ChangeTracker.Clear();
        var prior = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            pile.CompanyCode, pile.BranchCode, "A100");
        Assert.Equal(1m, prior.CurrentQuantity);
        Assert.Equal(3m, prior.CurrentValue);
        var priorIssue = Assert.Single(prior.ActiveConsumptions);
        var header = new IvStockCountHdr
        {
            CompanyCode = pile.CompanyCode, BranchCode = pile.BranchCode,
            CountNo = $"CC-FIFO-PRIOR-{countedQuantity}",
            CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = pile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE",
            PhysicalQty = countedQuantity, SystemQty = 1m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var basis = new StockCountCostBasis
        {
            CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
            StockCountLineId = line.Id, FrozenFallbackUnitCost = 4m,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = "Verified count cost",
            CostApprovedBy = "manager", CostApprovedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        };
        var physical = await StockReplayPhysicalPlanner
            .BuildCountSheetInsertionAsync(db, header.CompanyCode,
                header.BranchCode, header.Id);
        StockCountCostBasis[] bases = countedQuantity > 0m
            ? [basis] : [];
        var plan = await StockReplayFifoCountSheetPostingPlanner.BuildAsync(
            db, physical, bases);
        Assert.Equal(adjustment, Assert.Single(plan.Items)
            .Projection.Counts[0].SignedAdjustmentQuantity);
        var command = await StockCountAnchorCommandFactory.CreateAsync(db,
            header.CompanyCode, header.BranchCode, header.Id, bases);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var begin = await coordinator.BeginInTransactionAsync(db, command);
            Assert.NotNull(begin.Context);
            var root = begin.Context!;
            var committed = false;
            try
            {
                var prepared = await StockReplayFifoCountSheetPostingPlanner
                    .PrepareWithinRootAsync(root, plan);
                var posted = await StockReplayFifoInitialCountSheetWriter
                    .AppendWithinRootAsync(root, prepared,
                        new RunningNumberService());
                await coordinator.CompleteReplayInTransactionAsync(root,
                    posted.RequiredActiveHistoryIds);
                await transaction.CommitAsync();
                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    await coordinator.AbortInTransactionAsync(root);
                    await transaction.RollbackAsync();
                }
            }
        }
        db.ChangeTracker.Clear();
        var removal = await StockReplayCountSheetRemovalRootWriter.BuildAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        Assert.NotNull(removal.Fifo);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var begin = await coordinator.BeginInTransactionAsync(db,
                removal.Command);
            Assert.NotNull(begin.Context);
            var root = begin.Context!;
            var committed = false;
            try
            {
                var written = await StockReplayCountSheetRemovalRootWriter
                    .AppendWithinRootAsync(root, removal,
                        new RunningNumberService(), "Correct count",
                        DateTime.UtcNow);
                await coordinator.CompleteReplayInTransactionAsync(root,
                    written.RequiredActiveHistoryIds);
                await transaction.CommitAsync();
                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    await coordinator.AbortInTransactionAsync(root);
                    await transaction.RollbackAsync();
                }
            }
        }
        db.ChangeTracker.Clear();
        var restored = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            pile.CompanyCode, pile.BranchCode, "A100");
        Assert.Equal(1m, restored.CurrentQuantity);
        Assert.Equal(3m, restored.CurrentValue);
        Assert.Equal(priorIssue.ConsumptionId,
            Assert.Single(restored.ActiveConsumptions).ConsumptionId);
        Assert.Equal(IvStockCountStatuses.RolledBack,
            (await db.IvStockCountHdrs.SingleAsync()).Status);
    }

    [Fact]
    public async Task Fifo_count_outbound_splits_across_existing_layers()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints=ON;");
        await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
        db.StockCostStates.Remove(await db.StockCostStates.SingleAsync());
        await db.SaveChangesAsync();
        await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
        [
            new FifoLayerSeed(5m, 3m, FixedToday.AddDays(-2)),
            new FifoLayerSeed(5m, 4m, FixedToday.AddDays(-1))
        ]);
        var balance = await db.IvBalLocs.SingleAsync();
        balance.StdQty = 10m;
        var header = new IvStockCountHdr
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            CountNo = "CC-FIFO-SPLIT", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = balance.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", PhysicalQty = 3m,
            SystemQty = 10m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var basis = new StockCountCostBasis
        {
            CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
            StockCountLineId = line.Id, FrozenFallbackUnitCost = 4m,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = "Verified count cost", CostApprovedBy = "manager",
            CostApprovedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow
        };
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        var plan = await StockReplayFifoCountSheetPostingPlanner.BuildAsync(
            db, sheet, [basis]);
        Assert.Equal(3m, Assert.Single(plan.Items).Projection.FinalQuantity);
        Assert.Equal(12m, Assert.Single(plan.Items).Projection.FinalValue);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, header.CompanyCode, header.BranchCode, header.Id, [basis]);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        var committed = false;
        try
        {
            var prepared = await StockReplayFifoCountSheetPostingPlanner
                .PrepareWithinRootAsync(root, plan);
            var posted = await StockReplayFifoInitialCountSheetWriter
                .AppendWithinRootAsync(root, prepared, new RunningNumberService());
            Assert.Equal(2, posted.Facts.Count);
            Assert.Equal([15m, 8m], posted.Facts
                .OrderBy(f => f.SplitOrdinal).Select(f => f.CostAmount));
            Assert.Single(posted.RequiredActiveHistoryIds);
            await coordinator.CompleteReplayInTransactionAsync(root,
                posted.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                await coordinator.AbortInTransactionAsync(root);
                await transaction.RollbackAsync();
            }
        }
        db.ChangeTracker.Clear();
        var final = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, "A100");
        Assert.Equal(3m, final.CurrentQuantity);
        Assert.Equal(12m, final.CurrentValue);
        Assert.Equal(2, final.ActiveConsumptions.Count);
        Assert.Equal(0m, final.Layers[0].RemainingQuantity);
        Assert.Equal(3m, final.Layers[1].RemainingQuantity);
        var removal = await StockReplayCountSheetRemovalRootWriter.BuildAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        Assert.NotNull(removal.Fifo);
        await using var rollback = await db.Database.BeginTransactionAsync();
        var reverse = await coordinator.BeginInTransactionAsync(db,
            removal.Command);
        Assert.NotNull(reverse.Context);
        var rollbackRoot = reverse.Context!;
        var rollbackCommitted = false;
        try
        {
            var written = await StockReplayCountSheetRemovalRootWriter
                .AppendWithinRootAsync(rollbackRoot, removal,
                    new RunningNumberService(), "Correct count", DateTime.UtcNow);
            Assert.Single(written.ReversalHistoryIds);
            await coordinator.CompleteReplayInTransactionAsync(rollbackRoot,
                written.RequiredActiveHistoryIds);
            await rollback.CommitAsync();
            rollbackCommitted = true;
        }
        finally
        {
            if (!rollbackCommitted)
            {
                await coordinator.AbortInTransactionAsync(rollbackRoot);
                await rollback.RollbackAsync();
            }
        }
        db.ChangeTracker.Clear();
        var restored = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, "A100");
        Assert.Equal(10m, restored.CurrentQuantity);
        Assert.Equal(35m, restored.CurrentValue);
        Assert.Empty(restored.ActiveConsumptions);
        Assert.Equal([5m, 5m], restored.Layers
            .Select(l => l.RemainingQuantity).ToArray());
        Assert.Equal(2, await db.StockFifoLayerConsumptions.CountAsync(c =>
            c.ReversesConsumptionId != null));
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 4)]
    public async Task Fifo_mixed_count_sheet_keeps_new_layer_after_old_layer_depletion(
        bool outboundFirst, decimal expectedValue)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
        db.StockCostStates.Remove(await db.StockCostStates.SingleAsync());
        await db.SaveChangesAsync();
        await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [new FifoLayerSeed(2m, 3m, FixedToday.AddDays(-1))]);
        var firstPile = await db.IvBalLocs.SingleAsync();
        firstPile.StdQty = 0m;
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = firstPile.CompanyCode,
            BranchCode = firstPile.BranchCode,
            WarehouseCode = "MAIN", LocCode = "BIN2", IsActive = true
        });
        var secondPile = new IvBalLoc
        {
            CompanyCode = firstPile.CompanyCode,
            BranchCode = firstPile.BranchCode,
            ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", StdQty = 2m,
            StdUom = "EA", TransDate = FixedToday
        };
        db.IvBalLocs.Add(secondPile);
        await db.SaveChangesAsync();
        var header = new IvStockCountHdr
        {
            CompanyCode = firstPile.CompanyCode,
            BranchCode = firstPile.BranchCode,
            CountNo = "CC-FIFO-MIXED", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var first = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = firstPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", PhysicalQty = 1m,
            SystemQty = 0m
        };
        var outbound = new IvStockCountLine
        {
            Header = header, LineNumber = 2, BalLocId = secondPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN2",
            LotNo = "", IStatus = "ACTIVE", PhysicalQty = 0m,
            SystemQty = 2m
        };
        if (outboundFirst)
            db.IvStockCountLines.AddRange(outbound, first);
        else
            db.IvStockCountLines.AddRange(first, outbound);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var basis = new StockCountCostBasis
        {
            CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
            StockCountLineId = first.Id, FrozenFallbackUnitCost = 4m,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = "Verified count cost", CostApprovedBy = "manager",
            CostApprovedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow
        };
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        var plan = await StockReplayFifoCountSheetPostingPlanner.BuildAsync(
            db, sheet, [basis]);
        Assert.Equal(1m, Assert.Single(plan.Items).Projection.FinalQuantity);
        Assert.Equal(expectedValue, Assert.Single(plan.Items).Projection.FinalValue);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, header.CompanyCode, header.BranchCode, header.Id, [basis]);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        var committed = false;
        try
        {
            var prepared = await StockReplayFifoCountSheetPostingPlanner
                .PrepareWithinRootAsync(root, plan);
            var posted = await StockReplayFifoInitialCountSheetWriter
                .AppendWithinRootAsync(root, prepared, new RunningNumberService());
            Assert.Equal(2, posted.Facts.Count);
            Assert.Equal(1, posted.Facts.Count(f => f.Direction == 1));
            Assert.Equal(1, posted.Facts.Count(f => f.Direction == -1));
            await coordinator.CompleteReplayInTransactionAsync(root,
                posted.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                await coordinator.AbortInTransactionAsync(root);
                await transaction.RollbackAsync();
            }
        }
        db.ChangeTracker.Clear();
        var final = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, "A100");
        Assert.Equal(1m, final.CurrentQuantity);
        Assert.Equal(expectedValue, final.CurrentValue);
        Assert.Equal(2, final.Layers.Count);
        Assert.Equal(0m, final.Layers[0].RemainingQuantity);
        Assert.Equal(1m, final.Layers[1].RemainingQuantity);
        Assert.Single(final.ActiveConsumptions);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    public async Task Fifo_count_preserves_later_mr_receipt_with_proven_pile_timeline(
        bool changedDestination, bool receiptOnCountedPile,
        decimal physicalCount)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(
            "PRAGMA ignore_check_constraints=ON;");
        await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
        db.StockCostStates.Remove(await db.StockCostStates.SingleAsync());
        await db.SaveChangesAsync();
        await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [new FifoLayerSeed(2m, 3m, FixedToday.AddDays(1))]);
        var countedPile = await db.IvBalLocs.SingleAsync();
        countedPile.StdQty = receiptOnCountedPile ? 2m : 0m;
        IvBalLoc receiptPile;
        if (receiptOnCountedPile)
            receiptPile = countedPile;
        else
        {
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = countedPile.CompanyCode,
                BranchCode = countedPile.BranchCode,
                WarehouseCode = "MAIN", LocCode = "BIN2", IsActive = true
            });
            receiptPile = new IvBalLoc
            {
                CompanyCode = countedPile.CompanyCode,
                BranchCode = countedPile.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = "", IStatus = "ACTIVE", StdQty = 2m,
                StdUom = "EA", TransDate = FixedToday.AddDays(1)
            };
            db.IvBalLocs.Add(receiptPile);
        }
        await db.SaveChangesAsync();
        var receiptPosting = await db.StockPostings.SingleAsync();
        var receiptFact = await db.StockValuationFacts.SingleAsync();
        var receiptHistory = new IvTrxHistory
        {
            CompanyCode = countedPile.CompanyCode,
            BranchCode = countedPile.BranchCode,
            BatchNo = 700, TrxLineNo = 1,
            TrxDtTime = FixedToday.AddDays(1),
            TrxType = IvTrxTypes.MiscellaneousReceipt,
            BatchStatus = IvBatchStatuses.Posted,
            ICode = "A100", ToBalLocId = receiptPile.Id,
            ToWarehouse = "MAIN", ToLocation = receiptPile.LocCode,
            ToStdQty = 2m, ToStdUom = "EA",
            ValuationStatus = StockValuationStatuses.Valued,
            LedgerVersion = 2, LedgerEpochId = receiptPosting.LedgerEpochId,
            StockPostingId = receiptPosting.Id,
            PostingLineNo = receiptFact.PostingLineNo,
            EntryRole = "ORIGINAL", DocumentRevision = 1
        };
        db.IvTrxHistories.Add(receiptHistory);
        await db.SaveChangesAsync();
        // Turn the opening fixture into a posted MR receipt without mutating
        // append-only evidence through EF's guarded SaveChanges path.
        await db.StockPostings.Where(p => p.Id == receiptPosting.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.SourceDocumentType,
                    IvTrxTypes.MiscellaneousReceipt)
                .SetProperty(p => p.SourceDocumentId, "700")
                .SetProperty(p => p.SourceDocumentNo, "700"));
        await db.StockValuationFacts.Where(f => f.Id == receiptFact.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.SourceDocumentType,
                    IvTrxTypes.MiscellaneousReceipt)
                .SetProperty(f => f.SourceDocumentId, "700")
                .SetProperty(f => f.SourceDocumentNo, "700")
                .SetProperty(f => f.InventoryHistoryId, receiptHistory.Id));
        db.ChangeTracker.Clear();
        var header = new IvStockCountHdr
        {
            CompanyCode = countedPile.CompanyCode,
            BranchCode = countedPile.BranchCode,
            CountNo = "CC-FIFO-LATER-MR", CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = countedPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", PhysicalQty = physicalCount,
            SystemQty = 0m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        StockCountCostBasis[] bases = physicalCount == 0m ? [] : [new()
        {
            CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
            StockCountLineId = line.Id, FrozenFallbackUnitCost = 4m,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = "Verified count cost", CostApprovedBy = "manager",
            CostApprovedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow
        }];
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        var plan = await StockReplayFifoCountSheetPostingPlanner.BuildAsync(
            db, sheet, bases);
        var item = Assert.Single(plan.Items);
        Assert.Single(item.Projection.BeforeFirstCount.LaterReceipts);
        Assert.Equal(2m + physicalCount, item.Projection.FinalQuantity);
        Assert.Equal(6m + 4m * physicalCount,
            item.Projection.FinalValue);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, header.CompanyCode, header.BranchCode, header.Id, bases);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        var committed = false;
        try
        {
            var prepared = await StockReplayFifoCountSheetPostingPlanner
                .PrepareWithinRootAsync(root, plan);
            if (changedDestination)
            {
                await db.IvTrxHistories.Where(h => h.Id == receiptHistory.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(h =>
                        h.ToBalLocId, countedPile.Id));
                Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                    (await Assert.ThrowsAsync<StockLedgerException>(() =>
                        StockReplayFifoInitialCountSheetWriter
                            .AppendWithinRootAsync(root, prepared,
                                new RunningNumberService()))).Error.Code);
                Assert.Equal(0m, (await db.IvBalLocs.SingleAsync(p =>
                    p.Id == countedPile.Id)).StdQty);
                Assert.Single(await db.StockValuationFacts.ToArrayAsync());
                return;
            }
            var posted = await StockReplayFifoInitialCountSheetWriter
                .AppendWithinRootAsync(root, prepared, new RunningNumberService());
            if (physicalCount == 0m)
            {
                Assert.Empty(posted.Facts);
                Assert.Empty(posted.RequiredActiveHistoryIds);
                Assert.Null(Assert.Single(posted.History.Generations)
                    .AdjustmentHistoryId);
            }
            else
            {
                Assert.Equal(4m, Assert.Single(posted.Facts).CostAmount);
                Assert.Equal(StockValuationSources.ManualApproved,
                    Assert.Single(posted.Facts).ValuationSource);
            }
            await coordinator.CompleteReplayInTransactionAsync(root,
                posted.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                await coordinator.AbortInTransactionAsync(root);
                await transaction.RollbackAsync();
            }
        }
        db.ChangeTracker.Clear();
        var final = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, "A100");
        Assert.Equal(2m + physicalCount, final.CurrentQuantity);
        Assert.Equal(6m + 4m * physicalCount, final.CurrentValue);
        Assert.Equal(2m, final.Layers.Single(l =>
            l.OriginFactId == receiptFact.Id).RemainingQuantity);
        Assert.Equal(6m, final.Layers.Single(l =>
            l.OriginFactId == receiptFact.Id).RemainingValue);
        Assert.Equal(receiptOnCountedPile
            ? 2m + physicalCount : physicalCount,
            (await db.IvBalLocs.SingleAsync(p =>
            p.Id == countedPile.Id)).StdQty);
        Assert.Equal(receiptOnCountedPile
            ? 2m + physicalCount : 2m,
            (await db.IvBalLocs.SingleAsync(p =>
            p.Id == receiptPile.Id)).StdQty);
        if (receiptOnCountedPile)
            Assert.Equal(FixedToday.AddDays(1),
                (await db.IvBalLocs.SingleAsync(p =>
                    p.Id == countedPile.Id)).TransDate);
        if (physicalCount > 0m)
        {
            var removal = await StockReplayCountSheetRemovalRootWriter
                .BuildAsync(db, header.CompanyCode, header.BranchCode,
                    header.Id);
            Assert.NotNull(removal.Fifo);
            await using var rollback = await db.Database.BeginTransactionAsync();
            var reverse = await coordinator.BeginInTransactionAsync(db,
                removal.Command);
            Assert.NotNull(reverse.Context);
            var reversalRoot = reverse.Context!;
            var rollbackCommitted = false;
            try
            {
                var written = await StockReplayCountSheetRemovalRootWriter
                    .AppendWithinRootAsync(reversalRoot, removal,
                        new RunningNumberService(), "Correct count",
                        DateTime.UtcNow);
                Assert.Single(written.ReversalHistoryIds);
                await coordinator.CompleteReplayInTransactionAsync(
                    reversalRoot, written.RequiredActiveHistoryIds);
                await rollback.CommitAsync();
                rollbackCommitted = true;
            }
            finally
            {
                if (!rollbackCommitted)
                {
                    await coordinator.AbortInTransactionAsync(reversalRoot);
                    await rollback.RollbackAsync();
                }
            }
            db.ChangeTracker.Clear();
            var restored = await StockReplayFifoActiveLayerReader.ReadAsync(db,
                header.CompanyCode, header.BranchCode, "A100");
            Assert.Equal(2m, restored.CurrentQuantity);
            Assert.Equal(6m, restored.CurrentValue);
            Assert.Equal(receiptFact.Id,
                Assert.Single(restored.Layers).OriginFactId);
            Assert.Equal(receiptOnCountedPile ? 2m : 0m,
                (await db.IvBalLocs.SingleAsync(p =>
                    p.Id == countedPile.Id)).StdQty);
            Assert.Equal(2m, (await db.IvBalLocs.SingleAsync(p =>
                p.Id == receiptPile.Id)).StdQty);
            if (receiptOnCountedPile)
                Assert.Equal(FixedToday.AddDays(1),
                    (await db.IvBalLocs.SingleAsync(p =>
                        p.Id == countedPile.Id)).TransDate);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Fifo_negative_count_removal_preserves_later_mr(
        bool receiptOnCountedPile, bool laterIssue)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(
            "PRAGMA ignore_check_constraints=ON;");
        await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
        db.StockCostStates.Remove(await db.StockCostStates.SingleAsync());
        await db.SaveChangesAsync();
        await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [new FifoLayerSeed(2m, 3m, FixedToday.AddDays(-1))]);
        var countedPile = await db.IvBalLocs.SingleAsync();
        countedPile.StdQty = 2m;
        IvBalLoc receiptPile;
        if (receiptOnCountedPile)
            receiptPile = countedPile;
        else
        {
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = countedPile.CompanyCode,
                BranchCode = countedPile.BranchCode,
                WarehouseCode = "MAIN", LocCode = "BIN2", IsActive = true
            });
            receiptPile = new IvBalLoc
            {
                CompanyCode = countedPile.CompanyCode,
                BranchCode = countedPile.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = "", IStatus = "ACTIVE", StdQty = 0m,
                StdUom = "EA"
            };
            db.IvBalLocs.Add(receiptPile);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var receiptAt = FixedToday.AddDays(1);
        var receiptCommand = new StockPostingCommand
        {
            RequestId = Guid.NewGuid(),
            CommandType = "MR_AFTER_NEGATIVE_COUNT_TEST",
            SourceModule = "INVENTORY",
            SourceDocumentType = IvTrxTypes.MiscellaneousReceipt,
            SourceDocumentId = "706", SourceDocumentNo = "706",
            DocumentRevision = 1, PostingRole = "PRIMARY",
            EffectiveAt = receiptAt,
            Evidence = StockPostingFingerprint.Create(
                new { BatchNo = 706, Item = "A100", Quantity = 2m },
                new { EffectiveAt = receiptAt })
        };
        var receiptResult = await coordinator.ExecuteAsync(receiptCommand,
            async (context, _) =>
            {
                var pile = await context.Db.IvBalLocs.SingleAsync(p =>
                    p.Id == receiptPile.Id);
                pile.StdQty += 2m;
                pile.TransDate = receiptAt;
                var history = new IvTrxHistory
                {
                    CompanyCode = countedPile.CompanyCode,
                    BranchCode = countedPile.BranchCode,
                    BatchNo = 706, TrxLineNo = 1,
                    TrxType = IvTrxTypes.MiscellaneousReceipt,
                    BatchStatus = IvBatchStatuses.Posted,
                    ICode = "A100", IStatus = "ACTIVE",
                    ToBalLocId = receiptPile.Id,
                    ToWarehouse = "MAIN", ToLocation = receiptPile.LocCode,
                    ToStdQty = 2m, ToStdUom = "EA",
                    UnitPrice = 3m,
                    PriceEvidence = InventoryCostEvidenceTypes.ManualApproved
                };
                context.Db.IvTrxHistories.Add(history);
                new IvInventoryHistoryWriter().StampGeneration(
                    context, [history], 1);
                return 0;
            });
        Assert.True(receiptResult.Succeeded, receiptResult.Error?.Message);
        db.ChangeTracker.Clear();
        var before = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            countedPile.CompanyCode, countedPile.BranchCode, "A100");
        Assert.Equal(4m, before.CurrentQuantity);
        Assert.Equal(12m, before.CurrentValue);
        var laterReceipt = Assert.Single(before.Layers.Where(l =>
            l.ReceiptKey?.EffectiveAt == receiptAt));
        StockReplayFifoConsumptionPosition? originalIssue = null;
        var issueAt = FixedToday.AddDays(2);
        if (laterIssue)
        {
            var issueCommand = new StockPostingCommand
            {
                RequestId = Guid.NewGuid(),
                CommandType = "MI_AFTER_NEGATIVE_COUNT_MR_TEST",
                SourceModule = "INVENTORY",
                SourceDocumentType = IvTrxTypes.MiscellaneousIssue,
                SourceDocumentId = "707", SourceDocumentNo = "707",
                DocumentRevision = 1, PostingRole = "PRIMARY",
                EffectiveAt = issueAt,
                Evidence = StockPostingFingerprint.Create(
                    new { BatchNo = 707, Item = "A100", Quantity = 1m },
                    new { EffectiveAt = issueAt })
            };
            var issueResult = await coordinator.ExecuteAsync(issueCommand,
                async (context, _) =>
                {
                    var pile = await context.Db.IvBalLocs.SingleAsync(p =>
                        p.Id == countedPile.Id);
                    pile.StdQty -= 1m;
                    pile.TransDate = issueAt;
                    var history = new IvTrxHistory
                    {
                        CompanyCode = countedPile.CompanyCode,
                        BranchCode = countedPile.BranchCode,
                        BatchNo = 707, TrxLineNo = 1,
                        TrxType = IvTrxTypes.MiscellaneousIssue,
                        BatchStatus = IvBatchStatuses.Posted,
                        ICode = "A100", IStatus = "ACTIVE",
                        FromBalLocId = countedPile.Id,
                        FrWarehouse = "MAIN", FrLocation = "BIN1",
                        FrStdQty = 1m, FrStdUom = "EA"
                    };
                    context.Db.IvTrxHistories.Add(history);
                    new IvInventoryHistoryWriter().StampGeneration(
                        context, [history], 1);
                    return 0;
                });
            Assert.True(issueResult.Succeeded, issueResult.Error?.Message);
            db.ChangeTracker.Clear();
            originalIssue = Assert.Single((await
                StockReplayFifoActiveLayerReader.ReadAsync(db,
                    countedPile.CompanyCode, countedPile.BranchCode,
                    "A100")).ActiveConsumptions);
            Assert.Equal(3m, originalIssue.Value);
        }
        var header = new IvStockCountHdr
        {
            CompanyCode = countedPile.CompanyCode,
            BranchCode = countedPile.BranchCode,
            CountNo = "CC-FIFO-NEGATIVE-LATER-MR",
            CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = countedPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", PhysicalQty = 1m,
            SystemQty = 2m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var basis = new StockCountCostBasis
        {
            CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
            StockCountLineId = line.Id, FrozenFallbackUnitCost = 3m,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = "Verified count cost", CostApprovedBy = "manager",
            CostApprovedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow
        };
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        var plan = await StockReplayFifoCountSheetPostingPlanner.BuildAsync(
            db, sheet, [basis]);
        Assert.Equal(-1m, Assert.Single(Assert.Single(plan.Items)
            .Projection.Counts).SignedAdjustmentQuantity);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, header.CompanyCode, header.BranchCode, header.Id, [basis]);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var begin = await coordinator.BeginInTransactionAsync(db, command);
            Assert.NotNull(begin.Context);
            var root = begin.Context!;
            var committed = false;
            try
            {
                var prepared = await StockReplayFifoCountSheetPostingPlanner
                    .PrepareWithinRootAsync(root, plan);
                var posted = await StockReplayFifoInitialCountSheetWriter
                    .AppendWithinRootAsync(root, prepared,
                        new RunningNumberService());
                Assert.Equal(3m, Assert.Single(posted.Facts).CostAmount);
                await coordinator.CompleteReplayInTransactionAsync(root,
                    posted.RequiredActiveHistoryIds);
                await transaction.CommitAsync();
                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    await coordinator.AbortInTransactionAsync(root);
                    await transaction.RollbackAsync();
                }
            }
        }
        db.ChangeTracker.Clear();
        var counted = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, "A100");
        Assert.Equal(laterIssue ? 2m : 3m, counted.CurrentQuantity);
        Assert.Equal(laterIssue ? 6m : 9m, counted.CurrentValue);
        var removal = await StockReplayCountSheetRemovalRootWriter.BuildAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        Assert.NotNull(removal.Fifo);
        Assert.Same(Assert.Single(removal.Fifo.Items),
            StockReplayFifoCountSheetRemovalPlanner
                .RequireIsolatedAdjustment(removal.Fifo));
        await using (var rollback = await db.Database.BeginTransactionAsync())
        {
            var reverse = await coordinator.BeginInTransactionAsync(db,
                removal.Command);
            Assert.NotNull(reverse.Context);
            var root = reverse.Context!;
            var committed = false;
            try
            {
                var written = await StockReplayCountSheetRemovalRootWriter
                    .AppendWithinRootAsync(root, removal,
                        new RunningNumberService(), "Correct count",
                        DateTime.UtcNow);
                Assert.Single(written.ReversalHistoryIds);
                await coordinator.CompleteReplayInTransactionAsync(root,
                    written.RequiredActiveHistoryIds);
                await rollback.CommitAsync();
                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    await coordinator.AbortInTransactionAsync(root);
                    await rollback.RollbackAsync();
                }
            }
        }
        db.ChangeTracker.Clear();
        var restored = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, "A100");
        Assert.Equal(laterIssue ? 3m : 4m, restored.CurrentQuantity);
        Assert.Equal(laterIssue ? 9m : 12m, restored.CurrentValue);
        Assert.Equal(laterReceipt, restored.Layers.Single(l =>
            l.LayerId == laterReceipt.LayerId));
        if (laterIssue)
            Assert.Equal(originalIssue,
                Assert.Single(restored.ActiveConsumptions));
        else
            Assert.Empty(restored.ActiveConsumptions);
        Assert.Equal(receiptOnCountedPile
                ? laterIssue ? 3m : 4m
                : laterIssue ? 1m : 2m,
            (await db.IvBalLocs.SingleAsync(p =>
                p.Id == countedPile.Id)).StdQty);
        Assert.Equal(receiptOnCountedPile
                ? laterIssue ? 3m : 4m : 2m,
            (await db.IvBalLocs.SingleAsync(p =>
                p.Id == receiptPile.Id)).StdQty);
        if (receiptOnCountedPile || laterIssue)
            Assert.Equal(laterIssue ? issueAt : receiptAt,
                (await db.IvBalLocs.SingleAsync(p =>
                p.Id == countedPile.Id)).TransDate);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task Fifo_count_keeps_unchanged_later_mi_allocation(
        bool issueOnCountedPile, bool changedSource, bool negativeCount)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(
            "PRAGMA ignore_check_constraints=ON");
        await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
        db.StockCostStates.Remove(await db.StockCostStates.SingleAsync());
        await db.SaveChangesAsync();
        await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [new FifoLayerSeed(5m, 3m, FixedToday.AddDays(-2))]);
        var countedPile = await db.IvBalLocs.SingleAsync();
        countedPile.StdQty = issueOnCountedPile ? 5m
            : negativeCount ? 1m : 0m;
        IvBalLoc issuePile;
        if (issueOnCountedPile)
            issuePile = countedPile;
        else
        {
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = countedPile.CompanyCode,
                BranchCode = countedPile.BranchCode,
                WarehouseCode = "MAIN", LocCode = "BIN2", IsActive = true
            });
            issuePile = new IvBalLoc
            {
                CompanyCode = countedPile.CompanyCode,
                BranchCode = countedPile.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = "", IStatus = "ACTIVE",
                StdQty = negativeCount ? 4m : 5m,
                StdUom = "EA", TransDate = FixedToday.AddDays(-2)
            };
            db.IvBalLocs.Add(issuePile);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var issueCommand = new StockPostingCommand
        {
            RequestId = Guid.NewGuid(),
            CommandType = "MI_COUNT_TAIL_TEST",
            SourceModule = "INVENTORY",
            SourceDocumentType = IvTrxTypes.MiscellaneousIssue,
            SourceDocumentId = "701", SourceDocumentNo = "701",
            DocumentRevision = 1, PostingRole = "PRIMARY",
            EffectiveAt = FixedToday,
            Evidence = StockPostingFingerprint.Create(
                new { BatchNo = 701, Item = "A100", Quantity = 1m },
                new { EffectiveAt = FixedToday })
        };
        var issueResult = await coordinator.ExecuteAsync(issueCommand,
            async (context, _) =>
            {
                var pile = await context.Db.IvBalLocs.SingleAsync(p =>
                    p.Id == issuePile.Id);
                pile.StdQty = negativeCount && !issueOnCountedPile
                    ? 3m : 4m;
                pile.TransDate = FixedToday;
                var history = new IvTrxHistory
                {
                    CompanyCode = countedPile.CompanyCode,
                    BranchCode = countedPile.BranchCode,
                    BatchNo = 701, TrxLineNo = 1,
                    TrxType = IvTrxTypes.MiscellaneousIssue,
                    BatchStatus = IvBatchStatuses.Posted,
                    ICode = "A100", IStatus = "ACTIVE",
                    FromBalLocId = issuePile.Id,
                    FrWarehouse = "MAIN", FrLocation = issuePile.LocCode,
                    FrStdQty = 1m, FrStdUom = "EA"
                };
                context.Db.IvTrxHistories.Add(history);
                new IvInventoryHistoryWriter().StampGeneration(
                    context, [history], 1);
                return 0;
            });
        Assert.True(issueResult.Succeeded, issueResult.Error?.Message);
        db.ChangeTracker.Clear();
        var before = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            countedPile.CompanyCode, countedPile.BranchCode, "A100");
        Assert.Equal(4m, before.CurrentQuantity);
        Assert.Equal(12m, before.CurrentValue);
        var oldIssue = Assert.Single(before.ActiveConsumptions);
        var issueHistoryId = (await db.StockValuationFacts.AsNoTracking()
            .SingleAsync(f => f.Id == oldIssue.IssueFactId))
            .InventoryHistoryId!.Value;
        var header = new IvStockCountHdr
        {
            CompanyCode = countedPile.CompanyCode,
            BranchCode = countedPile.BranchCode,
            CountNo = "CC-FIFO-LATER-MI",
            CountDate = FixedToday.AddDays(-1),
            Status = IvStockCountStatuses.Counted
        };
        var line = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = countedPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE",
            PhysicalQty = negativeCount
                ? issueOnCountedPile ? 4m : 0m
                : issueOnCountedPile ? 6m : 1m,
            SystemQty = issueOnCountedPile ? 5m
                : negativeCount ? 1m : 0m
        };
        db.IvStockCountLines.Add(line);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var basis = new StockCountCostBasis
        {
            CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
            StockCountLineId = line.Id, FrozenFallbackUnitCost = 4m,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = "Verified count cost", CostApprovedBy = "manager",
            CostApprovedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow
        };
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        var plan = await StockReplayFifoCountSheetPostingPlanner.BuildAsync(
            db, sheet, line.PhysicalQty > 0m ? [basis] : []);
        var projected = Assert.Single(plan.Items).Projection;
        var later = Assert.Single(projected.LaterIssues);
        Assert.Equal(3m, later.Value);
        Assert.Equal(oldIssue.LayerId,
            Assert.Single(later.Allocations).LayerId);
        Assert.Equal(negativeCount ? 3m : 5m,
            projected.FinalQuantity);
        Assert.Equal(negativeCount ? 9m : 15m,
            projected.FinalValue);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, header.CompanyCode, header.BranchCode, header.Id,
            line.PhysicalQty > 0m ? [basis] : []);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        var committed = false;
        try
        {
            var prepared = await StockReplayFifoCountSheetPostingPlanner
                .PrepareWithinRootAsync(root, plan);
            if (changedSource)
            {
                await db.IvTrxHistories.Where(h => h.Id == issueHistoryId)
                    .ExecuteUpdateAsync(s => s.SetProperty(h =>
                        h.FromBalLocId, countedPile.Id));
                Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
                    (await Assert.ThrowsAsync<StockLedgerException>(() =>
                        StockReplayFifoInitialCountSheetWriter
                            .AppendWithinRootAsync(root, prepared,
                                new RunningNumberService()))).Error.Code);
                Assert.Equal(0m, (await db.IvBalLocs.SingleAsync(p =>
                    p.Id == countedPile.Id)).StdQty);
                Assert.Empty(await db.StockCountReplayGenerations.ToArrayAsync());
                return;
            }
            var posted = await StockReplayFifoInitialCountSheetWriter
                .AppendWithinRootAsync(root, prepared, new RunningNumberService());
            Assert.Equal(3m, Assert.Single(posted.Facts).CostAmount);
            Assert.Equal(StockValuationSources.Fifo,
                Assert.Single(posted.Facts).ValuationSource);
            await coordinator.CompleteReplayInTransactionAsync(root,
                posted.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                await coordinator.AbortInTransactionAsync(root);
                await transaction.RollbackAsync();
            }
        }
        db.ChangeTracker.Clear();
        var final = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, "A100");
        Assert.Equal(negativeCount ? 3m : 5m, final.CurrentQuantity);
        Assert.Equal(negativeCount ? 9m : 15m, final.CurrentValue);
        Assert.Contains(oldIssue, final.ActiveConsumptions);
        Assert.Equal(negativeCount ? 2 : 1,
            final.ActiveConsumptions.Count);
        Assert.Equal(issueOnCountedPile
                ? negativeCount ? 3m : 5m
                : negativeCount ? 0m : 1m,
            (await db.IvBalLocs.SingleAsync(p =>
            p.Id == countedPile.Id)).StdQty);
        Assert.Equal(issueOnCountedPile
                ? negativeCount ? 3m : 5m
                : negativeCount ? 3m : 4m,
            (await db.IvBalLocs.SingleAsync(p =>
            p.Id == issuePile.Id)).StdQty);
        if (issueOnCountedPile)
            Assert.Equal(FixedToday, (await db.IvBalLocs.SingleAsync(p =>
                p.Id == countedPile.Id)).TransDate);
        var removal = await StockReplayCountSheetRemovalRootWriter.BuildAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        Assert.NotNull(removal.Fifo);
        await using var rollback = await db.Database.BeginTransactionAsync();
        var reverse = await coordinator.BeginInTransactionAsync(db,
            removal.Command);
        Assert.NotNull(reverse.Context);
        var reversalRoot = reverse.Context!;
        var rollbackCommitted = false;
        try
        {
            var written = await StockReplayCountSheetRemovalRootWriter
                .AppendWithinRootAsync(reversalRoot, removal,
                    new RunningNumberService(), "Correct count", DateTime.UtcNow);
            Assert.Single(written.ReversalHistoryIds);
            await coordinator.CompleteReplayInTransactionAsync(reversalRoot,
                written.RequiredActiveHistoryIds);
            await rollback.CommitAsync();
            rollbackCommitted = true;
        }
        finally
        {
            if (!rollbackCommitted)
            {
                await coordinator.AbortInTransactionAsync(reversalRoot);
                await rollback.RollbackAsync();
            }
        }
        db.ChangeTracker.Clear();
        var restored = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, "A100");
        Assert.Equal(4m, restored.CurrentQuantity);
        Assert.Equal(12m, restored.CurrentValue);
        Assert.Equal(oldIssue, Assert.Single(restored.ActiveConsumptions));
        Assert.Equal(issueOnCountedPile ? 4m
                : negativeCount ? 1m : 0m,
            (await db.IvBalLocs.SingleAsync(p =>
                p.Id == countedPile.Id)).StdQty);
        Assert.Equal(negativeCount && !issueOnCountedPile ? 3m : 4m,
            (await db.IvBalLocs.SingleAsync(p =>
                p.Id == issuePile.Id)).StdQty);
        if (issueOnCountedPile)
            Assert.Equal(FixedToday, (await db.IvBalLocs.SingleAsync(p =>
                p.Id == countedPile.Id)).TransDate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fifo_count_reprices_later_mi_to_the_new_fifo_layer(
        bool issueOnCountedPile)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
        db.StockCostStates.Remove(await db.StockCostStates.SingleAsync());
        await db.SaveChangesAsync();
        await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
        [
            new FifoLayerSeed(1m, 3m, FixedToday.AddDays(-3)),
            new FifoLayerSeed(1m, 4m, FixedToday.AddDays(-2))
        ]);
        var countedPile = await db.IvBalLocs.SingleAsync();
        countedPile.StdQty = issueOnCountedPile ? 2m : 1m;
        IvBalLoc issuePile;
        if (issueOnCountedPile)
            issuePile = countedPile;
        else
        {
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = countedPile.CompanyCode,
                BranchCode = countedPile.BranchCode,
                WarehouseCode = "MAIN", LocCode = "BIN2", IsActive = true
            });
            issuePile = new IvBalLoc
            {
                CompanyCode = countedPile.CompanyCode,
                BranchCode = countedPile.BranchCode,
                ICode = "A100", WhCode = "MAIN", LocCode = "BIN2",
                LotNo = "", IStatus = "ACTIVE", StdQty = 1m,
                StdUom = "EA", TransDate = FixedToday.AddDays(-2)
            };
            db.IvBalLocs.Add(issuePile);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        var issueCommand = new StockPostingCommand
        {
            RequestId = Guid.NewGuid(),
            CommandType = "MI_COUNT_REPRICE_TEST",
            SourceModule = "INVENTORY",
            SourceDocumentType = IvTrxTypes.MiscellaneousIssue,
            SourceDocumentId = "702", SourceDocumentNo = "702",
            DocumentRevision = 1, PostingRole = "PRIMARY",
            EffectiveAt = FixedToday,
            Evidence = StockPostingFingerprint.Create(
                new { BatchNo = 702, Item = "A100", Quantity = 1m },
                new { EffectiveAt = FixedToday })
        };
        var issueResult = await coordinator.ExecuteAsync(issueCommand,
            async (context, _) =>
            {
                var pile = await context.Db.IvBalLocs.SingleAsync(p =>
                    p.Id == issuePile.Id);
                pile.StdQty = issueOnCountedPile ? 1m : 0m;
                pile.TransDate = FixedToday;
                var history = new IvTrxHistory
                {
                    CompanyCode = countedPile.CompanyCode,
                    BranchCode = countedPile.BranchCode,
                    BatchNo = 702, TrxLineNo = 1,
                    TrxType = IvTrxTypes.MiscellaneousIssue,
                    BatchStatus = IvBatchStatuses.Posted,
                    ICode = "A100", IStatus = "ACTIVE",
                    FromBalLocId = issuePile.Id,
                    FrWarehouse = "MAIN", FrLocation = "BIN2",
                    FrStdQty = 1m, FrStdUom = "EA"
                };
                context.Db.IvTrxHistories.Add(history);
                new IvInventoryHistoryWriter().StampGeneration(
                    context, [history], 1);
                return 0;
            });
        Assert.True(issueResult.Succeeded, issueResult.Error?.Message);
        db.ChangeTracker.Clear();
        var before = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            countedPile.CompanyCode, countedPile.BranchCode, "A100");
        Assert.Equal(1m, before.CurrentQuantity);
        Assert.Equal(4m, before.CurrentValue);
        Assert.Equal(3m, Assert.Single(before.ActiveConsumptions).Value);
        var header = new IvStockCountHdr
        {
            CompanyCode = countedPile.CompanyCode,
            BranchCode = countedPile.BranchCode,
            CountNo = "CC-FIFO-REPRICE-MI",
            CountDate = FixedToday.AddDays(-1),
            Status = IvStockCountStatuses.Counted
        };
        var countLine = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = countedPile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE",
            PhysicalQty = issueOnCountedPile ? 1m : 0m,
            SystemQty = issueOnCountedPile ? 2m : 1m
        };
        db.IvStockCountLines.Add(countLine);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        StockCountCostBasis[] bases = issueOnCountedPile ? [new()
        {
            CompanyCode = header.CompanyCode, BranchCode = header.BranchCode,
            StockCountLineId = countLine.Id, FrozenFallbackUnitCost = 3m,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = "Verified count cost", CostApprovedBy = "manager",
            CostApprovedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow
        }] : [];
        var sheet = await StockReplayPhysicalPlanner.BuildCountSheetInsertionAsync(
            db, header.CompanyCode, header.BranchCode, header.Id);
        var plan = await StockReplayFifoCountSheetPostingPlanner.BuildAsync(
            db, sheet, bases);
        var later = Assert.Single(Assert.Single(plan.Items).Projection.LaterIssues);
        Assert.Equal(4m, later.Value);
        Assert.NotEqual(Assert.Single(later.PreviousConsumptions).LayerId,
            Assert.Single(later.Allocations).LayerId);
        var command = await StockCountAnchorCommandFactory.CreateAsync(
            db, header.CompanyCode, header.BranchCode, header.Id, bases);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var begin = await coordinator.BeginInTransactionAsync(db, command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        var committed = false;
        try
        {
            var prepared = await StockReplayFifoCountSheetPostingPlanner
                .PrepareWithinRootAsync(root, plan);
            var posted = await StockReplayFifoInitialCountSheetWriter
                .AppendWithinRootAsync(root, prepared,
                    new RunningNumberService());
            Assert.Equal(3, posted.Facts.Count);
            Assert.Equal(2, posted.RequiredActiveHistoryIds.Count);
            Assert.Equal(1m, posted.Facts.Sum(f => f.SignedQty) * -1m);
            Assert.Equal(-4m, posted.Facts.Sum(f => f.SignedValue));
            await coordinator.CompleteReplayInTransactionAsync(root,
                posted.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                await coordinator.AbortInTransactionAsync(root);
                await transaction.RollbackAsync();
            }
        }
        db.ChangeTracker.Clear();
        var final = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            header.CompanyCode, header.BranchCode, "A100");
        Assert.Equal(0m, final.CurrentQuantity);
        Assert.Equal(0m, final.CurrentValue);
        var correctedIssue = Assert.Single(final.ActiveConsumptions
            .Where(c => c.IssueKey == later.Key));
        Assert.Equal(4m, correctedIssue.Value);
        Assert.NotEqual(Assert.Single(before.ActiveConsumptions).IssueFactId,
            correctedIssue.IssueFactId);
        var removal = await StockReplayFifoCountSheetRemovalPlanner
            .BuildAsync(db, header.CompanyCode, header.BranchCode, header.Id);
        var projected = Assert.Single(removal.Items).Projection!;
        Assert.Equal(1m, projected.FinalQuantity);
        Assert.Equal(4m, projected.FinalValue);
        Assert.Equal(3m, Assert.Single(projected.LaterIssues).Value);
        if (!issueOnCountedPile)
        {
            await using var rollback = await db.Database.BeginTransactionAsync();
            var reverse = await coordinator.BeginInTransactionAsync(db,
                removal.Command);
            Assert.NotNull(reverse.Context);
            var rollbackRoot = reverse.Context!;
            try
            {
                await StockReplayFifoCountSheetRemovalPlanner
                    .ValidateWithinRootAsync(rollbackRoot, removal);
                await db.IvTrxHistories.Where(h =>
                        h.TrxType == IvTrxTypes.MiscellaneousIssue)
                    .ExecuteUpdateAsync(s => s.SetProperty(h =>
                        h.FromBalLocId, countedPile.Id));
                var stale = await Assert.ThrowsAsync<StockLedgerException>(
                    () => StockReplayFifoCountSheetRemovalPlanner
                        .ValidateWithinRootAsync(rollbackRoot, removal));
                Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                    stale.Error.Code);
            }
            finally
            {
                await coordinator.AbortInTransactionAsync(rollbackRoot);
                await rollback.RollbackAsync();
            }
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public async Task Fifo_positive_count_reprices_later_mi_onto_count_layer(
        int issueCount, int issueQuantity)
    {
        var receiptQuantity = issueCount * issueQuantity;
        await using var db = await _factory.CreateDbContextAsync();
        await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
        db.StockCostStates.Remove(await db.StockCostStates.SingleAsync());
        await db.SaveChangesAsync();
        await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [new FifoLayerSeed(receiptQuantity, 4m,
                FixedToday.AddDays(1))]);
        var pile = await db.IvBalLocs.SingleAsync();
        pile.StdQty = receiptQuantity;
        pile.TransDate = FixedToday.AddDays(1);
        await db.SaveChangesAsync();
        var receiptPosting = await db.StockPostings.SingleAsync();
        var receiptFact = await db.StockValuationFacts.SingleAsync();
        var receiptHistory = new IvTrxHistory
        {
            CompanyCode = pile.CompanyCode, BranchCode = pile.BranchCode,
            BatchNo = 703, TrxLineNo = 1,
            TrxDtTime = FixedToday.AddDays(1),
            TrxType = IvTrxTypes.MiscellaneousReceipt,
            BatchStatus = IvBatchStatuses.Posted,
            ICode = "A100", ToBalLocId = pile.Id,
            ToWarehouse = "MAIN", ToLocation = "BIN1",
            ToStdQty = receiptQuantity, ToStdUom = "EA",
            ValuationStatus = StockValuationStatuses.Valued,
            LedgerVersion = 2, LedgerEpochId = receiptPosting.LedgerEpochId,
            StockPostingId = receiptPosting.Id,
            PostingLineNo = receiptFact.PostingLineNo,
            EntryRole = "ORIGINAL", DocumentRevision = 1
        };
        db.IvTrxHistories.Add(receiptHistory);
        await db.SaveChangesAsync();
        await db.StockPostings.Where(p => p.Id == receiptPosting.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.SourceDocumentType,
                    IvTrxTypes.MiscellaneousReceipt)
                .SetProperty(p => p.SourceDocumentId, "703")
                .SetProperty(p => p.SourceDocumentNo, "703"));
        await db.StockValuationFacts.Where(f => f.Id == receiptFact.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.SourceDocumentType,
                    IvTrxTypes.MiscellaneousReceipt)
                .SetProperty(f => f.SourceDocumentId, "703")
                .SetProperty(f => f.SourceDocumentNo, "703")
                .SetProperty(f => f.InventoryHistoryId, receiptHistory.Id));
        db.ChangeTracker.Clear();

        var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
        for (var index = 0; index < issueCount; index++)
        {
            var issueAt = FixedToday.AddDays(2 + index);
            var batchNo = 704 + index;
            var remaining = receiptQuantity
                - (index + 1) * issueQuantity;
            var issueCommand = new StockPostingCommand
            {
                RequestId = Guid.NewGuid(),
                CommandType = "MI_COUNT_POSITIVE_REPRICE_TEST",
                SourceModule = "INVENTORY",
                SourceDocumentType = IvTrxTypes.MiscellaneousIssue,
                SourceDocumentId = batchNo.ToString(),
                SourceDocumentNo = batchNo.ToString(),
                DocumentRevision = 1, PostingRole = "PRIMARY",
                EffectiveAt = issueAt,
                Evidence = StockPostingFingerprint.Create(
                    new { BatchNo = batchNo, Item = "A100",
                        Quantity = issueQuantity },
                    new { EffectiveAt = issueAt })
            };
            var issueResult = await coordinator.ExecuteAsync(issueCommand,
                async (context, _) =>
                {
                    var current = await context.Db.IvBalLocs.SingleAsync(p =>
                        p.Id == pile.Id);
                    current.StdQty = remaining;
                    current.TransDate = issueAt;
                    var history = new IvTrxHistory
                    {
                        CompanyCode = pile.CompanyCode,
                        BranchCode = pile.BranchCode,
                        BatchNo = batchNo, TrxLineNo = 1,
                        TrxType = IvTrxTypes.MiscellaneousIssue,
                        BatchStatus = IvBatchStatuses.Posted,
                        ICode = "A100", IStatus = "ACTIVE",
                        FromBalLocId = pile.Id,
                        FrWarehouse = "MAIN", FrLocation = "BIN1",
                        FrStdQty = issueQuantity, FrStdUom = "EA"
                    };
                    context.Db.IvTrxHistories.Add(history);
                    new IvInventoryHistoryWriter().StampGeneration(
                        context, [history], 1);
                    return 0;
                });
            Assert.True(issueResult.Succeeded, issueResult.Error?.Message);
        }
        db.ChangeTracker.Clear();
        var before = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            pile.CompanyCode, pile.BranchCode, "A100");
        Assert.Equal(0m, before.CurrentQuantity);
        Assert.Equal(issueCount, before.ActiveConsumptions.Count);
        Assert.All(before.ActiveConsumptions, c =>
            Assert.Equal(4m * issueQuantity, c.Value));

        var header = new IvStockCountHdr
        {
            CompanyCode = pile.CompanyCode, BranchCode = pile.BranchCode,
            CountNo = "CC-FIFO-POSITIVE-REPRICE-MI",
            CountDate = FixedToday,
            Status = IvStockCountStatuses.Counted
        };
        var countLine = new IvStockCountLine
        {
            Header = header, LineNumber = 1, BalLocId = pile.Id,
            ICode = "A100", WHCode = "MAIN", LocCode = "BIN1",
            LotNo = "", IStatus = "ACTIVE", PhysicalQty = issueCount,
            SystemQty = 0m
        };
        db.IvStockCountLines.Add(countLine);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var access = new Mock<IAccessRightService>();
        access.Setup(a => a.CanAsync(
                ErpWeb.Core.Menus.MenuCodes.InventoryStockCount,
                ErpWeb.Core.Menus.PermissionCodes.PriceOverride,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var rootPlan = await StockReplayCountSheetRootWriter.BuildAsync(
            db, access.Object, header.CompanyCode, header.BranchCode,
            header.Id,
            [new StockCountCostApprovalRequest(countLine.Id,
                StockCountCostBasisKinds.ManualApproved, 3m,
                "Verified count cost")],
            "manager", DateTime.UtcNow);
        var plan = Assert.IsType<StockReplayFifoCountSheetPostingPlan>(
            rootPlan.Fifo);
        var item = Assert.Single(plan.Items);
        Assert.Equal(issueCount, item.Projection.LaterIssues.Count);
        var projectedLayerId = Assert.Single(item.Projection.Counts)
            .Receipt!.LayerId;
        Assert.All(item.Projection.LaterIssues, projectedIssue =>
        {
            Assert.Equal(3m + 4m * (issueQuantity - 1),
                projectedIssue.Value);
            Assert.Equal(issueQuantity,
                projectedIssue.Allocations.Count);
            Assert.Contains(projectedIssue.Allocations, a =>
                a.LayerId == projectedLayerId
                && a.Quantity == 1m && a.Value == 3m);
        });
        Assert.Equal(issueCount, item.Projection.FinalQuantity);
        Assert.Equal(4m * issueCount, item.Projection.FinalValue);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var begin = await coordinator.BeginInTransactionAsync(db,
            rootPlan.Command);
        Assert.NotNull(begin.Context);
        var root = begin.Context!;
        var committed = false;
        try
        {
            var rootResult = await StockReplayCountSheetRootWriter
                .AppendWithinRootAsync(root, rootPlan,
                    new RunningNumberService());
            var posted = Assert.IsType<StockReplayFifoInitialCountSheetResult>(
                rootResult.Fifo);
            Assert.Equal(1 + issueCount * (1 + issueQuantity),
                posted.Facts.Count);
            Assert.Equal(1 + issueCount,
                posted.RequiredActiveHistoryIds.Count);
            Assert.Equal(posted.RequiredActiveHistoryIds,
                rootResult.RequiredActiveHistoryIds);
            await coordinator.CompleteReplayInTransactionAsync(root,
                posted.RequiredActiveHistoryIds);
            await transaction.CommitAsync();
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                await coordinator.AbortInTransactionAsync(root);
                await transaction.RollbackAsync();
            }
        }
        db.ChangeTracker.Clear();
        var final = await StockReplayFifoActiveLayerReader.ReadAsync(db,
            pile.CompanyCode, pile.BranchCode, "A100");
        Assert.Equal(issueCount, final.CurrentQuantity);
        Assert.Equal(4m * issueCount, final.CurrentValue);
        var correctedIssues = final.ActiveConsumptions.Where(c =>
            item.Projection.LaterIssues.Any(i => i.Key == c.IssueKey))
            .ToArray();
        Assert.Equal(issueCount * issueQuantity,
            correctedIssues.Length);
        Assert.Equal(issueCount,
            correctedIssues.Count(c => c.Value == 3m));
        var countLayer = Assert.Single(final.Layers.Where(l =>
            l.OriginPostingId == root.Posting.Id));
        Assert.Equal(issueCount,
            correctedIssues.Count(c => c.LayerId == countLayer.LayerId));
        Assert.Equal(0m, countLayer.RemainingQuantity);
        var mrLayer = Assert.Single(final.Layers.Where(l =>
            l.OriginFactId == receiptFact.Id));
        Assert.Equal(issueCount, mrLayer.RemainingQuantity);
        Assert.Equal(4m * issueCount, mrLayer.RemainingValue);
        Assert.Equal(issueCount, (await db.IvBalLocs.SingleAsync(p =>
            p.Id == pile.Id)).StdQty);
        var removal = await StockReplayFifoCountSheetRemovalPlanner
            .BuildAsync(db, header.CompanyCode, header.BranchCode, header.Id);
        var projected = Assert.Single(removal.Items).Projection!;
        Assert.Equal(0m, projected.FinalQuantity);
        Assert.Equal(0m, projected.FinalValue);
        Assert.Equal(issueCount, projected.LaterIssues.Count);
        Assert.All(projected.LaterIssues, issue =>
            Assert.Equal(4m * issueQuantity, issue.Value));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            Assert.Throws<StockLedgerException>(() =>
                StockReplayFifoCountSheetRemovalPlanner
                    .RequireIsolatedAdjustment(removal)).Error.Code);
        if (issueCount == 1 && issueQuantity == 1)
        {
            await using var rollback = await db.Database.BeginTransactionAsync();
            var reverse = await coordinator.BeginInTransactionAsync(db,
                removal.Command);
            Assert.NotNull(reverse.Context);
            var rollbackRoot = reverse.Context!;
            try
            {
                await StockReplayFifoCountSheetRemovalPlanner
                    .ValidateWithinRootAsync(rollbackRoot, removal);
                await db.IvTrxHistories.Where(h => h.Id == receiptHistory.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(h =>
                        h.TrxLineNo, 99));
                var stale = await Assert.ThrowsAsync<StockLedgerException>(
                    () => StockReplayFifoCountSheetRemovalPlanner
                        .ValidateWithinRootAsync(rollbackRoot, removal));
                Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                    stale.Error.Code);
            }
            finally
            {
                await coordinator.AbortInTransactionAsync(rollbackRoot);
                await rollback.RollbackAsync();
            }
        }
    }
}
