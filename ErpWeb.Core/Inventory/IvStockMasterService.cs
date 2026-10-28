using ErpWeb.Core.Menus;
using ErpWeb.Core.Pricing;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Inventory;

public sealed class IvStockMasterService : IIvStockMasterService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _dates;
    private readonly IIvStockMasterRepository _stockMasters;
    private readonly IIvStockCommonRepository _common;
    private readonly IIvStockMasterImageService? _imageService;
    private readonly ILogger<IvStockMasterService>? _logger;
    private readonly int _maxImagesPerItem;
    private readonly long _maxPendingGalleryBytes;

    public IvStockMasterService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService dates,
        IIvStockMasterRepository stockMasters,
        IIvStockCommonRepository common,
        IIvStockMasterImageService? imageService = null,
        ILogger<IvStockMasterService>? logger = null,
        IOptions<ItemImageStorageOptions>? imageOptions = null)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _dates = dates;
        _stockMasters = stockMasters;
        _common = common;
        _imageService = imageService;
        _logger = logger;
        _maxImagesPerItem = imageOptions?.Value.MaxImagesPerItem ?? 6;
        _maxPendingGalleryBytes = imageOptions?.Value.MaxPendingGalleryBytes ?? 12L * 1024 * 1024;
    }

    public async Task<IvMasterOperationResult<IvStockMasterListPage>> SearchAsync(
        IvStockMasterListQuery query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<IvStockMasterListPage>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Access, cancellationToken))
        {
            return Fail<IvStockMasterListPage>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        query ??= new IvStockMasterListQuery();
        var args = ToSearchArgs(query);
        var (rows, total) = await _stockMasters.SearchPagedAsync(context.CompanyCode!, args, cancellationToken);
        return IvMasterOperationResult<IvStockMasterListPage>.Ok(new IvStockMasterListPage
        {
            Rows = rows.Select(MapListRow).ToList(),
            TotalCount = total
        });
    }

    public async Task<IvMasterOperationResult<IvStockMasterEditVm>> GetAsync(
        string iCode,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<IvStockMasterEditVm>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Access, cancellationToken))
        {
            return Fail<IvStockMasterEditVm>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var code = (iCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return Fail<IvStockMasterEditVm>(IvMasterErrorCode.Validation, "Item code is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await _stockMasters.GetByCodeAsync(db, context.CompanyCode!, code, cancellationToken);
        if (entity is null)
        {
            return Fail<IvStockMasterEditVm>(IvMasterErrorCode.NotFound, "Item was not found.");
        }

        var model = MapEditVm(entity);
        var structure = await EvaluateStructuralLockAsync(
            db, context.CompanyCode!, entity.ICode, cancellationToken);
        model.StructuralFieldsLocked = structure.IsLocked;
        model.StructuralLockReason = structure.Reason;

        return IvMasterOperationResult<IvStockMasterEditVm>.Ok(model);
    }

    public async Task<IvMasterOperationResult<IvStockMasterEditVm>> SaveAsync(
        IvStockMasterEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default) =>
        await SaveAsync(
            model,
            isNew,
            galleryChanges: (IvStockMasterImageGalleryChangeSet?)null,
            cancellationToken: cancellationToken);

    public Task<IvMasterOperationResult<IvStockMasterEditVm>> SaveAsync(
        IvStockMasterEditVm model,
        bool isNew,
        IvStockMasterImageChange? imageChange,
        CancellationToken cancellationToken = default)
    {
        if (imageChange is not null && imageChange.Replacement is not null && imageChange.RemoveExisting)
        {
            return Task.FromResult(Fail<IvStockMasterEditVm>(
                IvMasterErrorCode.Validation,
                "Item image change is invalid."));
        }

        if (isNew && imageChange?.RemoveExisting == true)
        {
            return Task.FromResult(Fail<IvStockMasterEditVm>(
                IvMasterErrorCode.Validation,
                "A new item does not have an existing image to remove."));
        }

        if (imageChange?.HasChange != true)
        {
            return SaveAsync(
                model,
                isNew,
                galleryChanges: (IvStockMasterImageGalleryChangeSet?)null,
                cancellationToken: cancellationToken);
        }

        IvPendingStockImage[] additions = [];
        IvStockMasterPrimarySelection? selection = null;
        var replaceAll = false;
        var removeAll = false;
        if (imageChange.Replacement is not null)
        {
            var token = Guid.NewGuid();
            additions = [new IvPendingStockImage { Token = token, Image = imageChange.Replacement }];
            selection = new IvStockMasterPrimarySelection { PendingImageToken = token };
            replaceAll = !isNew;
        }
        else if (imageChange.RemoveExisting)
        {
            removeAll = !isNew;
        }

        return SaveAsync(
            model,
            isNew,
            new IvStockMasterImageGalleryChangeSet
            {
                Additions = additions,
                PrimarySelection = selection,
                ReplaceAllExisting = replaceAll,
                RemoveAllExisting = removeAll
            },
            cancellationToken);
    }

    public async Task<IvMasterOperationResult<IvStockMasterEditVm>> SaveAsync(
        IvStockMasterEditVm model,
        bool isNew,
        IvStockMasterImageGalleryChangeSet? galleryChanges,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return Fail<IvStockMasterEditVm>(IvMasterErrorCode.Validation, "Save model is required.");
        }

        if (galleryChanges?.HasChange == true && _imageService is null)
        {
            return Fail<IvStockMasterEditVm>(
                IvMasterErrorCode.Validation,
                "Item image storage is unavailable. The item was not saved.");
        }

        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<IvStockMasterEditVm>(context.ErrorCode.Value, context.Error!);
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        if (!await _accessRights.CanAsync(MenuCodes.InventoryItemMaster, permission, cancellationToken))
        {
            return Fail<IvStockMasterEditVm>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var code = (model.ICode ?? string.Empty).Trim();
        var desc = (model.IDesc ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(code))
        {
            errors["ICode"] = "Item code is required.";
        }
        else if (code.Length > 30)
        {
            errors["ICode"] = "Item code must be at most 30 characters.";
        }

        if (string.IsNullOrWhiteSpace(desc))
        {
            errors["IDesc"] = "Description is required.";
        }
        else if (desc.Length > 200)
        {
            errors["IDesc"] = "Description must be at most 200 characters.";
        }

        if (string.IsNullOrWhiteSpace(model.SellingGlCode))
        {
            errors["SellingGlCode"] = "Selling GL is required.";
        }

        if (model.LotControl && !model.StockControl)
        {
            errors["LotControl"] = "Lot control requires stock control.";
        }

        var expiryControlValid = IvExpiryControlModes.TryNormalize(model.ExpiryControl, out var expiryControl);
        if (!expiryControlValid)
        {
            errors["ExpiryControl"] = $"Expiry control must be one of {string.Join(", ", IvExpiryControlModes.Allowed)}.";
        }
        else if (!model.LotControl && !string.Equals(expiryControl, IvExpiryControlModes.None, StringComparison.Ordinal))
        {
            errors["ExpiryControl"] = "Expiry control must be None when lot control is disabled.";
        }

        if (model.MinStock is not null && model.MaxStock is not null && model.MinStock > model.MaxStock)
        {
            errors["MinStock"] = "Minimum stock cannot be greater than maximum stock.";
        }

        var iType = NullIfWhiteSpace(model.IType);
        var iClass = NullIfWhiteSpace(model.IClassCode);
        var iSubClass = NullIfWhiteSpace(model.ISubClassCode);
        var stdUom = NullIfWhiteSpace(model.StdUom);
        var sellingUom = NullIfWhiteSpace(model.SellingUom);
        var purUom = NullIfWhiteSpace(model.PurUom);
        var defWh = NullIfWhiteSpace(model.DefWarehouse);
        var defLoc = NullIfWhiteSpace(model.DefLocation);
        var classification = NullIfWhiteSpace(model.Classification);

        // Pack factors start as supplied; stock-controlled items are normalized below
        // (same UOM => exactly 1, different UOM => explicit positive factor).
        decimal? stdPackSize = model.StdPackSize;
        decimal? purStdPackSize = model.PurStdPackSize;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // §3.2 — structural requirements for a stock-controlled item. Checked before the UOM
        // defaults so a missing StdUom can never masquerade as a defaulted selling/purchase UOM.
        if (model.StockControl)
        {
            if (iClass is null)
            {
                errors["IClassCode"] = "Item class is required for a stock-controlled item.";
            }

            if (stdUom is null)
            {
                errors["StdUom"] = "Standard UOM is required for a stock-controlled item.";
            }
        }

        if (classification is null)
        {
            errors["Classification"] = "Classification is required.";
        }
        else
        {
            var classificationRow = await _common.GetClassificationAsync(db, classification, cancellationToken);
            if (classificationRow is null)
            {
                errors["Classification"] = $"Classification '{classification}' was not found.";
            }
            else
            {
                classification = classificationRow.Code;
            }
        }

        if (iType is not null)
        {
            var typeRow = await _common.GetActiveTypeAsync(db, context.CompanyCode!, iType, cancellationToken);
            if (typeRow is null)
            {
                errors["IType"] = $"Type '{iType}' was not found or is inactive.";
            }
            else
            {
                iType = typeRow.TypeCode;
            }
        }

        if (iClass is not null)
        {
            var classRow = await _common.GetActiveClassAsync(db, context.CompanyCode!, iClass, cancellationToken);
            if (classRow is null)
            {
                errors["IClassCode"] = $"Class '{iClass}' was not found or is inactive.";
            }
            else
            {
                iClass = classRow.IClassCode;
            }
        }

        if (iSubClass is not null)
        {
            if (iClass is null)
            {
                errors["ISubClassCode"] = "Subclass requires a class.";
            }
            else
            {
                var sub = await _common.GetActiveSubClassAsync(
                    db, context.CompanyCode!, iClass, iSubClass, cancellationToken);
                if (sub is null)
                {
                    errors["ISubClassCode"] = $"Subclass '{iSubClass}' was not found for class '{iClass}'.";
                }
                else
                {
                    iSubClass = sub.ISubClassCode;
                }
            }
        }

        if (stdUom is not null)
        {
            var uom = await _common.GetActiveUomAsync(db, context.CompanyCode!, stdUom, cancellationToken);
            if (uom is null)
            {
                errors["StdUom"] = $"UOM '{stdUom}' was not found or is inactive.";
            }
            else
            {
                stdUom = uom.UomCode;
            }
        }

        // §3.2 items 3 and 4 — a blank selling/purchase UOM defaults to the standard UOM so the
        // common PCS/PCS case needs no extra typing. A different UOM is never invented.
        if (sellingUom is null
            && stdUom is not null
            && !errors.ContainsKey("StdUom"))
        {
            sellingUom = stdUom;
        }

        if (purUom is null
            && stdUom is not null
            && !errors.ContainsKey("StdUom"))
        {
            purUom = stdUom;
        }

        if (sellingUom is not null)
        {
            var uom = await _common.GetActiveUomAsync(db, context.CompanyCode!, sellingUom, cancellationToken);
            if (uom is null)
            {
                errors["SellingUom"] = $"UOM '{sellingUom}' was not found or is inactive.";
            }
            else
            {
                sellingUom = uom.UomCode;
            }
        }

        if (purUom is not null)
        {
            var uom = await _common.GetActiveUomAsync(db, context.CompanyCode!, purUom, cancellationToken);
            if (uom is null)
            {
                errors["PurUom"] = $"UOM '{purUom}' was not found or is inactive.";
            }
            else
            {
                purUom = uom.UomCode;
            }
        }

        // §3.2 items 5 and 6 / §4 — same UOM is exactly 1; a different UOM needs an explicit
        // positive factor. Never silently assume 1 for a different UOM.
        if (model.StockControl && stdUom is not null && !errors.ContainsKey("StdUom"))
        {
            if (sellingUom is not null && !errors.ContainsKey("SellingUom"))
            {
                var sellingFactor = IvItemPackPolicy.Resolve(sellingUom, stdUom, model.StdPackSize);
                if (!sellingFactor.Succeeded)
                {
                    errors["StdPackSize"] = sellingFactor.FailureMessage!;
                }
                else
                {
                    stdPackSize = sellingFactor.Factor;
                }
            }

            if (purUom is not null && !errors.ContainsKey("PurUom"))
            {
                var purchaseFactor = IvItemPackPolicy.Resolve(purUom, stdUom, model.PurStdPackSize);
                if (!purchaseFactor.Succeeded)
                {
                    errors["PurStdPackSize"] = purchaseFactor.FailureMessage!;
                }
                else
                {
                    purStdPackSize = purchaseFactor.Factor;
                }
            }

            // §4.4 — identical selling/purchase UOM cannot mean two different quantities.
            if (!errors.ContainsKey("StdPackSize")
                && !errors.ContainsKey("PurStdPackSize")
                && !IvItemPackPolicy.TryValidatePairConsistency(
                    sellingUom,
                    stdPackSize ?? 0m,
                    purUom,
                    purStdPackSize ?? 0m,
                    out var pairError))
            {
                errors["PurStdPackSize"] = pairError!;
            }
        }

        if (defWh is not null)
        {
            var warehouse = await _common.GetActiveWarehouseAsync(
                db, context.CompanyCode!, context.BranchCode!, defWh, cancellationToken);
            if (warehouse is null)
            {
                errors["DefWarehouse"] = $"Warehouse '{defWh}' was not found for this branch.";
            }
            else
            {
                defWh = warehouse.WarehouseCode;
                if (defLoc is not null)
                {
                    var location = await _common.GetActiveLocationAsync(
                        db,
                        context.CompanyCode!,
                        context.BranchCode!,
                        defWh,
                        defLoc,
                        cancellationToken);
                    if (location is null)
                    {
                        errors["DefLocation"] = $"Location '{defLoc}' was not found for warehouse '{defWh}'.";
                    }
                    else
                    {
                        defLoc = location.LocCode;
                    }
                }
            }
        }
        else if (defLoc is not null)
        {
            errors["DefLocation"] = "Location requires a warehouse.";
        }

        if (isNew)
        {
            if (!errors.ContainsKey("ICode") &&
                await _stockMasters.ExistsAsync(context.CompanyCode!, code, cancellationToken))
            {
                errors["ICode"] = "Item code already exists.";
            }
        }
        else if (model.RowVersion is null || model.RowVersion.Length == 0)
        {
            return Fail<IvStockMasterEditVm>(
                IvMasterErrorCode.Concurrency,
                "Concurrency token is missing. Reload and try again.");
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<IvStockMasterEditVm>.Fail(
                IvMasterErrorCode.Validation,
                "Validation failed.",
                errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return Fail<IvStockMasterEditVm>(
                IvMasterErrorCode.InvalidScope,
                "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var userId = Truncate(writeScope.UserId, 10);
        var newImagePaths = new List<string>();
        var databaseSaved = false;

        try
        {
            if (isNew)
            {
                // §6.8 — reconcile the primary conversions before any external image file is
                // written, so a known conversion conflict cannot leave orphaned files behind.
                var conversionError = await ReconcilePrimaryConversionsAsync(
                    db,
                    context.CompanyCode!,
                    code,
                    existing: null,
                    stdUom,
                    sellingUom,
                    stdPackSize,
                    purUom,
                    purStdPackSize,
                    userId,
                    now,
                    cancellationToken);
                if (conversionError is not null)
                {
                    return Fail<IvStockMasterEditVm>(IvMasterErrorCode.Validation, conversionError);
                }

                var galleryError = TryValidateGalleryChanges(
                    galleryChanges,
                    true,
                    [],
                    null,
                    context.CompanyCode!,
                    code,
                    out var removeIds,
                    out var additions);
                if (galleryError is not null)
                {
                    return Fail<IvStockMasterEditVm>(IvMasterErrorCode.Validation, galleryError);
                }

                if (removeIds.Count > 0)
                {
                    return Fail<IvStockMasterEditVm>(IvMasterErrorCode.Validation, "A new item has no gallery images to remove.");
                }

                var stored = await StoreGalleryAdditionsAsync(
                    additions,
                    context.CompanyCode!,
                    code,
                    newImagePaths,
                    cancellationToken);
                if (!stored.Succeeded || stored.Data is null)
                {
                    return Fail<IvStockMasterEditVm>(
                        stored.ErrorCode == IvMasterErrorCode.None ? IvMasterErrorCode.Validation : stored.ErrorCode,
                        stored.Message ?? "The item images could not be stored.");
                }

                var newImages = BuildGalleryRows(
                    additions,
                    stored.Data,
                    [],
                    context.CompanyCode!,
                    code,
                    now,
                    userId);

                var entity = new IvStockMaster
                {
                    CompanyCode = context.CompanyCode!,
                    ICode = code,
                    ImagePath = ResolvePrimaryImagePath(
                        newImages,
                        currentPrimaryPath: null,
                        galleryChanges,
                        stored.Data),
                    CreatedDate = now,
                    CreatedBy = userId,
                    ModifiedDate = now,
                    ModifiedBy = userId
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                ApplyEditableFields(
                    entity,
                    model,
                    desc,
                    iType,
                    iClass,
                    iSubClass,
                    stdUom,
                    sellingUom,
                    purUom,
                    defWh,
                    defLoc,
                    classification,
                    expiryControl,
                    stdPackSize,
                    purStdPackSize);
                // Do not set RowVersion — database generates it.
                db.IvStockMasters.Add(entity);
                db.IvStockMasterImages.AddRange(newImages);
                AddItemDefaultPriceAudit(
                    db,
                    context.CompanyCode!,
                    userId,
                    entity,
                    _dates.Today,
                    oldPrice: null,
                    newPrice: entity.SellingPrice,
                    oldUom: null,
                    newUom: entity.SellingUom,
                    changeKind: SalesPriceChangeKinds.Create);
                await db.SaveChangesAsync(cancellationToken);
                databaseSaved = true;
                await db.Entry(entity).ReloadAsync(cancellationToken);
                return IvMasterOperationResult<IvStockMasterEditVm>.Ok(MapEditVm(entity));
            }

            var existing = await _stockMasters.GetTrackedAsync(db, context.CompanyCode!, code, cancellationToken);
            if (existing is null)
            {
                return Fail<IvStockMasterEditVm>(IvMasterErrorCode.NotFound, "Item was not found.");
            }

            if (!KeysEqual(existing.ICode, code))
            {
                return Fail<IvStockMasterEditVm>(
                    IvMasterErrorCode.Validation,
                    "Item code cannot be changed.",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ICode"] = "Item code cannot be changed."
                    });
            }

            if (!RowVersionsEqual(existing.RowVersion, model.RowVersion!))
            {
                return Fail<IvStockMasterEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "This item was modified by another user. Your changes were not saved.");
            }

            var structuralError = await EnsureStructuralFieldsUnlockedAsync(
                db,
                context.CompanyCode!,
                existing,
                stdUom,
                model.StockControl,
                model.LotControl,
                cancellationToken);
            if (structuralError is not null)
            {
                return Fail<IvStockMasterEditVm>(IvMasterErrorCode.Validation, structuralError);
            }

            // §6.8 — reconcile the primary conversions before any external image file is written.
            var editConversionError = await ReconcilePrimaryConversionsAsync(
                db,
                context.CompanyCode!,
                code,
                existing,
                stdUom,
                sellingUom,
                stdPackSize,
                purUom,
                purStdPackSize,
                userId,
                now,
                cancellationToken);
            if (editConversionError is not null)
            {
                return Fail<IvStockMasterEditVm>(IvMasterErrorCode.Validation, editConversionError);
            }

            List<IvStockMasterImage> currentImages = galleryChanges?.HasChange == true
                ? await db.IvStockMasterImages
                    .Where(x => x.CompanyCode == context.CompanyCode! && x.ICode == code)
                    .OrderBy(x => x.SortOrder)
                    .ToListAsync(cancellationToken)
                : new List<IvStockMasterImage>();
            var galleryValidationError = TryValidateGalleryChanges(
                galleryChanges,
                false,
                currentImages,
                existing.ImagePath,
                context.CompanyCode!,
                code,
                out var removalIds,
                out var pendingAdditions);
            if (galleryValidationError is not null)
            {
                return Fail<IvStockMasterEditVm>(IvMasterErrorCode.Validation, galleryValidationError);
            }

            var oldSellingPrice = existing.SellingPrice;
            var oldSellingUom = existing.SellingUom;
            var storedGallery = await StoreGalleryAdditionsAsync(
                pendingAdditions,
                context.CompanyCode!,
                code,
                newImagePaths,
                cancellationToken);
            if (!storedGallery.Succeeded || storedGallery.Data is null)
            {
                return Fail<IvStockMasterEditVm>(
                    storedGallery.ErrorCode == IvMasterErrorCode.None ? IvMasterErrorCode.Validation : storedGallery.ErrorCode,
                    storedGallery.Message ?? "The item images could not be stored.");
            }

            var addedImages = BuildGalleryRows(
                pendingAdditions,
                storedGallery.Data,
                currentImages,
                context.CompanyCode!,
                code,
                now,
                userId);
            var removedImages = currentImages.Where(x => removalIds.Contains(x.Uid)).ToList();
            var finalImages = currentImages.Where(x => !removalIds.Contains(x.Uid)).Concat(addedImages).ToList();

            var entry = db.Entry(existing);
            entry.Property(x => x.RowVersion).OriginalValue = model.RowVersion!;

            ApplyEditableFields(
                existing,
                model,
                desc,
                iType,
                iClass,
                iSubClass,
                stdUom,
                sellingUom,
                purUom,
                defWh,
                defLoc,
                classification,
                expiryControl,
                stdPackSize,
                purStdPackSize);
            if (galleryChanges?.HasChange == true)
            {
                existing.ImagePath = ResolvePrimaryImagePath(
                    finalImages,
                    existing.ImagePath,
                    galleryChanges,
                    storedGallery.Data);
                db.IvStockMasterImages.RemoveRange(removedImages);
                db.IvStockMasterImages.AddRange(addedImages);
            }

            // Leftover BranchCode / LocationCode: do not touch on update.
            existing.ModifiedDate = now;
            existing.ModifiedBy = userId;

            AddItemDefaultPriceAudit(
                db,
                context.CompanyCode!,
                userId,
                existing,
                _dates.Today,
                oldSellingPrice,
                existing.SellingPrice,
                oldSellingUom,
                existing.SellingUom,
                changeKind: SalesPriceChangeKinds.Update);

            await db.SaveChangesAsync(cancellationToken);
            databaseSaved = true;
            await db.Entry(existing).ReloadAsync(cancellationToken);
            foreach (var oldImagePath in removedImages
                         .Select(x => x.ImagePath)
                         .Where(x => !string.IsNullOrWhiteSpace(x))
                         .Distinct(StringComparer.Ordinal))
            {
                await TryCleanupImageAsync(
                    oldImagePath,
                    context.CompanyCode!,
                    code,
                    "item-remove");
            }

            return IvMasterOperationResult<IvStockMasterEditVm>.Ok(MapEditVm(existing));
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!databaseSaved)
            {
                await CleanupNewImageFilesAsync(newImagePaths, context.CompanyCode!, code, "item-save-rollback");
            }

            return Fail<IvStockMasterEditVm>(
                IvMasterErrorCode.Concurrency,
                "This item was modified by another user. Your changes were not saved.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            if (!databaseSaved)
            {
                await CleanupNewImageFilesAsync(newImagePaths, context.CompanyCode!, code, "item-save-rollback");
            }

            return Fail<IvStockMasterEditVm>(
                IvMasterErrorCode.DuplicateKey,
                "Item code already exists.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ICode"] = "Item code already exists."
                });
        }
        catch
        {
            if (!databaseSaved)
            {
                await CleanupNewImageFilesAsync(newImagePaths, context.CompanyCode!, code, "item-save-rollback");
            }

            throw;
        }
    }

    public async Task<IvMasterOperationResult<object>> SetActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<object>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Edit, cancellationToken))
        {
            return Fail<object>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        if (items is null || items.Count == 0)
        {
            return Fail<object>(IvMasterErrorCode.Validation, "No records selected.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var tracked = new List<(IvStockMaster Entity, byte[] Token)>();
            var stale = 0;

            foreach (var item in items)
            {
                var code = (item.Code ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(code))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return Fail<object>(IvMasterErrorCode.Validation, "Item code is required.");
                }

                var entity = await _stockMasters.GetTrackedAsync(db, context.CompanyCode!, code, cancellationToken);
                if (entity is null || !RowVersionsEqual(entity.RowVersion, item.RowVersion))
                {
                    stale++;
                    continue;
                }

                tracked.Add((entity, item.RowVersion));
            }

            if (stale > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return Fail<object>(
                    IvMasterErrorCode.Concurrency,
                    $"{stale} selected item(s) were modified by another user. No records were changed. Please reload and try again.");
            }

            var now = _dates.Now;
            var userId = Truncate(context.UserId!, 10);
            foreach (var (entity, token) in tracked)
            {
                db.Entry(entity).Property(x => x.RowVersion).OriginalValue = token;
                entity.IsActive = isActive;
                entity.ModifiedDate = now;
                entity.ModifiedBy = userId;
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Fail<object>(
                IvMasterErrorCode.Concurrency,
                "One or more items were modified by another user. No records were changed. Please reload and try again.");
        }
    }

    public async Task<DeleteCheckResult> CanDeleteBulkAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return DeleteCheckResult.Blocked(context.Error!, []);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Delete, cancellationToken))
        {
            return DeleteCheckResult.Blocked("Not authorized.", []);
        }

        var codeList = (codes ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (codeList.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        var refs = await _stockMasters.CountReferencesBulkAsync(context.CompanyCode!, codeList, cancellationToken);
        var hits = new List<IvMasterReferenceHit>();
        foreach (var code in codeList)
        {
            if (!refs.TryGetValue(code, out var list) || list.Count == 0)
            {
                continue;
            }

            foreach (var hit in list)
            {
                hits.Add(new IvMasterReferenceHit
                {
                    ReferenceType = hit.ReferenceType,
                    Count = hit.Count,
                    Detail = code
                });
            }
        }

        if (hits.Count > 0)
        {
            return DeleteCheckResult.Blocked(
                "One or more items are referenced by inventory transactions or balances.",
                hits);
        }

        return DeleteCheckResult.Ok();
    }

    public async Task<IvMasterOperationResult<object>> DeleteAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<object>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Delete, cancellationToken))
        {
            return Fail<object>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        if (items is null || items.Count == 0)
        {
            return Fail<object>(IvMasterErrorCode.Validation, "No records selected.");
        }

        var userId = Truncate(context.UserId ?? string.Empty, 10);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var codes = new List<string>();
            var entities = new List<IvStockMaster>();
            var imagePaths = new List<(string Code, string? Path)>();
            var stale = 0;

            foreach (var item in items)
            {
                var code = (item.Code ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(code))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return Fail<object>(IvMasterErrorCode.Validation, "Item code is required.");
                }

                var entity = await _stockMasters.GetTrackedAsync(db, context.CompanyCode!, code, cancellationToken);
                if (entity is null || !RowVersionsEqual(entity.RowVersion, item.RowVersion))
                {
                    stale++;
                    continue;
                }

                db.Entry(entity).Property(x => x.RowVersion).OriginalValue = item.RowVersion;
                codes.Add(code);
                entities.Add(entity);
                imagePaths.Add((code, entity.ImagePath));
                var galleryPaths = await db.IvStockMasterImages
                    .AsNoTracking()
                    .Where(x => x.CompanyCode == context.CompanyCode! && x.ICode == code)
                    .Select(x => x.ImagePath)
                    .ToListAsync(cancellationToken);
                imagePaths.AddRange(galleryPaths.Select(path => (code, (string?)path)));
            }

            if (stale > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return Fail<object>(
                    IvMasterErrorCode.Concurrency,
                    $"{stale} selected item(s) were modified by another user. No records were changed. Please reload and try again.");
            }

            var refs = await _stockMasters.CountReferencesBulkAsync(context.CompanyCode!, codes, cancellationToken);
            var hits = new List<IvMasterReferenceHit>();
            foreach (var code in codes)
            {
                if (!refs.TryGetValue(code, out var list) || list.Count == 0)
                {
                    continue;
                }

                foreach (var hit in list)
                {
                    hits.Add(new IvMasterReferenceHit
                    {
                        ReferenceType = hit.ReferenceType,
                        Count = hit.Count,
                        Detail = code
                    });
                }
            }

            if (hits.Count > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                var check = DeleteCheckResult.Blocked(
                    "One or more items are referenced by inventory transactions or balances.",
                    hits);
                return IvMasterOperationResult<object>.Fail(
                    IvMasterErrorCode.InUse,
                    check.Message!,
                    deleteCheck: check);
            }

            foreach (var entity in entities)
            {
                AddItemDefaultPriceAudit(
                    db,
                    context.CompanyCode!,
                    userId,
                    entity,
                    _dates.Today,
                    entity.SellingPrice,
                    newPrice: null,
                    oldUom: entity.SellingUom,
                    newUom: null,
                    changeKind: SalesPriceChangeKinds.Delete);
            }

            db.IvStockMasters.RemoveRange(entities);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            foreach (var (code, imagePath) in imagePaths
                         .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                         .Distinct())
            {
                await TryCleanupImageAsync(imagePath, context.CompanyCode!, code, "item-delete");
            }

            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Fail<object>(
                IvMasterErrorCode.Concurrency,
                "One or more items were modified by another user. No records were changed. Please reload and try again.");
        }
        catch (DbUpdateException ex) when (IsForeignKeyViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return Fail<object>(
                IvMasterErrorCode.InUse,
                "One or more items are referenced by inventory transactions or balances.");
        }
    }

    public async Task<IvMasterOperationResult<IvStockMasterListPage>> ExportRowsAsync(
        IvStockMasterListQuery query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<IvStockMasterListPage>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Export, cancellationToken))
        {
            return Fail<IvStockMasterListPage>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        query ??= new IvStockMasterListQuery();
        var args = ToSearchArgs(query);
        var count = await _stockMasters.CountExportAsync(context.CompanyCode!, args, cancellationToken);
        if (count > IvStockMasterRepository.MaxExportRows)
        {
            return Fail<IvStockMasterListPage>(
                IvMasterErrorCode.Validation,
                $"Export exceeds the maximum of {IvStockMasterRepository.MaxExportRows:N0} rows. Narrow your filters and try again.");
        }

        var rows = await _stockMasters.ListExportAsync(context.CompanyCode!, args, cancellationToken);
        return IvMasterOperationResult<IvStockMasterListPage>.Ok(new IvStockMasterListPage
        {
            Rows = rows.Select(MapListRow).ToList(),
            TotalCount = count
        });
    }

    /// <summary>Structural-lock field names covered by <see cref="EvaluateStructuralLockAsync"/>.</summary>
    private const string StructuralLockMessage =
        "Stock control, standard UOM, and lot control cannot be changed because {0}. Use a controlled inventory conversion/migration process.";

    /// <summary>
    /// Save-time structural guard (plan §5.5). Returns a blocking message only when a covered field
    /// actually changes, so an unrelated edit on a locked item is never rejected.
    /// </summary>
    private static async Task<string?> EnsureStructuralFieldsUnlockedAsync(
        AppDbContext db,
        string companyCode,
        IvStockMaster existing,
        string? requestedStdUom,
        bool requestedStockControl,
        bool requestedLotControl,
        CancellationToken cancellationToken)
    {
        var stdChanged = !string.Equals(
            (existing.StdUom ?? string.Empty).Trim(),
            (requestedStdUom ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);
        var stockChanged = existing.StockControl != requestedStockControl;
        var lotChanged = existing.LotControl != requestedLotControl;
        if (!stdChanged && !stockChanged && !lotChanged)
            return null;

        var structure = await EvaluateStructuralLockAsync(
            db, companyCode, existing.ICode, cancellationToken);
        return structure.IsLocked ? structure.Reason : null;
    }

    /// <summary>
    /// Read-only structural dependency evaluator (plan §5.1-§5.4). One authoritative place decides
    /// whether <c>StdUom</c>, <c>StockControl</c>, and <c>LotControl</c> may still change; both the
    /// edit page and <see cref="SaveAsync"/> consume it so the UI can never invent a different rule.
    /// </summary>
    private static async Task<IvStockStructureState> EvaluateStructuralLockAsync(
        AppDbContext db,
        string companyCode,
        string iCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(iCode))
        {
            return IvStockStructureState.Unlocked;
        }

        // §5.1 — existing inventory footprint. Zero-quantity balances still count.
        var hasBalLoc = await db.IvBalLocs.AsNoTracking()
            .AnyAsync(x => x.CompanyCode == companyCode && x.ICode == iCode, cancellationToken);
        if (hasBalLoc)
        {
            return IvStockStructureState.Locked(
                string.Format(StructuralLockMessage, "this item already has inventory balance rows"),
                "InventoryBalances");
        }

        var hasHistory = await db.IvTrxHistories.AsNoTracking()
            .AnyAsync(x => x.CompanyCode == companyCode && x.ICode == iCode, cancellationToken);
        if (hasHistory)
        {
            return IvStockStructureState.Locked(
                string.Format(StructuralLockMessage, "this item already has inventory history"),
                "InventoryHistory");
        }

        var hasDraft = await (
            from d in db.IvTrxBatchDetails.AsNoTracking()
            join b in db.IvTrxBatches.AsNoTracking() on d.BatchId equals b.Id
            where d.CompanyCode == companyCode
                  && d.ICode == iCode
                  && b.BatchStatus == IvBatchStatuses.New
                  && b.DeletedAtUtc == null
            select d.Id).AnyAsync(cancellationToken);
        if (hasDraft)
        {
            return IvStockStructureState.Locked(
                string.Format(StructuralLockMessage, "this item has NEW inventory draft transactions"),
                "InventoryDraft");
        }

        // §5.2 — purchase request lines that can still be ordered (cancelled / fully ordered do not
        // lock). Consumption is derived from live PO coverage, so FULLY_ORDERED is the terminal line.
        var hasOpenPr = await (
            from d in db.PoPrDetails.AsNoTracking()
            join h in db.PoPrs.AsNoTracking()
                on new { d.CompanyCode, d.PrNo } equals new { h.CompanyCode, h.PrNo }
            where d.CompanyCode == companyCode
                  && d.ICode == iCode
                  && h.Status != PoPrStatuses.Cancelled
                  && d.Status != PoPrStatuses.Cancelled
                  && d.Status != PoPrStatuses.FullyOrdered
            select d.PrNo).AnyAsync(cancellationToken);
        if (hasOpenPr)
        {
            return IvStockStructureState.Locked(
                string.Format(StructuralLockMessage, "this item has an open purchase request"),
                "OpenPurchaseRequest");
        }

        // §5.2 — only the LATEST operational PO revision with remaining quantity locks, so a
        // superseded historical revision never freezes the item forever.
        var openPoLines = await (
            from d in db.PoOrderDetails.AsNoTracking()
            join h in db.PoOrders.AsNoTracking()
                on new { d.CompanyCode, d.PoNo, d.PoRelNo } equals new { h.CompanyCode, h.PoNo, h.PoRelNo }
            where d.CompanyCode == companyCode
                  && d.ICode == iCode
                  && h.Status != PoOrderStatuses.Cancelled
                  && h.Status != PoOrderStatuses.Closed
                  && !h.FinClosed
                  && (d.BalanceQty > 0m
                      || d.PoQty - d.RecvQty - d.ReturnQty - d.ReturnQtyCn > 0m)
            select new { d.PoNo, d.PoRelNo }).Distinct().ToListAsync(cancellationToken);
        if (openPoLines.Count > 0)
        {
            var poNos = openPoLines.Select(x => x.PoNo).Distinct().ToArray();
            var latestRevisions = await db.PoOrders.AsNoTracking()
                .Where(h => h.CompanyCode == companyCode && poNos.Contains(h.PoNo))
                .GroupBy(h => h.PoNo)
                .Select(g => new { PoNo = g.Key, PoRelNo = g.Max(x => x.PoRelNo) })
                .ToListAsync(cancellationToken);
            var latestByPo = latestRevisions
                .GroupBy(x => x.PoNo, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(x => x.PoRelNo), StringComparer.OrdinalIgnoreCase);

            if (openPoLines.Any(x =>
                    latestByPo.TryGetValue(x.PoNo, out var latest) && latest == x.PoRelNo))
            {
                return IvStockStructureState.Locked(
                    string.Format(StructuralLockMessage, "this item has an operational purchase order with remaining quantity"),
                    "OpenPurchaseOrder");
            }
        }

        // §5.3 — current quotation with remaining convertible quantity.
        var hasOpenQt = await (
            from d in db.SaQtDetails.AsNoTracking()
            join h in db.SaQts.AsNoTracking()
                on new { d.CompanyCode, d.QtNo } equals new { h.CompanyCode, h.QtNo }
            where d.CompanyCode == companyCode
                  && d.ICode == iCode
                  && h.IsCurrent
                  && (h.Status == SaQtStatuses.New
                      || h.Status == SaQtStatuses.Sent
                      || h.Status == SaQtStatuses.Accepted)
                  && d.OrderQty - d.ConvertedQty > 0m
            select d.QtNo).AnyAsync(cancellationToken);
        if (hasOpenQt)
        {
            return IvStockStructureState.Locked(
                string.Format(StructuralLockMessage, "this item has a current quotation with remaining convertible quantity"),
                "OpenQuotation");
        }

        // §5.3 — current, non-shipped Sales Order with remaining fulfilment.
        var hasOpenSo = await (
            from d in db.SaSoDetails.AsNoTracking()
            join h in db.SaSos.AsNoTracking()
                on new { d.CompanyCode, d.SoNo } equals new { h.CompanyCode, h.SoNo }
            where d.CompanyCode == companyCode
                  && d.ICode == iCode
                  && h.IsCurrent
                  && h.Status == SaSoStatuses.New
                  && d.BalanceQty > 0m
            select d.SoNo).AnyAsync(cancellationToken);
        if (hasOpenSo)
        {
            return IvStockStructureState.Locked(
                string.Format(StructuralLockMessage, "this item has an open sales order with remaining quantity"),
                "OpenSalesOrder");
        }

        // §5.3 — operational Delivery Order (CLOSED is terminal).
        var hasOpenDo = await (
            from d in db.SaDoDetails.AsNoTracking()
            join h in db.SaDos.AsNoTracking()
                on new { d.CompanyCode, d.DoNo } equals new { h.CompanyCode, h.DoNo }
            where d.CompanyCode == companyCode
                  && d.ICode == iCode
                  && h.Status != SaDoStatuses.Closed
            select d.DoNo).AnyAsync(cancellationToken);
        if (hasOpenDo)
        {
            return IvStockStructureState.Locked(
                string.Format(StructuralLockMessage, "this item has an operational delivery order"),
                "OpenDeliveryOrder");
        }

        // §5.3 — operational Invoice line (invoices are permanent records in this system).
        var hasInvoice = await (
            from d in db.SaInvoiceDetails.AsNoTracking()
            join h in db.SaInvoices.AsNoTracking()
                on new { d.CompanyCode, d.InvNo } equals new { h.CompanyCode, h.InvNo }
            where d.CompanyCode == companyCode && d.ICode == iCode
            select d.InvNo).AnyAsync(cancellationToken);
        if (hasInvoice)
        {
            return IvStockStructureState.Locked(
                string.Format(StructuralLockMessage, "this item has an operational sales invoice"),
                "OpenInvoice");
        }

        // §5.3 — active Delivery Request source feeding production demand.
        var hasActiveDeliveryRequest = await (
            from s in db.SaDeliveryRequestSources.AsNoTracking()
            join h in db.SaDeliveryRequests.AsNoTracking() on s.DeliveryRequestId equals h.Uid
            where s.CompanyCode == companyCode
                  && s.ProductCode == iCode
                  && s.IsActive
                  && h.Status != SaDeliveryRequestStatuses.Cancelled
                  && h.Status != SaDeliveryRequestStatuses.Completed
            select s.Uid).AnyAsync(cancellationToken);
        if (hasActiveDeliveryRequest)
        {
            return IvStockStructureState.Locked(
                string.Format(StructuralLockMessage, "this item has an active delivery request source"),
                "ActiveDeliveryRequest");
        }

        // §5.4 — non-terminal Work Order as the produced item.
        var hasWorkOrderProduct = await db.ProductionWorkOrders.AsNoTracking()
            .AnyAsync(
                x => x.CompanyCode == companyCode
                     && x.ProductCode == iCode
                     && x.Status != ProductionWorkOrderStatuses.Closed
                     && x.Status != ProductionWorkOrderStatuses.Cancelled,
                cancellationToken);
        if (hasWorkOrderProduct)
        {
            return IvStockStructureState.Locked(
                string.Format(StructuralLockMessage, "this item is the product of a non-terminal production work order"),
                "OpenWorkOrderProduct");
        }

        // §5.4 — non-terminal Work Order material usage.
        var hasWorkOrderMaterial = await (
            from m in db.ProductionWorkOrderMaterials.AsNoTracking()
            join w in db.ProductionWorkOrders.AsNoTracking() on m.WorkOrderId equals w.Uid
            where w.CompanyCode == companyCode
                  && m.ComponentCode == iCode
                  && w.Status != ProductionWorkOrderStatuses.Closed
                  && w.Status != ProductionWorkOrderStatuses.Cancelled
            select m.Uid).AnyAsync(cancellationToken);
        if (hasWorkOrderMaterial)
        {
            return IvStockStructureState.Locked(
                string.Format(StructuralLockMessage, "this item is a material of a non-terminal production work order"),
                "OpenWorkOrderMaterial");
        }

        // §5.4 — live production balance for the item.
        var hasProductionBalance = await db.ProductionBalLots.AsNoTracking()
            .AnyAsync(
                x => x.CompanyCode == companyCode && x.ItemCode == iCode && x.Qty > 0m,
                cancellationToken);
        if (hasProductionBalance)
        {
            return IvStockStructureState.Locked(
                string.Format(StructuralLockMessage, "this item has a positive production balance"),
                "ProductionBalance");
        }

        return IvStockStructureState.Unlocked;
    }

    /// <summary>
    /// Reconciles the two primary direct conversions (selling→standard, purchase→standard) with the
    /// existing <c>IvItemUomConversion</c> rows (plan §6). Returns a blocking message, or
    /// <c>null</c> when the pair is consistent, absent, or safely updated in place.
    /// </summary>
    private static async Task<string?> ReconcilePrimaryConversionsAsync(
        AppDbContext db,
        string companyCode,
        string itemCode,
        IvStockMaster? existing,
        string? stdUom,
        string? sellingUom,
        decimal? sellingFactor,
        string? purUom,
        decimal? purchaseFactor,
        string userId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var standard = IvItemPackPolicy.NormalizeUomOrNull(stdUom);
        if (standard is null)
        {
            return null;
        }

        var sellingError = await ReconcilePairAsync(
            db,
            companyCode,
            itemCode,
            existing,
            existing is null ? null : (existing.SellingUom, existing.StdUom, existing.StdPackSize),
            sellingUom,
            standard,
            sellingFactor,
            "Selling",
            userId,
            now,
            cancellationToken);
        if (sellingError is not null)
        {
            return sellingError;
        }

        return await ReconcilePairAsync(
            db,
            companyCode,
            itemCode,
            existing,
            existing is null ? null : (existing.PurUom, existing.StdUom, existing.PurStdPackSize),
            purUom,
            standard,
            purchaseFactor,
            "Purchase",
            userId,
            now,
            cancellationToken);
    }

    private static async Task<string?> ReconcilePairAsync(
        AppDbContext db,
        string companyCode,
        string itemCode,
        IvStockMaster? existing,
        (string? SourceUom, string? StdUom, decimal? Factor)? oldPrimary,
        string? sourceUom,
        string stdUom,
        decimal? requestedFactor,
        string pairLabel,
        string userId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var from = IvItemPackPolicy.NormalizeUomOrNull(sourceUom);
        if (from is null || IvItemPackPolicy.SameUom(from, stdUom))
        {
            // §6.7 — identity pair: factor is exactly 1 and no conversion row is required.
            return null;
        }

        var to = IvItemPackPolicy.NormalizeUom(stdUom);
        var factor = requestedFactor is > 0m ? IvItemPackPolicy.RoundFactor(requestedFactor.Value) : 0m;
        if (factor <= 0m)
        {
            // Validation already rejected this; never create or rewrite a conversion from a bad factor.
            return null;
        }

        // §6.1 — load active rows in BOTH directions; either direction is the same logical pair.
        var rows = await db.IvItemUomConversions
            .Where(x => x.CompanyCode == companyCode
                        && x.ItemCode == itemCode
                        && x.IsActive
                        && ((x.FromUom == from && x.ToUom == to)
                            || (x.FromUom == to && x.ToUom == from)))
            .ToListAsync(cancellationToken);

        // §6.2 — logical duplicates: fail, never guess and never auto-delete a row.
        if (rows.Count > 1)
        {
            return $"Item {itemCode} has multiple active {from}/{to} conversion rows. Resolve the conversion conflict before saving.";
        }

        // §6.3 — no pair yet: create the canonical row in the same unit of work.
        if (rows.Count == 0)
        {
            db.IvItemUomConversions.Add(new IvItemUomConversion
            {
                CompanyCode = companyCode,
                ItemCode = itemCode,
                FromUom = from,
                ToUom = to,
                FromQty = 1m,
                ToQty = factor,
                RoundingScale = IvQty.Scale,
                RoundingMode = IvUomRoundingModes.AwayFromZero,
                IsActive = true,
                CreatedDate = now,
                CreatedBy = TruncateOptional(userId, 10),
                ModifiedDate = now,
                ModifiedBy = TruncateOptional(userId, 10)
            });
            return null;
        }

        var row = rows[0];
        var rowIsForward = string.Equals(
            (row.FromUom ?? string.Empty).Trim(), from, StringComparison.OrdinalIgnoreCase);
        var effectiveFactor = rowIsForward
            ? SafeDivide(row.ToQty, row.FromQty)
            : SafeDivide(row.FromQty, row.ToQty);

        if (effectiveFactor is null)
        {
            return $"The existing approved {from}/{to} conversion for item {itemCode} has a non-positive quantity pair. Correct the conversion before saving.";
        }

        // §6.4 — already consistent: accept without rewriting.
        if (IvItemPackPolicy.FactorsEqual(effectiveFactor.Value, factor))
        {
            return null;
        }

        // §6.5 Case A — the row represented the item's OLD saved primary factor for the SAME pair,
        // and the user is deliberately changing that primary factor. Update that one row atomically
        // while preserving identity, rounding policy, and audit fields.
        var oldMatchesPair = oldPrimary is { } old
            && IvItemPackPolicy.SameUom(old.SourceUom, from)
            && IvItemPackPolicy.SameUom(old.StdUom, to)
            && old.Factor is > 0m
            && IvItemPackPolicy.FactorsEqual(effectiveFactor.Value, old.Factor.Value);

        if (oldMatchesPair)
        {
            if (rowIsForward)
            {
                row.FromQty = 1m;
                row.ToQty = factor;
            }
            else
            {
                row.FromQty = factor;
                row.ToQty = 1m;
            }

            row.ModifiedDate = now;
            row.ModifiedBy = TruncateOptional(userId, 10);
            return null;
        }

        // §6.5 Case B — anything else is a real conflict that Stock Master must not "repair".
        return $"{pairLabel} pack size conflicts with the existing approved {from}/{to} conversion. No data was changed.";
    }

    private static decimal? SafeDivide(decimal numerator, decimal denominator) =>
        denominator == 0m ? null : numerator / denominator;

    private static StockMasterSearchArgs ToSearchArgs(IvStockMasterListQuery query) =>
        new(
            query.SearchText,
            query.IsActive,
            query.IClassCode,
            query.ISubClassCode,
            query.IType,
            query.DefWarehouse,
            query.Brand,
            query.SortField,
            query.SortDescending,
            query.Skip,
            query.Take);

    private static void ApplyEditableFields(
        IvStockMaster entity,
        IvStockMasterEditVm model,
        string desc,
        string? iType,
        string? iClass,
        string? iSubClass,
        string? stdUom,
        string? sellingUom,
        string? purUom,
        string? defWh,
        string? defLoc,
        string? classification,
        string expiryControl,
        decimal? stdPackSize,
        decimal? purStdPackSize)
    {
        entity.IDesc = TruncateOptional(desc, 200);
        entity.Barcode = TruncateOptional(model.Barcode, 50);
        entity.Brand = TruncateOptional(model.Brand, 50);
        entity.IsActive = model.IsActive;
        entity.IType = TruncateOptional(iType, 20);
        entity.IClassCode = TruncateOptional(iClass, 30);
        entity.ISubClassCode = TruncateOptional(iSubClass, 30);
        entity.StdUom = TruncateOptional(stdUom, 10);
        entity.SellingUom = TruncateOptional(sellingUom, 10);
        entity.PurUom = TruncateOptional(purUom, 10);
        entity.StockControl = model.StockControl;
        entity.LotControl = model.LotControl;
        entity.ExpiryControl = expiryControl;
        entity.DefWarehouse = TruncateOptional(defWh, 20);
        entity.DefLocation = TruncateOptional(defLoc, 10);
        entity.MinStock = model.MinStock;
        entity.MaxStock = model.MaxStock;
        entity.StdPackSize = stdPackSize;
        entity.PurStdPackSize = purStdPackSize;
        entity.SellingPrice = model.SellingPrice;
        entity.PurchasePrice = model.PurchasePrice;
        entity.SellingGlCode = TruncateOptional(model.SellingGlCode, 20);
        entity.PurchaseGlCode = TruncateOptional(model.PurchaseGlCode, 20);
        entity.TaxGroup = TruncateOptional(model.TaxGroup, 20);
        entity.PurchaseTaxGroup = TruncateOptional(model.PurchaseTaxGroup, 20);
        entity.Classification = TruncateOptional(classification, 50);
        entity.Size = TruncateOptional(model.Size, 50);
        entity.Color = TruncateOptional(model.Color, 50);
    }

    private static void AddItemDefaultPriceAudit(
        AppDbContext db,
        string companyCode,
        string changedBy,
        IvStockMaster item,
        DateTime effectiveDate,
        decimal? oldPrice,
        decimal? newPrice,
        string? oldUom,
        string? newUom,
        string changeKind)
    {
        if (oldPrice == newPrice && string.Equals(oldUom, newUom, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (oldPrice is null && newPrice is null)
        {
            return;
        }

        db.SaPriceChangeBatches.Add(
            SalesPriceChangeAuditFactory.BuildBatch(
                new SalesPriceChangeAuditContext(
                    companyCode,
                    SalesPriceChangeAuditOrigins.ItemMaster,
                    SalesPriceChangeAuditTargets.ItemDefault,
                    effectiveDate,
                    changedBy,
                    AdjustmentMethod: SalesPriceChangeAuditAdjustmentMethods.DirectEdit),
                [new SalesPriceChangeAuditLine(
                    changeKind,
                    item.ICode,
                    item.IDesc,
                    item.IType,
                    item.IClassCode,
                    item.ISubClassCode,
                    item.Brand,
                    OldUom: oldUom,
                    NewUom: newUom,
                    OldPrice: oldPrice,
                    NewPrice: newPrice)]));
    }

    private static IvStockMasterListRow MapListRow(IvStockMaster x) =>
        new()
        {
            ICode = x.ICode,
            IDesc = x.IDesc,
            MfgType = string.IsNullOrWhiteSpace(x.MfgType) ? "BUY" : x.MfgType,
            Barcode = x.Barcode,
            Brand = x.Brand,
            DefWarehouse = x.DefWarehouse,
            IClassCode = x.IClassCode,
            ISubClassCode = x.ISubClassCode,
            IType = x.IType,
            StdUom = x.StdUom,
            SellingUom = x.SellingUom,
            PurUom = x.PurUom,
            SellingGlCode = x.SellingGlCode,
            PurchaseGlCode = x.PurchaseGlCode,
            Classification = x.Classification,
            IsActive = x.IsActive,
            SellingPrice = x.SellingPrice,
            PurchasePrice = x.PurchasePrice,
            MinStock = x.MinStock,
            MaxStock = x.MaxStock,
            RowVersion = x.RowVersion ?? [],
            CreatedDate = x.CreatedDate,
            CreatedBy = x.CreatedBy,
            ModifiedDate = x.ModifiedDate,
            ModifiedBy = x.ModifiedBy
        };

    private static IvStockMasterEditVm MapEditVm(IvStockMaster x) =>
        new()
        {
            ICode = x.ICode,
            IDesc = x.IDesc,
            HasImage = !string.IsNullOrWhiteSpace(x.ImagePath),
            MfgType = string.IsNullOrWhiteSpace(x.MfgType) ? "BUY" : x.MfgType,
            Barcode = x.Barcode,
            Brand = x.Brand,
            IsActive = x.IsActive,
            IType = x.IType,
            IClassCode = x.IClassCode,
            ISubClassCode = x.ISubClassCode,
            StdUom = x.StdUom,
            SellingUom = x.SellingUom,
            PurUom = x.PurUom,
            StockControl = x.StockControl,
            LotControl = x.LotControl,
            ExpiryControl = CanonicalExpiryControl(x.ExpiryControl),
            DefWarehouse = x.DefWarehouse,
            DefLocation = x.DefLocation,
            MinStock = x.MinStock,
            MaxStock = x.MaxStock,
            StdPackSize = x.StdPackSize,
            PurStdPackSize = x.PurStdPackSize,
            SellingPrice = x.SellingPrice,
            PurchasePrice = x.PurchasePrice,
            SellingGlCode = x.SellingGlCode,
            PurchaseGlCode = x.PurchaseGlCode,
            TaxGroup = x.TaxGroup,
            PurchaseTaxGroup = x.PurchaseTaxGroup,
            Classification = x.Classification,
            Size = x.Size,
            Color = x.Color,
            RowVersion = x.RowVersion,
            CreatedDate = x.CreatedDate,
            CreatedBy = x.CreatedBy,
            ModifiedDate = x.ModifiedDate,
            ModifiedBy = x.ModifiedBy
        };

    private static string CanonicalExpiryControl(string? value) =>
        IvExpiryControlModes.TryNormalize(value, out var normalized)
            ? normalized
            : IvExpiryControlModes.None;

    private static IvMasterOperationResult<IvStockMasterEditVm> ImageFailure(
        IvMasterOperationResult<IvStoredStockImage> result) =>
        Fail<IvStockMasterEditVm>(
            result.ErrorCode == IvMasterErrorCode.None ? IvMasterErrorCode.Validation : result.ErrorCode,
            result.Message ?? "The item image could not be stored.",
            result.ValidationErrors);

    private string? TryValidateGalleryChanges(
        IvStockMasterImageGalleryChangeSet? changes,
        bool isNew,
        IReadOnlyList<IvStockMasterImage> currentImages,
        string? currentPrimaryPath,
        string companyCode,
        string itemCode,
        out HashSet<long> removalIds,
        out IReadOnlyList<IvPendingStockImage> additions)
    {
        removalIds = [];
        additions = [];
        if (changes?.HasChange != true)
        {
            return null;
        }

        if (changes.ReplaceAllExisting && changes.RemoveAllExisting)
        {
            return "Item image gallery change is invalid.";
        }

        if (isNew && (changes.RemoveAllExisting
            || changes.ReplaceAllExisting
            || (changes.RemoveImageIds?.Count ?? 0) > 0))
        {
            return "A new item has no gallery images to remove.";
        }

        if (!isNew)
        {
            var primaryMatches = !string.IsNullOrWhiteSpace(currentPrimaryPath)
                && currentImages.Any(x => string.Equals(x.ImagePath, currentPrimaryPath, StringComparison.Ordinal));
            var emptyIsConsistent = currentImages.Count == 0 && string.IsNullOrWhiteSpace(currentPrimaryPath);
            if (!primaryMatches && !emptyIsConsistent)
            {
                _logger?.LogWarning(
                    "Stock Master image gallery invariant mismatch for company {CompanyCode}, item {ItemCode}, operation {Operation}",
                    companyCode,
                    itemCode,
                    "gallery-mutation");
                return "The item image gallery is inconsistent. Run the approved image backfill script before changing images.";
            }
        }

        if (currentImages.Count > _maxImagesPerItem)
        {
            return $"The item gallery already exceeds the {_maxImagesPerItem} image limit.";
        }

        var pending = changes.Additions?.ToList();
        if (pending is null || pending.Any(x => x is null
                || x.Image is null
                || x.Image.Content is null
                || x.Image.Content.Length == 0
                || x.Token == Guid.Empty))
        {
            return "Item image additions are invalid.";
        }

        if (pending.Select(x => x.Token).Distinct().Count() != pending.Count)
        {
            return "Item image additions contain a duplicate token.";
        }

        var pendingBytes = pending.Aggregate(0L, (total, image) =>
            total > _maxPendingGalleryBytes - image.Image.Content.LongLength
                ? _maxPendingGalleryBytes + 1
                : total + image.Image.Content.LongLength);
        if (pendingBytes > _maxPendingGalleryBytes)
        {
            return $"Pending images exceed the {_maxPendingGalleryBytes / (1024 * 1024)} MB gallery memory limit.";
        }

        additions = pending;
        var requestedRemovals = changes.RemoveImageIds ?? Array.Empty<long>();
        removalIds = changes.ReplaceAllExisting || changes.RemoveAllExisting
            ? currentImages.Select(x => x.Uid).ToHashSet()
            : requestedRemovals.Distinct().ToHashSet();

        if (requestedRemovals.Any(uid => currentImages.All(x => x.Uid != uid)))
        {
            return "One or more selected images do not belong to this item.";
        }

        var finalCount = currentImages.Count - removalIds.Count + additions.Count;
        if (finalCount < 0)
        {
            return "Item image gallery change is invalid.";
        }

        if (finalCount > _maxImagesPerItem)
        {
            return $"Maximum {_maxImagesPerItem} images per item. Remove an image before adding another.";
        }

        var selection = changes.PrimarySelection;
        if (selection?.ExistingImageId is not null && selection.PendingImageToken is not null)
        {
            return "Choose one primary image.";
        }

        if (selection?.ExistingImageId is long selectedExistingId
            && (removalIds.Contains(selectedExistingId)
                || currentImages.All(x => x.Uid != selectedExistingId)))
        {
            return "The selected primary image does not belong to this item or is being removed.";
        }

        if (selection?.PendingImageToken is Guid selectedPendingToken
            && additions.All(x => x.Token != selectedPendingToken))
        {
            return "The selected primary image is not part of this Save.";
        }

        if (selection is not null
            && selection.ExistingImageId is null
            && selection.PendingImageToken is null
            && finalCount > 0)
        {
            return "An item with images must have a primary image.";
        }

        return null;
    }

    private async Task<IvMasterOperationResult<IReadOnlyDictionary<Guid, string>>> StoreGalleryAdditionsAsync(
        IReadOnlyList<IvPendingStockImage> additions,
        string companyCode,
        string itemCode,
        List<string> newlyStoredPaths,
        CancellationToken cancellationToken)
    {
        var paths = new Dictionary<Guid, string>();
        if (additions.Count == 0)
        {
            return IvMasterOperationResult<IReadOnlyDictionary<Guid, string>>.Ok(paths);
        }

        if (_imageService is null)
        {
            return IvMasterOperationResult<IReadOnlyDictionary<Guid, string>>.Fail(
                IvMasterErrorCode.Validation,
                "Item image storage is unavailable.");
        }

        foreach (var addition in additions)
        {
            var stored = await _imageService.StorePreparedAsync(
                companyCode,
                itemCode,
                addition.Image,
                cancellationToken);
            if (!stored.Succeeded || stored.Data is null)
            {
                await CleanupNewImageFilesAsync(newlyStoredPaths, companyCode, itemCode, "item-gallery-store-rollback");
                return IvMasterOperationResult<IReadOnlyDictionary<Guid, string>>.Fail(
                    stored.ErrorCode == IvMasterErrorCode.None ? IvMasterErrorCode.Validation : stored.ErrorCode,
                    stored.Message ?? "The item image could not be stored.",
                    stored.ValidationErrors);
            }

            paths.Add(addition.Token, stored.Data.RelativePath);
            newlyStoredPaths.Add(stored.Data.RelativePath);
        }

        return IvMasterOperationResult<IReadOnlyDictionary<Guid, string>>.Ok(paths);
    }

    private static List<IvStockMasterImage> BuildGalleryRows(
        IReadOnlyList<IvPendingStockImage> additions,
        IReadOnlyDictionary<Guid, string> storedPaths,
        IReadOnlyList<IvStockMasterImage> currentImages,
        string companyCode,
        string itemCode,
        DateTime createdDate,
        string createdBy)
    {
        var sortOrder = currentImages.Select(x => x.SortOrder).DefaultIfEmpty(0).Max();
        var rows = new List<IvStockMasterImage>(additions.Count);
        foreach (var addition in additions)
        {
            rows.Add(new IvStockMasterImage
            {
                CompanyCode = companyCode,
                ICode = itemCode,
                ImagePath = storedPaths[addition.Token],
                SortOrder = ++sortOrder,
                CreatedDate = createdDate,
                CreatedBy = createdBy
            });
        }

        return rows;
    }

    private static string? ResolvePrimaryImagePath(
        IReadOnlyList<IvStockMasterImage> finalImages,
        string? currentPrimaryPath,
        IvStockMasterImageGalleryChangeSet? changes,
        IReadOnlyDictionary<Guid, string> pendingPaths)
    {
        var selection = changes?.PrimarySelection;
        if (selection?.ExistingImageId is long existingImageId)
        {
            return finalImages.FirstOrDefault(x => x.Uid == existingImageId)?.ImagePath;
        }

        if (selection?.PendingImageToken is Guid pendingToken)
        {
            return pendingPaths.TryGetValue(pendingToken, out var pendingPath) ? pendingPath : null;
        }

        if (selection is not null)
        {
            return null;
        }

        return finalImages.FirstOrDefault(x =>
                   string.Equals(x.ImagePath, currentPrimaryPath, StringComparison.Ordinal))?.ImagePath
               ?? finalImages.OrderBy(x => x.SortOrder).FirstOrDefault()?.ImagePath;
    }

    private async Task CleanupNewImageFilesAsync(
        List<string> newlyStoredPaths,
        string companyCode,
        string itemCode,
        string operation)
    {
        foreach (var imagePath in newlyStoredPaths.ToArray())
        {
            await TryCleanupImageAsync(imagePath, companyCode, itemCode, operation);
        }

        newlyStoredPaths.Clear();
    }

    private async Task TryCleanupImageAsync(
        string? relativePath,
        string companyCode,
        string itemCode,
        string operation)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || _imageService is null)
        {
            return;
        }

        try
        {
            await _imageService.TryDeleteManagedFileAsync(
                relativePath,
                companyCode,
                itemCode,
                operation);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Item image cleanup failed for company {CompanyCode}, item {ItemCode}, operation {Operation}",
                companyCode,
                itemCode,
                operation);
        }
    }

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private UserContext ValidateUserContext()
    {
        var branchScope = _tenant.TryBranchScope();
        if (branchScope is null)
        {
            return UserContext.Fail(IvMasterErrorCode.InvalidScope, "Invalid company or branch context.");
        }

        return UserContext.Ok(branchScope.CompanyCode, branchScope.BranchCode, branchScope.UserId);
    }

    private static bool KeysEqual(string? left, string? right) =>
        string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static IvMasterOperationResult<T> Fail<T>(
        IvMasterErrorCode code,
        string message,
        IReadOnlyDictionary<string, string>? validationErrors = null) =>
        IvMasterOperationResult<T>.Fail(code, message, validationErrors);

    private static bool RowVersionsEqual(byte[]? left, byte[]? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return left.AsSpan().SequenceEqual(right);
    }

    private static bool IsDuplicateKey(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var message = e.Message;
            if (message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("2627") ||
                message.Contains("2601"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsForeignKeyViolation(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var message = e.Message;
            if (message.Contains("REFERENCE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("547") ||
                message.Contains("constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string? TruncateOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private readonly record struct UserContext(
        string? CompanyCode,
        string? BranchCode,
        string? UserId,
        IvMasterErrorCode? ErrorCode,
        string? Error)
    {
        public static UserContext Ok(string companyCode, string branchCode, string userId) =>
            new(companyCode, branchCode, userId, null, null);

        public static UserContext Fail(IvMasterErrorCode code, string error) =>
            new(null, null, null, code, error);
    }
}
