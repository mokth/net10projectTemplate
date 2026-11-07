using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ErpWeb.Core.Quality;

public sealed class QaFeaturePair
{
    public required string Name { get; init; }
    public bool IsReady { get; init; }
    public bool IsAbsent { get; init; }
    public bool AnyPresent { get; init; }
    public string? Message { get; init; }
}

public readonly record struct QaFeatureDecision(bool Skip, string? Error, bool CanQuery);

public sealed class QaSchemaReadinessResult
{
    public bool ProbeFailed { get; init; }
    public bool AllAllowlistedAbsent { get; init; }
    public string Provenance { get; init; } = QaSchemaProvenance.Unknown;
    public string? UserMessage { get; init; }
    public bool ShouldSkipAllQaQueries => !ProbeFailed && AllAllowlistedAbsent;
    public QaFeaturePair Foundation { get; init; } = AbsentPair("foundation");
    public QaFeaturePair Incoming { get; init; } = AbsentPair("incoming");
    public QaFeaturePair FinalFg { get; init; } = AbsentPair("finished-goods");
    public QaFeaturePair Logical { get; init; } = AbsentPair("logical hold");
    public QaFeaturePair Physical { get; init; } = AbsentPair("physical hold");
    public QaFeaturePair NonGood { get; init; } = AbsentPair("non-good");
    public bool CanQueryPolicy => Foundation.IsReady;
    public bool CanQueryCaseGraph =>
        Foundation.IsReady
        && Incoming.IsReady
        && FinalFg.IsReady
        && Logical.IsReady
        && Physical.IsReady
        && NonGood.IsReady;

    public QaFeatureDecision ForFeature(QaFeaturePair pair)
    {
        if (ProbeFailed)
        {
            return new(false, UserMessage ?? QaReadinessMessages.ProbeError, false);
        }

        if (AllAllowlistedAbsent)
        {
            return new(true, null, false);
        }

        if (!Foundation.IsReady)
        {
            return new(false, Foundation.Message ?? QaReadinessMessages.Incomplete, false);
        }

        if (pair.IsReady)
        {
            return new(false, null, true);
        }

        return new(false, pair.Message ?? QaReadinessMessages.Incomplete, false);
    }

    public string? RequireCaseGraph()
    {
        if (ProbeFailed)
        {
            return UserMessage ?? QaReadinessMessages.ProbeError;
        }

        if (AllAllowlistedAbsent)
        {
            return QaReadinessMessages.NotInstalled;
        }

        return CanQueryCaseGraph ? null : UserMessage ?? QaReadinessMessages.Incomplete;
    }

    internal static QaFeaturePair AbsentPair(string name) => new()
    {
        Name = name,
        IsReady = false,
        IsAbsent = true,
        AnyPresent = false,
        Message = QaReadinessMessages.Incomplete
    };
}

public static class QaSchemaReadiness
{
    public static readonly IReadOnlyList<string> AllowlistedTables =
    [
        "QaPolicy",
        "QaCase",
        "QaCaseLine",
        "QaInspection",
        "QaDispositionLine",
        "QaAuditEvent",
        "QaReceiptGrant",
        "QaReceiptAllocation",
        "QaFgGrant",
        "QaFgAllocation",
        "QaLogicalHold",
        "QaPhysicalHold",
        "QaPhysicalAllocation",
        "QaNonGoodGrant",
        "QaNonGoodAllocation"
    ];

    private static readonly string[] FoundationTables =
    [
        "QaPolicy", "QaCase", "QaCaseLine", "QaInspection", "QaDispositionLine", "QaAuditEvent"
    ];

    private static readonly (string Table, string Column)[] RequiredColumns =
    [
        ("QaPolicy", "CompanyCode"),
        ("QaPolicy", "Version"),
        ("QaPolicy", "IsPublished"),
        ("QaPolicy", "IncomingInspectionEnabled"),
        ("QaPolicy", "FinalFgInspectionEnabled"),
        ("QaPolicy", "LogicalHoldEnabled"),
        ("QaPolicy", "PhysicalHoldEnabled"),
        ("QaPolicy", "NonGoodEnabled"),
        ("QaPolicy", "InspectorApproverSeparation"),
        ("QaCase", "CompanyCode"),
        ("QaCase", "BranchCode"),
        ("QaCase", "CaseNo"),
        ("QaCase", "Status"),
        ("QaCase", "DeliveryInstanceId"),
        ("QaReceiptGrant", "CompanyCode"),
        ("QaReceiptGrant", "BranchCode"),
        ("QaReceiptGrant", "Status"),
        ("QaReceiptGrant", "AcceptedQty"),
        ("QaReceiptGrant", "CaseLineId"),
        ("QaReceiptAllocation", "CompanyCode"),
        ("QaReceiptAllocation", "BranchCode"),
        ("QaReceiptAllocation", "Status"),
        ("QaReceiptAllocation", "Qty"),
        ("QaReceiptAllocation", "BatchNo"),
        ("QaReceiptAllocation", "BatchDetailId"),
        ("QaReceiptAllocation", "GrantId"),
        ("QaFgGrant", "CompanyCode"),
        ("QaFgGrant", "BranchCode"),
        ("QaFgGrant", "Status"),
        ("QaFgGrant", "AcceptedQty"),
        ("QaFgGrant", "ProductionBalLotId"),
        ("QaFgAllocation", "CompanyCode"),
        ("QaFgAllocation", "BranchCode"),
        ("QaFgAllocation", "Status"),
        ("QaFgAllocation", "Qty"),
        ("QaFgAllocation", "BatchId"),
        ("QaFgAllocation", "SourceId"),
        ("QaFgAllocation", "GrantId"),
        ("QaLogicalHold", "CompanyCode"),
        ("QaLogicalHold", "BranchCode"),
        ("QaLogicalHold", "Status"),
        ("QaLogicalHold", "ReservedBaseQty"),
        ("QaLogicalHold", "ProductionBalLotId"),
        ("QaPhysicalHold", "CompanyCode"),
        ("QaPhysicalHold", "BranchCode"),
        ("QaPhysicalHold", "Status"),
        ("QaPhysicalHold", "ReservedBaseQty"),
        ("QaPhysicalHold", "BalLocId"),
        ("QaPhysicalAllocation", "CompanyCode"),
        ("QaPhysicalAllocation", "BranchCode"),
        ("QaPhysicalAllocation", "Status"),
        ("QaPhysicalAllocation", "Qty"),
        ("QaPhysicalAllocation", "BatchNo"),
        ("QaPhysicalAllocation", "HoldId"),
        ("QaNonGoodGrant", "CompanyCode"),
        ("QaNonGoodGrant", "BranchCode"),
        ("QaNonGoodGrant", "Status"),
        ("QaNonGoodGrant", "AcceptedQty"),
        ("QaNonGoodGrant", "ProductionOutputId"),
        ("QaNonGoodAllocation", "CompanyCode"),
        ("QaNonGoodAllocation", "BranchCode"),
        ("QaNonGoodAllocation", "Status"),
        ("QaNonGoodAllocation", "Qty"),
        ("QaNonGoodAllocation", "ProductionOutputId"),
        ("QaNonGoodAllocation", "GrantId")
    ];

    private static readonly (string Index, string Table)[] RequiredIndexes =
    [
        ("UX_QaPolicy_Company_Version", "QaPolicy"),
        ("UX_QaPolicy_Company_Published", "QaPolicy"),
        ("UX_QaCase_Company_Branch_No", "QaCase"),
        ("UX_QaCase_DeliveryInstance", "QaCase"),
        ("IX_QaCase_Workbench", "QaCase"),
        ("UX_QaCaseLine_Case_LineNo", "QaCaseLine"),
        ("UX_QaInspection_CaseLine", "QaInspection"),
        ("UX_QaDisposition_Line_Kind", "QaDispositionLine"),
        ("IX_QaAuditEvent_Case_At", "QaAuditEvent"),
        ("UX_QaReceiptGrant_CaseLine", "QaReceiptGrant"),
        ("UX_QaReceiptAllocation_Posted_Grant_Detail", "QaReceiptAllocation"),
        ("UX_QaFgGrant_CaseLine", "QaFgGrant"),
        ("UX_QaFgAllocation_Posted_Grant_Source", "QaFgAllocation"),
        ("UX_QaLogicalHold_CaseLine", "QaLogicalHold"),
        ("UX_QaPhysicalHold_CaseLine", "QaPhysicalHold"),
        ("UX_QaPhysicalAllocation_Posted_Hold_Detail", "QaPhysicalAllocation"),
        ("UX_QaNonGoodGrant_CaseLine", "QaNonGoodGrant"),
        ("UX_QaNonGoodAllocation_Posted_Grant_Output", "QaNonGoodAllocation")
    ];

    private static readonly ConditionalWeakTable<AppDbContext, StrongBox<QaSchemaReadinessResult>> Cache = new();

    public static async Task<QaSchemaReadinessResult> EvaluateAsync(
        AppDbContext db,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        lock (Cache)
        {
            if (Cache.TryGetValue(db, out var cached) && cached.Value is not null)
            {
                return cached.Value;
            }
        }

        var result = await ProbeAsync(db, cancellationToken);
        lock (Cache)
        {
            var box = Cache.GetValue(db, _ => new StrongBox<QaSchemaReadinessResult>());
            box.Value = result;
        }

        return result;
    }

    public static async Task<string?> RequireCaseGraphAsync(
        AppDbContext db,
        CancellationToken cancellationToken = default)
    {
        var ready = await EvaluateAsync(db, cancellationToken);
        return ready.RequireCaseGraph();
    }

    private static async Task<QaSchemaReadinessResult> ProbeAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        try
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            if (db.Database.CurrentTransaction is not null)
            {
                command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
            }

            if (db.Database.IsSqlServer())
            {
                return await ProbeSqlServerAsync(command, cancellationToken);
            }

            return await ProbeSqliteAsync(command, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new QaSchemaReadinessResult
            {
                ProbeFailed = true,
                Provenance = QaSchemaProvenance.Unknown,
                UserMessage = QaReadinessMessages.ProbeError
            };
        }
    }

    private static async Task<QaSchemaReadinessResult> ProbeSqlServerAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        command.CommandText = """
            SELECT CONVERT(int, ISNULL(HAS_PERMS_BY_NAME(N'dbo', N'SCHEMA', N'VIEW DEFINITION'), -1));
            """;
        var permRaw = await command.ExecuteScalarAsync(cancellationToken);
        var perm = permRaw is null or DBNull
            ? -1
            : Convert.ToInt32(permRaw, CultureInfo.InvariantCulture);
        if (perm != 1)
        {
            return new QaSchemaReadinessResult
            {
                ProbeFailed = true,
                Provenance = QaSchemaProvenance.Unknown,
                UserMessage = QaReadinessMessages.ProbeError
            };
        }

        command.CommandText = BuildSqlServerCatalogSql();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(2))
            {
                return new QaSchemaReadinessResult
                {
                    ProbeFailed = true,
                    Provenance = QaSchemaProvenance.Unknown,
                    UserMessage = QaReadinessMessages.ProbeError
                };
            }

            var kind = reader.GetString(0);
            var name = reader.GetString(1);
            var isPresent = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) == 1;
            if (isPresent)
            {
                present.Add(kind + ":" + name);
            }
        }

        return Classify(present, requireColumnsAndIndexes: true);
    }

    private static async Task<QaSchemaReadinessResult> ProbeSqliteAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        var names = string.Join(", ", AllowlistedTables.Select(x => "'" + x.Replace("'", "''", StringComparison.Ordinal) + "'"));
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN (" + names + ");";
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                present.Add("TABLE:" + reader.GetString(0));
            }
        }

        return Classify(present, requireColumnsAndIndexes: false);
    }

    private static QaSchemaReadinessResult Classify(
        HashSet<string> present,
        bool requireColumnsAndIndexes)
    {
        bool Table(string name) => present.Contains("TABLE:" + name);
        bool Column(string table, string column) =>
            !requireColumnsAndIndexes || present.Contains("COLUMN:" + table + "." + column);
        bool Index(string name) =>
            !requireColumnsAndIndexes || present.Contains("INDEX:" + name);

        var anyObject = AllowlistedTables.Any(Table)
                        || RequiredColumns.Any(x => present.Contains("COLUMN:" + x.Table + "." + x.Column))
                        || RequiredIndexes.Any(x => present.Contains("INDEX:" + x.Index));
        var allTablesAbsent = AllowlistedTables.All(x => !Table(x));
        if (allTablesAbsent && !anyObject)
        {
            return new QaSchemaReadinessResult
            {
                AllAllowlistedAbsent = true,
                Provenance = QaSchemaProvenance.Unknown
            };
        }

        var foundation = Pair(
            "foundation",
            FoundationTables.Select(t => (t, true)).ToArray(),
            RequiredColumns.Where(x => FoundationTables.Contains(x.Table)).ToArray(),
            RequiredIndexes.Where(x => FoundationTables.Contains(x.Table)).ToArray(),
            Table, Column, Index);
        var incoming = Pair(
            "incoming",
            [("QaReceiptGrant", true), ("QaReceiptAllocation", true)],
            RequiredColumns.Where(x => x.Table is "QaReceiptGrant" or "QaReceiptAllocation").ToArray(),
            RequiredIndexes.Where(x => x.Table is "QaReceiptGrant" or "QaReceiptAllocation").ToArray(),
            Table, Column, Index);
        var fg = Pair(
            "finished-goods",
            [("QaFgGrant", true), ("QaFgAllocation", true)],
            RequiredColumns.Where(x => x.Table is "QaFgGrant" or "QaFgAllocation").ToArray(),
            RequiredIndexes.Where(x => x.Table is "QaFgGrant" or "QaFgAllocation").ToArray(),
            Table, Column, Index);
        var logical = Pair(
            "logical hold",
            [("QaLogicalHold", true)],
            RequiredColumns.Where(x => x.Table == "QaLogicalHold").ToArray(),
            RequiredIndexes.Where(x => x.Table == "QaLogicalHold").ToArray(),
            Table, Column, Index);
        var physical = Pair(
            "physical hold",
            [("QaPhysicalHold", true), ("QaPhysicalAllocation", true)],
            RequiredColumns.Where(x => x.Table is "QaPhysicalHold" or "QaPhysicalAllocation").ToArray(),
            RequiredIndexes.Where(x => x.Table is "QaPhysicalHold" or "QaPhysicalAllocation").ToArray(),
            Table, Column, Index);
        var nonGood = Pair(
            "non-good",
            [("QaNonGoodGrant", true), ("QaNonGoodAllocation", true)],
            RequiredColumns.Where(x => x.Table is "QaNonGoodGrant" or "QaNonGoodAllocation").ToArray(),
            RequiredIndexes.Where(x => x.Table is "QaNonGoodGrant" or "QaNonGoodAllocation").ToArray(),
            Table, Column, Index);

        var incomplete = !foundation.IsReady
                         || !incoming.IsReady
                         || !fg.IsReady
                         || !logical.IsReady
                         || !physical.IsReady
                         || !nonGood.IsReady;
        return new QaSchemaReadinessResult
        {
            AllAllowlistedAbsent = false,
            Provenance = QaSchemaProvenance.PreviouslyDeployed,
            UserMessage = incomplete ? QaReadinessMessages.Incomplete : null,
            Foundation = foundation,
            Incoming = incoming,
            FinalFg = fg,
            Logical = logical,
            Physical = physical,
            NonGood = nonGood
        };
    }

    private static QaFeaturePair Pair(
        string name,
        (string Table, bool _)[] tables,
        (string Table, string Column)[] columns,
        (string Index, string Table)[] indexes,
        Func<string, bool> tablePresent,
        Func<string, string, bool> columnPresent,
        Func<string, bool> indexPresent)
    {
        var tableFlags = tables.Select(x => tablePresent(x.Table)).ToArray();
        var anyTable = tableFlags.Any(x => x);
        var allTables = tableFlags.All(x => x);
        var columnsOk = columns.All(x => columnPresent(x.Table, x.Column));
        var indexesOk = indexes.All(x => indexPresent(x.Index));
        var ready = allTables && columnsOk && indexesOk;
        return new QaFeaturePair
        {
            Name = name,
            IsReady = ready,
            IsAbsent = !ready && !anyTable,
            AnyPresent = anyTable,
            Message = ready
                ? null
                : $"Quality {name} schema is incomplete. Repair the quality tables before using this document."
        };
    }

    private static string BuildSqlServerCatalogSql()
    {
        var sql = new StringBuilder();
        sql.AppendLine("SELECT Kind, Name, Present FROM (");
        var first = true;
        foreach (var table in AllowlistedTables)
        {
            AppendUnion(sql, ref first);
            sql.Append("SELECT N'TABLE' AS Kind, N'")
                .Append(table)
                .Append("' AS Name, CASE WHEN OBJECT_ID(N'dbo.")
                .Append(table)
                .Append("', N'U') IS NULL THEN CONVERT(int, 0) ELSE CONVERT(int, 1) END AS Present");
        }

        foreach (var (table, column) in RequiredColumns)
        {
            AppendUnion(sql, ref first);
            sql.Append("SELECT N'COLUMN', N'")
                .Append(table)
                .Append('.')
                .Append(column)
                .Append("', CASE WHEN COL_LENGTH(N'dbo.")
                .Append(table)
                .Append("', N'")
                .Append(column)
                .Append("') IS NULL THEN CONVERT(int, 0) ELSE CONVERT(int, 1) END");
        }

        foreach (var (index, table) in RequiredIndexes)
        {
            AppendUnion(sql, ref first);
            sql.Append("SELECT N'INDEX', N'")
                .Append(index)
                .Append("', CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'")
                .Append(index)
                .Append("' AND object_id = OBJECT_ID(N'dbo.")
                .Append(table)
                .Append("')) THEN CONVERT(int, 1) ELSE CONVERT(int, 0) END");
        }

        sql.AppendLine();
        sql.AppendLine(") AS catalog");
        return sql.ToString();
    }

    private static void AppendUnion(StringBuilder sql, ref bool first)
    {
        if (first)
        {
            first = false;
            return;
        }

        sql.AppendLine();
        sql.Append("UNION ALL ");
    }
}
