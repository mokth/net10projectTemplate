using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ErpWeb.Model.Entities.Purchase;

namespace ErpWeb.Core.Purchase;

public static class ProcurementApprovalFingerprint
{
    public static string ForPr(PoPr header)
    {
        var sb = new StringBuilder();
        sb.Append(header.PrType).Append('|')
            .Append(header.DeptCode).Append('|')
            .Append(header.ProjId).Append('|')
            .Append(header.LocationCode).Append('|');
        foreach (var d in header.Details.OrderBy(x => x.Line))
        {
            sb.Append(d.Line).Append(',')
                .Append(d.ICode).Append(',')
                .Append(F(d.PurchaseQty)).Append(',')
                .Append(F(d.UnitPrice)).Append(',')
                .Append(F(d.Amount)).Append(',')
                .Append(d.Currency).Append(',')
                .Append(d.TaxGroup).Append(',')
                .Append(F(d.TaxAmount)).Append(',')
                .Append(d.IsInclusive ? '1' : '0').Append(',')
                .Append(d.VendorCd).Append(',')
                .Append(d.PaymentTerm).Append(',')
                .Append(d.BuyingTerm).Append(';');
        }

        return Hash(sb.ToString());
    }

    public static string ForPo(PoOrder header)
    {
        var sb = new StringBuilder();
        sb.Append(header.VendCode).Append('|')
            .Append(header.CurCode).Append('|')
            .Append(header.TermCode).Append('|')
            .Append(header.BuyingTerm).Append('|')
            .Append(F(header.Discount)).Append('|')
            .Append(header.TaxGrpCode).Append('|')
            .Append(header.ShipCode).Append('|')
            .Append(header.ShipName).Append('|');
        foreach (var d in header.Details.OrderBy(x => x.Line))
        {
            sb.Append(d.Line).Append(',')
                .Append(d.ICode).Append(',')
                .Append(F(d.PoPurQty)).Append(',')
                .Append(F(d.PoUnitPrice)).Append(',')
                .Append(F(d.NetAmount)).Append(',')
                .Append(F(d.TaxAmount)).Append(',')
                .Append(F(d.ItemDiscount)).Append(',')
                .Append(d.DiscountType).Append(',')
                .Append(F(d.ItemDiscount1)).Append(',')
                .Append(d.DiscountType1).Append(',')
                .Append(d.PrNo).Append(',')
                .Append(d.PrLineNo).Append(';');
        }

        return Hash(sb.ToString());
    }

    public static (decimal Gross, decimal Taxes, decimal Total, string Currency) TotalsForPr(PoPr header)
    {
        var currency = header.Details
            .Select(x => (x.Currency ?? string.Empty).Trim())
            .FirstOrDefault(x => x.Length > 0) ?? string.Empty;
        var sums = PoPrCalc.SumTotals(header.Details.Select(x =>
        {
            var amount = x.IsInclusive ? x.Amount - x.TaxAmount : x.Amount;
            return (amount, x.TaxAmount);
        }));
        return (sums.Gross, sums.Taxes, sums.Total, currency);
    }

    public static (decimal Gross, decimal Taxes, decimal Total, string Currency) TotalsForPo(PoOrder header)
    {
        var sums = PoOrderCalc.SumTotals(header.Details.Select(x => (x.NetAmount, x.TaxAmount)));
        return (sums.Gross, sums.Taxes, sums.Total, (header.CurCode ?? string.Empty).Trim());
    }

    private static string F(decimal value) =>
        value.ToString("0.00####", CultureInfo.InvariantCulture);

    private static string Hash(string canonical)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes);
    }
}
