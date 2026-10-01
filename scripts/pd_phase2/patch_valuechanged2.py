from pathlib import Path

# Entry
p = Path(r"c:/wincom/net10projects/ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor")
t = p.read_text(encoding="utf-8")
t = t.replace(
    'ValueChanged="OnLineSupplySourceChanged"',
    'ValueChanged="@((string? v) => OnLineSupplySourceChanged(v))"',
)
p.write_text(t, encoding="utf-8")
print("entry", "OnLineSupplySourceChanged" in t)

# WO
p = Path(r"c:/wincom/net10projects/ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor")
t = p.read_text(encoding="utf-8")
t = t.replace(
    'ValueChanged="OnChangeDefinitionTargetChanged"',
    'ValueChanged="@((string? v) => OnChangeDefinitionTargetChanged(v))"',
)
p.write_text(t, encoding="utf-8")
print("wo ok")
