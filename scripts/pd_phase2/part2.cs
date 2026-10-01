    public async Task<IvMasterOperationResult<PrProductDefEditVm>> SaveAsync(
        PrProductDefEditVm model,
        bool isNew,
        bool activate,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(permission, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm(ctx.Error);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm(IvMasterErrorCode.InvalidScope, "Invalid company / branch / location context.");
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var prodCode = ValidateCode(errors, nameof(model.ProdCode), "Product code", model.ProdCode, CodeMax);
        var lines = model.Lines ?? [];
        if (lines.Count == 0 && (model.Operations?.Count ?? 0) == 0)
        {
            errors[nameof(model.Lines)] = "Add at least one process or BOM component.";
        }

        var baseQty = IvQty.Round(model.BaseQty);
        if (baseQty <= 0m)
        {
            errors[nameof(model.BaseQty)] = "Base quantity must be greater than zero.";
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        IvStockMaster? product = null;
        if (prodCode.Length > 0)
        {
            product = await db.IvStockMasters
                .FirstOrDefaultAsync(x => x.CompanyCode == company && x.ICode == prodCode, cancellationToken);
            if (product is null)
            {
                errors[nameof(model.ProdCode)] = "Product does not exist in Stock Master.";
            }
            else if (!product.IsActive)
            {
                errors[nameof(model.ProdCode)] = "Product is inactive.";
            }
        }

        PrBomHdr? existingHeader = null;
        string definitionCode;
        string? definitionName;

        if (!isNew)
        {
            // DefinitionCode is immutable after first save — resolve the revision first, then lock the code.
            var requestedDef = PrProductDefinitionCodes.Normalize(model.DefinitionCode);
            existingHeader = await db.PrBomHdrs
                .FirstOrDefaultAsync(x => x.CompanyCode == company
                                          && x.ProdCode == prodCode
                                          && x.DefinitionCode == requestedDef
                                          && x.Version == model.Version, cancellationToken);
            if (existingHeader is null && requestedDef.Length > 0)
            {
                // Fallback: locate by Prod+Version then enforce DefinitionCode match (legacy rows).
                existingHeader = await db.PrBomHdrs
                    .FirstOrDefaultAsync(x => x.CompanyCode == company
                                              && x.ProdCode == prodCode
                                              && x.Version == model.Version, cancellationToken);
            }

            if (existingHeader is null)
            {
                return FailVm(IvMasterErrorCode.NotFound, "Product definition not found.");
            }

            definitionCode = string.IsNullOrWhiteSpace(existingHeader.DefinitionCode)
                ? PrProductDefinitionCodes.Standard
                : PrProductDefinitionCodes.Normalize(existingHeader.DefinitionCode);

            if (existingHeader.Status is PrBomStatuses.Active or PrBomStatuses.Superseded or PrBomStatuses.Inactive)
            {
                return FailVm(IvMasterErrorCode.Validation,
                    $"Product Definition version {model.Version} is {existingHeader.Status} and immutable. Create a new draft version.");
            }

            if (model.HeaderRowVersion is { Length: > 0 }
                && existingHeader.RowVersion is { Length: > 0 }
                && !model.HeaderRowVersion.SequenceEqual(existingHeader.RowVersion))
            {
                return FailVm(IvMasterErrorCode.Concurrency,
                    "This product definition was changed by another user. Reload and try again.");
            }

            definitionName = Truncate(
                string.IsNullOrWhiteSpace(model.DefinitionName) ? existingHeader.DefinitionName : model.DefinitionName,
                NameMax);
        }
        else
        {
            definitionCode = PrProductDefinitionCodes.Normalize(model.DefinitionCode);
            if (definitionCode.Length == 0)
            {
                definitionCode = PrProductDefinitionCodes.Standard;
            }

            if (!PrProductDefinitionCodes.IsValidFormat(definitionCode))
            {
                errors[nameof(model.DefinitionCode)] =
                    "Definition code must be 1–30 characters (A–Z, 0–9, hyphen, underscore).";
            }

            definitionName = Truncate(
                string.IsNullOrWhiteSpace(model.DefinitionName)
                    ? (definitionCode == PrProductDefinitionCodes.Standard
                        ? PrProductDefinitionCodes.StandardName
                        : model.DefinitionName)
                    : model.DefinitionName,
                NameMax);

            if (prodCode.Length > 0 && definitionCode.Length > 0
                && !errors.ContainsKey(nameof(model.DefinitionCode)))
            {
                var any = await db.PrBomHdrs.AsNoTracking()
                    .AnyAsync(x => x.CompanyCode == company
                                   && x.ProdCode == prodCode
                                   && x.DefinitionCode == definitionCode, cancellationToken);
                if (any)
                {
                    errors[nameof(model.DefinitionCode)] =
                        "A product definition already exists for this product and definition code. Use Create New Version.";
                }
            }
        }

        var mfgType = product is null ? PrMfgTypes.Buy : PrMfgTypes.Normalize(product.MfgType);
        if (mfgType == PrMfgTypes.Buy)
        {
            mfgType = PrMfgTypes.Make; // first BOM save promotes BUY → MAKE
        }

        if (mfgType is not (PrMfgTypes.Make or PrMfgTypes.Phantom))
        {
            errors[nameof(model.MfgType)] = "A BOM requires MfgType MAKE or PHANTOM.";
        }

        var activeWarehouses = await db.IvWarehouses.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.BranchCode == writeScope.BranchCode
                        && x.IsActive)
            .Select(x => x.WarehouseCode)
            .ToListAsync(cancellationToken);
        var warehouseSet = new HashSet<string>(activeWarehouses, StringComparer.OrdinalIgnoreCase);

        var activeItems = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.IsActive)
            .Select(x => new { x.ICode, x.IDesc, x.StdUom, x.MfgType, x.DefWarehouse })
            .ToListAsync(cancellationToken);
        var itemByCode = activeItems.ToDictionary(x => x.ICode, StringComparer.OrdinalIgnoreCase);
        var normalizedOperations = await ValidateOperationsAsync(
            db,
            company,
            prodCode,
            model.Operations ?? [],
            activate || existingHeader?.Status == PrBomStatuses.Active,
            itemByCode.ToDictionary(x => x.Key, x => x.Value.StdUom, StringComparer.OrdinalIgnoreCase),
            errors,
            cancellationToken);

        var seenComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<(
            string ICode,
            string? IName,
            decimal StdQty,
            string? StdUom,
            int SeqNo,
            decimal ScrapPercent,
            string Warehouse,
            bool BomDefault,
            string? AlternateGroupCode,
            decimal Tolerance,
            bool WipBomDefault,
            Guid? OperationKey,
            string IssueMethod,
            string SupplySource,
            string? ComponentDefinitionCode,
            Guid? ProducingRouteStepKey)>();
        var operationKeys = normalizedOperations.Select(x => x.OperationKey).ToHashSet();
        var defaultsByGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        var persistedRouteSteps = existingHeader is null
            ? []
            : await db.PrBomRouteSteps.AsNoTracking()
                .Where(x => x.BomHdrId == existingHeader.Uid)
                .ToListAsync(cancellationToken);
        var availableStepOutputs = KnownRouteStepOutputItems(persistedRouteSteps);
        var existingStepKeys = persistedRouteSteps
            .GroupBy(x => (x.WorkCentreCode, x.OutputItemCode, x.StageSequence))
            .ToDictionary(g => g.Key, g => g.First().RouteStepKey);
        if (operationKeys.Contains(Guid.Empty) || operationKeys.Count != normalizedOperations.Count)
            errors["Operations"] = "Each process must have a unique, non-empty identity.";

        // Preload logical definition existence for SEPARATE draft validation
        HashSet<(string Prod, string Def)>? logicalDefs = null;
        HashSet<(string Prod, string Def)>? activeDefs = null;
        if (lines.Any(l => string.Equals(
                (l.SupplySource ?? string.Empty).Trim(),
                PrMaterialSupplySources.SeparateProductDefinition,
                StringComparison.OrdinalIgnoreCase)))
        {
            var defRows = await db.PrBomHdrs.AsNoTracking()
                .Where(x => x.CompanyCode == company)
                .Select(x => new { x.ProdCode, x.DefinitionCode, x.Status })
                .ToListAsync(cancellationToken);
            logicalDefs = defRows
                .Select(x => (
                    NormalizeCode(x.ProdCode),
                    string.IsNullOrWhiteSpace(x.DefinitionCode)
                        ? PrProductDefinitionCodes.Standard
                        : PrProductDefinitionCodes.Normalize(x.DefinitionCode)))
                .ToHashSet(new ProdDefTupleComparer());
            activeDefs = defRows
                .Where(x => x.Status == PrBomStatuses.Active)
                .Select(x => (
                    NormalizeCode(x.ProdCode),
                    string.IsNullOrWhiteSpace(x.DefinitionCode)
                        ? PrProductDefinitionCodes.Standard
                        : PrProductDefinitionCodes.Normalize(x.DefinitionCode)))
                .ToHashSet(new ProdDefTupleComparer());
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var label = $"Line {i + 1}";
            if (line.OperationKey is { } owner && !operationKeys.Contains(owner))
                errors[$"Lines[{i}].OperationKey"] = $"{label}: the owning process is missing.";
            if (activate && normalizedOperations.Count > 0 && line.OperationKey is null)
                errors[$"Lines[{i}].OperationKey"] = $"{label}: assign this material to its consuming process before activation.";
            var iCode = ValidateCode(errors, $"Lines[{i}].ICode", $"{label} item", line.ICode, CodeMax);

            if (iCode.Length > 0 && string.Equals(iCode, prodCode, StringComparison.OrdinalIgnoreCase))
            {
                errors[$"Lines[{i}].ICode"] = $"{label}: component cannot be the same as the finished product.";
            }

            if (iCode.Length > 0 && !seenComponents.Add($"{line.OperationKey}|{iCode}"))
            {
                errors[$"Lines[{i}].ICode"] = $"{label}: duplicate component {iCode}.";
            }

            if (iCode.Length > 0 && !itemByCode.ContainsKey(iCode))
            {
                errors[$"Lines[{i}].ICode"] = $"{label}: item {iCode} does not exist or is not active.";
            }

            var stdQty = IvQty.Round(line.StdQty);
            if (stdQty <= 0m)
            {
                errors[$"Lines[{i}].StdQty"] = $"{label}: standard quantity must be greater than zero.";
            }

            var scrap = IvQty.Round(line.ScrapPercent);
            if (scrap < 0m)
            {
                errors[$"Lines[{i}].ScrapPercent"] = $"{label}: scrap percent cannot be negative.";
            }

            var tolerance = IvQty.Round(line.Tolerance);
            if (tolerance < 0m)
            {
                errors[$"Lines[{i}].Tolerance"] = $"{label}: tolerance cannot be negative.";
            }

            var warehouse = (line.Warehouse ?? string.Empty).Trim();
            if (warehouse.Length == 0)
            {
                errors[$"Lines[{i}].Warehouse"] = $"{label}: warehouse is required.";
            }
            else if (warehouse.Length > WarehouseMax)
            {
                errors[$"Lines[{i}].Warehouse"] = $"{label}: warehouse is too long.";
            }
            else if (!warehouseSet.Contains(warehouse))
            {
                errors[$"Lines[{i}].Warehouse"] = $"{label}: warehouse {warehouse} is not an active warehouse for this branch.";
            }

            var issueMethod = (line.IssueMethod ?? PrMaterialIssueMethods.Manual).Trim().ToUpperInvariant();
            if (!PrMaterialIssueMethods.IsValid(issueMethod))
                errors[$"Lines[{i}].IssueMethod"] = $"{label}: select a valid issue method.";

            var supplySource = (line.SupplySource ?? PrMaterialSupplySources.Purchased).Trim().ToUpperInvariant();
            if (!PrMaterialSupplySources.IsValid(supplySource))
                errors[$"Lines[{i}].SupplySource"] = $"{label}: select a valid supply source.";

            string? componentDefinitionCode = null;
            if (string.Equals(supplySource, PrMaterialSupplySources.SeparateProductDefinition, StringComparison.Ordinal))
            {
                var childDef = PrProductDefinitionCodes.Normalize(line.ComponentDefinitionCode);
                if (childDef.Length == 0 || !PrProductDefinitionCodes.IsValidFormat(childDef))
                {
                    errors[$"Lines[{i}].ComponentDefinitionCode"] =
                        $"{label}: component definition code is required for SEPARATE_PRODUCT_DEFINITION.";
                }
                else if (logicalDefs is not null && !logicalDefs.Contains((iCode, childDef)))
                {
                    errors[$"Lines[{i}].ComponentDefinitionCode"] =
                        $"{label}: product definition {iCode}[{childDef}] does not exist.";
                }
                else if (activate && activeDefs is not null && !activeDefs.Contains((iCode, childDef)))
                {
                    errors[$"Lines[{i}].ComponentDefinitionCode"] =
                        $"{label}: product definition {iCode}[{childDef}] has no ACTIVE revision.";
                }
                else
                {
                    componentDefinitionCode = childDef;
                }
            }
            else if (!string.IsNullOrWhiteSpace(line.ComponentDefinitionCode))
            {
                errors[$"Lines[{i}].ComponentDefinitionCode"] =
                    $"{label}: component definition code is only allowed for SEPARATE_PRODUCT_DEFINITION.";
            }

            Guid? producingKey = null;
            if (line.ProducingRouteStepKey is { } candidate && candidate != Guid.Empty)
            {
                if (!availableStepOutputs.TryGetValue(candidate, out var producerOutput))
                {
                    errors[$"Lines[{i}].ProducingRouteStepKey"] =
                        $"{label}: the producing route step does not belong to this revision.";
                }
                else if (!string.Equals(producerOutput, iCode, StringComparison.OrdinalIgnoreCase))
                {
                    errors[$"Lines[{i}].ProducingRouteStepKey"] =
                        $"{label}: route step outputs {producerOutput}, not component {iCode}.";
                }
                else
                {
                    producingKey = candidate;
                }
            }

            if (string.Equals(supplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal))
            {
                if (activate && producingKey is null)
                    errors[$"Lines[{i}].ProducingRouteStepKey"] =
                        $"{label}: select the in-house route step that produces {iCode} before activation.";
            }
            else if (producingKey is not null)
            {
                errors[$"Lines[{i}].ProducingRouteStepKey"] =
                    $"{label}: a producing route step is only allowed for INTERNAL_ROUTE_WIP material.";
            }

            var alternateGroup = PrBomAlternateGroups.Normalize(line.AlternateGroupCode);
            if (alternateGroup is null)
            {
                alternateGroup = iCode.Length > 0 ? iCode : null;
            }

            if (alternateGroup is null || !PrBomAlternateGroups.IsValidLength(alternateGroup))
            {
                errors[$"Lines[{i}].AlternateGroupCode"] =
                    $"{label}: alternate group is required (max {PrBomAlternateGroups.MaxLength} characters).";
            }
            else
            {
                var groupKey = $"{line.OperationKey}|{alternateGroup}";
                if (line.BomDefault)
                {
                    defaultsByGroup[groupKey] = defaultsByGroup.GetValueOrDefault(groupKey) + 1;
                }
            }

            if (itemByCode.TryGetValue(iCode, out var item))
            {
                bool wip = false;
                if (existingHeader is not null)
                {
                    var prior = await db.PrDefBOMs.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.BomHdrId == existingHeader.Uid
                                                  && x.ICode == iCode && x.OperationKey == line.OperationKey, cancellationToken);
                    wip = prior?.WipBomDefault ?? false;
                }

                var inventoryUom = Truncate(item.StdUom, UomMax);
                var authoredUom = Truncate(
                    string.IsNullOrWhiteSpace(line.StdUom) ? item.StdUom : line.StdUom, UomMax);
                if (!string.IsNullOrWhiteSpace(authoredUom)
                    && !string.IsNullOrWhiteSpace(inventoryUom)
                    && !string.Equals(authoredUom, inventoryUom, StringComparison.OrdinalIgnoreCase)
                    && !await _uom.HasConversionAsync(company, iCode, authoredUom, inventoryUom, cancellationToken))
                {
                    errors[$"Lines[{i}].StdUom"] =
                        $"{label}: no approved conversion from {authoredUom} to inventory UOM {inventoryUom} ({UomConversionFailureCodes.MissingConversion}).";
                }

                if (!errors.Keys.Any(k => k.StartsWith($"Lines[{i}].", StringComparison.Ordinal)))
                {
                    normalized.Add((
                        iCode,
                        Truncate(item.IDesc, NameMax),
                        stdQty,
                        authoredUom,
                        line.SeqNo > 0 ? line.SeqNo : i + 1,
                        scrap,
                        warehouse,
                        line.BomDefault,
                        alternateGroup,
                        tolerance,
                        wip,
                        line.OperationKey,
                        issueMethod,
                        supplySource,
                        componentDefinitionCode,
                        producingKey));
                }
            }
        }

        foreach (var line in normalized.Where(x => !x.BomDefault))
        {
            var groupKey = $"{line.OperationKey}|{line.AlternateGroupCode}";
            if (defaultsByGroup.GetValueOrDefault(groupKey) != 1)
            {
                errors[nameof(model.Lines)] =
                    $"Alternate group {line.AlternateGroupCode} must have exactly one default material.";
                break;
            }
        }

        foreach (var (groupKey, count) in defaultsByGroup)
        {
            if (count > 1)
            {
                var groupCode = groupKey.Contains('|') ? groupKey[(groupKey.IndexOf('|') + 1)..] : groupKey;
                errors[nameof(model.Lines)] =
                    $"Alternate group {groupCode} has more than one default material.";
                break;
            }
        }

        // Circular BOM: SEPARATE edges only, keyed by (Prod, Definition)
        if (errors.Count == 0 && prodCode.Length > 0 && definitionCode.Length > 0)
        {
            var graph = await BuildActiveComponentGraphAsync(
                db, company, exclude: null, cancellationToken);
            var componentNodes = normalized
                .Where(x => string.Equals(
                    x.SupplySource,
                    PrMaterialSupplySources.SeparateProductDefinition,
                    StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(x.ComponentDefinitionCode))
                .Select(x => CircularBomValidator.NodeKey.Create(x.ICode, x.ComponentDefinitionCode))
                .ToList();
            var path = CircularBomValidator.FindCyclePath(
                CircularBomValidator.NodeKey.Create(prodCode, definitionCode),
                componentNodes,
                graph);
            if (path is not null)
            {
                errors[nameof(model.Lines)] =
                    $"Cannot save BOM for {prodCode}[{definitionCode}] because it creates a circular BOM: {path}.";
            }
        }

        var productInventoryUom = Truncate(product?.StdUom, UomMax);
        var requestedBaseUom = Truncate(model.BaseUom ?? product?.StdUom, UomMax);
        if (prodCode.Length > 0
            && !string.IsNullOrWhiteSpace(requestedBaseUom)
            && !string.IsNullOrWhiteSpace(productInventoryUom)
            && !string.Equals(requestedBaseUom, productInventoryUom, StringComparison.OrdinalIgnoreCase)
            && !await _uom.HasConversionAsync(company, prodCode, requestedBaseUom, productInventoryUom, cancellationToken))
        {
            errors[nameof(model.BaseUom)] =
                $"No approved conversion from Base UOM {requestedBaseUom} to inventory UOM {productInventoryUom} ({UomConversionFailureCodes.MissingConversion}).";
        }

        var targetStatus = activate
            ? PrBomStatuses.Active
            : (existingHeader?.Status ?? PrBomStatuses.Draft);
        if (isNew && !activate)
        {
            targetStatus = PrBomStatuses.Draft;
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<PrProductDefEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var now = DateTime.UtcNow;
        var user = Truncate(ctx.UserId!, 10) ?? "system";
        var baseUom = Truncate(model.BaseUom ?? product?.StdUom, UomMax);
        var prefixRaw = NormalizeCode(model.Prefix);
        var prefix = prefixRaw.Length == 0 ? null : Truncate(prefixRaw, PrefixMax);
        var remark = Truncate(string.IsNullOrWhiteSpace(model.Remark) ? null : model.Remark.Trim(), HeaderRemarkMax);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            PrBomHdr header;
            if (isNew)
            {
                header = new PrBomHdr
                {
                    CompanyCode = company,
                    ProdCode = prodCode,
                    DefinitionCode = definitionCode,
                    DefinitionName = definitionName,
                    IsDefaultDefinition = false,
                    Version = 1,
                    Status = PrBomStatuses.Draft,
                    EffectiveFrom = null,
                    EffectiveTo = null,
                    BaseQty = baseQty,
                    BaseUom = baseUom,
                    Prefix = prefix,
                    Remark = remark,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user,
                    RowVersion = []
                };
                InventoryLeftoverSite.Apply(header, writeScope);
                db.PrBomHdrs.Add(header);
                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                header = existingHeader!;
                header.DefinitionName = definitionName;
                header.BaseQty = baseQty;
                header.BaseUom = baseUom;
                header.Prefix = prefix;
                header.Remark = remark;
                header.ModifiedDate = now;
                header.ModifiedBy = user;
                InventoryLeftoverSite.Apply(header, writeScope);

                var oldLines = await db.PrDefBOMs.Where(x => x.BomHdrId == header.Uid).ToListAsync(cancellationToken);
                db.PrDefBOMs.RemoveRange(oldLines);
                var oldOperations = await db.PrBomOperations
                    .Where(x => x.BomHdrId == header.Uid)
                    .ToListAsync(cancellationToken);
                db.PrBomOperations.RemoveRange(oldOperations);
                var oldRouteSteps = await db.PrBomRouteSteps
                    .Where(x => x.BomHdrId == header.Uid)
                    .ToListAsync(cancellationToken);
                db.PrBomRouteSteps.RemoveRange(oldRouteSteps);
                await db.SaveChangesAsync(cancellationToken);
            }

            var (operationsByKey, routeStepsByKey) = AddRoute(
                db, header, company, normalizedOperations, existingStepKeys, now, user);

            foreach (var line in normalized.OrderBy(x => x.SeqNo))
            {
                var row = new PrDefBOM
                {
                    BomHdrId = header.Uid,
                    OperationKey = line.OperationKey,
                    Operation = line.OperationKey is { } ownerKey
                        ? operationsByKey.GetValueOrDefault(ownerKey)
                        : null,
                    ProducingRouteStep = line.ProducingRouteStepKey is { } producerKey
                        ? routeStepsByKey.GetValueOrDefault(producerKey)
                        : null,
                    CompanyCode = company,
                    ProdCode = prodCode,
                    ICode = line.ICode,
                    IName = line.IName,
                    StdQty = line.StdQty,
                    StdUom = line.StdUom,
                    SeqNo = line.SeqNo,
                    ScrapPercent = line.ScrapPercent,
                    Warehouse = line.Warehouse,
                    BomDefault = line.BomDefault,
                    AlternateGroupCode = line.AlternateGroupCode,
                    WipBomDefault = line.WipBomDefault,
                    Tolerance = line.Tolerance,
                    IssueMethod = line.IssueMethod,
                    SupplySource = line.SupplySource,
                    ComponentDefinitionCode = line.ComponentDefinitionCode,
                    CreatedDate = now,
                    CreatedBy = user,
                    ModifiedDate = now,
                    ModifiedBy = user,
                    RowVersion = []
                };
                InventoryLeftoverSite.Apply(row, writeScope);
                db.PrDefBOMs.Add(row);
            }

            if (product is not null && PrMfgTypes.Normalize(product.MfgType) == PrMfgTypes.Buy)
            {
                product.MfgType = PrMfgTypes.Make;
            }

            await db.SaveChangesAsync(cancellationToken);

            if (activate)
            {
                await ActivateRevisionInTransactionAsync(
                    db,
                    company,
                    header,
                    requestDefault: model.IsDefaultDefinition,
                    now,
                    user,
                    cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);

            return await GetAsync(prodCode, definitionCode, header.Version, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm(IvMasterErrorCode.Concurrency,
                "This product definition was changed by another user. Reload and try again.");
        }
        catch (DbUpdateException ex) when (IsUniqueIndexConflict(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return FailVm(IvMasterErrorCode.Concurrency,
                "Another user activated or changed the default for this product definition. Reload and try again.");
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static bool IsUniqueIndexConflict(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
               || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
               || message.Contains("IX_", StringComparison.OrdinalIgnoreCase);
    }
