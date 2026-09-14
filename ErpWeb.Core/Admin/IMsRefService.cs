using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities;

namespace ErpWeb.Core.Admin;

// ── Department ────────────────────────────────────────────────────────────────

public sealed class MsDeptListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? ManagerEmpId { get; init; }
    public string? GlCode { get; init; }
    public bool IsActive { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class MsDeptEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? ManagerEmpId { get; set; }
    public string? GlCode { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Remarks { get; set; }
    public byte[]? RowVersion { get; set; }
}

// ── Project ───────────────────────────────────────────────────────────────────

public sealed class MsProjectListRow
{
    public string Code { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? CustCode { get; init; }
    public string? DeptCode { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }

    /// <summary>ACTIVE / CLOSED. "Active" is derived from this — MsProject has no IsActive column.</summary>
    public string Status { get; init; } = MsProjectStatus.Active;

    public decimal? BudgetAmnt { get; init; }
    public bool IsActive => !string.Equals(Status, MsProjectStatus.Closed, StringComparison.OrdinalIgnoreCase);
    public byte[] RowVersion { get; init; } = [];
}

public sealed class MsProjectEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? CustCode { get; set; }
    public string? DeptCode { get; set; }
    public string? ManagerEmpId { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? CloseDate { get; set; }
    public string Status { get; set; } = MsProjectStatus.Active;
    public decimal? BudgetAmnt { get; set; }
    public string? Remarks { get; set; }
    public byte[]? RowVersion { get; set; }
}

// ── Master-UI-only lookup bundle ──────────────────────────────────────────────

/// <summary>
/// Active Department + Project lists for the master UI (Project's default-department and
/// customer selectors). This is NOT a transaction lookup path — transaction services query
/// the DbSets directly.
/// </summary>
public sealed class MsRefLookupBundle
{
    public IReadOnlyList<IvCodeLookupRow> Departments { get; init; } = [];
    public IReadOnlyList<IvCodeLookupRow> Projects { get; init; } = [];

    /// <summary>Company-scoped, matching SaCust's company scope.</summary>
    public IReadOnlyList<IvCodeLookupRow> Customers { get; init; } = [];
}

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Department + Project reference masters. Company + branch scoped (RequireBranchScopeAsync),
/// under the ADMIN_DEPT / ADMIN_PROJECT menus.
/// </summary>
public interface IMsRefService
{
    // Department
    Task<IvMasterOperationResult<IReadOnlyList<MsDeptListRow>>> ListDepartmentsAsync(
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<MsDeptEditVm>> GetDepartmentAsync(
        string code, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<MsDeptEditVm>> SaveDepartmentAsync(
        MsDeptEditVm model, bool isNew, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> SetDepartmentActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default);

    Task<DeleteCheckResult> CanDeleteDepartmentsAsync(
        IReadOnlyList<string> codes, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> DeleteDepartmentsAsync(
        IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default);

    // Project
    Task<IvMasterOperationResult<IReadOnlyList<MsProjectListRow>>> ListProjectsAsync(
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<MsProjectEditVm>> GetProjectAsync(
        string code, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<MsProjectEditVm>> SaveProjectAsync(
        MsProjectEditVm model, bool isNew, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> SetProjectActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default);

    Task<DeleteCheckResult> CanDeleteProjectsAsync(
        IReadOnlyList<string> codes, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> DeleteProjectsAsync(
        IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default);

    /// <summary>Master UI only — never used by transaction services.</summary>
    Task<IvMasterOperationResult<MsRefLookupBundle>> ListActiveLookupsAsync(
        CancellationToken cancellationToken = default);
}
