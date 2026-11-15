using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Subcon;

public interface ISubconBomReadService
{
    Task<SubconBomExplosionResult> ExplodeForSubconAsync(
        SubconBomExplosionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record SubconBomExplosionRequest
{
    public string ProdCode { get; init; } = string.Empty;
    public string? DefinitionCode { get; init; }
    public decimal Quantity { get; init; } = 1m;
    public int? Version { get; init; }
}

public sealed class SubconBomExplosionResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public SubconBomSnapshotDto? Snapshot { get; init; }

    public static SubconBomExplosionResult Ok(SubconBomSnapshotDto snapshot) =>
        new() { Succeeded = true, Snapshot = snapshot };

    public static SubconBomExplosionResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public sealed class SubconBomSnapshotDto
{
    public string ProdCode { get; init; } = string.Empty;
    public string? ProdName { get; init; }
    public string DefinitionCode { get; init; } = string.Empty;
    public int? BomVersion { get; init; }
    public long? BomHdrUid { get; init; }
    public decimal RootQuantity { get; init; }
    public string Uom { get; init; } = string.Empty;
    public IReadOnlyList<SubconBomComponentDto> Materials { get; init; } = [];
}

public sealed class SubconBomComponentDto
{
    public int LineNo { get; init; }
    public string ItemCode { get; init; } = string.Empty;
    public string? ItemName { get; init; }
    public string Uom { get; init; } = string.Empty;
    public decimal RequiredQty { get; init; }
    public decimal ScrapPercent { get; init; }
    public string? WarehouseCode { get; init; }
    public string? LocationCode { get; init; }
}

public sealed class SubconBomReadService : ISubconBomReadService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;

    public SubconBomReadService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
    }

    public async Task<SubconBomExplosionResult> ExplodeForSubconAsync(
        SubconBomExplosionRequest request,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope() ?? _tenant.TryWriteScope() ?? _tenant.TryCompanyScope();
        if (scope is null)
        {
            return SubconBomExplosionResult.Fail(SubconMessages.CompanyBranchRequired);
        }

        if (!await _access.CanAsync(MenuCodes.SubconSendOut, PermissionCodes.Access, cancellationToken)
            && !await _access.CanAsync(MenuCodes.SubconWorkbench, PermissionCodes.Access, cancellationToken))
        {
            return SubconBomExplosionResult.Fail(SubconMessages.NotAuthorized);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var bomEngine = new BomExplosionService(_dbFactory, _tenant, _access);
        var exploded = await bomEngine.ExplodeInContextAsync(
            db,
            scope.CompanyCode,
            new BomExplosionRequest
            {
                ProdCode = request.ProdCode,
                DefinitionCode = string.IsNullOrWhiteSpace(request.DefinitionCode)
                    ? "STANDARD"
                    : request.DefinitionCode.Trim(),
                Quantity = request.Quantity,
                Version = request.Version,
                Mode = BomExplosionMode.ProductionIssueRequirement
            },
            cancellationToken);

        if (!exploded.Succeeded || exploded.Data is null)
        {
            return SubconBomExplosionResult.Fail(exploded.Message ?? "Unable to explode BOM.");
        }

        var data = exploded.Data;
        var allCodes = data.Nodes
            .Select(n => n.ItemCode)
            .Append(data.RootProdCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var masters = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && allCodes.Contains(x.ICode))
            .ToDictionaryAsync(x => x.ICode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        masters.TryGetValue(data.RootProdCode, out var rootMaster);

        var lineNo = 0;
        var materials = data.Nodes
            .Where(n => n.Level == 1 || n.IsIssueLine)
            .Select(n =>
            {
                lineNo++;
                masters.TryGetValue(n.ItemCode, out var m);
                return new SubconBomComponentDto
                {
                    LineNo = lineNo,
                    ItemCode = n.ItemCode,
                    ItemName = n.ItemDesc ?? m?.IDesc,
                    Uom = !string.IsNullOrWhiteSpace(n.Uom) ? n.Uom! : (m?.StdUom ?? "PCS"),
                    RequiredQty = decimal.Round(n.ExtendedQty, 4, MidpointRounding.AwayFromZero),
                    ScrapPercent = n.ScrapPercent,
                    WarehouseCode = !string.IsNullOrWhiteSpace(n.Warehouse) ? n.Warehouse : m?.DefWarehouse,
                    LocationCode = m?.DefLocation
                };
            })
            .ToArray();

        return SubconBomExplosionResult.Ok(new SubconBomSnapshotDto
        {
            ProdCode = data.RootProdCode,
            ProdName = rootMaster?.IDesc,
            DefinitionCode = data.RootDefinitionCode,
            BomVersion = data.RootBomVersion,
            BomHdrUid = data.RootBomHdrId,
            RootQuantity = data.RootQuantity,
            Uom = data.RootBaseUom ?? rootMaster?.StdUom ?? "PCS",
            Materials = materials
        });
    }
}
