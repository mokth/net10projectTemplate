using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed class ProcurementApprovalService : IProcurementApprovalService
{
    private static readonly HashSet<string> ActiveGateStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        ProcurementApprovalStatuses.Pending,
        ProcurementApprovalStatuses.Approved,
        ProcurementApprovalStatuses.NotRequired
    };

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    public ProcurementApprovalService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
    }

    public async Task<ProcurementApprovalResult> GetPolicyAsync(string docType, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        if (!await CanSettingsAsync(PermissionCodes.Access, cancellationToken))
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        var type = NormalizeDocType(docType);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var policy = await LoadPublishedPolicyAsync(db, scope.CompanyCode, type, cancellationToken);
        if (policy is null)
        {
            return new ProcurementApprovalResult
            {
                Succeeded = true,
                Policy = new ProcurementApprovalPolicyDto { DocType = type, Enabled = false }
            };
        }

        return new ProcurementApprovalResult { Succeeded = true, Policy = MapPolicy(policy) };
    }

    public async Task<ProcurementApprovalResult> GetLookupsAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        if (!await CanSettingsAsync(PermissionCodes.Access, cancellationToken)
            && !await CanInboxAsync(cancellationToken))
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        const string deletedMarker = "deleted";
        var users = await db.UserLogins
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.active == true
                && (x.UpdatedUID == null || x.UpdatedUID != deletedMarker))
            .OrderBy(x => x.id)
            .Select(x => new ProcurementApprovalLookupRow
            {
                Code = x.id,
                Name = x.name
            })
            .ToListAsync(cancellationToken);

        var roles = await db.Roles
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.IsActive)
            .OrderBy(x => x.RoleCode)
            .Select(x => new ProcurementApprovalLookupRow
            {
                Code = x.RoleCode,
                Name = x.RoleName
            })
            .ToListAsync(cancellationToken);

        return new ProcurementApprovalResult
        {
            Succeeded = true,
            Users = users,
            Roles = roles
        };
    }

    public async Task<ProcurementApprovalResult> SavePolicyAsync(
        ProcurementApprovalPolicyDto policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        if (!await CanSettingsAsync(PermissionCodes.Edit, cancellationToken))
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        var type = NormalizeDocType(policy.DocType);
        var validation = ValidatePolicy(policy);
        if (validation is not null)
        {
            return ProcurementApprovalResult.Fail(validation);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var current = await LoadPublishedPolicyAsync(db, scope.CompanyCode, type, cancellationToken);
        var now = DateTime.UtcNow;
        var uid = Truncate(scope.UserId, 20);
        if (current is not null)
        {
            current.IsPublished = false;
            current.Enabled = false;
            current.ModifiedDate = now;
            current.ModifiedBy = uid;
            Touch(db, current);
        }

        var nextVersion = (current?.Version ?? 0) + 1;
        var entity = new ProcurementApprovalPolicy
        {
            CompanyCode = scope.CompanyCode,
            DocType = type,
            Version = nextVersion,
            Enabled = policy.Enabled,
            IsPublished = true,
            LegacyDraftMode = string.IsNullOrWhiteSpace(policy.LegacyDraftMode)
                ? ProcurementApprovalLegacyModes.RequireGoingForward
                : policy.LegacyDraftMode.Trim(),
            CutoverAtUtc = policy.Enabled
                ? (current is { Enabled: true, CutoverAtUtc: not null } ? current.CutoverAtUtc : now)
                : current?.CutoverAtUtc,
            EffectiveAtUtc = now,
            CreatedDate = now,
            CreatedBy = uid
        };
        foreach (var band in policy.Bands.OrderBy(x => x.Currency, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.MinInclusive))
        {
            var bandEntity = new ProcurementApprovalBand
            {
                Currency = band.Currency.Trim().ToUpperInvariant(),
                MinInclusive = PoPrCalc.RoundMoney(band.MinInclusive),
                MaxExclusive = band.MaxExclusive is null ? null : PoPrCalc.RoundMoney(band.MaxExclusive.Value),
                NotRequired = band.NotRequired,
                Sequence = band.NotRequired ? 0 : 1
            };
            var seq = 1;
            foreach (var level in band.Levels.OrderBy(x => x.Sequence))
            {
                var levelEntity = new ProcurementApprovalLevel
                {
                    Sequence = seq++,
                    MatchMode = level.MatchMode.Trim().ToUpperInvariant()
                };
                foreach (var assignee in level.Assignees)
                {
                    levelEntity.Assignees.Add(new ProcurementApprovalLevelAssignee
                    {
                        Kind = assignee.Kind.Trim().ToUpperInvariant(),
                        RefCode = assignee.RefCode.Trim()
                    });
                }

                bandEntity.Levels.Add(levelEntity);
            }

            entity.Bands.Add(bandEntity);
        }

        Touch(db, entity);
        db.ProcurementApprovalPolicies.Add(entity);
        db.ProcurementApprovalEvents.Add(new ProcurementApprovalEvent
        {
            CompanyCode = scope.CompanyCode,
            BranchCode = scope.BranchCode ?? string.Empty,
            DocType = type,
            DocNo = "*",
            EventType = policy.Enabled
                ? ProcurementApprovalEventTypes.PolicyEnabled
                : ProcurementApprovalEventTypes.PolicyDisabled,
            PolicyVersion = nextVersion,
            ActorUserId = uid,
            OccurredAtUtc = now,
            Reason = policy.Enabled
                ? entity.LegacyDraftMode
                : "Policy disabled; in-flight cycles remain locked."
        });
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new ProcurementApprovalResult { Succeeded = true, Policy = MapPolicy(entity) };
    }

    public async Task<ProcurementApprovalResult> SubmitAsync(
        ProcurementApprovalSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = _tenant.TryWriteScope();
        if (scope is null)
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        var type = NormalizeDocType(request.DocType);
        var menu = MenuFor(type);
        if (!await _accessRights.CanAsync(menu, PermissionCodes.Submit, cancellationToken))
        {
            return ProcurementApprovalResult.Fail("Not authorized to submit this document.");
        }

        var docNo = (request.DocNo ?? string.Empty).Trim();
        if (docNo.Length == 0)
        {
            return ProcurementApprovalResult.Fail("Document number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (type == ProcurementApprovalDocTypes.Pr)
        {
            var header = await LockPrAsync(db, scope.CompanyCode, scope.BranchCode!, docNo, cancellationToken);
            if (header is null)
            {
                return ProcurementApprovalResult.Fail("Purchase Requisition was not found.");
            }

            await db.Entry(header).Collection(x => x.Details).LoadAsync(cancellationToken);
            var result = await SubmitLockedAsync(
                db, scope, type, docNo, 0, header.CreatedBy, header.CreatedDate,
                header.Status, RecvOrConsumed: IsPrConsumed(header),
                ProcurementApprovalFingerprint.ForPr(header),
                ProcurementApprovalFingerprint.TotalsForPr(header),
                headerDiscount: 0m,
                request.IdempotencyKey,
                cancellationToken);
            if (!result.Succeeded)
            {
                await tx.RollbackAsync(cancellationToken);
                return result;
            }

            return await CommitSubmitAsync(db, tx, scope, type, docNo, 0, cancellationToken);
        }
        else
        {
            var header = await LockPoAsync(db, scope.CompanyCode, scope.BranchCode!, docNo, request.DocRev, cancellationToken);
            if (header is null)
            {
                return ProcurementApprovalResult.Fail("Purchase Order was not found.");
            }

            await db.Entry(header).Collection(x => x.Details).LoadAsync(cancellationToken);
            var result = await SubmitLockedAsync(
                db, scope, type, docNo, header.PoRelNo, header.CreatedBy, header.CreatedDate,
                header.Status, RecvOrConsumed: header.Details.Any(x => x.RecvQty > 0m),
                ProcurementApprovalFingerprint.ForPo(header),
                ProcurementApprovalFingerprint.TotalsForPo(header),
                header.Discount,
                request.IdempotencyKey,
                cancellationToken);
            if (!result.Succeeded)
            {
                await tx.RollbackAsync(cancellationToken);
                return result;
            }

            return await CommitSubmitAsync(db, tx, scope, type, docNo, header.PoRelNo, cancellationToken);
        }
    }

    public async Task<ProcurementApprovalResult> DecideAsync(
        ProcurementApprovalDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        var decision = (request.Decision ?? string.Empty).Trim().ToUpperInvariant();
        if (decision is not (ProcurementApprovalDecisions.Approve
            or ProcurementApprovalDecisions.Reject
            or ProcurementApprovalDecisions.Return))
        {
            return ProcurementApprovalResult.Fail("Unknown approval decision.");
        }

        if (decision is ProcurementApprovalDecisions.Reject or ProcurementApprovalDecisions.Return
            && string.IsNullOrWhiteSpace(request.Reason))
        {
            return ProcurementApprovalResult.Fail("A reason is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var instance = await LockInstanceByIdAsync(db, request.InstanceId, cancellationToken);
        if (instance is null
            || !string.Equals(instance.CompanyCode, scope.CompanyCode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(instance.BranchCode, scope.BranchCode, StringComparison.OrdinalIgnoreCase))
        {
            return ProcurementApprovalResult.Fail("Approval request was not found.");
        }

        if (request.RowVersion is { Length: > 0 }
            && instance.RowVersion is { Length: > 0 }
            && !instance.RowVersion.SequenceEqual(request.RowVersion))
        {
            return ProcurementApprovalResult.Fail("This approval was changed by another user. Reload and try again.");
        }

        if (!string.Equals(instance.Status, ProcurementApprovalStatuses.Pending, StringComparison.OrdinalIgnoreCase))
        {
            return ProcurementApprovalResult.Fail("This approval is no longer pending.");
        }

        var actor = Truncate(scope.UserId, 20);
        if (string.Equals(instance.SubmittedBy, actor, StringComparison.OrdinalIgnoreCase)
            || await IsDocumentCreatorAsync(db, instance, actor, cancellationToken))
        {
            return ProcurementApprovalResult.Fail("You cannot approve a document you created or submitted.");
        }

        await db.Entry(instance).Collection(x => x.Assignees).LoadAsync(cancellationToken);
        await db.Entry(instance).Collection(x => x.Decisions).LoadAsync(cancellationToken);
        var levelAssignees = instance.Assignees.Where(x => x.LevelSequence == instance.CurrentLevel).ToList();
        if (!levelAssignees.Any(x => string.Equals(x.UserId, actor, StringComparison.OrdinalIgnoreCase)))
        {
            return ProcurementApprovalResult.Fail("You are not an assigned approver for the current level.");
        }

        if (!await UserIsActiveAsync(db, scope.CompanyCode, actor, cancellationToken))
        {
            return ProcurementApprovalResult.Fail("Your account is not active.");
        }

        var menu = MenuFor(instance.DocType);
        var needed = decision == ProcurementApprovalDecisions.Approve ? PermissionCodes.Approve : PermissionCodes.Reject;
        if (decision == ProcurementApprovalDecisions.Return)
        {
            needed = PermissionCodes.Reject;
        }

        if (!await _accessRights.CanAsync(menu, needed, cancellationToken)
            && !await _accessRights.CanAsync(menu, PermissionCodes.Approve, cancellationToken))
        {
            return ProcurementApprovalResult.Fail("Not authorized to decide this document.");
        }

        if (instance.Decisions.Any(x =>
                x.LevelSequence == instance.CurrentLevel
                && string.Equals(x.UserId, actor, StringComparison.OrdinalIgnoreCase)))
        {
            return new ProcurementApprovalResult { Succeeded = true, Instance = MapInstance(instance) };
        }

        if (decision == ProcurementApprovalDecisions.Return)
        {
            var consumed = await IsDocumentConsumedAsync(db, instance, cancellationToken);
            if (consumed)
            {
                return ProcurementApprovalResult.Fail("This document has downstream consumption and cannot be returned for editing.");
            }
        }

        var now = DateTime.UtcNow;
        instance.Decisions.Add(new ProcurementApprovalDecision
        {
            LevelSequence = instance.CurrentLevel,
            UserId = actor,
            Decision = decision,
            Reason = Truncate(request.Reason, 500),
            DecidedAtUtc = now
        });

        var prior = instance.Status;
        if (decision == ProcurementApprovalDecisions.Reject)
        {
            instance.Status = ProcurementApprovalStatuses.Rejected;
            AddEvent(instance, actor, ProcurementApprovalEventTypes.Rejected, prior, instance.Status, request.Reason, request.IdempotencyKey, now);
        }
        else if (decision == ProcurementApprovalDecisions.Return)
        {
            instance.Status = ProcurementApprovalStatuses.Returned;
            AddEvent(instance, actor, ProcurementApprovalEventTypes.Returned, prior, instance.Status, request.Reason, request.IdempotencyKey, now);
        }
        else
        {
            var mode = levelAssignees.First().MatchMode;
            var approvedUsers = instance.Decisions
                .Where(x => x.LevelSequence == instance.CurrentLevel
                    && x.Decision == ProcurementApprovalDecisions.Approve)
                .Select(x => x.UserId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var levelComplete = string.Equals(mode, ProcurementApprovalMatchModes.Any, StringComparison.OrdinalIgnoreCase)
                || levelAssignees.All(x => approvedUsers.Contains(x.UserId));
            if (levelComplete)
            {
                var maxLevel = instance.Assignees.Max(x => x.LevelSequence);
                if (instance.CurrentLevel >= maxLevel)
                {
                    instance.Status = ProcurementApprovalStatuses.Approved;
                    AddEvent(instance, actor, ProcurementApprovalEventTypes.Approved, prior, instance.Status, request.Reason, request.IdempotencyKey, now);
                }
                else
                {
                    instance.CurrentLevel += 1;
                    AddEvent(instance, actor, ProcurementApprovalEventTypes.LevelAdvanced, prior, instance.Status, request.Reason, request.IdempotencyKey, now);
                }
            }
        }

        Touch(db, instance);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new ProcurementApprovalResult { Succeeded = true, Instance = MapInstance(instance) };
    }

    public async Task<ProcurementApprovalResult> WithdrawAsync(
        ProcurementApprovalDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var instance = await LockInstanceByIdAsync(db, request.InstanceId, cancellationToken);
        if (instance is null || instance.CompanyCode != scope.CompanyCode || instance.BranchCode != scope.BranchCode)
        {
            return ProcurementApprovalResult.Fail("Approval request was not found.");
        }

        var actor = Truncate(scope.UserId, 20);
        if (!string.Equals(instance.SubmittedBy, actor, StringComparison.OrdinalIgnoreCase))
        {
            return ProcurementApprovalResult.Fail("Only the submitter can withdraw this request.");
        }

        if (!string.Equals(instance.Status, ProcurementApprovalStatuses.Pending, StringComparison.OrdinalIgnoreCase))
        {
            return ProcurementApprovalResult.Fail("Only a pending request can be withdrawn.");
        }

        if (await IsDocumentConsumedAsync(db, instance, cancellationToken))
        {
            return ProcurementApprovalResult.Fail("This document has downstream consumption and cannot be withdrawn.");
        }

        var prior = instance.Status;
        instance.Status = ProcurementApprovalStatuses.Withdrawn;
        AddEvent(instance, actor, ProcurementApprovalEventTypes.Withdrawn, prior, instance.Status, request.Reason, request.IdempotencyKey, DateTime.UtcNow);
        Touch(db, instance);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new ProcurementApprovalResult { Succeeded = true, Instance = MapInstance(instance) };
    }

    public async Task<ProcurementApprovalResult> ReassignAsync(
        ProcurementApprovalReassignRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return ProcurementApprovalResult.Fail("A reason is required for reassignment.");
        }

        if (!await CanSettingsAsync(PermissionCodes.Edit, cancellationToken))
        {
            return ProcurementApprovalResult.Fail("Not authorized to reassign approvers.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var instance = await LockInstanceByIdAsync(db, request.InstanceId, cancellationToken);
        if (instance is null || instance.CompanyCode != scope.CompanyCode)
        {
            return ProcurementApprovalResult.Fail("Approval request was not found.");
        }

        if (!string.Equals(instance.Status, ProcurementApprovalStatuses.Pending, StringComparison.OrdinalIgnoreCase))
        {
            return ProcurementApprovalResult.Fail("Only a pending request can be reassigned.");
        }

        await db.Entry(instance).Collection(x => x.Assignees).LoadAsync(cancellationToken);
        var addId = request.AddUserId.Trim();
        var removeId = request.RemoveUserId.Trim();
        if (!await UserIsActiveAsync(db, scope.CompanyCode, addId, cancellationToken))
        {
            return ProcurementApprovalResult.Fail("The replacement approver is not an active user.");
        }

        var existing = instance.Assignees.FirstOrDefault(x =>
            x.LevelSequence == request.LevelSequence
            && string.Equals(x.UserId, removeId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return ProcurementApprovalResult.Fail("The current assignee was not found on this level.");
        }

        existing.UserId = Truncate(addId, 20);
        AddEvent(instance, Truncate(scope.UserId, 20), ProcurementApprovalEventTypes.Reassigned,
            instance.Status, instance.Status,
            $"{removeId} -> {addId}. {request.Reason}", null, DateTime.UtcNow);
        Touch(db, instance);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new ProcurementApprovalResult { Succeeded = true, Instance = MapInstance(instance) };
    }

    public async Task<ProcurementApprovalResult> GetInboxAsync(
        bool allPending, bool completed, bool submittedByMe = false, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseApprovalInbox, PermissionCodes.Access, cancellationToken))
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        if (allPending
            && !await _accessRights.CanAsync(MenuCodes.PurchaseApprovalAll, PermissionCodes.Access, cancellationToken))
        {
            return ProcurementApprovalResult.Fail("Not authorized to view all pending approvals.");
        }

        var actor = Truncate(scope.UserId, 20);
        var canViewCost = await _accessRights.CanAsync(MenuCodes.PurchaseRequisition, PermissionCodes.ViewCost, cancellationToken)
            || await _accessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.ViewCost, cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.ProcurementApprovalInstances.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);
        if (completed)
        {
            q = q.Where(x => x.Status != ProcurementApprovalStatuses.Pending);
        }
        else
        {
            q = q.Where(x => x.Status == ProcurementApprovalStatuses.Pending);
            if (submittedByMe)
            {
                q = q.Where(x => x.SubmittedBy != null && x.SubmittedBy == actor);
            }
            else if (!allPending)
            {
                var mineInstanceIds = await (
                    from assignee in db.ProcurementApprovalAssignees.AsNoTracking()
                    join instance in db.ProcurementApprovalInstances.AsNoTracking() on assignee.InstanceId equals instance.Uid
                    where instance.CompanyCode == scope.CompanyCode
                        && instance.BranchCode == scope.BranchCode
                        && instance.Status == ProcurementApprovalStatuses.Pending
                        && assignee.LevelSequence == instance.CurrentLevel
                        && assignee.UserId == actor
                    select instance.Uid).Distinct().ToListAsync(cancellationToken);
                q = q.Where(x => mineInstanceIds.Contains(x.Uid));
            }
        }

        var rows = await q.OrderBy(x => x.SubmittedAtUtc).Take(200).ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        return new ProcurementApprovalResult
        {
            Succeeded = true,
            Inbox = rows.Select(x => new ProcurementApprovalInboxRow
            {
                InstanceId = x.Uid,
                DocType = x.DocType,
                DocNo = x.DocNo,
                DocRev = x.DocRev,
                Status = x.Status,
                SubmittedBy = x.SubmittedBy,
                SubmittedAtUtc = x.SubmittedAtUtc,
                CurrentLevel = x.CurrentLevel,
                Currency = x.Currency,
                Total = canViewCost ? x.Total : null,
                AmountHidden = !canViewCost,
                AgeDays = CalendarAgeDays(now, x.SubmittedAtUtc)
            }).ToList()
        };
    }

    public async Task<ProcurementApprovalResult> GetHistoryAsync(
        string docType, string docNo, short docRev, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var type = NormalizeDocType(docType);
        var no = (docNo ?? string.Empty).Trim();
        var rows = await db.ProcurementApprovalEvents.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.DocType == type
                && x.DocNo == no
                && x.DocRev == docRev)
            .OrderBy(x => x.OccurredAtUtc)
            .ThenBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        return new ProcurementApprovalResult
        {
            Succeeded = true,
            History = rows.Select(MapEvent).ToList()
        };
    }

    public async Task<ProcurementApprovalResult> GetPreviewAsync(
        string docType, string docNo, short docRev, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        var actor = Truncate(scope.UserId, 20);
        var type = NormalizeDocType(docType);
        var no = (docNo ?? string.Empty).Trim();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var instance = await db.ProcurementApprovalInstances.AsNoTracking()
            .Include(x => x.Assignees)
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.DocType == type
                && x.DocNo == no
                && x.DocRev == docRev)
            .OrderByDescending(x => x.Cycle)
            .FirstOrDefaultAsync(cancellationToken);
        var isAssignee = instance?.Assignees.Any(a => string.Equals(a.UserId, actor, StringComparison.OrdinalIgnoreCase)) == true;
        var canAll = await _accessRights.CanAsync(MenuCodes.PurchaseApprovalAll, PermissionCodes.Access, cancellationToken);
        var canInbox = await _accessRights.CanAsync(MenuCodes.PurchaseApprovalInbox, PermissionCodes.Access, cancellationToken);
        if (!isAssignee && !canAll && !canInbox)
        {
            return ProcurementApprovalResult.Fail("Not authorized to preview this approval.");
        }

        var canViewCost = await _accessRights.CanAsync(MenuFor(type), PermissionCodes.ViewCost, cancellationToken);
        string documentStatus = string.Empty;
        string? vendorOrRequester = null;
        string currency = instance?.Currency ?? string.Empty;
        decimal total = instance?.Total ?? 0m;
        if (type == ProcurementApprovalDocTypes.Pr)
        {
            var header = await db.PoPrs.AsNoTracking().Include(x => x.Details)
                .FirstOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.PrNo == no, cancellationToken);
            if (header is null)
            {
                return ProcurementApprovalResult.Fail("Purchase Requisition was not found.");
            }

            documentStatus = header.Status;
            vendorOrRequester = header.Requester;
            var t = ProcurementApprovalFingerprint.TotalsForPr(header);
            currency = t.Currency;
            total = t.Total;
        }
        else
        {
            var header = await db.PoOrders.AsNoTracking().Include(x => x.Details)
                .FirstOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.PoNo == no && x.PoRelNo == docRev, cancellationToken);
            if (header is null)
            {
                return ProcurementApprovalResult.Fail("Purchase Order was not found.");
            }

            documentStatus = header.Status;
            vendorOrRequester = header.VendName ?? header.VendCode;
            var t = ProcurementApprovalFingerprint.TotalsForPo(header);
            currency = t.Currency;
            total = t.Total;
        }

        var history = await db.ProcurementApprovalEvents.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.DocType == type && x.DocNo == no && x.DocRev == docRev)
            .OrderBy(x => x.OccurredAtUtc)
            .ToListAsync(cancellationToken);

        return new ProcurementApprovalResult
        {
            Succeeded = true,
            Preview = new ProcurementApprovalPreview
            {
                DocType = type,
                DocNo = no,
                DocRev = docRev,
                DocumentStatus = documentStatus,
                VendorOrRequester = vendorOrRequester,
                Currency = currency,
                Total = canViewCost ? total : null,
                AmountHidden = !canViewCost,
                Instance = instance is null ? null : MapInstance(instance),
                History = history.Select(MapEvent).ToList()
            }
        };
    }

    public async Task<ProcurementApprovalResult> GetKpiAsync(int overdueDays, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseApprovalInbox, PermissionCodes.Access, cancellationToken))
        {
            return ProcurementApprovalResult.Fail("Not authorized.");
        }

        var today = DateTime.UtcNow.Date;
        var minAge = Math.Max(1, overdueDays);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var pending = db.ProcurementApprovalInstances.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.Status == ProcurementApprovalStatuses.Pending);
        var completed = db.ProcurementApprovalInstances.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                && (x.Status == ProcurementApprovalStatuses.Approved || x.Status == ProcurementApprovalStatuses.Rejected)
                && x.SubmittedAtUtc != null);
        var approved = await completed.CountAsync(x => x.Status == ProcurementApprovalStatuses.Approved, cancellationToken);
        var rejected = await completed.CountAsync(x => x.Status == ProcurementApprovalStatuses.Rejected, cancellationToken);
        var durations = await completed
            .Select(x => new
            {
                x.SubmittedAtUtc,
                DoneAt = x.Events
                    .Where(e => e.EventType == ProcurementApprovalEventTypes.Approved
                        || e.EventType == ProcurementApprovalEventTypes.Rejected)
                    .Select(e => (DateTime?)e.OccurredAtUtc)
                    .Max()
            })
            .ToListAsync(cancellationToken);
        var hours = durations
            .Where(x => x.SubmittedAtUtc is not null && x.DoneAt is not null)
            .Select(x => (x.DoneAt!.Value - x.SubmittedAtUtc!.Value).TotalHours)
            .ToList();
        return new ProcurementApprovalResult
        {
            Succeeded = true,
            Kpi = new ProcurementApprovalKpiSummary
            {
                PendingPr = await pending.CountAsync(x => x.DocType == ProcurementApprovalDocTypes.Pr, cancellationToken),
                PendingPo = await pending.CountAsync(x => x.DocType == ProcurementApprovalDocTypes.Po, cancellationToken),
                Overdue = await pending.CountAsync(
                    x => x.SubmittedAtUtc != null && x.SubmittedAtUtc < today.AddDays(-minAge + 1),
                    cancellationToken),
                Approved = approved,
                Rejected = rejected,
                AverageHours = hours.Count == 0 ? null : hours.Average()
            }
        };
    }

    public async Task<ProcurementApprovalInstanceDto?> GetActiveAsync(
        string docType, string docNo, short docRev, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return null;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var instance = await FindActiveAsync(db, scope.CompanyCode, scope.BranchCode!, NormalizeDocType(docType), docNo.Trim(), docRev, cancellationToken);
        return instance is null ? null : MapInstance(instance);
    }

    public async Task<string?> AssertPrConvertibleAsync(
        AppDbContext db, string companyCode, string branchCode, string prNo, CancellationToken cancellationToken = default)
    {
        return await AssertGateAsync(db, companyCode, branchCode, ProcurementApprovalDocTypes.Pr, prNo, 0,
            progressed: false, cancellationToken);
    }

    public async Task<string?> AssertPoReceivableAsync(
        AppDbContext db, string companyCode, string branchCode, string poNo, short poRelNo, CancellationToken cancellationToken = default)
    {
        return await AssertGateAsync(db, companyCode, branchCode, ProcurementApprovalDocTypes.Po, poNo, poRelNo,
            progressed: false, cancellationToken);
    }

    public Task<string?> AssertPoCloseableAsync(
        AppDbContext db, string companyCode, string branchCode, string poNo, short poRelNo, CancellationToken cancellationToken = default) =>
        AssertPoReceivableAsync(db, companyCode, branchCode, poNo, poRelNo, cancellationToken);

    public async Task<string?> AssertDocumentMaterialEditAsync(
        AppDbContext db, string companyCode, string branchCode, string docType, string docNo, short docRev,
        string newFingerprint, CancellationToken cancellationToken = default)
    {
        var instance = await FindActiveAsync(db, companyCode, branchCode, docType, docNo, docRev, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        if (string.Equals(instance.Status, ProcurementApprovalStatuses.Pending, StringComparison.OrdinalIgnoreCase)
            || string.Equals(instance.Status, ProcurementApprovalStatuses.Approved, StringComparison.OrdinalIgnoreCase)
            || string.Equals(instance.Status, ProcurementApprovalStatuses.NotRequired, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(instance.MaterialFingerprint, newFingerprint, StringComparison.Ordinal))
            {
                return "This document is waiting for approval or already approved. Return or withdraw it before changing quantities, prices, vendor, tax, or lines.";
            }
        }

        return null;
    }

    public async Task<string?> AssertAttachmentMutationAsync(
        AppDbContext db, string companyCode, string branchCode, string docType, string docNo, short docRev,
        CancellationToken cancellationToken = default)
    {
        var instance = await FindActiveAsync(db, companyCode, branchCode, docType, docNo, docRev, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        if (string.Equals(instance.Status, ProcurementApprovalStatuses.Pending, StringComparison.OrdinalIgnoreCase)
            || string.Equals(instance.Status, ProcurementApprovalStatuses.Approved, StringComparison.OrdinalIgnoreCase))
        {
            return "Attachments cannot be changed while this document is pending or approved.";
        }

        return null;
    }

    public async Task<string?> ClosePendingOnDocumentCancelAsync(
        AppDbContext db, string companyCode, string branchCode, string docType, string docNo, short docRev,
        string actorUserId, CancellationToken cancellationToken = default)
    {
        var instance = await FindActiveAsync(db, companyCode, branchCode, docType, docNo, docRev, cancellationToken);
        if (instance is null || !string.Equals(instance.Status, ProcurementApprovalStatuses.Pending, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var prior = instance.Status;
        instance.Status = ProcurementApprovalStatuses.Cancelled;
        AddEvent(instance, Truncate(actorUserId, 20), ProcurementApprovalEventTypes.Cancelled, prior, instance.Status,
            "Document cancelled or deleted.", null, DateTime.UtcNow);
        Touch(db, instance);
        return null;
    }

    public async Task LockInstancesAsync(
        AppDbContext db, string companyCode, string branchCode, string docType,
        IEnumerable<(string DocNo, short DocRev)> keys, CancellationToken cancellationToken = default)
    {
        var ordered = keys
            .Select(k => (DocNo: k.DocNo.Trim(), k.DocRev))
            .Where(k => k.DocNo.Length > 0)
            .Distinct()
            .OrderBy(k => k.DocNo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(k => k.DocRev)
            .ToList();
        foreach (var key in ordered)
        {
            await LockActiveInstanceAsync(db, companyCode, branchCode, docType, key.DocNo, key.DocRev, cancellationToken);
        }
    }

    public async Task<bool> IsPrVisibleForPoAsync(
        AppDbContext db, string companyCode, string branchCode, string prNo, CancellationToken cancellationToken = default) =>
        await AssertPrConvertibleAsync(db, companyCode, branchCode, prNo, cancellationToken) is null;

    public async Task<bool> IsPoVisibleForGrAsync(
        AppDbContext db, string companyCode, string branchCode, string poNo, short poRelNo, CancellationToken cancellationToken = default) =>
        await AssertPoReceivableAsync(db, companyCode, branchCode, poNo, poRelNo, cancellationToken) is null;

    public async Task<bool> CanReadDocumentAsync(
        AppDbContext db, string companyCode, string branchCode, string docType, string docNo, short docRev,
        string userId, CancellationToken cancellationToken = default)
    {
        var actor = Truncate(userId, 20);
        var instance = await FindActiveAsync(db, companyCode, branchCode, docType, docNo, docRev, cancellationToken);
        if (instance?.Assignees.Any(a => string.Equals(a.UserId, actor, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return true;
        }

        return await _accessRights.CanAsync(MenuCodes.PurchaseApprovalInbox, PermissionCodes.Access, cancellationToken)
            || await _accessRights.CanAsync(MenuCodes.PurchaseApprovalAll, PermissionCodes.Access, cancellationToken);
    }

    private async Task<ProcurementApprovalResult> SubmitLockedAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        string docType,
        string docNo,
        short docRev,
        string? createdBy,
        DateTime? createdDate,
        string documentStatus,
        bool RecvOrConsumed,
        string fingerprint,
        (decimal Gross, decimal Taxes, decimal Total, string Currency) totals,
        decimal headerDiscount,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (headerDiscount != 0m)
        {
            return ProcurementApprovalResult.Fail(
                "This purchase order has a header discount that is not included in the document total. Clear the header discount before submitting for approval.");
        }

        if (string.IsNullOrWhiteSpace(totals.Currency))
        {
            return ProcurementApprovalResult.Fail("Document currency is missing. Approval cannot continue.");
        }

        await LockInstancesAsync(db, scope.CompanyCode, scope.BranchCode!, docType, [(docNo, docRev)], cancellationToken);
        var existing = await FindActiveAsync(db, scope.CompanyCode, scope.BranchCode!, docType, docNo, docRev, cancellationToken);
        if (existing is not null && ActiveGateStatuses.Contains(existing.Status))
        {
            return new ProcurementApprovalResult { Succeeded = true, Instance = MapInstance(existing) };
        }

        var policy = await LoadPublishedPolicyAsync(db, scope.CompanyCode, docType, cancellationToken);
        if (policy is null || !policy.Enabled)
        {
            return ProcurementApprovalResult.Fail("Approval is not enabled for this document type.");
        }

        if (RecvOrConsumed)
        {
            return ProcurementApprovalResult.Fail("A consumed or received document cannot be submitted for approval.");
        }

        var currency = totals.Currency.Trim().ToUpperInvariant();
        var bands = policy.Bands
            .Where(x => string.Equals(x.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.MinInclusive)
            .ToList();
        if (bands.Count == 0)
        {
            return ProcurementApprovalResult.Fail($"No approval rule exists for currency {currency}.");
        }

        var total = totals.Total;
        var band = bands.FirstOrDefault(x =>
            total >= x.MinInclusive && (x.MaxExclusive is null || total < x.MaxExclusive.Value));
        if (band is null)
        {
            return ProcurementApprovalResult.Fail("No approval amount band covers this document total.");
        }

        var maxCycle = await db.ProcurementApprovalInstances
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                && x.DocType == docType && x.DocNo == docNo && x.DocRev == docRev)
            .Select(x => (int?)x.Cycle)
            .MaxAsync(cancellationToken) ?? 0;
        var now = DateTime.UtcNow;
        var actor = Truncate(scope.UserId, 20);

        var instance = new ProcurementApprovalInstance
        {
            CompanyCode = scope.CompanyCode,
            BranchCode = scope.BranchCode!,
            DocType = docType,
            DocNo = docNo,
            DocRev = docRev,
            Cycle = maxCycle + 1,
            PolicyId = policy.Uid,
            PolicyVersion = policy.Version,
            BandId = band.Uid,
            Currency = currency,
            Gross = totals.Gross,
            Taxes = totals.Taxes,
            Total = totals.Total,
            MaterialFingerprint = fingerprint,
            SubmittedBy = actor,
            SubmittedAtUtc = now,
            IdempotencyKey = Truncate(idempotencyKey, 80),
            CreatedDate = now,
            CurrentLevel = 1
        };

        if (band.NotRequired)
        {
            instance.Status = ProcurementApprovalStatuses.NotRequired;
            AddEvent(instance, actor, ProcurementApprovalEventTypes.NotRequired, null, instance.Status,
                "Explicit no-approval band.", idempotencyKey, now);
        }
        else
        {
            if (band.Levels.Count == 0)
            {
                return ProcurementApprovalResult.Fail("The matching approval band has no levels.");
            }

            foreach (var level in band.Levels.OrderBy(x => x.Sequence))
            {
                var users = await ResolveAssigneesAsync(db, scope.CompanyCode, level, actor, createdBy, cancellationToken);
                if (users.Count == 0)
                {
                    return ProcurementApprovalResult.Fail($"Level {level.Sequence} has no active eligible approver.");
                }

                foreach (var user in users)
                {
                    instance.Assignees.Add(new ProcurementApprovalAssignee
                    {
                        LevelSequence = level.Sequence,
                        UserId = user,
                        MatchMode = level.MatchMode
                    });
                }
            }

            instance.Status = ProcurementApprovalStatuses.Pending;
            instance.CurrentLevel = band.Levels.Min(x => x.Sequence);
            AddEvent(instance, actor, ProcurementApprovalEventTypes.Submitted, null, instance.Status, null, idempotencyKey, now);
        }

        Touch(db, instance);
        db.ProcurementApprovalInstances.Add(instance);
        return new ProcurementApprovalResult { Succeeded = true, Instance = MapInstance(instance) };
    }

    private async Task<ProcurementApprovalResult> CommitSubmitAsync(
        AppDbContext db,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        InventoryTenantScope scope,
        string docType,
        string docNo,
        short docRev,
        CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        var saved = await FindActiveAsync(db, scope.CompanyCode, scope.BranchCode!, docType, docNo, docRev, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new ProcurementApprovalResult
        {
            Succeeded = true,
            Instance = saved is null ? null : MapInstance(saved)
        };
    }

    private async Task<string?> AssertGateAsync(
        AppDbContext db, string companyCode, string branchCode, string docType, string docNo, short docRev,
        bool progressed, CancellationToken cancellationToken)
    {
        var policy = await LoadPublishedPolicyAsync(db, companyCode, docType, cancellationToken);
        var instance = await FindActiveAsync(db, companyCode, branchCode, docType, docNo, docRev, cancellationToken);
        if (instance is not null && string.Equals(instance.Status, ProcurementApprovalStatuses.Pending, StringComparison.OrdinalIgnoreCase))
        {
            return "This document is still waiting for approval.";
        }

        if (policy is null || !policy.Enabled)
        {
            return null;
        }

        if (instance is not null
            && (string.Equals(instance.Status, ProcurementApprovalStatuses.Approved, StringComparison.OrdinalIgnoreCase)
                || string.Equals(instance.Status, ProcurementApprovalStatuses.NotRequired, StringComparison.OrdinalIgnoreCase)))
        {
            var fingerprint = await CurrentFingerprintAsync(db, companyCode, branchCode, docType, docNo, docRev, cancellationToken);
            if (fingerprint is not null && !string.Equals(fingerprint, instance.MaterialFingerprint, StringComparison.Ordinal))
            {
                return "This document changed after approval. Submit it again before continuing.";
            }

            return null;
        }

        if (await db.ProcurementApprovalCutoverExemptions.AnyAsync(x =>
                x.CompanyCode == companyCode && x.BranchCode == branchCode && x.DocType == docType
                && x.DocNo == docNo && x.DocRev == docRev && x.PolicyId == policy.Uid, cancellationToken))
        {
            return null;
        }

        DateTime? created = null;
        string status = string.Empty;
        bool consumed = progressed;
        if (docType == ProcurementApprovalDocTypes.Pr)
        {
            var pr = await db.PoPrs.AsNoTracking().Include(x => x.Details)
                .FirstOrDefaultAsync(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.PrNo == docNo, cancellationToken);
            if (pr is null)
            {
                return "Purchase Requisition was not found.";
            }

            created = pr.CreatedDate ?? pr.CreateDt;
            status = pr.Status;
            consumed = IsPrConsumed(pr);
        }
        else
        {
            var po = await db.PoOrders.AsNoTracking().Include(x => x.Details)
                .FirstOrDefaultAsync(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.PoNo == docNo && x.PoRelNo == docRev, cancellationToken);
            if (po is null)
            {
                return "Purchase Order was not found.";
            }

            created = po.CreatedDate ?? po.PoDate;
            status = po.Status;
            consumed = po.Details.Any(x => x.RecvQty > 0m)
                || string.Equals(status, PoOrderStatuses.Received, StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, PoOrderStatuses.Closed, StringComparison.OrdinalIgnoreCase);
        }

        if (consumed
            || string.Equals(status, PoPrStatuses.PartiallyOrdered, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, PoPrStatuses.FullyOrdered, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, PoOrderStatuses.Received, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, PoOrderStatuses.Closed, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (policy.CutoverAtUtc is { } cutover && created is { } createdAt && createdAt < cutover)
        {
            if (string.Equals(policy.LegacyDraftMode, ProcurementApprovalLegacyModes.LegacyExempt, StringComparison.OrdinalIgnoreCase))
            {
                db.ProcurementApprovalCutoverExemptions.Add(new ProcurementApprovalCutoverExemption
                {
                    CompanyCode = companyCode,
                    BranchCode = branchCode,
                    DocType = docType,
                    DocNo = docNo,
                    DocRev = docRev,
                    PolicyId = policy.Uid,
                    CutoverAtUtc = cutover,
                    Reason = "LegacyExempt document created before enablement.",
                    ActorUserId = "SYSTEM",
                    CreatedDate = DateTime.UtcNow
                });
                db.ProcurementApprovalEvents.Add(new ProcurementApprovalEvent
                {
                    CompanyCode = companyCode,
                    BranchCode = branchCode,
                    DocType = docType,
                    DocNo = docNo,
                    DocRev = docRev,
                    EventType = ProcurementApprovalEventTypes.CutoverExempted,
                    PolicyVersion = policy.Version,
                    ActorUserId = "SYSTEM",
                    OccurredAtUtc = DateTime.UtcNow,
                    Reason = "LegacyExempt"
                });
                return null;
            }
        }

        return "This document has not been submitted for approval.";
    }

    private static async Task<string?> CurrentFingerprintAsync(
        AppDbContext db, string companyCode, string branchCode, string docType, string docNo, short docRev,
        CancellationToken cancellationToken)
    {
        if (docType == ProcurementApprovalDocTypes.Pr)
        {
            var pr = await db.PoPrs.Include(x => x.Details)
                .FirstOrDefaultAsync(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.PrNo == docNo, cancellationToken);
            return pr is null ? null : ProcurementApprovalFingerprint.ForPr(pr);
        }

        var po = await db.PoOrders.Include(x => x.Details)
            .FirstOrDefaultAsync(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.PoNo == docNo && x.PoRelNo == docRev, cancellationToken);
        return po is null ? null : ProcurementApprovalFingerprint.ForPo(po);
    }

    private static bool IsPrConsumed(PoPr header) =>
        PoPrCalc.IsDerivedOrderedStatus(header.Status)
        || header.Details.Any(d => !string.IsNullOrWhiteSpace(d.PoNo));

    private async Task<bool> IsDocumentConsumedAsync(
        AppDbContext db, ProcurementApprovalInstance instance, CancellationToken cancellationToken)
    {
        if (instance.DocType == ProcurementApprovalDocTypes.Pr)
        {
            var pr = await db.PoPrs.Include(x => x.Details)
                .FirstOrDefaultAsync(x => x.CompanyCode == instance.CompanyCode && x.BranchCode == instance.BranchCode && x.PrNo == instance.DocNo, cancellationToken);
            return pr is not null && IsPrConsumed(pr);
        }

        var po = await db.PoOrders.Include(x => x.Details)
            .FirstOrDefaultAsync(x => x.CompanyCode == instance.CompanyCode && x.BranchCode == instance.BranchCode
                && x.PoNo == instance.DocNo && x.PoRelNo == instance.DocRev, cancellationToken);
        return po is not null && po.Details.Any(x => x.RecvQty > 0m);
    }

    private static async Task<bool> IsDocumentCreatorAsync(
        AppDbContext db, ProcurementApprovalInstance instance, string actor, CancellationToken cancellationToken)
    {
        if (instance.DocType == ProcurementApprovalDocTypes.Pr)
        {
            var createdBy = await db.PoPrs.AsNoTracking()
                .Where(x => x.CompanyCode == instance.CompanyCode && x.BranchCode == instance.BranchCode && x.PrNo == instance.DocNo)
                .Select(x => x.CreatedBy)
                .FirstOrDefaultAsync(cancellationToken);
            return !string.IsNullOrWhiteSpace(createdBy)
                && string.Equals(createdBy.Trim(), actor, StringComparison.OrdinalIgnoreCase);
        }

        var poCreatedBy = await db.PoOrders.AsNoTracking()
            .Where(x => x.CompanyCode == instance.CompanyCode && x.BranchCode == instance.BranchCode
                && x.PoNo == instance.DocNo && x.PoRelNo == instance.DocRev)
            .Select(x => x.CreatedBy)
            .FirstOrDefaultAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(poCreatedBy)
            && string.Equals(poCreatedBy.Trim(), actor, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<string>> ResolveAssigneesAsync(
        AppDbContext db, string companyCode, ProcurementApprovalLevel level, string submitter, string? createdBy,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignee in level.Assignees)
        {
            if (string.Equals(assignee.Kind, ProcurementApprovalAssigneeKinds.User, StringComparison.OrdinalIgnoreCase))
            {
                ids.Add(assignee.RefCode.Trim());
            }
            else
            {
                var roleUsers = await (
                    from map in db.UserRoleMappings
                    join role in db.Roles on map.RoleId equals role.RoleId
                    join user in db.UserLogins on map.UserUid equals user.uid
                    where role.CompanyCode == companyCode
                        && role.RoleCode == assignee.RefCode
                        && role.IsActive
                        && user.CompanyCode == companyCode
                        && user.active == true
                    select user.id).ToListAsync(cancellationToken);
                foreach (var id in roleUsers)
                {
                    ids.Add(id);
                }
            }
        }

        ids.Remove(submitter);
        if (!string.IsNullOrWhiteSpace(createdBy))
        {
            ids.Remove(createdBy.Trim());
        }

        var active = new List<string>();
        foreach (var id in ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (await UserIsActiveAsync(db, companyCode, id, cancellationToken))
            {
                active.Add(Truncate(id, 20));
            }
        }

        return active;
    }

    private static async Task<bool> UserIsActiveAsync(
        AppDbContext db, string companyCode, string userId, CancellationToken cancellationToken)
    {
        var id = userId.Trim();
        return await db.UserLogins.AsNoTracking().AnyAsync(x =>
            x.CompanyCode == companyCode && x.id == id && x.active == true, cancellationToken);
    }

    private async Task<ProcurementApprovalPolicy?> LoadPublishedPolicyAsync(
        AppDbContext db, string companyCode, string docType, CancellationToken cancellationToken) =>
        await db.ProcurementApprovalPolicies
            .Include(x => x.Bands).ThenInclude(x => x.Levels).ThenInclude(x => x.Assignees)
            .Where(x => x.CompanyCode == companyCode && x.DocType == docType && x.IsPublished)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);

    private static async Task<ProcurementApprovalInstance?> FindActiveAsync(
        AppDbContext db, string companyCode, string branchCode, string docType, string docNo, short docRev,
        CancellationToken cancellationToken) =>
        await db.ProcurementApprovalInstances
            .Include(x => x.Assignees)
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.DocType == docType
                && x.DocNo == docNo && x.DocRev == docRev)
            .OrderByDescending(x => x.Cycle)
            .FirstOrDefaultAsync(cancellationToken);

    private static async Task<PoPr?> LockPrAsync(
        AppDbContext db, string company, string branch, string prNo, CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlServer())
        {
            return await db.PoPrs.FromSqlInterpolated($@"
SELECT * FROM dbo.POPR WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company} AND BranchCode = {branch} AND PRNo = {prNo}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.PoPrs.FirstOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.PrNo == prNo, cancellationToken);
    }

    private static async Task<PoOrder?> LockPoAsync(
        AppDbContext db, string company, string branch, string poNo, short poRelNo, CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlServer())
        {
            return await db.PoOrders.FromSqlInterpolated($@"
SELECT * FROM dbo.POOrder WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company} AND BranchCode = {branch} AND PONo = {poNo} AND PORelNo = {poRelNo}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.PoOrders.FirstOrDefaultAsync(x =>
            x.CompanyCode == company && x.BranchCode == branch && x.PoNo == poNo && x.PoRelNo == poRelNo, cancellationToken);
    }

    private static async Task<ProcurementApprovalInstance?> LockInstanceByIdAsync(
        AppDbContext db, long id, CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlServer())
        {
            return await db.ProcurementApprovalInstances.FromSqlInterpolated($@"
SELECT * FROM dbo.PoApprovalInstance WITH (UPDLOCK, HOLDLOCK) WHERE UID = {id}")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await db.ProcurementApprovalInstances.FirstOrDefaultAsync(x => x.Uid == id, cancellationToken);
    }

    private static async Task LockActiveInstanceAsync(
        AppDbContext db, string company, string branch, string docType, string docNo, short docRev,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlServer())
        {
            await db.ProcurementApprovalInstances.FromSqlInterpolated($@"
SELECT * FROM dbo.PoApprovalInstance WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyCode = {company} AND BranchCode = {branch} AND DocType = {docType}
  AND DocNo = {docNo} AND DocRev = {docRev}")
                .AsTracking()
                .ToListAsync(cancellationToken);
            return;
        }

        await db.ProcurementApprovalInstances
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.DocType == docType
                && x.DocNo == docNo && x.DocRev == docRev)
            .OrderByDescending(x => x.Cycle)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private Task<bool> CanSettingsAsync(string permission, CancellationToken cancellationToken) =>
        _accessRights.CanAsync(MenuCodes.PurchaseApprovalSettings, permission, cancellationToken);

    private Task<bool> CanInboxAsync(CancellationToken cancellationToken) =>
        _accessRights.CanAsync(MenuCodes.PurchaseApprovalInbox, PermissionCodes.Access, cancellationToken);

    private static string MenuFor(string docType) =>
        docType == ProcurementApprovalDocTypes.Po ? MenuCodes.PurchaseOrder : MenuCodes.PurchaseRequisition;

    private static string NormalizeDocType(string? docType)
    {
        var t = (docType ?? string.Empty).Trim().ToUpperInvariant();
        return t is ProcurementApprovalDocTypes.Po or "PO_ORDER" ? ProcurementApprovalDocTypes.Po : ProcurementApprovalDocTypes.Pr;
    }

    private static string? ValidatePolicy(ProcurementApprovalPolicyDto policy)
    {
        if (policy.Enabled && policy.Bands.Count == 0)
        {
            return "Enable approval only after amount bands are configured.";
        }

        foreach (var currencyGroup in policy.Bands.GroupBy(x => x.Currency.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var ordered = currencyGroup.OrderBy(x => x.MinInclusive).ToList();
            decimal cursor = 0m;
            for (var i = 0; i < ordered.Count; i++)
            {
                var band = ordered[i];
                if (band.MinInclusive < 0m)
                {
                    return "Amount bands cannot start below zero.";
                }

                if (i == 0 && band.MinInclusive != 0m)
                {
                    return $"Currency {currencyGroup.Key} must include a band starting at 0.00.";
                }

                if (i > 0 && band.MinInclusive != cursor)
                {
                    return $"Currency {currencyGroup.Key} has a gap or overlap in amount bands.";
                }

                if (band.MaxExclusive is { } max)
                {
                    if (max <= band.MinInclusive)
                    {
                        return "Each amount band upper limit must be greater than its lower limit.";
                    }

                    cursor = max;
                }
                else if (i != ordered.Count - 1)
                {
                    return "Only the last amount band for a currency may be unbounded.";
                }

                if (!band.NotRequired)
                {
                    if (band.Levels.Count is < 1 or > 3)
                    {
                        return "Each required band must have 1 to 3 approval levels.";
                    }

                    foreach (var level in band.Levels)
                    {
                        if (level.Assignees.Count == 0)
                        {
                            return "Each approval level must have at least one user or role.";
                        }
                    }
                }
            }
        }

        return null;
    }

    private static ProcurementApprovalPolicyDto MapPolicy(ProcurementApprovalPolicy policy) =>
        new()
        {
            Uid = policy.Uid,
            DocType = policy.DocType,
            Version = policy.Version,
            Enabled = policy.Enabled,
            LegacyDraftMode = policy.LegacyDraftMode,
            CutoverAtUtc = policy.CutoverAtUtc,
            RowVersion = policy.RowVersion ?? [],
            Bands = policy.Bands.Select(b => new ProcurementApprovalBandDto
            {
                Currency = b.Currency,
                MinInclusive = b.MinInclusive,
                MaxExclusive = b.MaxExclusive,
                NotRequired = b.NotRequired,
                Levels = b.Levels.OrderBy(l => l.Sequence).Select(l => new ProcurementApprovalLevelDto
                {
                    Sequence = l.Sequence,
                    MatchMode = l.MatchMode,
                    Assignees = l.Assignees.Select(a => new ProcurementApprovalLevelAssigneeDto
                    {
                        Kind = a.Kind,
                        RefCode = a.RefCode
                    }).ToList()
                }).ToList()
            }).ToList()
        };

    private static ProcurementApprovalInstanceDto MapInstance(ProcurementApprovalInstance instance) =>
        new()
        {
            Uid = instance.Uid,
            DocType = instance.DocType,
            DocNo = instance.DocNo,
            DocRev = instance.DocRev,
            Cycle = instance.Cycle,
            Status = instance.Status,
            CurrentLevel = instance.CurrentLevel,
            Currency = instance.Currency,
            Total = instance.Total,
            SubmittedBy = instance.SubmittedBy,
            SubmittedAtUtc = instance.SubmittedAtUtc,
            CurrentAssignees = instance.Assignees
                .Where(x => x.LevelSequence == instance.CurrentLevel)
                .Select(x => x.UserId)
                .ToList(),
            RowVersion = instance.RowVersion ?? []
        };

    private static ProcurementApprovalEventRow MapEvent(ProcurementApprovalEvent e) =>
        new()
        {
            OccurredAtUtc = e.OccurredAtUtc,
            ActorUserId = e.ActorUserId,
            EventType = e.EventType,
            PriorStatus = e.PriorStatus,
            NewStatus = e.NewStatus,
            Cycle = e.Cycle,
            PolicyVersion = e.PolicyVersion,
            Reason = e.Reason
        };

    private static void AddEvent(
        ProcurementApprovalInstance instance, string actor, string eventType, string? prior, string? next,
        string? reason, string? idempotencyKey, DateTime now)
    {
        instance.Events.Add(new ProcurementApprovalEvent
        {
            CompanyCode = instance.CompanyCode,
            BranchCode = instance.BranchCode,
            DocType = instance.DocType,
            DocNo = instance.DocNo,
            DocRev = instance.DocRev,
            Cycle = instance.Cycle,
            EventType = eventType,
            PriorStatus = prior,
            NewStatus = next,
            PolicyVersion = instance.PolicyVersion,
            Reason = Truncate(reason, 500),
            IdempotencyKey = Truncate(idempotencyKey, 80),
            ActorUserId = actor,
            OccurredAtUtc = now
        });
    }

    private static void Touch(AppDbContext db, ProcurementApprovalPolicy policy)
    {
        if (!db.Database.IsSqlServer())
        {
            policy.RowVersion = Guid.NewGuid().ToByteArray();
        }
    }

    private static void Touch(AppDbContext db, ProcurementApprovalInstance instance)
    {
        if (!db.Database.IsSqlServer())
        {
            instance.RowVersion = Guid.NewGuid().ToByteArray();
        }
    }

    private static string Truncate(string? value, int max)
    {
        var t = (value ?? string.Empty).Trim();
        return t.Length <= max ? t : t[..max];
    }

    private static int CalendarAgeDays(DateTime nowUtc, DateTime? submittedAtUtc)
    {
        if (submittedAtUtc is null)
        {
            return 0;
        }

        return Math.Max(0, (nowUtc.Date - submittedAtUtc.Value.Date).Days);
    }
}
