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
    /// Work Order save uses it so BOM selection, snapshot creation and persistence share one
    /// transaction instead of trusting an earlier browser preview.
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

        if (request.Quantity <= 0m)
        {
            return Fail("Quantity must be greater than zero.", nameof(request.Quantity));
        }

        var asOf = request.AsOfDate.Date;

        var rootItem = await db.IvStockMasters.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == company && x.ICode == prodCode, cancellationToken);
        if (rootItem is null)
        {
            return Fail($"Product {prodCode} does not exist in Stock Master.", "ProdCode");
        }

        var rootHdr = await ResolveHeaderAsync(
            db, company, prodCode, asOf, request.Version, cancellationToken);
        if (rootHdr is null)
        {
            return Fail(
                request.Version is int v
                    ? $"Cannot explode Product {prodCode}: BOM version {v} was not found."
                    : $"Cannot explode Product {prodCode}: no active BOM for the requested date.",
                "ProdCode");
        }

        var cache = new BomLoadCache();
        cache.Headers[(prodCode, rootHdr.Version)] = rootHdr;
        cache.Items[prodCode] = new ItemInfo(prodCode, rootItem.IDesc, PrMfgTypes.Normalize(rootItem.MfgType), rootItem.StdUom);

        var nodes = new List<BomExplosionNode>();
        var pathStack = new List<string> { prodCode };
        var error = await WalkAsync(
            db,
            company,
            asOf,
            request.Mode,
            parentCode: null,
            itemCode: prodCode,
            extendedQty: request.Quantity,
            level: 0,
            qtyPerParent: request.Quantity,
            scrapPercent: 0m,
            warehouse: null,
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
            RootQuantity = request.Quantity,
            AsOfDate = asOf,
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
        DateTime asOf,
        BomExplosionMode mode,
        string? parentCode,
        string itemCode,
        decimal extendedQty,
        int level,
        decimal qtyPerParent,
        decimal scrapPercent,
        string? warehouse,
        long? sourceLineUid,
        PrBomHdr? bomHdr,
        bool issueContext,
        List<string> pathStack,
        List<BomExplosionNode> nodes,
        BomLoadCache cache,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (level > MaxDepth)
        {
            return $"BOM explosion exceeded maximum depth ({MaxDepth}) at path {string.Join(" → ", pathStack)}.";
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
                BomVersion = bomHdr?.Version,
                BomHdrId = bomHdr?.Uid,
                SourceLineUid = sourceLineUid,
                ScrapPercent = scrapPercent,
                Warehouse = warehouse,
                Path = string.Join(" → ", pathStack),
                IsLeafRequirement = isLeafRequirement,
                IsIssueLine = isIssueLine
            });
        }

        // Recursion policy
        var shouldRecurse = mfg is PrMfgTypes.Make or PrMfgTypes.Phantom || isRoot;
        if (!shouldRecurse)
        {
            return null;
        }

        // For ProductionIssue: after emitting a MAKE child as issue line, do not recurse into its children for issue.
        // Still must not recurse Make children in Issue mode.
        if (mode == BomExplosionMode.ProductionIssueRequirement && !isRoot && mfg == PrMfgTypes.Make)
        {
            return null;
        }

        // Phantom in Issue mode: explode through (issueContext stays true).
        // Make in Material/Tree: recurse with issueContext false for descendants under Make when in Issue mode — already returned.
        var childIssueContext = mode == BomExplosionMode.ProductionIssueRequirement
            && (isRoot || mfg == PrMfgTypes.Phantom);

        PrBomHdr? childHdr = bomHdr;
        if (!isRoot || childHdr is null)
        {
            if (mfg is PrMfgTypes.Make or PrMfgTypes.Phantom || isRoot)
            {
                childHdr = await ResolveHeaderCachedAsync(
                    db, company, itemCode, asOf, version: null, cache, cancellationToken);
                if (childHdr is null)
                {
                    return $"Cannot explode Product {pathStack[0]} because {(mfg == PrMfgTypes.Phantom ? "Phantom" : "Make")} item {itemCode} has no active BOM for the requested date.";
                }
            }
        }

        if (childHdr is null)
        {
            return null;
        }

        var lines = await GetLinesAsync(db, company, childHdr.Uid, cache, cancellationToken);
        // Defensive circular check on graph of reached headers
        var cycle = DetectPathCycle(pathStack);
        if (cycle is not null)
        {
            return $"Cannot explode BOM because it creates a circular BOM: {cycle}.";
        }

        foreach (var line in lines.OrderBy(x => x.SeqNo).ThenBy(x => x.ICode))
        {
            var childCode = Normalize(line.ICode);
            if (pathStack.Any(p => string.Equals(p, childCode, StringComparison.OrdinalIgnoreCase)))
            {
                var cyclePath = string.Join(" → ", pathStack) + " → " + childCode;
                return $"Cannot explode BOM because it creates a circular BOM: {cyclePath}.";
            }

            var childExt = PrBomCalc.RequiredQty(
                extendedQty, childHdr.BaseQty, line.StdQty, line.ScrapPercent);

            pathStack.Add(childCode);
            var err = await WalkAsync(
                db,
                company,
                asOf,
                mode,
                parentCode: itemCode,
                itemCode: childCode,
                extendedQty: childExt,
                level: level + 1,
                qtyPerParent: line.StdQty,
                scrapPercent: line.ScrapPercent,
                warehouse: line.Warehouse,
                sourceLineUid: line.Uid,
                bomHdr: null, // resolved inside for Make/Phantom
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

    private static string? DetectPathCycle(List<string> pathStack)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pathStack)
        {
            if (!seen.Add(p))
            {
                return string.Join(" → ", pathStack);
            }
        }

        return null;
    }

    private static async Task<PrBomHdr?> ResolveHeaderAsync(
        AppDbContext db,
        string company,
        string prodCode,
        DateTime asOf,
        int? version,
        CancellationToken cancellationToken)
    {
        var q = db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.ProdCode == prodCode);

        if (version is int v)
        {
            return await q.FirstOrDefaultAsync(x => x.Version == v, cancellationToken);
        }

        var actives = await q
            .Where(x => x.Status == PrBomStatuses.Active || x.Status == PrBomStatuses.Superseded)
            .ToListAsync(cancellationToken);

        return actives
            .Where(h => IsApplicable(h, asOf))
            .OrderByDescending(x => x.Status == PrBomStatuses.Active)
            .ThenByDescending(x => x.Version)
            .FirstOrDefault();
    }

    private static async Task<PrBomHdr?> ResolveHeaderCachedAsync(
        AppDbContext db,
        string company,
        string prodCode,
        DateTime asOf,
        int? version,
        BomLoadCache cache,
        CancellationToken cancellationToken)
    {
        if (version is int v && cache.Headers.TryGetValue((prodCode, v), out var cachedVer))
        {
            return cachedVer;
        }

        if (version is null
            && cache.ActiveByProd.TryGetValue(prodCode, out var cachedActive)
            && IsApplicable(cachedActive, asOf)
            && cachedActive.Status is PrBomStatuses.Active or PrBomStatuses.Superseded)
        {
            return cachedActive;
        }

        // Reachability batch: load all headers for this prod once
        if (!cache.AllHeadersLoaded.Contains(prodCode))
        {
            var list = await db.PrBomHdrs.AsNoTracking()
                .Where(x => x.CompanyCode == company && x.ProdCode == prodCode)
                .ToListAsync(cancellationToken);
            cache.AllHeadersLoaded.Add(prodCode);
            foreach (var h in list)
            {
                cache.Headers[(prodCode, h.Version)] = h;
            }

            var applicable = list
                .Where(x => x.Status is PrBomStatuses.Active or PrBomStatuses.Superseded
                            && IsApplicable(x, asOf))
                .OrderByDescending(x => x.Status == PrBomStatuses.Active)
                .ThenByDescending(x => x.Version)
                .FirstOrDefault();
            if (applicable is not null)
            {
                cache.ActiveByProd[prodCode] = applicable;
            }
        }

        if (version is int ver)
        {
            return cache.Headers.TryGetValue((prodCode, ver), out var h) ? h : null;
        }

        if (cache.ActiveByProd.TryGetValue(prodCode, out var active)
            && active.Status is PrBomStatuses.Active or PrBomStatuses.Superseded
            && IsApplicable(active, asOf))
        {
            return active;
        }

        // Re-scan headers for date window
        var match = cache.Headers.Values
            .Where(h => string.Equals(h.ProdCode, prodCode, StringComparison.OrdinalIgnoreCase)
                        && h.Status is PrBomStatuses.Active or PrBomStatuses.Superseded
                        && IsApplicable(h, asOf))
            .OrderByDescending(h => h.Status == PrBomStatuses.Active)
            .ThenByDescending(h => h.Version)
            .FirstOrDefault();
        return match;
    }

    internal static bool IsApplicable(PrBomHdr h, DateTime asOf)
    {
        if (h.EffectiveFrom is DateTime from && asOf < from.Date)
        {
            return false;
        }

        if (h.EffectiveTo is DateTime to && asOf >= to.Date)
        {
            return false;
        }

        return true;
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

        // Prefetch component items + their headers for reachability
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
        public Dictionary<(string Prod, int Ver), PrBomHdr> Headers { get; } = new();
        public Dictionary<string, PrBomHdr> ActiveByProd { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AllHeadersLoaded { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<long, List<PrDefBOM>> Lines { get; } = new();
    }

    private sealed record ItemInfo(string Code, string? Desc, string MfgType, string? StdUom);
}
