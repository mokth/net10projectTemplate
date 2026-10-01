from pathlib import Path
import re

p = Path(r"c:/wincom/net10projects/ErpWeb.Tests/BomExplosionServiceTests.cs")
t = p.read_text(encoding="utf-8")
t = t.replace("            AsOfDate = DateTime.UtcNow.Date,\n", "")
t = t.replace("            AsOfDate = new DateTime(2026, 3, 31),\n", "            Version = 1,\n")
t = t.replace("            AsOfDate = new DateTime(2026, 4, 1),\n", "")

# Rename test and adjust assertions comments
t = t.replace(
    "public async Task Effective_date_selects_correct_version()",
    "public async Task Explicit_version_selects_historical_revision()",
)
t = t.replace("var early = await explode.ExplodeAsync", "var historical = await explode.ExplodeAsync")
t = t.replace("Assert.Equal(1m, early.Data!.Nodes.Single().ExtendedQty);", "Assert.Equal(1m, historical.Data!.Nodes.Single().ExtendedQty);")
t = t.replace("var late = await explode.ExplodeAsync", "var active = await explode.ExplodeAsync")
t = t.replace("Assert.Equal(5m, late.Data!.Nodes.Single().ExtendedQty);", "Assert.Equal(5m, active.Data!.Nodes.Single().ExtendedQty);")

def ensure_def(m):
    body = m.group(0)
    if "DefinitionCode" in body:
        return body
    insert = "new BomExplosionRequest\n        {\n            DefinitionCode = PrProductDefinitionCodes.Standard,"
    return re.sub(r"new BomExplosionRequest\s*\{", insert, body, count=1)

t = re.sub(r"new BomExplosionRequest\s*\{[^}]*\}", ensure_def, t, flags=re.S)
p.write_text(t, encoding="utf-8")
print("BomExplosionServiceTests fixed")
print("AsOfDate left", t.count("AsOfDate"))
