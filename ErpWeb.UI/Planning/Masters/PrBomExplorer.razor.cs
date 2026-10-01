using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Masters;

public partial class PrBomExplorer : PageBase
{
    [Parameter] public string ProdCode { get; set; } = string.Empty;
    [Parameter] public string? DefinitionCode { get; set; }

    [Inject] private IBomExplosionService Explosion { get; set; } = default!;
    [Inject] private IPrProductDefService ProductDefs { get; set; } = default!;

    protected decimal Quantity { get; set; } = 1m;
    protected string ResolvedDefinitionCode { get; set; } = string.Empty;
    protected string SelectedMode { get; set; } = nameof(BomExplosionMode.StructuralTree);
    protected bool IsLoading;
    protected bool NeedsDefinitionChoice;
    protected IReadOnlyList<PrProductDefinitionLookupRow> DefinitionChoices { get; set; } = [];
    protected BomExplosionResult? Result { get; set; }

    private string? _loadedKey;

    protected IReadOnlyList<ModeOption> ModeOptions { get; } =
    [
        new(nameof(BomExplosionMode.StructuralTree), "Structural Tree"),
        new(nameof(BomExplosionMode.MaterialRequirement), "Material Requirement"),
        new(nameof(BomExplosionMode.ProductionIssueRequirement), "Production Issue Requirement")
    ];

    protected string ModeTitle => ModeOptions.FirstOrDefault(x => x.Value == SelectedMode)?.Text
        ?? "Explosion";

    protected override async Task OnParametersSetAsync()
    {
        var key = $"{ProdCode}|{DefinitionCode}";
        if (string.Equals(_loadedKey, key, StringComparison.OrdinalIgnoreCase) && !IsLoading)
        {
            return;
        }

        _loadedKey = key;
        NeedsDefinitionChoice = false;
        DefinitionChoices = [];
        Result = null;
        ResolvedDefinitionCode = string.Empty;

        if (string.IsNullOrWhiteSpace(ProdCode))
        {
            ErrorMessage = "Product code is required.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(DefinitionCode))
        {
            ResolvedDefinitionCode = PrProductDefinitionCodes.Normalize(DefinitionCode);
            await ExplodeAsync();
            return;
        }

        var list = await ProductDefs.ListDefinitionsAsync(ProdCode);
        if (!list.Succeeded || list.Data is null || list.Data.Count == 0)
        {
            ErrorMessage = list.Message ?? $"No Product Definitions found for {ProdCode}.";
            return;
        }

        if (list.Data.Count == 1)
        {
            Navigation.NavigateTo(
                $"/planning/product-definitions/explode/{Uri.EscapeDataString(ProdCode)}/{Uri.EscapeDataString(list.Data[0].DefinitionCode)}",
                replace: true);
            return;
        }

        var defaults = list.Data.Where(x => x.IsDefaultDefinition).ToList();
        if (defaults.Count == 1)
        {
            Navigation.NavigateTo(
                $"/planning/product-definitions/explode/{Uri.EscapeDataString(ProdCode)}/{Uri.EscapeDataString(defaults[0].DefinitionCode)}",
                replace: true);
            return;
        }

        NeedsDefinitionChoice = true;
        DefinitionChoices = list.Data;
    }

    protected void ChooseDefinition(string definitionCode) =>
        Navigation.NavigateTo(
            $"/planning/product-definitions/explode/{Uri.EscapeDataString(ProdCode)}/{Uri.EscapeDataString(definitionCode)}");

    protected async Task ExplodeAsync()
    {
        if (string.IsNullOrWhiteSpace(ResolvedDefinitionCode))
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            if (!Enum.TryParse<BomExplosionMode>(SelectedMode, out var mode))
            {
                mode = BomExplosionMode.StructuralTree;
            }

            var result = await Explosion.ExplodeAsync(new BomExplosionRequest
            {
                ProdCode = ProdCode,
                DefinitionCode = ResolvedDefinitionCode,
                Quantity = Quantity,
                Mode = mode
            });

            if (!result.Succeeded || result.Data is null)
            {
                Result = null;
                ErrorMessage = result.Message ?? "Explosion failed.";
                return;
            }

            Result = result.Data;
            ResolvedDefinitionCode = result.Data.RootDefinitionCode;
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected Task GoBackAsync()
    {
        if (!string.IsNullOrWhiteSpace(ResolvedDefinitionCode))
        {
            Navigation.NavigateTo(
                $"/planning/product-definitions/view/{Uri.EscapeDataString(ProdCode)}/{Uri.EscapeDataString(ResolvedDefinitionCode)}");
        }
        else
        {
            Navigation.NavigateTo($"/planning/product-definitions/view/{Uri.EscapeDataString(ProdCode)}");
        }

        return Task.CompletedTask;
    }

    protected sealed record ModeOption(string Value, string Text);
}
