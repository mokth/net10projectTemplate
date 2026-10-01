using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Planning;

public sealed class BomExplosionService : IBomExplosionService
{
    public const int MaxDepth = 50;
    public const int MaxNodes = 10_000;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;

    public BomExplosionService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
    }

    public async Task<IvMasterOperationResult<BomExplosionResult>> ExplodeAsync(
        BomExplosionRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new BomExplosionRequest();
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return IvMasterOperationResult<BomExplosionResult>.Fail(
                IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        if (!await _accessRights.CanAsync(MenuCodes.PlanningProductDef, PermissionCodes.Access, cancellationToken))
        {
            return IvMasterOperationResult<BomExplosionResult>.Fail(
                IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await ExplodeInContextAsync(db, scope.CompanyCode, request, cancellationToken);
    }

    /// <summary>
    /// Executes the calculation against the caller's DbContext/transaction. This is internal on
    /// purpose: callers must enforce their own tenant and permission boundary before invoking it.
    /// </summary>
    internal async Task<IvMasterOperationResult<BomExplosionResult>> ExplodeInContextAsync(
        AppDbContext db,
        string companyCode,
        BomExplosionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        request ??= new BomExplosionRequest();

        var company = Normalize(companyCode);
        if (company.Length == 0)
        {
            return IvMasterOperationResult<BomExplosionResult>.Fail(
                IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        var prodCode = Normalize(request.ProdCode);
        if (prodCode.Length == 0)
        {
            return Fail("Product code is required.", "ProdCode");
        }

        var definitionCode = string.IsNullOrWhiteSpace(request.DefinitionCode)
            ? PrProductDefinitionCodes.Standard
            : PrProductDefinitionCodes.Normalize(request.DefinitionCode);
        if (definitionCode.Length == 0 || !PrProductDefinitionCodes.IsValidFormat(definitionCode))
        {
            return Fail("Definition code is required.", nameof(request.DefinitionCode));
        }

        if (request.Quantity <= 0m)
        {
            return Fail("Quantity must be greater than zero.", nameof(request.Quantity));
        }

        var rootItem = await db.IvStockMasters.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.ICode == prodCode, cancellationToken);
        if (rootItem is null)
        {
            return Fail($"Product {prodCode} does not exist in Stock Master.", "ProdCode");
        }

        var rootHdr = await ResolveHeaderAsync(
            db, company, prodCode, definitionCode, request.Version, cancellationToken);
        if (rootHdr is null)
        {
            return Fail(
                request.Version is int v
                    ? $"Cannot explode Product {prodCode}[{definitionCode}]: BOM version {v} was not found."
                    : $"Cannot explode Product {prodCode}[{definitionCode}]: no ACTIVE Product Definition revision.",
                "ProdCode");
        }

        var cache = new BomLoadCache();
        cache.Headers[(prodCode, definitionCode, rootHdr.Version)] = rootHdr;
        cache.ActiveByKey[(prodCode, definitionCode)] = rootHdr;
        cache.Items[prodCode] = new ItemInfo(prodCode, rootItem.IDesc, PrMfgTypes.Normalize(rootItem.MfgType), rootItem.StdUom);

        var nodes = new List<BomExplosionNode>();
        var pathStack = new List<(string Prod, string Def)> { (prodCode, definitionCode) };
        var error = await WalkAsync(
            db,
            company,
            request.Mode,
            parentCode: null,
            itemCode: prodCode,
            itemDefinitionCode: definitionCode,
            extendedQty: request.Quantity,
            level: 0,
            qtyPerParent: request.Quantity,
            scrapPercent: 0m,
            warehouse: null,
            supplySource: null,
            componentDefinitionCode: null,
            sourceLineUid: null,
            bomHdr: rootHdr,
            issueContext: true,
            pathStack,
            nodes,
            cache,
            cancellationToken);

        if (error is not null)
        {
            return IvMasterOperationResult<BomExplosionResult>.Fail(
                IvMasterErrorCode.Validation, error);
        }

        IReadOnlyList<BomExplosionNode> output = request.Mode switch
        {
            BomExplosionMode.MaterialRequirement => nodes
                .Where(n => n.Level > 0 && n.MfgType is PrMfgTypes.Buy or PrMfgTypes.Make)
                .OrderBy(n => n.Path)
                .ToList(),
            BomExplosionMode.ProductionIssueRequirement => nodes
                .Where(n => n.IsIssueLine)
                .OrderBy(n => n.Path)
                .ToList(),
            _ => nodes.OrderBy(n => n.Level).ThenBy(n => n.Path).ToList()
        };

        return IvMasterOperationResult<BomExplosionResult>.Ok(new BomExplosionResult
        {
            RootProdCode = prodCode,
            RootDefinitionCode = definitionCode,
            RootQuantity = request.Quantity,
            Mode = request.Mode,
            RootBomHdrId = rootHdr.Uid,
            RootBomVersion = rootHdr.Version,
            RootBaseQty = rootHdr.BaseQty,
            RootBaseUom = rootHdr.BaseUom,
            Nodes = output
        });
    }

    private async Task<string?> WalkAsync(
        AppDbContext db,
        string company,
        BomExplosionMode mode,
        string? parentCode,
        string itemCode,
        string itemDefinitionCode,
        decimal extendedQty,
        int level,
        decimal qtyPerParent,
        decimal scrapPercent,
        string? warehouse,
        string? supplySource,
        string? componentDefinitionCode,
        long? sourceLineUid,
        PrBomHdr? bomHdr,
        bool issueContext,
        List<(string Prod, string Def)> pathStack,
        List<BomExplosionNode> nodes,
        BomLoadCache cache,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (level > MaxDepth)
        {
            return $"BOM explosion exceeded maximum depth ({MaxDepth}) at path {FormatPath(pathStack)}.";
        }

        if (nodes.Count >= MaxNodes)
        {
            return $"BOM explosion exceeded maximum node count ({MaxNodes}).";
        }

        var item = await GetItemAsync(db, company, itemCode, cache, cancellationToken);
        if (item is null)
        {
            return $"Item {itemCode} does not exist in Stock Master.";
        }

        var mfg = item.MfgType;
        var isRoot = level == 0;
        var isLeafRequirement = !isRoot && mfg == PrMfgTypes.Buy;
        var isMaterialNode = !isRoot && mfg is PrMfgTypes.Buy or PrMfgTypes.Make;
        var isIssueLine = !isRoot && issueContext && mfg is PrMfgTypes.Buy or PrMfgTypes.Make;

        // StructuralTree: every node. MaterialRequirement: BUY+MAKE (Phantom exploded through, not listed).
        // ProductionIssueRequirement: direct BUY+MAKE after Phantom pass-through.
        var emit = mode switch
        {
            BomExplosionMode.StructuralTree => true,
            BomExplosionMode.MaterialRequirement => isRoot || isMaterialNode,
            BomExplosionMode.ProductionIssueRequirement => isIssueLine,
            _ => true
        };

        if (emit)
        {
            nodes.Add(new BomExplosionNode
            {
                Level = level,
                ItemCode = itemCode,
                ItemDesc = item.Desc,
                ParentItemCode = parentCode,
                QtyPerParent = qtyPerParent,
                ExtendedQty = extendedQty,
                Uom = item.StdUom,
                MfgType = mfg,
                DefinitionCode = itemDefinitionCode,
                BomVersion = bomHdr?.Version,
                BomHdrId = bomHdr?.Uid,
                SourceLineUid = sourceLineUid,
                ScrapPercent = scrapPercent,
                Warehouse = warehouse,
                SupplySource = supplySource,
                ComponentDefinitionCode = componentDefinitionCode,
                Path = FormatPath(pathStack),
                IsLeafRequirement = isLeafRequirement,
                IsIssueLine = isIssueLine
            });
        }

        // Recursion policy: follow SEPARATE child definitions; preserve MAKE/PHANTOM/BUY emit semantics.
        // MAKE without SEPARATE is a procurement boundary (no child definition walk).
        // PHANTOM without SEPARATE has no child definition to explode through.
        var shouldRecurse = mfg is PrMfgTypes.Make or PrMfgTypes.Phantom || isRoot;
        if (!shouldRecurse)
        {
            return null;
        }

        if (mode == BomExplosionMode.ProductionIssueRequirement && !isRoot && mfg == PrMfgTypes.Make)
        {
            return null;
        }

        var childIssueContext = mode == BomExplosionMode.ProductionIssueRequirement
            && (isRoot || mfg == PrMfgTypes.Phantom);

        PrBomHdr? childHdr = bomHdr;
        if (!isRoot || childHdr is null)
        {
            if (mfg is PrMfgTypes.Make or PrMfgTypes.Phantom || isRoot)
            {
                var resolveDefinition = string.IsNullOrWhiteSpace(itemDefinitionCode)
                    ? PrProductDefinitionCodes.Standard
                    : itemDefinitionCode;
                childHdr = await ResolveHeaderCachedAsync(
                    db, company, itemCode, resolveDefinition, version: null, cache, cancellationToken);
                if (childHdr is null)
                {
                    return $"Cannot explode Product {pathStack[0].Prod}[{pathStack[0].Def}] because {(mfg == PrMfgTypes.Phantom ? "Phantom" : "Make")} item {itemCode}[{resolveDefinition}] has no ACTIVE Product Definition revision.";
                }
            }
        }

        if (childHdr is null)
        {
            return null;
        }

        var lines = await GetLinesAsync(db, company, childHdr.Uid, cache, cancellationToken);
        var cycle = DetectPathCycle(pathStack);
        if (cycle is not null)
        {
            return $"Cannot explode BOM because it creates a circular BOM: {cycle}.";
        }

        var ownerDefinition = string.IsNullOrWhiteSpace(childHdr.DefinitionCode)
            ? PrProductDefinitionCodes.Standard
            : PrProductDefinitionCodes.Normalize(childHdr.DefinitionCode);

        foreach (var line in lines.OrderBy(x => x.SeqNo).ThenBy(x => x.ICode))
        {
            if (mode is BomExplosionMode.MaterialRequirement or BomExplosionMode.ProductionIssueRequirement
                && !line.BomDefault)
            {
                continue;
            }

            var childCode = Normalize(line.ICode);
            var supply = string.IsNullOrWhiteSpace(line.SupplySource)
                ? PrMaterialSupplySources.Purchased
                : line.SupplySource.Trim().ToUpperInvariant();
            var isSeparate = string.Equals(
                supply,
                PrMaterialSupplySources.SeparateProductDefinition,
                StringComparison.Ordinal);

            string? separateDefinition = null;
            if (isSeparate)
            {
                separateDefinition = PrProductDefinitionCodes.Normalize(line.ComponentDefinitionCode);
                if (separateDefinition.Length == 0)
                {
                    return $"SEPARATE_PRODUCT_DEFINITION line {childCode} under {itemCode}[{ownerDefinition}] is missing ComponentDefinitionCode.";
                }
            }

            // Child definition selection:
            // SEPARATE → ComponentDefinitionCode.
            // MAKE/PHANTOM without SEPARATE → ACTIVE STANDARD (legacy multi-level / mode semantics).
            // BUY / INTERNAL / EXTERNAL → no nested definition.
            var childItem = await GetItemAsync(db, company, childCode, cache, cancellationToken);
            var childMfg = childItem?.MfgType ?? PrMfgTypes.Buy;
            string? recurseDefinition = null;
            if (isSeparate)
            {
                recurseDefinition = separateDefinition;
            }
            else if (childMfg is PrMfgTypes.Make or PrMfgTypes.Phantom)
            {
                recurseDefinition = PrProductDefinitionCodes.Standard;
            }

            var pathDef = recurseDefinition ?? ownerDefinition;
            if (pathStack.Any(p =>
                    string.Equals(p.Prod, childCode, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.Def, pathDef, StringComparison.OrdinalIgnoreCase)))
            {
                var cyclePath = FormatPath(pathStack) + " → " + CircularBomValidator.NodeKey.Format(childCode, pathDef);
                return $"Cannot explode BOM because it creates a circular BOM: {cyclePath}.";
            }

            var childExt = PrBomCalc.RequiredQty(
                extendedQty, childHdr.BaseQty, line.StdQty, line.ScrapPercent);

            pathStack.Add((childCode, pathDef));
            var err = await WalkAsync(
                db,
                company,
                mode,
                parentCode: itemCode,
                itemCode: childCode,
                itemDefinitionCode: recurseDefinition ?? string.Empty,
                extendedQty: childExt,
                level: level + 1,
                qtyPerParent: line.StdQty,
                scrapPercent: line.ScrapPercent,
                warehouse: line.Warehouse,
                supplySource: supply,
                componentDefinitionCode: separateDefinition,
                sourceLineUid: line.Uid,
                bomHdr: null,
                issueContext: childIssueContext,
                pathStack,
                nodes,
                cache,
                cancellationToken);
            pathStack.RemoveAt(pathStack.Count - 1);
            if (err is not null)
            {
                return err;
            }
        }

        return null;
    }

    private static string FormatPath(List<(string Prod, string Def)> pathStack) =>
        string.Join(" → ", pathStack.Select(p => CircularBomValidator.NodeKey.Format(p.Prod, p.Def)));

    private static string? DetectPathCycle(List<(string Prod, string Def)> pathStack)
    {
        var seen = new HashSet<(string, string)>();
        foreach (var p in pathStack)
        {
            var key = (Normalize(p.Prod), Normalize(p.Def));
            if (!seen.Add(key))
            {
                return FormatPath(pathStack);
            }
        }

        return null;
    }

    private static async Task<PrBomHdr?> ResolveHeaderAsync(
        AppDbContext db,
        string company,
        string prodCode,
        string definitionCode,
        int? version,
        CancellationToken cancellationToken)
    {
        var def = PrProductDefinitionCodes.Normalize(definitionCode);
        var q = db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.ProdCode == prodCode
                        && x.DefinitionCode == def);

        if (version is int v)
        {
            return await q.FirstOrDefaultAsync(x => x.Version == v, cancellationToken);
        }

        return await q
            .Where(x => x.Status == PrBomStatuses.Active)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<PrBomHdr?> ResolveHeaderCachedAsync(
        AppDbContext db,
        string company,
        string prodCode,
        string definitionCode,
        int? version,
        BomLoadCache cache,
        CancellationToken cancellationToken)
    {
        var def = PrProductDefinitionCodes.Normalize(definitionCode);
        if (def.Length == 0)
        {
            return null;
        }

        if (version is int v && cache.Headers.TryGetValue((prodCode, def, v), out var cachedVer))
        {
            return cachedVer;
        }

        if (version is null && cache.ActiveByKey.TryGetValue((prodCode, def), out var cachedActive))
        {
            return cachedActive;
        }

        if (!cache.AllHeadersLoaded.Contains((prodCode, def)))
        {
            var list = await db.PrBomHdrs.AsNoTracking()
                .Where(x => x.CompanyCode == company
                            && x.ProdCode == prodCode
                            && x.DefinitionCode == def)
                .ToListAsync(cancellationToken);
            cache.AllHeadersLoaded.Add((prodCode, def));
            foreach (var h in list)
            {
                cache.Headers[(prodCode, def, h.Version)] = h;
            }

            var active = list
                .Where(x => x.Status == PrBomStatuses.Active)
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();
            if (active is not null)
            {
                cache.ActiveByKey[(prodCode, def)] = active;
            }
        }

        if (version is int ver)
        {
            return cache.Headers.TryGetValue((prodCode, def, ver), out var h) ? h : null;
        }

        return cache.ActiveByKey.TryGetValue((prodCode, def), out var activeHdr) ? activeHdr : null;
    }

    private static async Task<ItemInfo?> GetItemAsync(
        AppDbContext db,
        string company,
        string iCode,
        BomLoadCache cache,
        CancellationToken cancellationToken)
    {
        if (cache.Items.TryGetValue(iCode, out var cached))
        {
            return cached;
        }

        var row = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.ICode == iCode)
            .Select(x => new { x.ICode, x.IDesc, x.MfgType, x.StdUom })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var info = new ItemInfo(row.ICode, row.IDesc, PrMfgTypes.Normalize(row.MfgType), row.StdUom);
        cache.Items[iCode] = info;
        return info;
    }

    private static async Task<IReadOnlyList<PrDefBOM>> GetLinesAsync(
        AppDbContext db,
        string company,
        long bomHdrId,
        BomLoadCache cache,
        CancellationToken cancellationToken)
    {
        if (cache.Lines.TryGetValue(bomHdrId, out var cached))
        {
            return cached;
        }

        var lines = await db.PrDefBOMs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BomHdrId == bomHdrId)
            .ToListAsync(cancellationToken);
        cache.Lines[bomHdrId] = lines;

        var codes = lines.Select(x => x.ICode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (codes.Count > 0)
        {
            var missingItems = codes.Where(c => !cache.Items.ContainsKey(c)).ToList();
            if (missingItems.Count > 0)
            {
                var items = await db.IvStockMasters.AsNoTracking()
                    .Where(x => x.CompanyCode == company && missingItems.Contains(x.ICode))
                    .Select(x => new { x.ICode, x.IDesc, x.MfgType, x.StdUom })
                    .ToListAsync(cancellationToken);
                foreach (var it in items)
                {
                    cache.Items[it.ICode] = new ItemInfo(
                        it.ICode, it.IDesc, PrMfgTypes.Normalize(it.MfgType), it.StdUom);
                }
            }
        }

        return lines;
    }

    private static IvMasterOperationResult<BomExplosionResult> Fail(string message, string? field = null)
    {
        Dictionary<string, string>? errors = null;
        if (!string.IsNullOrWhiteSpace(field))
        {
            errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [field] = message };
        }

        return IvMasterOperationResult<BomExplosionResult>.Fail(
            IvMasterErrorCode.Validation, message, errors);
    }

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    private sealed class BomLoadCache
    {
        public Dictionary<string, ItemInfo> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(string Prod, string Def, int Ver), PrBomHdr> Headers { get; } = new();
        public Dictionary<(string Prod, string Def), PrBomHdr> ActiveByKey { get; } = new();
        public HashSet<(string Prod, string Def)> AllHeadersLoaded { get; } = new();
        public Dictionary<long, List<PrDefBOM>> Lines { get; } = new();
    }

    private sealed record ItemInfo(string Code, string? Desc, string MfgType, string? StdUom);
}
