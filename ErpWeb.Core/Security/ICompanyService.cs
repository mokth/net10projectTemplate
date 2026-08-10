using ErpWeb.Model.Entities;

namespace ErpWeb.Core.Security;

public sealed class CompanyOperationResult
{

    public bool Succeeded { get; init; }

    public string? ErrorMessage { get; init; }

    public Company? Company { get; init; }

    public IReadOnlyList<Company> Companies { get; init; } = [];
    /// <summary>Set after a successful create that provisioned the first admin.</summary>

    public CompanyBootstrapResult? Bootstrap { get; init; }

    public static CompanyOperationResult Ok() =>
        new() { Succeeded = true };

    public static CompanyOperationResult Ok(Company company) =>
        new() { Succeeded = true, Company = company };

    public static CompanyOperationResult Ok(Company company, CompanyBootstrapResult bootstrap) =>
        new() { Succeeded = true, Company = company, Bootstrap = bootstrap };

    public static CompanyOperationResult Ok(IReadOnlyList<Company> companies) =>
        new() { Succeeded = true, Companies = companies };

    public static CompanyOperationResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public sealed class CompanyBootstrapResult
{

    public required string CompanyCode { get; init; }

    public required string AdminLoginId { get; init; }

    public required string BranchCode { get; init; }

    public required string LocationCode { get; init; }

    public required string TemplateCompanyCode { get; init; }
}

public interface ICompanyService
{
    Task<CompanyOperationResult> GetCompaniesAsync(CancellationToken cancellationToken = default);
    Task<CompanyOperationResult> GetCompanyAsync(int companyId, CancellationToken cancellationToken = default);
    Task<CompanyOperationResult> GetOwnCompanyAsync(CancellationToken cancellationToken = default);
    Task<CompanyOperationResult> AddCompanyAsync(
        Company company,
        CompanyBootstrapRequest bootstrap,
        CancellationToken cancellationToken = default);
    Task<CompanyOperationResult> UpdateCompanyAsync(Company company, CancellationToken cancellationToken = default);
    Task<CompanyOperationResult> DeleteCompaniesAsync(
        IReadOnlyCollection<int> companyIds,
        CancellationToken cancellationToken = default);
}
