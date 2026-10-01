from pathlib import Path

p = Path(r"c:/wincom/net10projects/ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor.cs")
t = p.read_text(encoding="utf-8")

replacements = []

replacements.append((
    '''    [Parameter] public string Mode { get; set; } = "view";
    [Parameter] public string? ProdCode { get; set; }
''',
    '''    [Parameter] public string Mode { get; set; } = "view";
    [Parameter] public string? ProdCode { get; set; }
    [Parameter] public string? DefinitionCode { get; set; }
''',
))

replacements.append((
    '''    private string? _pendingOwnerProdCode;
    private bool _pendingAddAfterSwitch;
''',
    '''    private string? _pendingOwnerProdCode;
    private string? _pendingOwnerDefinitionCode;
    private bool _pendingAddAfterSwitch;
''',
))

replacements.append((
    '''    protected string StructureRootProdCode { get; set; } = string.Empty;

    private string CurrentDefinitionCode =>
        string.IsNullOrWhiteSpace(Model.DefinitionCode)
            ? PrProductDefinitionCodes.Standard
            : PrProductDefinitionCodes.Normalize(Model.DefinitionCode);


    protected string CurrentOwnerProdCode { get; set; } = string.Empty;
''',
    '''    protected string StructureRootProdCode { get; set; } = string.Empty;
    protected string StructureRootDefinitionCode { get; set; } = PrProductDefinitionCodes.Standard;

    protected string CurrentOwnerProdCode { get; set; } = string.Empty;
    protected string CurrentOwnerDefinitionCode { get; set; } = PrProductDefinitionCodes.Standard;

    protected bool NeedsDefinitionChoice { get; set; }
    protected IReadOnlyList<PrProductDefinitionLookupRow> DefinitionChoices { get; set; } = [];

    protected string? LineComponentDefinitionCode { get; set; }
    protected IReadOnlyList<DefinitionOption> ComponentDefinitionOptions { get; set; } = [];
''',
))

replacements.append((
    '''    protected string? PendingOwnerProdCode => _pendingOwnerProdCode;
''',
    '''    protected string? PendingOwnerProdCode => _pendingOwnerProdCode;
    protected string? PendingOwnerDefinitionCode => _pendingOwnerDefinitionCode;
''',
))

replacements.append((
    '''    protected bool IsEditingRoot =>
        string.Equals(CurrentOwnerProdCode, StructureRootProdCode, StringComparison.OrdinalIgnoreCase);

    protected string OwnerBreadcrumb
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StructureRootProdCode))
            {
                return string.Empty;
            }

            if (IsEditingRoot)
            {
                return StructureRootProdCode;
            }

            return $"{StructureRootProdCode} › {CurrentOwnerProdCode}";
        }
    }
''',
    '''    protected bool IsEditingRoot =>
        string.Equals(CurrentOwnerProdCode, StructureRootProdCode, StringComparison.OrdinalIgnoreCase)
        && string.Equals(CurrentOwnerDefinitionCode, StructureRootDefinitionCode, StringComparison.OrdinalIgnoreCase);

    protected string OwnerBreadcrumb
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StructureRootProdCode))
            {
                return string.Empty;
            }

            var root = FormatOwnerLabel(StructureRootProdCode, StructureRootDefinitionCode);
            if (IsEditingRoot)
            {
                return root;
            }

            return $"{root} › {FormatOwnerLabel(CurrentOwnerProdCode, CurrentOwnerDefinitionCode)}";
        }
    }

    private static string FormatOwnerLabel(string? prodCode, string? definitionCode) =>
        $"{PrBomStructureKeys.Normalize(prodCode)} [{PrBomStructureKeys.Normalize(definitionCode)}]";
''',
))

replacements.append((
    '''    protected sealed record Option(string Value, string Text);
    protected sealed record ProducerOption(Guid? Value, string Text);
''',
    '''    protected sealed record Option(string Value, string Text);
    protected sealed record ProducerOption(Guid? Value, string Text);
    protected sealed record DefinitionOption(string Value, string Text);
''',
))

for i, (old, new) in enumerate(replacements):
    if old not in t:
        raise SystemExit(f"replacement {i} missing")
    t = t.replace(old, new, 1)

p.write_text(t, encoding="utf-8")
print("phase A ok")
