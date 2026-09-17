using System.Data;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// <c>SaLMW</c> — the LMW (Malaysia Licensed Manufacturing Warehouse) licence register. The only
/// master in the flat set with real business logic: the system-date window overlap rule (D-8),
/// enforced inside a Serializable transaction (D-9).
///
/// Key is <c>(CompanyCode, LicenseNo, CustCode)</c>; there is no <c>Active</c> column, so no
/// activate/deactivate path. Level A concurrency (RowVersion).
/// </summary>
public sealed partial class SaSalesRefService
{
    public Task<IvMasterOperationResult<IReadOnlyList<SaLMWListRow>>> ListLmwsAsync(
        CancellationToken cancellationToken = default) =>
        ListFlatMasterAsync(
            MenuCodes.SalesLmw,
            PermissionCodes.Access,
            (db, company) => db.SaLmws.Where(x => x.CompanyCode == company),
            q => q.OrderBy(x => x.CustCode).ThenBy(x => x.LicenseNo),
            MapLmwRow,
            cancellationToken);

    public Task<IvMasterOperationResult<IReadOnlyList<SaLMWListRow>>> ExportLmwsAsync(
        CancellationToken cancellationToken = default) =>
        ListFlatMasterAsync(
            MenuCodes.SalesLmw,
            PermissionCodes.Export,
            (db, company) => db.SaLmws.Where(x => x.CompanyCode == company),
            q => q.OrderBy(x => x.CustCode).ThenBy(x => x.LicenseNo).Take(MaxExportRows),
            MapLmwRow,
            cancellationToken);

    public async Task<IvMasterOperationResult<SaLMWEditVm>> GetLmwAsync(
        string licenseNo,
        string custCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesLmw, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaLMWEditVm>(ctx.Error.Value);
        }

        var license = NormalizeMasterCode(licenseNo);
        var cust = NormalizeMasterCode(custCode);
        if (license.Length == 0 || cust.Length == 0)
        {
            return FailVm<SaLMWEditVm>(
                IvMasterErrorCode.Validation,
                "Licence number and customer code are required.",
                license.Length == 0 ? "LicenseNo" : "CustCode");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SaLmws
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode && x.LicenseNo == license && x.CustCode == cust,
                cancellationToken);
        if (entity is null)
        {
            return FailVm<SaLMWEditVm>(IvMasterErrorCode.NotFound, "LMW licence not found.");
        }

        return IvMasterOperationResult<SaLMWEditVm>.Ok(MapLmw(entity));
    }

    public async Task<IvMasterOperationResult<SaLMWEditVm>> SaveLmwAsync(
        SaLMWEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<SaLMWEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesLmw, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaLMWEditVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var licenseNo = ValidateAndNormalizeCode(errors, "LicenseNo", "Licence number", model.LicenseNo, 40);
        var custCode = ValidateAndNormalizeCode(errors, "CustCode", "Customer code", model.CustCode, 30);
        ValidateOptionalLength(errors, "LicenseID", model.LicenseID, 40);
        ValidateOptionalLength(errors, "LicenseType", model.LicenseType, 20);
        ValidateOptionalLength(errors, "Name", model.Name, 100);
        ValidateOptionalLength(errors, "IC", model.IC, 30);
        ValidateOptionalLength(errors, "Position", model.Position, 50);
        ValidateOptionalLength(errors, "CustName", model.CustName, 200);

        // Ordering + containment first; the overlap query cannot be meaningful on an inverted window.
        SaLmwRules.ValidateWindows(
            model.LicenseStartDate,
            model.LicenseEndDate,
            model.SystemStartDate,
            model.SystemEndDate,
            errors);

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<SaLMWEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<SaLMWEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 20);
        var licenseStart = model.LicenseStartDate.Date;
        var licenseEnd = model.LicenseEndDate.Date;
        var systemStart = model.SystemStartDate.Date;
        var systemEnd = model.SystemEndDate.Date;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // D-9: the transaction covers the WHOLE mutation on BOTH paths. Nothing may run the overlap
        // query outside it, and nothing may commit before the overlap result is known. Serializable
        // is explicit — a ReadCommitted variant looks identical in review and still races (TC29 is
        // the only control that catches it).
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            SaLMW tracked;
            if (isNew)
            {
                if (await db.SaLmws.AnyAsync(
                        x => x.CompanyCode == ctx.CompanyCode
                             && x.LicenseNo == licenseNo
                             && x.CustCode == custCode,
                        cancellationToken))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailVm<SaLMWEditVm>(
                        IvMasterErrorCode.DuplicateKey,
                        "An LMW licence with this licence number and customer already exists.",
                        "LicenseNo");
                }

                tracked = new SaLMW
                {
                    CompanyCode = ctx.CompanyCode!,
                    LicenseNo = licenseNo,
                    CustCode = custCode,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(tracked, writeScope);
            }
            else
            {
                var expectedRowVersion = model.RowVersion;
                if (expectedRowVersion is null || expectedRowVersion.Length == 0)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailVm<SaLMWEditVm>(IvMasterErrorCode.Concurrency, "Row version is required for update.");
                }

                var existing = await db.SaLmws.FirstOrDefaultAsync(
                    x => x.CompanyCode == ctx.CompanyCode
                         && x.LicenseNo == licenseNo
                         && x.CustCode == custCode,
                    cancellationToken);

                // TC21: the full three-part key is resolved by the service. A right licence number
                // with a wrong customer code is simply not found — no bespoke mismatch code.
                if (existing is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailVm<SaLMWEditVm>(IvMasterErrorCode.NotFound, "LMW licence not found.");
                }

                var currentRowVersion = db.Entry(existing).Property("RowVersion").CurrentValue as byte[];
                if (!RowVersionsEqual(currentRowVersion, expectedRowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailVm<SaLMWEditVm>(
                        IvMasterErrorCode.Concurrency,
                        "This LMW licence was modified by another user.");
                }

                db.Entry(existing).Property("RowVersion").OriginalValue = expectedRowVersion;
                tracked = existing;
                tracked.ModifiedDate = now;
                tracked.ModifiedBy = user;
            }

            // Occupancy is per customer, across every licence other than this one. The index
            // IX_SaLMW_Overlap (scripts/init-sales-master-refs.sql) is the intended range-scan and
            // range-lock target — an expectation about the optimizer, not the correctness contract.
            var conflict = await db.SaLmws
                .AsNoTracking()
                .Where(x => x.CompanyCode == ctx.CompanyCode
                            && x.CustCode == custCode
                            && x.LicenseNo != licenseNo
                            && x.SystemStartDate <= systemEnd
                            && x.SystemEndDate >= systemStart)
                .OrderBy(x => x.SystemStartDate)
                .Select(x => new { x.LicenseNo, x.SystemStartDate, x.SystemEndDate })
                .FirstOrDefaultAsync(cancellationToken);

            if (conflict is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return FailVm<SaLMWEditVm>(
                    IvMasterErrorCode.Validation,
                    SaLmwRules.BuildOverlapMessage(
                        conflict.LicenseNo,
                        conflict.SystemStartDate,
                        conflict.SystemEndDate),
                    "SystemStartDate");
            }

            tracked.LicenseID = TruncateOptional(model.LicenseID, 40)?.ToUpperInvariant();
            tracked.LicenseType = TruncateOptional(model.LicenseType, 20)?.ToUpperInvariant();
            tracked.LicenseStartDate = licenseStart;
            tracked.LicenseEndDate = licenseEnd;
            tracked.SystemStartDate = systemStart;
            tracked.SystemEndDate = systemEnd;
            // Person-identifying fields keep their original casing — only trimmed.
            tracked.Name = TruncateOptional(model.Name, 100);
            tracked.IC = TruncateOptional(model.IC, 30);
            tracked.Position = TruncateOptional(model.Position, 50);
            tracked.CustName = TruncateOptional(model.CustName, 200);

            if (isNew)
            {
                db.SaLmws.Add(tracked);
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<SaLMWEditVm>.Ok(MapLmw(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm<SaLMWEditVm>(
                IvMasterErrorCode.Concurrency,
                "This LMW licence was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm<SaLMWEditVm>(
                IvMasterErrorCode.DuplicateKey,
                "An LMW licence with this licence number and customer already exists.",
                "LicenseNo");
        }
        catch (Exception ex) when (IsSerializationConflict(ex))
        {
            // Never retry silently — a silent retry would hide a rule violation from the operator.
            await tx.RollbackAsync(cancellationToken);
            return FailVm<SaLMWEditVm>(
                IvMasterErrorCode.Concurrency,
                "Another user just saved an overlapping licence. Reload and try again.");
        }
    }

    /// <summary>
    /// Nothing in scope references <c>SaLMW</c>: it is a standalone compliance register (§5.1), so
    /// the answer is an explicit statement rather than a silent <c>true</c>. The first consumer of a
    /// licence must arrive with its FK **and** its reference count in the same change (§10.5).
    /// </summary>
    public async Task<DeleteCheckResult> CanDeleteLmwsAsync(
        IReadOnlyList<SaLMWKey> keys,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesLmw, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var list = NormalizeLmwKeys(keys);
        if (list.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        return await Task.FromResult(DeleteCheckResult.Ok("No in-scope table references SaLMW."));
    }

    /// <summary>
    /// Deletes by the full <c>(CompanyCode, LicenseNo, CustCode)</c> key. <see cref="SaCompanyMasterKeyToken.Code"/>
    /// carries <c>LicenseNo</c> and <see cref="SaCompanyMasterKeyToken.ParentCode"/> carries <c>CustCode</c>.
    /// </summary>
    public async Task<IvMasterOperationResult<object>> DeleteLmwsAsync(
        IReadOnlyList<SaCompanyMasterKeyToken> items,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesLmw, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var tokens = NormalizeLmwTokens(items);
        if (tokens.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var token in tokens)
            {
                var entity = await db.SaLmws.FirstOrDefaultAsync(
                    x => x.CompanyCode == ctx.CompanyCode
                         && x.LicenseNo == token.Code
                         && x.CustCode == token.ParentCode,
                    cancellationToken);
                if (entity is null)
                {
                    // TC21: right licence number, wrong customer code ⇒ not found.
                    await tx.RollbackAsync(cancellationToken);
                    return FailObj(IvMasterErrorCode.NotFound, "LMW licence not found.");
                }

                var entry = db.Entry(entity);
                if (!RowVersionsEqual(entry.Property("RowVersion").CurrentValue as byte[], token.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                entry.Property("RowVersion").OriginalValue = token.RowVersion;
                db.SaLmws.Remove(entity);
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ===================== Helpers =====================

    private static SaLMWListRow MapLmwRow(SaLMW x) => new()
    {
        LicenseNo = x.LicenseNo,
        CustCode = x.CustCode,
        LicenseID = x.LicenseID,
        LicenseType = x.LicenseType,
        LicenseStartDate = x.LicenseStartDate,
        LicenseEndDate = x.LicenseEndDate,
        SystemStartDate = x.SystemStartDate,
        SystemEndDate = x.SystemEndDate,
        Name = x.Name,
        IC = x.IC,
        Position = x.Position,
        CustName = x.CustName,
        RowVersion = x.RowVersion ?? []
    };

    private static SaLMWEditVm MapLmw(SaLMW x) => new()
    {
        LicenseNo = x.LicenseNo,
        CustCode = x.CustCode,
        LicenseID = x.LicenseID,
        LicenseType = x.LicenseType,
        LicenseStartDate = x.LicenseStartDate,
        LicenseEndDate = x.LicenseEndDate,
        SystemStartDate = x.SystemStartDate,
        SystemEndDate = x.SystemEndDate,
        Name = x.Name,
        IC = x.IC,
        Position = x.Position,
        CustName = x.CustName,
        RowVersion = x.RowVersion
    };

    /// <summary>
    /// Accepts only tokens carrying both halves of the two-part key plus a row version, so a
    /// tampered or half-filled round trip can never resolve to a different row (TC30).
    /// </summary>
    private static List<SaCompanyMasterKeyToken> NormalizeLmwTokens(
        IReadOnlyList<SaCompanyMasterKeyToken>? items)
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        return items
            .Where(x => x is not null
                        && !string.IsNullOrWhiteSpace(x.Code)
                        && !string.IsNullOrWhiteSpace(x.ParentCode)
                        && x.RowVersion is { Length: > 0 })
            .Select(x => new SaCompanyMasterKeyToken
            {
                Code = NormalizeMasterCode(x.Code),
                ParentCode = NormalizeMasterCode(x.ParentCode),
                RowVersion = x.RowVersion
            })
            .GroupBy(x => $"{x.Code}|{x.ParentCode}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static List<SaLMWKey> NormalizeLmwKeys(IReadOnlyList<SaLMWKey>? keys)
    {
        if (keys is null || keys.Count == 0)
        {
            return [];
        }

        return keys
            .Select(k => new SaLMWKey
            {
                LicenseNo = NormalizeMasterCode(k?.LicenseNo),
                CustCode = NormalizeMasterCode(k?.CustCode)
            })
            .Where(k => k.LicenseNo.Length > 0 && k.CustCode.Length > 0)
            .GroupBy(k => $"{k.LicenseNo}|{k.CustCode}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// SQL Server serialization / deadlock failures. Delegates to the single shared classifier so the
    /// numbering service and this one can never disagree about which codes mean "lost a race".
    /// </summary>
    private static bool IsSerializationConflict(Exception ex) =>
        ErpWeb.Core.Services.SqlErrorClassifier.IsSerializationConflict(ex);
}
