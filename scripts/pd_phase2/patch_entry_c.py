from pathlib import Path

p = Path(r"c:/wincom/net10projects/ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor.cs")
t = p.read_text(encoding="utf-8")

reps = []

# LoadPersistedStructureAsync
reps.append((
'''        var tree = await ProductDefs.GetStructureTreeAsync(StructureRootProdCode, CurrentDefinitionCode, version);
''',
'''        var tree = await ProductDefs.GetStructureTreeAsync(StructureRootProdCode, StructureRootDefinitionCode, version);
'''))

# RebuildDisplayNodes
reps.append((
'''        DisplayNodes = MergeStructureWithCurrentOwner(_persistedNodes, StructureRootProdCode, CurrentOwnerProdCode, Model);
''',
'''        DisplayNodes = MergeStructureWithCurrentOwner(
            _persistedNodes,
            StructureRootProdCode,
            StructureRootDefinitionCode,
            CurrentOwnerProdCode,
            CurrentOwnerDefinitionCode,
            Model);
'''))

# OnProdCodeChanged
reps.append((
'''        Model.ProdCode = code ?? string.Empty;
        StructureRootProdCode = (code ?? string.Empty).Trim().ToUpperInvariant();
        CurrentOwnerProdCode = StructureRootProdCode;
        UpdateProdCode = StructureRootProdCode;
''',
'''        Model.ProdCode = code ?? string.Empty;
        StructureRootProdCode = (code ?? string.Empty).Trim().ToUpperInvariant();
        CurrentOwnerProdCode = StructureRootProdCode;
        if (string.IsNullOrWhiteSpace(Model.DefinitionCode))
        {
            Model.DefinitionCode = PrProductDefinitionCodes.Standard;
        }
        StructureRootDefinitionCode = PrProductDefinitionCodes.Normalize(Model.DefinitionCode);
        CurrentOwnerDefinitionCode = StructureRootDefinitionCode;
        UpdateProdCode = StructureRootProdCode;
'''))

# OnProductSelectedAsync
reps.append((
'''        StructureRootProdCode = row.ICode.Trim().ToUpperInvariant();
        CurrentOwnerProdCode = StructureRootProdCode;
        UpdateProdCode = StructureRootProdCode;
        RebuildDisplayNodes();
''',
'''        StructureRootProdCode = row.ICode.Trim().ToUpperInvariant();
        CurrentOwnerProdCode = StructureRootProdCode;
        if (string.IsNullOrWhiteSpace(Model.DefinitionCode))
        {
            Model.DefinitionCode = PrProductDefinitionCodes.Standard;
            Model.DefinitionName = PrProductDefinitionCodes.StandardName;
        }
        StructureRootDefinitionCode = PrProductDefinitionCodes.Normalize(Model.DefinitionCode);
        CurrentOwnerDefinitionCode = StructureRootDefinitionCode;
        UpdateProdCode = StructureRootProdCode;
        RebuildDisplayNodes();
'''))

for i, (old, new) in enumerate(reps):
    if old not in t:
        raise SystemExit(f"rep {i} missing:\n{old[:120]}")
    t = t.replace(old, new, 1)

p.write_text(t, encoding="utf-8")
print("phase C structure/product ok")
