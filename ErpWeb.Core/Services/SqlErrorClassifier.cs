using Microsoft.Data.SqlClient;

namespace ErpWeb.Core.Services;

/// <summary>
/// One definition of "this storage failure means the write LOST A RACE, so the caller should reload and
/// retry" — previously written out three times, in slightly different and partly incompatible ways.
///
/// <para>
/// The two predicates are deliberately different, because they mean different things:
/// </para>
/// <list type="bullet">
/// <item><b>Serialization conflict</b> — a deadlock victim, a serialization failure or a lock timeout. The
/// transaction was rolled back by the engine and the safe answer is <c>Concurrency</c>, never a 500.</item>
/// <item><b>Unique violation</b> — the insert collided with an existing key. Also a race, but the caller
/// can say something more specific.</item>
/// </list>
///
/// <para>
/// BOTH check the structured error number first and only fall back to message matching. Message matching
/// alone is fragile (localisation, wrapping, wording changes) and was the reason the three copies
/// disagreed about which codes counted.
/// </para>
///
/// <para>
/// <b>Broadening, deliberate and documented:</b> consolidating added <c>1222</c> (lock request timeout) to
/// the numbering service's view and <c>3961/41301/41302/41325</c> to the sales view. Every one of those
/// codes means the same thing to a caller — the write lost a race — and previously one caller would have
/// surfaced it as an unhandled exception instead of "reload and try again".
/// </para>
/// </summary>
internal static class SqlErrorClassifier
{
    /// <summary>Deadlock victim, lock timeout, update conflict, and the snapshot-isolation conflicts.</summary>
    private static readonly int[] SerializationNumbers = [1205, 1222, 3960, 3961, 41301, 41302, 41325];

    /// <summary>Duplicate key in a unique index (2601) and unique constraint violation (2627).</summary>
    private static readonly int[] UniqueNumbers = [2601, 2627];

    public static bool IsSerializationConflict(Exception ex) =>
        Matches(ex, SerializationNumbers, ["deadlock", "serialization", "serializable"]);

    public static bool IsUniqueViolation(Exception ex) =>
        Matches(ex, UniqueNumbers, ["duplicate key", "UNIQUE constraint failed"]);

    private static bool Matches(Exception ex, int[] errorNumbers, string[] messageFragments)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql && errorNumbers.Contains(sql.Number))
            {
                return true;
            }

            var message = e.Message;
            foreach (var fragment in messageFragments)
            {
                if (message.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
