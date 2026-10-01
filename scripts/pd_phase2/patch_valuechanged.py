from pathlib import Path

for path, old, new in [
    (
        Path(r"c:/wincom/net10projects/ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor"),
        'ValueChanged="@((string v) => OnLineSupplySourceChanged(v))"',
        'ValueChanged="OnLineSupplySourceChanged"',
    ),
    (
        Path(r"c:/wincom/net10projects/ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor"),
        'ValueChanged="@((string? v) => OnChangeDefinitionTargetChanged(v))"',
        'ValueChanged="OnChangeDefinitionTargetChanged"',
    ),
]:
    t = path.read_text(encoding="utf-8")
    if old not in t:
        print(path.name, "pattern missing")
        idx = t.find("ValueChanged=")
        # show nearby Supply/Change
        for key in ["OnLineSupplySourceChanged", "OnChangeDefinitionTargetChanged"]:
            i = t.find(key)
            if i >= 0:
                print(path.name, key, repr(t[i - 40 : i + len(key) + 10]))
        continue
    path.write_text(t.replace(old, new, 1), encoding="utf-8")
    print(path.name, "ok")
