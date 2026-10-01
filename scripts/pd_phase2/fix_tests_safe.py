from pathlib import Path
import re

# Safer minimal signature fixes only — do not strip EffectiveFrom lines (property gone on VM,
# so remove only assignments that are clearly PrProductDefEditVm property initializers).

def fix_file(path: Path):
    t = path.read_text(encoding="utf-8")
    orig = t
    std = "PrProductDefinitionCodes.Standard"

    # Structure key 1-arg / 3-arg overloads
    t = re.sub(
        r'PrBomStructureKeys\.Root\("([^"]+)"\)',
        rf'PrBomStructureKeys.Root("\1", {std})',
        t,
    )
    # Line(parent, "OWNER", id) where id has no commas
    t = re.sub(
        r'PrBomStructureKeys\.Line\(([^,\n]+),\s*"([^"]+)",\s*([^,\n\)]+)\)',
        rf'PrBomStructureKeys.Line(\1, "\2", {std}, \3)',
        t,
    )

    # defs.GetAsync / sut.GetAsync with one string literal
    t = re.sub(
        r'(\w+)\.GetAsync\("([^"]+)"\)',
        rf'\1.GetAsync("\2", {std})',
        t,
    )
    t = re.sub(
        r'(\w+)\.GetAsync\("([^"]+)",\s*(\d+)\)',
        rf'\1.GetAsync("\2", {std}, \3)',
        t,
    )

    t = re.sub(
        r'(\w+)\.CreateNewVersionAsync\("([^"]+)"\)',
        rf'\1.CreateNewVersionAsync("\2", {std})',
        t,
    )
    t = re.sub(
        r'(\w+)\.CreateNewVersionAsync\("([^"]+)",\s*(\d+)\)',
        rf'\1.CreateNewVersionAsync("\2", {std}, \3)',
        t,
    )
    t = re.sub(
        r'(\w+)\.CreateNewVersionAsync\("([^"]+)",\s*([a-zA-Z_][\w\.]*)\)',
        rf'\1.CreateNewVersionAsync("\2", {std}, \3)',
        t,
    )

    t = re.sub(
        r'(\w+)\.DeleteAsync\(\["([^"]+)"\]\)',
        rf'\1.DeleteAsync([new PrProductDefinitionKey {{ ProdCode = "\2", DefinitionCode = {std} }}])',
        t,
    )

    # ActivateAsync(prod, ver, from, to, rv) -> ActivateAsync(prod, STANDARD, ver, rv)
    t = re.sub(
        r'(\w+)\.ActivateAsync\(\s*"([^"]+)"\s*,\s*([^,]+)\s*,\s*[^,]+\s*,\s*[^,]+\s*,\s*([^)]+)\)',
        rf'\1.ActivateAsync("\2", {std}, \3, \4)',
        t,
    )

    # In PrProductDefEditVm object initializers, replace EffectiveFrom/To with DefinitionCode if missing
    def patch_vm(m):
        body = m.group(0)
        body2 = re.sub(r'\s*EffectiveFrom\s*=\s*[^,\n}]+,?', '', body)
        body2 = re.sub(r'\s*EffectiveTo\s*=\s*[^,\n}]+,?', '', body2)
        if "DefinitionCode" not in body2 and "ProdCode" in body2:
            body2 = re.sub(
                r'(ProdCode\s*=\s*[^,\n]+,)',
                rf'\1\n            DefinitionCode = {std},',
                body2,
                count=1,
            )
        return body2

    t = re.sub(
        r'new PrProductDefEditVm\s*\{(?:[^{}]|\{[^{}]*\})*\}',
        patch_vm,
        t,
    )

    if t != orig:
        path.write_text(t, encoding="utf-8")
        return True
    return False

roots = [
    Path(r"c:/wincom/net10projects/ErpWeb.Tests/BomExplosionServiceTests.cs"),
    Path(r"c:/wincom/net10projects/ErpWeb.Tests/PrProductDefServiceTests.cs"),
    Path(r"c:/wincom/net10projects/ErpWeb.Tests/PrBomStructureTreeTests.cs"),
    Path(r"c:/wincom/net10projects/ErpWeb.Tests/ProductDefinitionAuthoringTests.cs"),
    Path(r"c:/wincom/net10projects/ErpWeb.Tests/PrBomStructureMergeTests.cs"),
]
for p in roots:
    print(("updated" if fix_file(p) else "unchanged"), p.name)
