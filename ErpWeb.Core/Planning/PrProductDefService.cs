using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
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

        var headers = db.PrBomHdrs.AsNoTracking().Where(x => x.CompanyCode == company);

        // Latest version per Product + DefinitionCode
        var latest = from h in headers
                     group h by new { h.ProdCode, h.DefinitionCode } into g
                     select new
                     {
                         g.Key.ProdCode,
                         g.Key.DefinitionCode,
                         Version = g.Max(x => x.Version)
                     };

        var joined = from l in latest
                     join h in headers on new { l.ProdCode, l.DefinitionCode, l.Version }
                         equals new { h.ProdCode, h.DefinitionCode, Version = h.Version }
                     join a in headers.Where(x => x.Status == PrBomStatuses.Active)
                         on new { h.ProdCode, h.DefinitionCode }
                         equals new { a.ProdCode, a.DefinitionCode } into aj
                     from a in aj.DefaultIfEmpty()
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
                         DefinitionCode = h.DefinitionCode,
                         DefinitionName = a != null ? a.DefinitionName : h.DefinitionName,
                         IsDefaultDefinition = a != null && a.IsDefaultDefinition,
                         ActiveVersion = a != null ? (int?)a.Version : null,
                         LatestVersion = h.Version,
                         LatestStatus = h.Status,
                         LatestBomItemCount = db.PrDefBOMs.Count(b => b.BomHdrId == h.Uid),
                         LatestModifiedDate = h.ModifiedDate,
                         h.CreatedDate,
                         h.CreatedBy,
                         h.ModifiedDate,
                         h.ModifiedBy,
                         LatestUid = h.Uid
                     };

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            joined = joined.Where(x =>
                x.ProdCode.Contains(term)
                || (x.ProdDesc != null && x.ProdDesc.Contains(term))
                || x.DefinitionCode.Contains(term)
                || (x.DefinitionName != null && x.DefinitionName.Contains(term)));
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
                b.BomHdrId == p.LatestUid
                && (componentCode == null || componentCode.Length == 0 || b.ICode.Contains(componentCode))
                && (componentDesc == null || componentDesc.Length == 0
                    || (b.IName != null && b.IName.Contains(componentDesc)))
                && (warehouse == null || warehouse.Length == 0
                    || (b.Warehouse != null && b.Warehouse == warehouse))));
        }

        var sortField = (query.SortField ?? nameof(PrProductDefListRow.ProdCode)).Trim();
        joined = (sortField.ToUpperInvariant(), query.SortDescending) switch
        {
            ("PRODDESC", true) => joined.OrderByDescending(x => x.ProdDesc).ThenByDescending(x => x.ProdCode)
                .ThenBy(x => x.DefinitionCode),
            ("PRODDESC", false) => joined.OrderBy(x => x.ProdDesc).ThenBy(x => x.ProdCode)
                .ThenBy(x => x.DefinitionCode),
            ("DEFINITIONCODE", true) => joined.OrderByDescending(x => x.DefinitionCode).ThenBy(x => x.ProdCode),
            ("DEFINITIONCODE", false) => joined.OrderBy(x => x.DefinitionCode).ThenBy(x => x.ProdCode),
            ("BOMITEMCOUNT", true) => joined.OrderByDescending(x => x.LatestBomItemCount).ThenBy(x => x.ProdCode)
                .ThenBy(x => x.DefinitionCode),
            ("BOMITEMCOUNT", false) => joined.OrderBy(x => x.LatestBomItemCount).ThenBy(x => x.ProdCode)
                .ThenBy(x => x.DefinitionCode),
            ("ISACTIVE", true) => joined.OrderByDescending(x => x.IsActive).ThenBy(x => x.ProdCode)
                .ThenBy(x => x.DefinitionCode),
            ("ISACTIVE", false) => joined.OrderBy(x => x.IsActive).ThenBy(x => x.ProdCode)
                .ThenBy(x => x.DefinitionCode),
            ("BOMVERSION", true) => joined.OrderByDescending(x => x.LatestVersion).ThenBy(x => x.ProdCode)
                .ThenBy(x => x.DefinitionCode),
            ("BOMVERSION", false) => joined.OrderBy(x => x.LatestVersion).ThenBy(x => x.ProdCode)
                .ThenBy(x => x.DefinitionCode),
            (_, true) => joined.OrderByDescending(x => x.ProdCode).ThenByDescending(x => x.DefinitionCode),
            _ => joined.OrderBy(x => x.ProdCode).ThenBy(x => x.DefinitionCode)
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
                DefinitionCode = x.DefinitionCode,
                DefinitionName = x.DefinitionName,
                IsDefaultDefinition = x.IsDefaultDefinition,
                ActiveVersion = x.ActiveVersion,
                LatestVersion = x.LatestVersion,
                LatestStatus = x.LatestStatus,
                LatestBomItemCount = x.LatestBomItemCount,
                LatestModifiedDate = x.LatestModifiedDate,
                DefinitionKey = x.ProdCode + "\u001f" + x.DefinitionCode,
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
        string definitionCode,
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

        var defCode = PrProductDefinitionCodes.Normalize(definitionCode);
        if (defCode.Length == 0 || !PrProductDefinitionCodes.IsValidFormat(defCode))
        {
            return FailVm(IvMasterErrorCode.Validation, "Definition code is required.", "DefinitionCode");
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var header = await ResolveHeaderForEditAsync(db, company, code, defCode, version, cancellationToken);
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
        string definitionCode,
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

        var defCode = PrProductDefinitionCodes.Normalize(definitionCode);
        if (defCode.Length == 0 || !PrProductDefinitionCodes.IsValidFormat(defCode))
        {
            return FailStructure(IvMasterErrorCode.Validation, "Definition code is required.", "DefinitionCode");
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var rootHeader = await ResolveHeaderForEditAsync(db, company, code, defCode, version, cancellationToken);
        if (rootHeader is null)
        {
            return FailStructure(IvMasterErrorCode.NotFound, "Product definition not found.");
        }

        var rootItem = await db.IvStockMasters.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.ICode == code, cancellationToken);

        var rootDefinition = string.IsNullOrWhiteSpace(rootHeader.DefinitionCode)
            ? PrProductDefinitionCodes.Standard
            : PrProductDefinitionCodes.Normalize(rootHeader.DefinitionCode);

        var cache = new StructureLoadCache();
        cache.Headers[(code, rootDefinition, rootHeader.Version)] = rootHeader;
        if (rootItem is not null)
        {
            cache.Items[code] = new StructureItemInfo(
                code,
                rootItem.IDesc,
                PrMfgTypes.Normalize(rootItem.MfgType),
                rootItem.StdUom);
        }

        var nodes = new List<PrBomStructureNode>();
        var rootKey = PrBomStructureKeys.Root(code, rootDefinition);
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
            OwnerDefinitionCode = null,
            BomHdrId = rootHeader.Uid,
            BomVersion = rootHeader.Version,
            BomStatus = rootHeader.Status,
            Status = PrBomStructureNodeStatus.Normal
        });

        var pathStack = new List<(string Prod, string Def)> { (code, rootDefinition) };
        await WalkStructureAsync(
            db,
            company,
            ownerProdCode: code,
            ownerDefinitionCode: rootDefinition,
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
            RootDefinitionCode = rootDefinition,
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
        string definitionCode;
        string? definitionName;

        if (!isNew)
        {
            // DefinitionCode is immutable after first save — resolve the revision first, then lock the code.
            var requestedDef = PrProductDefinitionCodes.Normalize(model.DefinitionCode);
            existingHeader = await db.PrBomHdrs
                .FirstOrDefaultAsync(x => x.CompanyCode == company
                                          && x.ProdCode == prodCode
                                          && x.DefinitionCode == requestedDef
                                          && x.Version == model.Version, cancellationToken);
            if (existingHeader is null && requestedDef.Length > 0)
            {
                // Fallback: locate by Prod+Version then enforce DefinitionCode match (legacy rows).
                existingHeader = await db.PrBomHdrs
                    .FirstOrDefaultAsync(x => x.CompanyCode == company
                                              && x.ProdCode == prodCode
                                              && x.Version == model.Version, cancellationToken);
            }

            if (existingHeader is null)
            {
                return FailVm(IvMasterErrorCode.NotFound, "Product definition not found.");
            }

            definitionCode = string.IsNullOrWhiteSpace(existingHeader.DefinitionCode)
                ? PrProductDefinitionCodes.Standard
                : PrProductDefinitionCodes.Normalize(existingHeader.DefinitionCode);

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

            definitionName = Truncate(
                string.IsNullOrWhiteSpace(model.DefinitionName) ? existingHeader.DefinitionName : model.DefinitionName,
                NameMax);
        }
        else
        {
            definitionCode = PrProductDefinitionCodes.Normalize(model.DefinitionCode);
            if (definitionCode.Length == 0)
            {
                definitionCode = PrProductDefinitionCodes.Standard;
            }

            if (!PrProductDefinitionCodes.IsValidFormat(definitionCode))
            {
                errors[nameof(model.DefinitionCode)] =
                    "Definition code must be 1–30 characters (A–Z, 0–9, hyphen, underscore).";
            }

            definitionName = Truncate(
                string.IsNullOrWhiteSpace(model.DefinitionName)
                    ? (definitionCode == PrProductDefinitionCodes.Standard
                        ? PrProductDefinitionCodes.StandardName
                        : model.DefinitionName)
                    : model.DefinitionName,
                NameMax);

            if (prodCode.Length > 0 && definitionCode.Length > 0
                && !errors.ContainsKey(nameof(model.DefinitionCode)))
            {
                var any = await db.PrBomHdrs.AsNoTracking()
                    .AnyAsync(x => x.CompanyCode == company
                                   && x.ProdCode == prodCode
                                   && x.DefinitionCode == definitionCode, cancellationToken);
                if (any)
                {
                    errors[nameof(model.DefinitionCode)] =
                        "A product definition already exists for this product and definition code. Use Create New Version.";
                }
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
        var normalized = new List<(
            string ICode,
            string? IName,
            decimal StdQty,
            string? StdUom,
            int SeqNo,
            decimal ScrapPercent,
            string Warehouse,
            bool BomDefault,
            string? AlternateGroupCode,
            decimal Tolerance,
            bool WipBomDefault,
            Guid? OperationKey,
            string IssueMethod,
            string SupplySource,
            string? ComponentDefinitionCode,
            Guid? ProducingRouteStepKey)>();
        var operationKeys = normalizedOperations.Select(x => x.OperationKey).ToHashSet();
        var defaultsByGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        var persistedRouteSteps = existingHeader is null
            ? []
            : await db.PrBomRouteSteps.AsNoTracking()
                .Where(x => x.BomHdrId == existingHeader.Uid)
                .ToListAsync(cancellationToken);
        var availableStepOutputs = KnownRouteStepOutputItems(persistedRouteSteps);
        var existingStepKeys = persistedRouteSteps
            .GroupBy(x => (x.WorkCentreCode, x.OutputItemCode, x.StageSequence))
            .ToDictionary(g => g.Key, g => g.First().RouteStepKey);
        if (operationKeys.Contains(Guid.Empty) || operationKeys.Count != normalizedOperations.Count)
            errors["Operations"] = "Each process must have a unique, non-empty identity.";

        // Preload logical definition existence for SEPARATE draft validation
        HashSet<(string Prod, string Def)>? logicalDefs = null;
        HashSet<(string Prod, string Def)>? activeDefs = null;
        if (lines.Any(l => string.Equals(
                (l.SupplySource ?? string.Empty).Trim(),
                PrMaterialSupplySources.SeparateProductDefinition,
                StringComparison.OrdinalIgnoreCase)))
        {
            var defRows = await db.PrBomHdrs.AsNoTracking()
                .Where(x => x.CompanyCode == company)
                .Select(x => new { x.ProdCode, x.DefinitionCode, x.Status })
                .ToListAsync(cancellationToken);
            logicalDefs = defRows
                .Select(x => (
                    NormalizeCode(x.ProdCode),
                    string.IsNullOrWhiteSpace(x.DefinitionCode)
                        ? PrProductDefinitionCodes.Standard
                        : PrProductDefinitionCodes.Normalize(x.DefinitionCode)))
                .ToHashSet(new ProdDefTupleComparer());
            activeDefs = defRows
                .Where(x => x.Status == PrBomStatuses.Active)
                .Select(x => (
                    NormalizeCode(x.ProdCode),
                    string.IsNullOrWhiteSpace(x.DefinitionCode)
                        ? PrProductDefinitionCodes.Standard
                        : PrProductDefinitionCodes.Normalize(x.DefinitionCode)))
                .ToHashSet(new ProdDefTupleComparer());
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var label = $"Line {i + 1}";
            if (line.OperationKey is { } owner && !operationKeys.Contains(owner))
                errors[$"Lines[{i}].OperationKey"] = $"{label}: the owning process is missing.";
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

            string? componentDefinitionCode = null;
            if (string.Equals(supplySource, PrMaterialSupplySources.SeparateProductDefinition, StringComparison.Ordinal))
            {
                var childDef = PrProductDefinitionCodes.Normalize(line.ComponentDefinitionCode);
                if (childDef.Length == 0 || !PrProductDefinitionCodes.IsValidFormat(childDef))
                {
                    errors[$"Lines[{i}].ComponentDefinitionCode"] =
                        $"{label}: component definition code is required for SEPARATE_PRODUCT_DEFINITION.";
                }
                else if (logicalDefs is not null && !logicalDefs.Contains((iCode, childDef)))
                {
                    errors[$"Lines[{i}].ComponentDefinitionCode"] =
                        $"{label}: product definition {iCode}[{childDef}] does not exist.";
                }
                else if (activate && activeDefs is not null && !activeDefs.Contains((iCode, childDef)))
                {
                    errors[$"Lines[{i}].ComponentDefinitionCode"] =
                        $"{label}: product definition {iCode}[{childDef}] has no ACTIVE revision.";
                }
                else
                {
                    componentDefinitionCode = childDef;
                }
            }
            else if (!string.IsNullOrWhiteSpace(line.ComponentDefinitionCode))
            {
                errors[$"Lines[{i}].ComponentDefinitionCode"] =
                    $"{label}: component definition code is only allowed for SEPARATE_PRODUCT_DEFINITION.";
            }

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
                        componentDefinitionCode,
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

        // Circular BOM: SEPARATE edges only, keyed by (Prod, Definition)
        if (errors.Count == 0 && prodCode.Length > 0 && definitionCode.Length > 0)
        {
            var graph = await BuildActiveComponentGraphAsync(
                db, company, exclude: null, cancellationToken);
            var componentNodes = normalized
                .Where(x => string.Equals(
                    x.SupplySource,
                    PrMaterialSupplySources.SeparateProductDefinition,
                    StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(x.ComponentDefinitionCode))
                .Select(x => CircularBomValidator.NodeKey.Create(x.ICode, x.ComponentDefinitionCode))
                .ToList();
            var path = CircularBomValidator.FindCyclePath(
                CircularBomValidator.NodeKey.Create(prodCode, definitionCode),
                componentNodes,
                graph);
            if (path is not null)
            {
                errors[nameof(model.Lines)] =
                    $"Cannot save BOM for {prodCode}[{definitionCode}] because it creates a circular BOM: {path}.";
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

        var targetStatus = activate
            ? PrBomStatuses.Active
            : (existingHeader?.Status ?? PrBomStatuses.Draft);
        if (isNew && !activate)
        {
            targetStatus = PrBomStatuses.Draft;
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
                    DefinitionCode = definitionCode,
                    DefinitionName = definitionName,
                    IsDefaultDefinition = false,
                    Version = 1,
                    Status = PrBomStatuses.Draft,
                    EffectiveFrom = null,
                    EffectiveTo = null,
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
                header.DefinitionName = definitionName;
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
                    ComponentDefinitionCode = line.ComponentDefinitionCode,
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

            await db.SaveChangesAsync(cancellationToken);

            if (activate)
            {
                await ActivateRevisionInTransactionAsync(
                    db,
                    company,
                    header,
                    requestDefault: model.IsDefaultDefinition,
                    now,
                    user,
                    cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);

            return await GetAsync(prodCode, definitionCode, header.Version, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm(IvMasterErrorCode.Concurrency,
                "This product definition was changed by another user. Reload and try again.");
        }
        catch (DbUpdateException ex) when (IsUniqueIndexConflict(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm(IvMasterErrorCode.Concurrency,
                "Another user activated or changed the default for this product definition. Reload and try again.");
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static bool IsUniqueIndexConflict(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
               || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
               || message.Contains("IX_", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IvMasterOperationResult<PrProductDefEditVm>> CreateNewVersionAsync(
        string prodCode,
        string definitionCode,
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
        var defCode = PrProductDefinitionCodes.Normalize(definitionCode);
        if (code.Length == 0)
        {
            return FailVm(IvMasterErrorCode.Validation, "Product code is required.", "ProdCode");
        }

        if (defCode.Length == 0 || !PrProductDefinitionCodes.IsValidFormat(defCode))
        {
            return FailVm(IvMasterErrorCode.Validation, "Definition code is required.", "DefinitionCode");
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var source = fromVersion is int v
            ? await db.PrBomHdrs.AsNoTracking()
                .FirstOrDefaultAsync(x => x.CompanyCode == company
                                          && x.ProdCode == code
                                          && x.DefinitionCode == defCode
                                          && x.Version == v, cancellationToken)
            : await db.PrBomHdrs.AsNoTracking()
                .Where(x => x.CompanyCode == company
                            && x.ProdCode == code
                            && x.DefinitionCode == defCode)
                .OrderByDescending(x => x.Version)
                .FirstOrDefaultAsync(cancellationToken);

        if (source is null)
        {
            return FailVm(IvMasterErrorCode.NotFound, "Product definition not found.");
        }

        var maxVer = await db.PrBomHdrs
            .Where(x => x.CompanyCode == company
                        && x.ProdCode == code
                        && x.DefinitionCode == defCode)
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
            DefinitionCode = source.DefinitionCode,
            DefinitionName = source.DefinitionName,
            IsDefaultDefinition = false,
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
                ComponentDefinitionCode = line.ComponentDefinitionCode,
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
        return await GetAsync(code, defCode, header.Version, cancellationToken);
    }

    public async Task<IvMasterOperationResult<PrProductDefEditVm>> ActivateAsync(
        string prodCode,
        string definitionCode,
        int version,
        byte[]? headerRowVersion,
        CancellationToken cancellationToken = default)
    {
        var get = await GetAsync(prodCode, definitionCode, version, cancellationToken);
        if (!get.Succeeded || get.Data is null)
        {
            return get;
        }

        var model = get.Data;
        model.HeaderRowVersion = headerRowVersion ?? model.HeaderRowVersion;
        return await SaveAsync(model, isNew: false, activate: true, cancellationToken);
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>> ListActiveDefinitionsAsync(
        string prodCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailLookup(ctx.Error);
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
            return FailLookup(ctx.Error);
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
                : PrProductDefinitionCodes.Normalize(x.DefinitionCode), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var active = g.FirstOrDefault(x => x.Status == PrBomStatuses.Active);
                var drafts = g.Where(x => x.Status == PrBomStatuses.Draft).ToList();
                if (active is null && drafts.Count == 0)
                {
                    return null;
                }

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
                    IsDefaultDefinition = active?.IsDefaultDefinition ?? false
                };
            })
            .Where(x => x is not null)
            .Cast<PrProductDefinitionLookupRow>()
            .OrderByDescending(x => x.IsDefaultDefinition)
            .ThenBy(x => x.DefinitionCode, StringComparer.Ordinal)
            .ToList();

        return IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>.Ok(rows);
    }

    public async Task<IvMasterOperationResult<DeleteCheckResult>> CanDeleteAsync(
        IReadOnlyList<PrProductDefinitionKey> keys,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return IvMasterOperationResult<DeleteCheckResult>.Fail(ctx.Error.Code, ctx.Error.Message);
        }

        var normalized = NormalizeDefinitionKeys(keys);
        if (normalized.Count == 0)
        {
            return IvMasterOperationResult<DeleteCheckResult>.Fail(
                IvMasterErrorCode.Validation, "Select at least one product definition.");
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var blockers = await FindDeleteBlockersAsync(db, company, normalized, cancellationToken);
        if (blockers.Count > 0)
        {
            return IvMasterOperationResult<DeleteCheckResult>.Ok(
                DeleteCheckResult.Blocked(
                    "Product Definition cannot be deleted because it is referenced by Work Orders or other definitions.",
                    blockers));
        }

        return IvMasterOperationResult<DeleteCheckResult>.Ok(
            DeleteCheckResult.Ok("Deletes BOM headers and component lines only; Stock Master items are not deleted."));
    }

    public async Task<IvMasterOperationResult<object?>> DeleteAsync(
        IReadOnlyList<PrProductDefinitionKey> keys,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return IvMasterOperationResult<object?>.Fail(ctx.Error.Code, ctx.Error.Message);
        }

        var normalized = NormalizeDefinitionKeys(keys);
        if (normalized.Count == 0)
        {
            return IvMasterOperationResult<object?>.Fail(
                IvMasterErrorCode.Validation, "Select at least one product definition.");
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var blockers = await FindDeleteBlockersAsync(db, company, normalized, cancellationToken);
        if (blockers.Count > 0)
        {
            return IvMasterOperationResult<object?>.Fail(
                IvMasterErrorCode.InUse,
                "Product Definition cannot be deleted because it is referenced by Work Orders or other definitions.");
        }

        var prodCodes = normalized.Select(x => x.ProdCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var defCodes = normalized.Select(x => x.DefinitionCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var headers = await db.PrBomHdrs
            .Where(x => x.CompanyCode == company
                        && prodCodes.Contains(x.ProdCode)
                        && defCodes.Contains(x.DefinitionCode))
            .ToListAsync(cancellationToken);

        headers = headers
            .Where(h => normalized.Any(k =>
                string.Equals(k.ProdCode, h.ProdCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(k.DefinitionCode, h.DefinitionCode, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (headers.Count == 0)
        {
            return IvMasterOperationResult<object?>.Fail(
                IvMasterErrorCode.NotFound, "Product definition not found.");
        }

        db.PrBomHdrs.RemoveRange(headers);
        await db.SaveChangesAsync(cancellationToken);
        return IvMasterOperationResult<object?>.Ok();
    }

    private static async Task<List<IvMasterReferenceHit>> FindDeleteBlockersAsync(
        AppDbContext db,
        string company,
        IReadOnlyList<PrProductDefinitionKey> keys,
        CancellationToken cancellationToken)
    {
        var hits = new List<IvMasterReferenceHit>();
        var prodCodes = keys.Select(x => x.ProdCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var defCodes = keys.Select(x => x.DefinitionCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var headers = await db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && prodCodes.Contains(x.ProdCode)
                        && defCodes.Contains(x.DefinitionCode))
            .Select(x => new { x.Uid, x.ProdCode, x.DefinitionCode })
            .ToListAsync(cancellationToken);

        var matchedHeaders = headers
            .Where(h => keys.Any(k =>
                string.Equals(k.ProdCode, h.ProdCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(k.DefinitionCode, h.DefinitionCode, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var headerIds = matchedHeaders.Select(x => x.Uid).ToList();

        // Work Orders by Product + SourceDefinitionCode
        var woRefs = await db.ProductionWorkOrders.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && prodCodes.Contains(x.ProductCode)
                        && defCodes.Contains(x.SourceDefinitionCode))
            .Select(x => new { x.ProductCode, x.SourceDefinitionCode, x.Uid })
            .ToListAsync(cancellationToken);
        woRefs = woRefs
            .Where(x => keys.Any(k =>
                string.Equals(k.ProdCode, x.ProductCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(k.DefinitionCode, x.SourceDefinitionCode, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var g in woRefs.GroupBy(
                     x => (x.ProductCode, x.SourceDefinitionCode),
                     new ProdDefTupleComparer()))
        {
            hits.Add(new IvMasterReferenceHit
            {
                ReferenceType = "Production Work Order",
                Count = g.Select(x => x.Uid).Distinct().Count(),
                Detail = $"{g.Key.Item1}[{g.Key.Item2}]"
            });
        }

        // Material snapshots by SourceBomHdrId
        if (headerIds.Count > 0)
        {
            var materialCount = await db.ProductionWorkOrderMaterials.AsNoTracking()
                .Where(x => x.SourceBomHdrId != null && headerIds.Contains(x.SourceBomHdrId.Value))
                .Select(x => x.WorkOrderId)
                .Distinct()
                .CountAsync(cancellationToken);
            if (materialCount > 0)
            {
                hits.Add(new IvMasterReferenceHit
                {
                    ReferenceType = "Work Order Material Snapshot",
                    Count = materialCount,
                    Detail = string.Join(", ", keys.Select(k => $"{k.ProdCode}[{k.DefinitionCode}]"))
                });
            }
        }

        // SEPARATE parent lines referencing ICode + ComponentDefinitionCode
        foreach (var key in keys)
        {
            var parentCount = await db.PrDefBOMs.AsNoTracking()
                .Where(x => x.CompanyCode == company
                            && x.ICode == key.ProdCode
                            && x.ComponentDefinitionCode == key.DefinitionCode
                            && x.SupplySource == PrMaterialSupplySources.SeparateProductDefinition)
                .Select(x => x.BomHdrId)
                .Distinct()
                .CountAsync(cancellationToken);
            if (parentCount > 0)
            {
                hits.Add(new IvMasterReferenceHit
                {
                    ReferenceType = "Parent Product Definition (SEPARATE)",
                    Count = parentCount,
                    Detail = $"{key.ProdCode}[{key.DefinitionCode}]"
                });
            }
        }

        return hits;
    }

    /// <summary>
    /// Two-step activation inside the caller's open transaction: free ACTIVE/default filtered-index
    /// slots, SaveChanges, then set the target ACTIVE + IsDefaultDefinition and SaveChanges again.
    /// </summary>
    private static async Task ActivateRevisionInTransactionAsync(
        AppDbContext db,
        string company,
        PrBomHdr header,
        bool requestDefault,
        DateTime now,
        string user,
        CancellationToken cancellationToken)
    {
        var prodCode = header.ProdCode;
        var definitionCode = string.IsNullOrWhiteSpace(header.DefinitionCode)
            ? PrProductDefinitionCodes.Standard
            : PrProductDefinitionCodes.Normalize(header.DefinitionCode);

        var priorActive = await db.PrBomHdrs
            .Where(x => x.CompanyCode == company
                        && x.ProdCode == prodCode
                        && x.DefinitionCode == definitionCode
                        && x.Status == PrBomStatuses.Active
                        && x.Uid != header.Uid)
            .ToListAsync(cancellationToken);

        var priorWasDefault = priorActive.Any(x => x.IsDefaultDefinition);
        foreach (var prior in priorActive)
        {
            prior.Status = PrBomStatuses.Superseded;
            prior.IsDefaultDefinition = false;
            prior.ModifiedDate = now;
            prior.ModifiedBy = user;
        }

        var otherDefaults = await db.PrBomHdrs
            .Where(x => x.CompanyCode == company
                        && x.ProdCode == prodCode
                        && x.Status == PrBomStatuses.Active
                        && x.IsDefaultDefinition
                        && x.Uid != header.Uid
                        && x.DefinitionCode != definitionCode)
            .ToListAsync(cancellationToken);

        var otherDefaultExists = otherDefaults.Count > 0;
        var setDefault = requestDefault
                         || (priorWasDefault && !otherDefaultExists)
                         || (!priorWasDefault && !otherDefaultExists);

        if (setDefault)
        {
            foreach (var other in otherDefaults)
            {
                other.IsDefaultDefinition = false;
                other.ModifiedDate = now;
                other.ModifiedBy = user;
            }
        }

        // Step 1: free filtered unique index slots
        await db.SaveChangesAsync(cancellationToken);

        // Step 2: activate target
        header.Status = PrBomStatuses.Active;
        header.IsDefaultDefinition = setDefault;
        header.ActivatedDate = now;
        header.ActivatedBy = user;
        header.ModifiedDate = now;
        header.ModifiedBy = user;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Dictionary<CircularBomValidator.NodeKey, IReadOnlyList<CircularBomValidator.NodeKey>>>
        BuildActiveComponentGraphAsync(
            AppDbContext db,
            string company,
            CircularBomValidator.NodeKey? exclude,
            CancellationToken cancellationToken)
    {
        var activeHeaders = await db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.Status == PrBomStatuses.Active)
            .Select(x => new { x.Uid, x.ProdCode, x.DefinitionCode })
            .ToListAsync(cancellationToken);

        if (exclude is { } ex)
        {
            var excludeKey = CircularBomValidator.NodeKey.Create(ex.ProdCode, ex.DefinitionCode);
            activeHeaders = activeHeaders
                .Where(x => CircularBomValidator.NodeKey.Create(x.ProdCode, x.DefinitionCode) != excludeKey)
                .ToList();
        }

        var hdrIds = activeHeaders.Select(x => x.Uid).ToList();
        var lines = await db.PrDefBOMs.AsNoTracking()
            .Where(x => hdrIds.Contains(x.BomHdrId))
            .Select(x => new { x.BomHdrId, x.ICode, x.ComponentDefinitionCode, x.SupplySource })
            .ToListAsync(cancellationToken);

        var byHdr = lines.GroupBy(x => x.BomHdrId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CircularBomValidator.NodeKey>)g
                    .Where(x => string.Equals(
                        x.SupplySource,
                        PrMaterialSupplySources.SeparateProductDefinition,
                        StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(x.ComponentDefinitionCode))
                    .Select(x => CircularBomValidator.NodeKey.Create(x.ICode, x.ComponentDefinitionCode))
                    .ToList());

        var graph = new Dictionary<CircularBomValidator.NodeKey, IReadOnlyList<CircularBomValidator.NodeKey>>();
        foreach (var h in activeHeaders)
        {
            var key = CircularBomValidator.NodeKey.Create(
                h.ProdCode,
                string.IsNullOrWhiteSpace(h.DefinitionCode) ? PrProductDefinitionCodes.Standard : h.DefinitionCode);
            graph[key] = byHdr.TryGetValue(h.Uid, out var c) ? c : [];
        }

        return graph;
    }

    /// <summary>
    /// Same header pick as <see cref="GetAsync"/>: explicit version, else latest Version desc
    /// for Company + Prod + DefinitionCode.
    /// </summary>
    private static async Task<PrBomHdr?> ResolveHeaderForEditAsync(
        AppDbContext db,
        string company,
        string prodCode,
        string definitionCode,
        int? version,
        CancellationToken cancellationToken)
    {
        var def = PrProductDefinitionCodes.Normalize(definitionCode);
        if (version is int v)
        {
            return await db.PrBomHdrs.AsNoTracking()
                .FirstOrDefaultAsync(x => x.CompanyCode == company
                                          && x.ProdCode == prodCode
                                          && x.DefinitionCode == def
                                          && x.Version == v,
                    cancellationToken);
        }

        return await db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.ProdCode == prodCode
                        && x.DefinitionCode == def)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<PrBomHdr?> ResolveActiveHeaderAsync(
        AppDbContext db,
        string company,
        string prodCode,
        string definitionCode,
        CancellationToken cancellationToken)
    {
        var def = PrProductDefinitionCodes.Normalize(definitionCode);
        return await db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.ProdCode == prodCode
                        && x.DefinitionCode == def
                        && x.Status == PrBomStatuses.Active)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task WalkStructureAsync(
        AppDbContext db,
        string company,
        string ownerProdCode,
        string ownerDefinitionCode,
        PrBomHdr ownerHeader,
        string parentKey,
        int level,
        List<(string Prod, string Def)> pathStack,
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
            var ownerDefinition = string.IsNullOrWhiteSpace(ownerDefinitionCode)
                ? PrProductDefinitionCodes.Standard
                : PrProductDefinitionCodes.Normalize(ownerDefinitionCode);
            var key = PrBomStructureKeys.Line(parentKey, ownerProdCode, ownerDefinition, lineId);
            var item = await GetStructureItemAsync(db, company, childCode, cache, cancellationToken);
            var mfg = item?.MfgType ?? PrMfgTypes.Buy;
            var childLevel = level + 1;

            var isSeparate = string.Equals(
                line.SupplySource,
                PrMaterialSupplySources.SeparateProductDefinition,
                StringComparison.OrdinalIgnoreCase);
            var childDefinition = isSeparate
                ? PrProductDefinitionCodes.Normalize(line.ComponentDefinitionCode)
                : null;

            var status = PrBomStructureNodeStatus.Normal;
            var shouldRecurse = isSeparate
                                && !string.IsNullOrWhiteSpace(childDefinition);

            if (shouldRecurse
                && pathStack.Any(p =>
                    string.Equals(p.Prod, childCode, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.Def, childDefinition, StringComparison.OrdinalIgnoreCase)))
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
            if (shouldRecurse && childDefinition is not null)
            {
                childHeader = await ResolveActiveHeaderCachedAsync(
                    db, company, childCode, childDefinition, cache, cancellationToken);
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
                OwnerDefinitionCode = ownerDefinition,
                ComponentDefinitionCode = childDefinition,
                SourceLineUid = line.Uid,
                BomHdrId = ownerHeader.Uid,
                BomVersion = ownerHeader.Version,
                BomStatus = ownerHeader.Status,
                Status = status
            });

            if (shouldRecurse && childHeader is not null && childDefinition is not null)
            {
                pathStack.Add((childCode, childDefinition));
                await WalkStructureAsync(
                    db,
                    company,
                    ownerProdCode: childCode,
                    ownerDefinitionCode: childDefinition,
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

    private static async Task<PrBomHdr?> ResolveActiveHeaderCachedAsync(
        AppDbContext db,
        string company,
        string prodCode,
        string definitionCode,
        StructureLoadCache cache,
        CancellationToken cancellationToken)
    {
        var def = PrProductDefinitionCodes.Normalize(definitionCode);
        if (cache.ActiveResolved.TryGetValue((prodCode, def), out var known))
        {
            return known;
        }

        var header = await ResolveActiveHeaderAsync(db, company, prodCode, def, cancellationToken);
        cache.ActiveResolved[(prodCode, def)] = header;
        if (header is not null)
        {
            cache.Headers[(prodCode, def, header.Version)] = header;
        }

        return header;
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
        public Dictionary<(string Prod, string Def, int Version), PrBomHdr> Headers { get; } =
            new(new ProdDefVersionComparer());

        public Dictionary<(string Prod, string Def), PrBomHdr?> ActiveResolved { get; } =
            new(new ProdDefTupleComparer());

        public Dictionary<long, List<PrDefBOM>> Lines { get; } = new();
        public Dictionary<string, StructureItemInfo> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProdDefVersionComparer : IEqualityComparer<(string Prod, string Def, int Version)>
    {
        public bool Equals((string Prod, string Def, int Version) x, (string Prod, string Def, int Version) y) =>
            x.Version == y.Version
            && string.Equals(x.Prod, y.Prod, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Def, y.Def, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Prod, string Def, int Version) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Prod),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Def),
                obj.Version);
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
            DefinitionCode = header.DefinitionCode,
            DefinitionName = header.DefinitionName,
            IsDefaultDefinition = header.IsDefaultDefinition,
            BomHdrId = header.Uid,
            Version = header.Version,
            Status = header.Status,
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
                ComponentDefinitionCode = x.ComponentDefinitionCode,
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

    private static IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>> FailLookup(ScopeError error) =>
        IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>.Fail(error.Code, error.Message);

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

    private static List<PrProductDefinitionKey> NormalizeDefinitionKeys(IReadOnlyList<PrProductDefinitionKey>? keys)
    {
        var result = new List<PrProductDefinitionKey>();
        var seen = new HashSet<(string, string)>(new ProdDefTupleComparer());
        foreach (var key in keys ?? [])
        {
            var prod = NormalizeCode(key.ProdCode);
            var def = PrProductDefinitionCodes.Normalize(key.DefinitionCode);
            if (prod.Length == 0 || def.Length == 0)
                continue;
            if (!seen.Add((prod, def)))
                continue;
            result.Add(new PrProductDefinitionKey { ProdCode = prod, DefinitionCode = def });
        }

        return result;
    }

    private sealed class ProdDefTupleComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }

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
