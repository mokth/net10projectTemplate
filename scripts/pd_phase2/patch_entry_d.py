from pathlib import Path

p = Path(r"c:/wincom/net10projects/ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor.cs")
t = p.read_text(encoding="utf-8")

# Replace SelectNodeAsync owner-switch comparison through ReturnToStructureRootAsync
start = t.find("    private async Task SelectNodeAsync(PrBomStructureNode node)")
end = t.find("    private void BeginEditLine(PrProductDefLineVm line)")
if start < 0 or end < 0:
    raise SystemExit(f"bounds missing {start} {end}")

block = r'''    private async Task SelectNodeAsync(PrBomStructureNode node)
    {
        CurrentSelectedNodeKey = node.Key;
        SelectedNode = node;

        // Selecting a line owned by another product definition → switch edit context
        if (node.ParentKey is not null
            && !string.IsNullOrWhiteSpace(node.OwnerProdCode)
            && !OwnersEqual(
                node.OwnerProdCode,
                node.OwnerDefinitionCode ?? CurrentOwnerDefinitionCode,
                CurrentOwnerProdCode,
                CurrentOwnerDefinitionCode))
        {
            await RequestOwnerSwitchAsync(
                node.OwnerProdCode,
                node.OwnerDefinitionCode ?? CurrentOwnerDefinitionCode,
                addAfter: false,
                selectKey: node.Key);
            return;
        }

        if (node.ParentKey is not null
            && OwnersEqual(
                node.OwnerProdCode,
                node.OwnerDefinitionCode ?? CurrentOwnerDefinitionCode,
                CurrentOwnerProdCode,
                CurrentOwnerDefinitionCode))
        {
            var line = FindLine(node);
            if (line is not null)
            {
                BeginEditLine(line);
                return;
            }
        }

        ResetLineEditor();
    }

    protected async Task BeginAddUnderSelectionAsync()
    {
        ErrorMessage = null;
        if (!CanAddUnderSelection)
        {
            ErrorMessage = "Select the root, or a Make/Phantom item, to add a component.";
            return;
        }

        var (targetOwner, targetDefinition) = ResolveAddOwner();
        if (string.IsNullOrWhiteSpace(targetOwner))
        {
            ErrorMessage = "Product code is required before adding components.";
            return;
        }

        if (!OwnersEqual(targetOwner, targetDefinition, CurrentOwnerProdCode, CurrentOwnerDefinitionCode))
        {
            await RequestOwnerSwitchAsync(targetOwner, targetDefinition, addAfter: true, selectKey: SelectedNode?.Key);
            return;
        }

        ResetLineEditor();
    }

    private (string? ProdCode, string DefinitionCode) ResolveAddOwner()
    {
        if (SelectedNode is null || SelectedNode.ParentKey is null)
        {
            var prod = string.IsNullOrWhiteSpace(CurrentOwnerProdCode)
                ? StructureRootProdCode
                : CurrentOwnerProdCode;
            return (prod, CurrentOwnerDefinitionCode);
        }

        // Drill into SEPARATE child definition when present; otherwise keep current owner definition.
        var childDef = string.IsNullOrWhiteSpace(SelectedNode.ComponentDefinitionCode)
            ? CurrentOwnerDefinitionCode
            : PrProductDefinitionCodes.Normalize(SelectedNode.ComponentDefinitionCode);
        return (SelectedNode.ItemCode, childDef);
    }

    private async Task RequestOwnerSwitchAsync(
        string ownerProdCode,
        string ownerDefinitionCode,
        bool addAfter,
        string? selectKey)
    {
        if (IsDirty)
        {
            _pendingOwnerProdCode = ownerProdCode;
            _pendingOwnerDefinitionCode = ownerDefinitionCode;
            _pendingAddAfterSwitch = addAfter;
            CurrentSelectedNodeKey = selectKey;
            ConfirmDiscardVisible = true;
            return;
        }

        await SwitchOwnerAsync(ownerProdCode, ownerDefinitionCode, addAfter, selectKey);
    }

    private async Task SwitchOwnerAsync(
        string ownerProdCode,
        string ownerDefinitionCode,
        bool addAfter,
        string? selectKey)
    {
        ErrorMessage = null;
        var code = ownerProdCode.Trim().ToUpperInvariant();
        var definition = string.IsNullOrWhiteSpace(ownerDefinitionCode)
            ? PrProductDefinitionCodes.Standard
            : PrProductDefinitionCodes.Normalize(ownerDefinitionCode);

        // MissingBom / never saved: create new draft context in-memory
        var get = await ProductDefs.GetAsync(code, definition);
        if (!get.Succeeded || get.Data is null)
        {
            if (get.ErrorCode == IvMasterErrorCode.NotFound)
            {
                Model = new PrProductDefEditVm
                {
                    ProdCode = code,
                    DefinitionCode = definition,
                    DefinitionName = definition == PrProductDefinitionCodes.Standard
                        ? PrProductDefinitionCodes.StandardName
                        : definition,
                    Status = PrBomStatuses.Draft,
                    BaseQty = 1m,
                    Version = 1,
                    MfgType = SelectedNode is not null
                        ? PrMfgTypes.Normalize(SelectedNode.MfgType)
                        : PrMfgTypes.Make
                };
                if (SelectedNode is not null
                    && string.Equals(SelectedNode.ItemCode, code, StringComparison.OrdinalIgnoreCase))
                {
                    Model.ProdDesc = SelectedNode.ItemDesc;
                    Model.StdUom = SelectedNode.StdUom;
                    Model.BaseUom = SelectedNode.StdUom;
                }

                CurrentOwnerProdCode = code;
                CurrentOwnerDefinitionCode = definition;
                ResetRoutingEditors();
                CaptureClean();
                RebuildDisplayNodes();
                if (addAfter)
                {
                    ResetLineEditor();
                }

                CurrentSelectedNodeKey = selectKey;
                SelectedNode = DisplayNodes.FirstOrDefault(x =>
                    string.Equals(x.Key, selectKey, StringComparison.Ordinal));
                return;
            }

            ErrorMessage = get.Message ?? "Unable to load product definition.";
            return;
        }

        Model = get.Data;
        CurrentOwnerProdCode = code;
        CurrentOwnerDefinitionCode = string.IsNullOrWhiteSpace(Model.DefinitionCode)
            ? definition
            : PrProductDefinitionCodes.Normalize(Model.DefinitionCode);
        ResetRoutingEditors();
        CaptureClean();
        RebuildDisplayNodes();
        CurrentSelectedNodeKey = selectKey;
        SelectedNode = DisplayNodes.FirstOrDefault(x =>
            string.Equals(x.Key, selectKey, StringComparison.Ordinal));

        if (addAfter)
        {
            ResetLineEditor();
        }
        else if (SelectedNode is not null
                 && OwnersEqual(
                     SelectedNode.OwnerProdCode,
                     SelectedNode.OwnerDefinitionCode ?? CurrentOwnerDefinitionCode,
                     CurrentOwnerProdCode,
                     CurrentOwnerDefinitionCode))
        {
            var line = FindLine(SelectedNode);
            if (line is not null)
            {
                BeginEditLine(line);
            }
        }
    }

    protected async Task ReturnToStructureRootAsync()
    {
        if (IsEditingRoot)
        {
            return;
        }

        await RequestOwnerSwitchAsync(
            StructureRootProdCode,
            StructureRootDefinitionCode,
            addAfter: false,
            selectKey: PrBomStructureKeys.Root(StructureRootProdCode, StructureRootDefinitionCode));
    }

    private static bool OwnersEqual(
        string? leftProd,
        string? leftDefinition,
        string? rightProd,
        string? rightDefinition) =>
        string.Equals(
            PrBomStructureKeys.Normalize(leftProd),
            PrBomStructureKeys.Normalize(rightProd),
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            PrBomStructureKeys.Normalize(leftDefinition),
            PrBomStructureKeys.Normalize(rightDefinition),
            StringComparison.OrdinalIgnoreCase);

'''

t = t[:start] + block + t[end:]
p.write_text(t, encoding="utf-8")
print("phase D owner switch ok")
