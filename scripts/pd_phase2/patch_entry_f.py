from pathlib import Path

p = Path(r"c:/wincom/net10projects/ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor.cs")
t = p.read_text(encoding="utf-8")

# CanDeleteSelection - compound owner
old = '''    protected bool CanDeleteSelection =>
        !IsViewMode
        && !IsReadOnlyStatus
        && SelectedNode is { ParentKey: not null, OwnerProdCode: not null }
        && string.Equals(SelectedNode.OwnerProdCode, CurrentOwnerProdCode, StringComparison.OrdinalIgnoreCase);
'''
new = '''    protected bool CanDeleteSelection =>
        !IsViewMode
        && !IsReadOnlyStatus
        && SelectedNode is { ParentKey: not null, OwnerProdCode: not null }
        && OwnersEqual(
            SelectedNode.OwnerProdCode,
            SelectedNode.OwnerDefinitionCode ?? CurrentOwnerDefinitionCode,
            CurrentOwnerProdCode,
            CurrentOwnerDefinitionCode);
'''
if old not in t:
    raise SystemExit("CanDeleteSelection missing")
t = t.replace(old, new, 1)

# ResetLineEditor - clear component definition
old = '''        LineSupplySource = PrMaterialSupplySources.Purchased;
'''
# Find in ResetLineEditor
idx = t.find("    protected void ResetLineEditor()")
if idx < 0:
    raise SystemExit("ResetLineEditor missing")
chunk = t[idx:idx+800]
if "LineComponentDefinitionCode" not in chunk:
    old2 = '''        LineSupplySource = PrMaterialSupplySources.Purchased;
'''
    # only first after ResetLineEditor
    pos = t.find(old2, idx)
    if pos < 0:
        raise SystemExit("LineSupplySource reset missing")
    t = t[:pos] + '''        LineSupplySource = PrMaterialSupplySources.Purchased;
        LineComponentDefinitionCode = null;
        ComponentDefinitionOptions = [];
''' + t[pos+len(old2):]

# ApplyUpdateAsync CurrentDefinitionCode
if "CurrentDefinitionCode" in t:
    t = t.replace("CurrentDefinitionCode", "CurrentOwnerDefinitionCode")
    # but wait - Structure root loads should use StructureRootDefinitionCode for GetStructureTree - already done
    # ApplyUpdate for changing product when editing - use CurrentOwnerDefinitionCode is ok for get

# Snapshot ComponentDefinitionCode
old = '''                x.IssueMethod,
                x.SupplySource,
                x.ProducingRouteStepKey
'''
new = '''                x.IssueMethod,
                x.SupplySource,
                x.ComponentDefinitionCode,
                x.ProducingRouteStepKey
'''
if old not in t:
    raise SystemExit("Snapshot lines missing")
t = t.replace(old, new, 1)

# MergeStructureWithCurrentOwner signature + root key uses structureRootDefinition
old = '''    public static IReadOnlyList<PrBomStructureNode> MergeStructureWithCurrentOwner(
        IReadOnlyList<PrBomStructureNode> persisted,
        string structureRoot,
        string currentOwner,
        PrProductDefEditVm model)
    {
        var rootCode = PrBomStructureKeys.Normalize(structureRoot);
        var owner = PrBomStructureKeys.Normalize(currentOwner);
        var ownerDefinition = string.IsNullOrWhiteSpace(model.DefinitionCode)
            ? PrProductDefinitionCodes.Standard
            : PrProductDefinitionCodes.Normalize(model.DefinitionCode);
        if (rootCode.Length == 0)
        {
            return [];
        }

        var rootKey = PrBomStructureKeys.Root(rootCode, ownerDefinition);
'''
new = '''    public static IReadOnlyList<PrBomStructureNode> MergeStructureWithCurrentOwner(
        IReadOnlyList<PrBomStructureNode> persisted,
        string structureRoot,
        string structureRootDefinition,
        string currentOwner,
        string currentOwnerDefinition,
        PrProductDefEditVm model)
    {
        var rootCode = PrBomStructureKeys.Normalize(structureRoot);
        var rootDefinition = string.IsNullOrWhiteSpace(structureRootDefinition)
            ? PrProductDefinitionCodes.Standard
            : PrProductDefinitionCodes.Normalize(structureRootDefinition);
        var owner = PrBomStructureKeys.Normalize(currentOwner);
        var ownerDefinition = string.IsNullOrWhiteSpace(currentOwnerDefinition)
            ? (string.IsNullOrWhiteSpace(model.DefinitionCode)
                ? PrProductDefinitionCodes.Standard
                : PrProductDefinitionCodes.Normalize(model.DefinitionCode))
            : PrProductDefinitionCodes.Normalize(currentOwnerDefinition);
        if (rootCode.Length == 0)
        {
            return [];
        }

        var rootKey = PrBomStructureKeys.Root(rootCode, rootDefinition);
'''
if old not in t:
    raise SystemExit("Merge signature missing")
t = t.replace(old, new, 1)

# Also set OwnerDefinitionCode / ComponentDefinitionCode on merged nodes
old = '''                    OwnerProdCode = owner,
                    SourceLineUid = line.Uid > 0 ? line.Uid : null,
                    LineTempId = line.Uid > 0 ? null : line.TempId,
                    BomHdrId = model.BomHdrId > 0 ? model.BomHdrId : null,
                    BomVersion = model.Version > 0 ? model.Version : null,
                    BomStatus = model.Status,
                    Status = oldMatch?.Status ?? status
                });
'''
new = '''                    OwnerProdCode = owner,
                    OwnerDefinitionCode = ownerDefinition,
                    ComponentDefinitionCode = string.Equals(
                        line.SupplySource,
                        PrMaterialSupplySources.SeparateProductDefinition,
                        StringComparison.OrdinalIgnoreCase)
                        ? PrProductDefinitionCodes.Normalize(line.ComponentDefinitionCode)
                        : null,
                    SourceLineUid = line.Uid > 0 ? line.Uid : null,
                    LineTempId = line.Uid > 0 ? null : line.TempId,
                    BomHdrId = model.BomHdrId > 0 ? model.BomHdrId : null,
                    BomVersion = model.Version > 0 ? model.Version : null,
                    BomStatus = model.Status,
                    Status = oldMatch?.Status ?? status
                });
'''
if old not in t:
    raise SystemExit("merge node fields missing")
t = t.replace(old, new, 1)

# Add RefreshComponentDefinitionOptionsAsync before ResetLineEditor or after OnLineItemSelected
hook = '''    private async Task RefreshComponentDefinitionOptionsAsync()
    {
        ComponentDefinitionOptions = [];
        if (!string.Equals(LineSupplySource, PrMaterialSupplySources.SeparateProductDefinition, StringComparison.Ordinal))
        {
            LineComponentDefinitionCode = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(LineItem))
        {
            return;
        }

        var result = await ProductDefs.ListDefinitionsAsync(LineItem);
        if (!result.Succeeded || result.Data is null)
        {
            return;
        }

        ComponentDefinitionOptions = result.Data
            .Select(x => new DefinitionOption(
                x.DefinitionCode,
                string.IsNullOrWhiteSpace(x.DefinitionName)
                    ? x.DefinitionCode
                    : $"{x.DefinitionCode} — {x.DefinitionName}"))
            .ToList();

        if (!string.IsNullOrWhiteSpace(LineComponentDefinitionCode)
            && ComponentDefinitionOptions.All(x =>
                !string.Equals(x.Value, LineComponentDefinitionCode, StringComparison.OrdinalIgnoreCase)))
        {
            LineComponentDefinitionCode = null;
        }
    }

'''

if "RefreshComponentDefinitionOptionsAsync" not in t:
    marker = "    protected void ResetLineEditor()"
    if marker not in t:
        raise SystemExit("ResetLineEditor marker missing for hook")
    t = t.replace(marker, hook + marker, 1)

# OnLineItemSelectedAsync - refresh definitions when SEPARATE
old = '''        await InvokeAsync(StateHasChanged);
    }

    protected async Task OnTreeFocusedRowChanged(TreeListFocusedRowChangedEventArgs args)
'''
new = '''        await RefreshComponentDefinitionOptionsAsync();
        await InvokeAsync(StateHasChanged);
    }

    protected async Task OnLineSupplySourceChanged(string value)
    {
        LineSupplySource = value ?? PrMaterialSupplySources.Purchased;
        if (!string.Equals(LineSupplySource, PrMaterialSupplySources.SeparateProductDefinition, StringComparison.Ordinal))
        {
            LineComponentDefinitionCode = null;
            ComponentDefinitionOptions = [];
        }
        else
        {
            await RefreshComponentDefinitionOptionsAsync();
        }

        if (!string.Equals(LineSupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal))
        {
            LineProducingRouteStepKey = null;
        }

        await InvokeAsync(StateHasChanged);
    }

    protected async Task OnTreeFocusedRowChanged(TreeListFocusedRowChangedEventArgs args)
'''
if old not in t:
    raise SystemExit("OnLineItemSelected hook missing")
t = t.replace(old, new, 1)

p.write_text(t, encoding="utf-8")
print("phase F merge/picker ok")
print("CurrentDefinitionCode left:", t.count("CurrentDefinitionCode"))
