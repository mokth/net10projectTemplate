using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Purchase;

public interface IPoDeliveryFollowUpService
{
    Task<IvMasterOperationResult<IReadOnlyList<PoDeliveryFollowUpRow>>> ListAsync(
        string poNo,
        short poRelNo,
        short poLineNo,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<long>> AddAsync(
        PoDeliveryFollowUpRequest request,
        CancellationToken cancellationToken = default);
}
