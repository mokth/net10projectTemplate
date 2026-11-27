using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Quality;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.Transactions;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReplayBenchmark;

public static class Program
{
    private const string CompanyCode = "DEMO";
    private const string BranchCode = "HQ";
    private const string OtherCompany = "OTHER";
    private static readonly DateTime BenchmarkToday = new(2026, 10, 30);
    private static readonly DateTime PolicyStart = new(2026, 1, 1);
    private static readonly DateTime TailStart = new(2026, 10, 5, 8, 0, 0);
    private static readonly DateTime BackdatedTrxDate = new(2026, 10, 2, 8, 0, 0);

    public static async Task<int> Main(string[] args)
    {
        var sqlServer = GetArg(args, "--sql-server") ?? @".\SQLEXPRESS";
        var backgroundRows = int.Parse(GetArg(args, "--background-rows") ?? "5000000", CultureInfo.InvariantCulture);
        var keepDb = args.Contains("--keep-db", StringComparer.OrdinalIgnoreCase);
        var jsonOut = GetArg(args, "--json-out");
        var mdOut = GetArg(args, "--md-out");

        if (sqlServer.Contains("ERPWeb", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to target ERPWeb directly.");

        var dbName = $"ErpWeb_Bench_{Guid.NewGuid():N}"[..24];
        var masterConn = $"Server={sqlServer};Database=master;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True;Connect Timeout=30;";
        var benchConn = $"Server={sqlServer};Database={dbName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True;Connect Timeout=30;";

        Console.WriteLine($"[Bench] SQL Server : {sqlServer}");
        Console.WriteLine($"[Bench] Scratch DB : {dbName} (live ERPWeb untouched)");
        Console.WriteLine($"[Bench] Target BG  : {backgroundRows:N0} IvTrxHistory + {backgroundRows:N0} StockValuationFact");

        var interceptor = new QueryMetricsInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(benchConn, sql => sql.CommandTimeout(300))
            .AddInterceptors(interceptor)
            .Options;
        IDbContextFactory<AppDbContext> factory = new BenchDbContextFactory(options);

        try
        {
            await CreateDatabaseAsync(masterConn, dbName);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var seedSw = Stopwatch.StartNew();
            var seedInfo = await SeedDatabaseAsync(benchConn, factory, backgroundRows);
            seedSw.Stop();
            Console.WriteLine($"[Bench] Seed completed in {seedSw.Elapsed.TotalSeconds:F1}s | DB Size: {seedInfo.TotalDataMb:F1} MB data + {seedInfo.TotalLogMb:F1} MB log");
            Console.WriteLine($"[Bench] Exact Table Row Counts -> IvTrxHistory={seedInfo.IvTrxHistoryRows:N0}, StockValuationFact={seedInfo.StockValuationFactRows:N0}, StockPosting={seedInfo.StockPostingRows:N0}, StockFifoLayer={seedInfo.StockFifoLayerRows:N0}, StockFifoLayerConsumption={seedInfo.StockFifoConsumptionRows:N0}");

            var queryPlans = await CaptureQueryPlansAsync(benchConn, seedInfo);
            var plannerResults = await RunPlannerAndReaderScalingAsync(factory, benchConn, interceptor, seedInfo);
            var e2eResults = await RunEndToEndPostingBenchmarksAsync(factory, benchConn, interceptor, seedInfo);
            var contention = await RunCompetingWriterBenchmarkAsync(factory, benchConn, interceptor);

            var report = new BenchmarkReport(
                DateTime.UtcNow,
                sqlServer,
                dbName,
                backgroundRows,
                seedSw.Elapsed.TotalSeconds,
                seedInfo,
                queryPlans,
                plannerResults,
                e2eResults,
                contention);

            var markdown = FormatMarkdownReport(report);
            Console.WriteLine();
            Console.WriteLine(markdown);

            if (!string.IsNullOrWhiteSpace(jsonOut))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonOut))!);
                await File.WriteAllTextAsync(jsonOut, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            if (!string.IsNullOrWhiteSpace(mdOut))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(mdOut))!);
                await File.WriteAllTextAsync(mdOut, markdown);
            }

            var anyScan = queryPlans.Any(q => q.HasFullScan);
            var anyContentionTimeout = contention.TimeoutCount > 0;
            var anyFailedRequiredE2E = e2eResults.Any(r => r.ExpectedSuccess != r.Succeeded);
            return anyScan || anyContentionTimeout || anyFailedRequiredE2E ? 2 : 0;
        }
        finally
        {
            SqlConnection.ClearAllPools();
            if (!keepDb)
            {
                await DropDatabaseAsync(masterConn, dbName);
                Console.WriteLine($"[Bench] Dropped scratch DB {dbName}.");
            }
            else
            {
                Console.WriteLine($"[Bench] Kept scratch DB {dbName} (--keep-db).");
            }
        }
    }

    private static string? GetArg(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static async Task CreateDatabaseAsync(string masterConn, string dbName)
    {
        await using var conn = new SqlConnection(masterConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = $"""
            IF DB_ID('{dbName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{dbName}];
            END
            CREATE DATABASE [{dbName}];
            ALTER DATABASE [{dbName}] SET RECOVERY SIMPLE;
            ALTER DATABASE [{dbName}] MODIFY FILE (NAME = N'{dbName}', FILEGROWTH = 256MB);
            ALTER DATABASE [{dbName}] MODIFY FILE (NAME = N'{dbName}_log', FILEGROWTH = 128MB);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string masterConn, string dbName)
    {
        try
        {
            await using var conn = new SqlConnection(masterConn);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 120;
            cmd.CommandText = $"""
                IF DB_ID('{dbName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{dbName}];
                END
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Bench][WARN] Failed to drop {dbName}: {ex.Message}");
        }
    }

    private static async Task<SeedSummary> SeedDatabaseAsync(
        string benchConn,
        IDbContextFactory<AppDbContext> factory,
        int backgroundRows)
    {
        var wacItems = new (string Code, int Count)[]
        {
            ("WAC-10", 10),
            ("WAC-50", 50),
            ("WAC-100", 100),
            ("WAC-101", 101),
            ("WAC-1K", 1000),
            ("WAC-10K", 10000),
            ("WAC-50K", 50000),
            ("WAC-100-CONT", 100),
            ("WAC-COMPETE", 0)
        };
        var fifoItems = new (string Code, int Count)[]
        {
            ("FIFO-10", 10),
            ("FIFO-50", 50),
            ("FIFO-100", 100),
            ("FIFO-101", 101),
            ("FIFO-1K", 1000),
            ("FIFO-10K", 10000)
        };

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Companies.AddRange(
                new Company { CompanyCode = CompanyCode, CompanyName = "Demo", CurrencyCode = "MYR", IsActive = true, CreatedDate = DateTime.UtcNow, CreatedBy = "BENCH" },
                new Company { CompanyCode = OtherCompany, CompanyName = "Other", CurrencyCode = "MYR", IsActive = true, CreatedDate = DateTime.UtcNow, CreatedBy = "BENCH" });
            db.IvWarehouses.AddRange(
                new IvWarehouse { CompanyCode = CompanyCode, BranchCode = BranchCode, WarehouseCode = "MAIN", IsActive = true },
                new IvWarehouse { CompanyCode = OtherCompany, BranchCode = BranchCode, WarehouseCode = "MAIN", IsActive = true });
            db.IvLocations.AddRange(
                new IvLocation { CompanyCode = CompanyCode, BranchCode = BranchCode, WarehouseCode = "MAIN", LocCode = "BIN1", IsActive = true },
                new IvLocation { CompanyCode = OtherCompany, BranchCode = BranchCode, WarehouseCode = "MAIN", LocCode = "BIN1", IsActive = true });
            db.MsUoms.AddRange(
                new MsUom { CompanyCode = CompanyCode, UomCode = "EA", IsActive = true },
                new MsUom { CompanyCode = OtherCompany, UomCode = "EA", IsActive = true });
            db.IvClasses.AddRange(
                new IvClass { CompanyCode = CompanyCode, IClassCode = "RAW", IsActive = true },
                new IvClass { CompanyCode = OtherCompany, IClassCode = "RAW", IsActive = true });
            db.IvStatuses.AddRange(
                new IvStatus { CompanyCode = CompanyCode, IStatus = "ACTIVE", IsActive = true },
                new IvStatus { CompanyCode = OtherCompany, IStatus = "ACTIVE", IsActive = true });

            for (var i = 1; i <= 100; i++)
            {
                var bgCode = $"BG-{i:D4}";
                db.IvStockMasters.Add(new IvStockMaster
                {
                    CompanyCode = CompanyCode, ICode = bgCode, IDesc = bgCode, IClassCode = "RAW",
                    StdUom = "EA", StockControl = true, LotControl = false, IsActive = true, PurchasePrice = 10m
                });
                db.IvStockMasters.Add(new IvStockMaster
                {
                    CompanyCode = OtherCompany, ICode = bgCode, IDesc = bgCode, IClassCode = "RAW",
                    StdUom = "EA", StockControl = true, LotControl = false, IsActive = true, PurchasePrice = 10m
                });
            }

            foreach (var (code, _) in wacItems.Concat(fifoItems))
            {
                db.IvStockMasters.Add(new IvStockMaster
                {
                    CompanyCode = CompanyCode, ICode = code, IDesc = code, IClassCode = "RAW",
                    StdUom = "EA", StockControl = true, LotControl = false, IsActive = true, PurchasePrice = 10m
                });
            }

            db.StockLedgerEpochs.AddRange(
                new StockLedgerEpoch
                {
                    CompanyCode = CompanyCode, BranchCode = BranchCode, EffectiveFrom = PolicyStart,
                    Version = 2, Status = StockLedgerEpochStatuses.Active, MigrationBatchId = Guid.NewGuid(),
                    ReconciliationManifestHash = new string('A', 64), ActivatedAtUtc = DateTime.UtcNow, ActivatedBy = "BENCH"
                },
                new StockLedgerEpoch
                {
                    CompanyCode = OtherCompany, BranchCode = BranchCode, EffectiveFrom = PolicyStart,
                    Version = 2, Status = StockLedgerEpochStatuses.Active, MigrationBatchId = Guid.NewGuid(),
                    ReconciliationManifestHash = new string('B', 64), ActivatedAtUtc = DateTime.UtcNow, ActivatedBy = "BENCH"
                });

            // FIFO effective from 2026-10-15 onward only if FIFO tests switch? No — keep separate branches?
            // StockCostPolicyRevision is per (CompanyCode, BranchCode).
            // So we seed MOVING_AVERAGE for DEMO/HQ during WAC runs, and switch policy to FIFO for FIFO runs!
            db.StockCostPolicyRevisions.Add(new StockCostPolicyRevision
            {
                CompanyCode = CompanyCode, BranchCode = BranchCode, CostMethod = StockCostMethods.MovingAverage,
                EffectiveFrom = PolicyStart, Status = StockCostPolicyStatuses.Active, ApprovedBy = "BENCH",
                ApprovedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow, CreatedBy = "BENCH"
            });

            await db.SaveChangesAsync();
        }

        await using var conn = new SqlConnection(benchConn);
        await conn.OpenAsync();

        var epochDemo = await ScalarAsync<long>(conn, "SELECT Id FROM StockLedgerEpoch WHERE CompanyCode='DEMO' AND BranchCode='HQ'");
        var epochOther = await ScalarAsync<long>(conn, "SELECT Id FROM StockLedgerEpoch WHERE CompanyCode='OTHER' AND BranchCode='HQ'");

        // Create background piles (1 per BG item in DEMO and OTHER = 200 piles)
        await ExecAsync(conn, """
            INSERT INTO IvBalLoc (CompanyCode, BranchCode, ICode, WHCode, LocCode, LotNo, IStatus, StdQty, StdUOM, TransDate, UnitPrice)
            SELECT m.CompanyCode, 'HQ', m.ICode, 'MAIN', 'BIN1', '', 'ACTIVE', 1000.0000, 'EA', '2026-09-30', 10.0000
            FROM IvStockMaster m
            WHERE m.ICode LIKE 'BG-%';
            """);

        // Create target piles
        await ExecAsync(conn, """
            INSERT INTO IvBalLoc (CompanyCode, BranchCode, ICode, WHCode, LocCode, LotNo, IStatus, StdQty, StdUOM, TransDate, UnitPrice)
            SELECT 'DEMO', 'HQ', m.ICode, 'MAIN', 'BIN1', '', 'ACTIVE', 0.0000, 'EA', '2026-10-01', 10.0000
            FROM IvStockMaster m
            WHERE m.CompanyCode='DEMO' AND (m.ICode LIKE 'WAC-%' OR m.ICode LIKE 'FIFO-%');
            """);

        // Build fast 100,000-row tally helper table (untracked temp/scratch inside bench DB)
        await ExecAsync(conn, """
            IF OBJECT_ID('dbo.__BenchTally') IS NOT NULL DROP TABLE dbo.__BenchTally;
            SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
            INTO dbo.__BenchTally
            FROM sys.all_columns a CROSS JOIN sys.all_columns b;
            CREATE UNIQUE CLUSTERED INDEX CX_BenchTally ON dbo.__BenchTally(n);
            """);

        // 1) Seed 250,000 StockPosting rows for background (125k DEMO, 125k OTHER) -> total 250k postings
        const int bgPostingBatches = 5; // 5 * 50,000 = 250,000
        for (var b = 0; b < bgPostingBatches; b++)
        {
            var offset = b * 50000L;
            var seqBase = 10_000_000L + offset;
            await ExecAsync(conn, $"""
                INSERT INTO StockPosting (
                    CompanyCode, BranchCode, LedgerEpochId, PostingSequence, RequestId, CommandType,
                    RequestFingerprint, SourceModule, SourceDocumentType, SourceDocumentId, SourceDocumentNo,
                    DocumentRevision, PostingRole, SourceSnapshotJson, SourceSnapshotHash, SourceSnapshotSchemaVersion,
                    EffectiveAt, BusinessDate, PeriodKey, PostedAtUtc, PostedBy, SealedAtUtc
                )
                SELECT
                    CASE WHEN t.n % 2 = 0 THEN 'DEMO' ELSE 'OTHER' END,
                    'HQ',
                    CASE WHEN t.n % 2 = 0 THEN {epochDemo} ELSE {epochOther} END,
                    {seqBase} + t.n,
                    NEWID(),
                    'BG_SEED',
                    REPLICATE('F', 64),
                    'INVENTORY',
                    'BG_MR',
                    CAST({offset} + t.n AS nvarchar(64)),
                    CONCAT('BG-', {offset} + t.n),
                    1,
                    'PRIMARY',
                    CHAR(123) + CHAR(125),
                    REPLICATE('S', 64),
                    1,
                    DATEADD(SECOND, CAST((({offset} + t.n) % 25000000) AS int), '2024-01-01T00:00:00'),
                    CAST(DATEADD(SECOND, CAST((({offset} + t.n) % 25000000) AS int), '2024-01-01T00:00:00') AS date),
                    FORMAT(DATEADD(SECOND, CAST((({offset} + t.n) % 25000000) AS int), '2024-01-01T00:00:00'), 'yyyy-MM'),
                    SYSUTCDATETIME(),
                    'BENCH',
                    SYSUTCDATETIME()
                FROM dbo.__BenchTally t
                WHERE t.n <= 50000;
                """);
        }

        // Capture background posting range & pile IDs
        var firstBgBalDemo = await ScalarAsync<int>(conn, "SELECT MIN(ID) FROM IvBalLoc WHERE CompanyCode='DEMO' AND ICode LIKE 'BG-%'");
        var firstBgPostingId = await ScalarAsync<long>(conn, "SELECT MIN(Id) FROM StockPosting WHERE CommandType='BG_SEED'");
        var bgPostingCount = await ScalarAsync<long>(conn, "SELECT COUNT(*) FROM StockPosting WHERE CommandType='BG_SEED'");

        // 2) Seed background IvTrxHistory rows in chunks of 500,000 up to backgroundRows (default 5,000,000)
        // Keeping LedgerVersion IS NULL? Wait: UQ_IvTrxHistory_Company_Branch_Batch_Line is unique when LedgerVersion IS NULL!
        // Setting unique (BatchNo, TrxLineNo) handles that trivially cleanly across all 5M rows!
        var chunkSize = 500_000;
        for (var done = 0; done < backgroundRows; done += chunkSize)
        {
            var take = Math.Min(chunkSize, backgroundRows - done);
            var perOuter = 5; // 5 * 100,000 = 500,000
            for (var sub = 0; sub < perOuter && done + sub * 100_000 < backgroundRows; sub++)
            {
                var subTake = Math.Min(100_000, backgroundRows - (done + sub * 100_000));
                var batchOffset = done + sub * 100_000;
                await ExecAsync(conn, $"""
                    INSERT INTO IvTrxHistory (
                        CompanyCode, BranchCode, BatchNo, TrxLineNo, TrxDtTime, TrxType, BatchStatus,
                        RefNo, ICode, ToBalLocId, ToWarehouse, ToLocation, ToLot, ToStdQty, ToStdUOM,
                        IStatus, Cost, UnitPrice, AsNowCost, EntryRole
                    )
                    SELECT
                        CASE WHEN t.n % 2 = 0 THEN 'DEMO' ELSE 'OTHER' END,
                        'HQ',
                        {1_000_000 + (batchOffset / 10)} + ((t.n - 1) / 10),
                        CAST(((t.n - 1) % 10) + 1 AS smallint),
                        DATEADD(SECOND, CAST((({batchOffset} + t.n) % 25000000) AS int), '2024-01-01T00:00:00'),
                        'MR',
                        'POSTED',
                        CONCAT('BGR', {batchOffset} + t.n),
                        CONCAT('BG-', RIGHT('000' + CAST(((t.n - 1) % 100) + 1 AS varchar(4)), 4)),
                        {firstBgBalDemo} + ((t.n - 1) % 100),
                        'MAIN',
                        'BIN1',
                        '',
                        1.0000,
                        'EA',
                        'ACTIVE',
                        10.0000,
                        10.0000,
                        10.0000,
                        'ORIGINAL'
                    FROM dbo.__BenchTally t
                    WHERE t.n <= {subTake};
                    """);
            }
            if ((done + chunkSize) % 1_000_000 == 0 || done + chunkSize >= backgroundRows)
                Console.WriteLine($"[Bench] Seeded {Math.Min(done + chunkSize, backgroundRows):N0} / {backgroundRows:N0} IvTrxHistory rows...");
        }

        // 3) Seed background StockValuationFact rows (20 lines per StockPosting = 250,000 * 20 = 5,000,000)
        // Process in chunks of 25,000 postings * 20 = 500,000 facts per SQL batch
        var factTarget = backgroundRows;
        var factsDone = 0;
        var postingIdx = 0L;
        while (factsDone < factTarget)
        {
            var postingsThisChunk = Math.Min(25_000, (factTarget - factsDone + 19) / 20);
            var linesPerPosting = Math.Min(20, (factTarget - factsDone + postingsThisChunk - 1) / postingsThisChunk);
            var lowP = firstBgPostingId + postingIdx;
            var highP = lowP + postingsThisChunk - 1;
            if (highP >= firstBgPostingId + bgPostingCount)
                throw new InvalidOperationException("Exceeded seeded StockPosting pool for StockValuationFact.");

            await ExecAsync(conn, $"""
                INSERT INTO StockValuationFact (
                    CompanyCode, BranchCode, LedgerEpochId, StockPostingId, PostingLineNo, SplitOrdinal,
                    SourceLineId, SourceDocumentType, SourceDocumentId, SourceDocumentNo, SourceDocumentLine,
                    EffectiveAt, BusinessDate, PeriodKey, ItemCode, WarehouseCode, LocationCode, LotNo, ItemStatus,
                    BaseUom, MovementCode, Direction, BaseQty, CostMethod, UnitCost, CostAmount,
                    BaseCurrency, BaseCostAmount, ValuationSource, ValuationStatus, ValuationVersion,
                    CreatedAtUtc, CreatedBy
                )
                SELECT
                    p.CompanyCode,
                    p.BranchCode,
                    p.LedgerEpochId,
                    p.Id,
                    CAST(l.n AS int),
                    0,
                    CONCAT('BG:', p.Id, ':', l.n),
                    'MR',
                    p.SourceDocumentId,
                    p.SourceDocumentNo,
                    CAST(l.n AS varchar(10)),
                    p.EffectiveAt,
                    p.BusinessDate,
                    p.PeriodKey,
                    CONCAT('BG-', RIGHT('000' + CAST(((p.Id + l.n) % 100) + 1 AS varchar(4)), 4)),
                    'MAIN',
                    'BIN1',
                    '',
                    'ACTIVE',
                    'EA',
                    'RECEIPT_IN',
                    1,
                    1.000000,
                    'MOVING_AVERAGE',
                    10.000000,
                    10.000000,
                    'MYR',
                    10.000000,
                    'RECEIPT_ACTUAL',
                    'VALUED',
                    1,
                    p.PostedAtUtc,
                    'BENCH'
                FROM StockPosting p
                CROSS JOIN (SELECT n FROM dbo.__BenchTally WHERE n <= {linesPerPosting}) l
                WHERE p.Id BETWEEN {lowP} AND {highP};
                """);

            factsDone += postingsThisChunk * linesPerPosting;
            postingIdx += postingsThisChunk;
            if (factsDone % 1_000_000 == 0 || factsDone >= factTarget)
                Console.WriteLine($"[Bench] Seeded {factsDone:N0} / {factTarget:N0} StockValuationFact rows...");
        }

        // 4) Seed 50,000 unrelated StockFifoLayer + 100,000 StockFifoLayerConsumption rows
        await ExecAsync(conn, $"""
            ;WITH bgFacts AS (
                SELECT TOP (50000) f.Id, f.CompanyCode, f.BranchCode, f.StockPostingId, f.EffectiveAt, f.ItemCode, f.SourceDocumentNo
                FROM StockValuationFact f
                WHERE f.Id <= (SELECT MIN(Id) + 200000 FROM StockValuationFact)
                  AND f.ItemCode LIKE 'BG-%'
                ORDER BY f.Id
            )
            INSERT INTO StockFifoLayer (
                CompanyCode, BranchCode, ItemCode, BaseUom, OriginValuationFactId, OriginStockPostingId,
                ReceiptEffectiveAt, OriginalQty, RemainingQty, OriginalValue, AccumulatedAdjustment,
                RemainingValue, CurrentUnitCost, SourceDocumentType, SourceDocumentNo, WarehouseCode, LotNo, Status
            )
            SELECT
                CompanyCode, BranchCode, ItemCode, 'EA', Id, StockPostingId,
                EffectiveAt, 10.000000, 0.000000, 100.000000, 0.000000,
                0.000000, 10.000000, 'MR', SourceDocumentNo, 'MAIN', '', 'CLOSED'
            FROM bgFacts;
            """);

        await ExecAsync(conn, """
            INSERT INTO StockFifoLayerConsumption (
                CompanyCode, BranchCode, IssueValuationFactId, FifoLayerId, SplitOrdinal,
                ConsumedQty, ConsumedValue, CreatedAtUtc, CreatedBy
            )
            SELECT
                l.CompanyCode, l.BranchCode, l.OriginValuationFactId, l.Id, s.n - 1,
                5.000000, 50.000000, SYSUTCDATETIME(), 'BENCH'
            FROM StockFifoLayer l
            CROSS JOIN (SELECT 1 AS n UNION ALL SELECT 2) s;
            """);

        // 5) Seed target V2 tails cleanly via SQL for DEMO/HQ
        var nextSeq = 1L;
        var nextBatchNo = 10_000_000;
        var itemPileIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (code, count) in wacItems)
        {
            var balId = await ScalarAsync<int>(conn, $"SELECT ID FROM IvBalLoc WHERE CompanyCode='DEMO' AND BranchCode='HQ' AND ICode='{code}'");
            itemPileIds[code] = balId;
            (nextSeq, nextBatchNo) = await SeedWacTailAsync(conn, epochDemo, code, balId, count, nextSeq, nextBatchNo);
        }

        foreach (var (code, count) in fifoItems)
        {
            var balId = await ScalarAsync<int>(conn, $"SELECT ID FROM IvBalLoc WHERE CompanyCode='DEMO' AND BranchCode='HQ' AND ICode='{code}'");
            itemPileIds[code] = balId;
            (nextSeq, nextBatchNo) = await SeedFifoTailAsync(conn, epochDemo, code, balId, count, nextSeq, nextBatchNo);
        }

        // Branch sequence watermark
        await ExecAsync(conn, $"""
            DECLARE @maxSeq bigint = (SELECT ISNULL(MAX(PostingSequence), 0) FROM StockPosting WHERE CompanyCode='DEMO' AND BranchCode='HQ');
            IF @maxSeq < {nextSeq} SET @maxSeq = {nextSeq};
            IF EXISTS (SELECT 1 FROM StockPostingBranchSequence WHERE CompanyCode='DEMO' AND BranchCode='HQ')
                UPDATE StockPostingBranchSequence SET LastSequence = @maxSeq, UpdatedAtUtc = SYSUTCDATETIME() WHERE CompanyCode='DEMO' AND BranchCode='HQ';
            ELSE
                INSERT INTO StockPostingBranchSequence (CompanyCode, BranchCode, LastSequence, UpdatedAtUtc) VALUES ('DEMO', 'HQ', @maxSeq, SYSUTCDATETIME());
            """);

        await ExecAsync(conn, "DROP TABLE dbo.__BenchTally;");
        await ExecAsync(conn, "UPDATE STATISTICS IvTrxHistory; UPDATE STATISTICS StockValuationFact; UPDATE STATISTICS StockPosting; UPDATE STATISTICS StockFifoLayer; UPDATE STATISTICS StockFifoLayerConsumption; UPDATE STATISTICS StockCostState; UPDATE STATISTICS IvBalLoc;");

        // Exact metadata row counts & MB sizes
        var histRows = await ScalarAsync<long>(conn, "SELECT SUM(row_count) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('IvTrxHistory') AND index_id IN (0,1)");
        var factRows = await ScalarAsync<long>(conn, "SELECT SUM(row_count) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('StockValuationFact') AND index_id IN (0,1)");
        var postRows = await ScalarAsync<long>(conn, "SELECT SUM(row_count) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('StockPosting') AND index_id IN (0,1)");
        var layerRows = await ScalarAsync<long>(conn, "SELECT SUM(row_count) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('StockFifoLayer') AND index_id IN (0,1)");
        var consRows = await ScalarAsync<long>(conn, "SELECT SUM(row_count) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('StockFifoLayerConsumption') AND index_id IN (0,1)");
        var dataMb = await ScalarAsync<double>(conn, "SELECT CAST(SUM(CASE WHEN type = 0 THEN size END) * 8.0 / 1024.0 AS float) FROM sys.database_files");
        var logMb = await ScalarAsync<double>(conn, "SELECT CAST(SUM(CASE WHEN type = 1 THEN size END) * 8.0 / 1024.0 AS float) FROM sys.database_files");

        return new SeedSummary(histRows, factRows, postRows, layerRows, consRows, dataMb, logMb, itemPileIds);
    }

    private static async Task<(long NextSeq, int NextBatchNo)> SeedWacTailAsync(
        SqlConnection conn, long epochId, string itemCode, int balLocId, int count,
        long nextSeq, int nextBatchNo)
    {
        if (count == 0)
        {
            // Seed empty StockCostState for WAC-COMPETE so concurrent normal MR posts cleanly
            await ExecAsync(conn, $"""
                INSERT INTO StockCostState (CompanyCode, BranchCode, ItemCode, CostMethod, OnHandBaseQty, InventoryValue, CurrentUnitCost, AverageUnitCost, LastPostingSequence)
                VALUES ('DEMO', 'HQ', '{itemCode}', 'MOVING_AVERAGE', 0, 0, 0, 0, 0);
                """);
            return (nextSeq, nextBatchNo);
        }

        // Pattern: n=1 -> MR +10000 qty @ 10.000000 (keeps qty always >= 10000 even for 50k tails!);
        // even n -> MR +10 @ 10.000000; odd n>1 -> MI -5 @ 10.000000.
        // All prices 10.000000 so net balance and value are exact integer math!
        long mrCount = 1 + count / 2;
        long miCount = (count - 1) / 2;
        decimal finalQty = 500000m + (mrCount - 1) * 10m - miCount * 5m;
        decimal finalVal = finalQty * 10m;
        var seqStart = nextSeq;
        var batchStart = nextBatchNo;

        await ExecAsync(conn, $"""
            ;WITH nums AS (
                SELECT n FROM dbo.__BenchTally WHERE n <= {count}
            )
            INSERT INTO IvTrxBatch (
                CompanyCode, BranchCode, BatchNo, TrxDtTime, TrxType, BatchStatus, RefNo, PostedCount, RollbackCount
            )
            SELECT
                'DEMO', 'HQ', {batchStart} + n - 1,
                DATEADD(SECOND, n * 30, '2026-10-05T08:00:00'),
                CASE WHEN n = 1 OR n % 2 = 0 THEN 'MR' ELSE 'MI' END,
                'POSTED',
                CONCAT('B', {batchStart} + n - 1),
                1, 0
            FROM nums;

            ;WITH brows AS (
                SELECT b.ID, b.BatchNo, b.TrxDtTime, b.TrxType,
                       ROW_NUMBER() OVER (ORDER BY b.BatchNo) AS rn
                FROM IvTrxBatch b
                WHERE b.CompanyCode='DEMO' AND b.BranchCode='HQ'
                  AND b.BatchNo BETWEEN {batchStart} AND {batchStart + count - 1}
            )
            INSERT INTO StockPosting (
                CompanyCode, BranchCode, LedgerEpochId, PostingSequence, RequestId, CommandType,
                RequestFingerprint, SourceModule, SourceDocumentType, SourceDocumentId, SourceDocumentNo,
                DocumentRevision, PostingRole, SourceSnapshotJson, SourceSnapshotHash, SourceSnapshotSchemaVersion,
                EffectiveAt, BusinessDate, PeriodKey, PostedAtUtc, PostedBy, SealedAtUtc
            )
            SELECT
                'DEMO', 'HQ', {epochId}, {seqStart} + rn - 1, NEWID(), CONCAT('IV_POST_', TrxType),
                REPLICATE('W', 64), 'INVENTORY',
                TrxType,
                CAST(ID AS nvarchar(64)),
                CAST(BatchNo AS nvarchar(50)),
                1, 'PRIMARY', CHAR(123) + CHAR(125), REPLICATE('H', 64), 1,
                TrxDtTime,
                CAST(TrxDtTime AS date),
                '2026-10', SYSUTCDATETIME(), 'BENCH', SYSUTCDATETIME()
            FROM brows;

            INSERT INTO IvTrxHistory (
                CompanyCode, BranchCode, BatchNo, TrxLineNo, TrxDtTime, TrxType, BatchStatus,
                RefNo, ICode,
                FromBalLocId, FrWarehouse, FrLocation, FrLot, FrStdQty, FrStdUOM,
                ToBalLocId, ToWarehouse, ToLocation, ToLot, ToStdQty, ToStdUOM,
                IStatus, Cost, UnitPrice, AsNowCost,
                LedgerVersion, LedgerEpochId, StockPostingId, PostingLineNo, DocumentRevision, EntryRole, ValuationStatus
            )
            SELECT
                'DEMO', 'HQ', CAST(p.SourceDocumentNo AS int), 1,
                p.EffectiveAt, p.SourceDocumentType, 'POSTED',
                CONCAT('B', p.SourceDocumentNo), '{itemCode}',
                CASE WHEN p.SourceDocumentType = 'MI' THEN {balLocId} ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MI' THEN 'MAIN' ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MI' THEN 'BIN1' ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MI' THEN '' ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MI' THEN 5.0000 ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MI' THEN 'EA' ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN {balLocId} ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN 'MAIN' ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN 'BIN1' ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN '' ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN CASE WHEN p.PostingSequence = {seqStart} THEN 500000.0000 ELSE 10.0000 END ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN 'EA' ELSE NULL END,
                'ACTIVE', 10.0000, 10.0000, 10.0000,
                2, {epochId}, p.Id, 1, 1, 'ORIGINAL', 'VALUED'
            FROM StockPosting p
            WHERE p.CompanyCode='DEMO' AND p.BranchCode='HQ' AND p.PostingSequence BETWEEN {seqStart} AND {seqStart + count - 1};

            INSERT INTO StockValuationFact (
                CompanyCode, BranchCode, LedgerEpochId, StockPostingId, PostingLineNo, SplitOrdinal,
                InventoryHistoryId, FromBalLocId, ToBalLocId,
                SourceLineId, SourceDocumentType, SourceDocumentId, SourceDocumentNo, SourceDocumentLine,
                EffectiveAt, BusinessDate, PeriodKey, ItemCode, WarehouseCode, LocationCode, LotNo, ItemStatus,
                BaseUom, MovementCode, Direction, BaseQty, CostMethod, UnitCost, CostAmount,
                BaseCurrency, BaseCostAmount, ValuationSource, ValuationStatus, ValuationVersion,
                CreatedAtUtc, CreatedBy
            )
            SELECT
                'DEMO', 'HQ', {epochId}, p.Id, 1, 0,
                h.ID, h.FromBalLocId, h.ToBalLocId,
                CONCAT(h.ID, ':1:0'), p.SourceDocumentType, p.SourceDocumentId, p.SourceDocumentNo, '1',
                p.EffectiveAt, p.BusinessDate, p.PeriodKey, '{itemCode}', 'MAIN', 'BIN1', '', 'ACTIVE',
                'EA',
                CASE WHEN p.SourceDocumentType = 'MR' THEN 'RECEIPT_IN' ELSE 'ISSUE_OUT' END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN 1 ELSE -1 END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN h.ToStdQty ELSE h.FrStdQty END,
                'MOVING_AVERAGE', 10.000000,
                (CASE WHEN p.SourceDocumentType = 'MR' THEN h.ToStdQty ELSE h.FrStdQty END) * 10.000000,
                'MYR',
                (CASE WHEN p.SourceDocumentType = 'MR' THEN h.ToStdQty ELSE h.FrStdQty END) * 10.000000,
                CASE WHEN p.SourceDocumentType = 'MR' THEN 'RECEIPT_ACTUAL' ELSE 'MOVING_AVERAGE' END,
                'VALUED', 1, SYSUTCDATETIME(), 'BENCH'
            FROM StockPosting p
            JOIN IvTrxHistory h ON h.StockPostingId = p.Id AND h.PostingLineNo = 1
            WHERE p.CompanyCode='DEMO' AND p.BranchCode='HQ' AND p.PostingSequence BETWEEN {seqStart} AND {seqStart + count - 1};

            UPDATE IvBalLoc
            SET StdQty = {finalQty.ToString(CultureInfo.InvariantCulture)}, TransDate = DATEADD(SECOND, {count} * 30, '2026-10-05T08:00:00')
            WHERE ID = {balLocId};

            DECLARE @lastWacFactId bigint = (
                SELECT MAX(f.Id) FROM StockValuationFact f
                JOIN StockPosting p ON p.Id = f.StockPostingId
                WHERE p.CompanyCode='DEMO' AND p.BranchCode='HQ' AND p.PostingSequence = {seqStart + count - 1}
            );
            INSERT INTO StockCostState (CompanyCode, BranchCode, ItemCode, CostMethod, OnHandBaseQty, InventoryValue, CurrentUnitCost, AverageUnitCost, LastValuationFactId, LastPostingSequence)
            VALUES ('DEMO', 'HQ', '{itemCode}', 'MOVING_AVERAGE', {finalQty.ToString(CultureInfo.InvariantCulture)}, {finalVal.ToString(CultureInfo.InvariantCulture)}, 10.000000, 10.000000, @lastWacFactId, {seqStart + count - 1});
            """);

        return (nextSeq + count, nextBatchNo + count);
    }

    private static async Task<(long NextSeq, int NextBatchNo)> SeedFifoTailAsync(
        SqlConnection conn, long epochId, string itemCode, int balLocId, int count,
        long nextSeq, int nextBatchNo)
    {
        // Pattern: n=1 -> MR +10000 qty @ 10.000000 (big open layer #1 that absorbs all odd MI issues of 5 qty!)
        // even n -> MR +10 @ 10.000000 (creates OPEN layer of 10);
        // odd n>1 -> MI -5 @ 10.000000 (consumes 5 from layer #1 cleanly!).
        long mrCount = 1 + count / 2;
        long miCount = (count - 1) / 2;
        decimal layer1Rem = 500000m - miCount * 5m;
        if (layer1Rem < 0m) throw new InvalidOperationException($"FIFO layer1 underflow for {itemCode}");
        decimal finalQty = layer1Rem + (mrCount - 1) * 10m;
        decimal finalVal = finalQty * 10m;
        var seqStart = nextSeq;
        var batchStart = nextBatchNo;

        await ExecAsync(conn, $"""
            ;WITH nums AS (
                SELECT n FROM dbo.__BenchTally WHERE n <= {count}
            )
            INSERT INTO IvTrxBatch (
                CompanyCode, BranchCode, BatchNo, TrxDtTime, TrxType, BatchStatus, RefNo, PostedCount, RollbackCount
            )
            SELECT
                'DEMO', 'HQ', {batchStart} + n - 1,
                DATEADD(SECOND, n * 30, '2026-10-05T08:00:00'),
                CASE WHEN n = 1 OR n % 2 = 0 THEN 'MR' ELSE 'MI' END,
                'POSTED',
                CONCAT('F', {batchStart} + n - 1),
                1, 0
            FROM nums;

            ;WITH brows AS (
                SELECT b.ID, b.BatchNo, b.TrxDtTime, b.TrxType,
                       ROW_NUMBER() OVER (ORDER BY b.BatchNo) AS rn
                FROM IvTrxBatch b
                WHERE b.CompanyCode='DEMO' AND b.BranchCode='HQ'
                  AND b.BatchNo BETWEEN {batchStart} AND {batchStart + count - 1}
            )
            INSERT INTO StockPosting (
                CompanyCode, BranchCode, LedgerEpochId, PostingSequence, RequestId, CommandType,
                RequestFingerprint, SourceModule, SourceDocumentType, SourceDocumentId, SourceDocumentNo,
                DocumentRevision, PostingRole, SourceSnapshotJson, SourceSnapshotHash, SourceSnapshotSchemaVersion,
                EffectiveAt, BusinessDate, PeriodKey, PostedAtUtc, PostedBy, SealedAtUtc
            )
            SELECT
                'DEMO', 'HQ', {epochId}, {seqStart} + rn - 1, NEWID(), CONCAT('IV_POST_', TrxType),
                REPLICATE('F', 64), 'INVENTORY',
                TrxType,
                CAST(ID AS nvarchar(64)),
                CAST(BatchNo AS nvarchar(50)),
                1, 'PRIMARY', CHAR(123) + CHAR(125), REPLICATE('H', 64), 1,
                TrxDtTime,
                CAST(TrxDtTime AS date),
                '2026-10', SYSUTCDATETIME(), 'BENCH', SYSUTCDATETIME()
            FROM brows;

            INSERT INTO IvTrxHistory (
                CompanyCode, BranchCode, BatchNo, TrxLineNo, TrxDtTime, TrxType, BatchStatus,
                RefNo, ICode,
                FromBalLocId, FrWarehouse, FrLocation, FrLot, FrStdQty, FrStdUOM,
                ToBalLocId, ToWarehouse, ToLocation, ToLot, ToStdQty, ToStdUOM,
                IStatus, Cost, UnitPrice, AsNowCost,
                LedgerVersion, LedgerEpochId, StockPostingId, PostingLineNo, DocumentRevision, EntryRole, ValuationStatus
            )
            SELECT
                'DEMO', 'HQ', CAST(p.SourceDocumentNo AS int), 1,
                p.EffectiveAt, p.SourceDocumentType, 'POSTED',
                CONCAT('F', p.SourceDocumentNo), '{itemCode}',
                CASE WHEN p.SourceDocumentType = 'MI' THEN {balLocId} ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MI' THEN 'MAIN' ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MI' THEN 'BIN1' ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MI' THEN '' ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MI' THEN 5.0000 ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MI' THEN 'EA' ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN {balLocId} ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN 'MAIN' ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN 'BIN1' ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN '' ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN CASE WHEN p.PostingSequence = {seqStart} THEN 500000.0000 ELSE 10.0000 END ELSE NULL END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN 'EA' ELSE NULL END,
                'ACTIVE', 10.0000, 10.0000, 10.0000,
                2, {epochId}, p.Id, 1, 1, 'ORIGINAL', 'VALUED'
            FROM StockPosting p
            WHERE p.CompanyCode='DEMO' AND p.BranchCode='HQ' AND p.PostingSequence BETWEEN {seqStart} AND {seqStart + count - 1};

            INSERT INTO StockValuationFact (
                CompanyCode, BranchCode, LedgerEpochId, StockPostingId, PostingLineNo, SplitOrdinal,
                InventoryHistoryId, FromBalLocId, ToBalLocId,
                SourceLineId, SourceDocumentType, SourceDocumentId, SourceDocumentNo, SourceDocumentLine,
                EffectiveAt, BusinessDate, PeriodKey, ItemCode, WarehouseCode, LocationCode, LotNo, ItemStatus,
                BaseUom, MovementCode, Direction, BaseQty, CostMethod, UnitCost, CostAmount,
                BaseCurrency, BaseCostAmount, ValuationSource, ValuationStatus, ValuationVersion,
                CreatedAtUtc, CreatedBy
            )
            SELECT
                'DEMO', 'HQ', {epochId}, p.Id, 1, 0,
                h.ID, h.FromBalLocId, h.ToBalLocId,
                CONCAT(h.ID, ':1:0'), p.SourceDocumentType, p.SourceDocumentId, p.SourceDocumentNo, '1',
                p.EffectiveAt, p.BusinessDate, p.PeriodKey, '{itemCode}', 'MAIN', 'BIN1', '', 'ACTIVE',
                'EA',
                CASE WHEN p.SourceDocumentType = 'MR' THEN 'RECEIPT_IN' ELSE 'ISSUE_OUT' END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN 1 ELSE -1 END,
                CASE WHEN p.SourceDocumentType = 'MR' THEN h.ToStdQty ELSE h.FrStdQty END,
                'FIFO', 10.000000,
                (CASE WHEN p.SourceDocumentType = 'MR' THEN h.ToStdQty ELSE h.FrStdQty END) * 10.000000,
                'MYR',
                (CASE WHEN p.SourceDocumentType = 'MR' THEN h.ToStdQty ELSE h.FrStdQty END) * 10.000000,
                CASE WHEN p.SourceDocumentType = 'MR' THEN 'RECEIPT_ACTUAL' ELSE 'FIFO' END,
                'VALUED', 1, SYSUTCDATETIME(), 'BENCH'
            FROM StockPosting p
            JOIN IvTrxHistory h ON h.StockPostingId = p.Id AND h.PostingLineNo = 1
            WHERE p.CompanyCode='DEMO' AND p.BranchCode='HQ' AND p.PostingSequence BETWEEN {seqStart} AND {seqStart + count - 1};

            INSERT INTO StockFifoLayer (
                CompanyCode, BranchCode, ItemCode, BaseUom, OriginValuationFactId, OriginStockPostingId,
                ReceiptEffectiveAt, OriginalQty, RemainingQty, OriginalValue, AccumulatedAdjustment,
                RemainingValue, CurrentUnitCost, SourceDocumentType, SourceDocumentNo, WarehouseCode, LotNo, Status
            )
            SELECT
                'DEMO', 'HQ', '{itemCode}', 'EA', f.Id, p.Id,
                p.EffectiveAt,
                f.BaseQty,
                CASE WHEN p.PostingSequence = {seqStart} THEN {layer1Rem.ToString(CultureInfo.InvariantCulture)} ELSE f.BaseQty END,
                f.BaseQty * 10.000000,
                0.000000,
                (CASE WHEN p.PostingSequence = {seqStart} THEN {layer1Rem.ToString(CultureInfo.InvariantCulture)} ELSE f.BaseQty END) * 10.000000,
                10.000000, 'MR', p.SourceDocumentNo, 'MAIN', '', 'OPEN'
            FROM StockPosting p
            JOIN StockValuationFact f ON f.StockPostingId = p.Id AND f.PostingLineNo = 1
            WHERE p.CompanyCode='DEMO' AND p.BranchCode='HQ'
              AND p.PostingSequence BETWEEN {seqStart} AND {seqStart + count - 1}
              AND p.SourceDocumentType = 'MR';

            DECLARE @layer1Id bigint = (
                SELECT l.Id FROM StockFifoLayer l
                JOIN StockPosting p ON p.Id = l.OriginStockPostingId
                WHERE p.CompanyCode='DEMO' AND p.BranchCode='HQ' AND p.PostingSequence = {seqStart}
            );

            INSERT INTO StockFifoLayerConsumption (
                CompanyCode, BranchCode, IssueValuationFactId, FifoLayerId, SplitOrdinal,
                ConsumedQty, ConsumedValue, CreatedAtUtc, CreatedBy
            )
            SELECT
                'DEMO', 'HQ', f.Id, @layer1Id, 0,
                5.000000, 50.000000, SYSUTCDATETIME(), 'BENCH'
            FROM StockPosting p
            JOIN StockValuationFact f ON f.StockPostingId = p.Id AND f.PostingLineNo = 1
            WHERE p.CompanyCode='DEMO' AND p.BranchCode='HQ'
              AND p.PostingSequence BETWEEN {seqStart} AND {seqStart + count - 1}
              AND p.SourceDocumentType = 'MI';

            UPDATE IvBalLoc
            SET StdQty = {finalQty.ToString(CultureInfo.InvariantCulture)}, TransDate = DATEADD(SECOND, {count} * 30, '2026-10-05T08:00:00')
            WHERE ID = {balLocId};

            DECLARE @lastFifoFactId bigint = (
                SELECT MAX(f.Id) FROM StockValuationFact f
                JOIN StockPosting p ON p.Id = f.StockPostingId
                WHERE p.CompanyCode='DEMO' AND p.BranchCode='HQ' AND p.PostingSequence = {seqStart + count - 1}
            );
            INSERT INTO StockCostState (CompanyCode, BranchCode, ItemCode, CostMethod, OnHandBaseQty, InventoryValue, CurrentUnitCost, AverageUnitCost, LastValuationFactId, LastPostingSequence)
            VALUES ('DEMO', 'HQ', '{itemCode}', 'FIFO', {finalQty.ToString(CultureInfo.InvariantCulture)}, {finalVal.ToString(CultureInfo.InvariantCulture)}, 10.000000, 10.000000, @lastFifoFactId, {seqStart + count - 1});
            """);

        return (nextSeq + count, nextBatchNo + count);
    }

    private static async Task<List<QueryPlanResult>> CaptureQueryPlansAsync(string benchConn, SeedSummary seedInfo)
    {
        var results = new List<QueryPlanResult>();
        var wac100Bal = seedInfo.ItemPileIds["WAC-100"];
        var wac10kBal = seedInfo.ItemPileIds["WAC-10K"];
        var fifo100Layer = await ScalarAsync<long>(await OpenConnAsync(benchConn),
            "SELECT MIN(Id) FROM StockFifoLayer WHERE CompanyCode='DEMO' AND BranchCode='HQ' AND ItemCode='FIFO-100'");

        var wac10kSampleHistory = await ScalarAsync<int>(await OpenConnAsync(benchConn),
            "SELECT MIN(ID) FROM IvTrxHistory WHERE CompanyCode='DEMO' AND BranchCode='HQ' AND ICode='WAC-100'");

        var queries = new (string Name, string Sql, (string Param, object Value)[] Params)[]
        {
            ("StockPosting.LatestSealed",
             "SELECT MAX(PostingSequence) FROM StockPosting WHERE CompanyCode = @p0 AND BranchCode = @p1 AND SealedAtUtc IS NOT NULL",
             new[] { ("@p0", (object)CompanyCode), ("@p1", BranchCode) }),

            ("PhysicalTimeline.ReadLater(N=100)",
             "SELECT ID, TrxDtTime, FromBalLocId, ToBalLocId, StockPostingID FROM IvTrxHistory WHERE CompanyCode = @p0 AND BranchCode = @p1 AND TrxDtTime >= @p2 AND (FromBalLocId = @p3 OR ToBalLocId = @p3)",
             new[] { ("@p0", (object)CompanyCode), ("@p1", BranchCode), ("@p2", BackdatedTrxDate.Date), ("@p3", wac100Bal) }),

            ("PhysicalTimeline.ReadLater(N=10K)",
             "SELECT ID, TrxDtTime, FromBalLocId, ToBalLocId, StockPostingID FROM IvTrxHistory WHERE CompanyCode = @p0 AND BranchCode = @p1 AND TrxDtTime >= @p2 AND (FromBalLocId = @p3 OR ToBalLocId = @p3)",
             new[] { ("@p0", (object)CompanyCode), ("@p1", BranchCode), ("@p2", BackdatedTrxDate.Date), ("@p3", wac10kBal) }),

            ("ValuationFact.ItemEffectiveAtSeek",
             "SELECT COUNT(*) FROM StockValuationFact WHERE CompanyCode = @p0 AND BranchCode = @p1 AND ItemCode = @p2 AND EffectiveAt >= @p3",
             new[] { ("@p0", (object)CompanyCode), ("@p1", BranchCode), ("@p2", "WAC-10K"), ("@p3", BackdatedTrxDate) }),

            ("ValuationFact.ByHistorySeek",
             "SELECT Id, StockPostingId, CostAmount FROM StockValuationFact WHERE CompanyCode = @p0 AND BranchCode = @p1 AND InventoryHistoryId = @p2",
             new[] { ("@p0", (object)CompanyCode), ("@p1", BranchCode), ("@p2", wac10kSampleHistory) }),

            ("ValuationFact.PeriodSnapshotSeek",
             "SELECT COUNT(*) FROM StockValuationFact WHERE CompanyCode = @p0 AND BranchCode = @p1 AND PeriodKey = @p2",
             new[] { ("@p0", (object)CompanyCode), ("@p1", BranchCode), ("@p2", "2026-10") }),

            ("FifoLayerConsumption.ByLayerSeek",
             "SELECT COUNT(*) FROM StockFifoLayerConsumption WHERE CompanyCode = @p0 AND BranchCode = @p1 AND FifoLayerId = @p2",
             new[] { ("@p0", (object)CompanyCode), ("@p1", BranchCode), ("@p2", fifo100Layer) })
        };

        foreach (var (name, sql, prms) in queries)
        {
            await using var conn = await OpenConnAsync(benchConn);
            var ioMessages = new List<string>();
            conn.InfoMessage += (_, e) => ioMessages.Add(e.Message);

            await ExecAsync(conn, "SET STATISTICS IO ON; SET STATISTICS XML ON;");
            string? planXml = null;
            var sw = Stopwatch.StartNew();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = 120;
                cmd.CommandText = sql;
                foreach (var (p, v) in prms)
                    cmd.Parameters.AddWithValue(p, v);
                await using var reader = await cmd.ExecuteReaderAsync();
                do
                {
                    while (await reader.ReadAsync())
                    {
                        if (reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase))
                            planXml = reader.GetString(0);
                    }
                }
                while (await reader.NextResultAsync());
            }
            sw.Stop();
            await ExecAsync(conn, "SET STATISTICS XML OFF; SET STATISTICS IO OFF;");

            var ops = ExtractRelOps(planXml);
            var logicalReads = ParseLogicalReads(ioMessages);
            var hasScan = ops.Any(o =>
                o.Contains("Clustered Index Scan", StringComparison.OrdinalIgnoreCase) ||
                o.Contains("Table Scan", StringComparison.OrdinalIgnoreCase));
            results.Add(new QueryPlanResult(name, sw.Elapsed.TotalMilliseconds, logicalReads, ops, hasScan));
        }

        return results;
    }

    private static async Task<List<PlannerScaleResult>> RunPlannerAndReaderScalingAsync(
        IDbContextFactory<AppDbContext> factory,
        string benchConn,
        QueryMetricsInterceptor interceptor,
        SeedSummary seedInfo)
    {
        var results = new List<PlannerScaleResult>();

        // Test WAC planner & validation under lock across N=100, 1000, 10000, 50000
        foreach (var (item, count) in new[] { ("WAC-100", 100), ("WAC-1K", 1000), ("WAC-10K", 10000), ("WAC-50K", 50000) })
        {
            await SetActiveCostPolicyAsync(benchConn, StockCostMethods.MovingAverage);
            var balId = seedInfo.ItemPileIds[item];
            long nextSeq;
            await using (var seqDb = await factory.CreateDbContextAsync())
            {
                nextSeq = (await seqDb.StockPostingBranchSequences.AsNoTracking()
                    .Where(x => x.CompanyCode == CompanyCode && x.BranchCode == BranchCode)
                    .Select(x => (long?)x.LastSequence)
                    .SingleOrDefaultAsync()) ?? 0L;
            }
            nextSeq++;

            // Pre-warm JIT once on smallest case only if needed
            GC.Collect();
            var allocBefore = GC.GetTotalAllocatedBytes(true);
            interceptor.Reset();
            var planSw = Stopwatch.StartNew();

            StockReplayPhysicalPlan physPlan;
            StockReplayWacInsertionPlan wacPlan;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var candidate = new StockReplayPhysicalEvent(
                    9_999_000 + count,
                    new StockReplayEventKey(BackdatedTrxDate, StockReplayEventKey.OrdinaryClass, nextSeq, $"H{(9_999_000 + count):D10}", 0),
                    SignedDelta: 5m);
                physPlan = await StockReplayPhysicalPlanner.BuildInsertionAsync(db, CompanyCode, BranchCode, balId, candidate);
                var wacCandidate = new StockReplayWacEvent(candidate.EventId, candidate.Key, 0m, 0m, 5m, 50m);
                wacPlan = await StockReplayWacInsertionPlanner.BuildAsync(db, CompanyCode, BranchCode, item, physPlan, wacCandidate);
            }
            planSw.Stop();
            var planQueries = interceptor.QueryCount;
            var planAllocMb = (GC.GetTotalAllocatedBytes(true) - allocBefore) / (1024.0 * 1024.0);

            // Simulate locked root validation under real BranchStockTransactionLock (rollback cleanly)
            interceptor.Reset();
            var lockSw = Stopwatch.StartNew();
            await using (var db = await factory.CreateDbContextAsync())
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                await new BranchStockTransactionLock().AcquireAsync(db, CompanyCode, BranchCode);
                // Measure ValidateWithinRootAsync + Hash check (physical validation under lock)
                var epoch = await db.StockLedgerEpochs.SingleAsync(x => x.CompanyCode == CompanyCode && x.BranchCode == BranchCode && x.Status == StockLedgerEpochStatuses.Active);
                var tailCheck = await StockReplayWacFinancialTailReader.ReadLaterAsync(db, CompanyCode, BranchCode, item, physPlan.Candidate.Key);
                if (tailCheck.LaterEvents.Count != wacPlan.Tail.LaterEvents.Count)
                    throw new InvalidOperationException("Tail drift during validation benchmark.");
                await tx.RollbackAsync();
            }
            lockSw.Stop();
            var valQueries = interceptor.QueryCount;

            Console.WriteLine($"[Bench] WAC_PLAN_AND_VAL {item} (N={count:N0}): plan={planSw.Elapsed.TotalMilliseconds:F1}ms, lockVal={lockSw.Elapsed.TotalMilliseconds:F1}ms, q={planQueries}/{valQueries}");
            results.Add(new PlannerScaleResult("WAC_PLAN_AND_VAL", item, count, planSw.Elapsed.TotalMilliseconds, lockSw.Elapsed.TotalMilliseconds, planQueries, valQueries, planAllocMb));
        }

        // Test FIFO active-layer reader & insertion planner across N=100, 1000, 10000
        await SetActiveCostPolicyAsync(benchConn, StockCostMethods.Fifo);
        foreach (var (item, count) in new[] { ("FIFO-100", 100), ("FIFO-1K", 1000), ("FIFO-10K", 10000) })
        {
            var balId = seedInfo.ItemPileIds[item];
            long nextSeq;
            await using (var seqDb = await factory.CreateDbContextAsync())
            {
                nextSeq = ((await seqDb.StockPostingBranchSequences.AsNoTracking()
                    .Where(x => x.CompanyCode == CompanyCode && x.BranchCode == BranchCode)
                    .Select(x => (long?)x.LastSequence)
                    .SingleOrDefaultAsync()) ?? 0L) + 1;
            }

            GC.Collect();
            var allocBefore = GC.GetTotalAllocatedBytes(true);
            interceptor.Reset();
            var planSw = Stopwatch.StartNew();

            StockReplayPhysicalPlan physPlan;
            StockReplayFifoInsertionPlan fifoPlan;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var candidate = new StockReplayPhysicalEvent(
                    8_888_000 + count,
                    new StockReplayEventKey(BackdatedTrxDate, StockReplayEventKey.OrdinaryClass, nextSeq, $"H{(8_888_000 + count):D10}", 0),
                    SignedDelta: 5m);
                physPlan = await StockReplayPhysicalPlanner.BuildInsertionAsync(db, CompanyCode, BranchCode, balId, candidate);
                fifoPlan = await StockReplayFifoInsertionPlanner.BuildAsync(db, CompanyCode, BranchCode, item, physPlan, 50m, "EA");
            }
            planSw.Stop();
            var planQueries = interceptor.QueryCount;
            var planAllocMb = (GC.GetTotalAllocatedBytes(true) - allocBefore) / (1024.0 * 1024.0);

            interceptor.Reset();
            var lockSw = Stopwatch.StartNew();
            await using (var db = await factory.CreateDbContextAsync())
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                await new BranchStockTransactionLock().AcquireAsync(db, CompanyCode, BranchCode);
                var snap = await StockReplayFifoActiveLayerReader.ReadAsync(db, CompanyCode, BranchCode, item);
                if (snap.SnapshotHash != fifoPlan.Snapshot.SnapshotHash)
                    throw new InvalidOperationException("FIFO snapshot drift during validation benchmark.");
                await tx.RollbackAsync();
            }
            lockSw.Stop();
            var valQueries = interceptor.QueryCount;

            Console.WriteLine($"[Bench] FIFO_PLAN_AND_VAL {item} (N={count:N0}): plan={planSw.Elapsed.TotalMilliseconds:F1}ms, lockVal={lockSw.Elapsed.TotalMilliseconds:F1}ms, q={planQueries}/{valQueries}");
            results.Add(new PlannerScaleResult("FIFO_PLAN_AND_VAL", item, count, planSw.Elapsed.TotalMilliseconds, lockSw.Elapsed.TotalMilliseconds, planQueries, valQueries, planAllocMb));
        }

        await SetActiveCostPolicyAsync(benchConn, StockCostMethods.MovingAverage);
        return results;
    }

    private static async Task<List<EndToEndReplayResult>> RunEndToEndPostingBenchmarksAsync(
        IDbContextFactory<AppDbContext> factory,
        string benchConn,
        QueryMetricsInterceptor interceptor,
        SeedSummary seedInfo)
    {
        var results = new List<EndToEndReplayResult>();

        var wacCases = new (string Item, int Tail, bool ExpectedSuccess)[]
        {
            ("WAC-10", 10, true),
            ("WAC-50", 50, true),
            ("WAC-100", 100, true),
            ("WAC-101", 101, false),
            ("WAC-1K", 1000, false)
        };
        var fifoCases = new (string Item, int Tail, bool ExpectedSuccess)[]
        {
            ("FIFO-10", 10, true),
            ("FIFO-50", 50, true),
            ("FIFO-100", 100, true),
            ("FIFO-101", 101, false),
            ("FIFO-1K", 1000, false)
        };

        await SetActiveCostPolicyAsync(benchConn, StockCostMethods.MovingAverage);
        foreach (var (item, tail, expectedOk) in wacCases)
            results.Add(await RunSingleEndToEndCaseAsync(factory, benchConn, interceptor, "WAC", item, tail, expectedOk));

        await SetActiveCostPolicyAsync(benchConn, StockCostMethods.Fifo);
        foreach (var (item, tail, expectedOk) in fifoCases)
            results.Add(await RunSingleEndToEndCaseAsync(factory, benchConn, interceptor, "FIFO", item, tail, expectedOk));

        await SetActiveCostPolicyAsync(benchConn, StockCostMethods.MovingAverage);
        return results;
    }

    private static async Task<EndToEndReplayResult> RunSingleEndToEndCaseAsync(
        IDbContextFactory<AppDbContext> factory,
        string benchConn,
        QueryMetricsInterceptor interceptor,
        string method,
        string itemCode,
        int tail,
        bool expectedSuccess)
    {
        var measLock = new MeasuringBranchStockLock();
        var posting = CreateInventoryPosting(factory, measLock);
        var mr = CreateMiscReceipt(factory, BenchmarkToday, posting);

        var save = await mr.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = BackdatedTrxDate,
            Lines =
            [
                new IvMiscReceiptLineRequest
                {
                    ICode = itemCode, ToWarehouse = "MAIN", ToLocation = "BIN1",
                    Quantity = 5m, Uom = "EA", UnitPrice = 20m, PriceConfirmed = true,
                    IClassCode = "RAW", IStatus = "ACTIVE", Reason = "BENCH"
                }
            ]
        });
        if (!save.Succeeded)
            throw new InvalidOperationException($"SaveNewAsync failed for {itemCode}: {save.ErrorMessage}");

        GC.Collect();
        var allocBefore = GC.GetTotalAllocatedBytes(true);
        interceptor.Reset();
        measLock.Reset();

        // Snapshot cumulative SQL logical reads before call via sys.dm_exec_query_stats / connection session?
        // Using sys.dm_exec_session_wait_stats/requests is per-connection; EF queries use pooled connections.
        // We capture exact EF query count, total elapsed ms, exact lock-hold ms, and pre-lock planning ms = Total - LockHold!
        var totalSw = Stopwatch.StartNew();
        var post = await mr.PostAsync([save.BatchNo]);
        totalSw.Stop();

        var totalMs = totalSw.Elapsed.TotalMilliseconds;
        var lockAcquiredAt = measLock.LastLockAcquiredUtc;
        var lockWaitMs = measLock.LastWaitMs;
        var lockHoldMs = measLock.LastAcquireCount > 0 ? Math.Max(0, (DateTime.UtcNow - lockAcquiredAt).TotalMilliseconds) : 0;
        var planningMs = measLock.LastAcquireCount > 0 ? Math.Max(0, totalMs - lockHoldMs - lockWaitMs) : totalMs;
        var queries = interceptor.QueryCount;
        var allocMb = (GC.GetTotalAllocatedBytes(true) - allocBefore) / (1024.0 * 1024.0);

        // Verify post-commit reconciliation on expectedSuccess (or zero mutation on refusal)
        await using (var checkDb = await factory.CreateDbContextAsync())
        {
            var batchStatus = await checkDb.IvTrxBatches.AsNoTracking()
                .Where(b => b.CompanyCode == CompanyCode && b.BranchCode == BranchCode && b.BatchNo == save.BatchNo)
                .Select(b => b.BatchStatus)
                .SingleAsync();
            if (expectedSuccess && batchStatus != IvBatchStatuses.Posted)
                throw new InvalidOperationException($"{itemCode}: expected POSTED, got {batchStatus}: {post.ErrorMessage}");
            if (!expectedSuccess && batchStatus != IvBatchStatuses.New)
                throw new InvalidOperationException($"{itemCode}: refused batch mutated status to {batchStatus}");

            if (expectedSuccess)
            {
                var balQty = await checkDb.IvBalLocs.AsNoTracking()
                    .Where(b => b.CompanyCode == CompanyCode && b.BranchCode == BranchCode && b.ICode == itemCode)
                    .SumAsync(b => b.StdQty);
                var state = await checkDb.StockCostStates.AsNoTracking()
                    .SingleAsync(s => s.CompanyCode == CompanyCode && s.BranchCode == BranchCode && s.ItemCode == itemCode && s.CostMethod == (method == "WAC" ? StockCostMethods.MovingAverage : StockCostMethods.Fifo));
                if (balQty != state.OnHandBaseQty || state.InventoryValue <= 0m)
                    throw new InvalidOperationException($"{itemCode}: post-replay balance/cost mismatch.");
            }
        }

        // Cleanup refused NEW batch so period/pending state stays clean
        if (!post.Succeeded)
        {
            await using var conn = await OpenConnAsync(benchConn);
            await ExecAsync(conn, $"DELETE d FROM IvTrxBatchDetail d JOIN IvTrxBatch b ON b.ID = d.BatchId WHERE b.BatchNo = {save.BatchNo} AND b.CompanyCode='DEMO' AND b.BranchCode='HQ'; DELETE FROM IvTrxBatch WHERE BatchNo = {save.BatchNo} AND CompanyCode='DEMO' AND BranchCode='HQ';");
        }

        return new EndToEndReplayResult(method, itemCode, tail, expectedSuccess, post.Succeeded, planningMs, lockHoldMs, totalMs, queries, allocMb, post.Succeeded ? "POSTED" : post.ErrorMessage ?? "REFUSED");
    }

    private static async Task<ContentionSummary> RunCompetingWriterBenchmarkAsync(
        IDbContextFactory<AppDbContext> factory,
        string benchConn,
        QueryMetricsInterceptor interceptor)
    {
        var lockEnteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var measReplayLock = new MeasuringBranchStockLock(() => lockEnteredTcs.TrySetResult());
        var replayPosting = CreateInventoryPosting(factory, measReplayLock);
        var replayMr = CreateMiscReceipt(factory, BenchmarkToday, replayPosting);
        var replaySave = await replayMr.SaveNewAsync(new IvMiscReceiptSaveRequest
        {
            TrxDate = BackdatedTrxDate,
            Lines = [new() { ICode = "WAC-100-CONT", ToWarehouse = "MAIN", ToLocation = "BIN1", Quantity = 5m, Uom = "EA", UnitPrice = 20m, PriceConfirmed = true, IClassCode = "RAW", IStatus = "ACTIVE", Reason = "CONT" }]
        });

        // Pre-create 3 competing normal MR drafts on WAC-COMPETE
        var compLocks = Enumerable.Range(0, 3).Select(_ => new MeasuringBranchStockLock()).ToArray();
        var compMrs = compLocks.Select(l => CreateMiscReceipt(factory, BenchmarkToday, CreateInventoryPosting(factory, l))).ToArray();
        var compBatches = new int[3];
        for (var i = 0; i < 3; i++)
        {
            var s = await compMrs[i].SaveNewAsync(new IvMiscReceiptSaveRequest
            {
                TrxDate = BenchmarkToday,
                Lines = [new() { ICode = "WAC-COMPETE", ToWarehouse = "MAIN", ToLocation = "BIN1", Quantity = 1m, Uom = "EA", UnitPrice = 10m, PriceConfirmed = true, IClassCode = "RAW", IStatus = "ACTIVE", Reason = "CONT" }]
            });
            compBatches[i] = s.BatchNo;
        }

        // Launch 100-event replay, then launch 3 competing branch writers immediately once sp_getapplock is held
        var replayTask = Task.Run(() => replayMr.PostAsync([replaySave.BatchNo]));
        await lockEnteredTcs.Task;
        var writerTasks = Enumerable.Range(0, 3)
            .Select(i => Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                var res = await compMrs[i].PostAsync([compBatches[i]]);
                sw.Stop();
                return (Succeeded: res.Succeeded, WaitMs: compLocks[i].LastWaitMs, TotalMs: sw.Elapsed.TotalMilliseconds, Error: res.ErrorMessage);
            }))
            .ToArray();

        var replayRes = await replayTask;
        var replayHoldMs = (DateTime.UtcNow - measReplayLock.LastLockAcquiredUtc).TotalMilliseconds;
        var writerOutcomes = await Task.WhenAll(writerTasks);
        for (var i = 0; i < writerOutcomes.Length; i++)
            Console.WriteLine($"[Bench] Contention Writer[{i}]: ok={writerOutcomes[i].Succeeded}, acq={compLocks[i].LastAcquireCount}, wait={writerOutcomes[i].WaitMs:F1}ms, total={writerOutcomes[i].TotalMs:F1}ms, err={writerOutcomes[i].Error}");
        Console.WriteLine($"[Bench] Contention Replay: ok={replayRes.Succeeded}, acq={measReplayLock.LastAcquireCount}, approxHold={replayHoldMs:F1}ms, err={replayRes.ErrorMessage}");
        var maxWriterWaitMs = writerOutcomes.Max(w => w.WaitMs);
        var timeouts = writerOutcomes.Count(w => !w.Succeeded && (w.Error?.Contains("in progress", StringComparison.OrdinalIgnoreCase) ?? false));
        var headroomPct = (BranchStockTransactionLock.DefaultTimeoutMs - maxWriterWaitMs) / BranchStockTransactionLock.DefaultTimeoutMs * 100.0;

        return new ContentionSummary(
            replayRes.Succeeded,
            BranchStockTransactionLock.DefaultTimeoutMs,
            maxWriterWaitMs,
            writerOutcomes.Average(w => w.TotalMs),
            timeouts,
            headroomPct);
    }

    private static async Task SetActiveCostPolicyAsync(string benchConn, string method)
    {
        await using var conn = await OpenConnAsync(benchConn);
        await ExecAsync(conn, $"""
            UPDATE StockCostPolicyRevision
            SET CostMethod = '{method}'
            WHERE CompanyCode = 'DEMO' AND BranchCode = 'HQ' AND Status = 'ACTIVE';
            """);
    }

    private static IvInventoryPostingService CreateInventoryPosting(
        IDbContextFactory<AppDbContext> factory,
        IBranchStockTransactionLock branchLock)
    {
        var tenant = new BenchTenantContext(CompanyCode, BranchCode);
        var access = new BenchAllowAccess();
        var coordinator = new StockPostingCoordinator(
            factory, tenant, branchLock,
            new StockPeriodGuard(), new NoActiveStockFreezeGuard(),
            new InventoryValuationService());

        return new IvInventoryPostingService(
            factory, tenant, access,
            new IvStockPostingRepository(),
            new IvStockCommonRepository(factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance,
            new FixedCurrentDateService(DateTime.MaxValue.Date),
            historyWriter: new IvInventoryHistoryWriter(),
            coordinator: coordinator,
            configuration: new ConfigurationManager { ["OpenPeriodBackdatedReplayEnabled"] = "true" },
            approval: DisabledProcurementApprovalGuard.Instance);
    }

    private static IvMiscReceiptService CreateMiscReceipt(
        IDbContextFactory<AppDbContext> factory,
        DateTime today,
        IIvInventoryPostingService posting)
    {
        var tenant = new BenchTenantContext(CompanyCode, BranchCode);
        var access = new BenchAllowAccess();
        return new IvMiscReceiptService(
            factory, tenant, access, new RunningNumberService(),
            new FixedCurrentDateService(today),
            new IvStockMasterRepository(factory),
            new IvStockCommonRepository(factory),
            new IvStockTransactionRepository(),
            new IvStockPostingRepository(),
            posting,
            new PoSupplierRepository(factory),
            new IvUomConversionService(factory),
            NullLogger<IvMiscReceiptService>.Instance);
    }

    private static async Task<SqlConnection> OpenConnAsync(string cs)
    {
        var c = new SqlConnection(cs);
        await c.OpenAsync();
        return c;
    }

    private static async Task ExecAsync(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = sql;
        var res = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(res!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static List<string> ExtractRelOps(string? planXml)
    {
        if (string.IsNullOrWhiteSpace(planXml)) return [];
        var doc = XDocument.Parse(planXml);
        var ops = new List<string>();
        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "RelOp"))
        {
            var phys = el.Attribute("PhysicalOp")?.Value ?? "";
            var log = el.Attribute("LogicalOp")?.Value ?? "";
            var est = el.Attribute("EstimateRows")?.Value ?? "";
            var obj = el.Descendants().FirstOrDefault(c => c.Name.LocalName == "Object");
            var tbl = obj?.Attribute("Table")?.Value ?? "";
            var idx = obj?.Attribute("Index")?.Value ?? "";
            var act = el.Descendants().Where(c => c.Name.LocalName == "RunTimeCountersPerThread").Sum(c => long.TryParse(c.Attribute("ActualRows")?.Value, out var r) ? r : 0);
            if (!string.IsNullOrEmpty(tbl) || phys is "Clustered Index Scan" or "Index Seek" or "Clustered Index Seek" or "Index Scan" or "Table Scan")
                ops.Add($"{phys} {tbl}{idx} (est={est}, act={act})");
        }
        return ops.Distinct().ToList();
    }

    private static long ParseLogicalReads(List<string> infoMessages)
    {
        long total = 0;
        foreach (var msg in infoMessages)
        {
            var marker = "logical reads ";
            var idx = 0;
            while ((idx = msg.IndexOf(marker, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                idx += marker.Length;
                var end = idx;
                while (end < msg.Length && char.IsDigit(msg[end])) end++;
                if (long.TryParse(msg[idx..end], out var r)) total += r;
            }
        }
        return total;
    }

    private static string FormatMarkdownReport(BenchmarkReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Replay Volume, Query Plan & Lock-Budget Benchmark ({report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss} UTC)");
        sb.AppendLine($"- **SQL Server:** `{report.SqlServer}` (disposable `{report.DatabaseName}`, dropped after run)");
        sb.AppendLine($"- **Unrelated Background Volume:** **{report.Seed.IvTrxHistoryRows:N0}** `IvTrxHistory` + **{report.Seed.StockValuationFactRows:N0}** `StockValuationFact` + **{report.Seed.StockPostingRows:N0}** `StockPosting` + **{report.Seed.StockFifoLayerRows:N0}** `StockFifoLayer` + **{report.Seed.StockFifoConsumptionRows:N0}** `StockFifoLayerConsumption` (**{report.Seed.TotalDataMb:F1} MB** data, **{report.Seed.TotalLogMb:F1} MB** log, seeded in **{report.SeedSeconds:F1}s**)");
        sb.AppendLine();
        sb.AppendLine("## 1. Actual SQL Server Execution Plans (on 5M-Row Database)");
        sb.AppendLine("| Query | Elapsed ms | Logical Reads | Full/Clustered Scan? | Operators |");
        sb.AppendLine("| --- | ---: | ---: | --- | --- |");
        foreach (var q in report.QueryPlans)
            sb.AppendLine($"| `{q.Name}` | {q.ElapsedMs:F2} | {q.LogicalReads:N0} | {(q.HasFullScan ? "YES (FAIL)" : "NO (Seek)")} | `{string.Join("; ", q.Operators)}` |");
        sb.AppendLine();
        sb.AppendLine("## 2. Planner & Locked Validation Scaling (100 / 1,000 / 10,000 / 50,000 Events)");
        sb.AppendLine("| Workload | Item | Tail Events | Planning ms | Lock-Validation ms | Plan Queries | Lock Queries | Alloc MB |");
        sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var p in report.PlannerScaling)
            sb.AppendLine($"| `{p.Workload}` | `{p.ItemCode}` | {p.TailCount:N0} | {p.PlanningMs:F1} | {p.LockedValidationMs:F1} | {p.PlanQueries} | {p.LockQueries} | {p.AllocatedMb:F2} |");
        sb.AppendLine();
        sb.AppendLine("## 3. End-to-End Public `IvInventoryPostingService.PostAsync` (Backdated MR with 100-Event Online Cap)");
        sb.AppendLine("| Method | Item | Tail Events | Expected | Actual | Planning ms | Lock-Hold ms | Total ms | Queries | Alloc MB | Outcome |");
        sb.AppendLine("| --- | --- | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (var e in report.EndToEnd)
            sb.AppendLine($"| `{e.Method}` | `{e.ItemCode}` | {e.TailCount:N0} | {(e.ExpectedSuccess ? "PASS" : "REFUSE")} | {(e.Succeeded ? "PASS" : "REFUSED")} | {e.PlanningMs:F1} | {e.LockHoldMs:F1} | {e.TotalMs:F1} | {e.Queries} | {e.AllocatedMb:F2} | {e.Note} |");
        sb.AppendLine();
        sb.AppendLine("## 4. Competing-Writer Contention vs 15,000 ms `sp_getapplock` Acquisition Timeout");
        sb.AppendLine($"- **100-Event Concurrent Replay Succeeded:** `{report.Contention.ReplaySucceeded}`");
        sb.AppendLine($"- **Configured `sp_getapplock` Acquisition Timeout:** `{report.Contention.ConfiguredLockTimeoutMs:N0} ms`");
        sb.AppendLine($"- **Max Competing Writer Lock Wait:** **{report.Contention.MaxCompetingWriterWaitMs:F1} ms** (mean writer total: **{report.Contention.MeanCompetingWriterTotalMs:F1} ms**, timeouts: **{report.Contention.TimeoutCount}**, headroom below 15s: **{report.Contention.HeadroomPercent:F1}%**)");
        return sb.ToString();
    }

    private sealed class BenchDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class QueryMetricsInterceptor : DbCommandInterceptor
    {
        private long _count;
        public int QueryCount => (int)Interlocked.Read(ref _count);
        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Interlocked.Increment(ref _count); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { Interlocked.Increment(ref _count); return ValueTask.FromResult(result); }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        { Interlocked.Increment(ref _count); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { Interlocked.Increment(ref _count); return ValueTask.FromResult(result); }
        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        { Interlocked.Increment(ref _count); return result; }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default)
        { Interlocked.Increment(ref _count); return ValueTask.FromResult(result); }
    }

    private sealed class MeasuringBranchStockLock(Action? onAcquired = null) : IBranchStockTransactionLock
    {
        private readonly BranchStockTransactionLock _inner = new();
        public int LastAcquireCount { get; private set; }
        public double LastWaitMs { get; private set; }
        public DateTime LastLockAcquiredUtc { get; private set; }
        public void Reset() { LastAcquireCount = 0; LastWaitMs = 0; }

        public async Task AcquireAsync(AppDbContext db, string companyCode, string branchCode, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            await _inner.AcquireAsync(db, companyCode, branchCode, cancellationToken);
            sw.Stop();
            LastAcquireCount++;
            LastWaitMs = sw.Elapsed.TotalMilliseconds;
            LastLockAcquiredUtc = DateTime.UtcNow;
            onAcquired?.Invoke();
        }
    }

    private sealed class BenchTenantContext(string company, string branch) : IInventoryTenantContext
    {
        private readonly InventoryTenantScope _scope = new()
        {
            CompanyCode = company,
            BranchCode = branch,
            LocationCode = "MAIN",
            UserId = "BENCH"
        };

        public InventoryTenantScope? TryCompanyScope() => _scope;
        public InventoryTenantScope? TryBranchScope() => _scope;
        public InventoryTenantScope? TryWriteScope() => _scope;
    }

    private sealed class BenchAllowAccess : IAccessRightService
    {
        private static readonly IReadOnlySet<string> AllPerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PermissionCodes.Access, PermissionCodes.Add,
            PermissionCodes.Edit, PermissionCodes.Delete, PermissionCodes.Post,
            PermissionCodes.Rollback, PermissionCodes.Close, PermissionCodes.Reopen
        };

        public Task<bool> CanAccessAsync(string menuCode, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CanAsync(string menuCode, string permissionCode, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlySet<string>> GetPermissionsAsync(string menuCode, CancellationToken cancellationToken = default) => Task.FromResult(AllPerms);
        public Task<MenuAccessRight?> GetAccessAsync(string menuCode, CancellationToken cancellationToken = default) => Task.FromResult<MenuAccessRight?>(null);
        public Task<IReadOnlyDictionary<string, MenuAccessRight>> GetAllAccessAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, MenuAccessRight>>(new Dictionary<string, MenuAccessRight>());
        public Task RefreshPermissionsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedCurrentDateService(DateTime today) : ICurrentDateService
    {
        public DateTime Today => today.Date;
        public DateTime Now => today;
    }

    public sealed record SeedSummary(
        long IvTrxHistoryRows, long StockValuationFactRows, long StockPostingRows,
        long StockFifoLayerRows, long StockFifoConsumptionRows,
        double TotalDataMb, double TotalLogMb,
        Dictionary<string, int> ItemPileIds);

    public sealed record QueryPlanResult(string Name, double ElapsedMs, long LogicalReads, List<string> Operators, bool HasFullScan);
    public sealed record PlannerScaleResult(string Workload, string ItemCode, int TailCount, double PlanningMs, double LockedValidationMs, int PlanQueries, int LockQueries, double AllocatedMb);
    public sealed record EndToEndReplayResult(string Method, string ItemCode, int TailCount, bool ExpectedSuccess, bool Succeeded, double PlanningMs, double LockHoldMs, double TotalMs, int Queries, double AllocatedMb, string Note);
    public sealed record ContentionSummary(bool ReplaySucceeded, int ConfiguredLockTimeoutMs, double MaxCompetingWriterWaitMs, double MeanCompetingWriterTotalMs, int TimeoutCount, double HeadroomPercent);
    public sealed record BenchmarkReport(DateTime GeneratedAtUtc, string SqlServer, string DatabaseName, int TargetBackgroundRows, double SeedSeconds, SeedSummary Seed, List<QueryPlanResult> QueryPlans, List<PlannerScaleResult> PlannerScaling, List<EndToEndReplayResult> EndToEnd, ContentionSummary Contention);
}
