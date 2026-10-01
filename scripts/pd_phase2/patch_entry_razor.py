from pathlib import Path

p = Path(r"c:/wincom/net10projects/ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor")
t = p.read_text(encoding="utf-8")

old = """                                            <DxComboBox Data=\"@SupplySourceOptions\"
                                                        @bind-Value=\"@LineSupplySource\"
                                                        TextFieldName=\"@nameof(Option.Text)\"
                                                        ValueFieldName=\"@nameof(Option.Value)\"
                                                        SearchMode=\"ListSearchMode.AutoSearch\" />"""
new = """                                            <DxComboBox Data=\"@SupplySourceOptions\"
                                                        Value=\"@LineSupplySource\"
                                                        ValueChanged=\"OnLineSupplySourceChanged\"
                                                        TextFieldName=\"@nameof(Option.Text)\"
                                                        ValueFieldName=\"@nameof(Option.Value)\"
                                                        SearchMode=\"ListSearchMode.AutoSearch\" />"""
if old not in t:
    raise SystemExit("supply source bind missing")
t = t.replace(old, new, 1)

marker = """        else
        {
            <div class=\"pdf-form\">
                <section class=\"iv-card pdf-overview-card\">"""
choice = """        else if (NeedsDefinitionChoice)
        {
            <section class=\"iv-card\">
                <header class=\"pdf-section__head\">
                    <div>
                        <h2 class=\"iv-card__title mb-0\">Choose a Production Definition</h2>
                        <p>Product <span class=\"code-style\">@ProdCode</span> has more than one definition and no single default.</p>
                    </div>
                </header>
                <div class=\"pdf-editor__actions\" style=\"flex-wrap:wrap;gap:.5rem\">
                    @foreach (var row in DefinitionChoices)
                    {
                        <DxButton RenderStyle=\"@(row.IsDefaultDefinition ? ButtonRenderStyle.Primary : ButtonRenderStyle.Secondary)\"
                                  Text=\"@(row.IsDefaultDefinition ? $\"{row.DefinitionCode} (Default)\" : row.DefinitionCode)\"
                                  Click=\"@(() => ChooseDefinition(row.DefinitionCode))\" />
                    }
                </div>
            </section>
        }
        else
        {
            <div class=\"pdf-form\">
                <section class=\"iv-card pdf-overview-card\">"""
if "NeedsDefinitionChoice" not in t:
    if marker not in t:
        raise SystemExit("form marker missing")
    t = t.replace(marker, choice, 1)

help_old = """                    <section>
                        <h3>2. Product Definition status</h3>"""
help_new = """                    <section>
                        <h3>2. Production Definition vs revision vs default</h3>
                        <dl class=\"pdf-help__actions\">
                            <dt>Production Definition</dt>
                            <dd>A named manufacturing method for the same product (for example STANDARD, LINE-B, SUBCON). Identity is Product Code + Definition Code.</dd>
                            <dt>Revision</dt>
                            <dd>A numbered Draft/Active/Superseded copy of that definition. Create a New Version when an Active recipe must change.</dd>
                            <dt>Default definition</dt>
                            <dd>The ACTIVE definition Work Orders and BOM Explorer pick when the user does not choose one explicitly. Only one ACTIVE default is allowed per product.</dd>
                        </dl>
                    </section>

                    <section>
                        <h3>3. Product Definition status</h3>"""
if "Production Definition vs revision vs default" not in t:
    if help_old not in t:
        raise SystemExit("help status missing")
    t = t.replace(help_old, help_new, 1)

old_d = """                        <text>You have unsaved changes on @CurrentOwnerProdCode. Discard them and switch to @PendingOwnerProdCode?</text>"""
new_d = """                        <text>You have unsaved changes on @CurrentOwnerProdCode [@CurrentOwnerDefinitionCode]. Discard them and switch to @PendingOwnerProdCode [@PendingOwnerDefinitionCode]?</text>"""
if old_d in t:
    t = t.replace(old_d, new_d, 1)

# Materials help line mention SEPARATE
old_m = """                            <li>Supply Source = where the material comes from (normal stock or Internal Route WIP).</li>"""
new_m = """                            <li>Supply Source = where the material comes from (Purchased, Internal Route WIP, Separate Product Definition, or External).</li>
                            <li>Component Production Definition = required when Supply Source is Separate Product Definition; selects the child product's definition to explode.</li>"""
if old_m in t and "Component Production Definition =" not in t:
    t = t.replace(old_m, new_m, 1)

p.write_text(t, encoding="utf-8")
print("razor UI extras ok")
