using System.Data;
using System.Globalization;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Sales item family: price lists (<c>IvCustPriceGroup</c> + <c>IvCustPrice</c>), customer items
/// (<c>SaItemCust</c>) and item discount rules (<c>SaDisGroupItem</c>).
///
/// Plan: plans/sales-item-family-v2-plan.md. Decisions implemented here:
///   §7    price sources / fail-closed rules (the resolver itself is deferred to the SO/INV consumer),
///   §8.3  quantity-band + date-window validation and the single-matching-rule rule,
///   §8.8  keys: the natural key is authoritative; nothing re-keys a live table,
///   §10   concurrency: header RowVersion gates the aggregate, the band overlap check is Serializable,
///   §11.1 VIEW_PRICE is a visibility gate only — a denied caller gets NULLs, never a zeroed price.
/// </summary>
public sealed partial class SaSalesRefService
{
    private const int PriceGroupCodeMax = 20;
    private const int PriceGroupDescMax = 50;
    private const int ItemCodeMax = 20;
    private const int UomMax = 5;
    private const int ClassMax = 10;
    private const int CustCodeMax = 20;
    private const int ItemCustDescMax = 100;
    private const int InvDescMax = 300;

    // ===================== key encoding =====================

    /// <summary>
    /// Encodes a natural key for the bulk-delete token. ";" is the separator and is rejected inside
    /// every code by <c>ValidateAndNormalizeCode</c>, so the encoding cannot be ambiguous.
    /// </summary>
    private static string EncodeItemFamilyKey(params object?[] parts) =>
        string.Join(';', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture) ?? string.Empty));

    private static string KeyPart(string key, int index)
    {
        var parts = key.Split(';');
        return index >= 0 && index < parts.Length ? parts[index].Trim() : string.Empty;
    }

    private static int KeyPartInt(string key, int index) =>
        int.TryParse(KeyPart(key, index), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;

    // ===================== shared validation helpers =====================

    /// <summary>
    /// VIEW_PRICE is checked against the screen the caller is on, so a sales user with price rights on
    /// one screen is not silently granted them on another.
    /// </summary>
    private Task<bool> CanViewPriceAsync(string menuCode, CancellationToken cancellationToken) =>
        _accessRights.CanAsync(menuCode, PermissionCodes.ViewPrice, cancellationToken);

    private static Dictionary<string, string> NewErrors() =>
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Nullable money from the legacy <c>float</c> column, scaled explicitly (§8.5).</summary>
    private static decimal? ScaleLegacyMoney(double? value) =>
        value is null ? null : decimal.Round((decimal)value.Value, 4, MidpointRounding.AwayFromZero);

    private static double? UnscaleLegacyMoney(decimal? value) =>
        value is null ? null : (double)decimal.Round(value.Value, 4, MidpointRounding.AwayFromZero);

    private static string NormalizeOptionalCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

    private async Task<HashSet<string>> ActiveItemCodesAsync(AppDbContext db, string company, CancellationToken cancellationToken) =>
        (await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive)
            .Select(x => x.ICode)
            .ToListAsync(cancellationToken))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<HashSet<string>> ActiveUomCodesAsync(AppDbContext db, string company, CancellationToken cancellationToken) =>
        (await db.MsUoms.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive)
            .Select(x => x.UomCode)
            .ToListAsync(cancellationToken))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<HashSet<string>> ActiveClassCodesAsync(AppDbContext db, string company, CancellationToken cancellationToken) =>
        (await db.IvClasses.AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .Select(x => x.IClassCode)
            .ToListAsync(cancellationToken))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<HashSet<string>> CustomerCodesAsync(AppDbContext db, string company, CancellationToken cancellationToken) =>
        (await db.SaCusts.AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .Select(x => x.CustCode)
            .ToListAsync(cancellationToken))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IvMasterOperationResult<T> PriceView<T>(T data) =>
        IvMasterOperationResult<T>.Ok(data);

    // ============================================================================================
    // IvCustPriceGroup — price list header (+ lines, saved as one aggregate)
    // ============================================================================================

    public async Task<IvMasterOperationResult<IReadOnlyList<IvCustPriceGroupListRow>>> ListCustPriceGroupsAsync(
        CancellationToken cancellationToken = default) =>
        await ListCustPriceGroupsCoreAsync(PermissionCodes.Access, takeAll: false, cancellationToken);

    public async Task<IvMasterOperationResult<IReadOnlyList<IvCustPriceGroupListRow>>> ExportCustPriceGroupsAsync(
        CancellationToken cancellationToken = default) =>
        await ListCustPriceGroupsCoreAsync(PermissionCodes.Export, takeAll: true, cancellationToken);

    private async Task<IvMasterOperationResult<IReadOnlyList<IvCustPriceGroupListRow>>> ListCustPriceGroupsCoreAsync(
        string permission,
        bool takeAll,
        CancellationToken cancellationToken)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustPriceGroup, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<IvCustPriceGroupListRow>(ctx.Error.Value);
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var query = db.IvCustPriceGroups.AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .OrderBy(x => x.CustPriceCode);

        var rows = takeAll
            ? await query.Take(MaxExportRows).Select(x => new IvCustPriceGroupListRow
            {
                CustPriceCode = x.CustPriceCode,
                CustPriceDesc = x.CustPriceDesc,
                IsActive = x.IsActive,
                RowVersion = x.RowVersion ?? System.Array.Empty<byte>()
            }).ToListAsync(cancellationToken)
            : await query.Select(x => new IvCustPriceGroupListRow
            {
                CustPriceCode = x.CustPriceCode,
                CustPriceDesc = x.CustPriceDesc,
                IsActive = x.IsActive,
                RowVersion = x.RowVersion ?? System.Array.Empty<byte>()
            }).ToListAsync(cancellationToken);

        // Line counts in one round trip (avoid an N+1 over the list).
        var counts = await db.IvCustPrices.AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .GroupBy(x => x.CustPriceCode)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Code, x => x.Count, cancellationToken);

        var withCounts = rows
            .Select(r => new IvCustPriceGroupListRow
            {
                CustPriceCode = r.CustPriceCode,
                CustPriceDesc = r.CustPriceDesc,
                IsActive = r.IsActive,
                RowVersion = r.RowVersion,
                LineCount = counts.TryGetValue(r.CustPriceCode, out var c) ? c : 0
            })
            .ToList();

        return IvMasterOperationResult<IReadOnlyList<IvCustPriceGroupListRow>>.Ok(withCounts);
    }

    public async Task<IvMasterOperationResult<IvCustPriceGroupEditVm>> GetCustPriceGroupAsync(
        string custPriceCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustPriceGroup, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<IvCustPriceGroupEditVm>(ctx.Error.Value);
        }

        var company = ctx.CompanyCode!;
        var code = NormalizeOptionalCode(custPriceCode);
        if (code.Length == 0)
        {
            return FailVm<IvCustPriceGroupEditVm>(IvMasterErrorCode.Validation, "Price group code is required.", "CustPriceCode");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var header = await db.IvCustPriceGroups.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.CustPriceCode == code, cancellationToken);
        if (header is null)
        {
            return FailVm<IvCustPriceGroupEditVm>(IvMasterErrorCode.NotFound, "Price group not found.");
        }

        var canViewPrice = await CanViewPriceAsync(MenuCodes.SalesCustPriceGroup, cancellationToken);
        var lines = await db.IvCustPrices.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.CustPriceCode == code)
            .OrderBy(x => x.ICode).ThenBy(x => x.UOM)
            .Select(x => new IvCustPriceLineVm
            {
                ICode = x.ICode,
                IDesc = x.IDesc,
                UOM = x.UOM,
                SellingPrice = canViewPrice ? x.SellingPrice : null,
                SellPackSize = x.SellPackSize
            })
            .ToListAsync(cancellationToken);

        return PriceView(new IvCustPriceGroupEditVm
        {
            CustPriceCode = header.CustPriceCode,
            CustPriceDesc = header.CustPriceDesc,
            IsActive = header.IsActive,
            RowVersion = header.RowVersion ?? [],
            Lines = lines
        });
    }

    /// <summary>
    /// Saves the header and its lines in ONE transaction (§10): header → lines → duplicates → item/UOM
    /// existence → write → commit. Any failure rolls the whole group back; a half-saved price group
    /// cannot exist.
    /// </summary>
    public async Task<IvMasterOperationResult<IvCustPriceGroupEditVm>> SaveCustPriceGroupAsync(
        IvCustPriceGroupEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<IvCustPriceGroupEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustPriceGroup, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<IvCustPriceGroupEditVm>(ctx.Error.Value);
        }

        var company = ctx.CompanyCode!;
        var errors = NewErrors();
        var code = ValidateAndNormalizeCode(errors, nameof(model.CustPriceCode), "Price group code", model.CustPriceCode, PriceGroupCodeMax);
        var desc = ValidateAndNormalizeDescription(errors, nameof(model.CustPriceDesc), "Description", model.CustPriceDesc, PriceGroupDescMax);

        var lines = model.Lines ?? [];
        if (lines.Count == 0)
        {
            errors[nameof(model.Lines)] = "At least one item price is required.";
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var validItems = await ActiveItemCodesAsync(db, company, cancellationToken);
        var validUoms = await ActiveUomCodesAsync(db, company, cancellationToken);

        // A caller without VIEW_PRICE never sets a price — and never erases one either: the stored
        // value is preserved, because the price is simply absent from their payload (§11.1).
        var canViewPrice = await CanViewPriceAsync(MenuCodes.SalesCustPriceGroup, cancellationToken);

        // Existing stored codes are tolerated so legacy free text cannot block an unrelated edit
        // (the same three-clause contract as D-6, applied to lines).
        var storedLines = isNew
            ? []
            : await db.IvCustPrices.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.CustPriceCode == code)
                .ToListAsync(cancellationToken);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedLines = new List<(string ICode, string? IDesc, string UOM, decimal? Price, decimal? PackSize)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var label = $"Line {i + 1}";
            var iCode = ValidateAndNormalizeCode(errors, $"Lines[{i}].ICode", $"{label} item", line.ICode, ItemCodeMax);
            var uom = ValidateAndNormalizeCode(errors, $"Lines[{i}].UOM", $"{label} UOM", line.UOM, UomMax);

            if (iCode.Length > 0 && uom.Length > 0 && !seen.Add($"{iCode};{uom}"))
            {
                errors[$"Lines[{i}].ICode"] = $"{label}: item {iCode} / UOM {uom} appears more than once.";
            }

            var stored = storedLines.FirstOrDefault(x =>
                KeysEqual(x.ICode, iCode) && KeysEqual(x.UOM, uom));

            if (iCode.Length > 0 && !validItems.Contains(iCode) && stored is null)
            {
                errors[$"Lines[{i}].ICode"] = $"{label}: item {iCode} does not exist or is not active.";
            }

            if (uom.Length > 0 && !validUoms.Contains(uom) && stored is null)
            {
                errors[$"Lines[{i}].UOM"] = $"{label}: UOM {uom} does not exist or is not active.";
            }

            if (line.SellingPrice is < 0m)
            {
                errors[$"Lines[{i}].SellingPrice"] = $"{label}: selling price cannot be negative.";
            }

            var requestedPrice = canViewPrice ? line.SellingPrice : stored?.SellingPrice;
            normalizedLines.Add((iCode, line.IDesc, uom, requestedPrice, line.SellPackSize));
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<IvCustPriceGroupEditVm>.Fail(IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<IvCustPriceGroupEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        var now = DateTime.UtcNow;
        var user = ctx.UserId!;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            IvCustPriceGroup header;
            if (isNew)
            {
                if (await db.IvCustPriceGroups.AnyAsync(x => x.CompanyCode == company && x.CustPriceCode == code, cancellationToken))
                {
                    return FailVm<IvCustPriceGroupEditVm>(IvMasterErrorCode.DuplicateKey, "Price group code already exists.", nameof(model.CustPriceCode));
                }

                header = new IvCustPriceGroup
                {
                    CompanyCode = company,
                    CustPriceCode = code,
                    CustPriceDesc = desc,
                    IsActive = model.IsActive,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(header, writeScope);
                db.IvCustPriceGroups.Add(header);
            }
            else
            {
                header = await db.IvCustPriceGroups
                    .FirstOrDefaultAsync(x => x.CompanyCode == company && x.CustPriceCode == code, cancellationToken)
                    ?? null!;
                if (header is null)
                {
                    return FailVm<IvCustPriceGroupEditVm>(IvMasterErrorCode.NotFound, "Price group not found.");
                }

                var entry = db.Entry(header);
                var current = entry.Property("RowVersion").CurrentValue as byte[];
                if (!RowVersionsEqual(current, model.RowVersion))
                {
                    return FailVm<IvCustPriceGroupEditVm>(IvMasterErrorCode.Concurrency, "This price group was changed by another user. Reload and try again.");
                }

                entry.Property("RowVersion").OriginalValue = model.RowVersion;
                header.CustPriceDesc = desc;
                header.IsActive = model.IsActive;
                header.ModifiedDate = now;
                header.ModifiedBy = user;
            }

            // Replace the line set: remove what is gone, update what stayed, insert what is new.
            var currentLines = await db.IvCustPrices
                .Where(x => x.CompanyCode == company && x.CustPriceCode == code)
                .ToListAsync(cancellationToken);

            foreach (var existing in currentLines.Where(x =>
                         !normalizedLines.Any(n => KeysEqual(n.ICode, x.ICode) && KeysEqual(n.UOM, x.UOM))))
            {
                db.IvCustPrices.Remove(existing);
            }

            foreach (var line in normalizedLines)
            {
                var existing = currentLines.FirstOrDefault(x =>
                    KeysEqual(x.ICode, line.ICode) && KeysEqual(x.UOM, line.UOM));

                if (existing is null)
                {
                    var created = new IvCustPrice
                    {
                        CompanyCode = company,
                        CustPriceCode = code,
                        ICode = line.ICode,
                        UOM = line.UOM,
                        IDesc = line.IDesc,
                        CustPriceDesc = desc,
                        SellingPrice = line.Price,
                        SellPackSize = line.PackSize,
                        CreatedDate = now,
                        CreatedBy = user,
                        ModifiedDate = now,
                        ModifiedBy = user
                    };
                    InventoryLeftoverSite.Apply(created, writeScope);
                    db.IvCustPrices.Add(created);
                }
                else
                {
                    existing.IDesc = line.IDesc;
                    existing.CustPriceDesc = desc;
                    existing.SellingPrice = line.Price;
                    existing.SellPackSize = line.PackSize;
                    existing.ModifiedDate = now;
                    existing.ModifiedBy = user;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm<IvCustPriceGroupEditVm>(IvMasterErrorCode.Concurrency, "This price group was changed by another user. Reload and try again.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm<IvCustPriceGroupEditVm>(IvMasterErrorCode.DuplicateKey, "The same item and UOM already exists in this price group.", nameof(model.Lines));
        }

        return await GetCustPriceGroupAsync(code, cancellationToken);
    }

    public async Task<IvMasterOperationResult<object>> SetCustPriceGroupActiveAsync(
        IReadOnlyList<SaItemFamilyKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustPriceGroup, PermissionCodes.Edit, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        if (items is null || items.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var company = ctx.CompanyCode!;
        var codes = items.Select(i => i.Key.Trim().ToUpperInvariant()).ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var headers = await db.IvCustPriceGroups
            .Where(x => x.CompanyCode == company && codes.Contains(x.CustPriceCode))
            .ToListAsync(cancellationToken);

        if (headers.Count != codes.Count)
        {
            return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
        }

        foreach (var token in items)
        {
            var header = headers.FirstOrDefault(x => KeysEqual(x.CustPriceCode, token.Key));
            if (header is null)
            {
                return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
            }

            var entry = db.Entry(header);
            var current = entry.Property("RowVersion").CurrentValue as byte[];
            if (!RowVersionsEqual(current, token.RowVersion))
            {
                return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
            }

            entry.Property("RowVersion").OriginalValue = token.RowVersion;
            header.IsActive = isActive;
            header.ModifiedDate = DateTime.UtcNow;
            header.ModifiedBy = ctx.UserId!;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
        }

        return IvMasterOperationResult<object>.Ok();
    }

    /// <summary>
    /// A price group is referenced by its lines (<c>IvCustPrice</c>) and by customers
    /// (<c>SaCust.CustPriceCode</c>). Both counts are company-scoped and both block the delete.
    /// </summary>
    public async Task<DeleteCheckResult> CanDeleteCustPriceGroupsAsync(
        IReadOnlyList<string> custPriceCodes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustPriceGroup, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var codes = (custPriceCodes ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var refs = await CountCustPriceGroupReferencesBulkAsync(db, company, codes, cancellationToken);
        return BuildDeleteCheck(codes, refs);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountCustPriceGroupReferencesBulkAsync(
        AppDbContext db,
        string company,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        var lineCounts = await db.IvCustPrices.AsNoTracking()
            .Where(x => x.CompanyCode == company && codes.Contains(x.CustPriceCode))
            .GroupBy(x => x.CustPriceCode)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var custCounts = await db.SaCusts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.CustPriceCode != null && codes.Contains(x.CustPriceCode))
            .GroupBy(x => x.CustPriceCode!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var map = codes.ToDictionary(
            code => code,
            _ => new List<IvReferenceCount>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in lineCounts)
        {
            AddRef(map, row.Code, "IvCustPrice", row.Count);
        }

        foreach (var row in custCounts)
        {
            AddRef(map, row.Code, "SaCust.CustPriceCode", row.Count);
        }

        return map.ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<IvReferenceCount>)x.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public Task<IvMasterOperationResult<object>> DeleteCustPriceGroupsAsync(
        IReadOnlyList<SaItemFamilyKeyToken> items,
        CancellationToken cancellationToken = default) =>
        DeleteItemFamilyAsync<IvCustPriceGroup>(
            MenuCodes.SalesCustPriceGroup,
            items,
            async (db, company, codes, ct) =>
            {
                await db.IvCustPrices
                    .Where(x => x.CompanyCode == company && codes.Contains(x.CustPriceCode))
                    .LoadAsync(ct);
                return await db.IvCustPriceGroups
                    .Where(x => x.CompanyCode == company && codes.Contains(x.CustPriceCode))
                    .ToListAsync(ct);
            },
            (entity, key) => KeysEqual(entity.CustPriceCode, key),
            CountCustPriceGroupReferencesBulkAsync,
            cancellationToken);

    /// <summary>
    /// Per-group workbook download, driven from the price-list popup. EXPORT permission (not ACCESS),
    /// with the same VIEW_PRICE masking as the list.
    ///
    /// This is the only caller-facing price-line read: the standalone read-only Customer Prices screen
    /// (/sales/customer-prices) was merged into the Price Groups screen, which keeps every write inside
    /// the header aggregate.
    /// </summary>
    public async Task<IvMasterOperationResult<IReadOnlyList<IvCustPriceListRow>>> ExportCustPricesAsync(
        string custPriceCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustPriceGroup, PermissionCodes.Export, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<IvCustPriceListRow>(ctx.Error.Value);
        }

        return await CustPricesCoreAsync(ctx.CompanyCode!, custPriceCode, cancellationToken);
    }

    /// <summary>
    /// The rows of one price group, VIEW_PRICE-masked. Shared by the export; there is no line list page
    /// any more (the Customer Prices screen was merged into the price-list popup).
    /// </summary>
    private async Task<IvMasterOperationResult<IReadOnlyList<IvCustPriceListRow>>> CustPricesCoreAsync(
        string company,
        string custPriceCode,
        CancellationToken cancellationToken)
    {
        var code = NormalizeOptionalCode(custPriceCode);
        var canViewPrice = await CanViewPriceAsync(MenuCodes.SalesCustPriceGroup, cancellationToken);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.IvCustPrices.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.CustPriceCode == code)
            .OrderBy(x => x.ICode).ThenBy(x => x.UOM)
            .Select(x => new IvCustPriceListRow
            {
                ICode = x.ICode,
                IDesc = x.IDesc,
                UOM = x.UOM,
                SellingPrice = canViewPrice ? x.SellingPrice : null,
                SellPackSize = x.SellPackSize
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<IvCustPriceListRow>>.Ok(rows);
    }

    // ============================================================================================
    // SaItemCust — customer item
    // ============================================================================================

    public async Task<IvMasterOperationResult<IReadOnlyList<SaItemCustListRow>>> ListItemCustsAsync(
        CancellationToken cancellationToken = default) =>
        await ListItemCustsCoreAsync(PermissionCodes.Access, takeAll: false, cancellationToken);

    public async Task<IvMasterOperationResult<IReadOnlyList<SaItemCustListRow>>> ExportItemCustsAsync(
        CancellationToken cancellationToken = default) =>
        await ListItemCustsCoreAsync(PermissionCodes.Export, takeAll: true, cancellationToken);

    private async Task<IvMasterOperationResult<IReadOnlyList<SaItemCustListRow>>> ListItemCustsCoreAsync(
        string permission,
        bool takeAll,
        CancellationToken cancellationToken)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesItemCust, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<SaItemCustListRow>(ctx.Error.Value);
        }

        var company = ctx.CompanyCode!;
        var canViewPrice = await CanViewPriceAsync(MenuCodes.SalesItemCust, cancellationToken);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.SaItemCusts.AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .OrderBy(x => x.CustCode).ThenBy(x => x.ICode).ThenBy(x => x.SellingUOM).ThenBy(x => x.MOQ);

        var rows = await (takeAll ? query.Take(MaxExportRows) : query)
            .Select(x => new SaItemCustListRow
            {
                CustCode = x.CustCode,
                ICode = x.ICode,
                IDesc = x.IDesc,
                CustICode = x.CustICode,
                SellingUOM = x.SellingUOM,
                MOQ = x.MOQ,
                UnitPrice = canViewPrice ? (decimal?)x.UnitPrice : null,
                Currency = x.Currency,
                Status = x.Status,
                RowVersion = x.RowVersion ?? System.Array.Empty<byte>()
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<SaItemCustListRow>>.Ok(rows);
    }

    public async Task<IvMasterOperationResult<SaItemCustEditVm>> GetItemCustAsync(
        SaItemCustKey key,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesItemCust, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaItemCustEditVm>(ctx.Error.Value);
        }

        var company = ctx.CompanyCode!;
        var custCode = NormalizeOptionalCode(key?.CustCode);
        var iCode = NormalizeOptionalCode(key?.ICode);
        var uom = NormalizeOptionalCode(key?.SellingUOM);
        var moq = key?.MOQ ?? 0;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SaItemCusts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.CustCode == custCode && x.ICode == iCode
                                      && x.SellingUOM == uom && x.MOQ == moq, cancellationToken);
        if (entity is null)
        {
            return FailVm<SaItemCustEditVm>(IvMasterErrorCode.NotFound, "Customer item not found.");
        }

        var canViewPrice = await CanViewPriceAsync(MenuCodes.SalesItemCust, cancellationToken);
        return PriceView(new SaItemCustEditVm
        {
            CustCode = entity.CustCode,
            ICode = entity.ICode,
            IDesc = entity.IDesc,
            CustICode = entity.CustICode,
            InvDesc = entity.InvDesc,
            SellingUOM = entity.SellingUOM,
            MOQ = entity.MOQ,
            UnitPrice = canViewPrice ? ScaleLegacyMoney(entity.UnitPrice) : null,
            Currency = entity.Currency,
            StdCustPSize = ScaleLegacyMoney(entity.StdCustPSize),
            Status = entity.Status,
            DG = entity.DG,
            SG = entity.SG,
            ProjID = entity.ProjID,
            CustModel = entity.CustModel,
            RowVersion = entity.RowVersion ?? []
        });
    }

    /// <summary>
    /// Saves a customer item. The key (customer, item, UOM, MOQ) is the identity: editing a key field is
    /// a validation error telling the caller to create a new row, which is the fix for the legacy defect
    /// where a changed key silently inserted a second row.
    /// </summary>
    public async Task<IvMasterOperationResult<SaItemCustEditVm>> SaveItemCustAsync(
        SaItemCustEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<SaItemCustEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesItemCust, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaItemCustEditVm>(ctx.Error.Value);
        }

        var company = ctx.CompanyCode!;
        var errors = NewErrors();

        var custCode = ValidateAndNormalizeCode(errors, nameof(model.CustCode), "Customer", model.CustCode, CustCodeMax);
        var iCode = ValidateAndNormalizeCode(errors, nameof(model.ICode), "Item", model.ICode, ItemCodeMax);
        var uom = ValidateAndNormalizeCode(errors, nameof(model.SellingUOM), "Selling UOM", model.SellingUOM, UomMax);
        var custICode = ValidateAndNormalizeCode(errors, nameof(model.CustICode), "Customer item code", model.CustICode, ItemCustDescMax);

        if (model.MOQ < 0)
        {
            errors[nameof(model.MOQ)] = "MOQ cannot be negative.";
        }

        if (model.UnitPrice is < 0m)
        {
            errors[nameof(model.UnitPrice)] = "Unit price cannot be negative.";
        }

        var currency = NormalizeOptionalCode(model.Currency);
        if (currency.Length > UomMax)
        {
            errors[nameof(model.Currency)] = $"Currency must be at most {UomMax} characters.";
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var exists = await db.SaItemCusts.AsNoTracking()
            .AnyAsync(x => x.CompanyCode == company && x.CustCode == custCode && x.ICode == iCode
                           && x.SellingUOM == uom && x.MOQ == model.MOQ, cancellationToken);

        // VIEW_PRICE governs visibility, never persistence: without it the caller cannot set a price and
        // cannot erase one either (the stored value survives the round trip).
        var canViewPrice = await CanViewPriceAsync(MenuCodes.SalesItemCust, cancellationToken);
        decimal? effectiveUnitPrice = canViewPrice ? model.UnitPrice : null;
        if (!canViewPrice && !isNew)
        {
            var storedPrice = await db.SaItemCusts.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.CustCode == custCode && x.ICode == iCode
                            && x.SellingUOM == uom && x.MOQ == model.MOQ)
                .Select(x => x.UnitPrice)
                .FirstOrDefaultAsync(cancellationToken);
            effectiveUnitPrice = ScaleLegacyMoney(storedPrice);
        }

        if (exists && isNew)
        {
            errors[nameof(model.MOQ)] = "This customer, item, UOM and MOQ already exists.";
        }

        if (!exists && !isNew)
        {
            errors[nameof(model.ICode)] = "The key fields of an existing row cannot be changed — create a new row instead.";
        }

        if (isNew)
        {
            var customers = await CustomerCodesAsync(db, company, cancellationToken);
            if (custCode.Length > 0 && !customers.Contains(custCode))
            {
                errors[nameof(model.CustCode)] = $"Customer {custCode} does not exist.";
            }

            var items = await ActiveItemCodesAsync(db, company, cancellationToken);
            if (iCode.Length > 0 && !items.Contains(iCode))
            {
                errors[nameof(model.ICode)] = $"Item {iCode} does not exist or is not active.";
            }

            var uoms = await ActiveUomCodesAsync(db, company, cancellationToken);
            if (uom.Length > 0 && !uoms.Contains(uom))
            {
                errors[nameof(model.SellingUOM)] = $"UOM {uom} does not exist or is not active.";
            }
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<SaItemCustEditVm>.Fail(IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<SaItemCustEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        var now = DateTime.UtcNow;
        var user = ctx.UserId!;

        try
        {
            if (isNew)
            {
                var entity = new SaItemCust
                {
                    CompanyCode = company,
                    CustCode = custCode,
                    ICode = iCode,
                    SellingUOM = uom,
                    MOQ = model.MOQ,
                    IDesc = model.IDesc?.Trim().ToUpperInvariant(),
                    CustICode = custICode,
                    InvDesc = string.IsNullOrWhiteSpace(model.InvDesc) ? null : model.InvDesc.Trim(),
                    UnitPrice = UnscaleLegacyMoney(effectiveUnitPrice),
                    Currency = currency.Length == 0 ? null : currency,
                    StdCustPSize = UnscaleLegacyMoney(model.StdCustPSize),
                    // Legacy parity: a saved row is flagged NEW (the refresh flow owns FALSE).
                    Status = string.IsNullOrWhiteSpace(model.Status) ? "NEW" : model.Status.Trim().ToUpperInvariant(),
                    DG = model.DG,
                    SG = model.SG,
                    ProjID = model.ProjID,
                    CustModel = model.CustModel,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                db.SaItemCusts.Add(entity);
            }
            else
            {
                var entity = await db.SaItemCusts
                    .FirstOrDefaultAsync(x => x.CompanyCode == company && x.CustCode == custCode && x.ICode == iCode
                                              && x.SellingUOM == uom && x.MOQ == model.MOQ, cancellationToken)
                    ?? null!;
                if (entity is null)
                {
                    return FailVm<SaItemCustEditVm>(IvMasterErrorCode.NotFound, "Customer item not found.");
                }

                var entry = db.Entry(entity);
                var current = entry.Property("RowVersion").CurrentValue as byte[];
                if (!RowVersionsEqual(current, model.RowVersion))
                {
                    return FailVm<SaItemCustEditVm>(IvMasterErrorCode.Concurrency, "This row was changed by another user. Reload and try again.");
                }

                entry.Property("RowVersion").OriginalValue = model.RowVersion;
                entity.IDesc = model.IDesc?.Trim().ToUpperInvariant();
                entity.CustICode = custICode;
                entity.InvDesc = string.IsNullOrWhiteSpace(model.InvDesc) ? null : model.InvDesc.Trim();
                entity.UnitPrice = UnscaleLegacyMoney(effectiveUnitPrice);
                entity.Currency = currency.Length == 0 ? null : currency;
                entity.StdCustPSize = UnscaleLegacyMoney(model.StdCustPSize);
                entity.DG = model.DG;
                entity.SG = model.SG;
                entity.ProjID = model.ProjID;
                entity.CustModel = model.CustModel;
                entity.ModifiedDate = now;
                entity.ModifiedBy = user;
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm<SaItemCustEditVm>(IvMasterErrorCode.Concurrency, "This row was changed by another user. Reload and try again.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<SaItemCustEditVm>(IvMasterErrorCode.DuplicateKey, "This customer, item, UOM and MOQ already exists.", nameof(model.MOQ));
        }

        return await GetItemCustAsync(
            new SaItemCustKey { CustCode = custCode, ICode = iCode, SellingUOM = uom, MOQ = model.MOQ },
            cancellationToken);
    }

    /// <summary>No consumer writes against this master yet, so the answer says so explicitly (§10.5).</summary>
    public async Task<DeleteCheckResult> CanDeleteItemCustsAsync(
        IReadOnlyList<SaItemCustKey> keys,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesItemCust, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        if (keys is null || keys.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        return DeleteCheckResult.Ok(
            "No module consumes customer items yet, so no reference check is possible. The row will be deleted.");
    }

    public Task<IvMasterOperationResult<object>> DeleteItemCustsAsync(
        IReadOnlyList<SaItemFamilyKeyToken> items,
        CancellationToken cancellationToken = default) =>
        DeleteItemFamilyAsync<SaItemCust>(
            MenuCodes.SalesItemCust,
            items,
            async (db, company, codes, ct) =>
            {
                var custCodes = codes
                    .Select(c => KeyPart(c, 0))
                    .Where(c => c.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var rows = await db.SaItemCusts
                    .Where(x => x.CompanyCode == company && custCodes.Contains(x.CustCode))
                    .ToListAsync(ct);

                return rows
                    .Where(x => codes.Contains(EncodeItemFamilyKey(x.CustCode, x.ICode, x.SellingUOM, x.MOQ)))
                    .ToList();
            },
            (entity, key) => KeysEqual(EncodeItemFamilyKey(entity.CustCode, entity.ICode, entity.SellingUOM, entity.MOQ), key),
            (_, _, _, _) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>>(
                new Dictionary<string, IReadOnlyList<IvReferenceCount>>(StringComparer.OrdinalIgnoreCase)),
            cancellationToken);
}
