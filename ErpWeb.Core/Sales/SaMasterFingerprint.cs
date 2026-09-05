using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Deterministic concurrency fingerprints for sales masters without RowVersion.
/// Same helper for UI (after Get) and service (tracked reload in Save).
/// </summary>
public static class SaMasterFingerprint
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string PaymentTerm(SaPaymentTermEditVm vm) =>
        Hash(new PaymentTermPayload(vm.Desc, vm.Days, vm.IsActive));

    public static string SalesRep(SaSalesRepEditVm vm) =>
        Hash(new SalesRepPayload(
            vm.Name,
            vm.Address1,
            vm.Address2,
            vm.Address3,
            vm.City,
            vm.State,
            vm.PostalCode,
            vm.Country,
            vm.Tel,
            vm.Mobile,
            vm.Email,
            vm.CommissionRate,
            vm.IsActive));

    public static string TaxGroup(SaTaxGroupEditVm vm) =>
        Hash(new TaxGroupPayload(vm.Desc, vm.Percentage));

    private static string Hash<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed record PaymentTermPayload(string? Desc, int? Days, bool IsActive);

    private sealed record SalesRepPayload(
        string? Name,
        string? Address1,
        string? Address2,
        string? Address3,
        string? City,
        string? State,
        string? PostalCode,
        string? Country,
        string? Tel,
        string? Mobile,
        string? Email,
        decimal? CommissionRate,
        bool IsActive);

    private sealed record TaxGroupPayload(string? Desc, decimal Percentage);
}
