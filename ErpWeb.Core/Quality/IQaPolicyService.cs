namespace ErpWeb.Core.Quality;

public interface IQaPolicyService
{
    Task<QaResult> GetAsync(CancellationToken cancellationToken = default);

    Task<QaResult> SaveAsync(QaPolicySaveCommand command, CancellationToken cancellationToken = default);
}
