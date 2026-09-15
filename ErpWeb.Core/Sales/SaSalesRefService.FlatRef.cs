using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// The five flat sales code-reference masters that have no special business rule
/// (<c>SaCustSubGroup</c>, <c>SaShipVia</c>, <c>SaSOType</c>, <c>SaComment</c>,
/// <c>SaShippingLeadTime</c>). <c>SaLMW</c> lives in <c>SaSalesRefService.Lmw.cs</c>.
///
/// All five are Level A (DB <c>RowVersion</c> carried on the VM — no <c>expectedFingerprint</c>
/// argument) and go through the one shared write path of docs/sales-master-plan.md §10.1.
/// </summary>
public sealed partial class SaSalesRefService
{
    // Reference-check note (plan §10.5): five of six masters have no consumer column yet, so the
    // "no references" answer must be an explicit statement, never a silent true.

    // ===================== Customer Sub Group =====================

    public Task<IvMasterOperationResult<IReadOnlyList<SaCustSubGroupListRow>>> ListCustSubGroupsAsync(
        CancellationToken cancellationToken = default) =>
        ListFlatMasterAsync(
            MenuCodes.SalesCustSubGroup,
            PermissionCodes.Access,
            (db, company) => db.SaCustSubGroups.Where(x => x.CompanyCode == company),
            q => q.OrderBy(x => x.CustSubGroupCode),
            MapCustSubGroupRow,
            cancellationToken);

    public Task<IvMasterOperationResult<IReadOnlyList<SaCustSubGroupListRow>>> ExportCustSubGroupsAsync(
        CancellationToken cancellationToken = default) =>
        ListFlatMasterAsync(
            MenuCodes.SalesCustSubGroup,
            PermissionCodes.Export,
            (db, company) => db.SaCustSubGroups.Where(x => x.CompanyCode == company),
            q => q.OrderBy(x => x.CustSubGroupCode).Take(MaxExportRows),
            MapCustSubGroupRow,
            cancellationToken);

    public Task<IvMasterOperationResult<SaCustSubGroupEditVm>> GetCustSubGroupAsync(
        string code,
        CancellationToken cancellationToken = default) =>
        GetFlatMasterAsync(
            MenuCodes.SalesCustSubGroup,
            "Customer sub-group",
            code,
            (db, company, normalized, ct) => db.SaCustSubGroups
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.CompanyCode == company && x.CustSubGroupCode == normalized, ct),
            entity => new SaCustSubGroupEditVm
            {
                Code = entity.CustSubGroupCode,
                Desc = entity.CustSubGroupDesc,
                RowVersion = entity.RowVersion
            },
            cancellationToken);

    public Task<IvMasterOperationResult<SaCustSubGroupEditVm>> SaveCustSubGroupAsync(
        SaCustSubGroupEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default) =>
        SaveFlatMasterAsync<SaCustSubGroup, SaCustSubGroupEditVm>(
            model,
            isNew,
            MenuCodes.SalesCustSubGroup,
            "customer sub-group",
            "Customer sub-group",
            "Customer sub-group code already exists.",
            nameof(SaCustSubGroup.CustSubGroupCode),
            validate: errors =>
            {
                var code = ValidateAndNormalizeCode(errors, "Code", "Code", model?.Code, 20);
                var desc = ValidateAndNormalizeDescription(errors, "Desc", "Description", model?.Desc, 100);
                return (code, desc);
            },
            build: (company, code, desc, now, user, scope) =>
            {
                var entity = new SaCustSubGroup
                {
                    CompanyCode = company,
                    CustSubGroupCode = code,
                    CustSubGroupDesc = desc,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, scope);
                return entity;
            },
            apply: (entity, desc, now, user) =>
            {
                entity.CustSubGroupDesc = desc;
                entity.ModifiedDate = now;
                entity.ModifiedBy = user;
            },
            rowVersionOf: vm => vm.RowVersion,
            load: (db, company, code, ct) => db.SaCustSubGroups
                .FirstOrDefaultAsync(x => x.CompanyCode == company && x.CustSubGroupCode == code, ct),
            exists: (db, company, code, ct) => db.SaCustSubGroups
                .AnyAsync(x => x.CompanyCode == company && x.CustSubGroupCode == code, ct),
            map: entity => new SaCustSubGroupEditVm
            {
                Code = entity.CustSubGroupCode,
                Desc = entity.CustSubGroupDesc,
                RowVersion = entity.RowVersion
            },
            cancellationToken);

    /// <summary>
    /// The one master in the set that ships referenced: <c>SaCust.SubGroupCode</c> counts against
    /// it (D-6). The count is always scoped to the caller's company (TC33) — never global.
    /// </summary>
    public async Task<DeleteCheckResult> CanDeleteCustSubGroupsAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.SalesCustSubGroup, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var list = NormalizeMasterCodes(codes);
        if (list.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var refs = await CountCustSubGroupReferencesBulkAsync(db, ctx.CompanyCode!, list, cancellationToken);
        return BuildDeleteCheck(list, refs);
    }

    public Task<IvMasterOperationResult<object>> DeleteCustSubGroupsAsync(
        IReadOnlyList<SaCompanyMasterKeyToken> items,
        CancellationToken cancellationToken = default) =>
        DeleteCompanyWithRowVersionAsync<SaCustSubGroup>(
            MenuCodes.SalesCustSubGroup,
            items,
            async (db, company, codes, ct) =>
                await db.SaCustSubGroups
                    .Where(x => x.CompanyCode == company && codes.Contains(x.CustSubGroupCode))
                    .ToListAsync(ct),
            (e, code) => KeysEqual(e.CustSubGroupCode, code),
            async (db, company, codes, ct) =>
                await CountCustSubGroupReferencesBulkAsync(db, company, codes, ct),
            (db, entities) => db.SaCustSubGroups.RemoveRange(entities),
            cancellationToken);

    // ===================== Ship Via =====================

    public Task<IvMasterOperationResult<IReadOnlyList<SaShipViaListRow>>> ListShipViasAsync(
        CancellationToken cancellationToken = default) =>
        ListFlatMasterAsync(
            MenuCodes.SalesShipVia,
            PermissionCodes.Access,
            (db, company) => db.SaShipVias.Where(x => x.CompanyCode == company),
            q => q.OrderBy(x => x.ShipViaCode),
            MapShipViaRow,
            cancellationToken);

    public Task<IvMasterOperationResult<IReadOnlyList<SaShipViaListRow>>> ExportShipViasAsync(
        CancellationToken cancellationToken = default) =>
        ListFlatMasterAsync(
            MenuCodes.SalesShipVia,
            PermissionCodes.Export,
            (db, company) => db.SaShipVias.Where(x => x.CompanyCode == company),
            q => q.OrderBy(x => x.ShipViaCode).Take(MaxExportRows),
            MapShipViaRow,
            cancellationToken);

    public Task<IvMasterOperationResult<SaShipViaEditVm>> GetShipViaAsync(
        string code,
        CancellationToken cancellationToken = default) =>
        GetFlatMasterAsync(
            MenuCodes.SalesShipVia,
            "Ship via",
            code,
            (db, company, normalized, ct) => db.SaShipVias
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.CompanyCode == company && x.ShipViaCode == normalized, ct),
            entity => new SaShipViaEditVm
            {
                Code = entity.ShipViaCode,
                Desc = entity.ShipViaDesc,
                IsActive = entity.IsActive,
                RowVersion = entity.RowVersion
            },
            cancellationToken);

    public Task<IvMasterOperationResult<SaShipViaEditVm>> SaveShipViaAsync(
        SaShipViaEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default) =>
        SaveFlatMasterAsync<SaShipVia, SaShipViaEditVm>(
            model,
            isNew,
            MenuCodes.SalesShipVia,
            "ship via",
            "Ship via",
            "Ship via code already exists.",
            nameof(SaShipVia.ShipViaCode),
            validate: errors =>
            {
                var code = ValidateAndNormalizeCode(errors, "Code", "Code", model?.Code, 20);
                var desc = ValidateAndNormalizeDescription(errors, "Desc", "Description", model?.Desc, 100);
                return (code, desc);
            },
            build: (company, code, desc, now, user, scope) =>
            {
                var entity = new SaShipVia
                {
                    CompanyCode = company,
                    ShipViaCode = code,
                    ShipViaDesc = desc,
                    IsActive = model!.IsActive,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, scope);
                return entity;
            },
            apply: (entity, desc, now, user) =>
            {
                entity.ShipViaDesc = desc;
                entity.IsActive = model!.IsActive;
                entity.ModifiedDate = now;
                entity.ModifiedBy = user;
            },
            rowVersionOf: vm => vm.RowVersion,
            load: (db, company, code, ct) => db.SaShipVias
                .FirstOrDefaultAsync(x => x.CompanyCode == company && x.ShipViaCode == code, ct),
            exists: (db, company, code, ct) => db.SaShipVias
                .AnyAsync(x => x.CompanyCode == company && x.ShipViaCode == code, ct),
            map: entity => new SaShipViaEditVm
            {
                Code = entity.ShipViaCode,
                Desc = entity.ShipViaDesc,
                IsActive = entity.IsActive,
                RowVersion = entity.RowVersion
            },
            cancellationToken);

    public Task<IvMasterOperationResult<object>> SetShipViaActiveAsync(
        IReadOnlyList<SaCompanyMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default) =>
        SetFlatMasterActiveAsync<SaShipVia>(
            MenuCodes.SalesShipVia,
            items,
            isActive,
            (db, company, codes, ct) => db.SaShipVias
                .Where(x => x.CompanyCode == company && codes.Contains(x.ShipViaCode))
                .ToListAsync(ct),
            (e, code) => KeysEqual(e.ShipViaCode, code),
            (e, now, user) =>
            {
                e.IsActive = isActive;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            cancellationToken);

    public Task<DeleteCheckResult> CanDeleteShipViasAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default) =>
        CanDeleteUnreferencedFlatMasterAsync("SaShipVia", MenuCodes.SalesShipVia, codes, cancellationToken);

    public Task<IvMasterOperationResult<object>> DeleteShipViasAsync(
        IReadOnlyList<SaCompanyMasterKeyToken> items,
        CancellationToken cancellationToken = default) =>
        DeleteCompanyWithRowVersionAsync<SaShipVia>(
            MenuCodes.SalesShipVia,
            items,
            async (db, company, codes, ct) =>
                await db.SaShipVias
                    .Where(x => x.CompanyCode == company && codes.Contains(x.ShipViaCode))
                    .ToListAsync(ct),
            (e, code) => KeysEqual(e.ShipViaCode, code),
            (db, company, codes, ct) => Task.FromResult(NoReferenceMap(codes)),
            (db, entities) => db.SaShipVias.RemoveRange(entities),
            cancellationToken);

    // ===================== SO Type =====================

    public Task<IvMasterOperationResult<IReadOnlyList<SaSOTypeListRow>>> ListSoTypesAsync(
        CancellationToken cancellationToken = default) =>
        ListFlatMasterAsync(
            MenuCodes.SalesSoType,
            PermissionCodes.Access,
            (db, company) => db.SaSOTypes.Where(x => x.CompanyCode == company),
            q => q.OrderBy(x => x.SOTypeCode),
            MapSoTypeRow,
            cancellationToken);

    public Task<IvMasterOperationResult<IReadOnlyList<SaSOTypeListRow>>> ExportSoTypesAsync(
        CancellationToken cancellationToken = default) =>
        ListFlatMasterAsync(
            MenuCodes.SalesSoType,
            PermissionCodes.Export,
            (db, company) => db.SaSOTypes.Where(x => x.CompanyCode == company),
            q => q.OrderBy(x => x.SOTypeCode).Take(MaxExportRows),
            MapSoTypeRow,
            cancellationToken);

    public Task<IvMasterOperationResult<SaSOTypeEditVm>> GetSoTypeAsync(
        string code,
        CancellationToken cancellationToken = default) =>
        GetFlatMasterAsync(
            MenuCodes.SalesSoType,
            "SO type",
            code,
            (db, company, normalized, ct) => db.SaSOTypes
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.CompanyCode == company && x.SOTypeCode == normalized, ct),
            entity => new SaSOTypeEditVm
            {
                Code = entity.SOTypeCode,
                Desc = entity.SOTypeDesc,
                IsActive = entity.IsActive,
                RowVersion = entity.RowVersion
            },
            cancellationToken);

    public Task<IvMasterOperationResult<SaSOTypeEditVm>> SaveSoTypeAsync(
        SaSOTypeEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default) =>
        SaveFlatMasterAsync<SaSOType, SaSOTypeEditVm>(
            model,
            isNew,
            MenuCodes.SalesSoType,
            "SO type",
            "SO type",
            "SO type code already exists.",
            nameof(SaSOType.SOTypeCode),
            validate: errors =>
            {
                var code = ValidateAndNormalizeCode(errors, "Code", "Code", model?.Code, 20);
                var desc = ValidateAndNormalizeDescription(errors, "Desc", "Description", model?.Desc, 100);
                return (code, desc);
            },
            build: (company, code, desc, now, user, scope) =>
            {
                var entity = new SaSOType
                {
                    CompanyCode = company,
                    SOTypeCode = code,
                    SOTypeDesc = desc,
                    IsActive = model!.IsActive,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, scope);
                return entity;
            },
            apply: (entity, desc, now, user) =>
            {
                entity.SOTypeDesc = desc;
                entity.IsActive = model!.IsActive;
                entity.ModifiedDate = now;
                entity.ModifiedBy = user;
            },
            rowVersionOf: vm => vm.RowVersion,
            load: (db, company, code, ct) => db.SaSOTypes
                .FirstOrDefaultAsync(x => x.CompanyCode == company && x.SOTypeCode == code, ct),
            exists: (db, company, code, ct) => db.SaSOTypes
                .AnyAsync(x => x.CompanyCode == company && x.SOTypeCode == code, ct),
            map: entity => new SaSOTypeEditVm
            {
                Code = entity.SOTypeCode,
                Desc = entity.SOTypeDesc,
                IsActive = entity.IsActive,
                RowVersion = entity.RowVersion
            },
            cancellationToken);

    public Task<IvMasterOperationResult<object>> SetSoTypeActiveAsync(
        IReadOnlyList<SaCompanyMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default) =>
        SetFlatMasterActiveAsync<SaSOType>(
            MenuCodes.SalesSoType,
            items,
            isActive,
            (db, company, codes, ct) => db.SaSOTypes
                .Where(x => x.CompanyCode == company && codes.Contains(x.SOTypeCode))
                .ToListAsync(ct),
            (e, code) => KeysEqual(e.SOTypeCode, code),
            (e, now, user) =>
            {
                e.IsActive = isActive;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            cancellationToken);

    public Task<DeleteCheckResult> CanDeleteSoTypesAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default) =>
        CanDeleteUnreferencedFlatMasterAsync("SaSOType", MenuCodes.SalesSoType, codes, cancellationToken);

    public Task<IvMasterOperationResult<object>> DeleteSoTypesAsync(
        IReadOnlyList<SaCompanyMasterKeyToken> items,
        CancellationToken cancellationToken = default) =>
        DeleteCompanyWithRowVersionAsync<SaSOType>(
            MenuCodes.SalesSoType,
            items,
            async (db, company, codes, ct) =>
                await db.SaSOTypes
                    .Where(x => x.CompanyCode == company && codes.Contains(x.SOTypeCode))
                    .ToListAsync(ct),
            (e, code) => KeysEqual(e.SOTypeCode, code),
            (db, company, codes, ct) => Task.FromResult(NoReferenceMap(codes)),
            (db, entities) => db.SaSOTypes.RemoveRange(entities),
            cancellationToken);

    // ===================== Comment =====================

    public Task<IvMasterOperationResult<IReadOnlyList<SaCommentListRow>>> ListCommentsAsync(
        CancellationToken cancellationToken = default) =>
        ListFlatMasterAsync(
            MenuCodes.SalesComment,
            PermissionCodes.Access,
            (db, company) => db.SaComments.Where(x => x.CompanyCode == company),
            q => q.OrderBy(x => x.CommID),
            MapCommentRow,
            cancellationToken);

    public Task<IvMasterOperationResult<IReadOnlyList<SaCommentListRow>>> ExportCommentsAsync(
        CancellationToken cancellationToken = default) =>
        ListFlatMasterAsync(
            MenuCodes.SalesComment,
            PermissionCodes.Export,
            (db, company) => db.SaComments.Where(x => x.CompanyCode == company),
            q => q.OrderBy(x => x.CommID).Take(MaxExportRows),
            MapCommentRow,
            cancellationToken);

    public Task<IvMasterOperationResult<SaCommentEditVm>> GetCommentAsync(
        string code,
        CancellationToken cancellationToken = default) =>
        GetFlatMasterAsync(
            MenuCodes.SalesComment,
            "Comment",
            code,
            (db, company, normalized, ct) => db.SaComments
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.CompanyCode == company && x.CommID == normalized, ct),
            entity => new SaCommentEditVm
            {
                Code = entity.CommID,
                Comment = entity.Comment,
                IsActive = entity.IsActive,
                RowVersion = entity.RowVersion
            },
            cancellationToken);

    public Task<IvMasterOperationResult<SaCommentEditVm>> SaveCommentAsync(
        SaCommentEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default) =>
        SaveFlatMasterAsync<SaComment, SaCommentEditVm>(
            model,
            isNew,
            MenuCodes.SalesComment,
            "comment",
            "Comment",
            "Comment ID already exists.",
            nameof(SaComment.CommID),
            validate: errors =>
            {
                var code = ValidateAndNormalizeCode(errors, "Code", "Comment ID", model?.Code, 20);
                var desc = ValidateAndNormalizeDescription(errors, "Comment", "Comment", model?.Comment, 1000);
                return (code, desc);
            },
            build: (company, code, desc, now, user, scope) =>
            {
                var entity = new SaComment
                {
                    CompanyCode = company,
                    CommID = code,
                    Comment = desc,
                    IsActive = model!.IsActive,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, scope);
                return entity;
            },
            apply: (entity, desc, now, user) =>
            {
                entity.Comment = desc;
                entity.IsActive = model!.IsActive;
                entity.ModifiedDate = now;
                entity.ModifiedBy = user;
            },
            rowVersionOf: vm => vm.RowVersion,
            load: (db, company, code, ct) => db.SaComments
                .FirstOrDefaultAsync(x => x.CompanyCode == company && x.CommID == code, ct),
            exists: (db, company, code, ct) => db.SaComments
                .AnyAsync(x => x.CompanyCode == company && x.CommID == code, ct),
            map: entity => new SaCommentEditVm
            {
                Code = entity.CommID,
                Comment = entity.Comment,
                IsActive = entity.IsActive,
                RowVersion = entity.RowVersion
            },
            cancellationToken);

    public Task<IvMasterOperationResult<object>> SetCommentActiveAsync(
        IReadOnlyList<SaCompanyMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default) =>
        SetFlatMasterActiveAsync<SaComment>(
            MenuCodes.SalesComment,
            items,
            isActive,
            (db, company, codes, ct) => db.SaComments
                .Where(x => x.CompanyCode == company && codes.Contains(x.CommID))
                .ToListAsync(ct),
            (e, code) => KeysEqual(e.CommID, code),
            (e, now, user) =>
            {
                e.IsActive = isActive;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            cancellationToken);

    public Task<DeleteCheckResult> CanDeleteCommentsAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default) =>
        CanDeleteUnreferencedFlatMasterAsync("SaComment", MenuCodes.SalesComment, codes, cancellationToken);

    public Task<IvMasterOperationResult<object>> DeleteCommentsAsync(
        IReadOnlyList<SaCompanyMasterKeyToken> items,
        CancellationToken cancellationToken = default) =>
        DeleteCompanyWithRowVersionAsync<SaComment>(
            MenuCodes.SalesComment,
            items,
            async (db, company, codes, ct) =>
                await db.SaComments
                    .Where(x => x.CompanyCode == company && codes.Contains(x.CommID))
                    .ToListAsync(ct),
            (e, code) => KeysEqual(e.CommID, code),
            (db, company, codes, ct) => Task.FromResult(NoReferenceMap(codes)),
            (db, entities) => db.SaComments.RemoveRange(entities),
            cancellationToken);

    // ===================== Shipping Lead Time =====================

    public Task<IvMasterOperationResult<IReadOnlyList<SaShippingLeadTimeListRow>>> ListShippingLeadTimesAsync(
        CancellationToken cancellationToken = default) =>
        ListFlatMasterAsync(
            MenuCodes.SalesShipLeadTime,
            PermissionCodes.Access,
            (db, company) => db.SaShippingLeadTimes.Where(x => x.CompanyCode == company),
            q => q.OrderBy(x => x.LeadTimeCode),
            MapShippingLeadTimeRow,
            cancellationToken);

    public Task<IvMasterOperationResult<IReadOnlyList<SaShippingLeadTimeListRow>>> ExportShippingLeadTimesAsync(
        CancellationToken cancellationToken = default) =>
        ListFlatMasterAsync(
            MenuCodes.SalesShipLeadTime,
            PermissionCodes.Export,
            (db, company) => db.SaShippingLeadTimes.Where(x => x.CompanyCode == company),
            q => q.OrderBy(x => x.LeadTimeCode).Take(MaxExportRows),
            MapShippingLeadTimeRow,
            cancellationToken);

    public Task<IvMasterOperationResult<SaShippingLeadTimeEditVm>> GetShippingLeadTimeAsync(
        string code,
        CancellationToken cancellationToken = default) =>
        GetFlatMasterAsync(
            MenuCodes.SalesShipLeadTime,
            "Shipping lead time",
            code,
            (db, company, normalized, ct) => db.SaShippingLeadTimes
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.CompanyCode == company && x.LeadTimeCode == normalized, ct),
            entity => new SaShippingLeadTimeEditVm
            {
                Code = entity.LeadTimeCode,
                Desc = entity.LeadTimeDesc,
                Days = entity.Days,
                Type = entity.Type,
                IsActive = entity.IsActive,
                RowVersion = entity.RowVersion
            },
            cancellationToken);

    public Task<IvMasterOperationResult<SaShippingLeadTimeEditVm>> SaveShippingLeadTimeAsync(
        SaShippingLeadTimeEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        // Type is normalised up front because null / "" / "   " must all store NULL, not "" (D-5/TC19).
        var leadTimeType = NormalizeLeadTimeType(model?.Type);
        return SaveFlatMasterAsync<SaShippingLeadTime, SaShippingLeadTimeEditVm>(
            model,
            isNew,
            MenuCodes.SalesShipLeadTime,
            "shipping lead time",
            "Shipping lead time",
            "Lead time code already exists.",
            nameof(SaShippingLeadTime.LeadTimeCode),
            validate: errors =>
            {
                var code = ValidateAndNormalizeCode(errors, "Code", "Code", model?.Code, 20);
                var desc = ValidateAndNormalizeDescription(errors, "Desc", "Description", model?.Desc, 100);

                if (leadTimeType.Error is not null)
                {
                    errors["Type"] = leadTimeType.Error;
                }

                // NULL Days means "not specified" and is accepted; a supplied value is 0..3650 (TC13).
                if (model?.Days is < 0 or > 3650)
                {
                    errors["Days"] = "Days must be between 0 and 3650.";
                }

                return (code, desc);
            },
            build: (company, code, desc, now, user, scope) =>
            {
                var entity = new SaShippingLeadTime
                {
                    CompanyCode = company,
                    LeadTimeCode = code,
                    LeadTimeDesc = desc,
                    Days = model!.Days,
                    Type = leadTimeType.Value,
                    IsActive = model.IsActive,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user
                };
                InventoryLeftoverSite.Apply(entity, scope);
                return entity;
            },
            apply: (entity, desc, now, user) =>
            {
                entity.LeadTimeDesc = desc;
                entity.Days = model!.Days;
                entity.Type = leadTimeType.Value;
                entity.IsActive = model.IsActive;
                entity.ModifiedDate = now;
                entity.ModifiedBy = user;
            },
            rowVersionOf: vm => vm.RowVersion,
            load: (db, company, code, ct) => db.SaShippingLeadTimes
                .FirstOrDefaultAsync(x => x.CompanyCode == company && x.LeadTimeCode == code, ct),
            exists: (db, company, code, ct) => db.SaShippingLeadTimes
                .AnyAsync(x => x.CompanyCode == company && x.LeadTimeCode == code, ct),
            map: entity => new SaShippingLeadTimeEditVm
            {
                Code = entity.LeadTimeCode,
                Desc = entity.LeadTimeDesc,
                Days = entity.Days,
                Type = entity.Type,
                IsActive = entity.IsActive,
                RowVersion = entity.RowVersion
            },
            cancellationToken);
    }

    public Task<IvMasterOperationResult<object>> SetShippingLeadTimeActiveAsync(
        IReadOnlyList<SaCompanyMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default) =>
        SetFlatMasterActiveAsync<SaShippingLeadTime>(
            MenuCodes.SalesShipLeadTime,
            items,
            isActive,
            (db, company, codes, ct) => db.SaShippingLeadTimes
                .Where(x => x.CompanyCode == company && codes.Contains(x.LeadTimeCode))
                .ToListAsync(ct),
            (e, code) => KeysEqual(e.LeadTimeCode, code),
            (e, now, user) =>
            {
                e.IsActive = isActive;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            cancellationToken);

    public Task<DeleteCheckResult> CanDeleteShippingLeadTimesAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default) =>
        CanDeleteUnreferencedFlatMasterAsync(
            "SaShippingLeadTime",
            MenuCodes.SalesShipLeadTime,
            codes,
            cancellationToken);

    public Task<IvMasterOperationResult<object>> DeleteShippingLeadTimesAsync(
        IReadOnlyList<SaCompanyMasterKeyToken> items,
        CancellationToken cancellationToken = default) =>
        DeleteCompanyWithRowVersionAsync<SaShippingLeadTime>(
            MenuCodes.SalesShipLeadTime,
            items,
            async (db, company, codes, ct) =>
                await db.SaShippingLeadTimes
                    .Where(x => x.CompanyCode == company && codes.Contains(x.LeadTimeCode))
                    .ToListAsync(ct),
            (e, code) => KeysEqual(e.LeadTimeCode, code),
            (db, company, codes, ct) => Task.FromResult(NoReferenceMap(codes)),
            (db, entities) => db.SaShippingLeadTimes.RemoveRange(entities),
            cancellationToken);

    // ===================== Shared flat-master plumbing =====================

    private async Task<IvMasterOperationResult<IReadOnlyList<TRow>>> ListFlatMasterAsync<TEntity, TRow>(
        string menuCode,
        string permission,
        Func<AppDbContext, string, IQueryable<TEntity>> query,
        Func<IQueryable<TEntity>, IQueryable<TEntity>> order,
        Func<TEntity, TRow> map,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailList<TRow>(ctx.Error.Value);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await order(query(db, ctx.CompanyCode!)).AsNoTracking().ToListAsync(cancellationToken);
        return IvMasterOperationResult<IReadOnlyList<TRow>>.Ok(rows.Select(map).ToList());
    }

    private async Task<IvMasterOperationResult<TVm>> GetFlatMasterAsync<TEntity, TVm>(
        string menuCode,
        string entityLabel,
        string code,
        Func<AppDbContext, string, string, CancellationToken, Task<TEntity?>> load,
        Func<TEntity, TVm> map,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<TVm>(ctx.Error.Value);
        }

        var normalized = NormalizeMasterCode(code);
        if (normalized.Length == 0)
        {
            return FailVm<TVm>(IvMasterErrorCode.Validation, "Code is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await load(db, ctx.CompanyCode!, normalized, cancellationToken);
        if (entity is null)
        {
            return FailVm<TVm>(IvMasterErrorCode.NotFound, $"{entityLabel} not found.");
        }

        return IvMasterOperationResult<TVm>.Ok(map(entity));
    }

    /// <summary>
    /// The single write path for the flat masters (plan §10.1). Insert stamps
    /// <c>Created</c>+<c>UserID</c>+<c>Updated</c>+<c>UpdatedUID</c>; update stamps only
    /// <c>Updated</c>+<c>UpdatedUID</c> and leaves <c>Created</c>+<c>UserID</c> untouched (D-12).
    /// Time is company-local (<c>ICurrentDateService.Now</c>), never <c>DateTime.Now</c>.
    /// Company/branch/location are never read from the VM, route or query string.
    /// </summary>
    private async Task<IvMasterOperationResult<TVm>> SaveFlatMasterAsync<TEntity, TVm>(
        TVm? model,
        bool isNew,
        string menuCode,
        string conflictLabel,
        string entityLabel,
        string duplicateMessage,
        string keyPropertyName,
        Func<Dictionary<string, string>, (string Code, string? Desc)> validate,
        Func<string, string, string?, DateTime, string, InventoryTenantScope, TEntity> build,
        Action<TEntity, string?, DateTime, string> apply,
        Func<TVm, byte[]?> rowVersionOf,
        Func<AppDbContext, string, string, CancellationToken, Task<TEntity?>> load,
        Func<AppDbContext, string, string, CancellationToken, Task<bool>> exists,
        Func<TEntity, TVm> map,
        CancellationToken cancellationToken)
        where TEntity : class
        where TVm : class
    {
        if (model is null)
        {
            return FailVm<TVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(menuCode, permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm<TVm>(ctx.Error.Value);
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var (code, desc) = validate(errors);
        if (errors.Count > 0)
        {
            return IvMasterOperationResult<TVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm<TVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(writeScope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                // Duplicate on create is always a validation error, never a silent upsert (D2).
                if (await exists(db, ctx.CompanyCode!, code, cancellationToken))
                {
                    return FailVm<TVm>(IvMasterErrorCode.DuplicateKey, duplicateMessage, "Code");
                }

                var entity = build(ctx.CompanyCode!, code, desc, now, user, writeScope);
                db.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<TVm>.Ok(map(entity));
            }

            var expectedRowVersion = rowVersionOf(model);
            if (expectedRowVersion is null || expectedRowVersion.Length == 0)
            {
                return FailVm<TVm>(IvMasterErrorCode.Concurrency, "Row version is required for update.");
            }

            var tracked = await load(db, ctx.CompanyCode!, code, cancellationToken);
            if (tracked is null)
            {
                return FailVm<TVm>(IvMasterErrorCode.NotFound, $"{entityLabel} not found.");
            }

            // Business keys are not editable (D-11).
            var trackedKey = db.Entry(tracked).Property(keyPropertyName).CurrentValue as string;
            if (!KeysEqual(trackedKey, code))
            {
                return FailVm<TVm>(IvMasterErrorCode.Validation, "Code cannot be changed.", "Code");
            }

            var currentRowVersion = db.Entry(tracked).Property("RowVersion").CurrentValue as byte[];
            if (!RowVersionsEqual(currentRowVersion, expectedRowVersion))
            {
                return FailVm<TVm>(
                    IvMasterErrorCode.Concurrency,
                    $"This {conflictLabel} was modified by another user.");
            }

            db.Entry(tracked).Property("RowVersion").OriginalValue = expectedRowVersion;
            apply(tracked, desc, now, user);
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<TVm>.Ok(map(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm<TVm>(
                IvMasterErrorCode.Concurrency,
                $"This {conflictLabel} was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<TVm>(IvMasterErrorCode.DuplicateKey, duplicateMessage, "Code");
        }
    }

    private async Task<IvMasterOperationResult<object>> SetFlatMasterActiveAsync<TEntity>(
        string menuCode,
        IReadOnlyList<SaCompanyMasterKeyToken> items,
        bool isActive,
        Func<AppDbContext, string, IReadOnlyList<string>, CancellationToken, Task<List<TEntity>>> load,
        Func<TEntity, string, bool> match,
        Action<TEntity, DateTime, string> apply,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, PermissionCodes.Edit, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailObj(ctx.Error.Value);
        }

        var tokens = NormalizeCompanyTokens(items);
        if (tokens.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var codes = tokens.Select(t => t.Code).ToList();
        var now = _dates.Now;
        var user = Truncate(ctx.UserId!, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var entities = await load(db, ctx.CompanyCode!, codes, cancellationToken);
            if (entities.Count != tokens.Count)
            {
                return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
            }

            foreach (var token in tokens)
            {
                var entity = entities.FirstOrDefault(e => match(e, token.Code));
                if (entity is null)
                {
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                var entry = db.Entry(entity);
                if (!RowVersionsEqual(entry.Property("RowVersion").CurrentValue as byte[], token.RowVersion))
                {
                    return FailObj(IvMasterErrorCode.Concurrency, StaleBulkMessage());
                }

                entry.Property("RowVersion").OriginalValue = token.RowVersion;
                apply(entity, now, user);
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

    /// <summary>
    /// Deletability for a master with no consumer column yet. The answer is an explicit, documented
    /// "consumer deferred" note rather than a silent <c>true</c> (plan §10.5).
    /// </summary>
    private async Task<DeleteCheckResult> CanDeleteUnreferencedFlatMasterAsync(
        string tableName,
        string menuCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.Error.Value), []);
        }

        var list = NormalizeMasterCodes(codes);
        if (list.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        return await Task.FromResult(DeleteCheckResult.Ok(
            $"No in-scope table references {tableName} — its consumer is deferred (plan §5.1)."));
    }

    /// <summary>
    /// A reference-count map carrying no hits. Used where no consumer table exists yet, so the
    /// delete path and the matching <c>CanDelete*</c> cannot disagree.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>> NoReferenceMap(
        IEnumerable<string> codes) =>
        FreezeMap(InitRefMap(codes));

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<IvReferenceCount>>> CountCustSubGroupReferencesBulkAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        var map = InitRefMap(codes);
        var rows = await db.SaCusts
            .AsNoTracking()
            .Where(c => c.CompanyCode == companyCode
                        && c.SubGroupCode != null
                        && codes.Contains(c.SubGroupCode))
            .GroupBy(c => c.SubGroupCode!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            AddRef(map, row.Code, "Customers", row.Count);
        }

        return FreezeMap(map);
    }

    // ===================== Mapping =====================

    private static SaCustSubGroupListRow MapCustSubGroupRow(SaCustSubGroup x) => new()
    {
        Code = x.CustSubGroupCode,
        Desc = x.CustSubGroupDesc,
        RowVersion = x.RowVersion ?? []
    };

    private static SaShipViaListRow MapShipViaRow(SaShipVia x) => new()
    {
        Code = x.ShipViaCode,
        Desc = x.ShipViaDesc,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion ?? []
    };

    private static SaSOTypeListRow MapSoTypeRow(SaSOType x) => new()
    {
        Code = x.SOTypeCode,
        Desc = x.SOTypeDesc,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion ?? []
    };

    private static SaCommentListRow MapCommentRow(SaComment x) => new()
    {
        Code = x.CommID,
        Comment = x.Comment,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion ?? []
    };

    private static SaShippingLeadTimeListRow MapShippingLeadTimeRow(SaShippingLeadTime x) => new()
    {
        Code = x.LeadTimeCode,
        Desc = x.LeadTimeDesc,
        Days = x.Days,
        Type = x.Type,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion ?? []
    };

    // ===================== Validation / normalisation =====================

    /// <summary>Characters the legacy input validator rejected; a code is an identifier (D8).</summary>
    private const string ForbiddenCodeChars = ";?:@&=+$,%'";

    private static string ValidateAndNormalizeCode(
        Dictionary<string, string> errors,
        string field,
        string label,
        string? raw,
        int maxLength)
    {
        // Leading/trailing whitespace is trimmed silently and never stored; it is not an error (TC16).
        var code = NormalizeMasterCode(raw);
        if (code.Length == 0)
        {
            errors[field] = $"{label} is required.";
            return code;
        }

        if (code.Length > maxLength)
        {
            errors[field] = $"{label} must be at most {maxLength} characters.";
            return code;
        }

        if (code.IndexOfAny(ForbiddenCodeChars.ToCharArray()) >= 0)
        {
            errors[field] = $"{label} contains a character that is not allowed.";
        }

        return code;
    }

    /// <summary>
    /// Description normalisation: trimmed, upper-cased (legacy parity — every entry page
    /// upper-cased code and description on save), required by the service even though the DDL is
    /// permissive (plan §9.2 nullable-vs-required rule).
    /// </summary>
    private static string? ValidateAndNormalizeDescription(
        Dictionary<string, string> errors,
        string field,
        string label,
        string? raw,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            errors[field] = $"{label} is required.";
            return null;
        }

        var value = raw.Trim().ToUpperInvariant();
        if (value.Length > maxLength)
        {
            errors[field] = $"{label} must be at most {maxLength} characters.";
        }

        return value;
    }

    /// <summary>
    /// <c>null</c>, empty and whitespace all normalise to <c>NULL</c>; only INTERNAL/EXTERNAL are
    /// stored non-null (D-5 / TC19).
    /// </summary>
    private static (string? Value, string? Error) NormalizeLeadTimeType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, null);
        }

        var value = raw.Trim().ToUpperInvariant();
        return value is "INTERNAL" or "EXTERNAL"
            ? (value, null)
            : (null, "Type must be INTERNAL or EXTERNAL.");
    }
}
