using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Planning;

public sealed class PrProductDefService : IPrProductDefService
{
    private const int CodeMax = 30;
    private const int NameMax = 200;
    private const int UomMax = 10;
    private const int WarehouseMax = 20;
    private const int PrefixMax = 10;
    private const int HeaderRemarkMax = 1000;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IUomConversionService _uom;

    public PrProductDefService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IUomConversionService uom)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _uom = uom;
    }

    // ── Temporary Phase-2 interface adapters (definition-aware signatures) ────────────────────
    // Full multi-definition identity wiring belongs to Phase 2; these overloads unblock Core compile
    // and preserve current product-only behavior until that service rewrite lands.

    public Task<IvMasterOperationResult<PrProductDefEditVm>> GetAsync(
        string prodCode,
        string definitionCode,
        int? version = null,
        CancellationToken cancellationToken = default) =>
        GetAsync(prodCode, version, cancellationToken);

    public Task<IvMasterOperationResult<PrBomStructureResult>> GetStructureTreeAsync(
        string prodCode,
        string definitionCode,
        int? version = null,
        CancellationToken cancellationToken = default) =>
        GetStructureTreeAsync(prodCode, version, cancellationToken);

    public Task<IvMasterOperationResult<PrProductDefEditVm>> CreateNewVersionAsync(
        string prodCode,
        string definitionCode,
        int? fromVersion = null,
        CancellationToken cancellationToken = default) =>
        CreateNewVersionAsync(prodCode, fromVersion, cancellationToken);

    public Task<IvMasterOperationResult<PrProductDefEditVm>> ActivateAsync(
        string prodCode,
        string definitionCode,
        int version,
        byte[]? headerRowVersion,
        CancellationToken cancellationToken = default) =>
        ActivateAsync(prodCode, version, effectiveFrom: null, effectiveTo: null, headerRowVersion, cancellationToken);

    public async Task<IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>> ListActiveDefinitionsAsync(
        string prodCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>.Fail(
                ctx.Error.Code, ctx.Error.Message);
        }

        var code = NormalizeCode(prodCode);
        if (code.Length == 0)
        {
            return IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>.Fail(
                IvMasterErrorCode.Validation, "Product code is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode && x.ProdCode == code && x.Status == PrBomStatuses.Active)
            .OrderByDescending(x => x.IsDefaultDefinition)
            .ThenBy(x => x.DefinitionCode)
            .Select(x => new PrProductDefinitionLookupRow
            {
                BomHdrId = x.Uid,
                ProdCode = x.ProdCode,
                DefinitionCode = x.DefinitionCode,
                DefinitionName = x.DefinitionName,
                Version = x.Version,
                ActiveVersion = x.Version,
                LatestVersion = x.Version,
                LatestStatus = x.Status,
                IsDefaultDefinition = x.IsDefaultDefinition
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>.Ok(rows);
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>> ListDefinitionsAsync(
        string prodCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>.Fail(
                ctx.Error.Code, ctx.Error.Message);
        }

        var code = NormalizeCode(prodCode);
        if (code.Length == 0)
        {
            return IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>.Fail(
                IvMasterErrorCode.Validation, "Product code is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var headers = await db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode && x.ProdCode == code)
            .ToListAsync(cancellationToken);

        var rows = headers
            .GroupBy(x => string.IsNullOrWhiteSpace(x.DefinitionCode)
                ? PrProductDefinitionCodes.Standard
                : x.DefinitionCode, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var active = g.FirstOrDefault(x => x.Status == PrBomStatuses.Active);
                var latest = g.OrderByDescending(x => x.Version).First();
                return new PrProductDefinitionLookupRow
                {
                    BomHdrId = active?.Uid ?? latest.Uid,
                    ProdCode = code,
                    DefinitionCode = g.Key,
                    DefinitionName = active?.DefinitionName ?? latest.DefinitionName,
                    Version = active?.Version ?? latest.Version,
                    ActiveVersion = active?.Version,
                    LatestVersion = latest.Version,
                    LatestStatus = latest.Status,
                    IsDefaultDefinition = active?.IsDefaultDefinition ?? latest.IsDefaultDefinition
                };
            })
            .OrderByDescending(x => x.IsDefaultDefinition)
            .ThenBy(x => x.DefinitionCode, StringComparer.Ordinal)
            .ToList();

        return IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>.Ok(rows);
    }

    public Task<IvMasterOperationResult<DeleteCheckResult>> CanDeleteAsync(
        IReadOnlyList<PrProductDefinitionKey> keys,
        CancellationToken cancellationToken = default) =>
        CanDeleteAsync(
            keys.Select(k => k.ProdCode).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            cancellationToken);

    public Task<IvMasterOperationResult<object?>> DeleteAsync(
        IReadOnlyList<PrProductDefinitionKey> keys,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            keys.Select(k => k.ProdCode).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            cancellationToken);

    public async Task<IvMasterOperationResult<PrProductDefListPage>> SearchAsync(
        PrProductDefListQuery query,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailPage(ctx.Error);
        }

        query ??= new PrProductDefListQuery();
        var take = query.Take <= 0 ? 20 : Math.Min(query.Take, 500);
        var skip = Math.Max(0, query.Skip);
        var company = ctx.CompanyCode!;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Latest version per product (by Version desc)
        var headers = db.PrBomHdrs.AsNoTracking().Where(x => x.CompanyCode == company);
        var latest = from h in headers
                     group h by h.ProdCode into g
                     select new
                     {
                         ProdCode = g.Key,
                         Version = g.Max(x => x.Version)
                     };

        var joined = from l in latest
                     join h in headers on new { l.ProdCode, l.Version } equals new { h.ProdCode, h.Version }
                     join s in db.IvStockMasters.AsNoTracking().Where(x => x.CompanyCode == company)
                         on h.ProdCode equals s.ICode into sj
                     from s in sj.DefaultIfEmpty()
                     select new
                     {
                         h.ProdCode,
                         ProdDesc = s != null ? s.IDesc : null,
                         StdUom = s != null ? s.StdUom : null,
                         MfgType = s != null ? s.MfgType : PrMfgTypes.Buy,
                         IsActive = s != null && s.IsActive,
                         BomVersion = h.Version,
                         BomStatus = h.Status,
                         BomItemCount = db.PrDefBOMs.Count(b => b.BomHdrId == h.Uid),
                         h.CreatedDate,
                         h.CreatedBy,
                         h.ModifiedDate,
                         h.ModifiedBy,
                         h.Uid
                     };

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            joined = joined.Where(x =>
                x.ProdCode.Contains(term)
                || (x.ProdDesc != null && x.ProdDesc.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(query.ProdCode))
        {
            var code = query.ProdCode.Trim();
            joined = joined.Where(x => x.ProdCode.Contains(code));
        }

        if (!string.IsNullOrWhiteSpace(query.ProdDesc))
        {
            var desc = query.ProdDesc.Trim();
            joined = joined.Where(x => x.ProdDesc != null && x.ProdDesc.Contains(desc));
        }

        if (query.IsActive is bool active)
        {
            joined = joined.Where(x => x.IsActive == active);
        }

        if (!string.IsNullOrWhiteSpace(query.ComponentCode)
            || !string.IsNullOrWhiteSpace(query.ComponentDesc)
            || !string.IsNullOrWhiteSpace(query.Warehouse))
        {
            var componentCode = query.ComponentCode?.Trim();
            var componentDesc = query.ComponentDesc?.Trim();
            var warehouse = query.Warehouse?.Trim();
            var bom = db.PrDefBOMs.AsNoTracking().Where(x => x.CompanyCode == company);

            joined = joined.Where(p => bom.Any(b =>
                b.BomHdrId == p.Uid
                && (componentCode == null || componentCode.Length == 0 || b.ICode.Contains(componentCode))
                && (componentDesc == null || componentDesc.Length == 0
                    || (b.IName != null && b.IName.Contains(componentDesc)))
                && (warehouse == null || warehouse.Length == 0
                    || (b.Warehouse != null && b.Warehouse == warehouse))));
        }

        var sortField = (query.SortField ?? nameof(PrProductDefListRow.ProdCode)).Trim();
        joined = (sortField.ToUpperInvariant(), query.SortDescending) switch
        {
            ("PRODDESC", true) => joined.OrderByDescending(x => x.ProdDesc).ThenByDescending(x => x.ProdCode),
            ("PRODDESC", false) => joined.OrderBy(x => x.ProdDesc).ThenBy(x => x.ProdCode),
            ("BOMITEMCOUNT", true) => joined.OrderByDescending(x => x.BomItemCount).ThenBy(x => x.ProdCode),
            ("BOMITEMCOUNT", false) => joined.OrderBy(x => x.BomItemCount).ThenBy(x => x.ProdCode),
            ("ISACTIVE", true) => joined.OrderByDescending(x => x.IsActive).ThenBy(x => x.ProdCode),
            ("ISACTIVE", false) => joined.OrderBy(x => x.IsActive).ThenBy(x => x.ProdCode),
            ("BOMVERSION", true) => joined.OrderByDescending(x => x.BomVersion).ThenBy(x => x.ProdCode),
            ("BOMVERSION", false) => joined.OrderBy(x => x.BomVersion).ThenBy(x => x.ProdCode),
            (_, true) => joined.OrderByDescending(x => x.ProdCode),
            _ => joined.OrderBy(x => x.ProdCode)
        };

        var total = await joined.CountAsync(cancellationToken);
        var rows = await joined
            .Skip(skip)
            .Take(take)
            .Select(x => new PrProductDefListRow
            {
                ProdCode = x.ProdCode,
                ProdDesc = x.ProdDesc,
                StdUom = x.StdUom,
                MfgType = x.MfgType,
                BomVersion = x.BomVersion,
                BomStatus = x.BomStatus,
                BomItemCount = x.BomItemCount,
                IsActive = x.IsActive,
                CreatedDate = x.CreatedDate,
                CreatedBy = x.CreatedBy,
                ModifiedDate = x.ModifiedDate,
                ModifiedBy = x.ModifiedBy
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<PrProductDefListPage>.Ok(new PrProductDefListPage
        {
            Rows = rows,
            TotalCount = total
        });
    }

    public async Task<IvMasterOperationResult<PrProductDefEditVm>> GetAsync(
        string prodCode,
        int? version = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm(ctx.Error);
        }

        var code = NormalizeCode(prodCode);
        if (code.Length == 0)
        {
            return FailVm(IvMasterErrorCode.Validation, "Product code is required.", "ProdCode");
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var header = await ResolveHeaderForEditAsync(db, company, code, version, cancellationToken);
        if (header is null)
        {
            return FailVm(IvMasterErrorCode.NotFound, "Product definition not found.");
        }

        var lines = await db.PrDefBOMs.AsNoTracking()
            .Where(x => x.BomHdrId == header.Uid)
            .OrderBy(x => x.SeqNo).ThenBy(x => x.ICode)
            .ToListAsync(cancellationToken);
        var operations = await db.PrBomOperations.AsNoTracking()
            .Where(x => x.BomHdrId == header.Uid)
            .Include(x => x.Machines)
                .ThenInclude(x => x.Labours)
            .OrderBy(x => x.CentralSequence)
            .ThenBy(x => x.ProcessSequence)
            .ThenBy(x => x.OperationCode)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        var routeSteps = await db.PrBomRouteSteps.AsNoTracking()
            .Where(x => x.BomHdrId == header.Uid)
            .ToListAsync(cancellationToken);
        var routeStepKeyById = routeSteps
            .GroupBy(x => x.Uid)
            .ToDictionary(g => g.Key, g => g.First().RouteStepKey);

        var product = await db.IvStockMasters.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.ICode == code, cancellationToken);

        var componentCodes = lines.Select(x => x.ICode).Distinct().ToList();
        var mfgByCode = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && componentCodes.Contains(x.ICode))
            .Select(x => new { x.ICode, x.MfgType })
            .ToDictionaryAsync(x => x.ICode, x => PrMfgTypes.Normalize(x.MfgType),
                StringComparer.OrdinalIgnoreCase, cancellationToken);

        var workCentreCodes = operations.Select(x => x.WorkCentreCode).Distinct().ToList();
        var operationCodes = operations.Select(x => x.OperationCode).Distinct().ToList();
        var wcDescriptions = await db.PrWorkCentres.AsNoTracking()
            .Where(x => x.CompCode == company && workCentreCodes.Contains(x.WrkCtrCd))
            .ToDictionaryAsync(x => x.WrkCtrCd, x => x.WrkCtrDes, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var operationDescriptions = await db.PrProcesses.AsNoTracking()
            .Where(x => x.CompCode == company && operationCodes.Contains(x.ProcessCd))
            .GroupBy(x => x.ProcessCd)
            .ToDictionaryAsync(x => x.Key, x => x.First().ProcessDes, StringComparer.OrdinalIgnoreCase, cancellationToken);

        return IvMasterOperationResult<PrProductDefEditVm>.Ok(
            MapEdit(header, product, lines, mfgByCode, operations, routeStepKeyById, wcDescriptions, operationDescriptions));
    }

    public async Task<IvMasterOperationResult<PrBomStructureResult>> GetStructureTreeAsync(
        string prodCode,
        int? version = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailStructure(ctx.Error);
        }

        var code = NormalizeCode(prodCode);
        if (code.Length == 0)
        {
            return FailStructure(IvMasterErrorCode.Validation, "Product code is required.", "ProdCode");
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var rootHeader = await ResolveHeaderForEditAsync(db, company, code, version, cancellationToken);
        if (rootHeader is null)
        {
            return FailStructure(IvMasterErrorCode.NotFound, "Product definition not found.");
        }

        var rootItem = await db.IvStockMasters.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.ICode == code, cancellationToken);

        var cache = new StructureLoadCache();
        cache.Headers[(code, rootHeader.Version)] = rootHeader;
        if (rootItem is not null)
        {
            cache.Items[code] = new StructureItemInfo(
                code,
                rootItem.IDesc,
                PrMfgTypes.Normalize(rootItem.MfgType),
                rootItem.StdUom);
        }

        var nodes = new List<PrBomStructureNode>();
        var rootKey = PrBomStructureKeys.Root(code);
        nodes.Add(new PrBomStructureNode
        {
            Key = rootKey,
            ParentKey = null,
            Level = 0,
            ItemCode = code,
            ItemDesc = rootItem?.IDesc,
            MfgType = PrMfgTypes.Normalize(rootItem?.MfgType),
            StdQty = rootHeader.BaseQty,
            StdUom = rootHeader.BaseUom ?? rootItem?.StdUom,
            OwnerProdCode = null,
            BomHdrId = rootHeader.Uid,
            BomVersion = rootHeader.Version,
            BomStatus = rootHeader.Status,
            Status = PrBomStructureNodeStatus.Normal
        });

        var pathStack = new List<string> { code };
        await WalkStructureAsync(
            db,
            company,
            ownerProdCode: code,
            ownerHeader: rootHeader,
            parentKey: rootKey,
            level: 0,
            pathStack,
            nodes,
            cache,
            cancellationToken);

        return IvMasterOperationResult<PrBomStructureResult>.Ok(new PrBomStructureResult
        {
            RootProdCode = code,
            RootBomHdrId = rootHeader.Uid,
            RootBomVersion = rootHeader.Version,
            RootBomStatus = rootHeader.Status,
            Nodes = nodes
        });
    }

    public async Task<IvMasterOperationResult<PrProductDefEditVm>> SaveAsync(
        PrProductDefEditVm model,
        bool isNew,
        bool activate,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm(ctx.Error);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm(IvMasterErrorCode.InvalidScope, "Invalid company / branch / location context.");
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var prodCode = ValidateCode(errors, nameof(model.ProdCode), "Product code", model.ProdCode, CodeMax);
        var lines = model.Lines ?? [];
        if (lines.Count == 0 && (model.Operations?.Count ?? 0) == 0)
        {
            errors[nameof(model.Lines)] = "Add at least one process or BOM component.";
        }

        var baseQty = IvQty.Round(model.BaseQty);
        if (baseQty <= 0m)
        {
            errors[nameof(model.BaseQty)] = "Base quantity must be greater than zero.";
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        IvStockMaster? product = null;
        if (prodCode.Length > 0)
        {
            product = await db.IvStockMasters
                .FirstOrDefaultAsync(x => x.CompanyCode == company && x.ICode == prodCode, cancellationToken);
            if (product is null)
            {
                errors[nameof(model.ProdCode)] = "Product does not exist in Stock Master.";
            }
            else if (!product.IsActive)
            {
                errors[nameof(model.ProdCode)] = "Product is inactive.";
            }
        }

        PrBomHdr? existingHeader = null;
        if (!isNew)
        {
            existingHeader = await db.PrBomHdrs
                .FirstOrDefaultAsync(x => x.CompanyCode == company
                                          && x.ProdCode == prodCode
                                          && x.Version == model.Version, cancellationToken);
            if (existingHeader is null)
            {
                return FailVm(IvMasterErrorCode.NotFound, "Product definition not found.");
            }

            if (existingHeader.Status is PrBomStatuses.Active or PrBomStatuses.Superseded or PrBomStatuses.Inactive)
            {
                return FailVm(IvMasterErrorCode.Validation,
                    $"Product Definition version {model.Version} is {existingHeader.Status} and immutable. Create a new draft version.");
            }

            if (model.HeaderRowVersion is { Length: > 0 }
                && existingHeader.RowVersion is { Length: > 0 }
                && !model.HeaderRowVersion.SequenceEqual(existingHeader.RowVersion))
            {
                return FailVm(IvMasterErrorCode.Concurrency,
                    "This product definition was changed by another user. Reload and try again.");
            }
        }
        else
        {
            var any = await db.PrBomHdrs.AsNoTracking()
                .AnyAsync(x => x.CompanyCode == company && x.ProdCode == prodCode, cancellationToken);
            if (any)
            {
                errors[nameof(model.ProdCode)] = "A product definition already exists for this product. Use Create New Version.";
            }
        }

        var mfgType = product is null ? PrMfgTypes.Buy : PrMfgTypes.Normalize(product.MfgType);
        if (mfgType == PrMfgTypes.Buy)
        {
            mfgType = PrMfgTypes.Make; // first BOM save promotes BUY → MAKE
        }

        if (mfgType is not (PrMfgTypes.Make or PrMfgTypes.Phantom))
        {
            errors[nameof(model.MfgType)] = "A BOM requires MfgType MAKE or PHANTOM.";
        }

        var activeWarehouses = await db.IvWarehouses.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.BranchCode == writeScope.BranchCode
                        && x.IsActive)
            .Select(x => x.WarehouseCode)
            .ToListAsync(cancellationToken);
        var warehouseSet = new HashSet<string>(activeWarehouses, StringComparer.OrdinalIgnoreCase);

        var activeItems = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive)
            .Select(x => new { x.ICode, x.IDesc, x.StdUom, x.MfgType, x.DefWarehouse })
            .ToListAsync(cancellationToken);
        var itemByCode = activeItems.ToDictionary(x => x.ICode, StringComparer.OrdinalIgnoreCase);
        var normalizedOperations = await ValidateOperationsAsync(
            db,
            company,
            prodCode,
            model.Operations ?? [],
            activate || existingHeader?.Status == PrBomStatuses.Active,
            itemByCode.ToDictionary(x => x.Key, x => x.Value.StdUom, StringComparer.OrdinalIgnoreCase),
            errors,
            cancellationToken);

        var seenComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<(string ICode, string? IName, decimal StdQty, string? StdUom, int SeqNo, decimal ScrapPercent, string Warehouse, bool BomDefault, string? AlternateGroupCode, decimal Tolerance, bool WipBomDefault, Guid? OperationKey, string IssueMethod, string SupplySource, Guid? ProducingRouteStepKey)>();
        var operationKeys = normalizedOperations.Select(x => x.OperationKey).ToHashSet();
        var defaultsByGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        // Route steps that already exist for this revision. A material may only point at one of
        // these; a brand-new revision gains step keys after its first save.
        var persistedRouteSteps = existingHeader is null
            ? []
            : await db.PrBomRouteSteps.AsNoTracking()
                .Where(x => x.BomHdrId == existingHeader.Uid)
                .ToListAsync(cancellationToken);
        var availableStepOutputs = KnownRouteStepOutputItems(persistedRouteSteps);
        // Reuse persisted step keys on re-save so producer references and client state stay stable.
        var existingStepKeys = persistedRouteSteps
            .GroupBy(x => (x.WorkCentreCode, x.OutputItemCode, x.StageSequence))
            .ToDictionary(g => g.Key, g => g.First().RouteStepKey);
        if (operationKeys.Contains(Guid.Empty) || operationKeys.Count != normalizedOperations.Count)
            errors["Operations"] = "Each process must have a unique, non-empty identity.";

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var label = $"Line {i + 1}";
            if (line.OperationKey is { } owner && !operationKeys.Contains(owner))
                errors[$"Lines[{i}].OperationKey"] = $"{label}: the owning process is missing.";
            // Preserve standalone legacy BOMs; routed definitions require explicit ownership.
            if (activate && normalizedOperations.Count > 0 && line.OperationKey is null)
                errors[$"Lines[{i}].OperationKey"] = $"{label}: assign this material to its consuming process before activation.";
            var iCode = ValidateCode(errors, $"Lines[{i}].ICode", $"{label} item", line.ICode, CodeMax);

            if (iCode.Length > 0 && string.Equals(iCode, prodCode, StringComparison.OrdinalIgnoreCase))
            {
                errors[$"Lines[{i}].ICode"] = $"{label}: component cannot be the same as the finished product.";
            }

            if (iCode.Length > 0 && !seenComponents.Add($"{line.OperationKey}|{iCode}"))
            {
                errors[$"Lines[{i}].ICode"] = $"{label}: duplicate component {iCode}.";
            }

            if (iCode.Length > 0 && !itemByCode.ContainsKey(iCode))
            {
                errors[$"Lines[{i}].ICode"] = $"{label}: item {iCode} does not exist or is not active.";
            }

            var stdQty = IvQty.Round(line.StdQty);
            if (stdQty <= 0m)
            {
                errors[$"Lines[{i}].StdQty"] = $"{label}: standard quantity must be greater than zero.";
            }

            var scrap = IvQty.Round(line.ScrapPercent);
            if (scrap < 0m)
            {
                errors[$"Lines[{i}].ScrapPercent"] = $"{label}: scrap percent cannot be negative.";
            }

            var tolerance = IvQty.Round(line.Tolerance);
            if (tolerance < 0m)
            {
                errors[$"Lines[{i}].Tolerance"] = $"{label}: tolerance cannot be negative.";
            }

            var warehouse = (line.Warehouse ?? string.Empty).Trim();
            if (warehouse.Length == 0)
            {
                errors[$"Lines[{i}].Warehouse"] = $"{label}: warehouse is required.";
            }
            else if (warehouse.Length > WarehouseMax)
            {
                errors[$"Lines[{i}].Warehouse"] = $"{label}: warehouse is too long.";
            }
            else if (!warehouseSet.Contains(warehouse))
            {
                errors[$"Lines[{i}].Warehouse"] = $"{label}: warehouse {warehouse} is not an active warehouse for this branch.";
            }

            var issueMethod = (line.IssueMethod ?? PrMaterialIssueMethods.Manual).Trim().ToUpperInvariant();
            if (!PrMaterialIssueMethods.IsValid(issueMethod))
                errors[$"Lines[{i}].IssueMethod"] = $"{label}: select a valid issue method.";

            var supplySource = (line.SupplySource ?? PrMaterialSupplySources.Purchased).Trim().ToUpperInvariant();
            if (!PrMaterialSupplySources.IsValid(supplySource))
                errors[$"Lines[{i}].SupplySource"] = $"{label}: select a valid supply source.";

            Guid? producingKey = null;
            if (line.ProducingRouteStepKey is { } candidate && candidate != Guid.Empty)
            {
                if (!availableStepOutputs.TryGetValue(candidate, out var producerOutput))
                {
                    errors[$"Lines[{i}].ProducingRouteStepKey"] =
                        $"{label}: the producing route step does not belong to this revision.";
                }
                else if (!string.Equals(producerOutput, iCode, StringComparison.OrdinalIgnoreCase))
                {
                    errors[$"Lines[{i}].ProducingRouteStepKey"] =
                        $"{label}: route step outputs {producerOutput}, not component {iCode}.";
                }
                else
                {
                    producingKey = candidate;
                }
            }

            if (string.Equals(supplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal))
            {
                if (activate && producingKey is null)
                    errors[$"Lines[{i}].ProducingRouteStepKey"] =
                        $"{label}: select the in-house route step that produces {iCode} before activation.";
            }
            else if (producingKey is not null)
            {
                errors[$"Lines[{i}].ProducingRouteStepKey"] =
                    $"{label}: a producing route step is only allowed for INTERNAL_ROUTE_WIP material.";
            }

            var alternateGroup = PrBomAlternateGroups.Normalize(line.AlternateGroupCode);
            if (alternateGroup is null)
            {
                // New saves always require an explicit group. Singleton defaults default to ICode.
                alternateGroup = iCode.Length > 0 ? iCode : null;
            }

            if (alternateGroup is null || !PrBomAlternateGroups.IsValidLength(alternateGroup))
            {
                errors[$"Lines[{i}].AlternateGroupCode"] =
                    $"{label}: alternate group is required (max {PrBomAlternateGroups.MaxLength} characters).";
            }
            else
            {
                var groupKey = $"{line.OperationKey}|{alternateGroup}";
                if (line.BomDefault)
                {
                    defaultsByGroup[groupKey] = defaultsByGroup.GetValueOrDefault(groupKey) + 1;
                }
            }

            if (itemByCode.TryGetValue(iCode, out var item))
            {
                bool wip = false;
                if (existingHeader is not null)
                {
                    var prior = await db.PrDefBOMs.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.BomHdrId == existingHeader.Uid
                                                  && x.ICode == iCode && x.OperationKey == line.OperationKey, cancellationToken);
                    wip = prior?.WipBomDefault ?? false;
                }

                var inventoryUom = Truncate(item.StdUom, UomMax);
                var authoredUom = Truncate(
                    string.IsNullOrWhiteSpace(line.StdUom) ? item.StdUom : line.StdUom, UomMax);
                if (!string.IsNullOrWhiteSpace(authoredUom)
                    && !string.IsNullOrWhiteSpace(inventoryUom)
                    && !string.Equals(authoredUom, inventoryUom, StringComparison.OrdinalIgnoreCase)
                    && !await _uom.HasConversionAsync(company, iCode, authoredUom, inventoryUom, cancellationToken))
                {
                    errors[$"Lines[{i}].StdUom"] =
                        $"{label}: no approved conversion from {authoredUom} to inventory UOM {inventoryUom} ({UomConversionFailureCodes.MissingConversion}).";
                }

                if (!errors.Keys.Any(k => k.StartsWith($"Lines[{i}].", StringComparison.Ordinal)))
                {
                    normalized.Add((
                        iCode,
                        Truncate(item.IDesc, NameMax),
                        stdQty,
                        authoredUom,
                        line.SeqNo > 0 ? line.SeqNo : i + 1,
                        scrap,
                        warehouse,
                        line.BomDefault,
                        alternateGroup,
                        tolerance,
                        wip,
                        line.OperationKey,
                        issueMethod,
                        supplySource,
                        producingKey));
                }
            }
        }

        foreach (var line in normalized.Where(x => !x.BomDefault))
        {
            var groupKey = $"{line.OperationKey}|{line.AlternateGroupCode}";
            if (defaultsByGroup.GetValueOrDefault(groupKey) != 1)
            {
                errors[nameof(model.Lines)] =
                    $"Alternate group {line.AlternateGroupCode} must have exactly one default material.";
                break;
            }
        }

        foreach (var (groupKey, count) in defaultsByGroup)
        {
            if (count > 1)
            {
                var groupCode = groupKey.Contains('|') ? groupKey[(groupKey.IndexOf('|') + 1)..] : groupKey;
                errors[nameof(model.Lines)] =
                    $"Alternate group {groupCode} has more than one default material.";
                break;
            }
        }

        // Circular BOM check against other products' ACTIVE graphs (company-scoped)
        if (errors.Count == 0 && prodCode.Length > 0)
        {
            var graph = await BuildActiveComponentGraphAsync(db, company, excludeProdCode: null, cancellationToken);
            var path = CircularBomValidator.FindCyclePath(
                prodCode,
                normalized.Select(x => x.ICode).ToList(),
                graph);
            if (path is not null)
            {
                errors[nameof(model.Lines)] =
                    $"Cannot save BOM for {prodCode} because it creates a circular BOM: {path}.";
            }
        }

        var productInventoryUom = Truncate(product?.StdUom, UomMax);
        var requestedBaseUom = Truncate(model.BaseUom ?? product?.StdUom, UomMax);
        if (prodCode.Length > 0
            && !string.IsNullOrWhiteSpace(requestedBaseUom)
            && !string.IsNullOrWhiteSpace(productInventoryUom)
            && !string.Equals(requestedBaseUom, productInventoryUom, StringComparison.OrdinalIgnoreCase)
            && !await _uom.HasConversionAsync(company, prodCode, requestedBaseUom, productInventoryUom, cancellationToken))
        {
            errors[nameof(model.BaseUom)] =
                $"No approved conversion from Base UOM {requestedBaseUom} to inventory UOM {productInventoryUom} ({UomConversionFailureCodes.MissingConversion}).";
        }

        var targetStatus = activate ? PrBomStatuses.Active : (existingHeader?.Status ?? PrBomStatuses.Draft);
        if (activate)
        {
            targetStatus = PrBomStatuses.Active;
        }
        else if (isNew)
        {
            targetStatus = PrBomStatuses.Draft;
        }

        DateTime? effFrom = model.EffectiveFrom?.Date;
        DateTime? effTo = model.EffectiveTo?.Date;
        if (activate || targetStatus == PrBomStatuses.Active)
        {
            effFrom ??= DateTime.UtcNow.Date;
            if (effTo is not null && effFrom is not null && effTo <= effFrom)
            {
                errors[nameof(model.EffectiveTo)] = "Effective To must be after Effective From (the end date is exclusive).";
            }

            // Overlap is a hard error when editing an already-ACTIVE BOM's window.
            // Activate of a DRAFT supersedes prior ACTIVE versions instead.
            if (!activate
                && existingHeader is not null
                && existingHeader.Status == PrBomStatuses.Active
                && errors.Count == 0
                && prodCode.Length > 0)
            {
                var overlap = await FindOverlapAsync(
                    db, company, prodCode, effFrom, effTo,
                    excludeUid: existingHeader.Uid, cancellationToken);
                if (overlap is not null)
                {
                    errors[nameof(model.EffectiveFrom)] =
                        $"Effective dates overlap with BOM version {overlap.Version}.";
                }
            }
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<PrProductDefEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var now = DateTime.UtcNow;
        var user = Truncate(ctx.UserId!, 10) ?? "system";
        var baseUom = Truncate(model.BaseUom ?? product?.StdUom, UomMax);
        var prefixRaw = NormalizeCode(model.Prefix);
        var prefix = prefixRaw.Length == 0 ? null : Truncate(prefixRaw, PrefixMax);
        var remark = Truncate(string.IsNullOrWhiteSpace(model.Remark) ? null : model.Remark.Trim(), HeaderRemarkMax);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            PrBomHdr header;
            if (isNew)
            {
                header = new PrBomHdr
                {
                    CompanyCode = company,
                    ProdCode = prodCode,
                    Version = 1,
                    Status = targetStatus,
                    EffectiveFrom = targetStatus == PrBomStatuses.Active ? effFrom : model.EffectiveFrom?.Date,
                    EffectiveTo = model.EffectiveTo?.Date,
                    BaseQty = baseQty,
                    BaseUom = baseUom,
                    Prefix = prefix,
                    Remark = remark,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user,
                    RowVersion = []
                };
                InventoryLeftoverSite.Apply(header, writeScope);
                db.PrBomHdrs.Add(header);
                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                header = existingHeader!;
                if (activate && header.Status != PrBomStatuses.Active)
                {
                    await SupersedeOverlappingAsync(db, company, prodCode, header.Uid, effFrom!.Value, now, user, cancellationToken);
                    header.Status = PrBomStatuses.Active;
                    header.EffectiveFrom = effFrom;
                    header.EffectiveTo = effTo;
                }
                else if (header.Status == PrBomStatuses.Active)
                {
                    header.EffectiveFrom = effFrom ?? header.EffectiveFrom;
                    header.EffectiveTo = effTo;
                }
                else
                {
                    header.EffectiveFrom = model.EffectiveFrom?.Date;
                    header.EffectiveTo = model.EffectiveTo?.Date;
                    if (activate)
                    {
                        header.Status = PrBomStatuses.Active;
                    }
                }

                header.BaseQty = baseQty;
                header.BaseUom = baseUom;
                header.Prefix = prefix;
                header.Remark = remark;
                header.ModifiedDate = now;
                header.ModifiedBy = user;
                InventoryLeftoverSite.Apply(header, writeScope);

                var oldLines = await db.PrDefBOMs.Where(x => x.BomHdrId == header.Uid).ToListAsync(cancellationToken);
                db.PrDefBOMs.RemoveRange(oldLines);
                var oldOperations = await db.PrBomOperations
                    .Where(x => x.BomHdrId == header.Uid)
                    .ToListAsync(cancellationToken);
                db.PrBomOperations.RemoveRange(oldOperations);
                var oldRouteSteps = await db.PrBomRouteSteps
                    .Where(x => x.BomHdrId == header.Uid)
                    .ToListAsync(cancellationToken);
                db.PrBomRouteSteps.RemoveRange(oldRouteSteps);
                await db.SaveChangesAsync(cancellationToken);
            }

            if (isNew && activate)
            {
                await SupersedeOverlappingAsync(db, company, prodCode, header.Uid, effFrom!.Value, now, user, cancellationToken);
                header.Status = PrBomStatuses.Active;
                header.EffectiveFrom = effFrom;
                header.EffectiveTo = effTo;
            }

            var (operationsByKey, routeStepsByKey) = AddRoute(
                db, header, company, normalizedOperations, existingStepKeys, now, user);

            foreach (var line in normalized.OrderBy(x => x.SeqNo))
            {
                var row = new PrDefBOM
                {
                    BomHdrId = header.Uid,
                    OperationKey = line.OperationKey,
                    Operation = line.OperationKey is { } ownerKey
                        ? operationsByKey.GetValueOrDefault(ownerKey)
                        : null,
                    ProducingRouteStep = line.ProducingRouteStepKey is { } producerKey
                        ? routeStepsByKey.GetValueOrDefault(producerKey)
                        : null,
                    CompanyCode = company,
                    ProdCode = prodCode,
                    ICode = line.ICode,
                    IName = line.IName,
                    StdQty = line.StdQty,
                    StdUom = line.StdUom,
                    SeqNo = line.SeqNo,
                    ScrapPercent = line.ScrapPercent,
                    Warehouse = line.Warehouse,
                    BomDefault = line.BomDefault,
                    AlternateGroupCode = line.AlternateGroupCode,
                    WipBomDefault = line.WipBomDefault,
                    Tolerance = line.Tolerance,
                    IssueMethod = line.IssueMethod,
                    SupplySource = line.SupplySource,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user,
                    RowVersion = []
                };
                InventoryLeftoverSite.Apply(row, writeScope);
                db.PrDefBOMs.Add(row);
            }

            if (product is not null && PrMfgTypes.Normalize(product.MfgType) == PrMfgTypes.Buy)
            {
                product.MfgType = PrMfgTypes.Make;
            }
            else if (product is not null && mfgType == PrMfgTypes.Phantom
                     && PrMfgTypes.Normalize(product.MfgType) != PrMfgTypes.Phantom)
            {
                // keep phantom if already set; if promoting from buy already handled
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return await GetAsync(prodCode, header.Version, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm(IvMasterErrorCode.Concurrency,
                "This product definition was changed by another user. Reload and try again.");
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IvMasterOperationResult<PrProductDefEditVm>> CreateNewVersionAsync(
        string prodCode,
        int? fromVersion = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Add, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm(ctx.Error);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm(IvMasterErrorCode.InvalidScope, "Invalid company / branch / location context.");
        }

        var code = NormalizeCode(prodCode);
        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var source = fromVersion is int v
            ? await db.PrBomHdrs.AsNoTracking()
                .FirstOrDefaultAsync(x => x.CompanyCode == company && x.ProdCode == code && x.Version == v, cancellationToken)
            : await db.PrBomHdrs.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.ProdCode == code)
                .OrderByDescending(x => x.Version)
                .FirstOrDefaultAsync(cancellationToken);

        if (source is null)
        {
            return FailVm(IvMasterErrorCode.NotFound, "Product definition not found.");
        }

        var maxVer = await db.PrBomHdrs
            .Where(x => x.CompanyCode == company && x.ProdCode == code)
            .MaxAsync(x => x.Version, cancellationToken);

        var lines = await db.PrDefBOMs.AsNoTracking()
            .Where(x => x.BomHdrId == source.Uid)
            .OrderBy(x => x.SeqNo)
            .ToListAsync(cancellationToken);
        var sourceOperations = await db.PrBomOperations.AsNoTracking()
            .Where(x => x.BomHdrId == source.Uid)
            .Include(x => x.Machines)
                .ThenInclude(x => x.Labours)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var sourceRouteSteps = await db.PrBomRouteSteps.AsNoTracking()
            .Where(x => x.BomHdrId == source.Uid)
            .ToListAsync(cancellationToken);
        var existingStepKeys = sourceRouteSteps
            .GroupBy(x => (x.WorkCentreCode, x.OutputItemCode, x.StageSequence))
            .ToDictionary(g => g.Key, g => g.First().RouteStepKey);
        var sourceStepKeyById = sourceRouteSteps
            .GroupBy(x => x.Uid)
            .ToDictionary(g => g.Key, g => g.First().RouteStepKey);

        var now = DateTime.UtcNow;
        var user = Truncate(ctx.UserId!, 10) ?? "system";
        var header = new PrBomHdr
        {
            CompanyCode = company,
            ProdCode = code,
            Version = maxVer + 1,
            Status = PrBomStatuses.Draft,
            EffectiveFrom = null,
            EffectiveTo = null,
            BaseQty = source.BaseQty,
            BaseUom = source.BaseUom,
            Prefix = source.Prefix,
            Remark = source.Remark,
            CreatedDate = now,
            CreatedBy = user,
            ModifiedDate = now,
            ModifiedBy = user,
            RowVersion = []
        };
        InventoryLeftoverSite.Apply(header, writeScope);
        db.PrBomHdrs.Add(header);
        await db.SaveChangesAsync(cancellationToken);

        var clonedOperations = sourceOperations.Select(x => new NormalizedOperation(
            x.OperationKey,
            x.WorkCentreCode,
            x.OutputItemCode,
            x.CentralSequence,
            x.OutputBaseQty,
            x.OutputUom,
            x.OperationCode,
            x.ProcessSequence,
            x.ProcessType,
            x.StandardDurationMinutes,
            x.SetupLossQty,
            x.OperationLossQty,
            x.IsFinalOperation,
            x.Remark,
            x.Machines.Select(m => new NormalizedMachine(
                m.MachineCode,
                m.MachineDescription,
                m.ResourceSequence,
                m.IsPrimary,
                m.Priority,
                m.OutputPerCycle,
                m.CycleSeconds,
                m.ConversionSeconds,
                m.SetupSeconds,
                m.QueueSeconds,
                m.MachineRatePerHour,
                m.ParallelMachineCount,
                m.Labours.Select(l => new NormalizedLabour(
                    l.LabourCode,
                    l.LabourDescription,
                    l.CostPerOutputUnit)).ToList())).ToList())).ToList();

        // Route step keys are scoped per header, so a new version can reuse the source keys and
        // keep each material's producer reference valid without a remap.
        var (operationsByKey, routeStepsByKey) = AddRoute(
            db, header, company, clonedOperations, existingStepKeys, now, user);

        foreach (var line in lines)
        {
            var row = new PrDefBOM
            {
                BomHdrId = header.Uid,
                OperationKey = line.OperationKey,
                Operation = line.OperationKey is { } ownerKey
                    ? operationsByKey.GetValueOrDefault(ownerKey)
                    : null,
                ProducingRouteStep = line.ProducingRouteStepId is { } producerId
                                     && sourceStepKeyById.TryGetValue(producerId, out var producerKey)
                    ? routeStepsByKey.GetValueOrDefault(producerKey)
                    : null,
                CompanyCode = company,
                ProdCode = code,
                ICode = line.ICode,
                IName = line.IName,
                StdQty = line.StdQty,
                StdUom = line.StdUom,
                SeqNo = line.SeqNo,
                ScrapPercent = line.ScrapPercent,
                Warehouse = line.Warehouse,
                BomDefault = line.BomDefault,
                AlternateGroupCode = line.AlternateGroupCode,
                WipBomDefault = line.WipBomDefault,
                Tolerance = line.Tolerance,
                IssueMethod = line.IssueMethod,
                SupplySource = line.SupplySource,
                CreatedDate = now,
                CreatedBy = user,
                ModifiedDate = now,
                ModifiedBy = user,
                RowVersion = []
            };
            InventoryLeftoverSite.Apply(row, writeScope);
            db.PrDefBOMs.Add(row);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(code, header.Version, cancellationToken);
    }

    public async Task<IvMasterOperationResult<PrProductDefEditVm>> ActivateAsync(
        string prodCode,
        int version,
        DateTime? effectiveFrom,
        DateTime? effectiveTo,
        byte[]? headerRowVersion,
        CancellationToken cancellationToken = default)
    {
        var get = await GetAsync(prodCode, version, cancellationToken);
        if (!get.Succeeded || get.Data is null)
        {
            return get;
        }

        var model = get.Data;
        model.EffectiveFrom = effectiveFrom ?? model.EffectiveFrom ?? DateTime.UtcNow.Date;
        model.EffectiveTo = effectiveTo ?? model.EffectiveTo;
        model.HeaderRowVersion = headerRowVersion ?? model.HeaderRowVersion;
        return await SaveAsync(model, isNew: false, activate: true, cancellationToken);
    }

    public async Task<IvMasterOperationResult<DeleteCheckResult>> CanDeleteAsync(
        IReadOnlyList<string> prodCodes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return IvMasterOperationResult<DeleteCheckResult>.Fail(ctx.Error.Code, ctx.Error.Message);
        }

        var codes = NormalizeCodes(prodCodes);
        if (codes.Count == 0)
        {
            return IvMasterOperationResult<DeleteCheckResult>.Fail(
                IvMasterErrorCode.Validation, "Select at least one product definition.");
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rootWorkOrderRefs = await db.ProductionWorkOrders.AsNoTracking()
            .Where(x => x.CompanyCode == company && codes.Contains(x.ProductCode))
            .Select(x => new { ProductCode = x.ProductCode, WorkOrderId = x.Uid })
            .ToListAsync(cancellationToken);
        var materialWorkOrderRefs = await db.ProductionWorkOrderMaterials.AsNoTracking()
            .Where(x => x.SourceBomHeader != null
                && x.SourceBomHeader.CompanyCode == company
                && codes.Contains(x.SourceBomHeader.ProdCode))
            .Select(x => new { ProductCode = x.SourceBomHeader!.ProdCode, x.WorkOrderId })
            .Distinct()
            .ToListAsync(cancellationToken);
        var workOrderRefs = rootWorkOrderRefs
            .Concat(materialWorkOrderRefs)
            .GroupBy(x => x.ProductCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { ProductCode = g.Key, Count = g.Select(x => x.WorkOrderId).Distinct().Count() })
            .ToList();
        if (workOrderRefs.Count > 0)
        {
            var references = workOrderRefs
                .Select(x => new IvMasterReferenceHit
                {
                    ReferenceType = "Production Work Order",
                    Count = x.Count,
                    Detail = x.ProductCode
                })
                .ToList();
            return IvMasterOperationResult<DeleteCheckResult>.Ok(
                DeleteCheckResult.Blocked(
                    "Product Definition cannot be deleted because one or more Work Orders retain its BOM revision.",
                    references));
        }

        return IvMasterOperationResult<DeleteCheckResult>.Ok(
            DeleteCheckResult.Ok("Deletes BOM headers and component lines only; Stock Master items are not deleted."));
    }

    public async Task<IvMasterOperationResult<object?>> DeleteAsync(
        IReadOnlyList<string> prodCodes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return IvMasterOperationResult<object?>.Fail(ctx.Error.Code, ctx.Error.Message);
        }

        var codes = NormalizeCodes(prodCodes);
        if (codes.Count == 0)
        {
            return IvMasterOperationResult<object?>.Fail(
                IvMasterErrorCode.Validation, "Select at least one product definition.");
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var headers = await db.PrBomHdrs
            .Where(x => x.CompanyCode == company && codes.Contains(x.ProdCode))
            .ToListAsync(cancellationToken);

        if (headers.Count == 0)
        {
            return IvMasterOperationResult<object?>.Fail(
                IvMasterErrorCode.NotFound, "Product definition not found.");
        }

        var headerIds = headers.Select(x => x.Uid).ToList();
        var hasRootWorkOrderRef = await db.ProductionWorkOrders.AsNoTracking()
            .AnyAsync(x => x.CompanyCode == company && codes.Contains(x.ProductCode), cancellationToken);
        var hasMaterialSnapshotRef = await db.ProductionWorkOrderMaterials.AsNoTracking()
            .AnyAsync(x => x.SourceBomHdrId != null && headerIds.Contains(x.SourceBomHdrId.Value), cancellationToken);
        if (hasRootWorkOrderRef || hasMaterialSnapshotRef)
        {
            return IvMasterOperationResult<object?>.Fail(
                IvMasterErrorCode.InUse,
                "Product Definition cannot be deleted because a Work Order retains its BOM revision.");
        }

        db.PrBomHdrs.RemoveRange(headers);
        await db.SaveChangesAsync(cancellationToken);
        return IvMasterOperationResult<object?>.Ok();
    }

    private static async Task<Dictionary<string, IReadOnlyList<string>>> BuildActiveComponentGraphAsync(
        AppDbContext db,
        string company,
        string? excludeProdCode,
        CancellationToken cancellationToken)
    {
        var activeHeaders = await db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.Status == PrBomStatuses.Active)
            .Select(x => new { x.Uid, x.ProdCode })
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(excludeProdCode))
        {
            activeHeaders = activeHeaders
                .Where(x => !string.Equals(x.ProdCode, excludeProdCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var hdrIds = activeHeaders.Select(x => x.Uid).ToList();
        var lines = await db.PrDefBOMs.AsNoTracking()
            .Where(x => hdrIds.Contains(x.BomHdrId))
            .Select(x => new { x.BomHdrId, x.ICode })
            .ToListAsync(cancellationToken);

        var byHdr = lines.GroupBy(x => x.BomHdrId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ICode).ToList());

        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in activeHeaders)
        {
            graph[h.ProdCode] = byHdr.TryGetValue(h.Uid, out var c) ? c : [];
        }

        return graph;
    }

    private static async Task<PrBomHdr?> FindOverlapAsync(
        AppDbContext db,
        string company,
        string prodCode,
        DateTime? effFrom,
        DateTime? effTo,
        long? excludeUid,
        CancellationToken cancellationToken)
    {
        var from = effFrom ?? DateTime.MinValue.Date;
        var actives = await db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.ProdCode == prodCode
                        && x.Status == PrBomStatuses.Active
                        && (excludeUid == null || x.Uid != excludeUid))
            .ToListAsync(cancellationToken);

        foreach (var other in actives)
        {
            if (WindowsOverlap(from, effTo, other.EffectiveFrom, other.EffectiveTo))
            {
                return other;
            }
        }

        return null;
    }

    /// <summary>
    /// Inclusive From, exclusive To (null To = open). Overlap if ranges intersect.
    /// </summary>
    internal static bool WindowsOverlap(
        DateTime? aFrom, DateTime? aTo,
        DateTime? bFrom, DateTime? bTo)
    {
        var aStart = (aFrom ?? DateTime.MinValue).Date;
        var aEnd = aTo?.Date ?? DateTime.MaxValue.Date;
        var bStart = (bFrom ?? DateTime.MinValue).Date;
        var bEnd = bTo?.Date ?? DateTime.MaxValue.Date;
        // [start, end) overlap
        return aStart < bEnd && bStart < aEnd;
    }

    private static async Task SupersedeOverlappingAsync(
        AppDbContext db,
        string company,
        string prodCode,
        long keepUid,
        DateTime newFrom,
        DateTime now,
        string user,
        CancellationToken cancellationToken)
    {
        var others = await db.PrBomHdrs
            .Where(x => x.CompanyCode == company
                        && x.ProdCode == prodCode
                        && x.Status == PrBomStatuses.Active
                        && x.Uid != keepUid)
            .ToListAsync(cancellationToken);

        foreach (var other in others)
        {
            other.Status = PrBomStatuses.Superseded;
            other.EffectiveTo ??= newFrom;
            other.ModifiedDate = now;
            other.ModifiedBy = user;
        }
    }

    /// <summary>
    /// Same header pick as <see cref="GetAsync"/>: explicit version, else latest Version desc.
    /// </summary>
    private static async Task<PrBomHdr?> ResolveHeaderForEditAsync(
        AppDbContext db,
        string company,
        string prodCode,
        int? version,
        CancellationToken cancellationToken)
    {
        if (version is int v)
        {
            return await db.PrBomHdrs.AsNoTracking()
                .FirstOrDefaultAsync(x => x.CompanyCode == company && x.ProdCode == prodCode && x.Version == v,
                    cancellationToken);
        }

        return await db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.ProdCode == prodCode)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task WalkStructureAsync(
        AppDbContext db,
        string company,
        string ownerProdCode,
        PrBomHdr ownerHeader,
        string parentKey,
        int level,
        List<string> pathStack,
        List<PrBomStructureNode> nodes,
        StructureLoadCache cache,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var lines = await GetStructureLinesAsync(db, ownerHeader.Uid, cache, cancellationToken);
        foreach (var line in lines.OrderBy(x => x.SeqNo).ThenBy(x => x.ICode))
        {
            var childCode = NormalizeCode(line.ICode);
            var lineId = PrBomStructureKeys.LineId(line.Uid, tempId: null);
            var key = PrBomStructureKeys.Line(parentKey, ownerProdCode, lineId);
            var item = await GetStructureItemAsync(db, company, childCode, cache, cancellationToken);
            var mfg = item?.MfgType ?? PrMfgTypes.Buy;
            var childLevel = level + 1;

            var status = PrBomStructureNodeStatus.Normal;
            var shouldRecurse = mfg is PrMfgTypes.Make or PrMfgTypes.Phantom;

            if (shouldRecurse
                && pathStack.Any(p => string.Equals(p, childCode, StringComparison.OrdinalIgnoreCase)))
            {
                status = PrBomStructureNodeStatus.Circular;
                shouldRecurse = false;
            }
            else if (shouldRecurse && childLevel >= PrBomStructureKeys.MaxDepth)
            {
                status = PrBomStructureNodeStatus.MaxDepth;
                shouldRecurse = false;
            }

            PrBomHdr? childHeader = null;
            if (shouldRecurse)
            {
                childHeader = await ResolveHeaderForEditCachedAsync(
                    db, company, childCode, version: null, cache, cancellationToken);
                if (childHeader is null)
                {
                    status = PrBomStructureNodeStatus.MissingBom;
                    shouldRecurse = false;
                }
            }

            nodes.Add(new PrBomStructureNode
            {
                Key = key,
                ParentKey = parentKey,
                Level = childLevel,
                ItemCode = childCode,
                ItemDesc = item?.Desc ?? line.IName,
                MfgType = mfg,
                StdQty = line.StdQty,
                StdUom = line.StdUom ?? item?.StdUom,
                ScrapPercent = line.ScrapPercent,
                Warehouse = line.Warehouse,
                SeqNo = line.SeqNo,
                OwnerProdCode = ownerProdCode,
                SourceLineUid = line.Uid,
                BomHdrId = ownerHeader.Uid,
                BomVersion = ownerHeader.Version,
                BomStatus = ownerHeader.Status,
                Status = status
            });

            if (shouldRecurse && childHeader is not null)
            {
                pathStack.Add(childCode);
                await WalkStructureAsync(
                    db,
                    company,
                    ownerProdCode: childCode,
                    ownerHeader: childHeader,
                    parentKey: key,
                    level: childLevel,
                    pathStack,
                    nodes,
                    cache,
                    cancellationToken);
                pathStack.RemoveAt(pathStack.Count - 1);
            }
        }
    }

    private static async Task<PrBomHdr?> ResolveHeaderForEditCachedAsync(
        AppDbContext db,
        string company,
        string prodCode,
        int? version,
        StructureLoadCache cache,
        CancellationToken cancellationToken)
    {
        if (version is int v)
        {
            if (cache.Headers.TryGetValue((prodCode, v), out var byVersion))
            {
                return byVersion;
            }

            var header = await ResolveHeaderForEditAsync(db, company, prodCode, v, cancellationToken);
            if (header is not null)
            {
                cache.Headers[(prodCode, header.Version)] = header;
            }

            return header;
        }

        if (cache.LatestResolved.TryGetValue(prodCode, out var latestKnown))
        {
            return latestKnown;
        }

        var latest = await ResolveHeaderForEditAsync(db, company, prodCode, version: null, cancellationToken);
        cache.LatestResolved[prodCode] = latest;
        if (latest is not null)
        {
            cache.Headers[(prodCode, latest.Version)] = latest;
        }

        return latest;
    }

    private static async Task<List<PrDefBOM>> GetStructureLinesAsync(
        AppDbContext db,
        long bomHdrId,
        StructureLoadCache cache,
        CancellationToken cancellationToken)
    {
        if (cache.Lines.TryGetValue(bomHdrId, out var cached))
        {
            return cached;
        }

        var lines = await db.PrDefBOMs.AsNoTracking()
            .Where(x => x.BomHdrId == bomHdrId)
            .ToListAsync(cancellationToken);
        cache.Lines[bomHdrId] = lines;
        return lines;
    }

    private static async Task<StructureItemInfo?> GetStructureItemAsync(
        AppDbContext db,
        string company,
        string itemCode,
        StructureLoadCache cache,
        CancellationToken cancellationToken)
    {
        if (cache.Items.TryGetValue(itemCode, out var hit))
        {
            return hit;
        }

        var row = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.ICode == itemCode)
            .Select(x => new { x.ICode, x.IDesc, x.MfgType, x.StdUom })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var info = new StructureItemInfo(
            row.ICode,
            row.IDesc,
            PrMfgTypes.Normalize(row.MfgType),
            row.StdUom);
        cache.Items[itemCode] = info;
        return info;
    }

    private sealed class StructureLoadCache
    {
        public Dictionary<(string Prod, int Version), PrBomHdr> Headers { get; } = new(new ProdVersionComparer());
        public Dictionary<string, PrBomHdr?> LatestResolved { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<long, List<PrDefBOM>> Lines { get; } = new();
        public Dictionary<string, StructureItemInfo> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProdVersionComparer : IEqualityComparer<(string Prod, int Version)>
    {
        public bool Equals((string Prod, int Version) x, (string Prod, int Version) y) =>
            x.Version == y.Version
            && string.Equals(x.Prod, y.Prod, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Prod, int Version) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Prod), obj.Version);
    }

    private sealed record StructureItemInfo(string Code, string? Desc, string MfgType, string? StdUom);

    private async Task<List<NormalizedOperation>> ValidateOperationsAsync(
        AppDbContext db,
        string company,
        string productCode,
        IReadOnlyList<PrProductDefOperationVm> operations,
        bool publishing,
        IReadOnlyDictionary<string, string?> itemStdUomByCode,
        Dictionary<string, string> errors,
        CancellationToken cancellationToken)
    {
        var result = new List<NormalizedOperation>();
        if (operations.Count == 0)
        {
            // Existing BOM-only definitions remain loadable during migration. Once routing is
            // introduced, the complete legacy publication rules below are enforced.
            return result;
        }

        var activeItemCodes = itemStdUomByCode.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var centres = await db.PrWorkCentres.AsNoTracking()
            .Where(x => x.CompCode == company)
            .Select(x => new { x.WrkCtrCd, x.WrkCtrDes })
            .ToListAsync(cancellationToken);
        var centreByCode = centres.ToDictionary(x => x.WrkCtrCd, StringComparer.OrdinalIgnoreCase);
        var processRows = await db.PrProcesses.AsNoTracking()
            .Where(x => x.CompCode == company)
            .Select(x => new { x.ProcessCd, x.WorkCentre, x.ProcessDes })
            .ToListAsync(cancellationToken);
        var processByKey = processRows.ToDictionary(
            x => $"{x.WorkCentre}|{x.ProcessCd}", StringComparer.OrdinalIgnoreCase);
        var machineRows = await db.PrMachines.AsNoTracking()
            .Where(x => x.CompCode == company)
            .Select(x => new { x.MachineCd, x.ProcessCd, x.MachineDes })
            .ToListAsync(cancellationToken);
        var machineByKey = machineRows
            .GroupBy(x => $"{x.ProcessCd}|{x.MachineCd}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var labourRows = await db.PrOperators.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.Active != false)
            .Select(x => new { x.Code, x.Name })
            .ToListAsync(cancellationToken);
        var labourByCode = labourRows.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);

        var seenOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < operations.Count; i++)
        {
            var source = operations[i];
            var prefix = $"Operations[{i}]";
            var wc = NormalizeCode(source.WorkCentreCode);
            var output = NormalizeCode(source.OutputItemCode);
            var operation = NormalizeCode(source.OperationCode);
            var processType = (source.ProcessType ?? PrProcessTypes.Machine).Trim().ToUpperInvariant();
            if (!PrProcessTypes.IsValid(processType))
                errors[$"{prefix}.ProcessType"] = $"Route {i + 1}: select a valid process type.";

            if (wc.Length == 0 || wc.Length > 20 || !centreByCode.ContainsKey(wc))
                errors[$"{prefix}.WorkCentreCode"] = $"Route {i + 1}: select a valid work centre.";
            if (output.Length == 0 || output.Length > CodeMax || !activeItemCodes.Contains(output))
                errors[$"{prefix}.OutputItemCode"] = $"Route {i + 1}: select an active output item.";
            else if (itemStdUomByCode.TryGetValue(output, out var inventoryUomRaw))
            {
                var outputUom = Truncate(source.OutputUom ?? inventoryUomRaw, UomMax);
                var inventoryUom = Truncate(inventoryUomRaw, UomMax);
                if (!string.IsNullOrWhiteSpace(outputUom)
                    && !string.IsNullOrWhiteSpace(inventoryUom)
                    && !string.Equals(outputUom, inventoryUom, StringComparison.OrdinalIgnoreCase)
                    && !await _uom.HasConversionAsync(company, output, outputUom, inventoryUom, cancellationToken))
                {
                    errors[$"{prefix}.OutputUom"] =
                        $"Route {i + 1}: no approved conversion from {outputUom} to inventory UOM {inventoryUom} ({UomConversionFailureCodes.MissingConversion}).";
                }
            }
            if (operation.Length == 0 || operation.Length > 20
                || !processByKey.ContainsKey($"{wc}|{operation}"))
                errors[$"{prefix}.OperationCode"] = $"Route {i + 1}: select a process belonging to work centre {wc}.";
            if (source.CentralSequence <= 0)
                errors[$"{prefix}.CentralSequence"] = $"Route {i + 1}: central sequence must be greater than zero.";
            if (source.ProcessSequence <= 0)
                errors[$"{prefix}.ProcessSequence"] = $"Route {i + 1}: process sequence must be greater than zero.";

            var outputQty = IvQty.Round(source.OutputBaseQty);
            if (outputQty <= 0m)
                errors[$"{prefix}.OutputBaseQty"] = $"Route {i + 1}: output base quantity must be greater than zero.";

            var operationKey = $"{wc}|{output}|{operation}";
            if (!seenOperations.Add(operationKey))
                errors[$"{prefix}.OperationCode"] = $"Route {i + 1}: duplicate work centre/output/process combination.";

            var standardDuration = IvQty.Round(source.StandardDurationMinutes);
            if (standardDuration < 0m)
                errors[$"{prefix}.StandardDurationMinutes"] = $"Route {i + 1}: standard duration cannot be negative.";

            var durationBased = PrProcessTypes.IsDurationBased(processType);
            var machineCapable = PrProcessTypes.SupportsMachine(processType);
            // MACHINE/AUTOMATED always need a resource. Duration-only steps must not carry one.
            // PACKING is either machine-driven or duration-driven depending on what was authored.
            var requiresMachine = processType is PrProcessTypes.Machine or PrProcessTypes.Automated
                                  || (processType == PrProcessTypes.Packing && source.Machines.Count > 0);
            var requiresDuration = durationBased
                                   || (processType == PrProcessTypes.Packing && source.Machines.Count == 0);
            if (durationBased && source.Machines.Count > 0)
                errors[$"{prefix}.Machines"] =
                    $"Route {i + 1}: {processType} steps are duration-based and cannot select a machine.";
            else if (!machineCapable && !durationBased && source.Machines.Count > 0)
                errors[$"{prefix}.Machines"] =
                    $"Route {i + 1}: process type {processType} does not support machine options.";

            var machines = new List<NormalizedMachine>();
            var seenMachines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenMachinePriorities = new HashSet<int>();
            var primaryCount = 0;
            for (var j = 0; j < source.Machines.Count; j++)
            {
                var machine = source.Machines[j];
                var machinePrefix = $"{prefix}.Machines[{j}]";
                var machineCode = NormalizeCode(machine.MachineCode);
                var machineMaster = machineByKey.GetValueOrDefault($"{operation}|{machineCode}");
                if (machineCode.Length == 0 || machineCode.Length > 20
                    || machineMaster is null)
                {
                    errors[$"{machinePrefix}.MachineCode"] =
                        $"Route {i + 1}, machine {j + 1}: select a machine belonging to process {operation}.";
                }

                if (!seenMachines.Add(machineCode))
                    errors[$"{machinePrefix}.MachineCode"] = $"Route {i + 1}: duplicate machine {machineCode}.";
                if (machine.IsPrimary)
                    primaryCount++;
                if (machine.ResourceSequence <= 0)
                    errors[$"{machinePrefix}.ResourceSequence"] = "Machine sequence must be greater than zero.";
                if (machine.Priority <= 0)
                    errors[$"{machinePrefix}.Priority"] = "Machine priority must be greater than zero.";
                else if (!seenMachinePriorities.Add(machine.Priority))
                    errors[$"{machinePrefix}.Priority"] = $"Route {i + 1}: duplicate machine priority {machine.Priority}.";
                if (machine.ParallelMachineCount <= 0)
                    errors[$"{machinePrefix}.ParallelMachineCount"] = "Parallel machine count must be greater than zero.";
                if (machine.CycleSeconds < 0m || machine.ConversionSeconds < 0m
                    || machine.SetupSeconds < 0m || machine.QueueSeconds < 0m)
                    errors[$"{machinePrefix}.CycleSeconds"] = "Machine times cannot be negative.";
                if (machine.MachineRatePerHour < 0m)
                    errors[$"{machinePrefix}.MachineRatePerHour"] = "Machine rate per hour cannot be negative.";

                var outputPerCycle = IvQty.Round(machine.OutputPerCycle);
                if (outputPerCycle <= 0m)
                    errors[$"{machinePrefix}.OutputPerCycle"] = "Output per cycle must be greater than zero.";

                var labours = new List<NormalizedLabour>();
                if (machine.Labours.Count > 1)
                    errors[$"{machinePrefix}.Labours"] =
                        $"Route {i + 1}: the process-machine can have only one labour/operator.";
                var seenLabour = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var k = 0; k < machine.Labours.Count; k++)
                {
                    var labour = machine.Labours[k];
                    var labourPrefix = $"{machinePrefix}.Labours[{k}]";
                    var labourCode = NormalizeCode(labour.LabourCode);
                    var labourMaster = labourByCode.GetValueOrDefault(labourCode);
                    if (labourCode.Length == 0 || labourCode.Length > 20
                        || labourMaster is null)
                        errors[$"{labourPrefix}.LabourCode"] = "Select an active labour/operator code.";
                    if (!seenLabour.Add(labourCode))
                        errors[$"{labourPrefix}.LabourCode"] = $"Duplicate labour {labourCode}.";
                    if (labour.CostPerOutputUnit < 0m)
                        errors[$"{labourPrefix}.CostPerOutputUnit"] = "Labour cost per output unit cannot be negative.";

                    labours.Add(new NormalizedLabour(
                        labourCode,
                        labourByCode.GetValueOrDefault(labourCode)?.Name,
                        decimal.Round(labour.CostPerOutputUnit, 6, MidpointRounding.AwayFromZero)));
                }

                machines.Add(new NormalizedMachine(
                    machineCode,
                    machineMaster?.MachineDes ?? machine.MachineDescription,
                    machine.ResourceSequence,
                    machine.IsPrimary,
                    machine.Priority,
                    outputPerCycle,
                    IvQty.Round(machine.CycleSeconds),
                    IvQty.Round(machine.ConversionSeconds),
                    IvQty.Round(machine.SetupSeconds),
                    IvQty.Round(machine.QueueSeconds),
                    IvQty.Round(machine.MachineRatePerHour),
                    machine.ParallelMachineCount,
                    labours));
            }

            if (source.Machines.Count > 0 && primaryCount != 1)
                errors[$"{prefix}.Machines"] =
                    $"Route {i + 1}: select exactly one primary machine option.";

            if (publishing)
            {
                if (requiresMachine)
                {
                    if (machines.Count == 0)
                        errors[$"{prefix}.Machines"] =
                            $"Route {i + 1}: process {operation} requires at least one machine before activation.";
                    else if (machines.All(m => m.Labours.Count == 0))
                        errors[$"{prefix}.Machines"] =
                            $"Route {i + 1}: process {operation} requires a labour/operator on a machine option before activation.";
                }

                if (requiresDuration && standardDuration <= 0m)
                    errors[$"{prefix}.StandardDurationMinutes"] =
                        $"Route {i + 1}: process {operation} requires a standard duration greater than zero before activation.";
            }

            result.Add(new NormalizedOperation(
                source.OperationKey,
                wc,
                output,
                source.CentralSequence,
                outputQty,
                Truncate(source.OutputUom, UomMax),
                operation,
                source.ProcessSequence,
                processType,
                standardDuration,
                IvQty.Round(source.SetupLossQty),
                IvQty.Round(source.OperationLossQty),
                source.IsFinalOperation,
                Truncate(source.Remark, 500),
                machines));
        }

        if (publishing)
        {
            foreach (var centre in result.GroupBy(x => $"{x.WorkCentreCode}|{x.OutputItemCode}", StringComparer.OrdinalIgnoreCase))
            {
                var finals = centre.Count(x => x.IsFinalOperation);
                if (finals != 1)
                    errors[nameof(PrProductDefEditVm.Operations)] =
                        $"Work centre/output {centre.Key} must have exactly one final process before activation.";
            }

            // UQ_PrBomOperation_RouteStep_Sequence is (RouteStepID, ProcessSequence). Surface a
            // duplicate as a field error rather than a database constraint violation.
            foreach (var step in result.GroupBy(x => (x.WorkCentreCode, x.OutputItemCode, x.CentralSequence)))
            {
                var duplicate = step.GroupBy(x => x.ProcessSequence).FirstOrDefault(g => g.Count() > 1);
                if (duplicate is not null)
                    errors[nameof(PrProductDefEditVm.Operations)] =
                        $"Work centre/output {step.Key.OutputItemCode} stage {step.Key.CentralSequence} has duplicate process sequence {duplicate.Key}.";
            }
        }

        return result;
    }

    /// <summary>
    /// Persists the version's route: one <see cref="PrBomRouteStep"/> per
    /// (work centre, output item, central sequence) occurrence, each owning its process steps.
    /// Existing step keys are reused so a material's <c>ProducingRouteStepKey</c> survives a
    /// save/reload round-trip.
    /// </summary>
    private static (Dictionary<Guid, PrBomOperation> Operations, Dictionary<Guid, PrBomRouteStep> RouteSteps) AddRoute(
        AppDbContext db,
        PrBomHdr header,
        string company,
        IReadOnlyList<NormalizedOperation> operations,
        IReadOnlyDictionary<(string WorkCentreCode, string OutputItemCode, int StageSequence), Guid> existingStepKeys,
        DateTime now,
        string user)
    {
        var operationsByKey = new Dictionary<Guid, PrBomOperation>();
        var routeStepsByKey = new Dictionary<Guid, PrBomRouteStep>();
        var stepsByGroup = new Dictionary<(string WorkCentreCode, string OutputItemCode, int StageSequence), PrBomRouteStep>();

        var ordered = operations
            .OrderBy(x => x.CentralSequence)
            .ThenBy(x => x.WorkCentreCode)
            .ThenBy(x => x.OutputItemCode)
            .ThenBy(x => x.ProcessSequence)
            .ToList();

        foreach (var source in ordered)
        {
            var groupKey = (source.WorkCentreCode, source.OutputItemCode, source.CentralSequence);
            if (!stepsByGroup.TryGetValue(groupKey, out var routeStep))
            {
                var routeStepKey = existingStepKeys.TryGetValue(groupKey, out var priorKey) && priorKey != Guid.Empty
                    ? priorKey
                    : Guid.NewGuid();
                routeStep = new PrBomRouteStep
                {
                    Header = header,
                    BomHdrId = header.Uid,
                    RouteStepKey = routeStepKey,
                    CompanyCode = company,
                    WorkCentreCode = source.WorkCentreCode,
                    StageSequence = source.CentralSequence,
                    OutputItemCode = source.OutputItemCode,
                    OutputType = string.Equals(source.OutputItemCode, header.ProdCode, StringComparison.OrdinalIgnoreCase)
                        ? PrRouteOutputTypes.FinishedGoods
                        : PrRouteOutputTypes.WipStocked,
                    StandardOutputQty = source.OutputBaseQty,
                    OutputUom = source.OutputUom ?? header.BaseUom ?? string.Empty,
                    YieldPercent = 100m,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user,
                    RowVersion = []
                };
                db.PrBomRouteSteps.Add(routeStep);
                stepsByGroup[groupKey] = routeStep;
                routeStepsByKey[routeStepKey] = routeStep;
            }

            var operation = new PrBomOperation
            {
                Header = header,
                RouteStep = routeStep,
                OperationKey = source.OperationKey,
                BomHdrId = header.Uid,
                CompanyCode = company,
                WorkCentreCode = source.WorkCentreCode,
                OutputItemCode = source.OutputItemCode,
                CentralSequence = source.CentralSequence,
                OutputBaseQty = source.OutputBaseQty,
                OutputUom = source.OutputUom,
                OperationCode = source.OperationCode,
                ProcessSequence = source.ProcessSequence,
                ProcessType = source.ProcessType,
                StandardDurationMinutes = source.StandardDurationMinutes,
                SetupLossQty = source.SetupLossQty,
                OperationLossQty = source.OperationLossQty,
                IsFinalOperation = source.IsFinalOperation,
                Remark = source.Remark,
                CreatedDate = now,
                CreatedBy = user,
                ModifiedDate = now,
                ModifiedBy = user,
                RowVersion = []
            };
            foreach (var machineSource in source.Machines
                         .OrderBy(x => x.ResourceSequence)
                         .ThenBy(x => x.Priority)
                         .ThenBy(x => x.MachineCode))
            {
                var machine = new PrBomMachineOption
                {
                    Operation = operation,
                    MachineCode = machineSource.MachineCode,
                    MachineDescription = Truncate(machineSource.MachineDescription, NameMax),
                    ResourceSequence = machineSource.ResourceSequence,
                    IsPrimary = machineSource.IsPrimary,
                    Priority = machineSource.Priority,
                    OutputPerCycle = machineSource.OutputPerCycle,
                    CycleSeconds = machineSource.CycleSeconds,
                    ConversionSeconds = machineSource.ConversionSeconds,
                    SetupSeconds = machineSource.SetupSeconds,
                    QueueSeconds = machineSource.QueueSeconds,
                    MachineRatePerHour = machineSource.MachineRatePerHour,
                    ParallelMachineCount = machineSource.ParallelMachineCount,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user,
                    RowVersion = []
                };
                foreach (var labourSource in machineSource.Labours)
                {
                    machine.Labours.Add(new PrBomLabourStandard
                    {
                        MachineOption = machine,
                        LabourCode = labourSource.LabourCode,
                        LabourDescription = Truncate(labourSource.LabourDescription, NameMax),
                        CostPerOutputUnit = labourSource.CostPerOutputUnit,
                        CreatedDate = now,
                        CreatedBy = user,
                        ModifiedDate = now,
                        ModifiedBy = user,
                        RowVersion = []
                    });
                }
                operation.Machines.Add(machine);
            }
            db.PrBomOperations.Add(operation);
            operationsByKey[source.OperationKey] = operation;
        }

        return (operationsByKey, routeStepsByKey);
    }

    /// <summary>
    /// Route step keys a client may reference as a material producer during this save. Only
    /// persisted steps are known: a brand-new definition has none until it is saved once.
    /// </summary>
    private static Dictionary<Guid, string> KnownRouteStepOutputItems(IReadOnlyList<PrBomRouteStep> persistedSteps) =>
        persistedSteps
            .GroupBy(x => x.RouteStepKey)
            .ToDictionary(x => x.Key, x => x.First().OutputItemCode, EqualityComparer<Guid>.Default);

    private sealed record NormalizedOperation(
        Guid OperationKey,
        string WorkCentreCode,
        string OutputItemCode,
        int CentralSequence,
        decimal OutputBaseQty,
        string? OutputUom,
        string OperationCode,
        int ProcessSequence,
        string ProcessType,
        decimal StandardDurationMinutes,
        decimal SetupLossQty,
        decimal OperationLossQty,
        bool IsFinalOperation,
        string? Remark,
        IReadOnlyList<NormalizedMachine> Machines);

    private sealed record NormalizedMachine(
        string MachineCode,
        string? MachineDescription,
        int ResourceSequence,
        bool IsPrimary,
        int Priority,
        decimal OutputPerCycle,
        decimal CycleSeconds,
        decimal ConversionSeconds,
        decimal SetupSeconds,
        decimal QueueSeconds,
        decimal MachineRatePerHour,
        int ParallelMachineCount,
        IReadOnlyList<NormalizedLabour> Labours);

    private sealed record NormalizedLabour(
        string LabourCode,
        string? LabourDescription,
        decimal CostPerOutputUnit);

    private static PrProductDefEditVm MapEdit(
        PrBomHdr header,
        IvStockMaster? product,
        List<PrDefBOM> lines,
        Dictionary<string, string> mfgByCode,
        List<PrBomOperation> operations,
        Dictionary<long, Guid> routeStepKeyById,
        Dictionary<string, string?> workCentreDescriptions,
        Dictionary<string, string?> operationDescriptions) =>
        new()
        {
            ProdCode = header.ProdCode,
            ProdDesc = product?.IDesc,
            StdUom = product?.StdUom,
            MfgType = PrMfgTypes.Normalize(product?.MfgType),
            IsActive = product?.IsActive ?? false,
            BomHdrId = header.Uid,
            Version = header.Version,
            Status = header.Status,
            EffectiveFrom = header.EffectiveFrom,
            EffectiveTo = header.EffectiveTo,
            BaseQty = header.BaseQty,
            BaseUom = header.BaseUom,
            Prefix = header.Prefix,
            Remark = header.Remark,
            HeaderRowVersion = header.RowVersion,
            Lines = lines.Select(x => new PrProductDefLineVm
            {
                OperationKey = x.OperationKey,
                Uid = x.Uid,
                ICode = x.ICode,
                IName = x.IName,
                StdQty = x.StdQty,
                StdUom = x.StdUom,
                SeqNo = x.SeqNo,
                ScrapPercent = x.ScrapPercent,
                Warehouse = x.Warehouse,
                BomDefault = x.BomDefault,
                AlternateGroupCode = x.AlternateGroupCode,
                Tolerance = x.Tolerance,
                IssueMethod = x.IssueMethod,
                SupplySource = x.SupplySource,
                ProducingRouteStepKey = x.ProducingRouteStepId is { } producerId
                                        && routeStepKeyById.TryGetValue(producerId, out var producerKey)
                    ? producerKey
                    : null,
                MfgType = mfgByCode.TryGetValue(x.ICode, out var m) ? m : PrMfgTypes.Buy
            }).ToList(),
            Operations = operations.Select(x => new PrProductDefOperationVm
            {
                OperationKey = x.OperationKey,
                Uid = x.Uid,
                WorkCentreCode = x.WorkCentreCode,
                WorkCentreDescription = workCentreDescriptions.GetValueOrDefault(x.WorkCentreCode),
                OutputItemCode = x.OutputItemCode,
                CentralSequence = x.CentralSequence,
                OutputBaseQty = x.OutputBaseQty,
                OutputUom = x.OutputUom,
                OperationCode = x.OperationCode,
                OperationDescription = operationDescriptions.GetValueOrDefault(x.OperationCode),
                ProcessSequence = x.ProcessSequence,
                ProcessType = x.ProcessType,
                StandardDurationMinutes = x.StandardDurationMinutes,
                SetupLossQty = x.SetupLossQty,
                OperationLossQty = x.OperationLossQty,
                IsFinalOperation = x.IsFinalOperation,
                Remark = x.Remark,
                RouteStepKey = x.RouteStepId is { } routeStepId
                    && routeStepKeyById.TryGetValue(routeStepId, out var routeStepKey)
                    ? routeStepKey
                    : null,
                Machines = x.Machines
                    .OrderBy(m => m.ResourceSequence)
                    .ThenBy(m => m.MachineCode)
                    .Select(m => new PrProductDefMachineVm
                    {
                        Uid = m.Uid,
                        MachineCode = m.MachineCode,
                        MachineDescription = m.MachineDescription,
                        ResourceSequence = m.ResourceSequence,
                        IsPrimary = m.IsPrimary,
                        Priority = m.Priority,
                        OutputPerCycle = m.OutputPerCycle,
                        CycleSeconds = m.CycleSeconds,
                        ConversionSeconds = m.ConversionSeconds,
                        SetupSeconds = m.SetupSeconds,
                        QueueSeconds = m.QueueSeconds,
                        MachineRatePerHour = m.MachineRatePerHour,
                        ParallelMachineCount = m.ParallelMachineCount,
                        Labours = m.Labours
                            .OrderBy(l => l.LabourCode)
                            .Select(l => new PrProductDefLabourVm
                            {
                                Uid = l.Uid,
                                LabourCode = l.LabourCode,
                                LabourDescription = l.LabourDescription,
                                CostPerOutputUnit = l.CostPerOutputUnit
                            }).ToList()
                    }).ToList()
            }).ToList()
        };

    private async Task<(string? CompanyCode, string? UserId, ScopeError? Error)> RequireCompanyScopeAsync(
        string permission,
        CancellationToken cancellationToken)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return (null, null, new ScopeError(IvMasterErrorCode.InvalidScope, "Invalid company context."));
        }

        if (!await _accessRights.CanAsync(MenuCodes.PlanningProductDef, permission, cancellationToken))
        {
            return (null, null, new ScopeError(IvMasterErrorCode.AccessDenied, "Not authorized."));
        }

        return (scope.CompanyCode, scope.UserId, null);
    }

    private sealed record ScopeError(IvMasterErrorCode Code, string Message);

    private static IvMasterOperationResult<PrProductDefListPage> FailPage(ScopeError error) =>
        IvMasterOperationResult<PrProductDefListPage>.Fail(error.Code, error.Message);

    private static IvMasterOperationResult<PrProductDefEditVm> FailVm(ScopeError error) =>
        IvMasterOperationResult<PrProductDefEditVm>.Fail(error.Code, error.Message);

    private static IvMasterOperationResult<PrBomStructureResult> FailStructure(ScopeError error) =>
        IvMasterOperationResult<PrBomStructureResult>.Fail(error.Code, error.Message);

    private static IvMasterOperationResult<PrBomStructureResult> FailStructure(
        IvMasterErrorCode code,
        string message,
        string? field = null)
    {
        Dictionary<string, string>? errors = null;
        if (!string.IsNullOrWhiteSpace(field))
        {
            errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [field] = message };
        }

        return IvMasterOperationResult<PrBomStructureResult>.Fail(code, message, errors);
    }

    private static IvMasterOperationResult<PrProductDefEditVm> FailVm(
        IvMasterErrorCode code,
        string message,
        string? field = null)
    {
        Dictionary<string, string>? errors = null;
        if (!string.IsNullOrWhiteSpace(field))
        {
            errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [field] = message };
        }

        return IvMasterOperationResult<PrProductDefEditVm>.Fail(code, message, errors);
    }

    private static string NormalizeCode(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    private static List<string> NormalizeCodes(IReadOnlyList<string>? codes) =>
        (codes ?? [])
            .Select(NormalizeCode)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ValidateCode(
        IDictionary<string, string> errors,
        string field,
        string label,
        string? value,
        int maxLen)
    {
        var code = NormalizeCode(value);
        if (code.Length == 0)
        {
            errors[field] = $"{label} is required.";
            return string.Empty;
        }

        if (code.Length > maxLen)
        {
            errors[field] = $"{label} is too long.";
            return string.Empty;
        }

        return code;
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
