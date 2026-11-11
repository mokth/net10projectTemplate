using System.Data.Common;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ErpWeb.Tests.Production.Transaction;

// Stops the WAC writer after it has read receipt authority but before it
// reloads the detail used to create the posted Inventory history.
internal sealed class WacReceiptPostingBarrier : DbCommandInterceptor
{
    private int _armed;
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _editAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _observed = new();

    internal Task Reached => _reached.Task;
    internal Task EditAttempt => _editAttempt.Task;
    internal string Diagnostics => string.Join("\n---\n", _observed.TakeLast(8));
    internal void Arm() => Interlocked.Exchange(ref _armed, 1);
    internal void Release() => _release.TrySetResult();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (_reached.Task.IsCompleted && !_release.Task.IsCompleted
            && command.CommandText.Contains("dbo.IvTrxBatch WITH (UPDLOCK, HOLDLOCK)", StringComparison.OrdinalIgnoreCase))
            _editAttempt.TrySetResult();
        if (Volatile.Read(ref _armed) == 1 && command.CommandText.Contains("[IvTrxBatchDetail]", StringComparison.OrdinalIgnoreCase))
            _observed.Enqueue(command.CommandText);
        if (Volatile.Read(ref _armed) == 1
            && command.CommandText.Contains("[IvTrxBatchDetail]", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("authority_DetailId", StringComparison.OrdinalIgnoreCase)
            && Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _reached.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        return result;
    }
}
