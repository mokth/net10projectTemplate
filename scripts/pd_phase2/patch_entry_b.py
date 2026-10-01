from pathlib import Path

p = Path(r"c:/wincom/net10projects/ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor.cs")
t = p.read_text(encoding="utf-8")

# OnParametersSetAsync loaded key
old = '''        var key = $"{Mode}|{ProdCode}";
        if (string.Equals(_loadedKey, key, StringComparison.OrdinalIgnoreCase) && !IsLoading)
        {
            return;
        }

        _loadedKey = key;
        await LoadAsync();
    }
'''
new = '''        var key = $"{Mode}|{ProdCode}|{DefinitionCode}";
        if (string.Equals(_loadedKey, key, StringComparison.OrdinalIgnoreCase) && !IsLoading)
        {
            return;
        }

        _loadedKey = key;
        await LoadAsync();
    }
'''
if old not in t:
    raise SystemExit("loaded key missing")
t = t.replace(old, new, 1)

# Replace entire LoadAsync method body carefully - find method
start = t.find("    private async Task LoadAsync()")
if start < 0:
    raise SystemExit("LoadAsync missing")
end = t.find("    private async Task LoadRoutingLookupsAsync()", start)
if end < 0:
    raise SystemExit("LoadRoutingLookupsAsync missing")

load_async = r'''    private async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        ValidationErrors.Clear();
        NeedsDefinitionChoice = false;
        DefinitionChoices = [];
        ResetLineEditor();
        ResetRoutingEditors();
        SelectedNode = null;
        CurrentSelectedNodeKey = null;
        try
        {
            if (IsNewMode)
            {
                StructureRootProdCode = string.Empty;
                StructureRootDefinitionCode = PrProductDefinitionCodes.Standard;
                CurrentOwnerProdCode = string.Empty;
                CurrentOwnerDefinitionCode = PrProductDefinitionCodes.Standard;
                UpdateProdCode = string.Empty;
                Model = new PrProductDefEditVm
                {
                    Status = PrBomStatuses.Draft,
                    BaseQty = 1m,
                    Version = 1,
                    DefinitionCode = string.IsNullOrWhiteSpace(DefinitionCode)
                        ? PrProductDefinitionCodes.Standard
                        : PrProductDefinitionCodes.Normalize(DefinitionCode),
                    DefinitionName = PrProductDefinitionCodes.StandardName
                };
                _persistedNodes = [];
                PendingCentre = null;
                SelectedCentreKey = null;

                if (!string.IsNullOrWhiteSpace(ProdCode))
                {
                    var code = ProdCode.Trim().ToUpperInvariant();
                    // /new/{ProdCode} preselects the product and keeps DefinitionCode editable.
                    // Do NOT redirect to an existing default definition.
                    var resolved = await Lookups.ResolveItemAsync(code);
                    if (resolved.Succeeded && resolved.Item is not null)
                    {
                        await OnProductSelectedAsync(resolved.Item);
                        UpdateProdCode = code;
                        if (!string.IsNullOrWhiteSpace(DefinitionCode))
                        {
                            Model.DefinitionCode = PrProductDefinitionCodes.Normalize(DefinitionCode);
                            StructureRootDefinitionCode = Model.DefinitionCode;
                            CurrentOwnerDefinitionCode = Model.DefinitionCode;
                        }
                    }
                    else
                    {
                        ErrorMessage = $"Invalid product code '{code}'.";
                    }
                }

                RebuildDisplayNodes();
                RebuildCentreRows();
                CaptureClean();
                CaptureEditorClean();
                return;
            }

            if (string.IsNullOrWhiteSpace(ProdCode))
            {
                ErrorMessage = "Product code is required.";
                return;
            }

            StructureRootProdCode = ProdCode.Trim().ToUpperInvariant();
            CurrentOwnerProdCode = StructureRootProdCode;
            UpdateProdCode = StructureRootProdCode;

            var routeDefinition = string.IsNullOrWhiteSpace(DefinitionCode)
                ? null
                : PrProductDefinitionCodes.Normalize(DefinitionCode);

            if (routeDefinition is null)
            {
                var resolved = await ResolveDefinitionForRouteAsync(StructureRootProdCode);
                if (resolved.RedirectCode is string redirect)
                {
                    Navigation.NavigateTo(
                        $"/planning/product-definitions/{Mode}/{Uri.EscapeDataString(StructureRootProdCode)}/{Uri.EscapeDataString(redirect)}",
                        replace: true);
                    return;
                }

                if (resolved.Choices is { Count: > 0 })
                {
                    NeedsDefinitionChoice = true;
                    DefinitionChoices = resolved.Choices;
                    return;
                }

                ErrorMessage = resolved.Error ?? "Unable to resolve a Product Definition.";
                return;
            }

            StructureRootDefinitionCode = routeDefinition;
            CurrentOwnerDefinitionCode = routeDefinition;

            var result = await ProductDefs.GetAsync(StructureRootProdCode, StructureRootDefinitionCode);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Product definition not found.";
                return;
            }

            Model = result.Data;
            PendingCentre = null;
            SelectedCentreKey = null;
            await LoadPersistedStructureAsync(Model.Version);
            RebuildDisplayNodes();
            RebuildCentreRows();
            CaptureClean();
            CaptureEditorClean();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<(string? RedirectCode, IReadOnlyList<PrProductDefinitionLookupRow>? Choices, string? Error)> ResolveDefinitionForRouteAsync(
        string prodCode)
    {
        var list = await ProductDefs.ListDefinitionsAsync(prodCode);
        if (!list.Succeeded || list.Data is null || list.Data.Count == 0)
        {
            return (null, null, list.Message ?? $"No Product Definitions found for {prodCode}.");
        }

        if (list.Data.Count == 1)
        {
            return (list.Data[0].DefinitionCode, null, null);
        }

        var defaults = list.Data.Where(x => x.IsDefaultDefinition).ToList();
        if (defaults.Count == 1)
        {
            return (defaults[0].DefinitionCode, null, null);
        }

        return (null, list.Data, null);
    }

    protected void ChooseDefinition(string definitionCode)
    {
        if (string.IsNullOrWhiteSpace(ProdCode) || string.IsNullOrWhiteSpace(definitionCode))
        {
            return;
        }

        Navigation.NavigateTo(
            $"/planning/product-definitions/{Mode}/{Uri.EscapeDataString(ProdCode)}/{Uri.EscapeDataString(definitionCode)}");
    }

'''

t = t[:start] + load_async + t[end:]
p.write_text(t, encoding="utf-8")
print("phase B LoadAsync ok")
