using ErpWeb.Core.Purchase;
using ErpWeb.Model.Data;

namespace ErpWeb.Tests.Procurement.Transaction;

/// <summary>
/// Explicit policy-OFF fixture for tests. Not production DI.
/// Production composition must resolve <see cref="IProcurementApprovalGuard"/>
/// to <see cref="ProcurementApprovalService"/>.
/// </summary>
internal sealed class TestDisabledProcurementApprovalGuard : IProcurementApprovalGuard
{
    public static TestDisabledProcurementApprovalGuard Instance { get; } = new();

    public Task<string?> AssertPrConvertibleAsync(
        AppDbContext db, string companyCode, string branchCode, string prNo, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> AssertPoReceivableAsync(
        AppDbContext db, string companyCode, string branchCode, string poNo, short poRelNo, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> AssertPoCloseableAsync(
        AppDbContext db, string companyCode, string branchCode, string poNo, short poRelNo, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> AssertDocumentMaterialEditAsync(
        AppDbContext db, string companyCode, string branchCode, string docType, string docNo, short docRev,
        string newFingerprint, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> AssertAttachmentMutationAsync(
        AppDbContext db, string companyCode, string branchCode, string docType, string docNo, short docRev,
        CancellationToken cancellationToken = default, bool deleting = false) =>
        Task.FromResult<string?>(null);

    public Task<string?> ClosePendingOnDocumentCancelAsync(
        AppDbContext db, string companyCode, string branchCode, string docType, string docNo, short docRev,
        string actorUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task LockInstancesAsync(
        AppDbContext db, string companyCode, string branchCode, string docType,
        IEnumerable<(string DocNo, short DocRev)> keys, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<string?> LockPublishedPolicyAsync(
        AppDbContext db, string companyCode, string docType, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<bool> IsPrVisibleForPoAsync(
        AppDbContext db, string companyCode, string branchCode, string prNo, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<string?> EvaluatePrConvertibleReadOnlyAsync(
        AppDbContext db, string companyCode, string branchCode, string prNo, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<IReadOnlySet<string>> FilterConvertiblePrNosReadOnlyAsync(
        AppDbContext db, string companyCode, string branchCode, IReadOnlyCollection<string> prNos,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<string>>(
            prNos.ToHashSet(StringComparer.OrdinalIgnoreCase));

    public Task<bool> IsPoVisibleForGrAsync(
        AppDbContext db, string companyCode, string branchCode, string poNo, short poRelNo, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<bool> CanReadDocumentAsync(
        AppDbContext db, string companyCode, string branchCode, string docType, string docNo, short docRev,
        string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
