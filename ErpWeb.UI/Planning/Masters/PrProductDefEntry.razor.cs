using System.Text.Json;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Masters;

public partial class PrProductDefEntry : PageBase
{
    [Parameter] public string Mode { get; set; } = "view";
    [Parameter] public string? ProdCode { get; set; }
    [Parameter] public string? DefinitionCode { get; set; }

    [Inject] private IPrProductDefService ProductDefs { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private IPrWorkCentreService WorkCentreService { get; set; } = default!;
    [Inject] private IPrProcessService ProcessService { get; set; } = default!;
    [Inject] private IPrMachineService MachineService { get; set; } = default!;
    [Inject] private IPrOperatorService OperatorService { get; set; } = default!;
    [Inject] private IPrWorkPrefixService WorkPrefixService { get; set; } = default!;

    private string _cleanSnapshot = string.Empty;
    private string? _loadedKey;
    private int _tempSeq;
    private string? _pendingOwnerProdCode;
    private string? _pendingOwnerDefinitionCode;
    private bool _pendingAddAfterSwitch;
    private bool _pendingUpdateAfterDiscard;
    private Func<Task>? _pendingSelectionAction;
    private string _centreEditorSnapshot = string.Empty;
    private string _operationEditorSnapshot = string.Empty;
    private string _machineEditorSnapshot = string.Empty;
    private string _labourEditorSnapshot = string.Empty;
    private PrProductDefCentreProjection.CentreKey? _originalCentreKey;
    private IReadOnlyList<PrBomStructureNode> _persistedNodes = [];

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool ConfirmDiscardVisible;
    protected bool ConcurrencyVisible;
    protected bool HelpVisible;
    protected bool CanEdit;
    protected bool CanAdd;
    protected string? StatusMessage;
    private int _activeTabIndex;
    protected int ActiveTabIndex
    {
        get => _activeTabIndex;
        set
        {
            _activeTabIndex = value;
            // Operations (1) also needs a centre; Materials/Machines/Labour reuse the same selection.
            if (value is 1 or 2 or 3 or 4)
            {
                EnsureBomProcessSelected();
                if (value == 2)
                {
                    RebuildProcessBomNodes();
                }
                else if (value is 3 or 4)
                {
                    EnsureResourceSelection();
                }
            }
        }
    }
    protected string UpdateProdCode { get; set; } = string.Empty;
    protected PrProductDefEditVm Model { get; set; } = new();
    protected Dictionary<string, string> ValidationErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];
    protected IReadOnlyList<PrWorkCentre> WorkCentres { get; set; } = [];
    protected IReadOnlyList<PrProcess> Processes { get; set; } = [];
    protected IReadOnlyList<PrMachine> Machines { get; set; } = [];
    protected IReadOnlyList<PrOperator> Operators { get; set; } = [];
    protected IReadOnlyList<PrWorkPefix> WorkPrefixes { get; set; } = [];

    protected PrProductDefCentreProjection.CentreRowVm CentreEdit { get; set; } = NewCentre();
    protected PrProductDefCentreProjection.CentreRowVm? PendingCentre { get; set; }
    protected PrProductDefCentreProjection.CentreKey? SelectedCentreKey { get; set; }
    protected IReadOnlyList<PrProductDefCentreProjection.CentreRowVm> CentreRows { get; set; } = [];

    protected PrProductDefOperationVm OperationEdit { get; set; } = NewOperation();
    protected PrProductDefOperationVm? SelectedOperation { get; set; }
    protected PrProductDefMachineVm MachineEdit { get; set; } = NewMachine();
    protected PrProductDefMachineVm? SelectedMachine { get; set; }
    protected PrProductDefLabourVm LabourEdit { get; set; } = NewLabour();

    protected string StructureRootProdCode { get; set; } = string.Empty;
    protected string StructureRootDefinitionCode { get; set; } = PrProductDefinitionCodes.Standard;

    protected string CurrentOwnerProdCode { get; set; } = string.Empty;
    protected string CurrentOwnerDefinitionCode { get; set; } = PrProductDefinitionCodes.Standard;

    protected bool NeedsDefinitionChoice { get; set; }
    protected IReadOnlyList<PrProductDefinitionLookupRow> DefinitionChoices { get; set; } = [];

    protected string? LineComponentDefinitionCode { get; set; }
    protected IReadOnlyList<DefinitionOption> ComponentDefinitionOptions { get; set; } = [];
    protected string? CurrentSelectedNodeKey { get; set; }
    protected IReadOnlyList<PrBomStructureNode> DisplayNodes { get; set; } = [];
    protected IReadOnlyList<PrBomStructureNode> ProcessBomNodes { get; set; } = [];
    protected PrBomStructureNode? SelectedNode { get; set; }

    protected string? LineItem { get; set; }
    protected string? LineItemDesc { get; set; }
    protected string? LineUom { get; set; }
    protected string LineMfgType { get; set; } = PrMfgTypes.Buy;
    protected decimal LineStdQty { get; set; } = 1m;
    protected decimal LineScrapPercent { get; set; }
    protected string? LineWarehouse { get; set; }
    protected bool LineBomDefault { get; set; } = true;
    protected string? LineAlternateGroupCode { get; set; }
    protected decimal LineTolerance { get; set; }
    protected string LineIssueMethod { get; set; } = PrMaterialIssueMethods.Manual;
    protected string LineSupplySource { get; set; } = PrMaterialSupplySources.Purchased;
    protected Guid? LineProducingRouteStepKey { get; set; }
    protected long? LineEditUid { get; set; }
    protected string? LineEditTempId { get; set; }

    protected IReadOnlyList<Option> ProcessTypeOptions { get; } =
        PrProcessTypes.All.Select(x => new Option(x, x)).ToList();
    protected IReadOnlyList<Option> IssueMethodOptions { get; } =
        PrMaterialIssueMethods.All.Select(x => new Option(x, x)).ToList();
    protected IReadOnlyList<Option> SupplySourceOptions { get; } =
        PrMaterialSupplySources.All.Select(x => new Option(x, x)).ToList();

    protected IReadOnlyList<ProducerOption> ProducerOptionsForLine
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LineItem))
            {
                return [];
            }

            return Model.Operations
                .Where(x => x.RouteStepKey is { } key
                            && key != Guid.Empty
                            && string.Equals(x.OutputItemCode, LineItem, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.RouteStepKey!.Value)
                .Select(g =>
                {
                    var first = g.First();
                    return new ProducerOption(
                        g.Key,
                        $"{first.WorkCentreCode} · {first.OutputItemCode} · stage {first.CentralSequence}");
                })
                .OrderBy(x => x.Text, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    protected sealed record Option(string Value, string Text);
    protected sealed record ProducerOption(Guid? Value, string Text);
    protected sealed record DefinitionOption(string Value, string Text);

    protected static bool ShowsStandardDuration(PrProductDefOperationVm operation) =>
        PrProcessTypes.IsDurationBased(operation.ProcessType)
        || (string.Equals(operation.ProcessType, PrProcessTypes.Packing, StringComparison.Ordinal)
            && operation.Machines.Count == 0);

    protected string ProductDescText
    {
        get => Model.ProdDesc ?? string.Empty;
        set => Model.ProdDesc = value;
    }

    protected string ProductUomText
    {
        get => Model.StdUom ?? string.Empty;
        set => Model.StdUom = value;
    }

    protected string RemarkText
    {
        get => Model.Remark ?? string.Empty;
        set => Model.Remark = value;
    }

    protected string LineItemDescText
    {
        get => LineItemDesc ?? string.Empty;
        set => LineItemDesc = value;
    }

    protected string LineUomText
    {
        get => LineUom ?? string.Empty;
        set => LineUom = value;
    }

    protected string? PendingOwnerProdCode => _pendingOwnerProdCode;
    protected string? PendingOwnerDefinitionCode => _pendingOwnerDefinitionCode;

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => !IsNewMode && !IsEditMode;
    protected bool IsReadOnlyStatus =>
        Model.Status is PrBomStatuses.Active or PrBomStatuses.Superseded or PrBomStatuses.Inactive;

    protected string PageHeading => IsNewMode ? "New product definition"
        : IsEditMode ? "Edit product definition"
        : "View product definition";

    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";

    protected bool IsDirty =>
        !IsViewMode
        && !IsLoading
        && !string.Equals(_cleanSnapshot, Snapshot(Model), StringComparison.Ordinal);

    protected bool IsEditingRoot =>
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

    protected bool CanAddUnderSelection
    {
        get
        {
            if (IsViewMode || IsReadOnlyStatus)
            {
                return false;
            }

            if (SelectedNode is null)
            {
                return IsEditingRoot;
            }

            if (SelectedNode.ParentKey is null)
            {
                return true; // root
            }

            var mfg = PrMfgTypes.Normalize(SelectedNode.MfgType);
            return mfg is PrMfgTypes.Make or PrMfgTypes.Phantom;
        }
    }

    protected bool CanDeleteSelection =>
        !IsViewMode
        && !IsReadOnlyStatus
        && SelectedNode is { ParentKey: not null, OwnerProdCode: not null }
        && OwnersEqual(
            SelectedNode.OwnerProdCode,
            SelectedNode.OwnerDefinitionCode ?? CurrentOwnerDefinitionCode,
            CurrentOwnerProdCode,
            CurrentOwnerDefinitionCode);

    protected int CurrentOwnerLineCount => SelectedOperation is null ? 0
        : Model.Lines.Count(x => x.OperationKey == SelectedOperation.OperationKey);
    public static IReadOnlyList<PrBomStructureNode> FilterProcessBomNodes(
        IReadOnlyList<PrBomStructureNode> nodes, IReadOnlyList<PrProductDefLineVm> lines,
        string ownerCode, Guid? operationKey)
    {
        if (operationKey is null) return [];
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes.Where(x => string.Equals(x.OwnerProdCode, ownerCode, StringComparison.OrdinalIgnoreCase)))
        {
            var line = lines.FirstOrDefault(x => node.SourceLineUid is > 0
                ? x.Uid == node.SourceLineUid
                : node.LineTempId is not null && x.TempId == node.LineTempId);
            if (line?.OperationKey == operationKey) continue;
            excluded.Add(node.Key);
            CollectDescendantKeys(nodes, node.Key, excluded);
        }
        return nodes.Where(x => !excluded.Contains(x.Key)).ToList();
    }

    private void EnsureBomProcessSelected()
    {
        if (SelectedOperation is not null && Model.Operations.Contains(SelectedOperation))
        {
            if (!SelectedCentreKey.HasValue)
            {
                SelectedCentreKey = PrProductDefCentreProjection.CentreKey.From(SelectedOperation);
            }

            return;
        }

        SelectedOperation = (SelectedCentreKey is { } centre
                ? PrProductDefCentreProjection.FilterProcesses(Model.Operations, centre).FirstOrDefault()
                : null)
            ?? Model.Operations
                .OrderBy(x => x.CentralSequence)
                .ThenBy(x => x.ProcessSequence)
                .ThenBy(x => x.OperationCode)
                .FirstOrDefault();
        if (SelectedOperation is not null)
        {
            SelectedCentreKey = PrProductDefCentreProjection.CentreKey.From(SelectedOperation);
            return;
        }

        // No processes yet — still pick a centre so Operations shows the empty grid + editor.
        if (!SelectedCentreKey.HasValue && CentreRows.Count > 0)
        {
            var firstCentre = CentreRows.FirstOrDefault(r => !r.IsPending) ?? CentreRows[0];
            SelectedCentreKey = firstCentre.Key;
        }
    }

    private void EnsureResourceSelection()
    {
        if (SelectedOperation is null)
        {
            SelectedMachine = null;
            ResetMachineEditor();
            LabourEdit = NewLabour();
            CaptureMachineEditorClean();
            CaptureLabourEditorClean();
            return;
        }

        var machine = SelectedOperation.Machines.FirstOrDefault();
        if (machine is null)
        {
            SelectedMachine = null;
            ResetMachineEditor();
            CaptureMachineEditorClean();
            LabourEdit = NewLabour();
            CaptureLabourEditorClean();
            return;
        }

        SelectedMachine = machine;
        MachineEdit = CloneMachine(machine);
        LabourEdit = machine.Labours.FirstOrDefault() is { } labour
            ? CloneLabour(labour)
            : NewLabour();
        CaptureMachineEditorClean();
        CaptureLabourEditorClean();
    }

    protected async Task SelectResourceOperationAsync(PrProductDefOperationVm operation)
    {
        await RequestEditorNavigationAsync(() =>
        {
            SelectedOperation = operation;
            SelectedCentreKey = PrProductDefCentreProjection.CentreKey.From(operation);
            EnsureResourceSelection();
            return Task.CompletedTask;
        });
    }

    private void RebuildProcessBomNodes()
    {
        ProcessBomNodes = FilterProcessBomNodes(
            DisplayNodes, Model.Lines, CurrentOwnerProdCode, SelectedOperation?.OperationKey);
    }

    protected async Task OpenProcessBomAsync(PrProductDefOperationVm operation)
    {
        await SelectBomOperationAsync(operation);
        if (ReferenceEquals(SelectedOperation, operation))
        {
            ActiveTabIndex = 2;
        }
    }

    protected async Task OnBomProcessChanged(ChangeEventArgs args)
    {
        if (Guid.TryParse(args.Value?.ToString(), out var key)
            && Model.Operations.FirstOrDefault(x => x.OperationKey == key) is { } operation)
        {
            await SelectBomOperationAsync(operation);
        }
    }

    protected async Task SelectBomOperationAsync(PrProductDefOperationVm operation)
    {
        await SelectOperationAsync(operation);
        RebuildProcessBomNodes();
    }

    protected void AssignMaterialToProcess(PrProductDefLineVm line)
    {
        if (IsViewMode || IsReadOnlyStatus || SelectedOperation is null) return;
        if (Model.Lines.Any(x => x.OperationKey == SelectedOperation.OperationKey
            && string.Equals(x.ICode, line.ICode, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = "This process already contains that material. Review its quantities before assigning.";
            return;
        }
        line.OperationKey = SelectedOperation.OperationKey;
        RebuildDisplayNodes();
    }
    protected IReadOnlyList<PrProductDefOperationVm> FilteredProcesses =>
        PrProductDefCentreProjection.FilterProcesses(Model.Operations, SelectedCentreKey);

    protected IReadOnlyList<PrProcess> AvailableProcesses
    {
        get
        {
            var wc = SelectedCentreKey?.WorkCentreCode ?? OperationEdit.WorkCentreCode;
            return Processes
                .Where(x => string.Equals(x.WorkCentre, wc, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Sequence).ThenBy(x => x.ProcessCd).ToList();
        }
    }

    protected IReadOnlyList<PrMachine> AvailableMachines => Machines
        .Where(x => string.Equals(x.ProcessCd, MachineProcessCode, StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.MachineCd).ToList();
    private string MachineProcessCode => SelectedOperation?.OperationCode ?? OperationEdit.OperationCode;

    private bool IsCentreEditorDirty =>
        !string.Equals(_centreEditorSnapshot, SnapshotCentre(CentreEdit), StringComparison.Ordinal);
    private bool IsOperationEditorDirty =>
        !string.Equals(_operationEditorSnapshot, SnapshotOperation(OperationEdit), StringComparison.Ordinal);
    private bool IsMachineEditorDirty =>
        !string.Equals(_machineEditorSnapshot, SnapshotMachine(MachineEdit), StringComparison.Ordinal);
    private bool IsLabourEditorDirty =>
        !string.Equals(_labourEditorSnapshot, SnapshotLabour(LabourEdit), StringComparison.Ordinal);
    private bool IsChildEditorDirty =>
        IsCentreEditorDirty || IsOperationEditorDirty || IsMachineEditorDirty || IsLabourEditorDirty;

    protected override async Task OnParametersSetAsync()
    {
        CanEdit = await AccessRights.CanAsync(MenuCodes.PlanningProductDef, PermissionCodes.Edit);
        CanAdd = await AccessRights.CanAsync(MenuCodes.PlanningProductDef, PermissionCodes.Add);
        var warehouses = await Lookups.ListActiveWarehousesAsync();
        Warehouses = warehouses.Succeeded ? warehouses.Rows : [];
        if (!warehouses.Succeeded)
        {
            ErrorMessage = warehouses.ErrorMessage ?? "Unable to load warehouses.";
        }
        await LoadRoutingLookupsAsync();

        var key = $"{Mode}|{ProdCode}|{DefinitionCode}";
        if (string.Equals(_loadedKey, key, StringComparison.OrdinalIgnoreCase) && !IsLoading)
        {
            return;
        }

        _loadedKey = key;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        ValidationErrors.Clear();
        NeedsDefinitionChoice = false;
        DefinitionChoices = [];
        ResetLineEditor();
        ResetRoutingEditors();
        SelectedNode = null;
        CurrentSelectedNodeKey = null;
        try
        {
            if (IsNewMode)
            {
                StructureRootProdCode = string.Empty;
                StructureRootDefinitionCode = PrProductDefinitionCodes.Standard;
                CurrentOwnerProdCode = string.Empty;
                CurrentOwnerDefinitionCode = PrProductDefinitionCodes.Standard;
                UpdateProdCode = string.Empty;
                Model = new PrProductDefEditVm
                {
                    Status = PrBomStatuses.Draft,
                    BaseQty = 1m,
                    Version = 1,
                    DefinitionCode = string.IsNullOrWhiteSpace(DefinitionCode)
                        ? PrProductDefinitionCodes.Standard
                        : PrProductDefinitionCodes.Normalize(DefinitionCode),
                    DefinitionName = PrProductDefinitionCodes.StandardName
                };
                _persistedNodes = [];
                PendingCentre = null;
                SelectedCentreKey = null;

                if (!string.IsNullOrWhiteSpace(ProdCode))
                {
                    var code = ProdCode.Trim().ToUpperInvariant();
                    // /new/{ProdCode} preselects the product and keeps DefinitionCode editable.
                    // Do NOT redirect to an existing default definition.
                    var resolved = await Lookups.ResolveItemAsync(code);
                    if (resolved.Succeeded && resolved.Item is not null)
                    {
                        await OnProductSelectedAsync(resolved.Item);
                        UpdateProdCode = code;
                        if (!string.IsNullOrWhiteSpace(DefinitionCode))
                        {
                            Model.DefinitionCode = PrProductDefinitionCodes.Normalize(DefinitionCode);
                            StructureRootDefinitionCode = Model.DefinitionCode;
                            CurrentOwnerDefinitionCode = Model.DefinitionCode;
                        }
                    }
                    else
                    {
                        ErrorMessage = $"Invalid product code '{code}'.";
                    }
                }

                RebuildDisplayNodes();
                RebuildCentreRows();
                CaptureClean();
                CaptureEditorClean();
                return;
            }

            if (string.IsNullOrWhiteSpace(ProdCode))
            {
                ErrorMessage = "Product code is required.";
                return;
            }

            StructureRootProdCode = ProdCode.Trim().ToUpperInvariant();
            CurrentOwnerProdCode = StructureRootProdCode;
            UpdateProdCode = StructureRootProdCode;

            var routeDefinition = string.IsNullOrWhiteSpace(DefinitionCode)
                ? null
                : PrProductDefinitionCodes.Normalize(DefinitionCode);

            if (routeDefinition is null)
            {
                var resolved = await ResolveDefinitionForRouteAsync(StructureRootProdCode);
                if (resolved.RedirectCode is string redirect)
                {
                    Navigation.NavigateTo(
                        $"/planning/product-definitions/{Mode}/{Uri.EscapeDataString(StructureRootProdCode)}/{Uri.EscapeDataString(redirect)}",
                        replace: true);
                    return;
                }

                if (resolved.Choices is { Count: > 0 })
                {
                    NeedsDefinitionChoice = true;
                    DefinitionChoices = resolved.Choices;
                    return;
                }

                ErrorMessage = resolved.Error ?? "Unable to resolve a Product Definition.";
                return;
            }

            StructureRootDefinitionCode = routeDefinition;
            CurrentOwnerDefinitionCode = routeDefinition;

            var result = await ProductDefs.GetAsync(StructureRootProdCode, StructureRootDefinitionCode);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Product definition not found.";
                return;
            }

            Model = result.Data;
            PendingCentre = null;
            SelectedCentreKey = null;
            await LoadPersistedStructureAsync(Model.Version);
            RebuildDisplayNodes();
            RebuildCentreRows();
            CaptureClean();
            CaptureEditorClean();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<(string? RedirectCode, IReadOnlyList<PrProductDefinitionLookupRow>? Choices, string? Error)> ResolveDefinitionForRouteAsync(
        string prodCode)
    {
        var list = await ProductDefs.ListDefinitionsAsync(prodCode);
        if (!list.Succeeded || list.Data is null || list.Data.Count == 0)
        {
            return (null, null, list.Message ?? $"No Product Definitions found for {prodCode}.");
        }

        if (list.Data.Count == 1)
        {
            return (list.Data[0].DefinitionCode, null, null);
        }

        var defaults = list.Data.Where(x => x.IsDefaultDefinition).ToList();
        if (defaults.Count == 1)
        {
            return (defaults[0].DefinitionCode, null, null);
        }

        return (null, list.Data, null);
    }

    protected void ChooseDefinition(string definitionCode)
    {
        if (string.IsNullOrWhiteSpace(ProdCode) || string.IsNullOrWhiteSpace(definitionCode))
        {
            return;
        }

        Navigation.NavigateTo(
            $"/planning/product-definitions/{Mode}/{Uri.EscapeDataString(ProdCode)}/{Uri.EscapeDataString(definitionCode)}");
    }

    private async Task LoadRoutingLookupsAsync()
    {
        var workCentres = await WorkCentreService.ListAsync();
        var processes = await ProcessService.ListAsync();
        var machines = await MachineService.ListAsync();
        var operators = await OperatorService.ListAsync();
        var prefixes = await WorkPrefixService.ListAsync();
        WorkCentres = workCentres.Succeeded ? workCentres.Value ?? [] : [];
        Processes = processes.Succeeded ? processes.Value ?? [] : [];
        Machines = machines.Succeeded ? machines.Value ?? [] : [];
        Operators = operators.Succeeded ? operators.Value ?? [] : [];
        WorkPrefixes = prefixes.Succeeded ? prefixes.Value ?? [] : [];
    }

    private void RebuildCentreRows()
    {
        CentreRows = PrProductDefCentreProjection.BuildCentreRows(Model.Operations, PendingCentre);
        if (SelectedCentreKey is { } key
            && CentreRows.All(r => !PrProductDefCentreProjection.KeysEqual(r.Key, key)))
        {
            SelectedCentreKey = null;
            SelectedOperation = null;
            SelectedMachine = null;
        }
    }

    private async Task LoadPersistedStructureAsync(int? version)
    {
        if (string.IsNullOrWhiteSpace(StructureRootProdCode))
        {
            _persistedNodes = [];
            return;
        }

        var tree = await ProductDefs.GetStructureTreeAsync(StructureRootProdCode, StructureRootDefinitionCode, version);
        if (!tree.Succeeded || tree.Data is null)
        {
            // New unsaved root or load race — fall back to local-only tree
            _persistedNodes = [];
            if (!IsNewMode && tree.Message is not null)
            {
                ErrorMessage ??= tree.Message;
            }

            return;
        }

        _persistedNodes = tree.Data.Nodes;
    }

    private void RebuildDisplayNodes()
    {
        DisplayNodes = MergeStructureWithCurrentOwner(
            _persistedNodes,
            StructureRootProdCode,
            StructureRootDefinitionCode,
            CurrentOwnerProdCode,
            CurrentOwnerDefinitionCode,
            Model);
        if (!string.IsNullOrWhiteSpace(CurrentSelectedNodeKey))
        {
            SelectedNode = DisplayNodes.FirstOrDefault(x =>
                string.Equals(x.Key, CurrentSelectedNodeKey, StringComparison.Ordinal));
        }
        RebuildProcessBomNodes();
    }

    protected Task OnProdCodeChanged(string? code)
    {
        Model.ProdCode = code ?? string.Empty;
        StructureRootProdCode = (code ?? string.Empty).Trim().ToUpperInvariant();
        CurrentOwnerProdCode = StructureRootProdCode;
        if (string.IsNullOrWhiteSpace(Model.DefinitionCode))
        {
            Model.DefinitionCode = PrProductDefinitionCodes.Standard;
        }
        StructureRootDefinitionCode = PrProductDefinitionCodes.Normalize(Model.DefinitionCode);
        CurrentOwnerDefinitionCode = StructureRootDefinitionCode;
        UpdateProdCode = StructureRootProdCode;
        if (string.IsNullOrWhiteSpace(code))
        {
            Model.ProdDesc = null;
            Model.StdUom = null;
            Model.BaseUom = null;
            Model.MfgType = PrMfgTypes.Buy;
        }

        RebuildDisplayNodes();
        RebuildCentreRows();
        return Task.CompletedTask;
    }

    protected async Task OnProductSelectedAsync(IvStockMasterLookupRow row)
    {
        Model.ProdCode = row.ICode;
        Model.ProdDesc = row.IDesc;
        Model.StdUom = row.StdUom;
        Model.BaseUom = row.StdUom;
        Model.IsActive = true;
        StructureRootProdCode = row.ICode.Trim().ToUpperInvariant();
        CurrentOwnerProdCode = StructureRootProdCode;
        if (string.IsNullOrWhiteSpace(Model.DefinitionCode))
        {
            Model.DefinitionCode = PrProductDefinitionCodes.Standard;
            Model.DefinitionName = PrProductDefinitionCodes.StandardName;
        }
        StructureRootDefinitionCode = PrProductDefinitionCodes.Normalize(Model.DefinitionCode);
        CurrentOwnerDefinitionCode = StructureRootDefinitionCode;
        UpdateProdCode = StructureRootProdCode;
        RebuildDisplayNodes();
        RebuildCentreRows();
        await InvokeAsync(StateHasChanged);
    }

    protected Task OnLineItemCodeChanged(string? code)
    {
        LineItem = code;
        if (string.IsNullOrWhiteSpace(code))
        {
            LineItemDesc = null;
            LineUom = null;
            LineMfgType = PrMfgTypes.Buy;
        }

        return Task.CompletedTask;
    }

    protected async Task OnLineItemSelectedAsync(IvStockMasterLookupRow row)
    {
        LineItem = row.ICode;
        LineItemDesc = row.IDesc;
        LineUom = row.StdUom;
        LineMfgType = PrMfgTypes.Buy;
        if (string.IsNullOrWhiteSpace(LineAlternateGroupCode))
        {
            LineAlternateGroupCode = row.ICode;
        }

        if (string.IsNullOrWhiteSpace(LineWarehouse)
            && !string.IsNullOrWhiteSpace(row.DefWarehouse)
            && Warehouses.Any(w => string.Equals(w.Code, row.DefWarehouse, StringComparison.OrdinalIgnoreCase)))
        {
            LineWarehouse = row.DefWarehouse;
        }
        else if (string.IsNullOrWhiteSpace(LineWarehouse) && Warehouses.Count > 0)
        {
            LineWarehouse = Warehouses[0].Code;
        }

        await RefreshComponentDefinitionOptionsAsync();
        await InvokeAsync(StateHasChanged);
    }

    protected void OnLineSupplySourceChanged(string? value)
    {
        LineSupplySource = value ?? PrMaterialSupplySources.Purchased;
        if (!string.Equals(LineSupplySource, PrMaterialSupplySources.SeparateProductDefinition, StringComparison.Ordinal))
        {
            LineComponentDefinitionCode = null;
            ComponentDefinitionOptions = [];
        }
        else
        {
            _ = RefreshComponentDefinitionOptionsAsync();
        }

        if (!string.Equals(LineSupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal))
        {
            LineProducingRouteStepKey = null;
        }

        _ = InvokeAsync(StateHasChanged);
    }

    protected async Task OnTreeFocusedRowChanged(TreeListFocusedRowChangedEventArgs args)
    {
        if (args.DataItem is not PrBomStructureNode node)
        {
            return;
        }

        await SelectNodeAsync(node);
    }

    private async Task SelectNodeAsync(PrBomStructureNode node)
    {
        CurrentSelectedNodeKey = node.Key;
        SelectedNode = node;

        // Selecting a line owned by another product definition → switch edit context
        if (node.ParentKey is not null
            && !string.IsNullOrWhiteSpace(node.OwnerProdCode)
            && !OwnersEqual(
                node.OwnerProdCode,
                node.OwnerDefinitionCode ?? CurrentOwnerDefinitionCode,
                CurrentOwnerProdCode,
                CurrentOwnerDefinitionCode))
        {
            await RequestOwnerSwitchAsync(
                node.OwnerProdCode,
                node.OwnerDefinitionCode ?? CurrentOwnerDefinitionCode,
                addAfter: false,
                selectKey: node.Key);
            return;
        }

        if (node.ParentKey is not null
            && OwnersEqual(
                node.OwnerProdCode,
                node.OwnerDefinitionCode ?? CurrentOwnerDefinitionCode,
                CurrentOwnerProdCode,
                CurrentOwnerDefinitionCode))
        {
            var line = FindLine(node);
            if (line is not null)
            {
                BeginEditLine(line);
                return;
            }
        }

        ResetLineEditor();
    }

    protected async Task BeginAddUnderSelectionAsync()
    {
        ErrorMessage = null;
        if (!CanAddUnderSelection)
        {
            ErrorMessage = "Select the root, or a Make/Phantom item, to add a component.";
            return;
        }

        var (targetOwner, targetDefinition) = ResolveAddOwner();
        if (string.IsNullOrWhiteSpace(targetOwner))
        {
            ErrorMessage = "Product code is required before adding components.";
            return;
        }

        if (!OwnersEqual(targetOwner, targetDefinition, CurrentOwnerProdCode, CurrentOwnerDefinitionCode))
        {
            await RequestOwnerSwitchAsync(targetOwner, targetDefinition, addAfter: true, selectKey: SelectedNode?.Key);
            return;
        }

        ResetLineEditor();
    }

    private (string? ProdCode, string DefinitionCode) ResolveAddOwner()
    {
        if (SelectedNode is null || SelectedNode.ParentKey is null)
        {
            var prod = string.IsNullOrWhiteSpace(CurrentOwnerProdCode)
                ? StructureRootProdCode
                : CurrentOwnerProdCode;
            return (prod, CurrentOwnerDefinitionCode);
        }

        // Drill into SEPARATE child definition when present; otherwise keep current owner definition.
        var childDef = string.IsNullOrWhiteSpace(SelectedNode.ComponentDefinitionCode)
            ? CurrentOwnerDefinitionCode
            : PrProductDefinitionCodes.Normalize(SelectedNode.ComponentDefinitionCode);
        return (SelectedNode.ItemCode, childDef);
    }

    private async Task RequestOwnerSwitchAsync(
        string ownerProdCode,
        string ownerDefinitionCode,
        bool addAfter,
        string? selectKey)
    {
        if (IsDirty)
        {
            _pendingOwnerProdCode = ownerProdCode;
            _pendingOwnerDefinitionCode = ownerDefinitionCode;
            _pendingAddAfterSwitch = addAfter;
            CurrentSelectedNodeKey = selectKey;
            ConfirmDiscardVisible = true;
            return;
        }

        await SwitchOwnerAsync(ownerProdCode, ownerDefinitionCode, addAfter, selectKey);
    }

    private async Task SwitchOwnerAsync(
        string ownerProdCode,
        string ownerDefinitionCode,
        bool addAfter,
        string? selectKey)
    {
        ErrorMessage = null;
        var code = ownerProdCode.Trim().ToUpperInvariant();
        var definition = string.IsNullOrWhiteSpace(ownerDefinitionCode)
            ? PrProductDefinitionCodes.Standard
            : PrProductDefinitionCodes.Normalize(ownerDefinitionCode);

        // MissingBom / never saved: create new draft context in-memory
        var get = await ProductDefs.GetAsync(code, definition);
        if (!get.Succeeded || get.Data is null)
        {
            if (get.ErrorCode == IvMasterErrorCode.NotFound)
            {
                Model = new PrProductDefEditVm
                {
                    ProdCode = code,
                    DefinitionCode = definition,
                    DefinitionName = definition == PrProductDefinitionCodes.Standard
                        ? PrProductDefinitionCodes.StandardName
                        : definition,
                    Status = PrBomStatuses.Draft,
                    BaseQty = 1m,
                    Version = 1,
                    MfgType = SelectedNode is not null
                        ? PrMfgTypes.Normalize(SelectedNode.MfgType)
                        : PrMfgTypes.Make
                };
                if (SelectedNode is not null
                    && string.Equals(SelectedNode.ItemCode, code, StringComparison.OrdinalIgnoreCase))
                {
                    Model.ProdDesc = SelectedNode.ItemDesc;
                    Model.StdUom = SelectedNode.StdUom;
                    Model.BaseUom = SelectedNode.StdUom;
                }

                CurrentOwnerProdCode = code;
                CurrentOwnerDefinitionCode = definition;
                ResetRoutingEditors();
                CaptureClean();
                RebuildDisplayNodes();
                if (addAfter)
                {
                    ResetLineEditor();
                }

                CurrentSelectedNodeKey = selectKey;
                SelectedNode = DisplayNodes.FirstOrDefault(x =>
                    string.Equals(x.Key, selectKey, StringComparison.Ordinal));
                return;
            }

            ErrorMessage = get.Message ?? "Unable to load product definition.";
            return;
        }

        Model = get.Data;
        CurrentOwnerProdCode = code;
        CurrentOwnerDefinitionCode = string.IsNullOrWhiteSpace(Model.DefinitionCode)
            ? definition
            : PrProductDefinitionCodes.Normalize(Model.DefinitionCode);
        ResetRoutingEditors();
        CaptureClean();
        RebuildDisplayNodes();
        CurrentSelectedNodeKey = selectKey;
        SelectedNode = DisplayNodes.FirstOrDefault(x =>
            string.Equals(x.Key, selectKey, StringComparison.Ordinal));

        if (addAfter)
        {
            ResetLineEditor();
        }
        else if (SelectedNode is not null
                 && OwnersEqual(
                     SelectedNode.OwnerProdCode,
                     SelectedNode.OwnerDefinitionCode ?? CurrentOwnerDefinitionCode,
                     CurrentOwnerProdCode,
                     CurrentOwnerDefinitionCode))
        {
            var line = FindLine(SelectedNode);
            if (line is not null)
            {
                BeginEditLine(line);
            }
        }
    }

    protected async Task ReturnToStructureRootAsync()
    {
        if (IsEditingRoot)
        {
            return;
        }

        await RequestOwnerSwitchAsync(
            StructureRootProdCode,
            StructureRootDefinitionCode,
            addAfter: false,
            selectKey: PrBomStructureKeys.Root(StructureRootProdCode, StructureRootDefinitionCode));
    }

    private static bool OwnersEqual(
        string? leftProd,
        string? leftDefinition,
        string? rightProd,
        string? rightDefinition) =>
        string.Equals(
            PrBomStructureKeys.Normalize(leftProd),
            PrBomStructureKeys.Normalize(rightProd),
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            PrBomStructureKeys.Normalize(leftDefinition),
            PrBomStructureKeys.Normalize(rightDefinition),
            StringComparison.OrdinalIgnoreCase);

    private void BeginEditLine(PrProductDefLineVm line)
    {
        LineEditUid = line.Uid > 0 ? line.Uid : null;
        LineEditTempId = line.TempId;
        LineItem = line.ICode;
        LineItemDesc = line.IName;
        LineUom = line.StdUom;
        LineMfgType = line.MfgType;
        LineStdQty = line.StdQty;
        LineScrapPercent = line.ScrapPercent;
        LineWarehouse = line.Warehouse;
        LineBomDefault = line.BomDefault;
        LineAlternateGroupCode = line.AlternateGroupCode ?? line.ICode;
        LineTolerance = line.Tolerance;
        LineIssueMethod = string.IsNullOrWhiteSpace(line.IssueMethod)
            ? PrMaterialIssueMethods.Manual
            : line.IssueMethod;
        LineSupplySource = string.IsNullOrWhiteSpace(line.SupplySource)
            ? PrMaterialSupplySources.Purchased
            : line.SupplySource;
        LineComponentDefinitionCode = line.ComponentDefinitionCode;
        LineProducingRouteStepKey = line.ProducingRouteStepKey;
        _ = RefreshComponentDefinitionOptionsAsync();
    }

    private PrProductDefLineVm? FindLine(PrBomStructureNode node)
    {
        if (node.SourceLineUid is > 0)
        {
            return Model.Lines.FirstOrDefault(x => x.Uid == node.SourceLineUid);
        }

        if (!string.IsNullOrWhiteSpace(node.LineTempId))
        {
            return Model.Lines.FirstOrDefault(x =>
                string.Equals(x.TempId, node.LineTempId, StringComparison.Ordinal));
        }

        return null;
    }

    protected Task RemoveSelectedLineAsync()
    {
        ErrorMessage = null;
        if (!CanDeleteSelection || SelectedNode is null)
        {
            ErrorMessage = "Select a component belonging to the BOM you are editing.";
            return Task.CompletedTask;
        }

        var line = FindLine(SelectedNode);
        if (line is null)
        {
            ErrorMessage = "The selected material is no longer available. Select it again.";
            return Task.CompletedTask;
        }
        else if (line.Uid > 0)
        {
            Model.Lines.RemoveAll(x => x.Uid == line.Uid);
        }
        else
        {
            Model.Lines.RemoveAll(x =>
                string.Equals(x.TempId, line.TempId, StringComparison.Ordinal));
        }

        ResetLineEditor();
        CurrentSelectedNodeKey = null;
        SelectedNode = null;
        RebuildDisplayNodes();
        return Task.CompletedTask;
    }

    protected Task AddOrUpdateLineAsync()
    {
        ErrorMessage = null;
        if (SelectedOperation is null || !Model.Operations.Contains(SelectedOperation))
        {
            ErrorMessage = "Select the process that consumes these materials first.";
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(CurrentOwnerProdCode) && string.IsNullOrWhiteSpace(Model.ProdCode))
        {
            ErrorMessage = "Product code is required.";
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(LineItem))
        {
            ErrorMessage = "Component item is required.";
            return Task.CompletedTask;
        }

        if (LineStdQty <= 0m)
        {
            ErrorMessage = "Standard quantity must be greater than zero.";
            return Task.CompletedTask;
        }

        if (LineScrapPercent < 0m)
        {
            ErrorMessage = "Scrap percent cannot be negative.";
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(LineWarehouse))
        {
            ErrorMessage = "Warehouse is required.";
            return Task.CompletedTask;
        }

        if (string.Equals(LineItem, Model.ProdCode, StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "Component cannot be the same as the finished product.";
            return Task.CompletedTask;
        }

        var editing = LineEditUid is > 0 || !string.IsNullOrWhiteSpace(LineEditTempId);
        var existing = Model.Lines.FirstOrDefault(x =>
            x.OperationKey == SelectedOperation.OperationKey
            && string.Equals(x.ICode, LineItem, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !editing)
        {
            ErrorMessage = $"Component {LineItem} is already on the BOM.";
            return Task.CompletedTask;
        }

        if (editing)
        {
            var target = FindEditingLine();
            if (target is null || target.OperationKey != SelectedOperation.OperationKey)
            {
                ErrorMessage = "Select a material belonging to the selected process.";
                return Task.CompletedTask;
            }
            if (target is not null
                && !string.Equals(target.ICode, LineItem, StringComparison.OrdinalIgnoreCase)
                && existing is not null)
            {
                ErrorMessage = $"Component {LineItem} is already on the BOM.";
                return Task.CompletedTask;
            }
        }

        var seq = Model.Lines.Count == 0
            ? 1
            : Model.Lines.Max(x => x.SeqNo) + 1;

        if (editing)
        {
            var target = FindEditingLine();
            if (target is not null)
            {
                target.ICode = LineItem!;
                target.OperationKey = SelectedOperation.OperationKey;
                target.IName = LineItemDesc;
                target.StdQty = LineStdQty;
                target.StdUom = LineUom;
                target.ScrapPercent = LineScrapPercent;
                target.Warehouse = LineWarehouse;
                target.BomDefault = LineBomDefault;
                target.AlternateGroupCode = string.IsNullOrWhiteSpace(LineAlternateGroupCode)
                    ? LineItem
                    : LineAlternateGroupCode.Trim().ToUpperInvariant();
                target.Tolerance = LineTolerance;
                target.MfgType = LineMfgType;
                target.IssueMethod = LineIssueMethod;
                target.SupplySource = LineSupplySource;
                target.ComponentDefinitionCode =
                    string.Equals(LineSupplySource, PrMaterialSupplySources.SeparateProductDefinition, StringComparison.Ordinal)
                        ? (string.IsNullOrWhiteSpace(LineComponentDefinitionCode)
                            ? null
                            : PrProductDefinitionCodes.Normalize(LineComponentDefinitionCode))
                        : null;
                target.ProducingRouteStepKey =
                    string.Equals(LineSupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal)
                        ? LineProducingRouteStepKey
                        : null;
                if (target.SeqNo <= 0)
                {
                    target.SeqNo = seq;
                }
            }
        }
        else
        {
            _tempSeq++;
            Model.Lines.Add(new PrProductDefLineVm
            {
                TempId = $"T{_tempSeq}",
                OperationKey = SelectedOperation.OperationKey,
                ICode = LineItem!,
                IName = LineItemDesc,
                StdQty = LineStdQty,
                StdUom = LineUom,
                ScrapPercent = LineScrapPercent,
                SeqNo = seq,
                Warehouse = LineWarehouse,
                BomDefault = LineBomDefault,
                AlternateGroupCode = string.IsNullOrWhiteSpace(LineAlternateGroupCode)
                    ? LineItem
                    : LineAlternateGroupCode.Trim().ToUpperInvariant(),
                Tolerance = LineTolerance,
                MfgType = LineMfgType,
                IssueMethod = LineIssueMethod,
                SupplySource = LineSupplySource,
                ComponentDefinitionCode =
                    string.Equals(LineSupplySource, PrMaterialSupplySources.SeparateProductDefinition, StringComparison.Ordinal)
                        ? (string.IsNullOrWhiteSpace(LineComponentDefinitionCode)
                            ? null
                            : PrProductDefinitionCodes.Normalize(LineComponentDefinitionCode))
                        : null,
                ProducingRouteStepKey =
                    string.Equals(LineSupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal)
                        ? LineProducingRouteStepKey
                        : null
            });
        }

        ResetLineEditor();
        RebuildDisplayNodes();
        return Task.CompletedTask;
    }

    private PrProductDefLineVm? FindEditingLine()
    {
        if (LineEditUid is > 0)
        {
            return Model.Lines.FirstOrDefault(x => x.Uid == LineEditUid);
        }

        if (!string.IsNullOrWhiteSpace(LineEditTempId))
        {
            return Model.Lines.FirstOrDefault(x =>
                string.Equals(x.TempId, LineEditTempId, StringComparison.Ordinal));
        }

        return null;
    }

    protected async Task SaveAsync(bool activate)
    {
        if (IsReadOnlyStatus)
        {
            ErrorMessage = $"BOM version {Model.Version} is {Model.Status} and cannot be edited.";
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        ValidationErrors.Clear();
        try
        {
            var isNewOwner = Model.BomHdrId == 0;
            var result = await ProductDefs.SaveAsync(Model, isNewOwner, activate);
            if (!result.Succeeded)
            {
                if (result.ErrorCode == IvMasterErrorCode.Concurrency)
                {
                    ConcurrencyVisible = true;
                    return;
                }

                ValidationErrors = result.ValidationErrors?.ToDictionary(
                    x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ErrorMessage = result.Message ?? "Save failed.";
                return;
            }

            StatusMessage = activate ? "Product definition saved and activated." : "Product definition saved.";
            if (result.Data is not null)
            {
                Model = result.Data;
                CurrentOwnerProdCode = Model.ProdCode;
                CurrentOwnerDefinitionCode = string.IsNullOrWhiteSpace(Model.DefinitionCode)
                    ? PrProductDefinitionCodes.Standard
                    : PrProductDefinitionCodes.Normalize(Model.DefinitionCode);
                if (IsNewMode && string.IsNullOrWhiteSpace(StructureRootProdCode))
                {
                    StructureRootProdCode = Model.ProdCode;
                    StructureRootDefinitionCode = CurrentOwnerDefinitionCode;
                }

                CaptureClean();
            }

            // Keep structure rooted at route product + definition
            if (IsNewMode)
            {
                Navigation.NavigateTo(CanonicalEntryUrl("edit", StructureRootProdCode, StructureRootDefinitionCode));
                return;
            }

            await LoadPersistedStructureAsync(null);
            RebuildDisplayNodes();

            if (activate && IsEditingRoot)
            {
                Navigation.NavigateTo(CanonicalEntryUrl("view", StructureRootProdCode, StructureRootDefinitionCode));
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task CreateNewVersionAsync()
    {
        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await ProductDefs.CreateNewVersionAsync(Model.ProdCode, CurrentOwnerDefinitionCode, Model.Version);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Unable to create new version.";
                return;
            }

            StatusMessage = $"Created draft version {result.Data.Version}.";
            Navigation.NavigateTo(CanonicalEntryUrl("edit", StructureRootProdCode, StructureRootDefinitionCode));
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected Task GoEditAsync()
    {
        Navigation.NavigateTo(CanonicalEntryUrl("edit", StructureRootProdCode, StructureRootDefinitionCode));
        return Task.CompletedTask;
    }

    protected Task GoExplodeAsync()
    {
        Navigation.NavigateTo(
            $"/planning/product-definitions/explode/{Uri.EscapeDataString(StructureRootProdCode)}/{Uri.EscapeDataString(StructureRootDefinitionCode)}");
        return Task.CompletedTask;
    }

    private static string CanonicalEntryUrl(string mode, string prodCode, string definitionCode) =>
        $"/planning/product-definitions/{mode}/{Uri.EscapeDataString(prodCode)}/{Uri.EscapeDataString(definitionCode)}";

    protected Task CancelAsync()
    {
        if (IsDirty)
        {
            _pendingOwnerProdCode = null;
            _pendingOwnerDefinitionCode = null;
            _pendingAddAfterSwitch = false;
            ConfirmDiscardVisible = true;
            return Task.CompletedTask;
        }

        Navigation.NavigateTo("/planning/product-definitions");
        return Task.CompletedTask;
    }

    protected async Task ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        if (_pendingSelectionAction is not null)
        {
            var action = _pendingSelectionAction;
            _pendingSelectionAction = null;
            CaptureEditorClean();
            await action();
            return;
        }

        if (_pendingUpdateAfterDiscard)
        {
            _pendingUpdateAfterDiscard = false;
            var code = UpdateProdCode.Trim().ToUpperInvariant();
            CaptureClean();
            CaptureEditorClean();
            await ApplyUpdateAsync(code);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_pendingOwnerProdCode))
        {
            var owner = _pendingOwnerProdCode;
            var ownerDef = _pendingOwnerDefinitionCode ?? CurrentOwnerDefinitionCode;
            var add = _pendingAddAfterSwitch;
            var selectKey = CurrentSelectedNodeKey;
            _pendingOwnerProdCode = null;
            _pendingOwnerDefinitionCode = null;
            _pendingAddAfterSwitch = false;
            await SwitchOwnerAsync(owner, ownerDef, add, selectKey);
            return;
        }

        Navigation.NavigateTo("/planning/product-definitions");
    }

    protected Task CancelDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        _pendingOwnerProdCode = null;
        _pendingOwnerDefinitionCode = null;
        _pendingAddAfterSwitch = false;
        _pendingUpdateAfterDiscard = false;
        _pendingSelectionAction = null;
        return Task.CompletedTask;
    }

    protected async Task ReloadAfterConcurrencyAsync()
    {
        ConcurrencyVisible = false;
        await LoadAsync();
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private async Task RefreshComponentDefinitionOptionsAsync()
    {
        ComponentDefinitionOptions = [];
        if (!string.Equals(LineSupplySource, PrMaterialSupplySources.SeparateProductDefinition, StringComparison.Ordinal))
        {
            LineComponentDefinitionCode = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(LineItem))
        {
            return;
        }

        var result = await ProductDefs.ListDefinitionsAsync(LineItem);
        if (!result.Succeeded || result.Data is null)
        {
            return;
        }

        ComponentDefinitionOptions = result.Data
            .Select(x => new DefinitionOption(
                x.DefinitionCode,
                string.IsNullOrWhiteSpace(x.DefinitionName)
                    ? x.DefinitionCode
                    : $"{x.DefinitionCode} — {x.DefinitionName}"))
            .ToList();

        if (!string.IsNullOrWhiteSpace(LineComponentDefinitionCode)
            && ComponentDefinitionOptions.All(x =>
                !string.Equals(x.Value, LineComponentDefinitionCode, StringComparison.OrdinalIgnoreCase)))
        {
            LineComponentDefinitionCode = null;
        }
    }

    protected static string StatusBadge(PrBomStructureNodeStatus status) => status switch
    {
        PrBomStructureNodeStatus.MissingBom => "Missing BOM",
        PrBomStructureNodeStatus.Circular => "Circular",
        PrBomStructureNodeStatus.MaxDepth => "Max depth",
        _ => string.Empty
    };

    protected void ResetLineEditor()
    {
        LineEditUid = null;
        LineEditTempId = null;
        LineItem = null;
        LineItemDesc = null;
        LineUom = null;
        LineMfgType = PrMfgTypes.Buy;
        LineStdQty = 1m;
        LineScrapPercent = 0m;
        LineWarehouse = null;
        LineBomDefault = true;
        LineAlternateGroupCode = null;
        LineTolerance = 0m;
        LineIssueMethod = PrMaterialIssueMethods.Manual;
        LineSupplySource = PrMaterialSupplySources.Purchased;
        LineComponentDefinitionCode = null;
        ComponentDefinitionOptions = [];
        LineProducingRouteStepKey = null;
    }

    protected async Task UpdateProductAsync()
    {
        ErrorMessage = null;
        var code = (IsNewMode && IsEditingRoot ? Model.ProdCode : UpdateProdCode)?.Trim().ToUpperInvariant() ?? string.Empty;
        if (code.Length == 0)
        {
            ErrorMessage = "Product Def Code is required.";
            return;
        }

        if (IsDirty || IsChildEditorDirty)
        {
            _pendingUpdateAfterDiscard = true;
            UpdateProdCode = code;
            ConfirmDiscardVisible = true;
            return;
        }

        await ApplyUpdateAsync(code);
    }

    private async Task ApplyUpdateAsync(string code)
    {
        ErrorMessage = null;
        var get = await ProductDefs.GetAsync(code, CurrentOwnerDefinitionCode);
        if (get.Succeeded && get.Data is not null)
        {
            if (!string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(StructureRootProdCode, code, StringComparison.OrdinalIgnoreCase))
            {
                Navigation.NavigateTo($"/planning/product-definitions/edit/{Uri.EscapeDataString(code)}");
                return;
            }

            if (string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(StructureRootProdCode, code, StringComparison.OrdinalIgnoreCase))
            {
                Navigation.NavigateTo($"/planning/product-definitions/view/{Uri.EscapeDataString(code)}");
                return;
            }

            Model = get.Data;
            StructureRootProdCode = code;
            CurrentOwnerProdCode = code;
            UpdateProdCode = code;
            PendingCentre = null;
            SelectedCentreKey = null;
            await LoadPersistedStructureAsync(Model.Version);
            RebuildDisplayNodes();
            RebuildCentreRows();
            ResetRoutingEditors();
            CaptureClean();
            CaptureEditorClean();
            StatusMessage = $"Loaded product definition {code}.";
            return;
        }

        if (get.ErrorCode == IvMasterErrorCode.NotFound)
        {
            var resolved = await Lookups.ResolveItemAsync(code);
            if (!resolved.Succeeded || resolved.Item is null)
            {
                ErrorMessage = $"Invalid product code '{code}'.";
                return;
            }

            if (IsNewMode)
            {
                await OnProductSelectedAsync(resolved.Item);
                UpdateProdCode = code;
                StatusMessage = $"Initialized new definition for {code}.";
                return;
            }

            Navigation.NavigateTo($"/planning/product-definitions/new/{Uri.EscapeDataString(code)}");
            return;
        }

        ErrorMessage = get.Message ?? "Unable to load product definition.";
    }

    protected Task OnCentreOutputCodeChanged(string? code)
    {
        CentreEdit.OutputItemCode = code ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code))
        {
            CentreEdit.OutputItemDescription = null;
            CentreEdit.Class = null;
            CentreEdit.OutputUom = null;
        }

        return Task.CompletedTask;
    }

    protected Task OnCentreOutputSelectedAsync(IvStockMasterLookupRow row)
    {
        CentreEdit.OutputItemCode = row.ICode;
        CentreEdit.OutputItemDescription = row.IDesc;
        CentreEdit.Class = row.IClassCode;
        CentreEdit.OutputUom = row.StdUom;
        return Task.CompletedTask;
    }

    protected void BeginAddCentre()
    {
        _originalCentreKey = null;
        CentreEdit = NewCentre();
        CentreEdit.CentralSequence = CentreRows.Count == 0
            ? 10
            : CentreRows.Where(x => !x.IsPending).Select(x => x.CentralSequence).DefaultIfEmpty(0).Max() + 10;
        CentreEdit.OutputItemCode = Model.ProdCode;
        CentreEdit.OutputUom = Model.BaseUom ?? Model.StdUom;
        CentreEdit.OutputBaseQty = Model.BaseQty > 0 ? Model.BaseQty : 1m;
        CaptureCentreEditorClean();
    }

    protected void EditCentre(PrProductDefCentreProjection.CentreRowVm centre)
    {
        _originalCentreKey = centre.Key;
        CentreEdit = CloneCentre(centre);
        CaptureCentreEditorClean();
    }

    protected Task SaveCentreAsync()
    {
        ErrorMessage = null;
        var newKey = PrProductDefCentreProjection.CentreKey.From(CentreEdit.WorkCentreCode, CentreEdit.OutputItemCode);
        if (newKey.IsEmpty)
        {
            ErrorMessage = "Work centre and center product are required.";
            return Task.CompletedTask;
        }

        if (_originalCentreKey is null)
        {
            // Pending centre (Add)
            if (CentreRows.Any(r => !r.IsPending && PrProductDefCentreProjection.KeysEqual(r.Key, newKey)))
            {
                ErrorMessage = $"Centre {newKey.WorkCentreCode} / {newKey.OutputItemCode} already exists.";
                return Task.CompletedTask;
            }

            if (CentreEdit.CentralSequence <= 0 || CentreEdit.OutputBaseQty <= 0m)
            {
                ErrorMessage = "Centre sequence and pack size must be greater than zero.";
                return Task.CompletedTask;
            }

            PendingCentre = CloneCentre(CentreEdit);
            PendingCentre.IsPending = true;
            SelectedCentreKey = newKey;
            SelectedOperation = null;
            SelectedMachine = null;
            RebuildCentreRows();
            BeginAddOperation();
            ActiveTabIndex = 1; // PROCESS
            CaptureCentreEditorClean();
            StatusMessage = "Centre staged. Add a process to persist it.";
            return Task.CompletedTask;
        }

        var result = PrProductDefCentreProjection.TryUpdateCentre(
            Model.Operations, _originalCentreKey.Value, CentreEdit);
        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
            return Task.CompletedTask;
        }

        SelectedCentreKey = result.NewKey;
        if (SelectedOperation is not null
            && !PrProductDefCentreProjection.OperationBelongsToCentre(SelectedOperation, SelectedCentreKey))
        {
            SelectedOperation = null;
            SelectedMachine = null;
        }

        RebuildCentreRows();
        _originalCentreKey = result.NewKey;
        CaptureCentreEditorClean();
        return Task.CompletedTask;
    }

    protected async Task DeleteCentreAsync(PrProductDefCentreProjection.CentreRowVm centre)
    {
        ErrorMessage = null;
        var keys = PrProductDefCentreProjection.FilterProcesses(Model.Operations, centre.Key)
            .Select(x => x.OperationKey).ToHashSet();
        if (Model.Lines.Any(x => x.OperationKey is { } key && keys.Contains(key)))
        {
            ErrorMessage = "Remove the centre's process materials before deleting this centre.";
            return;
        }
        await RequestEditorNavigationAsync(async () =>
        {
            if (centre.IsPending)
            {
                PendingCentre = null;
            }
            else
            {
                PrProductDefCentreProjection.DeleteCentre(Model.Operations, centre.Key);
            }

            if (SelectedCentreKey.HasValue
                && PrProductDefCentreProjection.KeysEqual(SelectedCentreKey.Value, centre.Key))
            {
                SelectedCentreKey = null;
                SelectedOperation = null;
                SelectedMachine = null;
            }

            RebuildCentreRows();
            ResetRoutingEditors();
        });
    }

    protected async Task SelectCentreAsync(PrProductDefCentreProjection.CentreRowVm centre)
    {
        await RequestEditorNavigationAsync(() =>
        {
            SelectedCentreKey = centre.Key;
            SelectedOperation = null;
            SelectedMachine = null;
            ResetMachineEditor();
            LabourEdit = NewLabour();
            CaptureOperationEditorClean();
            CaptureMachineEditorClean();
            CaptureLabourEditorClean();
            if (!centre.IsPending)
            {
                EditCentre(centre);
            }

            return Task.CompletedTask;
        });
    }

    protected Task OnRouteOutputCodeChanged(string? code)
    {
        OperationEdit.OutputItemCode = code ?? string.Empty;
        return Task.CompletedTask;
    }

    protected Task OnRouteOutputSelectedAsync(IvStockMasterLookupRow row)
    {
        OperationEdit.OutputItemCode = row.ICode;
        OperationEdit.OutputUom = row.StdUom;
        return Task.CompletedTask;
    }

    protected void BeginAddOperation()
    {
        if (!SelectedCentreKey.HasValue && PendingCentre is null)
        {
            ErrorMessage = "Select or add a work centre before adding a process.";
            return;
        }

        var centreRow = CentreRows.FirstOrDefault(r =>
            SelectedCentreKey.HasValue && PrProductDefCentreProjection.KeysEqual(r.Key, SelectedCentreKey.Value))
            ?? PendingCentre
            ?? CentreEdit;

        OperationEdit = NewOperation();
        OperationEdit.WorkCentreCode = centreRow.WorkCentreCode;
        OperationEdit.OutputItemCode = centreRow.OutputItemCode;
        OperationEdit.OutputUom = centreRow.OutputUom ?? Model.BaseUom ?? Model.StdUom;
        OperationEdit.OutputBaseQty = centreRow.OutputBaseQty > 0 ? centreRow.OutputBaseQty : 1m;
        OperationEdit.CentralSequence = centreRow.CentralSequence > 0 ? centreRow.CentralSequence : 10;
        OperationEdit.ProcessSequence = FilteredProcesses.Count == 0
            ? 10
            : FilteredProcesses.Max(x => x.ProcessSequence) + 10;
        CaptureOperationEditorClean();
    }

    protected async Task EditOperationAsync(PrProductDefOperationVm operation)
    {
        await RequestEditorNavigationAsync(() =>
        {
            SelectedOperation = operation;
            ResetLineEditor();
            SelectedNode = null;
            CurrentSelectedNodeKey = null;
            SelectedMachine = null;
            OperationEdit = CloneOperation(operation);
            CaptureOperationEditorClean();
            ResetMachineEditor();
            LabourEdit = NewLabour();
            CaptureMachineEditorClean();
            CaptureLabourEditorClean();
            return Task.CompletedTask;
        });
    }

    protected Task SaveOperationAsync()
    {
        if (!SelectedCentreKey.HasValue && PendingCentre is null)
        {
            ErrorMessage = "Select a work centre before saving a process.";
            return Task.CompletedTask;
        }

        var centreRow = CentreRows.FirstOrDefault(r =>
                            SelectedCentreKey.HasValue
                            && PrProductDefCentreProjection.KeysEqual(r.Key, SelectedCentreKey.Value))
                        ?? PendingCentre;
        if (centreRow is not null)
        {
            OperationEdit.WorkCentreCode = centreRow.WorkCentreCode;
            OperationEdit.OutputItemCode = centreRow.OutputItemCode;
            OperationEdit.CentralSequence = centreRow.CentralSequence;
            OperationEdit.OutputBaseQty = centreRow.OutputBaseQty;
            if (!string.IsNullOrWhiteSpace(centreRow.OutputUom))
            {
                OperationEdit.OutputUom = centreRow.OutputUom;
            }
        }

        if (string.IsNullOrWhiteSpace(OperationEdit.WorkCentreCode)
            || string.IsNullOrWhiteSpace(OperationEdit.OutputItemCode)
            || string.IsNullOrWhiteSpace(OperationEdit.OperationCode))
        {
            ErrorMessage = "Work centre, output item and process are required for a route operation.";
            return Task.CompletedTask;
        }

        if (OperationEdit.CentralSequence <= 0 || OperationEdit.ProcessSequence <= 0
            || OperationEdit.OutputBaseQty <= 0m)
        {
            ErrorMessage = "Route sequences and output base quantity must be greater than zero.";
            return Task.CompletedTask;
        }

        var centreKey = PrProductDefCentreProjection.CentreKey.From(
            OperationEdit.WorkCentreCode, OperationEdit.OutputItemCode);
        var duplicate = Model.Operations.Any(x =>
            PrProductDefCentreProjection.KeysEqual(PrProductDefCentreProjection.CentreKey.From(x), centreKey)
            && string.Equals(x.OperationCode, OperationEdit.OperationCode, StringComparison.OrdinalIgnoreCase)
            && (OperationEdit.Uid <= 0
                ? string.IsNullOrWhiteSpace(OperationEdit.TempId)
                    || !string.Equals(x.TempId, OperationEdit.TempId, StringComparison.Ordinal)
                : x.Uid != OperationEdit.Uid));
        if (duplicate)
        {
            ErrorMessage = $"Process {OperationEdit.OperationCode} already exists on this centre.";
            return Task.CompletedTask;
        }

        var master = Processes.FirstOrDefault(x =>
            string.Equals(x.WorkCentre, OperationEdit.WorkCentreCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.ProcessCd, OperationEdit.OperationCode, StringComparison.OrdinalIgnoreCase));
        OperationEdit.WorkCentreDescription = WorkCentres.FirstOrDefault(x =>
            string.Equals(x.WrkCtrCd, OperationEdit.WorkCentreCode, StringComparison.OrdinalIgnoreCase))?.WrkCtrDes;
        OperationEdit.OperationDescription = master?.ProcessDes;

        var index = FindOperationIndex(OperationEdit);
        var saved = CloneOperation(OperationEdit);
        saved.TempId ??= Guid.NewGuid().ToString("N");
        if (index >= 0)
            Model.Operations[index] = saved;
        else
            Model.Operations.Add(saved);

        // Materialize pending centre
        if (PendingCentre is not null
            && PrProductDefCentreProjection.KeysEqual(PendingCentre.Key, centreKey))
        {
            PendingCentre = null;
        }

        Model.Operations = Model.Operations
            .OrderBy(x => x.CentralSequence).ThenBy(x => x.ProcessSequence).ThenBy(x => x.OperationCode)
            .ToList();
        SelectedCentreKey = centreKey;
        SelectedOperation = saved;
        OperationEdit = NewOperation();
        BeginAddOperation();
        RebuildCentreRows();
        RebuildProcessBomNodes();
        ResetMachineEditor();
        ErrorMessage = null;
        return Task.CompletedTask;
    }

    protected async Task SelectOperationAsync(PrProductDefOperationVm operation)
    {
        await RequestEditorNavigationAsync(() =>
        {
            SelectedOperation = operation;
            ResetLineEditor();
            SelectedNode = null;
            CurrentSelectedNodeKey = null;
            SelectedCentreKey = PrProductDefCentreProjection.CentreKey.From(operation);
            SelectedMachine = null;
            ResetMachineEditor();
            LabourEdit = NewLabour();
            CaptureMachineEditorClean();
            CaptureLabourEditorClean();
            return Task.CompletedTask;
        });
    }

    protected async Task RemoveOperationAsync(PrProductDefOperationVm operation)
    {
        if (Model.Lines.Any(x => x.OperationKey == operation.OperationKey))
        {
            ErrorMessage = "Remove this process's materials before deleting the process.";
            return;
        }
        await RequestEditorNavigationAsync(() =>
        {
            Model.Operations.Remove(operation);
            if (ReferenceEquals(SelectedOperation, operation))
            {
                SelectedOperation = null;
                SelectedMachine = null;
            }

            OperationEdit = NewOperation();
            ResetMachineEditor();
            RebuildCentreRows();
            RebuildProcessBomNodes();
            return Task.CompletedTask;
        });
    }

    protected void BeginAddMachine()
    {
        if (SelectedOperation is null)
        {
            ErrorMessage = "Select a route operation before adding a machine.";
            return;
        }
        if (SelectedOperation.Machines.Count > 0)
        {
            SelectedMachine = SelectedOperation.Machines[0];
            MachineEdit = CloneMachine(SelectedMachine);
            LabourEdit = SelectedMachine.Labours.FirstOrDefault() is { } labour
                ? CloneLabour(labour)
                : NewLabour();
            CaptureMachineEditorClean();
            CaptureLabourEditorClean();
            return;
        }
        ResetMachineEditor();
        MachineEdit.ResourceSequence = 10;
        CaptureMachineEditorClean();
    }

    protected async Task EditMachineAsync(PrProductDefMachineVm machine)
    {
        await RequestEditorNavigationAsync(() =>
        {
            SelectedMachine = machine;
            MachineEdit = CloneMachine(machine);
            LabourEdit = NewLabour();
            CaptureMachineEditorClean();
            CaptureLabourEditorClean();
            return Task.CompletedTask;
        });
    }

    protected async Task SelectMachineAsync(PrProductDefMachineVm machine)
    {
        await RequestEditorNavigationAsync(() =>
        {
            SelectedMachine = machine;
            MachineEdit = CloneMachine(machine);
            LabourEdit = NewLabour();
            CaptureMachineEditorClean();
            CaptureLabourEditorClean();
            return Task.CompletedTask;
        });
    }

    protected Task SaveMachineAsync()
    {
        if (SelectedOperation is null)
        {
            ErrorMessage = "Select a route operation before saving a machine.";
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(MachineEdit.MachineCode))
        {
            ErrorMessage = "Machine code is required.";
            return Task.CompletedTask;
        }
        if (MachineEdit.ResourceSequence <= 0 || MachineEdit.ParallelMachineCount <= 0
            || MachineEdit.Priority <= 0 || MachineEdit.OutputPerCycle <= 0m
            || MachineEdit.CycleSeconds < 0m || MachineEdit.ConversionSeconds < 0m
            || MachineEdit.SetupSeconds < 0m || MachineEdit.QueueSeconds < 0m
            || MachineEdit.MachineRatePerHour < 0m)
        {
            ErrorMessage = "Machine priority, output per cycle, and parallel count must be positive; times and rate cannot be negative.";
            return Task.CompletedTask;
        }

        MachineEdit.MachineDescription = Machines.FirstOrDefault(x =>
            string.Equals(x.MachineCd, MachineEdit.MachineCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.ProcessCd, SelectedOperation.OperationCode, StringComparison.OrdinalIgnoreCase))?.MachineDes;
        var index = FindMachineIndex(SelectedOperation, MachineEdit);
        var saved = CloneMachine(MachineEdit);
        saved.IsPrimary = true;
        saved.TempId ??= Guid.NewGuid().ToString("N");
        if (index >= 0)
            SelectedOperation.Machines[index] = saved;
        else if (SelectedOperation.Machines.Count == 0)
            SelectedOperation.Machines.Add(saved);
        else
        {
            ErrorMessage = "This process already has its machine. Edit or remove it before assigning another.";
            return Task.CompletedTask;
        }
        SelectedMachine = saved;
        MachineEdit = CloneMachine(saved);
        LabourEdit = saved.Labours.FirstOrDefault() is { } labour ? CloneLabour(labour) : NewLabour();
        CaptureMachineEditorClean();
        CaptureLabourEditorClean();
        ErrorMessage = null;
        return Task.CompletedTask;
    }

    protected async Task RemoveMachineAsync(PrProductDefMachineVm machine)
    {
        await RequestEditorNavigationAsync(() =>
        {
            SelectedOperation?.Machines.Remove(machine);
            EnsureResourceSelection();
            return Task.CompletedTask;
        });
    }

    protected void BeginAddLabour()
    {
        LabourEdit = SelectedMachine?.Labours.FirstOrDefault() is { } labour
            ? CloneLabour(labour)
            : NewLabour();
        CaptureLabourEditorClean();
    }

    protected void EditLabour(PrProductDefLabourVm labour)
    {
        LabourEdit = CloneLabour(labour);
        CaptureLabourEditorClean();
    }

    protected Task SaveLabourAsync()
    {
        if (SelectedMachine is null)
        {
            ErrorMessage = "Select and save a machine before adding labour.";
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(LabourEdit.LabourCode) || LabourEdit.CostPerOutputUnit < 0m)
        {
            ErrorMessage = "Labour code is required and cost per output unit cannot be negative.";
            return Task.CompletedTask;
        }

        LabourEdit.LabourDescription = Operators.FirstOrDefault(x =>
            string.Equals(x.Code, LabourEdit.LabourCode, StringComparison.OrdinalIgnoreCase))?.Name;
        var index = FindLabourIndex(SelectedMachine, LabourEdit);
        var saved = CloneLabour(LabourEdit);
        saved.TempId ??= Guid.NewGuid().ToString("N");
        if (index >= 0)
            SelectedMachine.Labours[index] = saved;
        else if (SelectedMachine.Labours.Count == 0)
            SelectedMachine.Labours.Add(saved);
        else
        {
            ErrorMessage = "This process-machine already has its labour. Edit or remove it before assigning another.";
            return Task.CompletedTask;
        }
        LabourEdit = CloneLabour(saved);
        CaptureLabourEditorClean();
        ErrorMessage = null;
        return Task.CompletedTask;
    }

    protected void RemoveLabour(PrProductDefLabourVm labour)
    {
        SelectedMachine?.Labours.Remove(labour);
        LabourEdit = SelectedMachine?.Labours.FirstOrDefault() is { } remaining
            ? CloneLabour(remaining)
            : NewLabour();
        CaptureLabourEditorClean();
    }

    private async Task RequestEditorNavigationAsync(Func<Task> action)
    {
        if (IsChildEditorDirty)
        {
            _pendingSelectionAction = action;
            ConfirmDiscardVisible = true;
            return;
        }

        await action();
    }

    private void ResetRoutingEditors()
    {
        OperationEdit = NewOperation();
        SelectedOperation = null;
        SelectedMachine = null;
        PendingCentre = null;
        SelectedCentreKey = null;
        CentreEdit = NewCentre();
        _originalCentreKey = null;
        ResetMachineEditor();
        LabourEdit = NewLabour();
        RebuildCentreRows();
        CaptureEditorClean();
    }

    private void ResetMachineEditor() => MachineEdit = NewMachine();

    private void CaptureEditorClean()
    {
        CaptureCentreEditorClean();
        CaptureOperationEditorClean();
        CaptureMachineEditorClean();
        CaptureLabourEditorClean();
    }

    private void CaptureCentreEditorClean() => _centreEditorSnapshot = SnapshotCentre(CentreEdit);
    private void CaptureOperationEditorClean() => _operationEditorSnapshot = SnapshotOperation(OperationEdit);
    private void CaptureMachineEditorClean() => _machineEditorSnapshot = SnapshotMachine(MachineEdit);
    private void CaptureLabourEditorClean() => _labourEditorSnapshot = SnapshotLabour(LabourEdit);

    private int FindOperationIndex(PrProductDefOperationVm candidate) => Model.Operations.FindIndex(x =>
        candidate.Uid > 0 ? x.Uid == candidate.Uid
            : !string.IsNullOrWhiteSpace(candidate.TempId)
              && string.Equals(x.TempId, candidate.TempId, StringComparison.Ordinal));

    private static int FindMachineIndex(PrProductDefOperationVm owner, PrProductDefMachineVm candidate) =>
        owner.Machines.FindIndex(x => candidate.Uid > 0 ? x.Uid == candidate.Uid
            : !string.IsNullOrWhiteSpace(candidate.TempId)
              && string.Equals(x.TempId, candidate.TempId, StringComparison.Ordinal));

    private static int FindLabourIndex(PrProductDefMachineVm owner, PrProductDefLabourVm candidate) =>
        owner.Labours.FindIndex(x => candidate.Uid > 0 ? x.Uid == candidate.Uid
            : !string.IsNullOrWhiteSpace(candidate.TempId)
              && string.Equals(x.TempId, candidate.TempId, StringComparison.Ordinal));

    private static PrProductDefCentreProjection.CentreRowVm NewCentre() => new()
    {
        CentralSequence = 10,
        OutputBaseQty = 1m
    };

    private static PrProductDefCentreProjection.CentreRowVm CloneCentre(PrProductDefCentreProjection.CentreRowVm x) => new()
    {
        WorkCentreCode = x.WorkCentreCode,
        WorkCentreDescription = x.WorkCentreDescription,
        OutputItemCode = x.OutputItemCode,
        OutputItemDescription = x.OutputItemDescription,
        Class = x.Class,
        CentralSequence = x.CentralSequence,
        OutputBaseQty = x.OutputBaseQty,
        OutputUom = x.OutputUom,
        HasSequenceConflict = x.HasSequenceConflict,
        IsPending = x.IsPending,
        ProcessCount = x.ProcessCount
    };

    private static PrProductDefOperationVm NewOperation() => new()
    {
        CentralSequence = 10,
        ProcessSequence = 10,
        OutputBaseQty = 1m,
        ProcessType = PrProcessTypes.Machine
    };

    private static PrProductDefMachineVm NewMachine() => new()
    {
        ResourceSequence = 10,
        IsPrimary = true,
        Priority = 1,
        OutputPerCycle = 1m,
        ParallelMachineCount = 1
    };

    private static PrProductDefLabourVm NewLabour() => new();

    private static PrProductDefOperationVm CloneOperation(PrProductDefOperationVm x) => new()
    {
        OperationKey = x.OperationKey,
        Uid = x.Uid,
        TempId = x.TempId,
        WorkCentreCode = x.WorkCentreCode,
        WorkCentreDescription = x.WorkCentreDescription,
        OutputItemCode = x.OutputItemCode,
        CentralSequence = x.CentralSequence,
        OutputBaseQty = x.OutputBaseQty,
        OutputUom = x.OutputUom,
        OperationCode = x.OperationCode,
        OperationDescription = x.OperationDescription,
        ProcessSequence = x.ProcessSequence,
        ProcessType = x.ProcessType,
        StandardDurationMinutes = x.StandardDurationMinutes,
        RouteStepKey = x.RouteStepKey,
        SetupLossQty = x.SetupLossQty,
        OperationLossQty = x.OperationLossQty,
        IsFinalOperation = x.IsFinalOperation,
        Remark = x.Remark,
        Machines = x.Machines.Select(CloneMachine).ToList()
    };

    private static PrProductDefMachineVm CloneMachine(PrProductDefMachineVm x) => new()
    {
        Uid = x.Uid,
        TempId = x.TempId,
        MachineCode = x.MachineCode,
        MachineDescription = x.MachineDescription,
        ResourceSequence = x.ResourceSequence,
        IsPrimary = x.IsPrimary,
        Priority = x.Priority,
        OutputPerCycle = x.OutputPerCycle,
        MachineRatePerHour = x.MachineRatePerHour,
        CycleSeconds = x.CycleSeconds,
        ConversionSeconds = x.ConversionSeconds,
        SetupSeconds = x.SetupSeconds,
        QueueSeconds = x.QueueSeconds,
        ParallelMachineCount = x.ParallelMachineCount,
        Labours = x.Labours.Select(CloneLabour).ToList()
    };

    private static PrProductDefLabourVm CloneLabour(PrProductDefLabourVm x) => new()
    {
        Uid = x.Uid,
        TempId = x.TempId,
        LabourCode = x.LabourCode,
        LabourDescription = x.LabourDescription,
        CostPerOutputUnit = x.CostPerOutputUnit
    };

    private void CaptureClean() => _cleanSnapshot = Snapshot(Model);

    private static string SnapshotCentre(PrProductDefCentreProjection.CentreRowVm x) =>
        JsonSerializer.Serialize(new
        {
            x.WorkCentreCode,
            x.OutputItemCode,
            x.CentralSequence,
            x.OutputBaseQty,
            x.OutputUom
        });

    private static string SnapshotOperation(PrProductDefOperationVm x) =>
        JsonSerializer.Serialize(new
        {
            x.Uid,
            x.TempId,
            x.WorkCentreCode,
            x.OutputItemCode,
            x.OperationCode,
            x.ProcessSequence,
            x.ProcessType,
            x.StandardDurationMinutes,
            x.SetupLossQty,
            x.OperationLossQty,
            x.IsFinalOperation,
            x.Remark
        });

    private static string SnapshotMachine(PrProductDefMachineVm x) =>
        JsonSerializer.Serialize(new
        {
            x.Uid,
            x.TempId,
            x.MachineCode,
            x.ResourceSequence,
            x.IsPrimary,
            x.Priority,
            x.OutputPerCycle,
            x.MachineRatePerHour,
            x.CycleSeconds,
            x.ConversionSeconds,
            x.SetupSeconds,
            x.QueueSeconds,
            x.ParallelMachineCount
        });

    private static string SnapshotLabour(PrProductDefLabourVm x) =>
        JsonSerializer.Serialize(new
        {
            x.Uid,
            x.TempId,
            x.LabourCode,
            x.CostPerOutputUnit
        });

    private static string Snapshot(PrProductDefEditVm model) =>
        JsonSerializer.Serialize(new
        {
            model.ProdCode,
            model.BaseQty,
            model.BaseUom,
            model.Prefix,
            model.Remark,
            model.DefinitionCode,
            model.DefinitionName,
            model.IsDefaultDefinition,
            model.Status,
            model.Version,
            Lines = model.Lines.Select(x => new
            {
                x.OperationKey,
                x.Uid,
                x.TempId,
                x.ICode,
                x.IName,
                x.StdQty,
                x.StdUom,
                x.SeqNo,
                x.ScrapPercent,
                x.Warehouse,
                x.BomDefault,
                x.AlternateGroupCode,
                x.Tolerance,
                x.IssueMethod,
                x.SupplySource,
                x.ComponentDefinitionCode,
                x.ProducingRouteStepKey
            }),
            Operations = model.Operations.Select(x => new
            {
                x.OperationKey,
                x.Uid,
                x.TempId,
                x.WorkCentreCode,
                x.OutputItemCode,
                x.CentralSequence,
                x.OutputBaseQty,
                x.OutputUom,
                x.OperationCode,
                x.ProcessSequence,
                x.ProcessType,
                x.StandardDurationMinutes,
                x.SetupLossQty,
                x.OperationLossQty,
                x.IsFinalOperation,
                x.Remark,
                Machines = x.Machines.Select(m => new
                {
                    m.Uid,
                    m.TempId,
                    m.MachineCode,
                    m.ResourceSequence,
                    m.IsPrimary,
                    m.Priority,
                    m.OutputPerCycle,
                    m.MachineRatePerHour,
                    m.CycleSeconds,
                    m.ConversionSeconds,
                    m.SetupSeconds,
                    m.QueueSeconds,
                    m.ParallelMachineCount,
                    Labours = m.Labours.Select(l => new
                    {
                        l.Uid,
                        l.TempId,
                        l.LabourCode,
                        l.CostPerOutputUnit
                    })
                })
            })
        });

    /// <summary>
    /// Replaces persisted direct children of <paramref name="currentOwner"/> with <paramref name="model"/>.Lines;
    /// reattaches persisted descendant subtrees for matching Make/Phantom component codes.
    /// </summary>
    public static IReadOnlyList<PrBomStructureNode> MergeStructureWithCurrentOwner(
        IReadOnlyList<PrBomStructureNode> persisted,
        string structureRoot,
        string structureRootDefinition,
        string currentOwner,
        string currentOwnerDefinition,
        PrProductDefEditVm model)
    {
        var rootCode = PrBomStructureKeys.Normalize(structureRoot);
        var rootDefinition = string.IsNullOrWhiteSpace(structureRootDefinition)
            ? PrProductDefinitionCodes.Standard
            : PrProductDefinitionCodes.Normalize(structureRootDefinition);
        var owner = PrBomStructureKeys.Normalize(currentOwner);
        var ownerDefinition = string.IsNullOrWhiteSpace(currentOwnerDefinition)
            ? (string.IsNullOrWhiteSpace(model.DefinitionCode)
                ? PrProductDefinitionCodes.Standard
                : PrProductDefinitionCodes.Normalize(model.DefinitionCode))
            : PrProductDefinitionCodes.Normalize(currentOwnerDefinition);
        if (rootCode.Length == 0)
        {
            return [];
        }

        var rootKey = PrBomStructureKeys.Root(rootCode, rootDefinition);
        List<PrBomStructureNode> baseNodes;
        if (persisted.Count == 0)
        {
            baseNodes =
            [
                new PrBomStructureNode
                {
                    Key = rootKey,
                    ParentKey = null,
                    Level = 0,
                    ItemCode = rootCode,
                    ItemDesc = model.ProdDesc,
                    MfgType = PrMfgTypes.Normalize(model.MfgType),
                    StdQty = model.BaseQty,
                    StdUom = model.BaseUom ?? model.StdUom,
                    OwnerProdCode = null,
                    BomHdrId = model.BomHdrId > 0 ? model.BomHdrId : null,
                    BomVersion = model.Version > 0 ? model.Version : null,
                    BomStatus = model.Status,
                    Status = PrBomStructureNodeStatus.Normal
                }
            ];
        }
        else
        {
            baseNodes = persisted.ToList();
        }

        // Owner assembly node keys in the tree (root or line occurrences of the owner item)
        var ownerNodeKeys = new List<string>();
        if (string.Equals(owner, rootCode, StringComparison.OrdinalIgnoreCase))
        {
            ownerNodeKeys.Add(rootKey);
        }
        else
        {
            ownerNodeKeys.AddRange(baseNodes
                .Where(x => string.Equals(x.ItemCode, owner, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Key));
        }

        if (ownerNodeKeys.Count == 0)
        {
            // Owner not in tree yet (e.g. MissingBom make selected for create) — still show persisted
            return baseNodes;
        }

        // Capture old direct children per owner node for subtree reattach
        var oldChildrenByOwnerNode = ownerNodeKeys.ToDictionary(
            k => k,
            k => baseNodes.Where(n => string.Equals(n.ParentKey, k, StringComparison.Ordinal)).ToList(),
            StringComparer.Ordinal);

        // Remove all nodes that are descendants of any owner node key (children and below)
        var removeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ok in ownerNodeKeys)
        {
            CollectDescendantKeys(baseNodes, ok, removeKeys);
        }

        var kept = baseNodes.Where(n => !removeKeys.Contains(n.Key)).ToList();
        var result = new List<PrBomStructureNode>(kept);

        foreach (var ownerNodeKey in ownerNodeKeys)
        {
            var ownerNode = result.FirstOrDefault(x => string.Equals(x.Key, ownerNodeKey, StringComparison.Ordinal))
                            ?? baseNodes.First(x => string.Equals(x.Key, ownerNodeKey, StringComparison.Ordinal));
            var ownerLevel = ownerNode.Level;
            oldChildrenByOwnerNode.TryGetValue(ownerNodeKey, out var oldChildren);
            oldChildren ??= [];

            foreach (var line in model.Lines.OrderBy(x => x.SeqNo).ThenBy(x => x.ICode))
            {
                var lineId = PrBomStructureKeys.LineId(line.Uid, line.TempId);
                var key = PrBomStructureKeys.Line(ownerNodeKey, owner, ownerDefinition, lineId);
                var mfg = PrMfgTypes.Normalize(line.MfgType);
                var status = PrBomStructureNodeStatus.Normal;

                var oldMatch = oldChildren.FirstOrDefault(c => c.SourceLineUid == line.Uid && line.Uid > 0)
                    ?? oldChildren.FirstOrDefault(c =>
                    string.Equals(c.ItemCode, line.ICode, StringComparison.OrdinalIgnoreCase));

                result.Add(new PrBomStructureNode
                {
                    Key = key,
                    ParentKey = ownerNodeKey,
                    Level = ownerLevel + 1,
                    ItemCode = line.ICode,
                    ItemDesc = line.IName,
                    MfgType = mfg,
                    StdQty = line.StdQty,
                    StdUom = line.StdUom,
                    ScrapPercent = line.ScrapPercent,
                    Warehouse = line.Warehouse,
                    SeqNo = line.SeqNo,
                    OwnerProdCode = owner,
                    OwnerDefinitionCode = ownerDefinition,
                    ComponentDefinitionCode = string.Equals(
                        line.SupplySource,
                        PrMaterialSupplySources.SeparateProductDefinition,
                        StringComparison.OrdinalIgnoreCase)
                        ? PrProductDefinitionCodes.Normalize(line.ComponentDefinitionCode)
                        : null,
                    SourceLineUid = line.Uid > 0 ? line.Uid : null,
                    LineTempId = line.Uid > 0 ? null : line.TempId,
                    BomHdrId = model.BomHdrId > 0 ? model.BomHdrId : null,
                    BomVersion = model.Version > 0 ? model.Version : null,
                    BomStatus = model.Status,
                    Status = oldMatch?.Status ?? status
                });

                if (oldMatch is not null
                    && mfg is PrMfgTypes.Make or PrMfgTypes.Phantom
                    && oldMatch.Status == PrBomStructureNodeStatus.Normal)
                {
                    ReattachSubtree(persisted.Count > 0 ? persisted : baseNodes, result, oldMatch.Key, key);
                }
            }
        }

        return result;
    }

    private static void CollectDescendantKeys(
        IReadOnlyList<PrBomStructureNode> nodes,
        string parentKey,
        HashSet<string> sink)
    {
        foreach (var child in nodes.Where(n => string.Equals(n.ParentKey, parentKey, StringComparison.Ordinal)))
        {
            if (!sink.Add(child.Key))
            {
                continue;
            }

            CollectDescendantKeys(nodes, child.Key, sink);
        }
    }

    private static void ReattachSubtree(
        IReadOnlyList<PrBomStructureNode> source,
        List<PrBomStructureNode> target,
        string oldParentKey,
        string newParentKey)
    {
        foreach (var child in source.Where(n => string.Equals(n.ParentKey, oldParentKey, StringComparison.Ordinal)))
        {
            var newKey = newParentKey + child.Key[oldParentKey.Length..];
            target.Add(new PrBomStructureNode
            {
                Key = newKey,
                ParentKey = newParentKey,
                Level = child.Level, // approximate; TreeList uses ParentKey
                ItemCode = child.ItemCode,
                ItemDesc = child.ItemDesc,
                MfgType = child.MfgType,
                StdQty = child.StdQty,
                StdUom = child.StdUom,
                ScrapPercent = child.ScrapPercent,
                Warehouse = child.Warehouse,
                SeqNo = child.SeqNo,
                OwnerProdCode = child.OwnerProdCode,
                SourceLineUid = child.SourceLineUid,
                LineTempId = child.LineTempId,
                BomHdrId = child.BomHdrId,
                BomVersion = child.BomVersion,
                BomStatus = child.BomStatus,
                Status = child.Status
            });
            ReattachSubtree(source, target, child.Key, newKey);
        }
    }
}
