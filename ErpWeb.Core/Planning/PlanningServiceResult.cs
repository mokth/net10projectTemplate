namespace ErpWeb.Core.Planning;

public enum PlanningErrorCode
{
    None = 0,
    DuplicateCode,
    ValidationFailed,
    DependencyExists,
    ConcurrencyConflict,
    TenantScopeError,
    PermissionDenied,
    ImportTemplateInvalid,
    ImportValidationFailed,
    RelocationFailed,
    RelocationBlocked
}

public sealed class ValidationIssue
{
    public PlanningErrorCode Code { get; init; } = PlanningErrorCode.ValidationFailed;
    public string Message { get; init; } = string.Empty;
    public string? ClientNodeId { get; init; }
    public string? FieldName { get; init; }
    public string? BusinessKey { get; init; }
    public string? ImportSheet { get; init; }
    public int? ImportRow { get; init; }
}

public sealed class DependencyReference
{
    public string SourceTable { get; init; } = string.Empty;
    public string BusinessKey { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

public class PlanningServiceResult
{
    public bool Succeeded { get; init; }
    public PlanningErrorCode ErrorCode { get; init; } = PlanningErrorCode.None;
    public string? Message { get; init; }
    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];
    public IReadOnlyList<DependencyReference> Dependencies { get; init; } = [];

    public static PlanningServiceResult Ok(string? message = null) =>
        new() { Succeeded = true, Message = message };

    public static PlanningServiceResult Fail(
        PlanningErrorCode code,
        string message,
        IEnumerable<ValidationIssue>? issues = null,
        IEnumerable<DependencyReference>? dependencies = null) =>
        new()
        {
            Succeeded = false,
            ErrorCode = code,
            Message = message,
            Issues = issues?.ToList() ?? [],
            Dependencies = dependencies?.ToList() ?? []
        };

    public static PlanningServiceResult From(PlanningServiceResult other) =>
        new()
        {
            Succeeded = other.Succeeded,
            ErrorCode = other.ErrorCode,
            Message = other.Message,
            Issues = other.Issues,
            Dependencies = other.Dependencies
        };

    public static PlanningServiceResult PermissionDenied(string menuCode) =>
        Fail(PlanningErrorCode.PermissionDenied, $"Permission denied for {menuCode}.");

    public static PlanningServiceResult TenantRequired() =>
        Fail(PlanningErrorCode.TenantScopeError, "Tenant scope is required.");
}

public sealed class PlanningServiceResult<T> : PlanningServiceResult
{
    public T? Value { get; init; }

    public static PlanningServiceResult<T> Ok(T value, string? message = null) =>
        new() { Succeeded = true, Value = value, Message = message };

    public new static PlanningServiceResult<T> Fail(
        PlanningErrorCode code,
        string message,
        IEnumerable<ValidationIssue>? issues = null,
        IEnumerable<DependencyReference>? dependencies = null) =>
        new()
        {
            Succeeded = false,
            ErrorCode = code,
            Message = message,
            Issues = issues?.ToList() ?? [],
            Dependencies = dependencies?.ToList() ?? []
        };

    public static PlanningServiceResult<T> Fail(
        PlanningErrorCode code,
        string message,
        T value,
        IEnumerable<ValidationIssue>? issues = null,
        IEnumerable<DependencyReference>? dependencies = null) =>
        new()
        {
            Succeeded = false,
            ErrorCode = code,
            Message = message,
            Value = value,
            Issues = issues?.ToList() ?? [],
            Dependencies = dependencies?.ToList() ?? []
        };

    public static PlanningServiceResult<T> From(PlanningServiceResult other) =>
        new()
        {
            Succeeded = other.Succeeded,
            ErrorCode = other.ErrorCode,
            Message = other.Message,
            Issues = other.Issues,
            Dependencies = other.Dependencies
        };
}
