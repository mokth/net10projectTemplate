using System.Collections.Concurrent;

namespace ErpWeb.Core.Settings;

/// <summary>
/// Epoch counters that make cache invalidation total and lock-free.
///
/// <para>
/// <c>IMemoryCache</c> cannot enumerate keys by prefix, so "evict every affected snapshot" cannot be
/// implemented reliably by removal — a partial eviction would leave a stale entry that no test catches
/// until a customer acts on the wrong value. Instead the counters form part of every cache key, so
/// bumping one makes the affected snapshots unreachable atomically and cannot half-succeed. The
/// abandoned entries simply expire.
/// </para>
///
/// <list type="bullet">
/// <item>a GLOBAL write bumps the global counter — a global row is the last fallback for every tenant,
/// so every snapshot in the process is affected;</item>
/// <item>a COMPANY write bumps that company's counter, which also invalidates that company's BRANCH
/// snapshots, because a branch snapshot may have fallen back to the company value;</item>
/// <item>a BRANCH write bumps only that company + branch.</item>
/// </list>
///
/// <para>Registered as a singleton: the counters must be shared across every scoped service instance.</para>
/// </summary>
public sealed class AppSettingCacheVersions
{
    private long _global;
    private readonly ConcurrentDictionary<string, long> _company = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _branch = new(StringComparer.OrdinalIgnoreCase);

    public void BumpGlobal() => Interlocked.Increment(ref _global);

    public void BumpCompany(string companyCode)
    {
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            BumpGlobal();
            return;
        }

        _company.AddOrUpdate(companyCode.Trim(), 1, static (_, current) => current + 1);
    }

    public void BumpBranch(string companyCode, string branchCode)
    {
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            BumpGlobal();
            return;
        }

        if (string.IsNullOrWhiteSpace(branchCode))
        {
            BumpCompany(companyCode);
            return;
        }

        _branch.AddOrUpdate(BranchKey(companyCode, branchCode), 1, static (_, current) => current + 1);
    }

    /// <summary>The version stamp that forms part of a cache key for one tenant target.</summary>
    public string Stamp(string? companyCode, string? branchCode)
    {
        var global = Interlocked.Read(ref _global);

        var company = 0L;
        if (!string.IsNullOrWhiteSpace(companyCode))
        {
            _company.TryGetValue(companyCode.Trim(), out company);
        }

        var branch = 0L;
        if (!string.IsNullOrWhiteSpace(companyCode) && !string.IsNullOrWhiteSpace(branchCode))
        {
            _branch.TryGetValue(BranchKey(companyCode, branchCode), out branch);
        }

        return $"{global}.{company}.{branch}";
    }

    private static string BranchKey(string companyCode, string branchCode) =>
        $"{companyCode.Trim()}|{branchCode.Trim()}";
}
