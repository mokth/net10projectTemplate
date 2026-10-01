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
