using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ErpWeb.Core.Numbering;

public sealed class RunningNumberService : IRunningNumberService
{
    private const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";

    public async Task<int> PeekNextAsync(
        AppDbContext db,
        string companyCode,
        string docKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var (company, key) = Normalize(companyCode, docKey);

        var last = await db.MsRunningNos
            .AsNoTracking()
            .Where(x => x.CompanyCode == company && x.DocKey == key)
            .Select(x => (int?)x.LastNo)
            .SingleOrDefaultAsync(cancellationToken);

        return (last ?? 0) + 1;
    }

    public async Task<int> GetNextAsync(
        AppDbContext db,
        string companyCode,
        string docKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var (company, key) = Normalize(companyCode, docKey);
        var isSqlServer = string.Equals(db.Database.ProviderName, SqlServerProvider, StringComparison.Ordinal);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            IDbContextTransaction? ownedTx = null;
            MsRunningNo? row = null;
            if (isSqlServer && db.Database.CurrentTransaction is null)
                ownedTx = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                row = await LoadForUpdateAsync(db, company, key, isSqlServer, cancellationToken);
                if (row is null)
                {
                    row = new MsRunningNo
                    {
                        CompanyCode = company,
                        DocKey = key,
                        LastNo = 1,
                        CreatedDate = DateTime.UtcNow
                    };
                    db.MsRunningNos.Add(row);
                    try
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    catch (DbUpdateException)
                    {
                        db.Entry(row).State = EntityState.Detached;
                        if (ownedTx is not null)
                            await ownedTx.RollbackAsync(cancellationToken);
                        continue;
                    }

                    if (ownedTx is not null)
                        await ownedTx.CommitAsync(cancellationToken);
                    return 1;
                }

                row.LastNo += 1;
                row.ModifiedDate = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                if (ownedTx is not null)
                    await ownedTx.CommitAsync(cancellationToken);
                return row.LastNo;
            }
            catch (Exception ex) when (isSqlServer && ownedTx is not null && IsRetryableSqlRace(ex))
            {
                if (row is not null)
                    db.Entry(row).State = EntityState.Detached;
                await ownedTx.RollbackAsync(cancellationToken);
            }
            finally
            {
                if (ownedTx is not null)
                    await ownedTx.DisposeAsync();
            }
        }

        throw new InvalidOperationException("Unable to allocate the next running number.");
    }

    private static bool IsRetryableSqlRace(Exception exception) =>
        exception switch
        {
            SqlException sql => sql.Number is 1205 or 2601 or 2627,
            DbUpdateException dbEx when dbEx.InnerException is not null => IsRetryableSqlRace(dbEx.InnerException),
            _ => false
        };

    private static async Task<MsRunningNo?> LoadForUpdateAsync(
        AppDbContext db,
        string company,
        string key,
        bool isSqlServer,
        CancellationToken cancellationToken)
    {
        if (isSqlServer)
        {
            return await db.MsRunningNos
                .FromSqlInterpolated($@"
                    SELECT * FROM MsRunningNo WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
                    WHERE CompanyCode = {company} AND DocKey = {key}")
                .SingleOrDefaultAsync(cancellationToken);
        }

        return await db.MsRunningNos
            .SingleOrDefaultAsync(x => x.CompanyCode == company && x.DocKey == key, cancellationToken);
    }

    private static (string Company, string Key) Normalize(string companyCode, string docKey)
    {
        var company = (companyCode ?? string.Empty).Trim().ToUpperInvariant();
        var key = (docKey ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(company))
        {
            throw new ArgumentException("Company code is required.", nameof(companyCode));
        }

        if (company.Length > 5)
        {
            throw new ArgumentException("Company code must be at most 5 characters.", nameof(companyCode));
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Document key is required.", nameof(docKey));
        }

        if (key.Length > 20)
        {
            throw new ArgumentException("Document key must be at most 20 characters.", nameof(docKey));
        }

        return (company, key);
    }
}
