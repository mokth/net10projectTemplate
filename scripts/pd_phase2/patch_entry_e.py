from pathlib import Path
import re

p = Path(r"c:/wincom/net10projects/ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor.cs")
t = p.read_text(encoding="utf-8")

# BeginEditLine - add ComponentDefinitionCode load + refresh picker
old = '''        LineSupplySource = string.IsNullOrWhiteSpace(line.SupplySource)
            ? PrMaterialSupplySources.Purchased
            : line.SupplySource;
        LineProducingRouteStepKey = line.ProducingRouteStepKey;
    }
'''
new = '''        LineSupplySource = string.IsNullOrWhiteSpace(line.SupplySource)
            ? PrMaterialSupplySources.Purchased
            : line.SupplySource;
        LineComponentDefinitionCode = line.ComponentDefinitionCode;
        LineProducingRouteStepKey = line.ProducingRouteStepKey;
        _ = RefreshComponentDefinitionOptionsAsync();
    }
'''
if old not in t:
    raise SystemExit("BeginEditLine tail missing")
t = t.replace(old, new, 1)

# AddOrUpdateLine - persist ComponentDefinitionCode
old = '''                target.SupplySource = LineSupplySource;
                target.ProducingRouteStepKey =
                    string.Equals(LineSupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal)
                        ? LineProducingRouteStepKey
                        : null;
'''
new = '''                target.SupplySource = LineSupplySource;
                target.ComponentDefinitionCode =
                    string.Equals(LineSupplySource, PrMaterialSupplySources.SeparateProductDefinition, StringComparison.Ordinal)
                        ? (string.IsNullOrWhiteSpace(LineComponentDefinitionCode)
                            ? null
                            : PrProductDefinitionCodes.Normalize(LineComponentDefinitionCode))
                        : null;
                target.ProducingRouteStepKey =
                    string.Equals(LineSupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal)
                        ? LineProducingRouteStepKey
                        : null;
'''
if old not in t:
    raise SystemExit("AddOrUpdate edit path missing")
t = t.replace(old, new, 1)

old = '''                SupplySource = LineSupplySource,
                ProducingRouteStepKey =
                    string.Equals(LineSupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal)
                        ? LineProducingRouteStepKey
                        : null
            });
'''
new = '''                SupplySource = LineSupplySource,
                ComponentDefinitionCode =
                    string.Equals(LineSupplySource, PrMaterialSupplySources.SeparateProductDefinition, StringComparison.Ordinal)
                        ? (string.IsNullOrWhiteSpace(LineComponentDefinitionCode)
                            ? null
                            : PrProductDefinitionCodes.Normalize(LineComponentDefinitionCode))
                        : null,
                ProducingRouteStepKey =
                    string.Equals(LineSupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal)
                        ? LineProducingRouteStepKey
                        : null
            });
'''
if old not in t:
    raise SystemExit("AddOrUpdate add path missing")
t = t.replace(old, new, 1)

# SaveAsync navigation + owner update
old = '''            if (result.Data is not null)
            {
                Model = result.Data;
                CurrentOwnerProdCode = Model.ProdCode;
                if (IsNewMode && string.IsNullOrWhiteSpace(StructureRootProdCode))
                {
                    StructureRootProdCode = Model.ProdCode;
                }

                CaptureClean();
            }

            // Keep structure rooted at route product
            if (IsNewMode)
            {
                Navigation.NavigateTo($"/planning/product-definitions/edit/{Uri.EscapeDataString(StructureRootProdCode)}");
                return;
            }

            await LoadPersistedStructureAsync(null);
            RebuildDisplayNodes();

            if (activate && IsEditingRoot)
            {
                Navigation.NavigateTo($"/planning/product-definitions/view/{Uri.EscapeDataString(StructureRootProdCode)}");
            }
'''
new = '''            if (result.Data is not null)
            {
                Model = result.Data;
                CurrentOwnerProdCode = Model.ProdCode;
                CurrentOwnerDefinitionCode = string.IsNullOrWhiteSpace(Model.DefinitionCode)
                    ? PrProductDefinitionCodes.Standard
                    : PrProductDefinitionCodes.Normalize(Model.DefinitionCode);
                if (IsNewMode && string.IsNullOrWhiteSpace(StructureRootProdCode))
                {
                    StructureRootProdCode = Model.ProdCode;
                    StructureRootDefinitionCode = CurrentOwnerDefinitionCode;
                }

                CaptureClean();
            }

            // Keep structure rooted at route product + definition
            if (IsNewMode)
            {
                Navigation.NavigateTo(CanonicalEntryUrl("edit", StructureRootProdCode, StructureRootDefinitionCode));
                return;
            }

            await LoadPersistedStructureAsync(null);
            RebuildDisplayNodes();

            if (activate && IsEditingRoot)
            {
                Navigation.NavigateTo(CanonicalEntryUrl("view", StructureRootProdCode, StructureRootDefinitionCode));
            }
'''
if old not in t:
    raise SystemExit("SaveAsync nav missing")
t = t.replace(old, new, 1)

# CreateNewVersion / GoEdit / GoExplode
reps = [
('''            var result = await ProductDefs.CreateNewVersionAsync(Model.ProdCode, CurrentDefinitionCode, Model.Version);
''',
 '''            var result = await ProductDefs.CreateNewVersionAsync(Model.ProdCode, CurrentOwnerDefinitionCode, Model.Version);
'''),
('''            Navigation.NavigateTo($"/planning/product-definitions/edit/{Uri.EscapeDataString(StructureRootProdCode)}");
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected Task GoEditAsync()
    {
        Navigation.NavigateTo($"/planning/product-definitions/edit/{Uri.EscapeDataString(StructureRootProdCode)}");
        return Task.CompletedTask;
    }

    protected Task GoExplodeAsync()
    {
        Navigation.NavigateTo($"/planning/product-definitions/explode/{Uri.EscapeDataString(StructureRootProdCode)}");
        return Task.CompletedTask;
    }
''',
 '''            Navigation.NavigateTo(CanonicalEntryUrl("edit", StructureRootProdCode, StructureRootDefinitionCode));
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected Task GoEditAsync()
    {
        Navigation.NavigateTo(CanonicalEntryUrl("edit", StructureRootProdCode, StructureRootDefinitionCode));
        return Task.CompletedTask;
    }

    protected Task GoExplodeAsync()
    {
        Navigation.NavigateTo(
            $"/planning/product-definitions/explode/{Uri.EscapeDataString(StructureRootProdCode)}/{Uri.EscapeDataString(StructureRootDefinitionCode)}");
        return Task.CompletedTask;
    }

    private static string CanonicalEntryUrl(string mode, string prodCode, string definitionCode) =>
        $"/planning/product-definitions/{mode}/{Uri.EscapeDataString(prodCode)}/{Uri.EscapeDataString(definitionCode)}";
'''),
]

for i, (old, new) in enumerate(reps):
    if old not in t:
        raise SystemExit(f"nav rep {i} missing")
    t = t.replace(old, new, 1)

# Cancel discard pending
old = '''            _pendingOwnerProdCode = null;
            _pendingAddAfterSwitch = false;
            ConfirmDiscardVisible = true;
'''
# only the CancelAsync one - careful; there may be multiple. Use surrounding context.
old = '''    protected Task CancelAsync()
    {
        if (IsDirty)
        {
            _pendingOwnerProdCode = null;
            _pendingAddAfterSwitch = false;
            ConfirmDiscardVisible = true;
            return Task.CompletedTask;
        }
'''
new = '''    protected Task CancelAsync()
    {
        if (IsDirty)
        {
            _pendingOwnerProdCode = null;
            _pendingOwnerDefinitionCode = null;
            _pendingAddAfterSwitch = false;
            ConfirmDiscardVisible = true;
            return Task.CompletedTask;
        }
'''
if old not in t:
    raise SystemExit("CancelAsync missing")
t = t.replace(old, new, 1)

old = '''        if (!string.IsNullOrWhiteSpace(_pendingOwnerProdCode))
        {
            var owner = _pendingOwnerProdCode;
            var add = _pendingAddAfterSwitch;
            var selectKey = CurrentSelectedNodeKey;
            _pendingOwnerProdCode = null;
            _pendingAddAfterSwitch = false;
            await SwitchOwnerAsync(owner, add, selectKey);
            return;
        }
'''
new = '''        if (!string.IsNullOrWhiteSpace(_pendingOwnerProdCode))
        {
            var owner = _pendingOwnerProdCode;
            var ownerDef = _pendingOwnerDefinitionCode ?? CurrentOwnerDefinitionCode;
            var add = _pendingAddAfterSwitch;
            var selectKey = CurrentSelectedNodeKey;
            _pendingOwnerProdCode = null;
            _pendingOwnerDefinitionCode = null;
            _pendingAddAfterSwitch = false;
            await SwitchOwnerAsync(owner, ownerDef, add, selectKey);
            return;
        }
'''
if old not in t:
    raise SystemExit("ConfirmDiscard owner missing")
t = t.replace(old, new, 1)

old = '''        ConfirmDiscardVisible = false;
        _pendingOwnerProdCode = null;
        _pendingAddAfterSwitch = false;
        _pendingUpdateAfterDiscard = false;
        _pendingSelectionAction = null;
'''
new = '''        ConfirmDiscardVisible = false;
        _pendingOwnerProdCode = null;
        _pendingOwnerDefinitionCode = null;
        _pendingAddAfterSwitch = false;
        _pendingUpdateAfterDiscard = false;
        _pendingSelectionAction = null;
'''
if old not in t:
    raise SystemExit("CancelDiscard missing")
t = t.replace(old, new, 1)

p.write_text(t, encoding="utf-8")
print("phase E save/nav/line ok")
