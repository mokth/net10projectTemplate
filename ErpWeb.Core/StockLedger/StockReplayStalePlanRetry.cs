namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Bounded pre-lock replay retry helper for unrelated branch watermark drift.
/// Retries strictly outside the branch lock and transaction, and ONLY when:
/// 1. The attempt threw a retryable <see cref="StockLedgerErrorCodes.StaleDocument"/>
///    marked with <see cref="StockLedgerError.IsBranchWatermarkDrift"/> = true, AND
/// 2. The target document and its affected item/pile guard fingerprint are unchanged
///    (proving no draft edit or same-item concurrent mutation occurred).
/// </summary>
public static class StockReplayStalePlanRetry
{
    public const int DefaultMaxAttempts = 3;

    public static async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<string?>> captureGuardFingerprintAsync,
        Func<CancellationToken, Task<TResult>> attemptAsync,
        CancellationToken cancellationToken = default,
        int maxAttempts = DefaultMaxAttempts)
    {
        ArgumentNullException.ThrowIfNull(captureGuardFingerprintAsync);
        ArgumentNullException.ThrowIfNull(attemptAsync);
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        var initialGuard = await captureGuardFingerprintAsync(cancellationToken);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await attemptAsync(cancellationToken);
            }
            catch (StockLedgerException ex)
                when (attempt < maxAttempts
                    && ex.Error.Code == StockLedgerErrorCodes.StaleDocument
                    && ex.Error.Retryable
                    && ex.Error.IsBranchWatermarkDrift)
            {
                var currentGuard = await captureGuardFingerprintAsync(cancellationToken);
                if (initialGuard is null
                    || currentGuard is null
                    || !StringComparer.Ordinal.Equals(initialGuard, currentGuard))
                {
                    throw;
                }
            }
        }
    }
}
