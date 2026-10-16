using NaeemTractors.Core;
using Xunit;

namespace NaeemTractors.Tests;

public sealed class InventoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),"naeem-tests-"+Guid.NewGuid());
    private readonly InventoryStore _store;
    public InventoryTests()
    {
        _store=new InventoryStore(Path.Combine(_directory,"inventory.db"));
        _store.Initialize();
    }
    private long Product(string sku="MF-001",decimal price=150m) => _store.SaveProduct(new(0,sku,"Oil filter","Filters","Millat","MF 240","piece","A-1",3,price,true));
    private long Stocked(string sku="MF-001",long qty=10,decimal cost=100m)
    { var id=Product(sku); _store.ReceiveStock(id,qty,cost,"Opening stock","","Count","Initial count"); return id; }
    private SaleRequest Sale(long id,long qty=2,decimal paid=300m,string customer="Walk in",decimal discount=0,decimal tax=0) =>
        new(customer,"03000000000",[new(id,qty,150m)],discount,tax,paid,"Cash","");

    [Fact] public void EmptyStoreHasNoFakeTransactions()
    {
        Assert.Empty(_store.FindProducts()); Assert.Empty(_store.FindInvoices());
        Assert.Equal(0,_store.Dashboard().Outstanding); Assert.Equal(0,_store.Dashboard().SalesToday);
    }
    [Fact] public void ProductPersistsAndCanBeSearchedByModelOrLocation()
    {
        Product(); var reopened=new InventoryStore(_store.DatabasePath); reopened.Initialize();
        Assert.Single(reopened.FindProducts("MF 240")); Assert.Single(reopened.FindProducts("A-1"));
        Assert.Empty(reopened.FindProducts("%"));
    }
    [Fact] public void PartNumbersAreUniqueRegardlessOfCase()
    { Product(); Assert.Throws<ArgumentException>(()=>Product("mf-001")); }
    [Fact] public void ReceiptsRecalculateWeightedAverageAndPreserveHistory()
    {
        var id=Stocked(); _store.ReceiveStock(id,10,200,"Purchase","Supplier A","PUR-1","");
        var product=Assert.Single(_store.FindProducts());
        Assert.Equal(20,product.Stock); Assert.Equal(150m,product.AverageCost);
        Assert.Equal(3000m,product.StockValue); Assert.Equal(2,_store.Movements().Count);
    }
    [Fact] public void OpeningStockCannotBePostedTwice()
    {
        var id=Stocked();
        Assert.Throws<ArgumentException>(()=>_store.ReceiveStock(id,1,100,"Opening stock","","",""));
        Assert.Equal(10,Assert.Single(_store.FindProducts()).Stock);
    }
    [Fact] public void PurchaseRequiresSupplier()
    { var id=Product(); Assert.Throws<ArgumentException>(()=>_store.ReceiveStock(id,1,10,"Purchase","","","")); }
    [Fact] public void CreditSaleUpdatesStockCostDuesAndCollectionsSeparately()
    {
        var id=Stocked(); var saleId=_store.PostSale(Sale(id,paid:100,customer:"Ali"));
        var invoice=_store.GetInvoice(saleId);
        Assert.Equal("NT-000001",invoice.Number); Assert.Equal(300m,invoice.Total);
        Assert.Equal(200m,invoice.Cost); Assert.Equal(100m,invoice.GrossProfit);
        Assert.Equal(200m,invoice.Balance); Assert.Equal("Partial",invoice.Status);
        Assert.Equal(8,Assert.Single(_store.FindProducts()).Stock);
        Assert.Equal(100m,_store.Dashboard().CollectedToday);
        Assert.Equal(300m,_store.Dashboard().SalesToday);
        Assert.Equal(200m,_store.Dashboard().Outstanding);
        Assert.Equal(100m,Assert.Single(_store.InvoiceLines(saleId)).UnitCost);
    }
    [Fact] public void SaleCannotOversellAndLeavesNoPartialRecords()
    {
        var a=Stocked("A"); var b=Stocked("B",1);
        var request=new SaleRequest("Ali","",[new(a,1,150),new(b,2,150)],0,0,0,"Cash","");
        Assert.Throws<ArgumentException>(()=>_store.PostSale(request));
        Assert.Empty(_store.FindInvoices()); Assert.Empty(_store.Payments());
        Assert.Equal(10,_store.FindProducts("A").Single(p=>p.Sku=="A").Stock);
        Assert.Equal(2,_store.Movements().Count);
    }
    [Fact] public void CreditSaleRequiresCustomer()
    { var id=Stocked(); Assert.Throws<ArgumentException>(()=>_store.PostSale(Sale(id,paid:0,customer:" "))); }
    [Fact] public void RepeatedProductCannotBypassStockValidation()
    {
        var id=Stocked();
        Assert.Throws<ArgumentException>(()=>_store.PostSale(new("Ali","",[new(id,6,150),new(id,6,150)],0,0,0,"Cash","")));
        Assert.Equal(10,Assert.Single(_store.FindProducts()).Stock);
    }
    [Fact] public void PaymentsSettleInvoiceAndRejectOverpayment()
    {
        var id=_store.PostSale(Sale(Stocked(),paid:0,customer:"Ali"));
        _store.RecordPayment(id,100,"Bank","BANK-1");
        Assert.Throws<ArgumentException>(()=>_store.RecordPayment(id,201,"Cash",""));
        Assert.Equal(200,_store.GetInvoice(id).Balance);
        _store.RecordPayment(id,200,"Cash","");
        Assert.Equal("Paid",_store.GetInvoice(id).Status); Assert.Empty(_store.FindInvoices(outstandingOnly:true));
        Assert.Equal(300,_store.Dashboard().CollectedToday);
    }
    [Fact] public void DiscountsAndTaxDoNotInflateProfit()
    {
        var invoice=_store.GetInvoice(_store.PostSale(Sale(Stocked(),paid:297,discount:30,tax:10)));
        Assert.Equal(27,invoice.Tax); Assert.Equal(297,invoice.Total); Assert.Equal(70,invoice.GrossProfit);
        var report=_store.Report(DateTime.Today,DateTime.Today);
        Assert.Equal(27,report.Tax); Assert.Equal(70,report.GrossProfit);
    }
    [Fact] public void InvalidDiscountRollsBackInvoice()
    { var id=Stocked(); Assert.Throws<ArgumentException>(()=>_store.PostSale(Sale(id,paid:0,discount:301))); Assert.Empty(_store.FindInvoices()); }
    [Fact] public void FractionalPenniesAreRejected()
    { Assert.Throws<ArgumentException>(()=>Product(price:12.345m)); }
    [Fact] public void ArchivedProductsKeepHistoryButCannotSellOrReceive()
    {
        var id=Stocked(); _store.SaveProduct(new(id,"MF-001","Oil filter","Filters","Millat","MF 240","piece","A-1",3,150,false));
        Assert.Empty(_store.FindProducts()); Assert.Single(_store.FindProducts(filter:"Archived"));
        Assert.Throws<ArgumentException>(()=>_store.PostSale(Sale(id)));
        Assert.Throws<ArgumentException>(()=>_store.ReceiveStock(id,1,100,"Purchase","Supplier","",""));
        Assert.Single(_store.Movements());
    }
    [Fact] public void ReportsSeparateExpensesAndUseInclusiveLocalDateRange()
    {
        _store.PostSale(Sale(Stocked())); _store.AddExpense("Transport","Delivery",20);
        var report=_store.Report(DateTime.Today,DateTime.Today);
        Assert.Equal(1,report.InvoiceCount); Assert.Equal(80,report.OperatingProfit);
        Assert.Equal(0,_store.Report(DateTime.Today.AddDays(-2),DateTime.Today.AddDays(-1)).InvoiceCount);
        Assert.Throws<ArgumentException>(()=>_store.Report(DateTime.Today,DateTime.Today.AddDays(-1)));
    }
    [Fact] public void NewCostsNeverRewriteHistoricalProfit()
    {
        var id=Stocked(); var invoice=_store.PostSale(Sale(id));
        _store.ReceiveStock(id,10,300,"Purchase","Supplier","P2","");
        Assert.Equal(100,_store.GetInvoice(invoice).GrossProfit);
        Assert.Equal(100,Assert.Single(_store.InvoiceLines(invoice)).UnitCost);
    }
    [Fact] public void BackupCanBeOpenedWithAllBalancesAndProfileIntact()
    {
        _store.PostSale(Sale(Stocked(),paid:100,customer:"Ali"));
        _store.SaveSettings(new("Naeem Tractors","0123","Shop address","PKR"));
        var path=Path.Combine(_directory,"backup.db"); _store.Backup(path);
        var restored=new InventoryStore(path); restored.Initialize();
        Assert.Equal(200,restored.Dashboard().Outstanding);
        Assert.Equal(8,Assert.Single(restored.FindProducts()).Stock);
        Assert.Equal("0123",restored.Settings().Phone);
        Assert.Throws<ArgumentException>(()=>_store.Backup(_store.DatabasePath));
    }
    [Fact] public async Task ConcurrentSalesCannotBothSellTheLastItem()
    {
        var id=Stocked(qty:1);
        var results=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>Task.Run(()=>
        {
            try { _store.PostSale(Sale(id,qty:1,paid:150)); return true; }
            catch (ArgumentException) { return false; }
        })));
        Assert.Single(results,x=>x); Assert.Equal(0,Assert.Single(_store.FindProducts()).Stock);
        Assert.Single(_store.FindInvoices());
    }
    [Fact] public void CsvEscapesQuotesAndNeutralizesSpreadsheetFormulas()
    {
        var path=Path.Combine(_directory,"stock.csv");
        CsvExport.Write(path,[new[] { "Part", "=1+1", "  @SUM(A1)", "A,\"B\"" }]);
        var csv=File.ReadAllText(path);
        Assert.Contains("\"'=1+1\"",csv); Assert.Contains("\"'  @SUM(A1)\"",csv);
        Assert.Contains("\"A,\"\"B\"\"\"",csv);
    }
    [Fact] public void UnitCannotChangeAfterStockHasBeenRecorded()
    {
        var id=Stocked();
        Assert.Throws<ArgumentException>(()=>_store.SaveProduct(new(id,"MF-001","Oil filter","Filters","Millat","MF 240","box","A-1",3,150,true)));
        Assert.Equal("piece",Assert.Single(_store.FindProducts()).Unit);
    }
    [Fact] public void FullStockExportIsNotLimitedToTheScreenPage()
    {
        for (var i=0; i<503; i++) Product($"P-{i:0000}");
        Assert.Equal(500,_store.FindProducts().Count);
        Assert.Equal(503,_store.FindProducts(limit:-1).Count);
        Assert.Single(_store.FindProducts("P-0502"));
    }
    [Fact] public void AverageCostRoundsHalfAwayFromZero()
    {
        var id=Stocked(qty:1,cost:1.00m);
        _store.ReceiveStock(id,1,1.01m,"Purchase","Supplier","R","");
        Assert.Equal(1.01m,Assert.Single(_store.FindProducts()).AverageCost);
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1000001)]
    public void InvalidStockQuantitiesAreRejected(long quantity)
    {
        var id=Product();
        Assert.Throws<ArgumentException>(()=>_store.ReceiveStock(id,quantity,100,"Opening stock","","",""));
        Assert.Empty(_store.Movements());
    }
    [Fact] public async Task ConcurrentPaymentsCannotOvercollect()
    {
        var id=_store.PostSale(Sale(Stocked(),paid:0,customer:"Ali"));
        var results=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>Task.Run(()=>
        {
            try { _store.RecordPayment(id,200,"Cash",""); return true; }
            catch (ArgumentException) { return false; }
        })));
        Assert.Single(results,x=>x);
        Assert.Equal(100,_store.GetInvoice(id).Balance);
        Assert.Equal(200,Assert.Single(_store.Payments()).Amount);
    }
    public void Dispose() { Directory.Delete(_directory,true); }
}
