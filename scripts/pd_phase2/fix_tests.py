from pathlib import Path
import re

# Minimal compile fixes for Product Definition tests — use STANDARD definition and new signatures.
std = "PrProductDefinitionCodes.Standard"

files = [
    Path(r"c:/wincom/net10projects/ErpWeb.Tests/PrProductDefServiceTests.cs"),
    Path(r"c:/wincom/net10projects/ErpWeb.Tests/PrBomStructureTreeTests.cs"),
    Path(r"c:/wincom/net10projects/ErpWeb.Tests/BomExplosionServiceTests.cs"),
    Path(r"c:/wincom/net10projects/ErpWeb.Tests/ProductDefinitionAuthoringTests.cs"),
    Path(r"c:/wincom/net10projects/ErpWeb.Tests/PrBomStructureMergeTests.cs"),
]

for p in files:
    if not p.exists():
        print("skip missing", p)
        continue
    t = p.read_text(encoding="utf-8")
    orig = t

    # Structure keys
    t = re.sub(
        r'PrBomStructureKeys\.Root\("([^"]+)"\)',
        rf'PrBomStructureKeys.Root("\1", {std})',
        t,
    )
    t = re.sub(
        r'PrBomStructureKeys\.Line\(([^,]+),\s*"([^"]+)",\s*([^)]+)\)',
        rf'PrBomStructureKeys.Line(\1, "\2", {std}, \3)',
        t,
    )
    # Line with variables: Line(parent, owner, id) — careful already 4-arg
    t = re.sub(
        r'PrBomStructureKeys\.Line\(([^,]+),\s*"FG001",\s*([^,\)]+)\)',
        rf'PrBomStructureKeys.Line(\1, "FG001", {std}, \2)',
        t,
    )

    # GetAsync("X") -> GetAsync("X", STANDARD) but not GetAsync("X", 1)
    t = re.sub(
        r'\.GetAsync\("([^"]+)"\)',
        rf'.GetAsync("\1", {std})',
        t,
    )
    t = re.sub(
        r'\.GetAsync\("([^"]+)",\s*(\d+)\)',
        rf'.GetAsync("\1", {std}, \2)',
        t,
    )
    t = re.sub(
        r'\.GetAsync\(([^,\n"]+)\)',
        # only simple identifiers that are already not multi-arg — skip if too risky
        lambda m: m.group(0),
        t,
    )

    # CreateNewVersionAsync("X") / ("X", n)
    t = re.sub(
        r'\.CreateNewVersionAsync\("([^"]+)"\)',
        rf'.CreateNewVersionAsync("\1", {std})',
        t,
    )
    t = re.sub(
        r'\.CreateNewVersionAsync\("([^"]+)",\s*(\d+)\)',
        rf'.CreateNewVersionAsync("\1", {std}, \2)',
        t,
    )
    t = re.sub(
        r'\.CreateNewVersionAsync\("([^"]+)",\s*([^,\)]+)\)',
        rf'.CreateNewVersionAsync("\1", {std}, \2)',
        t,
    )

    # DeleteAsync(["X"]) -> DeleteAsync([new PrProductDefinitionKey{...}])
    t = re.sub(
        r'\.DeleteAsync\(\["([^"]+)"\]\)',
        rf'.DeleteAsync([new PrProductDefinitionKey {{ ProdCode = "\1", DefinitionCode = {std} }}])',
        t,
    )

    # Remove EffectiveFrom assignments in PrProductDefEditVm initializers (property gone)
    t = re.sub(r'\n\s*EffectiveFrom\s*=\s*[^,\n]+,?', '', t)
    t = re.sub(r'\n\s*EffectiveTo\s*=\s*[^,\n]+,?', '', t)

    # When constructing PrProductDefEditVm for SaveAsync, ensure DefinitionCode if ProdCode present
    # Add DefinitionCode = STANDARD after ProdCode = "..." when missing nearby — lightweight
    def ensure_def(m):
        block = m.group(0)
        if "DefinitionCode" in block:
            return block
        return block.replace(
            m.group(1),
            m.group(1) + f"\n            DefinitionCode = {std},",
            1,
        )

    t = re.sub(
        r'(new PrProductDefEditVm\s*\{[^}]*?ProdCode\s*=\s*"[^"]+",)',
        ensure_def,
        t,
        flags=re.DOTALL,
    )

    if t != orig:
        p.write_text(t, encoding="utf-8")
        print("updated", p.name)
    else:
        print("unchanged", p.name)
