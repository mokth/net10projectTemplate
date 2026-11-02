using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ErpWeb.Core.Inventory;
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
    private static readonly CultureInfo AmountCulture = CultureInfo.InvariantCulture;
    private const decimal PreviewSampleAmount = 5000m;
    private const string DefaultCurrencyCode = "MYR";

    private string _snapshot = string.Empty;
    private int _pendingTabIndex;
    private string _pendingDocType = ProcurementApprovalDocTypes.Pr;
    private bool _loadedEnabled;
    private int _loadedVersion;
    private string _loadedLegacyDraftMode = ProcurementApprovalLegacyModes.RequireGoingForward;
    private RemoveTarget _pendingRemove;
    private bool _currencyLookupFailed;

    [Inject] private IProcurementApprovalService Approval { get; set; } = default!;
    [Inject] private IPoSupplierLookupService SupplierLookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected int ActiveTabIndex;
    protected BandEditor? ExpandedBand;
    protected PolicyEditor? Editor;
    protected bool CanEdit;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool DiscardVisible;
    protected bool PolicyConfirmVisible;
    protected bool RemoveConfirmVisible;
    protected bool HelpVisible;
    protected string? StatusMessage;
    protected string? ServerErrorDetail;
    protected string ActiveDocType = ProcurementApprovalDocTypes.Pr;
    protected List<ClientIssue> ClientIssues { get; private set; } = [];

    protected bool PublishedEnabled => _loadedEnabled;
    protected int PublishedVersion => _loadedVersion;
    protected bool IsDirty => Editor is not null && Serialize(Editor) != _snapshot;
    protected bool CurrencyLookupFailed => _currencyLookupFailed;
    protected bool CurrencyLookupEmpty => !_currencyLookupFailed && Currencies.Count == 0;
    protected bool CanShowCurrencyCombo => !_currencyLookupFailed;
    protected bool HasUnsavedEnabledChange =>
        Editor is not null && Editor.Enabled != _loadedEnabled;
    protected bool IsUiLocked =>
        IsLoading || IsSubmitting || PolicyConfirmVisible || DiscardVisible || RemoveConfirmVisible;
    protected bool CanMutate => CanEdit && !IsUiLocked;
    protected bool CanSave =>
        CanEdit
        && Editor is not null
        && IsDirty
        && !IsSubmitting
        && !IsLoading
        && !PolicyConfirmVisible
        && !DiscardVisible
        && !RemoveConfirmVisible;
    protected bool PolicyImpactChanged =>
        Editor is not null
        && (Editor.Enabled != _loadedEnabled
            || !string.Equals(Editor.LegacyDraftMode, _loadedLegacyDraftMode, StringComparison.OrdinalIgnoreCase));

    protected IReadOnlyList<LegacyModeOption> LegacyModeOptions { get; } =
    [
        new(ProcurementApprovalLegacyModes.RequireGoingForward, "Yes — include existing unfinished documents"),
        new(ProcurementApprovalLegacyModes.LegacyExempt, "No — start with newly created documents")
    ];

    protected IReadOnlyList<MatchModeOption> MatchModeOptions { get; } =
    [
        new(ProcurementApprovalMatchModes.Any, "Any one of these approvers"),
        new(ProcurementApprovalMatchModes.All, "Everyone listed must approve")
    ];

    protected IReadOnlyList<AssigneeKindOption> AssigneeKindOptions { get; } =
    [
        new(ProcurementApprovalAssigneeKinds.User, "Person"),
        new(ProcurementApprovalAssigneeKinds.Role, "Role")
    ];

    protected IReadOnlyList<RequirementOption> RequirementOptions { get; } =
    [
        new(false, "Approval needed"),
        new(true, "No approval needed")
    ];

    protected IReadOnlyList<ProcurementApprovalLookupRow> Users { get; set; } = [];
    protected IReadOnlyList<ProcurementApprovalLookupRow> Roles { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Currencies { get; set; } = [];

    protected string LegacyModeHint =>
        string.Equals(Editor?.LegacyDraftMode, ProcurementApprovalLegacyModes.LegacyExempt, StringComparison.OrdinalIgnoreCase)
            ? "Unprocessed PRs/POs created before approval was enabled can continue without new approval."
            : "Older PRs/POs that have not been processed must also be approved.";

    protected string UnsavedEnabledHint =>
        Editor is null
            ? string.Empty
            : Editor.Enabled
                ? "Not saved yet — approval will turn ON after Save."
                : "Not saved yet — approval will turn OFF after Save.";

    protected string PolicyConfirmCopy
    {
        get
        {
            if (Editor is null)
            {
                return string.Empty;
            }

            if (Editor.Enabled != _loadedEnabled)
            {
                if (Editor.Enabled)
                {
                    var older = string.Equals(
                        Editor.LegacyDraftMode,
                        ProcurementApprovalLegacyModes.LegacyExempt,
                        StringComparison.OrdinalIgnoreCase)
                        ? "keep the old process"
                        : "also need approval";
                    return $"This will turn ON approval for all branches in this company. Older unfinished documents will {older}. Save these changes? An existing pending approval is not automatically cleared.";
                }

                return "New documents will no longer use these approval rules. Existing requests waiting for approval may still need to be completed. Save these changes?";
            }

            return "This changes whether older unfinished documents need approval too. Changes apply to all branches and take effect after Save. Save these changes?";
        }
    }

    protected string RemoveConfirmCopy =>
        _pendingRemove.Kind == RemoveKind.Level
            ? "Delete this approval step and its selected people?"
            : "Delete this approval rule and its approvers?";

    protected override async Task OnPageInitializedAsync()
    {
        CanEdit = await AccessRights.CanAsync(MenuCodes.PurchaseApprovalSettings, PermissionCodes.Edit);
        await LoadLookupsAsync();
        await LoadAsync(ProcurementApprovalDocTypes.Pr);
    }

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError()
    {
        ErrorMessage = null;
        ServerErrorDetail = null;
    }

    protected void OpenHelp() => HelpVisible = true;

    protected void CloseHelp() => HelpVisible = false;

    protected Task OnTabIndexChangedAsync(int index)
    {
        if (IsUiLocked)
        {
            return Task.CompletedTask;
        }

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

    protected void KeepEditing()
    {
        DiscardVisible = false;
        PolicyConfirmVisible = false;
        RemoveConfirmVisible = false;
    }

    protected Task OnCancelAsync()
    {
        if (IsUiLocked)
        {
            return Task.CompletedTask;
        }

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
        if (Editor is null || !CanMutate)
        {
            return;
        }

        var band = CreateBand(PreferredNewCurrency());
        Editor.Bands.Add(band);
        ExpandedBand = band;
        ClientIssues = [];
    }

    protected void RequestRemoveBand(BandEditor band)
    {
        if (Editor is null || !CanMutate)
        {
            return;
        }

        if (BandHasSignificantData(band))
        {
            _pendingRemove = new RemoveTarget(RemoveKind.Band, band, null);
            RemoveConfirmVisible = true;
            return;
        }

        RemoveBand(band);
    }

    protected void RequestRemoveLevel(BandEditor band, LevelEditor level)
    {
        if (!CanMutate)
        {
            return;
        }

        if (level.Assignees.Count > 0)
        {
            _pendingRemove = new RemoveTarget(RemoveKind.Level, band, level);
            RemoveConfirmVisible = true;
            return;
        }

        RemoveLevel(band, level);
    }

    protected void ConfirmRemove()
    {
        RemoveConfirmVisible = false;
        if (Editor is null || _pendingRemove.Band is null)
        {
            return;
        }

        if (_pendingRemove.Kind == RemoveKind.Band)
        {
            RemoveBand(_pendingRemove.Band);
            return;
        }

        if (_pendingRemove.Level is not null)
        {
            RemoveLevel(_pendingRemove.Band, _pendingRemove.Level);
        }
    }

    protected void ToggleExpand(BandEditor band)
    {
        if (IsSubmitting)
        {
            return;
        }

        ExpandedBand = ReferenceEquals(ExpandedBand, band) ? null : band;
    }

    protected static void AddLevel(BandEditor band)
    {
        if (band.Levels.Count >= 3)
        {
            return;
        }

        band.Levels.Add(new LevelEditor
        {
            Sequence = band.Levels.Count + 1,
            MatchMode = ProcurementApprovalMatchModes.Any
        });
    }

    public static void ApplyRequirement(BandEditor band, bool notRequired)
    {
        band.NotRequired = notRequired;
        if (!notRequired && band.Levels.Count == 0)
        {
            AddLevel(band);
        }
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

    protected IReadOnlyList<IvCodeLookupRow> CurrencyOptionsFor(BandEditor band)
    {
        var code = band.Currency?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code)
            || Currencies.Any(row => string.Equals(row.Code, code, StringComparison.OrdinalIgnoreCase)))
        {
            return Currencies;
        }

        return Currencies
            .Prepend(new IvCodeLookupRow { Code = code, Desc = "existing code" })
            .ToList();
    }

    protected static void OnAssigneeKindChanged(LevelEditor level, string kind)
    {
        level.PendingKind = string.IsNullOrWhiteSpace(kind)
            ? ProcurementApprovalAssigneeKinds.User
            : kind;
        level.PendingRefCode = null;
    }

    protected static void OnPendingAssigneeChanged(LevelEditor level, string? code)
    {
        level.PendingRefCode = code;
        AddAssignee(level);
    }

    protected static void OnCurrencyChanged(BandEditor band, string? code) =>
        band.Currency = code ?? string.Empty;

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
            : "Person";
        return $"{kind} {LookupDisplay(assignee.Kind, assignee.RefCode)}";
    }

    protected bool IsRuleInvalid(BandEditor band) =>
        Editor is not null
        && ClientIssues.Any(issue =>
            issue.BandIndex >= 0
            && issue.BandIndex < Editor.Bands.Count
            && ReferenceEquals(Editor.Bands[issue.BandIndex], band));

    protected string LevelCountLabel(BandEditor band)
    {
        if (band.NotRequired)
        {
            return "—";
        }

        return band.Levels.Count switch
        {
            0 => "No steps yet",
            1 => "1 step",
            var count => $"{count} steps"
        };
    }

    protected string? PreviewText => Editor is null ? null : FormatPreview(Editor);

    protected Task SaveAsync()
    {
        if (Editor is null || !CanEdit || IsSubmitting || IsLoading || !IsDirty || PolicyConfirmVisible)
        {
            return Task.CompletedTask;
        }

        ErrorMessage = null;
        ServerErrorDetail = null;
        StatusMessage = null;
        ClientIssues = CollectIssues(Editor);
        if (ClientIssues.Count > 0)
        {
            ExpandFirstIssue();
            return Task.CompletedTask;
        }

        if (PolicyImpactChanged)
        {
            PolicyConfirmVisible = true;
            return Task.CompletedTask;
        }

        return PersistPolicyAsync();
    }

    protected Task ConfirmPolicySaveAsync()
    {
        PolicyConfirmVisible = false;
        if (Editor is null || !CanEdit || IsSubmitting || !IsDirty)
        {
            return Task.CompletedTask;
        }

        return PersistPolicyAsync();
    }

    protected Task RetryCurrenciesAsync() => LoadCurrenciesAsync();

    private async Task PersistPolicyAsync()
    {
        if (Editor is null || !CanEdit || IsSubmitting || !IsDirty)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        ServerErrorDetail = null;
        StatusMessage = null;
        try
        {
            var result = await Approval.SavePolicyAsync(ToDto(Editor));
            if (!result.Succeeded)
            {
                var original = result.ErrorMessage;
                ErrorMessage = MapServerError(original);
                ServerErrorDetail = string.Equals(ErrorMessage, original, StringComparison.Ordinal)
                    ? null
                    : original;
                ClientIssues = [new ClientIssue(-1, ErrorMessage ?? "These settings could not be saved.")];
                return;
            }

            Editor = FromDto(result.Policy ?? ToDto(Editor));
            CaptureSnapshot();
            ExpandedBand = null;
            ClientIssues = [];
            StatusMessage = "Approval settings saved.";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task LoadLookupsAsync()
    {
        try
        {
            var result = await Approval.GetLookupsAsync();
            if (!result.Succeeded)
            {
                Users = [];
                Roles = [];
            }
            else
            {
                Users = result.Users ?? [];
                Roles = result.Roles ?? [];
            }
        }
        catch
        {
            Users = [];
            Roles = [];
        }

        await LoadCurrenciesAsync();
    }

    private async Task LoadCurrenciesAsync()
    {
        try
        {
            Currencies = await SupplierLookups.ListCurrenciesForAssignmentAsync() ?? [];
            _currencyLookupFailed = false;
        }
        catch
        {
            Currencies = [];
            _currencyLookupFailed = true;
        }
    }

    private async Task LoadAsync(string docType)
    {
        IsLoading = true;
        ErrorMessage = null;
        ServerErrorDetail = null;
        ClientIssues = [];
        ExpandedBand = null;
        PolicyConfirmVisible = false;
        RemoveConfirmVisible = false;
        ActiveDocType = docType;
        ActiveTabIndex = string.Equals(docType, ProcurementApprovalDocTypes.Po, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var result = await Approval.GetPolicyAsync(docType);
        if (!result.Succeeded)
        {
            ErrorMessage = MapServerError(result.ErrorMessage);
            ServerErrorDetail = string.Equals(ErrorMessage, result.ErrorMessage, StringComparison.Ordinal)
                ? null
                : result.ErrorMessage;
            Editor = null;
            _snapshot = string.Empty;
            _loadedEnabled = false;
            _loadedVersion = 0;
            _loadedLegacyDraftMode = ProcurementApprovalLegacyModes.RequireGoingForward;
        }
        else
        {
            Editor = FromDto(result.Policy ?? new ProcurementApprovalPolicyDto { DocType = docType });
            CaptureSnapshot();
        }

        IsLoading = false;
    }

    private void CaptureSnapshot()
    {
        if (Editor is null)
        {
            _snapshot = string.Empty;
            _loadedEnabled = false;
            _loadedVersion = 0;
            _loadedLegacyDraftMode = ProcurementApprovalLegacyModes.RequireGoingForward;
            return;
        }

        _snapshot = Serialize(Editor);
        _loadedEnabled = Editor.Enabled;
        _loadedVersion = Editor.Version;
        _loadedLegacyDraftMode = Editor.LegacyDraftMode;
    }

    private static string Serialize(PolicyEditor editor) => JsonSerializer.Serialize(editor, SnapshotOptions);

    private void ExpandFirstIssue()
    {
        if (Editor is null)
        {
            return;
        }

        var firstBandIssue = ClientIssues.FirstOrDefault(issue => issue.BandIndex >= 0);
        if (firstBandIssue.BandIndex >= 0 && firstBandIssue.BandIndex < Editor.Bands.Count)
        {
            ExpandedBand = Editor.Bands[firstBandIssue.BandIndex];
        }
    }

    private void RemoveBand(BandEditor band)
    {
        if (Editor is null)
        {
            return;
        }

        Editor.Bands.Remove(band);
        if (ReferenceEquals(ExpandedBand, band))
        {
            ExpandedBand = null;
        }
    }

    private static void RemoveLevel(BandEditor band, LevelEditor level) =>
        band.Levels.Remove(level);

    private string PreferredNewCurrency()
    {
        if (Currencies.Any(row => string.Equals(row.Code, DefaultCurrencyCode, StringComparison.OrdinalIgnoreCase)))
        {
            return DefaultCurrencyCode;
        }

        return string.Empty;
    }

    private static BandEditor CreateBand(string currency) =>
        new()
        {
            Currency = currency,
            MinInclusive = 0m,
            Levels = [new LevelEditor { Sequence = 1, MatchMode = ProcurementApprovalMatchModes.Any }]
        };

    public static PolicyEditor FromDto(ProcurementApprovalPolicyDto policy) =>
        new()
        {
            DocType = policy.DocType,
            Version = policy.Version,
            RowVersion = policy.RowVersion ?? [],
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

    public static ProcurementApprovalPolicyDto ToDto(PolicyEditor editor) =>
        new()
        {
            DocType = editor.DocType,
            Version = editor.Version,
            RowVersion = editor.RowVersion ?? [],
            Enabled = editor.Enabled,
            LegacyDraftMode = editor.LegacyDraftMode,
            Bands = editor.Bands.Select(band => new ProcurementApprovalBandDto
            {
                Currency = band.Currency,
                MinInclusive = band.MinInclusive,
                MaxExclusive = band.MaxExclusive,
                NotRequired = band.NotRequired,
                Levels = band.Levels.Select((level, index) => new ProcurementApprovalLevelDto
                {
                    Sequence = index + 1,
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

    public static string FormatAmount(decimal value)
    {
        var scale = (decimal.GetBits(value)[3] >> 16) & 0x7F;
        var decimals = Math.Max(2, scale);
        return value.ToString("N" + decimals, AmountCulture);
    }

    public static string FormatBandRange(string? currency, decimal minInclusive, decimal? maxExclusive)
    {
        var code = string.IsNullOrWhiteSpace(currency) ? "—" : currency.Trim().ToUpperInvariant();
        if (maxExclusive is null)
        {
            return $"{code} {FormatAmount(minInclusive)} and above";
        }

        if (minInclusive == 0m)
        {
            return $"Below {code} {FormatAmount(maxExclusive.Value)}";
        }

        return $"{code} {FormatAmount(minInclusive)} to below {FormatAmount(maxExclusive.Value)}";
    }

    public static string LevelTitle(int index) =>
        index switch
        {
            0 => "Step 1 — First approval",
            1 => "Step 2 — Second approval",
            2 => "Step 3 — Final approval",
            _ => $"Step {index + 1}"
        };

    public static string? FormatPreview(PolicyEditor editor, decimal sampleAmount = PreviewSampleAmount)
    {
        var currency = editor.Bands
            .Select(band => band.Currency?.Trim())
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
        if (string.IsNullOrWhiteSpace(currency))
        {
            return null;
        }

        var bands = editor.Bands
            .Where(band => string.Equals(band.Currency?.Trim(), currency, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (bands.Count == 0)
        {
            return null;
        }

        var amount = sampleAmount;
        var band = bands.FirstOrDefault(item =>
            amount >= item.MinInclusive
            && (item.MaxExclusive is null || amount < item.MaxExclusive.Value));
        if (band is null)
        {
            band = bands[0];
            amount = band.MinInclusive;
        }

        var amountText = $"{currency.ToUpperInvariant()} {FormatAmount(amount)}";
        if (band.NotRequired)
        {
            return $"Preview only (not the live result): {amountText} → no approval needed";
        }

        if (band.Levels.Count == 0)
        {
            return $"Preview only (not the live result): {amountText} → needs an approver";
        }

        var steps = string.Join(" then ", Enumerable.Range(1, band.Levels.Count).Select(n => $"Step {n}"));
        return $"Preview only (not the live result): {amountText} → {steps}";
    }

    public static string MapServerError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "These settings could not be saved. Please check the rules and try again.";
        }

        if (string.Equals(message, "Enable approval only after amount bands are configured.", StringComparison.Ordinal))
        {
            return "Add at least one approval rule before turning approval on.";
        }

        if (string.Equals(message, "Amount bands cannot start below zero.", StringComparison.Ordinal))
        {
            return "Amount from cannot be below zero.";
        }

        if (string.Equals(message, "Each amount band upper limit must be greater than its lower limit.", StringComparison.Ordinal))
        {
            return "‘Amount below’ must be greater than ‘Amount from’.";
        }

        if (string.Equals(message, "Only the last amount band for a currency may be unbounded.", StringComparison.Ordinal))
        {
            return "Only the last rule for a currency can have no upper limit.";
        }

        if (string.Equals(message, "Each required band must have 1 to 3 approval levels.", StringComparison.Ordinal))
        {
            return "Each rule that needs approval must have 1 to 3 approval steps.";
        }

        if (string.Equals(message, "Each approval level must have at least one user or role.", StringComparison.Ordinal))
        {
            return "Choose at least one person or role for every approval step.";
        }

        if (string.Equals(message, "These approval settings were changed by another user. Reload and try again.", StringComparison.Ordinal))
        {
            return "Someone else saved these approval settings. Reload and try again.";
        }

        var gap = Regex.Match(message, @"^Currency (.+) has a gap or overlap in amount bands\.$");
        if (gap.Success)
        {
            return $"The {gap.Groups[1].Value} amount ranges leave a gap or overlap. Please check where one rule ends and the next begins.";
        }

        var starting = Regex.Match(message, @"^Currency (.+) must include a band starting at 0\.00\.$");
        if (starting.Success)
        {
            return $"The {starting.Groups[1].Value} rules must include an amount starting at 0.";
        }

        return message;
    }

    public static List<ClientIssue> CollectIssues(PolicyEditor editor)
    {
        var issues = new List<ClientIssue>();
        if (editor.Enabled && editor.Bands.Count == 0)
        {
            issues.Add(new ClientIssue(-1, "Add at least one approval rule before turning approval on."));
            return issues;
        }

        for (var i = 0; i < editor.Bands.Count; i++)
        {
            var band = editor.Bands[i];
            if (string.IsNullOrWhiteSpace(band.Currency))
            {
                issues.Add(new ClientIssue(i, "Choose a currency for this rule."));
            }

            if (band.MinInclusive < 0m)
            {
                issues.Add(new ClientIssue(i, "Amount from cannot be below zero."));
            }

            if (band.MaxExclusive is { } max && max <= band.MinInclusive)
            {
                issues.Add(new ClientIssue(i, "‘Amount below’ must be greater than ‘Amount from’."));
            }

            if (!band.NotRequired)
            {
                if (band.Levels.Count is < 1 or > 3)
                {
                    issues.Add(new ClientIssue(i, "Each rule that needs approval must have 1 to 3 approval steps."));
                }
                else
                {
                    for (var levelIndex = 0; levelIndex < band.Levels.Count; levelIndex++)
                    {
                        if (band.Levels[levelIndex].Assignees.Count == 0)
                        {
                            issues.Add(new ClientIssue(i, $"{LevelTitle(levelIndex)} needs at least one person or role."));
                        }
                    }
                }
            }
        }

        foreach (var group in editor.Bands
            .Select((band, index) => (Band: band, Index: index))
            .Where(item => !string.IsNullOrWhiteSpace(item.Band.Currency))
            .GroupBy(item => item.Band.Currency.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(item => item.Band.MinInclusive).ToList();
            decimal cursor = 0m;
            for (var i = 0; i < ordered.Count; i++)
            {
                var item = ordered[i];
                var band = item.Band;
                if (i == 0 && band.MinInclusive != 0m)
                {
                    issues.Add(new ClientIssue(item.Index, $"The {group.Key} rules must include an amount starting at 0."));
                }

                if (i > 0 && band.MinInclusive != cursor)
                {
                    issues.Add(new ClientIssue(item.Index, $"The {group.Key} amount ranges leave a gap or overlap. Please check where one rule ends and the next begins."));
                }

                if (band.MaxExclusive is { } max)
                {
                    if (max > band.MinInclusive)
                    {
                        cursor = max;
                    }
                }
                else if (i != ordered.Count - 1)
                {
                    issues.Add(new ClientIssue(item.Index, "Only the last rule for a currency can have no upper limit."));
                }
            }
        }

        return issues;
    }

    public static bool BandHasSignificantData(BandEditor band) =>
        band.MinInclusive != 0m
        || band.MaxExclusive is not null
        || band.NotRequired
        || band.Levels.Count > 1
        || band.Levels.Any(level => level.Assignees.Count > 0);

    protected sealed record LegacyModeOption(string Key, string Name);

    protected sealed record MatchModeOption(string Key, string Name);

    protected sealed record AssigneeKindOption(string Key, string Name);

    protected sealed record RequirementOption(bool NotRequired, string Name);

    public readonly record struct ClientIssue(int BandIndex, string Message);

    private enum RemoveKind
    {
        Band,
        Level
    }

    private readonly record struct RemoveTarget(RemoveKind Kind, BandEditor? Band, LevelEditor? Level);

    public sealed class PolicyEditor
    {
        public string DocType { get; set; } = ProcurementApprovalDocTypes.Pr;
        public int Version { get; set; }
        public byte[] RowVersion { get; set; } = [];
        public bool Enabled { get; set; }
        public string LegacyDraftMode { get; set; } = ProcurementApprovalLegacyModes.RequireGoingForward;
        public List<BandEditor> Bands { get; set; } = [];
    }

    public sealed class BandEditor
    {
        [JsonIgnore]
        public Guid UiKey { get; set; } = Guid.NewGuid();

        public string Currency { get; set; } = string.Empty;
        public decimal MinInclusive { get; set; }
        public decimal? MaxExclusive { get; set; }
        public bool NotRequired { get; set; }
        public List<LevelEditor> Levels { get; set; } = [];
    }

    public sealed class LevelEditor
    {
        [JsonIgnore]
        public Guid UiKey { get; set; } = Guid.NewGuid();

        public int Sequence { get; set; } = 1;
        public string MatchMode { get; set; } = ProcurementApprovalMatchModes.Any;
        public List<ProcurementApprovalLevelAssigneeDto> Assignees { get; set; } = [];

        [JsonIgnore]
        public string PendingKind { get; set; } = ProcurementApprovalAssigneeKinds.User;

        [JsonIgnore]
        public string? PendingRefCode { get; set; }
    }
}
