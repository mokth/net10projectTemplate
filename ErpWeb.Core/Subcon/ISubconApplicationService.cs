namespace ErpWeb.Core.Subcon;

public interface ISubconApplicationService
{
    Task<SubconResult> GetPolicyAsync(CancellationToken cancellationToken = default);

    Task<SubconResult> SavePolicyAsync(SubconPolicySaveCommand command, CancellationToken cancellationToken = default);

    Task<SubconResult> GetWorkbenchAsync(SubconListQuery query, CancellationToken cancellationToken = default);

    Task<SubconResult> SearchAsync(SubconListQuery query, CancellationToken cancellationToken = default);

    Task<SubconResult> GetDispatchAsync(string docNo, CancellationToken cancellationToken = default);

    Task<SubconResult> SaveDispatchDraftAsync(SubconDispatchSaveCommand command, CancellationToken cancellationToken = default);

    Task<SubconResult> DeleteDispatchDraftAsync(SubconKeyCommand command, CancellationToken cancellationToken = default);

    Task<SubconResult> PostDispatchAsync(SubconKeyCommand command, CancellationToken cancellationToken = default);

    Task<SubconResult> RollbackDispatchAsync(SubconKeyCommand command, CancellationToken cancellationToken = default);

    Task<SubconResult> GetReceiptAsync(string docNo, CancellationToken cancellationToken = default);

    Task<SubconResult> SaveReceiptDraftAsync(SubconReceiptSaveCommand command, CancellationToken cancellationToken = default);

    Task<SubconResult> DeleteReceiptDraftAsync(SubconKeyCommand command, CancellationToken cancellationToken = default);

    Task<SubconResult> PostReceiptAsync(SubconKeyCommand command, CancellationToken cancellationToken = default);
}
