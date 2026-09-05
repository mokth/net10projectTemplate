using System.Data;
using System.Data.Common;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ErpWeb.Core.Numbering;

public sealed class AdSmNumAdminService : IAdSmNumAdminService
{
    private const int MaxDocLength = 30;
    private const int MaxLocationLength = 10;
    private const int MaxUserIdLength = 10;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;

    public AdSmNumAdminService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
    }

    // ── Continuous list / get ───────────────────────────────────────────

    public async Task<IvMasterOperationResult<IReadOnlyList<AdSmNumListRow>>> ListContinuousAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminSmNum, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<AdSmNumListRow>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AdSmNums.AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode && x.BranchCode == ctx.BranchCode)
            .OrderBy(x => x.NumCd)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<AdSmNumListRow>>.Ok(
            rows.Select(MapContinuousList).ToList());
    }

    public async Task<IvMasterOperationResult<AdSmNumEditVm>> GetContinuousAsync(
        string numCd,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminSmNum, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<AdSmNumEditVm>(ctx.Error.Value);
        }

        var code = NormalizeNumCd(numCd);
        if (code is null)
        {
            return FailVm<AdSmNumEditVm>(IvMasterErrorCode.Validation, "NumCd is required.", "NumCd");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.AdSmNums.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.BranchCode == ctx.BranchCode && x.NumCd == code,
                cancellationToken);
        if (entity is null)
        {
            return FailVm<AdSmNumEditVm>(IvMasterErrorCode.NotFound, "Continuous numbering not found.");
        }

        return IvMasterOperationResult<AdSmNumEditVm>.Ok(MapContinuousEdit(entity));
    }

    public async Task<IvMasterOperationResult<AdSmNumEditVm>> SaveContinuousAsync(
        AdSmNumEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var branchCtx = await RequireBranchScopeAsync(MenuCodes.AdminSmNum, permission, cancellationToken);
        if (branchCtx.Error is not null)
        {
            return FailVm<AdSmNumEditVm>(branchCtx.Error.Value);
        }

        var write = _tenant.TryWriteScope();
        if (write is null)
        {
            return FailVm<AdSmNumEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        if (model is null)
        {
            return FailVm<AdSmNumEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var errors = ValidateContinuousModel(model, isNew);
        if (errors.Count > 0)
        {
            return IvMasterOperationResult<AdSmNumEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var numCd = NormalizeNumCd(model.NumCd)!;
        var prefix = model.Prefix!.Trim();
        var numDes = Truncate(model.NumDes?.Trim(), 30);
        var location = Truncate(write.LocationCode, MaxLocationLength);
        var userId = Truncate(write.UserId, MaxUserIdLength);
        var company = write.CompanyCode;
        var branch = write.BranchCode!;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await BeginSaveTransactionAsync(db, cancellationToken);
        try
        {
            var hasPeriod = await db.AdSmNumDates.AsNoTracking()
                .AnyAsync(
                    x => x.CompanyCode == company && x.BranchCode == branch && x.NumCd == numCd,
                    cancellationToken);
            if (hasPeriod)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailVm<AdSmNumEditVm>(
                    IvMasterErrorCode.Validation,
                    $"Period numbering already exists for {numCd}. Delete those period rows before creating a continuous series.",
                    "NumCd");
            }

            string formatted;
            try
            {
                formatted = DocumentNumberFormatter.FormatContinuous(prefix, model.Seq, model.TotLength);
                DocumentNumberFormatter.EnsureFitsMaxLength(formatted, MaxDocLength);
            }
            catch (Exception ex) when (ex is DocumentNumberingConfigurationException or DocumentNumberingOverflowException)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailVm<AdSmNumEditVm>(IvMasterErrorCode.Validation, ex.Message, "TotLength");
            }

            if (await InvoiceExistsAsync(db, company, branch, formatted, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return FailVm<AdSmNumEditVm>(
                    IvMasterErrorCode.Validation,
                    $"This configuration would issue {formatted}, which already exists.",
                    "Seq");
            }

            var now = DateTime.UtcNow;

            if (isNew)
            {
                var exists = await db.AdSmNums.AsNoTracking()
                    .AnyAsync(
                        x => x.CompanyCode == company && x.BranchCode == branch && x.NumCd == numCd,
                        cancellationToken);
                if (exists)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailVm<AdSmNumEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        $"Continuous numbering '{numCd}' already exists.",
                        "NumCd");
                }

                db.AdSmNums.Add(new AdSmNum
                {
                    CompanyCode = company,
                    BranchCode = branch,
                    LocationCode = location,
                    NumCd = numCd,
                    NumDes = numDes,
                    TotLength = model.TotLength,
                    Prefix = prefix,
                    Seq = model.Seq,
                    Created = now,
                    Updated = now,
                    UserID = userId,
                    UpdatedUID = userId
                });

                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                return IvMasterOperationResult<AdSmNumEditVm>.Ok(new AdSmNumEditVm
                {
                    NumCd = numCd,
                    Prefix = prefix,
                    TotLength = model.TotLength,
                    Seq = model.Seq,
                    NumDes = numDes,
                    OriginalSeq = model.Seq
                });
            }

            var current = await db.AdSmNums
                .SingleOrDefaultAsync(
                    x => x.CompanyCode == company && x.BranchCode == branch && x.NumCd == numCd,
                    cancellationToken);
            if (current is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailVm<AdSmNumEditVm>(IvMasterErrorCode.NotFound, "Continuous numbering not found.");
            }

            var originalSeq = current.Seq;

            if (originalSeq > 1)
            {
                if (!string.Equals(current.Prefix ?? string.Empty, prefix, StringComparison.Ordinal)
                    || current.TotLength != model.TotLength)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailVm<AdSmNumEditVm>(
                        IvMasterErrorCode.Validation,
                        "Prefix and total length cannot be changed after numbers have been issued. Create a new series.",
                        "Prefix");
                }
            }

            if (model.Seq < originalSeq)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailVm<AdSmNumEditVm>(
                    IvMasterErrorCode.Validation,
                    "Next sequence cannot be reduced.",
                    "Seq");
            }

            // Re-format after freeze/seq checks (same as proposed — already done, but keep invariant clear)
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE AdSmNum
SET Prefix = {prefix}, TotLength = {model.TotLength}, Seq = {model.Seq}, NumDes = {numDes},
    LocationCode = {location}, Updated = {now}, UpdatedUID = {userId}
WHERE CompanyCode = {company} AND BranchCode = {branch} AND NumCd = {numCd} AND Seq = {originalSeq}",
                cancellationToken);

            if (affected == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailVm<AdSmNumEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "This record was changed by another user. Reload and try again.");
            }

            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<AdSmNumEditVm>.Ok(new AdSmNumEditVm
            {
                NumCd = numCd,
                Prefix = prefix,
                TotLength = model.TotLength,
                Seq = model.Seq,
                NumDes = numDes,
                OriginalSeq = model.Seq
            });
        }
        catch (Exception ex) when (IsSerializationConflict(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm<AdSmNumEditVm>(
                IvMasterErrorCode.Concurrency,
                "This record was changed by another user. Reload and try again.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm<AdSmNumEditVm>(
                IvMasterErrorCode.DuplicateKey,
                $"Continuous numbering '{numCd}' already exists.",
                "NumCd");
        }
    }

    public async Task<DeleteCheckResult> CanDeleteContinuousAsync(
        IReadOnlyList<string> numCds,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminSmNum, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        if (numCds is null || numCds.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        return DeleteCheckResult.Ok();
    }

    public async Task<IvMasterOperationResult<object>> DeleteContinuousAsync(
        IReadOnlyList<string> numCds,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminSmNum, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var codes = (numCds ?? [])
            .Select(NormalizeNumCd)
            .Where(c => c is not null)
            .Select(c => c!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (codes.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await db.AdSmNums
                .Where(x => x.CompanyCode == ctx.CompanyCode
                    && x.BranchCode == ctx.BranchCode
                    && codes.Contains(x.NumCd))
                .ToListAsync(cancellationToken);

            if (entities.Count == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailObj(IvMasterErrorCode.NotFound, "Continuous numbering not found.");
            }

            db.AdSmNums.RemoveRange(entities);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ── Period list / get ───────────────────────────────────────────────

    public async Task<IvMasterOperationResult<IReadOnlyList<AdSmNumDateListRow>>> ListPeriodAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminSmNumDate, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<AdSmNumDateListRow>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AdSmNumDates.AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode && x.BranchCode == ctx.BranchCode)
            .OrderBy(x => x.NumCd)
            .ThenByDescending(x => x.Year)
            .ThenByDescending(x => x.Month)
            .ThenByDescending(x => x.Uid)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<AdSmNumDateListRow>>.Ok(
            rows.Select(MapPeriodList).ToList());
    }

    public async Task<IvMasterOperationResult<AdSmNumDateEditVm>> GetPeriodAsync(
        int uid,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminSmNumDate, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<AdSmNumDateEditVm>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.AdSmNumDates.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Uid == uid && x.CompanyCode == ctx.CompanyCode && x.BranchCode == ctx.BranchCode,
                cancellationToken);
        if (entity is null)
        {
            return FailVm<AdSmNumDateEditVm>(IvMasterErrorCode.NotFound, "Period numbering not found.");
        }

        return IvMasterOperationResult<AdSmNumDateEditVm>.Ok(MapPeriodEdit(entity));
    }

    public async Task<IvMasterOperationResult<AdSmNumDateEditVm>> SavePeriodAsync(
        AdSmNumDateEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var branchCtx = await RequireBranchScopeAsync(MenuCodes.AdminSmNumDate, permission, cancellationToken);
        if (branchCtx.Error is not null)
        {
            return FailVm<AdSmNumDateEditVm>(branchCtx.Error.Value);
        }

        var write = _tenant.TryWriteScope();
        if (write is null)
        {
            return FailVm<AdSmNumDateEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        if (model is null)
        {
            return FailVm<AdSmNumDateEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var errors = ValidatePeriodModel(model, isNew);
        if (errors.Count > 0)
        {
            return IvMasterOperationResult<AdSmNumDateEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var numCd = NormalizeNumCd(model.NumCd)!;
        var prefix = model.Prefix!.Trim();
        var year = model.Year;
        var month = model.Month;
        var numDes = Truncate(model.NumDes?.Trim(), 30);
        var delimiter = Truncate(model.NumberingDelimeter?.Trim(), 5);
        var format = Truncate(model.NumberingFormat?.Trim(), 50);
        if (string.IsNullOrWhiteSpace(format))
        {
            format = null;
        }

        var location = Truncate(write.LocationCode, MaxLocationLength);
        var userId = Truncate(write.UserId, MaxUserIdLength);
        var company = write.CompanyCode;
        var branch = write.BranchCode!;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await BeginSaveTransactionAsync(db, cancellationToken);
        try
        {
            var hasContinuous = await db.AdSmNums.AsNoTracking()
                .AnyAsync(
                    x => x.CompanyCode == company && x.BranchCode == branch && x.NumCd == numCd,
                    cancellationToken);
            if (hasContinuous)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailVm<AdSmNumDateEditVm>(
                    IvMasterErrorCode.Validation,
                    $"Continuous numbering already exists for {numCd}. Delete that continuous row before creating a period series.",
                    "NumCd");
            }

            string formatted;
            try
            {
                formatted = FormatPeriodNext(prefix, model.Seq, model.TotLength, delimiter, format, year, month);
                DocumentNumberFormatter.EnsureFitsMaxLength(formatted, MaxDocLength);
            }
            catch (Exception ex) when (ex is DocumentNumberingConfigurationException or DocumentNumberingOverflowException)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailVm<AdSmNumDateEditVm>(IvMasterErrorCode.Validation, ex.Message, "TotLength");
            }

            if (await InvoiceExistsAsync(db, company, branch, formatted, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return FailVm<AdSmNumDateEditVm>(
                    IvMasterErrorCode.Validation,
                    $"This configuration would issue {formatted}, which already exists.",
                    "Seq");
            }

            var now = DateTime.UtcNow;

            if (isNew)
            {
                var dup = await db.AdSmNumDates.AsNoTracking()
                    .AnyAsync(
                        x => x.CompanyCode == company
                            && x.BranchCode == branch
                            && x.NumCd == numCd
                            && x.Year == year
                            && x.Month == month,
                        cancellationToken);
                if (dup)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailVm<AdSmNumDateEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        $"Period numbering for {numCd} Year={year} Month={month} already exists.",
                        "Year");
                }

                var entity = new AdSmNumDate
                {
                    CompanyCode = company,
                    BranchCode = branch,
                    LocationCode = location,
                    Year = year,
                    Month = month,
                    NumCd = numCd,
                    NumDes = numDes,
                    TotLength = model.TotLength,
                    Prefix = prefix,
                    Seq = model.Seq,
                    Created = now,
                    Updated = now,
                    UserID = userId,
                    NumberingDelimeter = delimiter,
                    NumberingFormat = format
                };
                EnsureRowVersionForSqlite(db, entity);
                db.AdSmNumDates.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return IvMasterOperationResult<AdSmNumDateEditVm>.Ok(MapPeriodEdit(entity));
            }

            var current = await db.AdSmNumDates
                .SingleOrDefaultAsync(
                    x => x.Uid == model.Uid
                        && x.CompanyCode == company
                        && x.BranchCode == branch,
                    cancellationToken);
            if (current is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailVm<AdSmNumDateEditVm>(IvMasterErrorCode.NotFound, "Period numbering not found.");
            }

            if (!RowVersionsEqual(current.RowVersion, model.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return FailVm<AdSmNumDateEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "This record was changed by another user. Reload and try again.");
            }

            var originalSeq = current.Seq ?? 0;
            if (originalSeq > 1)
            {
                if (!string.Equals(current.Prefix ?? string.Empty, prefix, StringComparison.Ordinal)
                    || (current.TotLength ?? 0) != model.TotLength
                    || !string.Equals(current.NumberingDelimeter ?? string.Empty, delimiter ?? string.Empty, StringComparison.Ordinal)
                    || !string.Equals(current.NumberingFormat ?? string.Empty, format ?? string.Empty, StringComparison.Ordinal))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailVm<AdSmNumDateEditVm>(
                        IvMasterErrorCode.Validation,
                        "Prefix, format, delimiter, and sequence digits cannot be changed after numbers have been issued. Create a new series or period.",
                        "Prefix");
                }
            }

            if (model.Seq < originalSeq)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailVm<AdSmNumDateEditVm>(
                    IvMasterErrorCode.Validation,
                    "Next sequence cannot be reduced.",
                    "Seq");
            }

            // NumCd / Year / Month immutable — ignore proposed changes
            db.Entry(current).Property(x => x.RowVersion).OriginalValue = model.RowVersion!;
            current.Prefix = prefix;
            current.TotLength = model.TotLength;
            current.Seq = model.Seq;
            current.NumDes = numDes;
            current.NumberingDelimeter = delimiter;
            current.NumberingFormat = format;
            current.LocationCode = location;
            current.UserID = userId;
            current.Updated = now;
            BumpRowVersionForSqlite(db, current);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailVm<AdSmNumDateEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "This record was changed by another user. Reload and try again.");
            }

            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<AdSmNumDateEditVm>.Ok(MapPeriodEdit(current));
        }
        catch (Exception ex) when (IsSerializationConflict(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm<AdSmNumDateEditVm>(
                IvMasterErrorCode.Concurrency,
                "This record was changed by another user. Reload and try again.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm<AdSmNumDateEditVm>(
                IvMasterErrorCode.DuplicateKey,
                $"Period numbering for {numCd} Year={year} Month={month} already exists.",
                "Year");
        }
    }

    public async Task<DeleteCheckResult> CanDeletePeriodAsync(
        IReadOnlyList<AdSmNumDateKey> keys,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminSmNumDate, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        if (keys is null || keys.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        return DeleteCheckResult.Ok();
    }

    public async Task<IvMasterOperationResult<object>> DeletePeriodAsync(
        IReadOnlyList<AdSmNumDateKey> keys,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireBranchScopeAsync(MenuCodes.AdminSmNumDate, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var list = (keys ?? []).Where(k => k.Uid > 0).ToList();
        if (list.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var key in list)
            {
                var entity = await db.AdSmNumDates
                    .SingleOrDefaultAsync(
                        x => x.Uid == key.Uid
                            && x.CompanyCode == ctx.CompanyCode
                            && x.BranchCode == ctx.BranchCode,
                        cancellationToken);
                if (entity is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailObj(IvMasterErrorCode.NotFound, "Period numbering not found.");
                }

                if (!RowVersionsEqual(entity.RowVersion, key.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailObj(
                        IvMasterErrorCode.Concurrency,
                        "This record was changed by another user. Reload and try again.");
                }

                db.Entry(entity).Property(x => x.RowVersion).OriginalValue = key.RowVersion;
                db.AdSmNumDates.Remove(entity);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailObj(
                    IvMasterErrorCode.Concurrency,
                    "This record was changed by another user. Reload and try again.");
            }

            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ── Formatting helpers (public for UI sample consistency via AdSmNumSampleDate) ──

    public static string FormatContinuousSample(string? prefix, long seq, short totLength) =>
        DocumentNumberFormatter.FormatContinuous(prefix, seq, totLength);

    public static string FormatPeriodSample(
        string? prefix,
        long seq,
        short totLength,
        string? delimiter,
        string? format,
        short year,
        short month) =>
        FormatPeriodNext(prefix, seq, totLength, delimiter, format, year, month);

    private static string FormatPeriodNext(
        string? prefix,
        long seq,
        short totLength,
        string? delimiter,
        string? format,
        short year,
        short month)
    {
        var sampleDate = AdSmNumSampleDate.ForPeriod(year, month);
        if (!string.IsNullOrWhiteSpace(format))
        {
            return DocumentNumberFormatter.FormatTemplate(format, prefix, seq, totLength, sampleDate);
        }

        return DocumentNumberFormatter.FormatDateMode(
            prefix,
            seq,
            totLength,
            delimiter,
            sampleDate,
            AdSmNumSampleDate.ModeForPeriod(year, month));
    }

    // ── Validation ──────────────────────────────────────────────────────

    private static Dictionary<string, string> ValidateContinuousModel(AdSmNumEditVm model, bool isNew)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var numCd = NormalizeNumCd(model.NumCd);
        if (numCd is null)
        {
            errors["NumCd"] = "NumCd is required (max 10).";
        }

        if (string.IsNullOrWhiteSpace(model.Prefix))
        {
            errors["Prefix"] = "Prefix is required.";
        }
        else if (model.Prefix.Trim().Length > 10)
        {
            errors["Prefix"] = "Prefix must be at most 10 characters.";
        }

        var prefixLen = model.Prefix?.Trim().Length ?? 0;
        if (model.TotLength <= prefixLen)
        {
            errors["TotLength"] = "Total length must be greater than Prefix length.";
        }

        if (model.Seq < 1)
        {
            errors["Seq"] = "Next sequence must be at least 1.";
        }

        if (model.NumDes is { Length: > 30 })
        {
            errors["NumDes"] = "Description must be at most 30 characters.";
        }

        if (!isNew && string.IsNullOrWhiteSpace(model.NumCd))
        {
            errors["NumCd"] = "NumCd is required.";
        }

        return errors;
    }

    private static Dictionary<string, string> ValidatePeriodModel(AdSmNumDateEditVm model, bool isNew)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var numCd = NormalizeNumCd(model.NumCd);
        if (numCd is null)
        {
            errors["NumCd"] = "NumCd is required (max 10).";
        }

        if (model.Year < 0 || model.Year > 2099)
        {
            errors["Year"] = "Year must be between 0 and 2099.";
        }

        if (model.Month < 0 || model.Month > 12)
        {
            errors["Month"] = "Month must be between 0 and 12.";
        }

        if (model.Year == 0 && model.Month is >= 1 and <= 12)
        {
            errors["Month"] = "Month cannot be 1–12 when Year is 0. Use Year=0 Month=0 for continuous date mode.";
        }

        if (string.IsNullOrWhiteSpace(model.Prefix))
        {
            errors["Prefix"] = "Prefix is required.";
        }
        else if (model.Prefix.Trim().Length > 20)
        {
            errors["Prefix"] = "Prefix must be at most 20 characters.";
        }

        if (model.TotLength <= 0)
        {
            errors["TotLength"] = "Sequence digits must be greater than 0.";
        }

        if (model.Seq < 1)
        {
            errors["Seq"] = "Next sequence must be at least 1.";
        }

        if (model.NumDes is { Length: > 30 })
        {
            errors["NumDes"] = "Description must be at most 30 characters.";
        }

        if (model.NumberingDelimeter is { Length: > 5 })
        {
            errors["NumberingDelimeter"] = "Delimiter must be at most 5 characters.";
        }

        var format = model.NumberingFormat?.Trim();
        if (!string.IsNullOrEmpty(format))
        {
            if (format.Length > 50)
            {
                errors["NumberingFormat"] = "Format must be at most 50 characters.";
            }
            else if (!format.Contains("{1}", StringComparison.Ordinal))
            {
                errors["NumberingFormat"] = "NumberingFormat must contain {1}.";
            }
        }

        if (!isNew && model.Uid <= 0)
        {
            errors["Uid"] = "Uid is required for update.";
        }

        return errors;
    }

    private static string? NormalizeNumCd(string? value)
    {
        var numCd = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(numCd) || numCd.Length > 10)
        {
            return null;
        }

        return numCd;
    }

    private static Task<bool> InvoiceExistsAsync(
        AppDbContext db,
        string company,
        string branch,
        string invNo,
        CancellationToken ct) =>
        db.SaInvoices.AsNoTracking()
            .AnyAsync(
                x => x.CompanyCode == company && x.BranchCode == branch && x.InvNo == invNo,
                ct);

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= max ? value : value[..max];
    }

    // ── Mapping ─────────────────────────────────────────────────────────

    private static AdSmNumListRow MapContinuousList(AdSmNum x) => new()
    {
        NumCd = x.NumCd,
        Prefix = x.Prefix,
        TotLength = x.TotLength,
        Seq = x.Seq,
        NumDes = x.NumDes
    };

    private static AdSmNumEditVm MapContinuousEdit(AdSmNum x) => new()
    {
        NumCd = x.NumCd,
        Prefix = x.Prefix,
        TotLength = x.TotLength,
        Seq = x.Seq,
        NumDes = x.NumDes,
        OriginalSeq = x.Seq
    };

    private static AdSmNumDateListRow MapPeriodList(AdSmNumDate x) => new()
    {
        Uid = x.Uid,
        NumCd = x.NumCd ?? string.Empty,
        Year = x.Year ?? 0,
        Month = x.Month ?? 0,
        Prefix = x.Prefix,
        TotLength = x.TotLength ?? 0,
        Seq = x.Seq ?? 0,
        NumberingDelimeter = x.NumberingDelimeter,
        NumberingFormat = x.NumberingFormat,
        NumDes = x.NumDes,
        RowVersion = x.RowVersion ?? []
    };

    private static AdSmNumDateEditVm MapPeriodEdit(AdSmNumDate x) => new()
    {
        Uid = x.Uid,
        NumCd = x.NumCd ?? string.Empty,
        Year = x.Year ?? 0,
        Month = x.Month ?? 0,
        Prefix = x.Prefix,
        TotLength = x.TotLength ?? 0,
        Seq = x.Seq ?? 0,
        NumberingDelimeter = x.NumberingDelimeter,
        NumberingFormat = x.NumberingFormat,
        NumDes = x.NumDes,
        RowVersion = x.RowVersion,
        OriginalSeq = x.Seq ?? 0
    };

    // ── Scope / errors ──────────────────────────────────────────────────

    private async Task<UserContext> RequireBranchScopeAsync(
        string menuCode,
        string permission,
        CancellationToken cancellationToken)
    {
        var ctx = ValidateBranchContext();
        if (ctx.Error is not null)
        {
            return ctx;
        }

        if (!await _accessRights.CanAsync(menuCode, permission, cancellationToken))
        {
            return UserContext.Fail(IvMasterErrorCode.AccessDenied);
        }

        return ctx;
    }

    private UserContext ValidateBranchContext()
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
        {
            return UserContext.Fail(IvMasterErrorCode.InvalidScope);
        }

        return UserContext.Ok(scope.CompanyCode, scope.BranchCode, scope.UserId);
    }

    private static string MessageFor(IvMasterErrorCode code) => code switch
    {
        IvMasterErrorCode.AccessDenied => "Not authorized.",
        IvMasterErrorCode.InvalidScope => "Invalid company or branch context.",
        IvMasterErrorCode.NotFound => "Record not found.",
        IvMasterErrorCode.Concurrency => "This record was changed by another user. Reload and try again.",
        IvMasterErrorCode.DuplicateKey => "A record with the same key already exists.",
        _ => "Request failed."
    };

    private static IvMasterOperationResult<IReadOnlyList<T>> FailList<T>(IvMasterErrorCode code) =>
        IvMasterOperationResult<IReadOnlyList<T>>.Fail(code, MessageFor(code));

    private static IvMasterOperationResult<T> FailVm<T>(
        IvMasterErrorCode code,
        string? message = null,
        string? field = null)
    {
        Dictionary<string, string>? errors = null;
        if (!string.IsNullOrEmpty(field) && !string.IsNullOrEmpty(message))
        {
            errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [field] = message
            };
        }

        return IvMasterOperationResult<T>.Fail(code, message ?? MessageFor(code), errors);
    }

    private static IvMasterOperationResult<object> FailObj(IvMasterErrorCode code, string? message = null) =>
        IvMasterOperationResult<object>.Fail(code, message ?? MessageFor(code));

    private static async Task<IDbContextTransaction> BeginSaveTransactionAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        // SQL Server: Serializable so invoice insert cannot sneak between collision AnyAsync and config commit.
        // SQLite: default isolation (Serializable + concurrent writers fails on shared in-memory connections).
        if (IsSqlite(db))
        {
            return await db.Database.BeginTransactionAsync(cancellationToken);
        }

        return await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    }

    private static bool RowVersionsEqual(byte[]? left, byte[]? right)
    {
        if (left is null || right is null || left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        return left.AsSpan().SequenceEqual(right);
    }

    private static bool IsSqlite(AppDbContext db) =>
        string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal);

    private static void EnsureRowVersionForSqlite(AppDbContext db, AdSmNumDate entity)
    {
        if (IsSqlite(db) && (entity.RowVersion is null || entity.RowVersion.Length == 0))
        {
            entity.RowVersion = Guid.NewGuid().ToByteArray();
        }
    }

    private static void BumpRowVersionForSqlite(AppDbContext db, AdSmNumDate entity)
    {
        if (IsSqlite(db))
        {
            entity.RowVersion = Guid.NewGuid().ToByteArray();
        }
    }

    private static bool IsDuplicateKey(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql && sql.Number is 2601 or 2627)
            {
                return true;
            }

            if (e is DbException dbEx)
            {
                var msg = dbEx.Message;
                if (msg.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("2627", StringComparison.Ordinal)
                    || msg.Contains("2601", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSerializationConflict(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql && sql.Number is 1205 or 3960 or 3961 or 41301 or 41302 or 41325)
            {
                return true;
            }

            var msg = e.Message;
            if (msg.Contains("Serializable", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("was deadlocked", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct UserContext(
        string? CompanyCode,
        string? BranchCode,
        string? UserId,
        IvMasterErrorCode? Error)
    {
        public static UserContext Ok(string companyCode, string branchCode, string userId) =>
            new(companyCode, branchCode, userId, null);

        public static UserContext Fail(IvMasterErrorCode code) =>
            new(null, null, null, code);
    }
}
