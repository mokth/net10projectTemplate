using System.Security.Cryptography;
using System.Text;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Costing;

public sealed class CostingRepairPlanner : ICostingRepairPlanner
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;
    private readonly ICostingRepairOwnershipResolver _ownership;
    private readonly IEnumerable<ICostingRepairAdapter> _adapters;
    private readonly CostingStateRepairPlanner? _statePlanner;

    public CostingRepairPlanner(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        ICostingRepairOwnershipResolver ownership,
        IEnumerable<ICostingRepairAdapter> adapters,
        CostingStateRepairPlanner? statePlanner = null)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _ownership = ownership;
        _adapters = adapters;
        _statePlanner = statePlanner;
    }

    public Task<CostingRepairPlan> PlanAsync(CostingRepairTarget target, CancellationToken cancellationToken = default)
    {
        if (target.Kind == CostingRepairTargetKind.CostState)
            return (_statePlanner ?? throw new InvalidOperationException("Cost-state repair planning is not registered."))
                .PlanAsync(target.ItemCode ?? string.Empty, target.CostMethod ?? string.Empty, target.FindingCode, cancellationToken);
        if (target.Kind == CostingRepairTargetKind.Posting && target.StockPostingId is long postingId)
            return PlanAsync(postingId, cancellationToken);
        return Task.FromResult(Blocked(target.Kind == CostingRepairTargetKind.DiagnosticOnly
            ? "This finding is diagnostic only. Open the source document and reconcile it. The Costing Center will not roll it back."
            : "A stock posting is required for this repair."));
    }

    public async Task<CostingRepairPlan> PlanAsync(long stockPostingId, CancellationToken cancellationToken = default)
    {
        if (!await _access.CanAsync(MenuCodes.InventoryCostingCenter, PermissionCodes.Access, cancellationToken))
            return Blocked("Not authorized.");
        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
            return Blocked("A trusted company and branch are required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var posting = await db.StockPostings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == stockPostingId
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode, cancellationToken);
        if (posting is null)
            return Blocked("The stock posting was not found in this branch.");
        if (posting.SealedAtUtc is null)
            return Blocked("Unsealed postings require ledger reconciliation, not document rollback.");
        if (!string.Equals(posting.PostingRole, "PRIMARY", StringComparison.OrdinalIgnoreCase))
            return Blocked("Only a PRIMARY posting can be reversed from the Costing Repair Center.");
        var alreadyReversed = await db.StockPostings.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode
            && x.ReversesPostingId == posting.Id
            && x.SealedAtUtc != null, cancellationToken);
        if (alreadyReversed)
            return Blocked("This posting already has a sealed reversal.");

        var node = new CostingRepairNode(posting.Id, posting.SourceDocumentType, posting.SourceDocumentId, posting.SourceDocumentNo);
        var ownership = await _ownership.ResolveAsync(node, cancellationToken);
        if (!ownership.IsProven || ownership.Owner is null)
            return Blocked(ownership.BlockingReason ?? "The repair owner cannot be proven.");

        var matches = _adapters.Where(x => x.OwnerTypes.Contains(ownership.Owner.OwnerType)).ToArray();
        if (matches.Length == 0)
            return Blocked("No registered adapter owns this repair. Manual module workflow is required.");
        if (matches.Length > 1)
            return Blocked("More than one adapter claims this repair owner.");

        var adapter = matches[0];
        var capability = await adapter.CanHandleAsync(node, ownership.Owner, cancellationToken);
        if (!capability.CanReverse)
            return Blocked(capability.BlockingReason ?? "The adapter cannot reverse this document.");

        var closed = await db.IvPeriodCloseHdrs.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode
            && x.Status == IvPeriodCloseStatuses.Closed
            && posting.BusinessDate >= x.PeriodFrom
            && posting.BusinessDate <= x.PeriodTo, cancellationToken);
        if (closed)
            return Blocked("The posting falls in a closed period. Reopen later periods first, then this period, and rebuild the preview.");

        var step = new CostingRepairStep(
            $"step-{posting.Id}",
            ownership.Owner.PhysicalSourceDocumentType,
            ownership.Owner.PhysicalSourceDocumentId,
            ownership.Owner.PhysicalSourceDocumentNo,
            ownership.Owner.OwnerType,
            ownership.Owner.OwnerDocumentNo,
            adapter.GetType().Name,
            "REVERSE");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{posting.Id}|{posting.PostingSequence}|{ownership.Owner.OwnerType}|{ownership.Owner.OwnerDocumentNo}")));
        return new CostingRepairPlan(
            true,
            "GUIDED_REVERSAL",
            null,
            [step],
            [],
            [ownership.Owner.OwnerDocumentNo],
            posting.EffectiveAt,
            posting.EffectiveAt,
            1,
            0,
            [],
            hash);
    }

    private static CostingRepairPlan Blocked(string reason) =>
        new(false, "PREVIEW_ONLY", reason, [], [], [], null, null, 0, 0, [], string.Empty);
}
