    public async Task<IvMasterOperationResult<PrProductDefEditVm>> CreateNewVersionAsync(
        string prodCode,
        string definitionCode,
        int? fromVersion = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Add, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailVm(ctx.Error);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return FailVm(IvMasterErrorCode.InvalidScope, "Invalid company / branch / location context.");
        }

        var code = NormalizeCode(prodCode);
        var defCode = PrProductDefinitionCodes.Normalize(definitionCode);
        if (code.Length == 0)
        {
            return FailVm(IvMasterErrorCode.Validation, "Product code is required.", "ProdCode");
        }

        if (defCode.Length == 0 || !PrProductDefinitionCodes.IsValidFormat(defCode))
        {
            return FailVm(IvMasterErrorCode.Validation, "Definition code is required.", "DefinitionCode");
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var source = fromVersion is int v
            ? await db.PrBomHdrs.AsNoTracking()
                .FirstOrDefaultAsync(x => x.CompanyCode == company
                                          && x.ProdCode == code
                                          && x.DefinitionCode == defCode
                                          && x.Version == v, cancellationToken)
            : await db.PrBomHdrs.AsNoTracking()
                .Where(x => x.CompanyCode == company
                            && x.ProdCode == code
                            && x.DefinitionCode == defCode)
                .OrderByDescending(x => x.Version)
                .FirstOrDefaultAsync(cancellationToken);

        if (source is null)
        {
            return FailVm(IvMasterErrorCode.NotFound, "Product definition not found.");
        }

        var maxVer = await db.PrBomHdrs
            .Where(x => x.CompanyCode == company
                        && x.ProdCode == code
                        && x.DefinitionCode == defCode)
            .MaxAsync(x => x.Version, cancellationToken);

        var lines = await db.PrDefBOMs.AsNoTracking()
            .Where(x => x.BomHdrId == source.Uid)
            .OrderBy(x => x.SeqNo)
            .ToListAsync(cancellationToken);
        var sourceOperations = await db.PrBomOperations.AsNoTracking()
            .Where(x => x.BomHdrId == source.Uid)
            .Include(x => x.Machines)
                .ThenInclude(x => x.Labours)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var sourceRouteSteps = await db.PrBomRouteSteps.AsNoTracking()
            .Where(x => x.BomHdrId == source.Uid)
            .ToListAsync(cancellationToken);
        var existingStepKeys = sourceRouteSteps
            .GroupBy(x => (x.WorkCentreCode, x.OutputItemCode, x.StageSequence))
            .ToDictionary(g => g.Key, g => g.First().RouteStepKey);
        var sourceStepKeyById = sourceRouteSteps
            .GroupBy(x => x.Uid)
            .ToDictionary(g => g.Key, g => g.First().RouteStepKey);

        var now = DateTime.UtcNow;
        var user = Truncate(ctx.UserId!, 10) ?? "system";
        var header = new PrBomHdr
        {
            CompanyCode = company,
            ProdCode = code,
            DefinitionCode = source.DefinitionCode,
            DefinitionName = source.DefinitionName,
            IsDefaultDefinition = false,
            Version = maxVer + 1,
            Status = PrBomStatuses.Draft,
            EffectiveFrom = null,
            EffectiveTo = null,
            BaseQty = source.BaseQty,
            BaseUom = source.BaseUom,
            Prefix = source.Prefix,
            Remark = source.Remark,
            CreatedDate = now,
            CreatedBy = user,
            ModifiedDate = now,
            ModifiedBy = user,
            RowVersion = []
        };
        InventoryLeftoverSite.Apply(header, writeScope);
        db.PrBomHdrs.Add(header);
        await db.SaveChangesAsync(cancellationToken);

        var clonedOperations = sourceOperations.Select(x => new NormalizedOperation(
            x.OperationKey,
            x.WorkCentreCode,
            x.OutputItemCode,
            x.CentralSequence,
            x.OutputBaseQty,
            x.OutputUom,
            x.OperationCode,
            x.ProcessSequence,
            x.ProcessType,
            x.StandardDurationMinutes,
            x.SetupLossQty,
            x.OperationLossQty,
            x.IsFinalOperation,
            x.Remark,
            x.Machines.Select(m => new NormalizedMachine(
                m.MachineCode,
                m.MachineDescription,
                m.ResourceSequence,
                m.IsPrimary,
                m.Priority,
                m.OutputPerCycle,
                m.CycleSeconds,
                m.ConversionSeconds,
                m.SetupSeconds,
                m.QueueSeconds,
                m.MachineRatePerHour,
                m.ParallelMachineCount,
                m.Labours.Select(l => new NormalizedLabour(
                    l.LabourCode,
                    l.LabourDescription,
                    l.CostPerOutputUnit)).ToList())).ToList())).ToList();

        var (operationsByKey, routeStepsByKey) = AddRoute(
            db, header, company, clonedOperations, existingStepKeys, now, user);

        foreach (var line in lines)
        {
            var row = new PrDefBOM
            {
                BomHdrId = header.Uid,
                OperationKey = line.OperationKey,
                Operation = line.OperationKey is { } ownerKey
                    ? operationsByKey.GetValueOrDefault(ownerKey)
                    : null,
                ProducingRouteStep = line.ProducingRouteStepId is { } producerId
                                     && sourceStepKeyById.TryGetValue(producerId, out var producerKey)
                    ? routeStepsByKey.GetValueOrDefault(producerKey)
                    : null,
                CompanyCode = company,
                ProdCode = code,
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

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(code, defCode, header.Version, cancellationToken);
    }

    public async Task<IvMasterOperationResult<PrProductDefEditVm>> ActivateAsync(
        string prodCode,
        string definitionCode,
        int version,
        byte[]? headerRowVersion,
        CancellationToken cancellationToken = default)
    {
        var get = await GetAsync(prodCode, definitionCode, version, cancellationToken);
        if (!get.Succeeded || get.Data is null)
        {
            return get;
        }

        var model = get.Data;
        model.HeaderRowVersion = headerRowVersion ?? model.HeaderRowVersion;
        return await SaveAsync(model, isNew: false, activate: true, cancellationToken);
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>> ListActiveDefinitionsAsync(
        string prodCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailLookup(ctx.Error);
        }

        var code = NormalizeCode(prodCode);
        if (code.Length == 0)
        {
            return IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>.Fail(
                IvMasterErrorCode.Validation, "Product code is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode && x.ProdCode == code && x.Status == PrBomStatuses.Active)
            .OrderByDescending(x => x.IsDefaultDefinition)
            .ThenBy(x => x.DefinitionCode)
            .Select(x => new PrProductDefinitionLookupRow
            {
                BomHdrId = x.Uid,
                ProdCode = x.ProdCode,
                DefinitionCode = x.DefinitionCode,
                DefinitionName = x.DefinitionName,
                Version = x.Version,
                ActiveVersion = x.Version,
                LatestVersion = x.Version,
                LatestStatus = x.Status,
                IsDefaultDefinition = x.IsDefaultDefinition
            })
            .ToListAsync(cancellationToken);

        return IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>.Ok(rows);
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>> ListDefinitionsAsync(
        string prodCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Access, cancellationToken);
        if (ctx.Error is not null)
        {
            return FailLookup(ctx.Error);
        }

        var code = NormalizeCode(prodCode);
        if (code.Length == 0)
        {
            return IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>.Fail(
                IvMasterErrorCode.Validation, "Product code is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var headers = await db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode && x.ProdCode == code)
            .ToListAsync(cancellationToken);

        var rows = headers
            .GroupBy(x => string.IsNullOrWhiteSpace(x.DefinitionCode)
                ? PrProductDefinitionCodes.Standard
                : PrProductDefinitionCodes.Normalize(x.DefinitionCode), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var active = g.FirstOrDefault(x => x.Status == PrBomStatuses.Active);
                var drafts = g.Where(x => x.Status == PrBomStatuses.Draft).ToList();
                if (active is null && drafts.Count == 0)
                {
                    return null;
                }

                var latest = g.OrderByDescending(x => x.Version).First();
                return new PrProductDefinitionLookupRow
                {
                    BomHdrId = active?.Uid ?? latest.Uid,
                    ProdCode = code,
                    DefinitionCode = g.Key,
                    DefinitionName = active?.DefinitionName ?? latest.DefinitionName,
                    Version = active?.Version ?? latest.Version,
                    ActiveVersion = active?.Version,
                    LatestVersion = latest.Version,
                    LatestStatus = latest.Status,
                    IsDefaultDefinition = active?.IsDefaultDefinition ?? false
                };
            })
            .Where(x => x is not null)
            .Cast<PrProductDefinitionLookupRow>()
            .OrderByDescending(x => x.IsDefaultDefinition)
            .ThenBy(x => x.DefinitionCode, StringComparer.Ordinal)
            .ToList();

        return IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>.Ok(rows);
    }

    public async Task<IvMasterOperationResult<DeleteCheckResult>> CanDeleteAsync(
        IReadOnlyList<PrProductDefinitionKey> keys,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return IvMasterOperationResult<DeleteCheckResult>.Fail(ctx.Error.Code, ctx.Error.Message);
        }

        var normalized = NormalizeDefinitionKeys(keys);
        if (normalized.Count == 0)
        {
            return IvMasterOperationResult<DeleteCheckResult>.Fail(
                IvMasterErrorCode.Validation, "Select at least one product definition.");
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var blockers = await FindDeleteBlockersAsync(db, company, normalized, cancellationToken);
        if (blockers.Count > 0)
        {
            return IvMasterOperationResult<DeleteCheckResult>.Ok(
                DeleteCheckResult.Blocked(
                    "Product Definition cannot be deleted because it is referenced by Work Orders or other definitions.",
                    blockers));
        }

        return IvMasterOperationResult<DeleteCheckResult>.Ok(
            DeleteCheckResult.Ok("Deletes BOM headers and component lines only; Stock Master items are not deleted."));
    }

    public async Task<IvMasterOperationResult<object?>> DeleteAsync(
        IReadOnlyList<PrProductDefinitionKey> keys,
        CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(PermissionCodes.Delete, cancellationToken);
        if (ctx.Error is not null)
        {
            return IvMasterOperationResult<object?>.Fail(ctx.Error.Code, ctx.Error.Message);
        }

        var normalized = NormalizeDefinitionKeys(keys);
        if (normalized.Count == 0)
        {
            return IvMasterOperationResult<object?>.Fail(
                IvMasterErrorCode.Validation, "Select at least one product definition.");
        }

        var company = ctx.CompanyCode!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var blockers = await FindDeleteBlockersAsync(db, company, normalized, cancellationToken);
        if (blockers.Count > 0)
        {
            return IvMasterOperationResult<object?>.Fail(
                IvMasterErrorCode.InUse,
                "Product Definition cannot be deleted because it is referenced by Work Orders or other definitions.");
        }

        var prodCodes = normalized.Select(x => x.ProdCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var defCodes = normalized.Select(x => x.DefinitionCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var headers = await db.PrBomHdrs
            .Where(x => x.CompanyCode == company
                        && prodCodes.Contains(x.ProdCode)
                        && defCodes.Contains(x.DefinitionCode))
            .ToListAsync(cancellationToken);

        headers = headers
            .Where(h => normalized.Any(k =>
                string.Equals(k.ProdCode, h.ProdCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(k.DefinitionCode, h.DefinitionCode, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (headers.Count == 0)
        {
            return IvMasterOperationResult<object?>.Fail(
                IvMasterErrorCode.NotFound, "Product definition not found.");
        }

        db.PrBomHdrs.RemoveRange(headers);
        await db.SaveChangesAsync(cancellationToken);
        return IvMasterOperationResult<object?>.Ok();
    }

    private static async Task<List<IvMasterReferenceHit>> FindDeleteBlockersAsync(
        AppDbContext db,
        string company,
        IReadOnlyList<PrProductDefinitionKey> keys,
        CancellationToken cancellationToken)
    {
        var hits = new List<IvMasterReferenceHit>();
        var prodCodes = keys.Select(x => x.ProdCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var defCodes = keys.Select(x => x.DefinitionCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var headers = await db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && prodCodes.Contains(x.ProdCode)
                        && defCodes.Contains(x.DefinitionCode))
            .Select(x => new { x.Uid, x.ProdCode, x.DefinitionCode })
            .ToListAsync(cancellationToken);

        var matchedHeaders = headers
            .Where(h => keys.Any(k =>
                string.Equals(k.ProdCode, h.ProdCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(k.DefinitionCode, h.DefinitionCode, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var headerIds = matchedHeaders.Select(x => x.Uid).ToList();

        // Work Orders by Product + SourceDefinitionCode
        var woRefs = await db.ProductionWorkOrders.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && prodCodes.Contains(x.ProductCode)
                        && defCodes.Contains(x.SourceDefinitionCode))
            .Select(x => new { x.ProductCode, x.SourceDefinitionCode, x.Uid })
            .ToListAsync(cancellationToken);
        woRefs = woRefs
            .Where(x => keys.Any(k =>
                string.Equals(k.ProdCode, x.ProductCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(k.DefinitionCode, x.SourceDefinitionCode, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var g in woRefs.GroupBy(
                     x => (x.ProductCode, x.SourceDefinitionCode),
                     new ProdDefTupleComparer()))
        {
            hits.Add(new IvMasterReferenceHit
            {
                ReferenceType = "Production Work Order",
                Count = g.Select(x => x.Uid).Distinct().Count(),
                Detail = $"{g.Key.Item1}[{g.Key.Item2}]"
            });
        }

        // Material snapshots by SourceBomHdrId
        if (headerIds.Count > 0)
        {
            var materialCount = await db.ProductionWorkOrderMaterials.AsNoTracking()
                .Where(x => x.SourceBomHdrId != null && headerIds.Contains(x.SourceBomHdrId.Value))
                .Select(x => x.WorkOrderId)
                .Distinct()
                .CountAsync(cancellationToken);
            if (materialCount > 0)
            {
                hits.Add(new IvMasterReferenceHit
                {
                    ReferenceType = "Work Order Material Snapshot",
                    Count = materialCount,
                    Detail = string.Join(", ", keys.Select(k => $"{k.ProdCode}[{k.DefinitionCode}]"))
                });
            }
        }

        // SEPARATE parent lines referencing ICode + ComponentDefinitionCode
        foreach (var key in keys)
        {
            var parentCount = await db.PrDefBOMs.AsNoTracking()
                .Where(x => x.CompanyCode == company
                            && x.ICode == key.ProdCode
                            && x.ComponentDefinitionCode == key.DefinitionCode
                            && x.SupplySource == PrMaterialSupplySources.SeparateProductDefinition)
                .Select(x => x.BomHdrId)
                .Distinct()
                .CountAsync(cancellationToken);
            if (parentCount > 0)
            {
                hits.Add(new IvMasterReferenceHit
                {
                    ReferenceType = "Parent Product Definition (SEPARATE)",
                    Count = parentCount,
                    Detail = $"{key.ProdCode}[{key.DefinitionCode}]"
                });
            }
        }

        return hits;
    }

    /// <summary>
    /// Two-step activation inside the caller's open transaction: free ACTIVE/default filtered-index
    /// slots, SaveChanges, then set the target ACTIVE + IsDefaultDefinition and SaveChanges again.
    /// </summary>
    private static async Task ActivateRevisionInTransactionAsync(
        AppDbContext db,
        string company,
        PrBomHdr header,
        bool requestDefault,
        DateTime now,
        string user,
        CancellationToken cancellationToken)
    {
        var prodCode = header.ProdCode;
        var definitionCode = string.IsNullOrWhiteSpace(header.DefinitionCode)
            ? PrProductDefinitionCodes.Standard
            : PrProductDefinitionCodes.Normalize(header.DefinitionCode);

        var priorActive = await db.PrBomHdrs
            .Where(x => x.CompanyCode == company
                        && x.ProdCode == prodCode
                        && x.DefinitionCode == definitionCode
                        && x.Status == PrBomStatuses.Active
                        && x.Uid != header.Uid)
            .ToListAsync(cancellationToken);

        var priorWasDefault = priorActive.Any(x => x.IsDefaultDefinition);
        foreach (var prior in priorActive)
        {
            prior.Status = PrBomStatuses.Superseded;
            prior.IsDefaultDefinition = false;
            prior.ModifiedDate = now;
            prior.ModifiedBy = user;
        }

        var otherDefaults = await db.PrBomHdrs
            .Where(x => x.CompanyCode == company
                        && x.ProdCode == prodCode
                        && x.Status == PrBomStatuses.Active
                        && x.IsDefaultDefinition
                        && x.Uid != header.Uid
                        && x.DefinitionCode != definitionCode)
            .ToListAsync(cancellationToken);

        var otherDefaultExists = otherDefaults.Count > 0;
        var setDefault = requestDefault
                         || (priorWasDefault && !otherDefaultExists)
                         || (!priorWasDefault && !otherDefaultExists);

        if (setDefault)
        {
            foreach (var other in otherDefaults)
            {
                other.IsDefaultDefinition = false;
                other.ModifiedDate = now;
                other.ModifiedBy = user;
            }
        }

        // Step 1: free filtered unique index slots
        await db.SaveChangesAsync(cancellationToken);

        // Step 2: activate target
        header.Status = PrBomStatuses.Active;
        header.IsDefaultDefinition = setDefault;
        header.ActivatedDate = now;
        header.ActivatedBy = user;
        header.ModifiedDate = now;
        header.ModifiedBy = user;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Dictionary<CircularBomValidator.NodeKey, IReadOnlyList<CircularBomValidator.NodeKey>>>
        BuildActiveComponentGraphAsync(
            AppDbContext db,
            string company,
            CircularBomValidator.NodeKey? exclude,
            CancellationToken cancellationToken)
    {
        var activeHeaders = await db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.Status == PrBomStatuses.Active)
            .Select(x => new { x.Uid, x.ProdCode, x.DefinitionCode })
            .ToListAsync(cancellationToken);

        if (exclude is { } ex)
        {
            var excludeKey = CircularBomValidator.NodeKey.Create(ex.ProdCode, ex.DefinitionCode);
            activeHeaders = activeHeaders
                .Where(x => CircularBomValidator.NodeKey.Create(x.ProdCode, x.DefinitionCode) != excludeKey)
                .ToList();
        }

        var hdrIds = activeHeaders.Select(x => x.Uid).ToList();
        var lines = await db.PrDefBOMs.AsNoTracking()
            .Where(x => hdrIds.Contains(x.BomHdrId))
            .Select(x => new { x.BomHdrId, x.ICode, x.ComponentDefinitionCode, x.SupplySource })
            .ToListAsync(cancellationToken);

        var byHdr = lines.GroupBy(x => x.BomHdrId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CircularBomValidator.NodeKey>)g
                    .Where(x => string.Equals(
                        x.SupplySource,
                        PrMaterialSupplySources.SeparateProductDefinition,
                        StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(x.ComponentDefinitionCode))
                    .Select(x => CircularBomValidator.NodeKey.Create(x.ICode, x.ComponentDefinitionCode))
                    .ToList());

        var graph = new Dictionary<CircularBomValidator.NodeKey, IReadOnlyList<CircularBomValidator.NodeKey>>();
        foreach (var h in activeHeaders)
        {
            var key = CircularBomValidator.NodeKey.Create(
                h.ProdCode,
                string.IsNullOrWhiteSpace(h.DefinitionCode) ? PrProductDefinitionCodes.Standard : h.DefinitionCode);
            graph[key] = byHdr.TryGetValue(h.Uid, out var c) ? c : [];
        }

        return graph;
    }

    /// <summary>
    /// Same header pick as <see cref="GetAsync"/>: explicit version, else latest Version desc
    /// for Company + Prod + DefinitionCode.
    /// </summary>
    private static async Task<PrBomHdr?> ResolveHeaderForEditAsync(
        AppDbContext db,
        string company,
        string prodCode,
        string definitionCode,
        int? version,
        CancellationToken cancellationToken)
    {
        var def = PrProductDefinitionCodes.Normalize(definitionCode);
        if (version is int v)
        {
            return await db.PrBomHdrs.AsNoTracking()
                .FirstOrDefaultAsync(x => x.CompanyCode == company
                                          && x.ProdCode == prodCode
                                          && x.DefinitionCode == def
                                          && x.Version == v,
                    cancellationToken);
        }

        return await db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.ProdCode == prodCode
                        && x.DefinitionCode == def)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<PrBomHdr?> ResolveActiveHeaderAsync(
        AppDbContext db,
        string company,
        string prodCode,
        string definitionCode,
        CancellationToken cancellationToken)
    {
        var def = PrProductDefinitionCodes.Normalize(definitionCode);
        return await db.PrBomHdrs.AsNoTracking()
            .Where(x => x.CompanyCode == company
                        && x.ProdCode == prodCode
                        && x.DefinitionCode == def
                        && x.Status == PrBomStatuses.Active)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task WalkStructureAsync(
        AppDbContext db,
        string company,
        string ownerProdCode,
        string ownerDefinitionCode,
        PrBomHdr ownerHeader,
        string parentKey,
        int level,
        List<(string Prod, string Def)> pathStack,
        List<PrBomStructureNode> nodes,
        StructureLoadCache cache,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var lines = await GetStructureLinesAsync(db, ownerHeader.Uid, cache, cancellationToken);
        foreach (var line in lines.OrderBy(x => x.SeqNo).ThenBy(x => x.ICode))
        {
            var childCode = NormalizeCode(line.ICode);
            var lineId = PrBomStructureKeys.LineId(line.Uid, tempId: null);
            var ownerDefinition = string.IsNullOrWhiteSpace(ownerDefinitionCode)
                ? PrProductDefinitionCodes.Standard
                : PrProductDefinitionCodes.Normalize(ownerDefinitionCode);
            var key = PrBomStructureKeys.Line(parentKey, ownerProdCode, ownerDefinition, lineId);
            var item = await GetStructureItemAsync(db, company, childCode, cache, cancellationToken);
            var mfg = item?.MfgType ?? PrMfgTypes.Buy;
            var childLevel = level + 1;

            var isSeparate = string.Equals(
                line.SupplySource,
                PrMaterialSupplySources.SeparateProductDefinition,
                StringComparison.OrdinalIgnoreCase);
            var childDefinition = isSeparate
                ? PrProductDefinitionCodes.Normalize(line.ComponentDefinitionCode)
                : null;

            var status = PrBomStructureNodeStatus.Normal;
            var shouldRecurse = isSeparate
                                && !string.IsNullOrWhiteSpace(childDefinition);

            if (shouldRecurse
                && pathStack.Any(p =>
                    string.Equals(p.Prod, childCode, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.Def, childDefinition, StringComparison.OrdinalIgnoreCase)))
            {
                status = PrBomStructureNodeStatus.Circular;
                shouldRecurse = false;
            }
            else if (shouldRecurse && childLevel >= PrBomStructureKeys.MaxDepth)
            {
                status = PrBomStructureNodeStatus.MaxDepth;
                shouldRecurse = false;
            }

            PrBomHdr? childHeader = null;
            if (shouldRecurse && childDefinition is not null)
            {
                childHeader = await ResolveActiveHeaderCachedAsync(
                    db, company, childCode, childDefinition, cache, cancellationToken);
                if (childHeader is null)
                {
                    status = PrBomStructureNodeStatus.MissingBom;
                    shouldRecurse = false;
                }
            }

            nodes.Add(new PrBomStructureNode
            {
                Key = key,
                ParentKey = parentKey,
                Level = childLevel,
                ItemCode = childCode,
                ItemDesc = item?.Desc ?? line.IName,
                MfgType = mfg,
                StdQty = line.StdQty,
                StdUom = line.StdUom ?? item?.StdUom,
                ScrapPercent = line.ScrapPercent,
                Warehouse = line.Warehouse,
                SeqNo = line.SeqNo,
                OwnerProdCode = ownerProdCode,
                OwnerDefinitionCode = ownerDefinition,
                ComponentDefinitionCode = childDefinition,
                SourceLineUid = line.Uid,
                BomHdrId = ownerHeader.Uid,
                BomVersion = ownerHeader.Version,
                BomStatus = ownerHeader.Status,
                Status = status
            });

            if (shouldRecurse && childHeader is not null && childDefinition is not null)
            {
                pathStack.Add((childCode, childDefinition));
                await WalkStructureAsync(
                    db,
                    company,
                    ownerProdCode: childCode,
                    ownerDefinitionCode: childDefinition,
                    ownerHeader: childHeader,
                    parentKey: key,
                    level: childLevel,
                    pathStack,
                    nodes,
                    cache,
                    cancellationToken);
                pathStack.RemoveAt(pathStack.Count - 1);
            }
        }
    }

    private static async Task<PrBomHdr?> ResolveActiveHeaderCachedAsync(
        AppDbContext db,
        string company,
        string prodCode,
        string definitionCode,
        StructureLoadCache cache,
        CancellationToken cancellationToken)
    {
        var def = PrProductDefinitionCodes.Normalize(definitionCode);
        if (cache.ActiveResolved.TryGetValue((prodCode, def), out var known))
        {
            return known;
        }

        var header = await ResolveActiveHeaderAsync(db, company, prodCode, def, cancellationToken);
        cache.ActiveResolved[(prodCode, def)] = header;
        if (header is not null)
        {
            cache.Headers[(prodCode, def, header.Version)] = header;
        }

        return header;
    }

    private static async Task<List<PrDefBOM>> GetStructureLinesAsync(
        AppDbContext db,
        long bomHdrId,
        StructureLoadCache cache,
        CancellationToken cancellationToken)
    {
        if (cache.Lines.TryGetValue(bomHdrId, out var cached))
        {
            return cached;
        }

        var lines = await db.PrDefBOMs.AsNoTracking()
            .Where(x => x.BomHdrId == bomHdrId)
            .ToListAsync(cancellationToken);
        cache.Lines[bomHdrId] = lines;
        return lines;
    }

    private static async Task<StructureItemInfo?> GetStructureItemAsync(
        AppDbContext db,
        string company,
        string itemCode,
        StructureLoadCache cache,
        CancellationToken cancellationToken)
    {
        if (cache.Items.TryGetValue(itemCode, out var hit))
        {
            return hit;
        }

        var row = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.ICode == itemCode)
            .Select(x => new { x.ICode, x.IDesc, x.MfgType, x.StdUom })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var info = new StructureItemInfo(
            row.ICode,
            row.IDesc,
            PrMfgTypes.Normalize(row.MfgType),
            row.StdUom);
        cache.Items[itemCode] = info;
        return info;
    }

    private sealed class StructureLoadCache
    {
        public Dictionary<(string Prod, string Def, int Version), PrBomHdr> Headers { get; } =
            new(new ProdDefVersionComparer());

        public Dictionary<(string Prod, string Def), PrBomHdr?> ActiveResolved { get; } =
            new(new ProdDefTupleComparer());

        public Dictionary<long, List<PrDefBOM>> Lines { get; } = new();
        public Dictionary<string, StructureItemInfo> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProdDefVersionComparer : IEqualityComparer<(string Prod, string Def, int Version)>
    {
        public bool Equals((string Prod, string Def, int Version) x, (string Prod, string Def, int Version) y) =>
            x.Version == y.Version
            && string.Equals(x.Prod, y.Prod, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Def, y.Def, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Prod, string Def, int Version) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Prod),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Def),
                obj.Version);
    }

    private sealed record StructureItemInfo(string Code, string? Desc, string MfgType, string? StdUom);
