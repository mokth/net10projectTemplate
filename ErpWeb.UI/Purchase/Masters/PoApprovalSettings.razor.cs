using System.Text.Json;
using System.Text.Json.Serialization;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Masters;

public partial class PoApprovalSettings : PageBase
{
    private static readonly JsonSerializerOptions SnapshotOptions = new() { WriteIndented = false };
    private string _snapshot = string.Empty;
    private int _pendingTabIndex;
    private string _pendingDocType = ProcurementApprovalDocTypes.Pr;

    [Inject] private IProcurementApprovalService Approval { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected int ActiveTabIndex;
    protected PolicyEditor? Editor;
    protected bool CanEdit;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool DiscardVisible;
    protected string? StatusMessage;
    protected string ActiveDocType = ProcurementApprovalDocTypes.Pr;

    protected bool PolicyEnabled => Editor?.Enabled == true;
    protected int PolicyVersion => Editor?.Version ?? 0;
    protected bool IsDirty => Editor is not null && Serialize(Editor) != _snapshot;

    protected IReadOnlyList<LegacyModeOption> LegacyModeOptions { get; } =
    [
        new(ProcurementApprovalLegacyModes.RequireGoingForward, "Require going forward"),
        new(ProcurementApprovalLegacyModes.LegacyExempt, "Exempt existing drafts")
    ];

    protected IReadOnlyList<string> MatchModes { get; } =
        [ProcurementApprovalMatchModes.Any, ProcurementApprovalMatchModes.All];

    protected IReadOnlyList<AssigneeKindOption> AssigneeKindOptions { get; } =
    [
        new(ProcurementApprovalAssigneeKinds.User, "User"),
        new(ProcurementApprovalAssigneeKinds.Role, "Role")
    ];

    protected IReadOnlyList<ProcurementApprovalLookupRow> Users { get; set; } = [];
    protected IReadOnlyList<ProcurementApprovalLookupRow> Roles { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        CanEdit = await AccessRights.CanAsync(MenuCodes.PurchaseApprovalSettings, PermissionCodes.Edit);
        await LoadLookupsAsync();
        await LoadAsync(ProcurementApprovalDocTypes.Pr);
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    protected Task OnTabIndexChangedAsync(int index)
    {
        var nextType = index == 1 ? ProcurementApprovalDocTypes.Po : ProcurementApprovalDocTypes.Pr;
        if (string.Equals(nextType, ActiveDocType, StringComparison.OrdinalIgnoreCase))
        {
            ActiveTabIndex = index;
            return Task.CompletedTask;
        }

        if (IsDirty)
        {
            _pendingTabIndex = index;
            _pendingDocType = nextType;
            DiscardVisible = true;
            return Task.CompletedTask;
        }

        ActiveTabIndex = index;
        return LoadAsync(nextType);
    }

    protected Task ConfirmDiscardAsync()
    {
        DiscardVisible = false;
        ActiveTabIndex = _pendingTabIndex;
        return LoadAsync(_pendingDocType);
    }

    protected Task OnCancelAsync()
    {
        if (IsDirty)
        {
            _pendingTabIndex = ActiveTabIndex;
            _pendingDocType = ActiveDocType;
            DiscardVisible = true;
            return Task.CompletedTask;
        }

        return LoadAsync(ActiveDocType);
    }

    protected void AddBand()
    {
        Editor?.Bands.Add(new BandEditor
        {
            Currency = "MYR",
            MinInclusive = 0m,
            Levels = [new LevelEditor { Sequence = 1, MatchMode = ProcurementApprovalMatchModes.Any }]
        });
    }

    protected void RemoveBand(int index)
    {
        if (Editor is null || index < 0 || index >= Editor.Bands.Count)
        {
            return;
        }

        Editor.Bands.RemoveAt(index);
    }

    protected static void AddLevel(BandEditor band) =>
        band.Levels.Add(new LevelEditor
        {
            Sequence = band.Levels.Count + 1,
            MatchMode = ProcurementApprovalMatchModes.Any
        });

    protected static void RemoveLevel(BandEditor band, int index)
    {
        if (index < 0 || index >= band.Levels.Count)
        {
            return;
        }

        band.Levels.RemoveAt(index);
    }

    protected static bool IsRoleKind(LevelEditor level) =>
        string.Equals(level.PendingKind, ProcurementApprovalAssigneeKinds.Role, StringComparison.OrdinalIgnoreCase);

    protected IReadOnlyList<ProcurementApprovalLookupRow> AvailableAssignees(LevelEditor level)
    {
        var source = IsRoleKind(level) ? Roles : Users;
        return source
            .Where(row => !level.Assignees.Any(a =>
                string.Equals(a.Kind, level.PendingKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.RefCode, row.Code, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    protected static void OnAssigneeKindChanged(LevelEditor level, string kind)
    {
        level.PendingKind = string.IsNullOrWhiteSpace(kind)
            ? ProcurementApprovalAssigneeKinds.User
            : kind;
        level.PendingRefCode = null;
    }

    protected static void AddAssignee(LevelEditor level)
    {
        var code = level.PendingRefCode?.Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var kind = string.Equals(level.PendingKind, ProcurementApprovalAssigneeKinds.Role, StringComparison.OrdinalIgnoreCase)
            ? ProcurementApprovalAssigneeKinds.Role
            : ProcurementApprovalAssigneeKinds.User;
        if (level.Assignees.Any(a =>
            string.Equals(a.Kind, kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.RefCode, code, StringComparison.OrdinalIgnoreCase)))
        {
            level.PendingRefCode = null;
            return;
        }

        level.Assignees.Add(new ProcurementApprovalLevelAssigneeDto { Kind = kind, RefCode = code });
        level.PendingRefCode = null;
    }

    protected static void RemoveAssignee(LevelEditor level, ProcurementApprovalLevelAssigneeDto assignee) =>
        level.Assignees.Remove(assignee);

    protected string AssigneeLabel(ProcurementApprovalLevelAssigneeDto assignee)
    {
        var kind = string.Equals(assignee.Kind, ProcurementApprovalAssigneeKinds.Role, StringComparison.OrdinalIgnoreCase)
            ? "Role"
            : "User";
        return $"{kind} {LookupDisplay(assignee.Kind, assignee.RefCode)}";
    }

    protected async Task SaveAsync()
    {
        if (Editor is null || !CanEdit || IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var result = await Approval.SavePolicyAsync(ToDto(Editor));
            if (!result.Succeeded)
            {
                ErrorMessage = result.ErrorMessage;
                return;
            }

            Editor = FromDto(result.Policy ?? ToDto(Editor));
            CaptureSnapshot();
            StatusMessage = "Approval rules saved.";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task LoadLookupsAsync()
    {
        var result = await Approval.GetLookupsAsync();
        if (!result.Succeeded)
        {
            Users = [];
            Roles = [];
            return;
        }

        Users = result.Users ?? [];
        Roles = result.Roles ?? [];
    }

    private async Task LoadAsync(string docType)
    {
        IsLoading = true;
        ErrorMessage = null;
        ActiveDocType = docType;
        ActiveTabIndex = string.Equals(docType, ProcurementApprovalDocTypes.Po, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var result = await Approval.GetPolicyAsync(docType);
        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
            Editor = null;
            _snapshot = string.Empty;
        }
        else
        {
            Editor = FromDto(result.Policy ?? new ProcurementApprovalPolicyDto { DocType = docType });
            if (Editor.Bands.Count == 0)
            {
                AddBand();
            }

            CaptureSnapshot();
        }

        IsLoading = false;
    }

    private void CaptureSnapshot() => _snapshot = Editor is null ? string.Empty : Serialize(Editor);

    private static string Serialize(PolicyEditor editor) => JsonSerializer.Serialize(editor, SnapshotOptions);

    private static PolicyEditor FromDto(ProcurementApprovalPolicyDto policy) =>
        new()
        {
            DocType = policy.DocType,
            Version = policy.Version,
            Enabled = policy.Enabled,
            LegacyDraftMode = string.IsNullOrWhiteSpace(policy.LegacyDraftMode)
                ? ProcurementApprovalLegacyModes.RequireGoingForward
                : policy.LegacyDraftMode,
            Bands = policy.Bands.Select(band => new BandEditor
            {
                Currency = band.Currency,
                MinInclusive = band.MinInclusive,
                MaxExclusive = band.MaxExclusive,
                NotRequired = band.NotRequired,
                Levels = band.Levels.Select(level => new LevelEditor
                {
                    Sequence = level.Sequence,
                    MatchMode = level.MatchMode,
                    Assignees = level.Assignees
                        .Select(a => new ProcurementApprovalLevelAssigneeDto
                        {
                            Kind = a.Kind,
                            RefCode = a.RefCode
                        })
                        .ToList()
                }).ToList()
            }).ToList()
        };

    private static ProcurementApprovalPolicyDto ToDto(PolicyEditor editor) =>
        new()
        {
            DocType = editor.DocType,
            Enabled = editor.Enabled,
            LegacyDraftMode = editor.LegacyDraftMode,
            Bands = editor.Bands.Select(band => new ProcurementApprovalBandDto
            {
                Currency = band.Currency,
                MinInclusive = band.MinInclusive,
                MaxExclusive = band.MaxExclusive,
                NotRequired = band.NotRequired,
                Levels = band.Levels.Select(level => new ProcurementApprovalLevelDto
                {
                    Sequence = level.Sequence,
                    MatchMode = level.MatchMode,
                    Assignees = level.Assignees
                        .Where(a => !string.IsNullOrWhiteSpace(a.RefCode))
                        .Select(a => new ProcurementApprovalLevelAssigneeDto
                        {
                            Kind = string.Equals(a.Kind, ProcurementApprovalAssigneeKinds.Role, StringComparison.OrdinalIgnoreCase)
                                ? ProcurementApprovalAssigneeKinds.Role
                                : ProcurementApprovalAssigneeKinds.User,
                            RefCode = a.RefCode.Trim()
                        })
                        .ToList()
                }).ToList()
            }).ToList()
        };

    private string LookupDisplay(string kind, string refCode)
    {
        var source = string.Equals(kind, ProcurementApprovalAssigneeKinds.Role, StringComparison.OrdinalIgnoreCase)
            ? Roles
            : Users;
        var match = source.FirstOrDefault(x => string.Equals(x.Code, refCode, StringComparison.OrdinalIgnoreCase));
        return match?.DisplayText ?? refCode;
    }

    protected sealed record LegacyModeOption(string Key, string Name);

    protected sealed record AssigneeKindOption(string Key, string Name);

    public sealed class PolicyEditor
    {
        public string DocType { get; set; } = ProcurementApprovalDocTypes.Pr;
        public int Version { get; set; }
        public bool Enabled { get; set; }
        public string LegacyDraftMode { get; set; } = ProcurementApprovalLegacyModes.RequireGoingForward;
        public List<BandEditor> Bands { get; set; } = [];
    }

    public sealed class BandEditor
    {
        public string Currency { get; set; } = "MYR";
        public decimal MinInclusive { get; set; }
        public decimal? MaxExclusive { get; set; }
        public bool NotRequired { get; set; }
        public List<LevelEditor> Levels { get; set; } = [];
    }

    public sealed class LevelEditor
    {
        public int Sequence { get; set; } = 1;
        public string MatchMode { get; set; } = ProcurementApprovalMatchModes.Any;
        public List<ProcurementApprovalLevelAssigneeDto> Assignees { get; set; } = [];

        [JsonIgnore]
        public string PendingKind { get; set; } = ProcurementApprovalAssigneeKinds.User;

        [JsonIgnore]
        public string? PendingRefCode { get; set; }
    }
}
