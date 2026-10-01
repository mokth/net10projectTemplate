from pathlib import Path

p = Path(r"c:/wincom/net10projects/ErpWeb.UI/Planning/Masters/PrProductDefList.razor.cs")
t = p.read_text(encoding="utf-8")

t = t.replace(
    """                var revision = await ProductDefs.CreateNewVersionAsync(
                    info.SelectedRow.ProdCode, info.SelectedRow.BomVersion);""",
    """                var revision = await ProductDefs.CreateNewVersionAsync(
                    info.SelectedRow.ProdCode, info.SelectedRow.DefinitionCode, info.SelectedRow.LatestVersion);""",
)

old_del = """            var codes = _selectedRows.Select(x => x.ProdCode).ToList();
            var result = await ProductDefs.DeleteAsync(codes);"""
new_del = """            var codes = _selectedRows.Select(x => new PrProductDefinitionKey
            {
                ProdCode = x.ProdCode,
                DefinitionCode = x.DefinitionCode
            }).ToList();
            var result = await ProductDefs.DeleteAsync(codes);"""
if old_del not in t:
    raise SystemExit("delete block not found")
t = t.replace(old_del, new_del)

old_msg = """            StatusMessage = codes.Count == 1
                ? $"Deleted product definition {codes[0]}."
                : $"Deleted {codes.Count} product definitions.";"""
new_msg = """            StatusMessage = codes.Count == 1
                ? $"Deleted product definition {codes[0].ProdCode}[{codes[0].DefinitionCode}]."
                : $"Deleted {codes.Count} product definitions.";"""
if old_msg not in t:
    raise SystemExit("status msg not found")
t = t.replace(old_msg, new_msg)

p.write_text(t, encoding="utf-8")
print("list ok")
