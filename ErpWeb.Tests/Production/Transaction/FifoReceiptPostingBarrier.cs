using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ErpWeb.Tests.Production.Transaction;

// Pauses one SQL Server receipt immediately before its locked batch read.
// Used only to exercise a real draft edit between planning and revalidation.
internal sealed class FifoReceiptPostingBarrier : DbCommandInterceptor
{
    private int _armed;
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Reached => _reached.Task;
    internal void Arm() => Interlocked.Exchange(ref _armed, 1);
    internal void Release() => _release.TrySetResult();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _armed) == 1
            && command.CommandText.Contains("dbo.IvTrxBatch WITH (UPDLOCK, HOLDLOCK)", StringComparison.OrdinalIgnoreCase)
            && Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _reached.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        return result;
    }
}
