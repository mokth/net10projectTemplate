from pathlib import Path
import re

std = "PrProductDefinitionCodes.Standard"

# Fix double-STANDARD CreateNewVersionAsync
for p in Path(r"c:/wincom/net10projects/ErpWeb.Tests").glob("*.cs"):
    t = p.read_text(encoding="utf-8")
    o = t
    t = t.replace(
        f'CreateNewVersionAsync("A", {std}, {std})',
        f'CreateNewVersionAsync("A", {std})',
    )
    t = t.replace(
        f'CreateNewVersionAsync("FG001", {std}, {std})',
        f'CreateNewVersionAsync("FG001", {std})',
    )
    # any remaining double
    t = re.sub(
        rf'CreateNewVersionAsync\("([^"]+)",\s*{re.escape(std)},\s*{re.escape(std)}\)',
        rf'CreateNewVersionAsync("\1", {std})',
        t,
    )

    # GetStructureTreeAsync("X") and GetStructureTreeAsync("X", n)
    t = re.sub(
        r'(\w+)\.GetStructureTreeAsync\("([^"]+)"\)',
        rf'\1.GetStructureTreeAsync("\2", {std})',
        t,
    )
    t = re.sub(
        r'(\w+)\.GetStructureTreeAsync\("([^"]+)",\s*(\d+)\)',
        rf'\1.GetStructureTreeAsync("\2", {std}, \3)',
        t,
    )
    t = re.sub(
        r'(\w+)\.GetStructureTreeAsync\("([^"]+)",\s*([a-zA-Z_][\w\.]*)\)',
        rf'\1.GetStructureTreeAsync("\2", {std}, \3)',
        t,
    )

    # CanDelete/Delete with string list
    t = re.sub(
        r'\.CanDeleteAsync\(\["([^"]+)"\]\)',
        rf'.CanDeleteAsync([new PrProductDefinitionKey {{ ProdCode = "\1", DefinitionCode = {std} }}])',
        t,
    )
    t = re.sub(
        r'\.DeleteAsync\(\["([^"]+)"\]\)',
        rf'.DeleteAsync([new PrProductDefinitionKey {{ ProdCode = "\1", DefinitionCode = {std} }}])',
        t,
    )

    if t != o:
        p.write_text(t, encoding="utf-8")
        print("updated", p.name)
