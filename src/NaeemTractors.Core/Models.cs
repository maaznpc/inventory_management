namespace NaeemTractors.Core;

public sealed record Product(long Id, string Sku, string Name, string Category, string Brand,
    string TractorModel, string Unit, string Location, long MinimumStock, decimal SellingPrice,
    decimal AverageCost, long Stock, bool Active)
{
    public string StockStatus => !Active ? "Archived" : Stock == 0 ? "Out of stock" : Stock <= MinimumStock ? "Low stock" : "In stock";
    public string Display => $"{Sku} · {Name} — {Stock} {Unit}";
    public decimal StockValue => AverageCost * Stock;
}

public sealed record ProductInput(long Id, string Sku, string Name, string Category, string Brand,
    string TractorModel, string Unit, string Location, long MinimumStock, decimal SellingPrice, bool Active);
public sealed record SaleLine(long ProductId, long Quantity, decimal UnitPrice);
public sealed record SaleRequest(string Customer, string Phone, IReadOnlyList<SaleLine> Lines,
    decimal Discount, decimal TaxPercent, decimal Paid, string PaymentMethod, string Notes);
public sealed record Invoice(long Id, string Number, DateTime CreatedAt, string Customer, string Phone,
    decimal Subtotal, decimal Discount, decimal Tax, decimal Total, decimal Cost, decimal Paid, string Notes)
{
    public decimal Balance => Total - Paid;
    public decimal GrossProfit => Subtotal - Discount - Cost;
    public string Status => Balance == 0 ? "Paid" : Paid == 0 ? "Unpaid" : "Partial";
}
public sealed record InvoiceLine(string Sku, string Name, long Quantity, decimal UnitPrice, decimal UnitCost)
{
    public decimal Total => Quantity * UnitPrice;
}
public sealed record Payment(long Id, string InvoiceNumber, DateTime CreatedAt, string Customer,
    decimal Amount, string Method, string Reference);
public sealed record StockMovement(long Id, DateTime CreatedAt, string Sku, string Product,
    long Quantity, string Kind, decimal UnitCost, string Reference, string Supplier, string Notes);
public sealed record Expense(long Id, DateTime CreatedAt, string Category, string Description, decimal Amount);
public sealed record Dashboard(decimal SalesToday, decimal CollectedToday, decimal Outstanding,
    decimal StockValue, long ActiveProducts, long LowStockProducts, decimal GrossProfitToday, decimal ExpensesToday);
public sealed record PeriodReport(decimal Sales, decimal Discount, decimal Tax, decimal Cost,
    decimal Collections, decimal Expenses, long InvoiceCount)
{
    public decimal GrossProfit => Sales - Tax - Cost;
    public decimal OperatingProfit => GrossProfit - Expenses;
}
public sealed record ShopSettings(string Name, string Phone, string Address, string Currency);

public static class Money
{
    public static long ToMinor(decimal amount)
    {
        if (amount < 0 || amount > 1_000_000_000m)
            throw new ArgumentException("Amounts must be between 0 and 1,000,000,000.");
        if (decimal.Round(amount, 2, MidpointRounding.AwayFromZero) != amount)
            throw new ArgumentException("Enter money with at most two decimal places.");
        return checked((long)(amount * 100));
    }
    public static decimal FromMinor(long amount) => amount / 100m;
}
