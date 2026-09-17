using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Sales-rep monthly targets (sales-analysis Phase 1).
/// <para>
/// These are deliberately company-wide: the row key is <c>(CompanyCode, SRepCode, Year, Month)</c> with
/// no branch part, because <see cref="SaSalesRep"/> is itself company-scoped. Attainment therefore never
/// varies with the optional branch filter on Sales Summary.
/// </para>
/// <para>
/// Save is an UPSERT of one month: re-saving the same <c>(code, year, month)</c> updates the existing
/// row instead of tripping a duplicate-key error. A month with no row is a target of zero, so the
/// analysis service can sum intersecting months without special-casing missing data.
/// </para>
/// </summary>
public sealed partial class SaSalesRefService
{
    public async Task<IvMasterOperationResult<IReadOnlyList<SaSalesRepTargetRow>>> ListSalesRepTargetsAsync(
        string code,
        int year,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesSalesRep, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<SaSalesRepTargetRow>(ctx.Error.Value);
        }

        var normalized = NormalizeMasterCode(code);
        if (normalized.Length == 0)
        {
            return FailList<SaSalesRepTargetRow>(IvMasterErrorCode.Validation, "Sales rep code is required.");
        }

        if (year <= 0)
        {
            return FailList<SaSalesRepTargetRow>(IvMasterErrorCode.Validation, "Year must be greater than zero.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var exists = await db.SaSalesReps
            .AsNoTracking()
            .AnyAsync(x => x.CompanyCode == ctx.CompanyCode && x.SrepCode == normalized, cancellationToken);
        if (!exists)
        {
            return FailList<SaSalesRepTargetRow>(IvMasterErrorCode.NotFound, "Sales rep not found.");
        }

        var rows = await db.SaSalesRepTargets
            .AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode && x.SrepCode == normalized && x.Year == year)
            .OrderBy(x => x.Month)
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<SaSalesRepTargetRow>>.Ok(
            rows.Select(x => new SaSalesRepTargetRow
            {
                Year = x.Year,
                Month = x.Month,
                TargetAmount = x.TargetAmount
            }).ToList());
    }

    public async Task<IvMasterOperationResult<SaSalesRepTargetRow>> SaveSalesRepTargetAsync(
        SaSalesRepTargetEditVm model,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<SaSalesRepTargetRow>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        // Targets are commercial master data, so editing them needs the same EDIT right as the rep.
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesSalesRep, PermissionCodes.Edit, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<SaSalesRepTargetRow>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var code = NormalizeMasterCode(model.Code);
        if (code.Length == 0)
        {
            errors["Code"] = "Sales rep code is required.";
        }
        else if (code.Length > 20)
        {
            errors["Code"] = "Sales rep code must be at most 20 characters.";
        }

        if (model.Year <= 0)
        {
            errors["Year"] = "Year must be greater than zero.";
        }

        if (model.Month < 1 || model.Month > 12)
        {
            errors["Month"] = "Month must be between 1 and 12.";
        }

        if (model.TargetAmount < 0m)
        {
            errors["TargetAmount"] = "Target amount cannot be negative.";
        }
        else if (HasExcessScale(model.TargetAmount, 2))
        {
            errors["TargetAmount"] = "Target amount must have at most 2 decimal places.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<SaSalesRepTargetRow>.Fail(
                IvMasterErrorCode.Validation,
                "Please correct the highlighted fields.",
                errors);
        }

        var now = _dates.Now;
        var user = Truncate(ctx.UserId ?? "SYSTEM", 20);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var repExists = await db.SaSalesReps
            .AsNoTracking()
            .AnyAsync(x => x.CompanyCode == ctx.CompanyCode && x.SrepCode == code, cancellationToken);
        if (!repExists)
        {
            return FailVm<SaSalesRepTargetRow>(IvMasterErrorCode.NotFound, "Sales rep not found.", "Code");
        }

        // UPSERT: the composite key means a blind insert would throw on re-save of the same month.
        var entity = await db.SaSalesRepTargets
            .FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode
                    && x.SrepCode == code
                    && x.Year == model.Year
                    && x.Month == model.Month,
                cancellationToken);

        if (entity is null)
        {
            entity = new SaSalesRepTarget
            {
                CompanyCode = ctx.CompanyCode!,
                SrepCode = code,
                Year = model.Year,
                Month = model.Month,
                TargetAmount = model.TargetAmount,
                CreatedDate = now,
                CreatedBy = user,
                ModifiedDate = now,
                ModifiedBy = user
            };
            db.SaSalesRepTargets.Add(entity);
        }
        else
        {
            entity.TargetAmount = model.TargetAmount;
            entity.ModifiedDate = now;
            entity.ModifiedBy = user;
        }

        await db.SaveChangesAsync(cancellationToken);

        return IvMasterOperationResult<SaSalesRepTargetRow>.Ok(new SaSalesRepTargetRow
        {
            Year = entity.Year,
            Month = entity.Month,
            TargetAmount = entity.TargetAmount
        });
    }

    public async Task<IvMasterOperationResult<object>> DeleteSalesRepTargetAsync(
        string code,
        int year,
        int month,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesSalesRep, PermissionCodes.Edit, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<object>(ctx.Error.Value);
        }

        var normalized = NormalizeMasterCode(code);
        if (normalized.Length == 0)
        {
            return FailVm<object>(IvMasterErrorCode.Validation, "Sales rep code is required.", "Code");
        }

        if (year <= 0 || month < 1 || month > 12)
        {
            return FailVm<object>(IvMasterErrorCode.Validation, "A valid year and month are required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.SaSalesRepTargets
            .FirstOrDefaultAsync(
                x => x.CompanyCode == ctx.CompanyCode
                    && x.SrepCode == normalized
                    && x.Year == year
                    && x.Month == month,
                cancellationToken);

        if (entity is null)
        {
            return FailVm<object>(IvMasterErrorCode.NotFound, "No target exists for that month.");
        }

        db.SaSalesRepTargets.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);

        return IvMasterOperationResult<object>.Ok(new object());
    }
}
