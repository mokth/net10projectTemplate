using System.Text;
using System.Text.RegularExpressions;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

// Offline DDL generation only. No connection is opened and no migration is applied.
if (args.Length != 1) throw new ArgumentException("Supply the output SQL path.");
using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=.;Database=Unused;Integrated Security=true;TrustServerCertificate=true").Options);
var tables = new HashSet<string> { "PrFinishedGoodReceipt", "PrFinishedGoodSource", "PrFinishedGoodFact", "PrFinishedGoodPriceSnapshot", "PrFinishedGoodLotOrigin", "PrPoolValuation", "PrValuationEvidence", "PrPoolDependency" };
var sql = new StringBuilder("-- Generated from the EF model by scripts/FinishedGoodSchema. Rerunnable; preserves all existing data.\nSET XACT_ABORT ON;\nSET ANSI_NULLS ON;\nSET QUOTED_IDENTIFIER ON;\nGO\n");
foreach (var batch in Regex.Split(db.Database.GenerateCreateScript(), @"^GO\s*$", RegexOptions.Multiline))
{
    var create = Regex.Match(batch, @"CREATE TABLE \[(?<table>[^\]]+)\]");
    if (create.Success && tables.Contains(create.Groups["table"].Value))
        sql.AppendLine($"IF OBJECT_ID(N'dbo.{create.Groups["table"].Value}', N'U') IS NULL\nBEGIN\n{batch.Trim()}\nEND;\nGO");
    var index = Regex.Match(batch, @"CREATE (?:UNIQUE )?INDEX \[(?<index>[^\]]+)\] ON \[(?<table>[^\]]+)\]");
    if (index.Success && tables.Contains(index.Groups["table"].Value))
        sql.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.{index.Groups["table"].Value}') AND name=N'{index.Groups["index"].Value}')\n{batch.Trim()}\nGO");
    var deferredForeignKey = Regex.Match(batch,
        @"ALTER TABLE \[(?<table>[^\]]+)\] ADD CONSTRAINT \[(?<constraint>[^\]]+)\] FOREIGN KEY");
    if (deferredForeignKey.Success && tables.Contains(deferredForeignKey.Groups["table"].Value))
        sql.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.{deferredForeignKey.Groups["table"].Value}') AND name=N'{deferredForeignKey.Groups["constraint"].Value}')\n{batch.Trim()}\nGO");
}
foreach (var (table, column, type) in new[] {
    ("IvTrxBatchDetail", "PriceEvidence", "nvarchar(200)"), ("IvBalLoc", "PriceEvidence", "nvarchar(200)"),
    ("IvTrxHistory", "PriceEvidence", "nvarchar(200)"), ("IvTrxHistory", "ExactTransferredValue", "decimal(18,4)"),
    ("IvTrxHistory", "ValuationStatus", "nvarchar(20)"), ("IvTrxHistory", "EvidenceBaseQty", "decimal(18,4)"), ("IvTrxHistory", "EvidenceBaseUom", "nvarchar(10)") })
    sql.AppendLine($"IF COL_LENGTH(N'dbo.{table}', N'{column}') IS NULL ALTER TABLE dbo.{table} ADD {column} {type} NULL;\nGO");
File.WriteAllText(args[0], sql.ToString());
Console.WriteLine($"Generated {tables.Count} extension tables and nullable inventory evidence columns: {args[0]}");
