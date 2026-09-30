using System.Globalization;
using ClosedXML.Excel;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Planning;

public sealed class PrDefImportPreviewRow
{
    public string Sheet { get; init; } = string.Empty;
    public int RowNumber { get; init; }
    public string? SourceValue { get; init; }
    public string? ParsedValue { get; init; }
    public bool FormulaDetected { get; init; }
    public string? FieldName { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
    public string? ICode { get; init; }
}

public sealed class PrDefImportPreview
{
    public bool IsValid { get; init; }
    public IReadOnlyList<PrDefImportPreviewRow> Rows { get; init; } = [];
    public IReadOnlyList<string> AffectedICodes { get; init; } = [];
    public string? TemplateVersion { get; init; }
}

public interface IPrDefImportService
{
    Task<PlanningServiceResult<PrDefImportPreview>> PreviewAsync(Stream excelStream, string fileName, CancellationToken ct = default);
    Task<PlanningServiceResult> CommitAsync(Stream excelStream, string fileName, CancellationToken ct = default);
}

/// <summary>
/// Routing PrDef* import (not multilevel BOM). v1: formulas rejected on key/business fields; numeric literals only.
/// </summary>
public sealed class PrDefImportService : IPrDefImportService
{
    public const string TemplateVersion = "ImportPrdDefV2";
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxRows = 20_000;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IPlanningMasterAccess _access;
    private readonly ILogger<PrDefImportService> _logger;

    public PrDefImportService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IPlanningMasterAccess access,
        ILogger<PrDefImportService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _logger = logger;
    }

    public async Task<PlanningServiceResult<PrDefImportPreview>> PreviewAsync(Stream excelStream, string fileName, CancellationToken ct = default)
    {
        if (!await _access.CanAccessAsync(MenuCodes.PlanningImportPrdDef, ct))
            return PlanningServiceResult<PrDefImportPreview>.Fail(PlanningErrorCode.PermissionDenied, "Permission denied.");
        if (excelStream.Length > MaxFileBytes)
            return PlanningServiceResult<PrDefImportPreview>.Fail(PlanningErrorCode.ImportTemplateInvalid, "File exceeds size limit.");

        try
        {
            using var book = new XLWorkbook(excelStream);
            var preview = ParseWorkbook(book);
            return PlanningServiceResult<PrDefImportPreview>.Ok(preview);
        }
        catch (Exception ex)
        {
            return PlanningServiceResult<PrDefImportPreview>.Fail(PlanningErrorCode.ImportTemplateInvalid, ex.Message);
        }
    }

    public async Task<PlanningServiceResult> CommitAsync(Stream excelStream, string fileName, CancellationToken ct = default)
    {
        var write = _tenant.TryWriteScope();
        if (write is null) return PlanningServiceResult.TenantRequired();
        if (!await _access.CanAddAsync(MenuCodes.PlanningImportPrdDef, ct))
            return PlanningServiceResult.PermissionDenied(MenuCodes.PlanningImportPrdDef);

        var previewResult = await PreviewAsync(excelStream, fileName, ct);
        if (!previewResult.Succeeded || previewResult.Value is null)
            return PlanningServiceResult.From(previewResult);
        if (!previewResult.Value.IsValid)
            return PlanningServiceResult.Fail(PlanningErrorCode.ImportValidationFailed, "Import has validation errors.", previewResult.Value.Rows
                .Where(r => !string.IsNullOrEmpty(r.Error))
                .Select(r => new ValidationIssue
                {
                    Code = PlanningErrorCode.ImportValidationFailed,
                    ImportSheet = r.Sheet,
                    ImportRow = r.RowNumber,
                    FieldName = r.FieldName,
                    Message = r.Error ?? "Invalid",
                    BusinessKey = r.ICode
                }));

        // Rewind / re-parse for commit payload
        excelStream.Position = 0;
        using var book = new XLWorkbook(excelStream);
        var master = ReadSection(book, "Master");
        var centres = ReadSection(book, "Centre");
        var processes = ReadSection(book, "Process");
        var boms = ReadSection(book, "BOM");
        var machines = ReadSection(book, "Machine");
        var labour = ReadSection(book, "Labour");

        var icodes = master.Select(r => PlanningCodeNormalizer.NormalizeCode(Get(r, "ICode"))).Where(x => x.Length > 0).Distinct().ToList();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Validate routing FKs against tenant masters before replace
        var wcCodes = centres.Select(r => PlanningCodeNormalizer.NormalizeCode(Get(r, "WCCode") ?? Get(r, "WorkCentre"))).Where(x => x.Length > 0).Distinct().ToList();
        var processCodes = processes.Select(r => PlanningCodeNormalizer.NormalizeCode(Get(r, "ProcessCode"))).Where(x => x.Length > 0).Distinct().ToList();
        var machineCodes = machines.Select(r => PlanningCodeNormalizer.NormalizeCode(Get(r, "MachineCode"))).Where(x => x.Length > 0).Distinct().ToList();

        var missingWc = wcCodes.Where(c => !db.PrWorkCentres.AsNoTracking().Any(x => x.WrkCtrCd == c && x.CompCode == write.CompanyCode)).ToList();
        var missingProcess = processCodes.Where(c => !db.PrProcesses.AsNoTracking().Any(x => x.ProcessCd == c && x.CompCode == write.CompanyCode)).ToList();
        var missingMachine = machineCodes.Where(c => !db.PrMachines.AsNoTracking().Any(x => x.MachineCd == c && x.CompCode == write.CompanyCode)).ToList();
        if (missingWc.Count > 0 || missingProcess.Count > 0 || missingMachine.Count > 0)
        {
            var parts = new List<string>();
            if (missingWc.Count > 0) parts.Add("WC: " + string.Join(", ", missingWc.Take(10)));
            if (missingProcess.Count > 0) parts.Add("Process: " + string.Join(", ", missingProcess.Take(10)));
            if (missingMachine.Count > 0) parts.Add("Machine: " + string.Join(", ", missingMachine.Take(10)));
            return PlanningServiceResult.Fail(PlanningErrorCode.ImportValidationFailed,
                "Import references missing tenant masters: " + string.Join("; ", parts));
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var icode in icodes)
            {
                var mac = await db.PrDefMachines.Where(x => x.ProdCode == icode && (x.CompCode == null || x.CompCode == write.CompanyCode)).ToListAsync(ct);
                db.PrDefMachines.RemoveRange(mac);
                var pro = await db.PrDefProcesses.Where(x => x.ProdCode == icode && (x.CompCode == null || x.CompCode == write.CompanyCode)).ToListAsync(ct);
                db.PrDefProcesses.RemoveRange(pro);
                var wc = await db.PrDefWcenters.Where(x => x.ProdCode == icode && (x.CompCode == null || x.CompCode == write.CompanyCode)).ToListAsync(ct);
                db.PrDefWcenters.RemoveRange(wc);
                var mas = await db.PrDefMas.Where(x => x.ICode == icode && (x.CompCode == null || x.CompCode == write.CompanyCode)).ToListAsync(ct);
                db.PrDefMas.RemoveRange(mas);
            }
            await db.SaveChangesAsync(ct);

            foreach (var r in master)
            {
                var icode = PlanningCodeNormalizer.NormalizeCode(Get(r, "ICode"));
                if (icode.Length == 0) continue;
                db.PrDefMas.Add(new PrDefMa
                {
                    ICode = icode,
                    IDesc = PlanningCodeNormalizer.NormalizeDescription(Get(r, "IDesc")),
                    CompCode = write.CompanyCode,
                    BranchCode = write.BranchCode,
                    LocCode = write.LocationCode
                });
            }
            foreach (var r in centres)
            {
                var prod = PlanningCodeNormalizer.NormalizeCode(Get(r, "ProdCode") ?? Get(r, "ICode"));
                var wc = PlanningCodeNormalizer.NormalizeCode(Get(r, "WCCode") ?? Get(r, "WorkCentre"));
                if (prod.Length == 0 || wc.Length == 0) continue;
                db.PrDefWcenters.Add(new PrDefWcenter
                {
                    ProdCode = prod,
                    WcCode = wc,
                    ICode = PlanningCodeNormalizer.NormalizeCode(Get(r, "WCICode") ?? prod),
                    CompCode = write.CompanyCode,
                    BranchCode = write.BranchCode,
                    LocCode = write.LocationCode
                });
            }
            foreach (var r in processes)
            {
                var prod = PlanningCodeNormalizer.NormalizeCode(Get(r, "ProdCode") ?? Get(r, "ICode"));
                var process = PlanningCodeNormalizer.NormalizeCode(Get(r, "ProcessCode"));
                if (prod.Length == 0 || process.Length == 0) continue;
                db.PrDefProcesses.Add(new PrDefProcess
                {
                    ProdCode = prod,
                    WcCode = PlanningCodeNormalizer.NormalizeCode(Get(r, "WCCode")),
                    WciCode = PlanningCodeNormalizer.NormalizeCode(Get(r, "WCICode") ?? prod),
                    ProcessCode = process,
                    Remark = PlanningCodeNormalizer.NormalizeDescription(Get(r, "Remark")),
                    CompCode = write.CompanyCode,
                    BranchCode = write.BranchCode,
                    LocCode = write.LocationCode
                });
            }
            foreach (var r in machines)
            {
                var prod = PlanningCodeNormalizer.NormalizeCode(Get(r, "ProdCode") ?? Get(r, "ICode"));
                var machine = PlanningCodeNormalizer.NormalizeCode(Get(r, "MachineCode"));
                if (prod.Length == 0 || machine.Length == 0) continue;
                db.PrDefMachines.Add(new PrDefMachine
                {
                    ProdCode = prod,
                    WcCode = PlanningCodeNormalizer.NormalizeCode(Get(r, "WCCode")),
                    WciCode = PlanningCodeNormalizer.NormalizeCode(Get(r, "WCICode") ?? prod),
                    ProcessCode = PlanningCodeNormalizer.NormalizeCode(Get(r, "ProcessCode")),
                    MachineCode = machine,
                    MachineName = PlanningCodeNormalizer.NormalizeDescription(Get(r, "MachineName")),
                    CycleTime = ParseDoubleLiteral(Get(r, "CycleTime")) ?? 0,
                    CompCode = write.CompanyCode,
                    BranchCode = write.BranchCode,
                    LocCode = write.LocationCode
                });
            }
            // BOM sheet is validated in preview but persisted via Product Definition (PrBomHdr/PrDefBOM multilevel) — not this routing import.
            _ = boms;
            _ = labour;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _logger.LogInformation("PrDef import committed for {Count} products company={Company}", icodes.Count, write.CompanyCode);
            return PlanningServiceResult.Ok($"Imported {icodes.Count} product definition(s).");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private PrDefImportPreview ParseWorkbook(XLWorkbook book)
    {
        var previewRows = new List<PrDefImportPreviewRow>();
        var issues = 0;
        var icodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenICodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Template version: Meta sheet cell A1 or named "TemplateVersion"
        string? templateVersion = null;
        var meta = book.Worksheets.FirstOrDefault(w =>
            string.Equals(w.Name, "Meta", StringComparison.OrdinalIgnoreCase)
            || string.Equals(w.Name, "Version", StringComparison.OrdinalIgnoreCase));
        if (meta is not null)
        {
            templateVersion = meta.Cell(1, 1).GetFormattedString()?.Trim();
            if (string.IsNullOrWhiteSpace(templateVersion))
                templateVersion = meta.Cell(1, 2).GetFormattedString()?.Trim();
        }
        if (!string.Equals(templateVersion, TemplateVersion, StringComparison.OrdinalIgnoreCase))
        {
            previewRows.Add(new PrDefImportPreviewRow
            {
                Sheet = "Meta",
                RowNumber = 1,
                FieldName = "TemplateVersion",
                SourceValue = templateVersion,
                Status = "Error",
                Error = $"Expected template version '{TemplateVersion}'."
            });
            issues++;
        }

        foreach (var section in new[] { "Master", "Centre", "Process", "BOM", "Machine", "Labour" })
        {
            var sheet = book.Worksheets.FirstOrDefault(w =>
                string.Equals(w.Name, section, StringComparison.OrdinalIgnoreCase)
                || string.Equals(w.Name, section + "$", StringComparison.OrdinalIgnoreCase));
            if (sheet is null && section != "Labour")
            {
                previewRows.Add(new PrDefImportPreviewRow
                {
                    Sheet = section,
                    RowNumber = 0,
                    Status = "Error",
                    Error = $"Required sheet '{section}' missing."
                });
                issues++;
                continue;
            }
            if (sheet is null || sheet.Visibility != XLWorksheetVisibility.Visible) continue;

            var headerRow = sheet.FirstRowUsed();
            if (headerRow is null) continue;
            var headers = headerRow.CellsUsed().ToDictionary(c => c.GetString().Trim(), c => c.Address.ColumnNumber, StringComparer.OrdinalIgnoreCase);

            // Required column check: at least one of each alias group present for key sheets
            if (section is "Master")
            {
                if (!headers.ContainsKey("ICode"))
                {
                    previewRows.Add(new PrDefImportPreviewRow { Sheet = section, Status = "Error", Error = "Missing required column ICode." });
                    issues++;
                }
            }
            else if (section is "Centre")
            {
                if (!headers.Keys.Any(k => k is "ProdCode" or "ICode") || !headers.Keys.Any(k => k is "WCCode" or "WorkCentre"))
                {
                    previewRows.Add(new PrDefImportPreviewRow { Sheet = section, Status = "Error", Error = "Missing ProdCode/ICode or WCCode/WorkCentre." });
                    issues++;
                }
            }
            else if (section is "Process")
            {
                if (!headers.Keys.Any(k => k is "ProdCode" or "ICode") || !headers.ContainsKey("ProcessCode"))
                {
                    previewRows.Add(new PrDefImportPreviewRow { Sheet = section, Status = "Error", Error = "Missing ProdCode/ICode or ProcessCode." });
                    issues++;
                }
            }
            else if (section is "Machine")
            {
                if (!headers.Keys.Any(k => k is "ProdCode" or "ICode") || !headers.ContainsKey("MachineCode"))
                {
                    previewRows.Add(new PrDefImportPreviewRow { Sheet = section, Status = "Error", Error = "Missing ProdCode/ICode or MachineCode." });
                    issues++;
                }
            }

            var rowCount = 0;
            foreach (var row in sheet.RowsUsed().Skip(1))
            {
                if (row.IsEmpty() || row.CellsUsed().All(c => string.IsNullOrWhiteSpace(c.GetFormattedString())))
                    continue;
                if (row.IsHidden)
                    continue;

                rowCount++;
                if (rowCount > MaxRows)
                {
                    previewRows.Add(new PrDefImportPreviewRow { Sheet = section, RowNumber = row.RowNumber(), Status = "Error", Error = "Max row count exceeded." });
                    issues++;
                    break;
                }

                if (section == "Master")
                {
                    var code = PlanningCodeNormalizer.NormalizeCode(GetCell(row, headers, "ICode"));
                    if (code.Length > 0 && !seenICodes.Add(code))
                    {
                        previewRows.Add(new PrDefImportPreviewRow
                        {
                            Sheet = section,
                            RowNumber = row.RowNumber(),
                            FieldName = "ICode",
                            ICode = code,
                            Status = "Error",
                            Error = "Duplicate ICode in workbook."
                        });
                        issues++;
                    }
                }

                foreach (var (name, col) in headers)
                {
                    var cell = row.Cell(col);
                    var hasFormula = cell.HasFormula;
                    var raw = cell.HasFormula ? cell.FormulaA1 : cell.GetFormattedString();
                    string? parsed = null;
                    string? error = null;

                    var isKey = name.Contains("Code", StringComparison.OrdinalIgnoreCase) || name is "ICode" or "ProdCode";
                    var isNumeric = name.Contains("Qty", StringComparison.OrdinalIgnoreCase)
                                    || name.Contains("Time", StringComparison.OrdinalIgnoreCase)
                                    || name.Contains("Size", StringComparison.OrdinalIgnoreCase);

                    if (hasFormula && isKey)
                        error = "Formulas are not allowed on key/business fields.";
                    else if (hasFormula && isNumeric)
                        error = "Numeric fields require literal values (no formulas) in v1.";
                    else if (isNumeric && !string.IsNullOrWhiteSpace(raw) && !double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                        error = "Invalid numeric literal.";
                    else
                        parsed = raw?.Trim();

                    if (string.Equals(name, "ICode", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "ProdCode", StringComparison.OrdinalIgnoreCase))
                    {
                        var code = PlanningCodeNormalizer.NormalizeCode(parsed);
                        if (code.Length > 0) icodes.Add(code);
                    }

                    if (error is not null) issues++;
                    previewRows.Add(new PrDefImportPreviewRow
                    {
                        Sheet = section,
                        RowNumber = row.RowNumber(),
                        FieldName = name,
                        SourceValue = raw,
                        ParsedValue = parsed,
                        FormulaDetected = hasFormula,
                        Status = error is null ? "OK" : "Error",
                        Error = error,
                        ICode = PlanningCodeNormalizer.NormalizeCode(GetCell(row, headers, "ICode") ?? GetCell(row, headers, "ProdCode"))
                    });
                }
            }
        }

        return new PrDefImportPreview
        {
            IsValid = issues == 0,
            Rows = previewRows,
            AffectedICodes = icodes.ToList(),
            TemplateVersion = templateVersion ?? TemplateVersion
        };
    }

    private static List<Dictionary<string, string?>> ReadSection(XLWorkbook book, string section)
    {
        var list = new List<Dictionary<string, string?>>();
        var sheet = book.Worksheets.FirstOrDefault(w => string.Equals(w.Name, section, StringComparison.OrdinalIgnoreCase));
        if (sheet is null) return list;
        var headerRow = sheet.FirstRowUsed();
        if (headerRow is null) return list;
        var headers = headerRow.CellsUsed().Select(c => (Name: c.GetString().Trim(), Col: c.Address.ColumnNumber)).ToList();
        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            if (row.IsEmpty()) continue;
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers)
            {
                var cell = row.Cell(h.Col);
                if (cell.HasFormula)
                    dict[h.Name] = null; // rejected at preview
                else
                    dict[h.Name] = cell.GetFormattedString()?.Trim();
            }
            list.Add(dict);
        }
        return list;
    }

    private static string? Get(Dictionary<string, string?> row, string key) =>
        row.TryGetValue(key, out var v) ? v : null;

    private static string? GetCell(IXLRow row, Dictionary<string, int> headers, string name) =>
        headers.TryGetValue(name, out var col) ? row.Cell(col).GetFormattedString()?.Trim() : null;

    private static double? ParseDoubleLiteral(string? s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static decimal? ParseDecimalLiteral(string? s) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
}
