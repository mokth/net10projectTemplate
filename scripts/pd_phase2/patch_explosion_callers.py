from pathlib import Path
import re

# Fix ProductionWorkOrderService explosion call
p = Path(r"c:/wincom/net10projects/ErpWeb.Core/Production/ProductionWorkOrderService.cs")
t = p.read_text(encoding="utf-8")
old = """            explosion = await _bomExplosion.ExplodeInContextAsync(db, scope.CompanyCode,
                new BomExplosionRequest
                {
                    ProdCode = normalized.ProductCode,
                    Quantity = normalized.PlannedQty,
                    AsOfDate = normalized.SnapshotAsOfDate,
                    Mode = BomExplosionMode.ProductionIssueRequirement
                }, cancellationToken);"""
new = """            explosion = await _bomExplosion.ExplodeInContextAsync(db, scope.CompanyCode,
                new BomExplosionRequest
                {
                    ProdCode = normalized.ProductCode,
                    DefinitionCode = string.IsNullOrWhiteSpace(request.DefinitionCode)
                        ? PrProductDefinitionCodes.Standard
                        : PrProductDefinitionCodes.Normalize(request.DefinitionCode),
                    Quantity = normalized.PlannedQty,
                    Mode = BomExplosionMode.ProductionIssueRequirement
                }, cancellationToken);"""
if old not in t:
    raise SystemExit("WO explosion call missing")
t = t.replace(old, new, 1)
p.write_text(t, encoding="utf-8")
print("WO ok")

# Fix BomExplosionServiceTests
p = Path(r"c:/wincom/net10projects/ErpWeb.Tests/BomExplosionServiceTests.cs")
t = p.read_text(encoding="utf-8")
t = t.replace("AsOfDate = DateTime.UtcNow.Date,\n", "")
# Replace Effective_date test
old_test = re.search(
    r"\[Fact\]\s*public async Task Effective_date_selects_correct_version\(\).*?^\s*\}",
    t,
    re.M | re.S,
)
if not old_test:
    raise SystemExit("effective date test missing")

replacement = '''[Fact]
    public async Task Explicit_version_selects_historical_revision()
    {
        var defs = CreateDefs();
        var v1 = await defs.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "A",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            BaseQty = 1m,
            Lines = [new PrProductDefLineVm { ICode = "AA", StdQty = 1m, Warehouse = "WH01", SeqNo = 1 }]
        }, true, true);
        Assert.True(v1.Succeeded, v1.Message);

        var draft = await defs.CreateNewVersionAsync("A", PrProductDefinitionCodes.Standard);
        draft.Data!.Lines[0].StdQty = 5m;
        var v2 = await defs.SaveAsync(new PrProductDefEditVm
        {
            ProdCode = "A",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            Version = draft.Data.Version,
            BomHdrId = draft.Data.BomHdrId,
            HeaderRowVersion = draft.Data.HeaderRowVersion,
            Status = draft.Data.Status,
            BaseQty = 1m,
            Lines = draft.Data.Lines
        }, false, true);
        Assert.True(v2.Succeeded, v2.Message);

        var explode = CreateExplosion();
        var historical = await explode.ExplodeAsync(new BomExplosionRequest
        {
            ProdCode = "A",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            Quantity = 1m,
            Version = 1,
            Mode = BomExplosionMode.ProductionIssueRequirement
        });
        Assert.Equal(1m, historical.Data!.Nodes.Single().ExtendedQty);

        var active = await explode.ExplodeAsync(new BomExplosionRequest
        {
            ProdCode = "A",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            Quantity = 1m,
            Mode = BomExplosionMode.ProductionIssueRequirement
        });
        Assert.Equal(5m, active.Data!.Nodes.Single().ExtendedQty);
    }'''

t = t[:old_test.start()] + replacement + t[old_test.end():]

# Ensure DefinitionCode on remaining BomExplosionRequest initializers that lack it
def ensure_def(m):
    body = m.group(0)
    if "DefinitionCode" in body:
        return body
    return body.replace(
        "new BomExplosionRequest\n        {",
        "new BomExplosionRequest\n        {\n            DefinitionCode = PrProductDefinitionCodes.Standard,",
        1,
    )

t = re.sub(r"new BomExplosionRequest\s*\{[^}]*\}", ensure_def, t, flags=re.S)
p.write_text(t, encoding="utf-8")
print("tests ok")
