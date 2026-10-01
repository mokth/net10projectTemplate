from pathlib import Path

p = Path(r"c:/wincom/net10projects/ErpWeb.Core/Planning/PrProductDefService.cs")
text = p.read_text(encoding="utf-8")
marker = "    private async Task<List<NormalizedOperation>> ValidateOperationsAsync"
idx = text.find(marker)
assert idx > 0, "marker not found"
helpers = text[idx:]
Path(r"c:/wincom/net10projects/ErpWeb.Core/Planning/_helpers_tail.cs").write_text(helpers, encoding="utf-8")
print("saved", len(helpers))
