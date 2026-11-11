using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Quality;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ErpWeb.Core.Quality;

public sealed class QaCaseService : IQaCaseService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IRunningNumberService _runningNumbers;
    private readonly IBranchStockTransactionLock? _branchLock;
    private readonly IQaLogicalHoldAuthorization? _logicalHolds;
    private readonly IQaPhysicalHoldAuthorization? _physicalHolds;

    public QaCaseService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IRunningNumberService runningNumbers,
        IBranchStockTransactionLock? branchLock = null,
        IQaLogicalHoldAuthorization? logicalHolds = null,
        IQaPhysicalHoldAuthorization? physicalHolds = null)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _runningNumbers = runningNumbers;
        _branchLock = branchLock;
        _logicalHolds = logicalHolds;
        _physicalHolds = physicalHolds;
    }

    public async Task<QaResult> SearchAsync(QaListQuery query, CancellationToken cancellationToken = default)
    {
        var scope = RequireBranch();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await SchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        var rows = db.QaCases.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);

        if (!string.IsNullOrWhiteSpace(query.Status)
            && !string.Equals(query.Status, "all", StringComparison.OrdinalIgnoreCase))
        {
            var status = query.Status.Trim().ToUpperInvariant();
            rows = rows.Where(x => x.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.SourceType)
            && !string.Equals(query.SourceType, "all", StringComparison.OrdinalIgnoreCase))
        {
            var source = query.SourceType.Trim().ToUpperInvariant();
            rows = rows.Where(x => x.SourceType == source);
        }

        if (query.DateFrom is not null)
        {
            var from = query.DateFrom.Value.Date;
            rows = rows.Where(x => x.CaseDate >= from);
        }

        if (query.DateTo is not null)
        {
            var to = query.DateTo.Value.Date.AddDays(1);
            rows = rows.Where(x => x.CaseDate < to);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var text = query.SearchText.Trim();
            rows = rows.Where(x =>
                x.CaseNo.Contains(text)
                || (x.SourceDocNo != null && x.SourceDocNo.Contains(text))
                || (x.SupplierName != null && x.SupplierName.Contains(text))
                || (x.WorkOrderNo != null && x.WorkOrderNo.Contains(text))
                || (x.DeliveryNote != null && x.DeliveryNote.Contains(text))
                || (x.Remarks != null && x.Remarks.Contains(text)));
        }

        var descending = query.SortDescending;
        rows = (query.SortField ?? string.Empty).Trim() switch
        {
            nameof(QaCaseListRow.CaseNo) => descending ? rows.OrderByDescending(x => x.CaseNo) : rows.OrderBy(x => x.CaseNo),
            nameof(QaCaseListRow.Status) => descending ? rows.OrderByDescending(x => x.Status) : rows.OrderBy(x => x.Status),
            nameof(QaCaseListRow.SourceType) => descending ? rows.OrderByDescending(x => x.SourceType) : rows.OrderBy(x => x.SourceType),
            _ => descending ? rows.OrderByDescending(x => x.CaseDate).ThenByDescending(x => x.CaseNo) : rows.OrderBy(x => x.CaseDate).ThenBy(x => x.CaseNo)
        };

        var take = Math.Clamp(query.Take <= 0 ? 20 : query.Take, 1, 100);
        var skip = Math.Max(0, query.Skip);
        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.Skip(skip).Take(take)
            .Select(x => new QaCaseListRow
            {
                CaseNo = x.CaseNo,
                CaseDate = x.CaseDate,
                Status = x.Status,
                SourceType = x.SourceType,
                SourceDocNo = x.SourceDocNo,
                SupplierName = x.SupplierName,
                WorkOrderNo = x.WorkOrderNo,
                LineCount = x.Lines.Count,
                EligibilityNote = x.EligibilityNote,
                CreatedDate = x.CreatedDate,
                CreatedBy = x.CreatedBy,
                ModifiedDate = x.ModifiedDate,
                ModifiedBy = x.ModifiedBy
            })
            .ToListAsync(cancellationToken);

        return QaResult.OkList(new QaListPage { Rows = page, TotalCount = total });
    }

    public async Task<QaResult> GetInquiryAsync(QaInquiryQuery query, CancellationToken cancellationToken = default)
    {
        var scope = RequireBranch();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await _accessRights.CanAsync(MenuCodes.QualityInquiry, PermissionCodes.Access, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await SchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        var followUpReady = (await QaSchemaReadiness.EvaluateAsync(db, cancellationToken)).CanQueryFollowUp;
        var take = Math.Clamp(query.Take <= 0 ? 50 : query.Take, 1, QaInquiryLimits.MaxPageTake);
        return QaResult.OkInquiry(await LoadInquiryPageAsync(
            db, scope, query, followUpReady, take, cancellationToken));
    }

    public async Task<QaResult> GetWorkloadAsync(CancellationToken cancellationToken = default)
    {
        var scope = RequireBranch();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await SchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        var followUpReady = (await QaSchemaReadiness.EvaluateAsync(db, cancellationToken)).CanQueryFollowUp;
        return QaResult.OkInquiry(await LoadInquiryPageAsync(
            db, scope, new QaInquiryQuery(), followUpReady, take: 0, cancellationToken));
    }

    public async Task<QaResult> ExportInquiryAsync(QaInquiryQuery query, CancellationToken cancellationToken = default)
    {
        var scope = RequireBranch();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await _accessRights.CanAsync(MenuCodes.QualityInquiry, PermissionCodes.Export, cancellationToken)
            || !await _accessRights.CanAsync(MenuCodes.QualityInquiry, PermissionCodes.Access, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await SchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        var followUpReady = (await QaSchemaReadiness.EvaluateAsync(db, cancellationToken)).CanQueryFollowUp;
        query.Skip = 0;
        query.Take = QaInquiryLimits.MaxExportRows;
        var filtered = QaInquiryQuerying.Filter(
            db.QaCases.AsNoTracking(), scope.CompanyCode, scope.BranchCode!, query, followUpReady);
        var total = await filtered.CountAsync(cancellationToken);
        if (total > QaInquiryLimits.MaxExportRows)
        {
            return QaResult.Fail(QaCaseMessages.ExportTooLarge);
        }

        var page = await LoadInquiryPageAsync(
            db, scope, query, followUpReady, QaInquiryLimits.MaxExportRows, cancellationToken);
        return QaResult.OkFile(
            QaInquiryCsv.Build(page, query, DateTime.UtcNow),
            $"QaInquiry_{DateTime.UtcNow:yyMMddHHmmss}.csv");
    }

    public async Task<QaResult> GetAsync(string caseNo, CancellationToken cancellationToken = default)
    {
        var scope = RequireBranch();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await SchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        var entity = await LoadCaseAsync(db, scope, caseNo, cancellationToken);
        if (entity is null)
        {
            return QaResult.Fail("Quality case was not found.");
        }

        var sourceNote = await QaSourceResolver.RevalidateCaseAsync(db, entity, cancellationToken);
        var followUp = await QaSchemaReadiness.EvaluateAsync(db, cancellationToken);
        return QaResult.OkCase(Map(entity, followUp.CanQueryFollowUp, sourceNote));
    }

    public async Task<QaResult> SaveDraftAsync(QaCaseSaveCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        var isNew = string.IsNullOrWhiteSpace(command.CaseNo);
        if (!await CanAsync(isNew ? PermissionCodes.Add : PermissionCodes.Edit, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        var validation = ValidateDraft(command) ?? QaSourceResolver.FreezeGeneration(command);
        if (validation is not null)
        {
            return QaResult.Fail(validation);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await SchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        var sourceError = await QaSourceResolver.RevalidateDraftAsync(db, scope, command, cancellationToken);
        if (sourceError is not null)
        {
            return QaResult.Fail(sourceError);
        }

        QaCase entity;
        if (isNew)
        {
            entity = new QaCase
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode!,
                CaseNo = await AllocateCaseNoAsync(db, scope.CompanyCode, cancellationToken),
                Status = QaCaseStatuses.Draft,
                DeliveryInstanceId = string.Equals(command.SourceType, QaSourceTypes.IncomingPreGrn, StringComparison.OrdinalIgnoreCase)
                    ? Guid.NewGuid()
                    : null,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = TruncateUser(scope.UserId)
            };
            db.QaCases.Add(entity);
        }
        else
        {
            entity = await LoadCaseAsync(db, scope, command.CaseNo!, cancellationToken);
            if (entity is null)
            {
                return QaResult.Fail("Quality case was not found.");
            }

            if (!QaCaseStatuses.CanEdit(entity.Status))
            {
                return QaResult.Fail("Only draft quality cases can be edited.");
            }

            if (Stale(entity, command.RowVersion))
            {
                return QaResult.Fail(QaCaseMessages.ChangedReload);
            }
        }

        ApplyHeader(entity, command, scope.CompanyCode);
        if (!isNew)
        {
            db.QaDispositionLines.RemoveRange(entity.Dispositions);
            db.QaInspections.RemoveRange(entity.Inspections);
            db.QaCaseLines.RemoveRange(entity.Lines);
            entity.Dispositions.Clear();
            entity.Inspections.Clear();
            entity.Lines.Clear();
        }

        ReplaceLines(entity, command.Lines);
        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = TruncateUser(scope.UserId);
        AddAudit(entity, isNew ? QaAuditEvents.Created : QaAuditEvents.Saved, scope.UserId, entity.SourceType);
        return await CommitCaseAsync(db, entity, cancellationToken);
    }

    public Task<QaResult> SubmitAsync(QaReviewCommand command, CancellationToken cancellationToken = default) =>
        TransitionAsync(command, PermissionCodes.Submit, QaCaseStatuses.Draft, QaCaseStatuses.Submitted, QaAuditEvents.Submitted, cancellationToken);

    public Task<QaResult> StartInspectionAsync(QaReviewCommand command, CancellationToken cancellationToken = default) =>
        TransitionAsync(command, PermissionCodes.Edit, QaCaseStatuses.Submitted, QaCaseStatuses.InInspection, QaAuditEvents.InspectionStarted, cancellationToken);

    public async Task<QaResult> SaveInspectionAsync(QaInspectionSaveCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await SchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        var entity = await LoadCaseAsync(db, scope, command.CaseNo, cancellationToken);
        if (entity is null)
        {
            return QaResult.Fail("Quality case was not found.");
        }

        if (!string.Equals(entity.Status, QaCaseStatuses.InInspection, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entity.Status, QaCaseStatuses.Submitted, StringComparison.OrdinalIgnoreCase))
        {
            return QaResult.Fail("Inspection can only be recorded on a submitted or in-inspection case.");
        }

        if (Stale(entity, command.RowVersion))
        {
            return QaResult.Fail(QaCaseMessages.ChangedReload);
        }

        var sourceError = await QaSourceResolver.RevalidateInspectionAsync(db, entity, command.Lines, cancellationToken);
        if (sourceError is not null)
        {
            return QaResult.Fail(sourceError);
        }

        if (command.Lines.Count == 0)
        {
            return QaResult.Fail("Enter at least one inspection result.");
        }

        var followUpReady = (await QaSchemaReadiness.EvaluateAsync(db, cancellationToken)).CanQueryFollowUp;
        foreach (var lineCommand in command.Lines)
        {
            var line = entity.Lines.FirstOrDefault(x => x.LineNo == lineCommand.LineNo);
            if (line is null)
            {
                return QaResult.Fail($"Line {lineCommand.LineNo} was not found.");
            }

            if (!QaInspectionResults.IsKnown(lineCommand.Result))
            {
                return QaResult.Fail($"Inspection result on line {lineCommand.LineNo} is not valid.");
            }

            var inspected = IvQty.Round(lineCommand.InspectedQty);
            if (inspected <= 0m || inspected > line.Qty)
            {
                return QaResult.Fail($"Inspected quantity on line {lineCommand.LineNo} must be greater than zero and not exceed the case quantity.");
            }

            var dispositionError = ValidateDispositions(lineCommand.Dispositions, inspected, lineCommand.LineNo);
            if (dispositionError is not null)
            {
                return QaResult.Fail(dispositionError);
            }

            line.InspectedQty = inspected;
            var inspection = line.Inspections.FirstOrDefault() ?? entity.Inspections.FirstOrDefault(x => x.CaseLineId == line.Uid);
            if (inspection is null)
            {
                inspection = new QaInspection { Case = entity, CaseLine = line };
                entity.Inspections.Add(inspection);
                line.Inspections.Add(inspection);
            }

            inspection.Result = lineCommand.Result.Trim().ToUpperInvariant();
            inspection.Inspector = TruncateUser(scope.UserId);
            inspection.InspectedAtUtc = DateTime.UtcNow;
            inspection.Notes = TrimTo(lineCommand.Notes, 500);
            inspection.MeasuredValues = TrimTo(lineCommand.MeasuredValues, 500);

            ReplaceDispositions(db, entity, line, lineCommand.Dispositions);
            if (followUpReady)
            {
                QaFollowUp.AppendAttempt(
                    entity,
                    line,
                    TruncateUser(scope.UserId),
                    inspected,
                    lineCommand.Result,
                    lineCommand.Dispositions,
                    TrimTo(lineCommand.Notes, 500),
                    TrimTo(lineCommand.MeasuredValues, 500),
                    TrimTo(command.ReinspectionReason, 250));
            }
        }

        entity.Status = QaCaseStatuses.InInspection;
        entity.InspectedBy = TruncateUser(scope.UserId);
        entity.InspectedAtUtc = DateTime.UtcNow;
        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = TruncateUser(scope.UserId);
        AddAudit(entity, QaAuditEvents.Inspected, scope.UserId, null);
        return await CommitCaseAsync(db, entity, cancellationToken);
    }

    public async Task<QaResult> SubmitForReviewAsync(QaReviewCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await CanAsync(PermissionCodes.Submit, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await SchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        var entity = await LoadCaseAsync(db, scope, command.CaseNo, cancellationToken);
        if (entity is null)
        {
            return QaResult.Fail("Quality case was not found.");
        }

        if (!string.Equals(entity.Status, QaCaseStatuses.InInspection, StringComparison.OrdinalIgnoreCase))
        {
            return QaResult.Fail("Submit for review only after inspection.");
        }

        if (Stale(entity, command.RowVersion))
        {
            return QaResult.Fail(QaCaseMessages.ChangedReload);
        }

        if (entity.Lines.Any(x =>
                x.Inspections.Count == 0
                || IvQty.Round(x.InspectedQty) != IvQty.Round(x.Qty)))
        {
            return QaResult.Fail(QaCaseMessages.InspectFullQuantity);
        }

        entity.Status = QaCaseStatuses.PendingReview;
        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = TruncateUser(scope.UserId);
        AddAudit(entity, QaAuditEvents.ReviewSubmitted, scope.UserId, command.Remarks);
        return await CommitCaseAsync(db, entity, cancellationToken);
    }

    public async Task<QaResult> ApproveAsync(QaReviewCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await CanAsync(PermissionCodes.Approve, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await SchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var entity = await LoadCaseAsync(db, scope, command.CaseNo, cancellationToken);
        if (entity is null)
        {
            return QaResult.Fail("Quality case was not found.");
        }

        if (!string.Equals(entity.Status, QaCaseStatuses.PendingReview, StringComparison.OrdinalIgnoreCase))
        {
            return QaResult.Fail("Only cases pending review can be approved.");
        }

        if (Stale(entity, command.RowVersion))
        {
            return QaResult.Fail(QaCaseMessages.ChangedReload);
        }

        var policy = await QaPolicyService.LoadPublishedAsync(db, scope.CompanyCode, cancellationToken);
        if (InspectorMayNotReview(entity, scope.UserId, policy))
        {
            return QaResult.Fail("The inspector cannot approve this case. Another user must review it.");
        }

        if (_branchLock is not null && !string.IsNullOrWhiteSpace(scope.BranchCode))
        {
            await _branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode, cancellationToken);
        }

        var sourceError = await QaSourceResolver.RevalidateCaseAsync(db, entity, cancellationToken);
        if (sourceError is not null)
        {
            return QaResult.Fail(sourceError);
        }

        entity.Status = QaCaseStatuses.Approved;
        entity.ReviewedBy = TruncateUser(scope.UserId);
        entity.ReviewedAtUtc = DateTime.UtcNow;
        entity.ReviewDecision = QaCaseStatuses.Approved;
        entity.ReviewRemarks = TrimTo(command.Remarks, 500);
        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = TruncateUser(scope.UserId);
        if (string.Equals(entity.SourceType, QaSourceTypes.IncomingPreGrn, StringComparison.OrdinalIgnoreCase))
        {
            entity.DeliveryInstanceId ??= Guid.NewGuid();
        }

        entity.EligibilityNote = QaEligibilityNotes.ForSource(entity.SourceType);
        var followUp = await QaSchemaReadiness.EvaluateAsync(db, cancellationToken);
        if (followUp.CanQueryFollowUp)
        {
            QaFollowUp.EnsureApprovedActions(entity, TruncateUser(scope.UserId));
            if (entity.CorrectiveActions.Any(x => x.Uid == 0))
            {
                AddAudit(entity, QaAuditEvents.ActionCreated, scope.UserId, "Follow-up tasks were created from reject, rework, and hold quantities.");
            }
        }

        if (_logicalHolds is not null)
        {
            var holdError = await _logicalHolds.PlaceFromApprovedCaseAsync(
                db, scope, entity, policy, cancellationToken);
            if (holdError is not null)
            {
                return QaResult.Fail(holdError);
            }

            if (entity.LogicalHolds.Any(x =>
                    string.Equals(x.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase)))
            {
                AddAudit(entity, QaAuditEvents.HoldPlaced, scope.UserId, QaEligibilityNotes.WipReserved);
            }
        }

        if (_physicalHolds is not null)
        {
            var physicalError = await _physicalHolds.PlaceFromApprovedCaseAsync(
                db, scope, entity, policy, cancellationToken);
            if (physicalError is not null)
            {
                return QaResult.Fail(physicalError);
            }

            if (entity.PhysicalHolds.Any(x =>
                    string.Equals(x.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase)))
            {
                AddAudit(entity, QaAuditEvents.HoldPlaced, scope.UserId, QaEligibilityNotes.PhysicalReserved);
            }
        }

        AddAudit(entity, QaAuditEvents.Approved, scope.UserId, entity.EligibilityNote);
        return await CommitCaseAsync(db, entity, cancellationToken, tx);
    }

    public async Task<QaResult> ReleaseHoldAsync(QaReviewCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await CanAsync(PermissionCodes.Approve, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        if (_logicalHolds is null && _physicalHolds is null)
        {
            return QaResult.Fail("Quality hold release is not available.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await SchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var entity = await LoadCaseAsync(db, scope, command.CaseNo, cancellationToken);
        if (entity is null)
        {
            return QaResult.Fail("Quality case was not found.");
        }

        if (!string.Equals(entity.Status, QaCaseStatuses.Approved, StringComparison.OrdinalIgnoreCase))
        {
            return QaResult.Fail("Only approved cases can release a quality hold.");
        }

        if (Stale(entity, command.RowVersion))
        {
            return QaResult.Fail(QaCaseMessages.ChangedReload);
        }

        if (_branchLock is not null && !string.IsNullOrWhiteSpace(scope.BranchCode))
        {
            await _branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode, cancellationToken);
        }

        var released = false;
        if (_logicalHolds is not null
            && entity.LogicalHolds.Any(x =>
                string.Equals(x.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase)))
        {
            var error = await _logicalHolds.ReleaseCaseAsync(db, scope, entity, cancellationToken);
            if (error is not null)
            {
                return QaResult.Fail(error);
            }

            released = true;
            AddAudit(entity, QaAuditEvents.HoldReleased, scope.UserId, QaEligibilityNotes.WipLot);
        }

        if (_physicalHolds is not null
            && entity.PhysicalHolds.Any(x =>
                string.Equals(x.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase)))
        {
            var error = await _physicalHolds.ReleaseCaseAsync(db, scope, entity, cancellationToken);
            if (error is not null)
            {
                return QaResult.Fail(error);
            }

            released = true;
            AddAudit(entity, QaAuditEvents.HoldReleased, scope.UserId, QaEligibilityNotes.PostedStock);
        }

        if (!released)
        {
            return QaResult.Fail("There is no active quality hold to release.");
        }

        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = TruncateUser(scope.UserId);
        return await CommitCaseAsync(db, entity, cancellationToken, tx);
    }

    public async Task<QaResult> RejectAsync(QaReviewCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await CanAsync(PermissionCodes.Reject, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await SchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        var entity = await LoadCaseAsync(db, scope, command.CaseNo, cancellationToken);
        if (entity is null)
        {
            return QaResult.Fail("Quality case was not found.");
        }

        if (!string.Equals(entity.Status, QaCaseStatuses.PendingReview, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entity.Status, QaCaseStatuses.Submitted, StringComparison.OrdinalIgnoreCase))
        {
            return QaResult.Fail("Only submitted or pending-review cases can be rejected.");
        }

        if (Stale(entity, command.RowVersion))
        {
            return QaResult.Fail(QaCaseMessages.ChangedReload);
        }

        var rejectPolicy = await QaPolicyService.LoadPublishedAsync(db, scope.CompanyCode, cancellationToken);
        if (InspectorMayNotReview(entity, scope.UserId, rejectPolicy))
        {
            return QaResult.Fail("The inspector cannot reject this case. Another user must review it.");
        }

        entity.Status = QaCaseStatuses.Rejected;
        entity.ReviewedBy = TruncateUser(scope.UserId);
        entity.ReviewedAtUtc = DateTime.UtcNow;
        entity.ReviewDecision = QaCaseStatuses.Rejected;
        entity.ReviewRemarks = TrimTo(command.Remarks, 500);
        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = TruncateUser(scope.UserId);
        AddAudit(entity, QaAuditEvents.Rejected, scope.UserId, command.Remarks);
        return await CommitCaseAsync(db, entity, cancellationToken);
    }

    public async Task<QaResult> SaveActionAsync(QaActionSaveCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await FollowUpSchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        var entity = await LoadCaseAsync(db, scope, command.CaseNo, cancellationToken);
        if (entity is null)
        {
            return QaResult.Fail("Quality case was not found.");
        }

        if (Stale(entity, command.RowVersion))
        {
            return QaResult.Fail(QaCaseMessages.ChangedReload);
        }

        if (command.RequestId is { } requestId)
        {
            var existing = entity.CorrectiveActions.FirstOrDefault(x => x.RequestId == requestId);
            if (existing is not null)
            {
                return QaResult.OkCase(Map(entity, followUpReady: true));
            }
        }

        if (!QaActionTypes.IsKnown(command.ActionType))
        {
            return QaResult.Fail("Select a follow-up type.");
        }

        var line = entity.Lines.FirstOrDefault(x => x.LineNo == command.LineNo);
        if (line is null)
        {
            return QaResult.Fail($"Line {command.LineNo} was not found.");
        }

        var qtyError = QaFollowUp.ValidateNewQty(entity, line, command.ActionType, command.Qty);
        if (qtyError is not null)
        {
            return QaResult.Fail(qtyError);
        }

        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return QaResult.Fail("Enter a reason for this follow-up task.");
        }

        QaFollowUp.AddAction(
            entity,
            line,
            command.ActionType,
            command.Qty,
            TruncateUser(scope.UserId),
            TrimTo(command.Reason, 250),
            TrimTo(command.AssignedTo, 20),
            TrimTo(command.AssignedDepartment, 30),
            command.DueDate,
            TrimTo(command.Remarks, 500),
            command.RequestId);
        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = TruncateUser(scope.UserId);
        AddAudit(entity, QaAuditEvents.ActionCreated, scope.UserId, command.Reason);
        return await CommitCaseAsync(db, entity, cancellationToken);
    }

    public Task<QaResult> StartActionAsync(QaActionUpdateCommand command, CancellationToken cancellationToken = default) =>
        MutateActionAsync(command, QaActionStatuses.InProgress, QaAuditEvents.ActionStarted, requireReason: false, cancellationToken);

    public Task<QaResult> ReassignActionAsync(QaActionUpdateCommand command, CancellationToken cancellationToken = default) =>
        MutateActionAsync(command, null, QaAuditEvents.ActionReassigned, requireReason: true, cancellationToken);

    public Task<QaResult> CompleteActionAsync(QaActionUpdateCommand command, CancellationToken cancellationToken = default) =>
        MutateActionAsync(command, QaActionStatuses.Done, QaAuditEvents.ActionCompleted, requireReason: false, cancellationToken);

    public Task<QaResult> CancelActionAsync(QaActionUpdateCommand command, CancellationToken cancellationToken = default) =>
        MutateActionAsync(command, QaActionStatuses.Cancelled, QaAuditEvents.ActionCancelled, requireReason: true, cancellationToken);

    public async Task<QaResult> ReinspectAsync(QaReviewCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await FollowUpSchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        var entity = await LoadCaseAsync(db, scope, command.CaseNo, cancellationToken);
        if (entity is null)
        {
            return QaResult.Fail("Quality case was not found.");
        }

        if (Stale(entity, command.RowVersion))
        {
            return QaResult.Fail(QaCaseMessages.ChangedReload);
        }

        if (!QaFollowUp.CanReinspect(entity))
        {
            return QaResult.Fail(QaCaseMessages.ReinspectionBlocked);
        }

        if (string.IsNullOrWhiteSpace(command.Remarks))
        {
            return QaResult.Fail("Enter a reason to reinspect the same source. Quality will not create a second delivery.");
        }

        entity.Status = QaCaseStatuses.InInspection;
        entity.ReviewDecision = null;
        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = TruncateUser(scope.UserId);
        AddAudit(entity, QaAuditEvents.ReinspectionStarted, scope.UserId, command.Remarks);
        return await CommitCaseAsync(db, entity, cancellationToken);
    }

    public Task<QaResult> CloseAsync(QaReviewCommand command, CancellationToken cancellationToken = default) =>
        TransitionAsync(
            command,
            PermissionCodes.Edit,
            allowedFrom: [QaCaseStatuses.Approved, QaCaseStatuses.Rejected],
            toStatus: QaCaseStatuses.Closed,
            audit: QaAuditEvents.Closed,
            cancellationToken);

    public Task<QaResult> CancelAsync(QaReviewCommand command, CancellationToken cancellationToken = default) =>
        TransitionAsync(
            command,
            PermissionCodes.Delete,
            allowedFrom: [QaCaseStatuses.Draft, QaCaseStatuses.Submitted],
            toStatus: QaCaseStatuses.Cancelled,
            audit: QaAuditEvents.Cancelled,
            cancellationToken);

    public async Task<QaResult> DeleteDraftAsync(QaReviewCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await CanAsync(PermissionCodes.Delete, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await SchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        var entity = await LoadCaseAsync(db, scope, command.CaseNo, cancellationToken);
        if (entity is null)
        {
            return QaResult.Fail("Quality case was not found.");
        }

        if (!QaCaseStatuses.CanDelete(entity.Status))
        {
            return QaResult.Fail("Only draft quality cases can be deleted.");
        }

        if (Stale(entity, command.RowVersion))
        {
            return QaResult.Fail(QaCaseMessages.ChangedReload);
        }

        db.QaAuditEvents.RemoveRange(entity.AuditEvents);
        db.QaDispositionLines.RemoveRange(entity.Dispositions);
        db.QaInspections.RemoveRange(entity.Inspections);
        if (entity.CorrectiveActions.Count > 0)
        {
            db.QaCorrectiveActions.RemoveRange(entity.CorrectiveActions);
        }

        if (entity.InspectionAttempts.Count > 0)
        {
            db.QaInspectionAttempts.RemoveRange(entity.InspectionAttempts);
        }

        db.QaCaseLines.RemoveRange(entity.Lines);
        db.QaCases.Remove(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new QaResult { Succeeded = true };
        }
        catch (DbUpdateConcurrencyException)
        {
            return QaResult.Fail(QaCaseMessages.ChangedReload);
        }
    }

    public async Task<QaResult> SearchSourcesAsync(QaSourceQuery query, CancellationToken cancellationToken = default)
    {
        var scope = RequireBranch();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!QaSourceTypes.IsKnown(query.SourceType))
        {
            return QaResult.Fail("Select a quality source type.");
        }

        var exact = query.ExactSourceHeaderId is > 0 || query.ExactLotId is > 0 || query.ExactOutputId is > 0;
        var take = exact
            ? Math.Clamp(query.Take <= 0 ? 500 : query.Take, 1, 500)
            : Math.Clamp(query.Take <= 0 ? 20 : query.Take, 1, 100);
        var skip = exact ? 0 : Math.Max(0, query.Skip);
        var text = (query.SearchText ?? string.Empty).Trim();
        var sourceType = query.SourceType.Trim().ToUpperInvariant();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        return sourceType switch
        {
            QaSourceTypes.IncomingPreGrn => await SearchIncomingAsync(db, scope, text, skip, take, cancellationToken),
            QaSourceTypes.PostedGrn => await SearchPostedInventoryAsync(db, scope, IvTrxTypes.GoodsReceive, QaSourceTypes.PostedGrn, text, skip, take, query.ExactSourceHeaderId, cancellationToken),
            QaSourceTypes.InventoryStock => await SearchPostedInventoryAsync(db, scope, IvTrxTypes.MiscellaneousReceipt, QaSourceTypes.InventoryStock, text, skip, take, query.ExactSourceHeaderId, cancellationToken),
            QaSourceTypes.ProductionOutputGood => await SearchOutputAsync(db, scope, goodOnly: true, text, skip, take, query.ExactOutputId ?? query.ExactSourceHeaderId, cancellationToken),
            QaSourceTypes.ProductionOutputNonGood => await SearchOutputAsync(db, scope, goodOnly: false, text, skip, take, query.ExactOutputId ?? query.ExactSourceHeaderId, cancellationToken),
            QaSourceTypes.ProductionWipLot => await SearchLotsAsync(db, scope, "PROCESS_WIP", QaSourceTypes.ProductionWipLot, text, skip, take, query.ExactLotId, cancellationToken),
            QaSourceTypes.FgStagingLot => await SearchLotsAsync(db, scope, "FG_STAGING", QaSourceTypes.FgStagingLot, text, skip, take, query.ExactLotId, cancellationToken),
            _ => QaResult.Fail("Select a quality source type.")
        };
    }

    private async Task<QaResult> TransitionAsync(
        QaReviewCommand command,
        string permission,
        string fromStatus,
        string toStatus,
        string audit,
        CancellationToken cancellationToken) =>
        await TransitionAsync(command, permission, [fromStatus], toStatus, audit, cancellationToken);

    private async Task<QaResult> TransitionAsync(
        QaReviewCommand command,
        string permission,
        IReadOnlyList<string> allowedFrom,
        string toStatus,
        string audit,
        CancellationToken cancellationToken)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await CanAsync(permission, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await SchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        var entity = await LoadCaseAsync(db, scope, command.CaseNo, cancellationToken);
        if (entity is null)
        {
            return QaResult.Fail("Quality case was not found.");
        }

        if (!allowedFrom.Contains(entity.Status, StringComparer.OrdinalIgnoreCase))
        {
            return QaResult.Fail($"This case cannot move from {entity.Status} to {toStatus}.");
        }

        if (Stale(entity, command.RowVersion))
        {
            return QaResult.Fail(QaCaseMessages.ChangedReload);
        }

        if (string.Equals(toStatus, QaCaseStatuses.Closed, StringComparison.OrdinalIgnoreCase))
        {
            var logicalActive = entity.LogicalHolds.Any(x =>
                string.Equals(x.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase));
            var physicalRemaining = IvQty.Round(
                entity.PhysicalHolds
                    .Where(x => string.Equals(x.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase))
                    .Sum(x => x.ReservedBaseQty)
                - entity.PhysicalHolds.SelectMany(x => x.Allocations)
                    .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                    .Sum(x => x.Qty));
            if (logicalActive || physicalRemaining > 0m)
            {
                return QaResult.Fail("Release the quality hold before closing this case.");
            }

            if (QaFollowUp.HasOpenActions(entity))
            {
                return QaResult.Fail(QaCaseMessages.OpenActionsRemain);
            }
        }

        if (string.Equals(toStatus, QaCaseStatuses.Submitted, StringComparison.OrdinalIgnoreCase)
            && entity.Lines.Count == 0)
        {
            return QaResult.Fail("Add at least one line before submitting.");
        }

        if (string.Equals(toStatus, QaCaseStatuses.Submitted, StringComparison.OrdinalIgnoreCase))
        {
            entity.SubmittedBy = TruncateUser(scope.UserId);
            entity.SubmittedAtUtc = DateTime.UtcNow;
        }

        entity.Status = toStatus;
        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = TruncateUser(scope.UserId);
        AddAudit(entity, audit, scope.UserId, command.Remarks);
        return await CommitCaseAsync(db, entity, cancellationToken);
    }

    private async Task<QaResult> SearchIncomingAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        string text,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var lines = db.PoOrderDetails.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);
        if (!string.IsNullOrWhiteSpace(text))
        {
            lines = lines.Where(x =>
                x.PoNo.Contains(text)
                || (x.ICode != null && x.ICode.Contains(text))
                || (x.IDesc != null && x.IDesc.Contains(text)));
        }

        var joined = from line in lines
                     join hdr in db.PoOrders.AsNoTracking()
                         on new { line.CompanyCode, line.BranchCode, line.PoNo, line.PoRelNo }
                         equals new { hdr.CompanyCode, hdr.BranchCode, hdr.PoNo, hdr.PoRelNo }
                     select new { line, hdr };

        var total = await joined.CountAsync(cancellationToken);
        var page = await joined
            .OrderBy(x => x.line.PoNo).ThenBy(x => x.line.Line)
            .Skip(skip).Take(take)
            .Select(x => new QaSourceRow
            {
                SourceType = QaSourceTypes.IncomingPreGrn,
                DisplayText = x.line.PoNo + " / " + x.line.Line + " · " + (x.line.ICode ?? ""),
                SourceDocType = "PO",
                SourceDocNo = x.line.PoNo,
                ItemCode = x.line.ICode,
                ItemName = x.line.IDesc,
                Uom = x.line.PurchaseUom ?? x.line.StdUom,
                Qty = x.line.PoQty,
                SupplierCode = x.hdr.VendCode,
                SupplierName = x.hdr.VendName,
                PoNo = x.line.PoNo,
                PoRelNo = x.line.PoRelNo,
                PoLineNo = x.line.Line,
                SourceHref = "/purchase/orders/view/" + x.line.PoNo + "/" + x.line.PoRelNo,
                EligibilityNote = QaEligibilityNotes.IncomingPreGrn
            })
            .ToListAsync(cancellationToken);

        return QaResult.OkSources(new QaSourceListPage { Rows = page, TotalCount = total });
    }

    private async Task<QaResult> SearchPostedInventoryAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        string trxType,
        string sourceType,
        string text,
        int skip,
        int take,
        long? exactHeaderId,
        CancellationToken cancellationToken)
    {
        var details = from d in db.IvTrxBatchDetails.AsNoTracking()
                      join b in db.IvTrxBatches.AsNoTracking() on d.BatchId equals b.Id
                      where b.CompanyCode == scope.CompanyCode
                            && b.BranchCode == scope.BranchCode
                            && b.TrxType == trxType
                            && b.BatchStatus == IvBatchStatuses.Posted
                            && b.DeletedAtUtc == null
                      select new { d, b };

        if (exactHeaderId is > 0 and <= int.MaxValue)
        {
            var batchId = (int)exactHeaderId.Value;
            details = details.Where(x => x.b.Id == batchId);
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            var batchNo = int.TryParse(text, out var parsed) ? parsed : (int?)null;
            details = details.Where(x =>
                (batchNo != null && (x.b.BatchNo == batchNo || x.b.Id == batchNo))
                || (x.b.RefNo != null && x.b.RefNo.Contains(text))
                || (x.d.ICode != null && x.d.ICode.Contains(text))
                || (x.d.PoNo != null && x.d.PoNo.Contains(text)));
        }

        var total = await details.CountAsync(cancellationToken);
        var hrefBase = string.Equals(trxType, IvTrxTypes.GoodsReceive, StringComparison.Ordinal)
            ? "/inventory/goods-receipts/view/"
            : "/inventory/misc-receipt/view/";
        var page = await details
            .OrderByDescending(x => x.b.TrxDtTime)
            .Skip(skip).Take(take)
            .Select(x => new QaSourceRow
            {
                SourceType = sourceType,
                DisplayText = x.b.BatchNo + " · " + (x.d.ICode ?? ""),
                SourceDocType = trxType,
                SourceDocNo = x.b.BatchNo.ToString(),
                SourceHeaderId = x.b.Id,
                SourceDocumentRevision = x.d.DocumentRevision,
                SourceLineId = x.d.Id,
                ItemCode = x.d.ICode,
                ItemName = x.d.IDesc,
                Uom = x.d.ToStdUom ?? x.d.ToPurUom,
                Qty = x.d.ToStdQty ?? x.d.ToPurQty ?? 0m,
                LotNo = x.d.ToLotNo,
                WarehouseCode = x.d.ToWarehouse,
                LocationCode = x.d.ToLocation,
                ItemStatus = x.d.IStatus,
                SupplierCode = x.b.VendCode,
                SupplierName = x.b.VendName,
                DeliveryNote = x.b.RefNo,
                PoNo = x.d.PoNo,
                PoRelNo = x.d.PoRelNo,
                PoLineNo = x.d.PoLineNo,
                SourceHref = hrefBase + x.b.BatchNo,
                EligibilityNote = QaEligibilityNotes.ForSource(sourceType)
            })
            .ToListAsync(cancellationToken);

        return QaResult.OkSources(new QaSourceListPage { Rows = page, TotalCount = total });
    }

    private async Task<QaResult> SearchOutputAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        bool goodOnly,
        string text,
        int skip,
        int take,
        long? exactOutputId,
        CancellationToken cancellationToken)
    {
        var sourceType = goodOnly ? QaSourceTypes.ProductionOutputGood : QaSourceTypes.ProductionOutputNonGood;
        var rows = db.ProductionOutputs.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                        && x.BranchCode == scope.BranchCode
                        && x.DeletedAtUtc == null);
        if (goodOnly)
        {
            rows = rows.Where(x => x.Status == ProductionOutputStatuses.Posted && x.GoodQty > 0);
        }
        else
        {
            rows = rows.Where(x =>
                (x.Status == ProductionOutputStatuses.New || x.Status == ProductionOutputStatuses.Posted)
                && x.ScrapQty + x.RejectQty + x.HoldQty > 0);
        }

        if (exactOutputId is > 0)
        {
            rows = rows.Where(x => x.Uid == exactOutputId);
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            var outputId = long.TryParse(text, out var parsed) ? parsed : (long?)null;
            rows = rows.Where(x =>
                (outputId != null && x.Uid == outputId)
                || x.DocumentNo.Contains(text)
                || x.OutputItemCode.Contains(text)
                || x.OutputLotNo.Contains(text)
                || (x.WorkOrder != null && x.WorkOrder.WorkOrderNo.Contains(text)));
        }

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows
            .OrderByDescending(x => x.ProductionDate)
            .Skip(skip).Take(take)
            .Select(x => new QaSourceRow
            {
                SourceType = sourceType,
                DisplayText = x.DocumentNo + " · " + x.OutputItemCode,
                SourceDocType = "WI",
                SourceDocNo = x.DocumentNo,
                SourceHeaderId = x.Uid,
                SourceDocumentRevision = x.DocumentRevision,
                ItemCode = x.OutputItemCode,
                Uom = x.OutputUom,
                Qty = goodOnly ? x.GoodQty : x.ScrapQty + x.RejectQty + x.HoldQty,
                LotNo = x.OutputLotNo,
                WorkOrderNo = x.WorkOrder != null ? x.WorkOrder.WorkOrderNo : null,
                SourceHref = "/planning/daily-production/view/" + x.Uid,
                EligibilityNote = QaEligibilityNotes.ForSource(sourceType)
            })
            .ToListAsync(cancellationToken);

        return QaResult.OkSources(new QaSourceListPage { Rows = page, TotalCount = total });
    }

    private async Task<QaResult> SearchLotsAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        string stage,
        string sourceType,
        string text,
        int skip,
        int take,
        long? exactLotId,
        CancellationToken cancellationToken)
    {
        var rows = db.ProductionBalLots.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                        && x.BranchCode == scope.BranchCode
                        && x.BalanceStage == stage
                        && x.BaseQty > 0);
        if (exactLotId is > 0)
        {
            rows = rows.Where(x => x.Uid == exactLotId);
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            var lotId = long.TryParse(text, out var parsed) ? parsed : (long?)null;
            rows = rows.Where(x =>
                (lotId != null && x.Uid == lotId)
                || x.ItemCode.Contains(text)
                || x.WorkOrderNo.Contains(text)
                || x.LotNo.Contains(text));
        }

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows
            .OrderBy(x => x.ItemCode).ThenBy(x => x.LotNo)
            .Skip(skip).Take(take)
            .Select(x => new QaSourceRow
            {
                SourceType = sourceType,
                DisplayText = x.WorkOrderNo + " · " + x.ItemCode + " · " + x.LotNo,
                SourceDocType = "WIP",
                SourceDocNo = x.WorkOrderNo,
                SourceHeaderId = x.Uid,
                ItemCode = x.ItemCode,
                ItemName = x.Description,
                Uom = x.Uom,
                Qty = x.Qty,
                LotNo = x.LotNo,
                WarehouseCode = x.WarehouseCode,
                LocationCode = x.LocationCode,
                ItemStatus = x.StockStatusCode,
                WorkOrderNo = x.WorkOrderNo,
                ProductionBalLotId = x.Uid,
                SourceHref = "/planning/production-balance",
                EligibilityNote = QaEligibilityNotes.ForSource(sourceType)
            })
            .ToListAsync(cancellationToken);

        return QaResult.OkSources(new QaSourceListPage { Rows = page, TotalCount = total });
    }

    private async Task<QaInquiryPage> LoadInquiryPageAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        QaInquiryQuery query,
        bool followUpReady,
        int take,
        CancellationToken cancellationToken)
    {
        var filtered = QaInquiryQuerying.Filter(
            db.QaCases.AsNoTracking(), scope.CompanyCode, scope.BranchCode!, query, followUpReady);
        var kpis = await ComputeInquiryKpisAsync(filtered, followUpReady, cancellationToken);
        var skip = Math.Max(0, query.Skip);
        var pageIds = take <= 0
            ? []
            : await filtered
                .OrderByDescending(x => x.CaseDate)
                .ThenByDescending(x => x.CaseNo)
                .Skip(skip)
                .Take(take)
                .Select(x => x.Uid)
                .ToListAsync(cancellationToken);

        IReadOnlyList<QaInquiryRow> inquiryRows = [];
        if (pageIds.Count > 0)
        {
            IQueryable<QaCase> graph = db.QaCases.AsNoTracking()
                .Where(x => pageIds.Contains(x.Uid))
                .Include(x => x.Lines)
                .ThenInclude(x => x.Dispositions)
                .Include(x => x.Inspections)
                .Include(x => x.Grants)
                .ThenInclude(x => x.Allocations)
                .Include(x => x.FgGrants)
                .ThenInclude(x => x.Allocations)
                .Include(x => x.LogicalHolds)
                .Include(x => x.PhysicalHolds)
                .ThenInclude(x => x.Allocations)
                .Include(x => x.NonGoodGrants)
                .ThenInclude(x => x.Allocations);
            if (followUpReady)
            {
                graph = graph.Include(x => x.CorrectiveActions);
            }

            var entities = await graph.ToListAsync(cancellationToken);
            var byId = entities.ToDictionary(x => x.Uid);
            var today = DateTime.Today;
            inquiryRows = pageIds
                .Where(byId.ContainsKey)
                .Select(id => MapInquiryRow(byId[id], today, followUpReady))
                .ToList();
        }

        return new QaInquiryPage
        {
            CaseCount = kpis.TotalCount,
            OpenCaseCount = kpis.OpenCaseCount,
            PendingInspectionCount = kpis.PendingInspectionCount,
            PendingReviewCount = kpis.PendingReviewCount,
            OpenDestinationCount = kpis.OpenDestinationCount,
            RejectedDecisionQty = kpis.RejectedDecisionQty,
            OverdueActionCount = kpis.OverdueActionCount,
            CompletedActionCount = kpis.CompletedActionCount,
            AverageCloseDays = kpis.AverageCloseDays,
            MedianCloseDays = kpis.MedianCloseDays,
            InspectedCount = kpis.InspectedCount,
            FirstPassCount = kpis.ApprovedWithoutRejectCount,
            ApprovedWithoutRejectCount = kpis.ApprovedWithoutRejectCount,
            FirstPassYield = kpis.InspectedCount == 0
                ? 0m
                : IvQty.Round((decimal)kpis.ApprovedWithoutRejectCount / kpis.InspectedCount),
            FirstPassYieldNote = QaCaseMessages.ApprovedWithoutRejectNote,
            RecordOnlyNote = QaCaseMessages.InquiryRecordOnly,
            FollowUpReady = followUpReady,
            Rows = inquiryRows,
            TotalCount = kpis.TotalCount
        };
    }

    private static async Task<InquiryKpis> ComputeInquiryKpisAsync(
        IQueryable<QaCase> filtered,
        bool followUpReady,
        CancellationToken cancellationToken)
    {
        var rejectKind = QaDispositionKinds.Reject;
        var total = await filtered.CountAsync(cancellationToken);
        var open = await filtered.CountAsync(
            x => x.Status != QaCaseStatuses.Closed && x.Status != QaCaseStatuses.Cancelled,
            cancellationToken);
        var pendingInspection = await filtered.CountAsync(
            x => x.Status == QaCaseStatuses.Submitted || x.Status == QaCaseStatuses.InInspection,
            cancellationToken);
        var pendingReview = await filtered.CountAsync(
            x => x.Status == QaCaseStatuses.PendingReview,
            cancellationToken);
        var inspected = await filtered.CountAsync(
            x => x.InspectedAtUtc != null
                || x.Status == QaCaseStatuses.InInspection
                || x.Status == QaCaseStatuses.PendingReview
                || x.Status == QaCaseStatuses.Approved
                || x.Status == QaCaseStatuses.Rejected
                || x.Status == QaCaseStatuses.Closed,
            cancellationToken);
        var approvedWithoutReject = await filtered.CountAsync(
            x => (x.Status == QaCaseStatuses.Approved || x.Status == QaCaseStatuses.Closed)
                && !x.Dispositions.Any(d => d.Kind == rejectKind && d.Qty > 0m),
            cancellationToken);
        var openDestinations = await filtered.CountAsync(
            x => (x.Status == QaCaseStatuses.Approved || x.Status == QaCaseStatuses.Rejected)
                && x.Dispositions.Any(d => d.Kind == rejectKind && d.Qty > 0m),
            cancellationToken);
        var rejectedQty = await filtered
            .SelectMany(x => x.Dispositions)
            .Where(d => d.Kind == rejectKind)
            .SumAsync(d => (decimal?)d.Qty, cancellationToken) ?? 0m;

        var overdueActions = 0;
        var completedActions = 0;
        if (followUpReady)
        {
            var today = DateTime.Today;
            overdueActions = await filtered
                .SelectMany(x => x.CorrectiveActions)
                .CountAsync(
                    a => (a.Status == QaActionStatuses.Open || a.Status == QaActionStatuses.InProgress)
                        && a.DueDate != null
                        && a.DueDate < today,
                    cancellationToken);
            completedActions = await filtered
                .SelectMany(x => x.CorrectiveActions)
                .CountAsync(a => a.Status == QaActionStatuses.Done, cancellationToken);
        }

        var closed = await filtered
            .Where(x => x.Status == QaCaseStatuses.Closed)
            .Select(x => new { x.CreatedDate, ClosedAt = x.ModifiedDate ?? x.CreatedDate })
            .ToListAsync(cancellationToken);
        var closeDays = closed
            .Select(x => Math.Max(0, (x.ClosedAt.Date - x.CreatedDate.Date).Days))
            .ToList();

        return new InquiryKpis(
            total,
            open,
            pendingInspection,
            pendingReview,
            openDestinations,
            IvQty.Round(rejectedQty),
            overdueActions,
            completedActions,
            closeDays.Count == 0 ? null : Math.Round((decimal)closeDays.Average(), 2),
            QaInquiryQuerying.Median(closeDays),
            inspected,
            approvedWithoutReject);
    }

    private static QaInquiryRow MapInquiryRow(QaCase entity, DateTime today, bool followUpReady)
    {
        var destinations = QaDestinationComposer.Compose(entity);
        var rejected = QaDestinationComposer.RejectedQty(entity);
        var accepted = IvQty.Round(
            entity.Grants.Sum(x => x.AcceptedQty)
            + entity.FgGrants.Sum(x => x.AcceptedQty)
            + entity.NonGoodGrants.Sum(x => x.AcceptedQty));
        if (accepted == 0m)
        {
            accepted = IvQty.Round(entity.Lines.SelectMany(x => x.Dispositions)
                .Where(x => string.Equals(x.Kind, QaDispositionKinds.Accept, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Qty));
        }

        var posted = IvQty.Round(
            entity.Grants.SelectMany(x => x.Allocations)
                .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                .Sum(x => x.Qty)
            + entity.FgGrants.SelectMany(x => x.Allocations)
                .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                .Sum(x => x.Qty)
            + entity.NonGoodGrants.SelectMany(x => x.Allocations)
                .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                .Sum(x => x.Qty));
        return new QaInquiryRow
        {
            CaseNo = entity.CaseNo,
            CaseDate = entity.CaseDate,
            Status = entity.Status,
            SourceType = entity.SourceType,
            SourceDocNo = entity.SourceDocNo,
            SupplierName = entity.SupplierName,
            WorkOrderNo = entity.WorkOrderNo,
            Inspector = entity.InspectedBy,
            Reviewer = entity.ReviewedBy,
            AcceptedQty = accepted,
            RejectedQty = rejected,
            PostedQty = posted,
            AgingDays = QaDestinationComposer.AgingDays(entity, today),
            OpenActionCount = followUpReady
                ? entity.CorrectiveActions.Count(x => QaActionStatuses.IsOpen(x.Status))
                : 0,
            DestinationSummary = QaDestinationComposer.Summary(destinations),
            Destinations = destinations
        };
    }

    private readonly record struct InquiryKpis(
        int TotalCount,
        int OpenCaseCount,
        int PendingInspectionCount,
        int PendingReviewCount,
        int OpenDestinationCount,
        decimal RejectedDecisionQty,
        int OverdueActionCount,
        int CompletedActionCount,
        decimal? AverageCloseDays,
        decimal? MedianCloseDays,
        int InspectedCount,
        int ApprovedWithoutRejectCount);

    private InventoryTenantScope? RequireBranch()
    {
        var scope = _tenant.TryBranchScope() ?? _tenant.TryWriteScope();
        return scope is null || string.IsNullOrWhiteSpace(scope.BranchCode) ? null : scope;
    }

    private InventoryTenantScope? RequireWrite()
    {
        var scope = _tenant.TryWriteScope() ?? _tenant.TryBranchScope();
        return scope is null || string.IsNullOrWhiteSpace(scope.BranchCode) ? null : scope;
    }

    private Task<bool> CanAsync(string permission, CancellationToken cancellationToken) =>
        _accessRights.CanAsync(MenuCodes.QualityWorkbench, permission, cancellationToken);

    private static async Task<QaResult?> SchemaErrorAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var error = await QaSchemaReadiness.RequireCaseGraphAsync(db, cancellationToken);
        return error is null ? null : QaResult.Fail(error);
    }

    private static async Task<QaCase?> LoadCaseAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        string? caseNo,
        CancellationToken cancellationToken)
    {
        var no = (caseNo ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(no))
        {
            return null;
        }

        IQueryable<QaCase> query = db.QaCases
            .Include(x => x.Lines)
            .ThenInclude(x => x.Inspections)
            .Include(x => x.Lines)
            .ThenInclude(x => x.Dispositions)
            .Include(x => x.Inspections)
            .Include(x => x.Dispositions)
            .Include(x => x.AuditEvents)
            .Include(x => x.Grants)
            .ThenInclude(x => x.Allocations)
            .Include(x => x.FgGrants)
            .ThenInclude(x => x.Allocations)
            .Include(x => x.LogicalHolds)
            .Include(x => x.PhysicalHolds)
            .ThenInclude(x => x.Allocations)
            .Include(x => x.NonGoodGrants)
            .ThenInclude(x => x.Allocations);
        var ready = await QaSchemaReadiness.EvaluateAsync(db, cancellationToken);
        if (ready.CanQueryFollowUp)
        {
            query = query
                .Include(x => x.Lines)
                .ThenInclude(x => x.CorrectiveActions)
                .Include(x => x.Lines)
                .ThenInclude(x => x.InspectionAttempts)
                .Include(x => x.CorrectiveActions)
                .Include(x => x.InspectionAttempts);
        }

        return await query.FirstOrDefaultAsync(
            x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.CaseNo == no,
            cancellationToken);
    }

    private static async Task<QaResult?> FollowUpSchemaErrorAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        if (await SchemaErrorAsync(db, cancellationToken) is { } graphError)
        {
            return graphError;
        }

        var ready = await QaSchemaReadiness.EvaluateAsync(db, cancellationToken);
        return ready.CanQueryFollowUp ? null : QaResult.Fail(QaCaseMessages.FollowUpNotInstalled);
    }

    private async Task<QaResult> MutateActionAsync(
        QaActionUpdateCommand command,
        string? toStatus,
        string audit,
        bool requireReason,
        CancellationToken cancellationToken)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return QaResult.Fail("Not authorized.");
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return QaResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (await FollowUpSchemaErrorAsync(db, cancellationToken) is { } schemaError)
        {
            return schemaError;
        }

        var entity = await LoadCaseAsync(db, scope, command.CaseNo, cancellationToken);
        if (entity is null)
        {
            return QaResult.Fail("Quality case was not found.");
        }

        if (Stale(entity, command.RowVersion))
        {
            return QaResult.Fail(QaCaseMessages.ChangedReload);
        }

        var action = entity.CorrectiveActions.FirstOrDefault(x => x.ActionNo == command.ActionNo);
        if (action is null)
        {
            return QaResult.Fail("Follow-up task was not found.");
        }

        if (requireReason && string.IsNullOrWhiteSpace(command.Reason) && string.IsNullOrWhiteSpace(command.Remarks))
        {
            return QaResult.Fail("Enter a reason.");
        }

        if (toStatus is QaActionStatuses.InProgress or QaActionStatuses.Done or QaActionStatuses.Cancelled
            && !QaActionStatuses.IsOpen(action.Status))
        {
            return QaResult.Fail("This follow-up task is already closed.");
        }

        if (toStatus == QaActionStatuses.InProgress)
        {
            action.Status = QaActionStatuses.InProgress;
        }
        else if (toStatus == QaActionStatuses.Done)
        {
            action.Status = QaActionStatuses.Done;
            action.CompletedAtUtc = DateTime.UtcNow;
            action.CompletedBy = TruncateUser(scope.UserId);
            action.CompletionEvidence = TrimTo(command.CompletionEvidence, 500);
        }
        else if (toStatus == QaActionStatuses.Cancelled)
        {
            action.Status = QaActionStatuses.Cancelled;
            action.CancelledAtUtc = DateTime.UtcNow;
            action.CancelledBy = TruncateUser(scope.UserId);
            action.CancelReason = TrimTo(command.Reason ?? command.Remarks, 250);
        }

        if (!string.IsNullOrWhiteSpace(command.AssignedTo) || audit == QaAuditEvents.ActionReassigned)
        {
            action.AssignedTo = TrimTo(command.AssignedTo, 20);
            action.AssignedDepartment = TrimTo(command.AssignedDepartment, 30);
        }

        action.Remarks = TrimTo(command.Remarks, 500) ?? action.Remarks;
        action.ModifiedDate = DateTime.UtcNow;
        action.ModifiedBy = TruncateUser(scope.UserId);
        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = TruncateUser(scope.UserId);
        AddAudit(entity, audit, scope.UserId, command.Reason ?? command.Remarks);
        return await CommitCaseAsync(db, entity, cancellationToken);
    }

    private async Task<string> AllocateCaseNoAsync(AppDbContext db, string company, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 10; i++)
        {
            var seq = await _runningNumbers.GetNextAsync(db, company, RunningNumberKeys.QualityCase, cancellationToken);
            var number = $"QC{seq:D8}";
            if (!await db.QaCases.AsNoTracking().AnyAsync(x => x.CompanyCode == company && x.CaseNo == number, cancellationToken))
            {
                return number;
            }
        }

        throw new InvalidOperationException("Unable to allocate a quality case number.");
    }

    private static string? ValidateDraft(QaCaseSaveCommand command)
    {
        if (!QaSourceTypes.IsKnown(command.SourceType))
        {
            return "Select a quality source type.";
        }

        if (command.Lines.Count == 0)
        {
            return "Add at least one line.";
        }

        var seen = new HashSet<int>();
        foreach (var line in command.Lines)
        {
            if (line.LineNo <= 0 || !seen.Add(line.LineNo))
            {
                return "Each line needs a unique line number.";
            }

            if (string.IsNullOrWhiteSpace(line.ItemCode))
            {
                return $"Item is required on line {line.LineNo}.";
            }

            if (string.IsNullOrWhiteSpace(line.Uom))
            {
                return $"UOM is required on line {line.LineNo}.";
            }

            if (IvQty.Round(line.Qty) <= 0m)
            {
                return $"Quantity on line {line.LineNo} must be greater than zero.";
            }
        }

        if (string.Equals(command.SourceType, QaSourceTypes.IncomingPreGrn, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(command.PoNo)
            && string.IsNullOrWhiteSpace(command.DeliveryNote))
        {
            return "Incoming cases need a PO number or a delivery note (the note is not a server identity).";
        }

        return null;
    }

    private static string? ValidateDispositions(IReadOnlyList<QaDispositionDto> dispositions, decimal inspected, int lineNo)
    {
        if (dispositions.Count == 0)
        {
            return $"Split the inspected quantity on line {lineNo} into accept / reject / rework / hold.";
        }

        decimal total = 0m;
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in dispositions)
        {
            if (!QaDispositionKinds.IsKnown(row.Kind))
            {
                return $"Disposition on line {lineNo} is not valid.";
            }

            if (!kinds.Add(row.Kind))
            {
                return $"Each disposition kind can appear once on line {lineNo}.";
            }

            var qty = IvQty.Round(row.Qty);
            if (qty < 0m)
            {
                return $"Disposition quantity on line {lineNo} cannot be negative.";
            }

            total += qty;
        }

        if (IvQty.Round(total) != inspected)
        {
            return $"Disposition quantities on line {lineNo} must equal the inspected quantity.";
        }

        return null;
    }

    private static void ApplyHeader(QaCase entity, QaCaseSaveCommand command, string companyCode)
    {
        var source = command.SourceType.Trim().ToUpperInvariant();
        entity.CaseDate = command.CaseDate == default ? DateTime.UtcNow.Date : command.CaseDate.Date;
        entity.SourceType = source;
        entity.SourceDocType = TrimTo(command.SourceDocType, 10);
        entity.SourceDocNo = TrimTo(command.SourceDocNo, 30);
        entity.SourceHeaderId = command.SourceHeaderId;
        entity.SourceDocumentRevision = command.SourceDocumentRevision;
        entity.SupplierCode = TrimTo(command.SupplierCode, 20);
        entity.SupplierName = TrimTo(command.SupplierName, 100);
        entity.DeliveryNote = TrimTo(command.DeliveryNote, 50);
        entity.PoNo = TrimTo(command.PoNo, 30);
        entity.PoRelNo = command.PoRelNo;
        entity.WorkOrderNo = TrimTo(command.WorkOrderNo, 30);
        entity.ProductionBalLotId = command.ProductionBalLotId;
        entity.SourceHref = TrimTo(command.SourceHref, 200);
        entity.EligibilityNote = QaEligibilityNotes.ForSource(source);
        entity.Remarks = TrimTo(command.Remarks, 500);
        entity.PolicyVersion = 0;
        _ = companyCode;
    }

    private static void ReplaceLines(QaCase entity, IReadOnlyList<QaCaseLineSaveCommand> lines)
    {
        entity.Lines.Clear();
        foreach (var line in lines.OrderBy(x => x.LineNo))
        {
            entity.Lines.Add(new QaCaseLine
            {
                LineNo = line.LineNo,
                ItemCode = line.ItemCode.Trim(),
                ItemName = TrimTo(line.ItemName, 100),
                Uom = line.Uom.Trim(),
                Qty = IvQty.Round(line.Qty),
                InspectedQty = 0m,
                LotNo = TrimTo(line.LotNo, 50),
                WarehouseCode = TrimTo(line.WarehouseCode, 10),
                LocationCode = TrimTo(line.LocationCode, 10),
                ItemStatus = TrimTo(line.ItemStatus, 10),
                SourceLineId = line.SourceLineId,
                PoLineNo = line.PoLineNo,
                SourceIdentity = TrimTo(line.SourceIdentity, 200),
                LineEligibilityNote = QaEligibilityNotes.ForSource(entity.SourceType),
                Remarks = TrimTo(line.Remarks, 500)
            });
        }
    }

    private static void ReplaceDispositions(
        AppDbContext db,
        QaCase entity,
        QaCaseLine line,
        IReadOnlyList<QaDispositionDto> dispositions)
    {
        var incoming = dispositions
            .Where(x => IvQty.Round(x.Qty) > 0m)
            .Select(x => new
            {
                Kind = x.Kind.Trim().ToUpperInvariant(),
                Qty = IvQty.Round(x.Qty),
                ReasonCode = TrimTo(x.ReasonCode, 30),
                Remarks = TrimTo(x.Remarks, 500)
            })
            .ToList();
        var existing = entity.Dispositions
            .Where(x => ReferenceEquals(x.CaseLine, line) || x.CaseLineId == line.Uid)
            .ToList();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in existing)
        {
            var match = incoming.FirstOrDefault(x => string.Equals(x.Kind, row.Kind, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                db.QaDispositionLines.Remove(row);
                entity.Dispositions.Remove(row);
                line.Dispositions.Remove(row);
                continue;
            }

            row.Qty = match.Qty;
            row.ReasonCode = match.ReasonCode;
            row.Remarks = match.Remarks;
            consumed.Add(row.Kind);
        }

        foreach (var row in incoming.Where(x => !consumed.Contains(x.Kind)))
        {
            var created = new QaDispositionLine
            {
                Case = entity,
                CaseLine = line,
                Kind = row.Kind,
                Qty = row.Qty,
                ReasonCode = row.ReasonCode,
                Remarks = row.Remarks
            };
            entity.Dispositions.Add(created);
            line.Dispositions.Add(created);
        }
    }

    private static void IssueIncomingGrants(QaCase entity, int policyVersion)
    {
        var deliveryId = entity.DeliveryInstanceId ?? Guid.NewGuid();
        entity.DeliveryInstanceId = deliveryId;
        foreach (var line in entity.Lines.OrderBy(x => x.LineNo))
        {
            if (entity.Grants.Any(x => x.CaseLineId == line.Uid || ReferenceEquals(x.CaseLine, line)))
            {
                continue;
            }

            var accepted = IvQty.Round(line.Dispositions
                .Where(x => string.Equals(x.Kind, QaDispositionKinds.Accept, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Qty));
            var rejected = IvQty.Round(line.Dispositions
                .Where(x => string.Equals(x.Kind, QaDispositionKinds.Reject, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Qty));
            entity.Grants.Add(new QaReceiptGrant
            {
                CompanyCode = entity.CompanyCode,
                BranchCode = entity.BranchCode,
                Case = entity,
                CaseLine = line,
                DeliveryInstanceId = deliveryId,
                LineKey = line.LineNo,
                PoNo = entity.PoNo,
                PoRelNo = entity.PoRelNo,
                PoLineNo = line.PoLineNo,
                SupplierCode = entity.SupplierCode,
                DeliveryNote = entity.DeliveryNote,
                ItemCode = line.ItemCode,
                Uom = line.Uom,
                LotNo = line.LotNo,
                AcceptedQty = accepted,
                RejectedQty = rejected,
                PolicyVersion = policyVersion,
                Status = QaReceiptGrantStatuses.Open,
                ApprovedAtUtc = DateTime.UtcNow
            });
        }
    }

    private static void IssueFgGrants(QaCase entity, int policyVersion)
    {
        foreach (var line in entity.Lines.OrderBy(x => x.LineNo))
        {
            if (entity.FgGrants.Any(x => x.CaseLineId == line.Uid || ReferenceEquals(x.CaseLine, line)))
            {
                continue;
            }

            var accepted = IvQty.Round(line.Dispositions
                .Where(x => string.Equals(x.Kind, QaDispositionKinds.Accept, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Qty));
            var rejected = IvQty.Round(line.Dispositions
                .Where(x => string.Equals(x.Kind, QaDispositionKinds.Reject, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Qty));
            entity.FgGrants.Add(new QaFgGrant
            {
                CompanyCode = entity.CompanyCode,
                BranchCode = entity.BranchCode,
                Case = entity,
                CaseLine = line,
                ProductionBalLotId = entity.ProductionBalLotId ?? 0,
                LineKey = line.LineNo,
                WorkOrderNo = entity.WorkOrderNo,
                ItemCode = line.ItemCode,
                Uom = line.Uom,
                LotNo = line.LotNo,
                AcceptedQty = accepted,
                RejectedQty = rejected,
                PolicyVersion = policyVersion,
                Status = QaReceiptGrantStatuses.Open,
                ApprovedAtUtc = DateTime.UtcNow
            });
        }
    }

    private static void IssueNonGoodGrants(QaCase entity, int policyVersion)
    {
        foreach (var line in entity.Lines.OrderBy(x => x.LineNo))
        {
            if (entity.NonGoodGrants.Any(x => x.CaseLineId == line.Uid || ReferenceEquals(x.CaseLine, line)))
            {
                continue;
            }

            var accepted = IvQty.Round(line.Dispositions
                .Where(x => string.Equals(x.Kind, QaDispositionKinds.Accept, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Qty));
            var rejected = IvQty.Round(line.Dispositions
                .Where(x => string.Equals(x.Kind, QaDispositionKinds.Reject, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Qty));
            entity.NonGoodGrants.Add(new QaNonGoodGrant
            {
                CompanyCode = entity.CompanyCode,
                BranchCode = entity.BranchCode,
                Case = entity,
                CaseLine = line,
                ProductionOutputId = entity.SourceHeaderId ?? 0,
                LineKey = line.LineNo,
                WorkOrderNo = entity.WorkOrderNo,
                DocumentNo = entity.SourceDocNo,
                ItemCode = line.ItemCode,
                Uom = line.Uom,
                LotNo = line.LotNo,
                AcceptedQty = accepted,
                RejectedQty = rejected,
                PolicyVersion = policyVersion,
                Status = QaReceiptGrantStatuses.Open,
                ApprovedAtUtc = DateTime.UtcNow
            });
        }
    }

    private static void AddAudit(QaCase entity, string eventType, string user, string? payload)
    {
        entity.AuditEvents.Add(new QaAuditEvent
        {
            EventType = eventType,
            AtUtc = DateTime.UtcNow,
            ByUser = TruncateUser(user),
            Payload = TrimTo(payload, 1000)
        });
    }

    private static bool InspectorMayNotReview(QaCase entity, string userId, QaPolicy? policy)
    {
        if (!(policy?.InspectorApproverSeparation ?? true))
        {
            return false;
        }

        var reviewer = TruncateUser(userId);
        var inspectors = entity.Inspections
            .Select(x => x.Inspector)
            .Concat(entity.InspectionAttempts.Select(x => x.Inspector))
            .Append(entity.InspectedBy)
            .Where(x => !string.IsNullOrWhiteSpace(x));
        return inspectors.Any(x => string.Equals(x, reviewer, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<QaResult> CommitCaseAsync(
        AppDbContext db,
        QaCase entity,
        CancellationToken cancellationToken,
        IDbContextTransaction? transaction = null)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            var followUp = await QaSchemaReadiness.EvaluateAsync(db, cancellationToken);
            return QaResult.OkCase(Map(entity, followUp.CanQueryFollowUp));
        }
        catch (DbUpdateConcurrencyException)
        {
            return QaResult.Fail(QaCaseMessages.ChangedReload);
        }
    }

    private static bool Stale(QaCase entity, byte[]? rowVersion)
    {
        if (entity.RowVersion is not { Length: > 0 })
        {
            return false;
        }

        return rowVersion is not { Length: > 0 }
            || !entity.RowVersion.SequenceEqual(rowVersion);
    }

    private static QaCaseDto Map(QaCase entity, bool followUpReady = false, string? sourceCheckNote = null)
    {
        var lines = entity.Lines.OrderBy(x => x.LineNo).Select(l => MapLine(l, entity)).ToList();
        var actions = entity.CorrectiveActions.OrderBy(x => x.ActionNo).Select(x => new QaActionDto
        {
            ActionNo = x.ActionNo,
            LineNo = entity.Lines.FirstOrDefault(l => l.Uid == x.CaseLineId || ReferenceEquals(l, x.CaseLine))?.LineNo ?? 0,
            ActionType = x.ActionType,
            Qty = x.Qty,
            Status = x.Status,
            AssignedTo = x.AssignedTo,
            AssignedDepartment = x.AssignedDepartment,
            Reason = x.Reason,
            DueDate = x.DueDate,
            Remarks = x.Remarks,
            CompletionEvidence = x.CompletionEvidence,
            RequestId = x.RequestId,
            RowVersion = x.RowVersion
        }).ToList();
        var attempts = entity.InspectionAttempts.OrderBy(x => x.AttemptNo).Select(x => new QaAttemptDto
        {
            LineNo = entity.Lines.FirstOrDefault(l => l.Uid == x.CaseLineId || ReferenceEquals(l, x.CaseLine))?.LineNo ?? 0,
            AttemptNo = x.AttemptNo,
            Result = x.Result,
            Inspector = x.Inspector,
            InspectedAtUtc = x.InspectedAtUtc,
            InspectedQty = x.InspectedQty,
            Notes = x.Notes,
            DispositionSnapshot = x.DispositionSnapshot,
            Reason = x.Reason
        }).ToList();
        return new QaCaseDto
        {
        CaseNo = entity.CaseNo,
        CaseDate = entity.CaseDate,
        Status = entity.Status,
        SourceType = entity.SourceType,
        SourceDocType = entity.SourceDocType,
        SourceDocNo = entity.SourceDocNo,
        SourceHeaderId = entity.SourceHeaderId,
        SourceDocumentRevision = entity.SourceDocumentRevision,
        SupplierCode = entity.SupplierCode,
        SupplierName = entity.SupplierName,
        DeliveryNote = entity.DeliveryNote,
        DeliveryInstanceId = entity.DeliveryInstanceId,
        DeliveredQty = lines.Sum(x => x.Qty),
        AcceptedQty = lines.Sum(x => x.AcceptedQty),
        RejectedQty = lines.Sum(x => x.RejectedQty),
        PostedQty = lines.Sum(x => x.PostedQty),
        RemainingQty = lines.Sum(x => x.RemainingQty),
        HeldQty = IvQty.Round(entity.LogicalHolds
            .Where(x => string.Equals(x.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.ReservedBaseQty)),
        PhysicalHeldQty = Math.Max(0m, IvQty.Round(entity.PhysicalHolds
            .Where(x => string.Equals(x.Status, QaLogicalHoldStatuses.Active, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.ReservedBaseQty)
            - entity.PhysicalHolds.SelectMany(x => x.Allocations)
                .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                .Sum(x => x.Qty))),
        PoNo = entity.PoNo,
        PoRelNo = entity.PoRelNo,
        WorkOrderNo = entity.WorkOrderNo,
        ProductionBalLotId = entity.ProductionBalLotId,
        SourceHref = entity.SourceHref,
        EligibilityNote = entity.EligibilityNote,
        Remarks = entity.Remarks,
        PolicyVersion = entity.PolicyVersion,
        SubmittedBy = entity.SubmittedBy,
        SubmittedAtUtc = entity.SubmittedAtUtc,
        InspectedBy = entity.InspectedBy,
        InspectedAtUtc = entity.InspectedAtUtc,
        ReviewedBy = entity.ReviewedBy,
        ReviewedAtUtc = entity.ReviewedAtUtc,
        ReviewDecision = entity.ReviewDecision,
        ReviewRemarks = entity.ReviewRemarks,
        CreatedDate = entity.CreatedDate,
        CreatedBy = entity.CreatedBy,
        ModifiedDate = entity.ModifiedDate,
        ModifiedBy = entity.ModifiedBy,
        RowVersion = entity.RowVersion,
        Lines = lines,
        Audit = entity.AuditEvents.OrderBy(x => x.AtUtc).Select(x => new QaAuditEventDto
        {
            EventType = x.EventType,
            AtUtc = x.AtUtc,
            ByUser = x.ByUser,
            Payload = x.Payload
        }).ToList(),
        Destinations = QaDestinationComposer.Compose(entity),
        Actions = actions,
        Attempts = attempts,
        SourceCheckNote = sourceCheckNote,
        FollowUpReady = followUpReady,
        OpenActionCount = actions.Count(x => x.IsOpen)
    };
    }

    private static QaCaseLineDto MapLine(QaCaseLine line, QaCase entity)
    {
        var inspection = line.Inspections.FirstOrDefault();
        var grant = entity.Grants.FirstOrDefault(x => x.CaseLineId == line.Uid || ReferenceEquals(x.CaseLine, line));
        var fgGrant = entity.FgGrants.FirstOrDefault(x => x.CaseLineId == line.Uid || ReferenceEquals(x.CaseLine, line));
        var nonGoodGrant = entity.NonGoodGrants.FirstOrDefault(x => x.CaseLineId == line.Uid || ReferenceEquals(x.CaseLine, line));
        var accepted = grant?.AcceptedQty ?? fgGrant?.AcceptedQty ?? nonGoodGrant?.AcceptedQty ?? IvQty.Round(line.Dispositions
            .Where(x => string.Equals(x.Kind, QaDispositionKinds.Accept, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Qty));
        var rejected = grant?.RejectedQty ?? fgGrant?.RejectedQty ?? nonGoodGrant?.RejectedQty ?? IvQty.Round(line.Dispositions
            .Where(x => string.Equals(x.Kind, QaDispositionKinds.Reject, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Qty));
        var posted = grant is not null
            ? IvQty.Round(grant.Allocations
                .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                .Sum(x => x.Qty))
            : fgGrant is not null
                ? IvQty.Round(fgGrant.Allocations
                    .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                    .Sum(x => x.Qty))
                : nonGoodGrant is null
                    ? 0m
                    : IvQty.Round(nonGoodGrant.Allocations
                        .Where(x => x.Status == QaReceiptAllocationStatuses.Posted)
                        .Sum(x => x.Qty));
        return new QaCaseLineDto
        {
            Uid = line.Uid,
            LineNo = line.LineNo,
            ItemCode = line.ItemCode,
            ItemName = line.ItemName,
            Uom = line.Uom,
            Qty = line.Qty,
            InspectedQty = line.InspectedQty,
            LotNo = line.LotNo,
            WarehouseCode = line.WarehouseCode,
            LocationCode = line.LocationCode,
            ItemStatus = line.ItemStatus,
            SourceLineId = line.SourceLineId,
            PoLineNo = line.PoLineNo,
            SourceIdentity = line.SourceIdentity,
            LineEligibilityNote = line.LineEligibilityNote,
            Remarks = line.Remarks,
            AcceptedQty = accepted,
            RejectedQty = rejected,
            PostedQty = posted,
            RemainingQty = IvQty.Round(accepted - posted),
            Inspection = inspection is null
                ? null
                : new QaInspectionDto
                {
                    Result = inspection.Result,
                    Inspector = inspection.Inspector,
                    InspectedAtUtc = inspection.InspectedAtUtc,
                    Notes = inspection.Notes,
                    MeasuredValues = inspection.MeasuredValues
                },
            Dispositions = line.Dispositions.Select(x => new QaDispositionDto
            {
                Kind = x.Kind,
                Qty = x.Qty,
                ReasonCode = x.ReasonCode,
                Remarks = x.Remarks
            }).ToList()
        };
    }

    private static string TruncateUser(string userId)
    {
        var value = (userId ?? string.Empty).Trim();
        return value.Length <= 20 ? value : value[..20];
    }

    private static string? TrimTo(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
