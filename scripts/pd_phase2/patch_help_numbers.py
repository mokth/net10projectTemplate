from pathlib import Path

p = Path(r"c:/wincom/net10projects/ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor")
t = p.read_text(encoding="utf-8")
# Fix duplicate section 3 by renumbering subsequent headings (process from high to low)
replacements = [
    ("<h3>12. Tips</h3>", "<h3>13. Tips</h3>"),
    ("<h3>11. Save actions</h3>", "<h3>12. Save actions</h3>"),
    ("<h3>10. Labour</h3>", "<h3>11. Labour</h3>"),
    ("<h3>9. Machine alternatives</h3>", "<h3>10. Machine alternatives</h3>"),
    ("<h3>8. Machine</h3>", "<h3>9. Machine</h3>"),
    ("<h3>7. WIP from earlier route</h3>", "<h3>8. WIP from earlier route</h3>"),
    ("<h3>6. Alternate Materials</h3>", "<h3>7. Alternate Materials</h3>"),
    ("<h3>5. Materials / BOM</h3>", "<h3>6. Materials / BOM</h3>"),
    ("<h3>4. Process</h3>", "<h3>5. Process</h3>"),
    ("<h3>3. Route / Work Center</h3>", "<h3>4. Route / Work Center</h3>"),
]
for old, new in replacements:
    if old not in t:
        print("missing", old)
    else:
        t = t.replace(old, new, 1)
p.write_text(t, encoding="utf-8")
print("help renumbered")
