namespace ErpWeb.UI.Sales.Transactions;

public sealed record SaDocSourceHeaderOption(
    string DocumentNo,
    string CustomerCode,
    string? CustomerName,
    DateTime DocumentDate,
    string Status);
