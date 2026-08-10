namespace ErpWeb.Core.Menus;

public static class PermissionCodes
{
    public const string Access = "ACCESS";
    public const string Add = "ADD";
    public const string Edit = "EDIT";
    public const string Delete = "DELETE";
    public const string Print = "PRINT";
    public const string Post = "POST";
    public const string Rollback = "ROLLBACK";
    public const string Approve = "APPROVE";
    public const string Reject = "REJECT";
    public const string Cancel = "CANCEL";
    public const string Void = "VOID";
    public const string Reverse = "REVERSE";
    public const string Export = "EXPORT";
    public const string Import = "IMPORT";
    public const string Email = "EMAIL";
    public const string Submit = "SUBMIT";
    public const string Close = "CLOSE";
    public const string Reopen = "REOPEN";
    public const string ViewCost = "VIEW_COST";
    public const string ViewProfit = "VIEW_PROFIT";

    /// <summary>All known permission codes (ADMIN UI / GetPermissionsAsync).</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Access, Add, Edit, Delete, Print, Post, Rollback, Approve, Reject, Cancel,
        Void, Reverse, Export, Import, Email, Submit, Close, Reopen, ViewCost, ViewProfit
    };
}
