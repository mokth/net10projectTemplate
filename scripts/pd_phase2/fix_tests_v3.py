from pathlib import Path
import re

std = "PrProductDefinitionCodes.Standard"
files = [
    Path(r"c:/wincom/net10projects/ErpWeb.Tests/BomExplosionServiceTests.cs"),
    Path(r"c:/wincom/net10projects/ErpWeb.Tests/PrProductDefServiceTests.cs"),
    Path(r"c:/wincom/net10projects/ErpWeb.Tests/PrBomStructureTreeTests.cs"),
    Path(r"c:/wincom/net10projects/ErpWeb.Tests/ProductDefinitionAuthoringTests.cs"),
    Path(r"c:/wincom/net10projects/ErpWeb.Tests/PrBomStructureMergeTests.cs"),
]

for p in files:
    t = p.read_text(encoding="utf-8")
    o = t
    t = re.sub(
        r'PrBomStructureKeys\.Root\("([^"]+)"\)',
        rf'PrBomStructureKeys.Root("\1", {std})',
        t,
    )
    t = re.sub(
        r'PrBomStructureKeys\.Line\(([^,\n]+),\s*"([^"]+)",\s*([^,\n\)]+)\)',
        rf'PrBomStructureKeys.Line(\1, "\2", {std}, \3)',
        t,
    )
    t = re.sub(r'(\w+)\.GetAsync\("([^"]+)"\)', rf'\1.GetAsync("\2", {std})', t)
    t = re.sub(r'(\w+)\.GetAsync\("([^"]+)",\s*(\d+)\)', rf'\1.GetAsync("\2", {std}, \3)', t)
    t = re.sub(r'(\w+)\.CreateNewVersionAsync\("([^"]+)"\)', rf'\1.CreateNewVersionAsync("\2", {std})', t)
    t = re.sub(r'(\w+)\.CreateNewVersionAsync\("([^"]+)",\s*(\d+)\)', rf'\1.CreateNewVersionAsync("\2", {std}, \3)', t)
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
    t = re.sub(
        r'(\w+)\.ActivateAsync\(\s*"([^"]+)"\s*,\s*([^,]+)\s*,\s*[^,]+\s*,\s*[^,]+\s*,\s*([^)]+)\)',
        rf'\1.ActivateAsync("\2", {std}, \3, \4)',
        t,
    )
    t = re.sub(r"\r?\n\s*EffectiveFrom\s*=\s*new DateTime\([^)]*\),?", "", t)
    t = re.sub(r"\r?\n\s*EffectiveTo\s*=\s*new DateTime\([^)]*\),?", "", t)
    t = re.sub(r"\r?\n\s*EffectiveFrom\s*=\s*[^,\r\n}]+,?", "", t)
    t = re.sub(r"\r?\n\s*EffectiveTo\s*=\s*[^,\r\n}]+,?", "", t)

    lines = t.splitlines(True)
    out = []
    for i, line in enumerate(lines):
        out.append(line)
        if re.search(r"ProdCode\s*=", line):
            window = "".join(lines[i : i + 12])
            prelude = "".join(lines[max(0, i - 8) : i + 1])
            if "PrProductDefEditVm" in prelude and "DefinitionCode" not in window:
                indent = re.match(r"^(\s*)", line).group(1)
                out.append(f"{indent}DefinitionCode = {std},\n")
    t = "".join(out)

    if t != o:
        p.write_text(t, encoding="utf-8")
        print("updated", p.name)
    else:
        print("unchanged", p.name)
