using ErpWeb.Model.Entities;

namespace ErpWeb.Core.Security;

public sealed class BranchOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public Branch? Branch { get; init; }
    public IReadOnlyList<Branch> Branches { get; init; } = [];

    public static BranchOperationResult Ok() => new() { Succeeded = true };

    public static BranchOperationResult Ok(Branch branch) =>
        new() { Succeeded = true, Branch = branch };

    public static BranchOperationResult Ok(IReadOnlyList<Branch> branches) =>
        new() { Succeeded = true, Branches = branches };

    public static BranchOperationResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public interface IBranchService
{
    Task<BranchOperationResult> GetBranchesAsync(CancellationToken cancellationToken = default);
    Task<BranchOperationResult> AddBranchAsync(Branch branch, CancellationToken cancellationToken = default);
    Task<BranchOperationResult> UpdateBranchAsync(Branch branch, CancellationToken cancellationToken = default);
    Task<BranchOperationResult> DeleteBranchesAsync(
        IReadOnlyCollection<long> branchIds,
        CancellationToken cancellationToken = default);
}
