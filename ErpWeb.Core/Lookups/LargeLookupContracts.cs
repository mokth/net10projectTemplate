namespace ErpWeb.Core.Lookups;

/// <summary>
/// Shared request for server-paged large ERP lookups (items, customers, suppliers, etc.).
/// Callers must never rely on an unbounded <see cref="Take"/>.
/// </summary>
public sealed class LargeLookupSearchRequest
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public string? SearchText { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = DefaultPageSize;

    public int NormalizedSkip => Math.Max(0, Skip);

    public int NormalizedTake =>
        Math.Clamp(Take <= 0 ? DefaultPageSize : Take, 1, MaxPageSize);
}

public sealed class LargeLookupPage<T>
{
    public IReadOnlyList<T> Rows { get; init; } = [];
    public int TotalCount { get; init; }
    public bool Succeeded { get; init; } = true;
    public string? ErrorMessage { get; init; }

    public static LargeLookupPage<T> Ok(IReadOnlyList<T> rows, int totalCount) =>
        new()
        {
            Succeeded = true,
            Rows = rows ?? [],
            TotalCount = Math.Max(0, totalCount)
        };

    public static LargeLookupPage<T> Fail(string message) =>
        new()
        {
            Succeeded = false,
            ErrorMessage = message,
            Rows = [],
            TotalCount = 0
        };
}

public sealed class LargeLookupResolveResult<T>
{
    public bool Succeeded { get; init; }
    public bool Ambiguous { get; init; }
    public string? ErrorMessage { get; init; }
    public T? Item { get; init; }

    public static LargeLookupResolveResult<T> Ok(T item) =>
        new() { Succeeded = true, Item = item };

    public static LargeLookupResolveResult<T> Fail(string message, bool ambiguous = false) =>
        new()
        {
            Succeeded = false,
            Ambiguous = ambiguous,
            ErrorMessage = message
        };
}
