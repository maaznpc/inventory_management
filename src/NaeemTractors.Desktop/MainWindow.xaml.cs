using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NaeemTractors.Core;

namespace NaeemTractors.Desktop;

public partial class MainWindow : Window
{
    private readonly InventoryStore _store;
    private readonly ObservableCollection<CartItem> _cart = [];
    private readonly Dictionary<string, CancellationTokenSource> _searches = [];
    private bool _ready, _busy, _suppressEvents;
    private long _editingProduct;
    private ShopSettings _settings = new("Naeem Tractors", "", "", "PKR");
    private IReadOnlyList<Invoice> _reportInvoices = [];

    public MainWindow()
    {
        _store = new InventoryStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NaeemTractors", "inventory.db"));
        InitializeComponent();
        CartGrid.ItemsSource = _cart;
        ReportFrom.SelectedDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        ReportTo.SelectedDate = DateTime.Today;
        DatabaseLocation.Text = _store.DatabasePath;
        MarkNavigation(0);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await Work(async () =>
        {
            await Task.Run(_store.Initialize);
            await RefreshAll();
            _ready = true;
        }, "Ready. Start by adding products, then record opening stock.");
        if (_ready)
        {
            try { await Task.Run(() => _store.BackupDaily(Path.Combine(Path.GetDirectoryName(_store.DatabasePath)!, "Backups"))); }
            catch (Exception ex) { StatusText.Text = "Database is ready; daily backup failed: " + ex.Message; }
        }
    }

    private async Task Work(Func<Task> action, string success = "Updated.")
    {
        if (_busy) return;
        var focused = Keyboard.FocusedElement as UIElement;
        _busy = true;
        Workspace.IsEnabled = false;
        Cursor = Cursors.Wait;
        StatusText.Foreground = (Brush)FindResource("Ink");
        StatusText.Text = "Working…";
        try
        {
            await action();
            StatusText.Text = success;
        }
        catch (Exception ex)
        {
            StatusText.Foreground = (Brush)FindResource("Danger");
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Please check", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Workspace.IsEnabled = true;
            Cursor = null;
            _busy = false;
            if (IsActive && focused is { IsVisible: true, IsEnabled: true }) focused.Focus();
        }
    }

    private async Task RefreshAll()
    {
        _suppressEvents = true;
        try
        {
            var settings = await Task.Run(_store.Settings);
            _settings = settings;
            ShopHeading.Text = settings.Name;
            Title = settings.Name + " · Inventory";
            CurrencyLabel.Text = settings.Currency + " · LOCAL DATABASE";
            ShopName.Text = settings.Name; ShopPhone.Text = settings.Phone;
            ShopAddress.Text = settings.Address; ShopCurrency.Text = settings.Currency;
            var category = CategoryFilter.SelectedItem as string ?? "All categories";
            CategoryFilter.ItemsSource = new[] { "All categories" }.Concat(await Task.Run(_store.Categories)).ToArray();
            CategoryFilter.SelectedItem = CategoryFilter.Items.Cast<string>().Contains(category) ? category : "All categories";
            await RefreshProducts();
            await RefreshProductPickers();
            await RefreshOverview();
            await RefreshMovements();
            await RefreshInvoices();
            await RefreshExpenses();
            await RefreshReport();
        }
        finally { _suppressEvents = false; }
    }

    private async Task RefreshOverview()
    {
        var d = await Task.Run(_store.Dashboard);
        TodaySales.Text = F(d.SalesToday); TodayCollections.Text = F(d.CollectedToday);
        Outstanding.Text = F(d.Outstanding); StockValue.Text = F(d.StockValue);
        ProductCount.Text = d.ActiveProducts.ToString("N0"); LowStockCount.Text = d.LowStockProducts.ToString("N0");
        TodayProfit.Text = F(d.GrossProfitToday); TodayExpenses.Text = F(d.ExpensesToday);
        LowStockGrid.ItemsSource = await Task.Run(() => _store.FindProducts(filter: "Low stock"));
    }

    private async Task RefreshProducts()
    {
        var search = ProductSearch.Text;
        var category = CategoryFilter.SelectedItem as string ?? "All categories";
        var filter = Choice(StockFilter);
        ProductsGrid.ItemsSource = await Task.Run(() => _store.FindProducts(search, category == "All categories" ? "" : category, filter));
    }

    private async Task RefreshProductPickers()
    {
        await RefreshStockPicker();
        await RefreshSalePicker();
    }
    private async Task RefreshStockPicker()
    {
        var id = (StockProduct.SelectedItem as Product)?.Id;
        var search = StockProductSearch.Text;
        var products = await Task.Run(() => _store.FindProducts(search));
        StockProduct.ItemsSource = products;
        StockProduct.SelectedItem = products.FirstOrDefault(p => p.Id == id) ?? products.FirstOrDefault();
    }
    private async Task RefreshSalePicker()
    {
        var id = (SaleProduct.SelectedItem as Product)?.Id;
        var search = SaleSearch.Text;
        var products = await Task.Run(() => _store.FindProducts(search, filter: "In stock"));
        SaleProduct.ItemsSource = products;
        SaleProduct.SelectedItem = products.FirstOrDefault(p => p.Id == id) ?? products.FirstOrDefault();
    }
    private async Task RefreshMovements()
    {
        var search = MovementSearch.Text;
        MovementsGrid.ItemsSource = await Task.Run(() => _store.Movements(search));
    }
    private async Task RefreshInvoices()
    {
        var selected = (InvoicesGrid.SelectedItem as Invoice)?.Id;
        var search = InvoiceSearch.Text;
        var onlyDue = OnlyOutstanding.IsChecked == true;
        var invoices = await Task.Run(() => _store.FindInvoices(search, onlyDue));
        InvoicesGrid.ItemsSource = invoices;
        InvoicesGrid.SelectedItem = invoices.FirstOrDefault(x => x.Id == selected);
        PaymentsGrid.ItemsSource = await Task.Run(() => _store.Payments(search));
        UpdatePaymentSelection();
    }
    private async Task RefreshExpenses()
    {
        var search = ExpenseSearch.Text;
        ExpensesGrid.ItemsSource = await Task.Run(() => _store.Expenses(search));
    }
    private (DateTime From, DateTime Through) ReportDates()
    {
        if (ReportFrom.SelectedDate is not DateTime from || ReportTo.SelectedDate is not DateTime through)
            throw new ArgumentException("Select both report dates.");
        if (from.Date > through.Date) throw new ArgumentException("Start date must be on or before the end date.");
        return (from,through);
    }
    private async Task RefreshReport()
    {
        var (from,through) = ReportDates();
        var report = await Task.Run(() => _store.Report(from,through));
        _reportInvoices = await Task.Run(() => _store.FindInvoices(from: from, through: through));
        ReportInvoicesGrid.ItemsSource = _reportInvoices;
        ReportSummary.Text = $"{from:dd MMM yyyy} – {through:dd MMM yyyy} · {report.InvoiceCount:N0} invoices · {_settings.Currency}\n"
            + $"Sales {F(report.Sales)}     Tax {F(report.Tax)}     Discounts {F(report.Discount)}\n"
            + $"Cost of goods sold {F(report.Cost)}     Gross profit {F(report.GrossProfit)}\n"
            + $"Collections {F(report.Collections)}     Expenses {F(report.Expenses)}     Operating result {F(report.OperatingProfit)}";
    }

    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _busy) return;
        var index = int.Parse(((Button)sender).Tag.ToString()!,CultureInfo.InvariantCulture);
        Pages.SelectedIndex = index;
        MarkNavigation(index);
        await Work(RefreshCurrent,"Ready.");
        FocusSearch();
    }
    private void MarkNavigation(int index)
    {
        for (var i=0; i<8; i++)
        {
            var nav = (Button)FindName("Nav" + i);
            nav.Background = i == index ? new SolidColorBrush(Color.FromRgb(62,82,57)) : Brushes.Transparent;
            nav.Foreground = i == index ? Brushes.White : new SolidColorBrush(Color.FromRgb(223,230,218));
        }
    }
    private Task RefreshCurrent() => Pages.SelectedIndex switch
    {
        0 => RefreshOverview(), 1 => RefreshProducts(), 2 => RefreshStockPage(),
        3 => RefreshSalePicker(), 4 => RefreshInvoices(), 5 => RefreshExpenses(),
        6 => RefreshReport(), _ => Task.CompletedTask
    };
    private async Task RefreshStockPage() { await RefreshStockPicker(); await RefreshMovements(); }
    private async void Refresh_Click(object sender,RoutedEventArgs e) { if (_ready) await Work(RefreshCurrent); }

    private async Task Debounce(string key, Func<Task> action)
    {
        if (!_ready || _suppressEvents || _busy) return;
        if (_searches.Remove(key,out var previous)) { previous.Cancel(); previous.Dispose(); }
        var source = new CancellationTokenSource();
        _searches[key] = source;
        try
        {
            await Task.Delay(220,source.Token);
            if (!_busy) await Work(action,"Search updated · showing up to 500 matches.");
        }
        catch (OperationCanceledException) { }
    }
    private async void ProductFilter_Changed(object sender,RoutedEventArgs e) => await Debounce("products",RefreshProducts);
    private async void StockSearch_Changed(object sender,TextChangedEventArgs e) => await Debounce("stock",RefreshStockPicker);
    private async void SaleSearch_Changed(object sender,TextChangedEventArgs e) => await Debounce("sale",RefreshSalePicker);
    private async void MovementSearch_Changed(object sender,TextChangedEventArgs e) => await Debounce("movements",RefreshMovements);
    private async void InvoiceFilter_Changed(object sender,RoutedEventArgs e) => await Debounce("invoices",RefreshInvoices);
    private async void ExpenseSearch_Changed(object sender,TextChangedEventArgs e) => await Debounce("expenses",RefreshExpenses);

    private void Product_Selected(object sender,SelectionChangedEventArgs e)
    {
        if (ProductsGrid.SelectedItem is not Product p) return;
        _editingProduct=p.Id;
        ProductSku.Text=p.Sku; ProductName.Text=p.Name; ProductCategory.Text=p.Category;
        ProductBrand.Text=p.Brand; ProductModel.Text=p.TractorModel; ProductUnit.Text=p.Unit;
        ProductLocation.Text=p.Location; ProductPrice.Text=F(p.SellingPrice);
        ProductMinimum.Text=p.MinimumStock.ToString(CultureInfo.CurrentCulture); ProductActive.IsChecked=p.Active;
    }
    private void NewProduct_Click(object sender,RoutedEventArgs e) => ResetProduct();
    private void ResetProduct()
    {
        _editingProduct=0; ProductsGrid.SelectedItem=null;
        ProductSku.Clear(); ProductName.Clear(); ProductCategory.Clear(); ProductBrand.Clear(); ProductModel.Clear();
        ProductUnit.Text="piece"; ProductLocation.Clear(); ProductPrice.Text="0.00"; ProductMinimum.Text="5";
        ProductActive.IsChecked=true; ProductSku.Focus();
    }
    private async void SaveProduct_Click(object sender,RoutedEventArgs e)
    {
        await Work(async () =>
        {
            var p = new ProductInput(_editingProduct,ProductSku.Text,ProductName.Text,ProductCategory.Text,
                ProductBrand.Text,ProductModel.Text,ProductUnit.Text,ProductLocation.Text,
                Whole(ProductMinimum.Text,"Minimum stock",true),Amount(ProductPrice.Text,"Selling price"),ProductActive.IsChecked==true);
            await Task.Run(() => _store.SaveProduct(p));
            await RefreshAll(); ResetProduct();
        },"Product saved. Add stock from Stock receipts.");
    }
    private async void ReceiveStock_Click(object sender,RoutedEventArgs e)
    {
        await Work(async () =>
        {
            if (StockProduct.SelectedItem is not Product p) throw new ArgumentException("Select a product first.");
            var qty=Whole(StockQuantity.Text,"Quantity"); var cost=Amount(StockCost.Text,"Unit cost");
            var kind=Choice(StockKind); var supplier=StockSupplier.Text; var reference=StockReference.Text; var notes=StockNotes.Text;
            await Task.Run(() => _store.ReceiveStock(p.Id,qty,cost,kind,supplier,reference,notes));
            StockQuantity.Text="1"; StockReference.Clear(); StockNotes.Clear();
            await RefreshAll();
        },"Stock receipt posted. Inventory and average cost updated.");
    }

    private void SaleProduct_Selected(object sender,SelectionChangedEventArgs e)
    {
        if (SaleProduct.SelectedItem is Product p) SaleUnitPrice.Text=F(p.SellingPrice);
    }
    private async void AddSaleLine_Click(object sender,RoutedEventArgs e)
    {
        await Work(() =>
        {
            if (SaleProduct.SelectedItem is not Product p) throw new ArgumentException("Select an in-stock product.");
            var qty=Whole(SaleQuantity.Text,"Quantity"); var price=Amount(SaleUnitPrice.Text,"Unit price");
            var existing=_cart.FirstOrDefault(x => x.ProductId==p.Id);
            var combined=checked(qty+(existing?.Quantity ?? 0));
            if (combined > p.Stock) throw new ArgumentException($"Only {p.Stock} units of {p.Name} are available.");
            if (existing is not null)
            {
                if (existing.UnitPrice!=price) throw new ArgumentException("This item is already in the draft at a different price. Remove its row before changing the price.");
                var index=_cart.IndexOf(existing); _cart[index]=existing with { Quantity=combined };
            }
            else _cart.Add(new(p.Id,p.Sku,p.Name,qty,price));
            SaleQuantity.Text="1"; UpdateCartTotals();
            return Task.CompletedTask;
        },"Item added to draft.");
        SaleSearch.Focus();
    }
    private void RemoveSaleLine_Click(object sender,RoutedEventArgs e)
    {
        if (CartGrid.SelectedItem is CartItem line) { _cart.Remove(line); UpdateCartTotals(); }
    }
    private void ClearSale_Click(object sender,RoutedEventArgs e)
    {
        if (_cart.Count>0 && MessageBox.Show(this,"Discard this unsaved invoice?","Clear draft",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) return;
        ResetSale();
    }
    private void ResetSale()
    {
        _cart.Clear(); SaleCustomer.Clear(); SalePhone.Clear(); SaleNotes.Clear();
        SaleDiscount.Text="0.00"; SaleTax.Text="0"; SalePaid.Text="0.00"; SaleQuantity.Text="1";
        SaleMethod.SelectedIndex=0; UpdateCartTotals();
    }
    private void SaleTotals_Changed(object sender,TextChangedEventArgs e)
    {
        if (CartTotal is not null) UpdateCartTotals();
    }
    private void UpdateCartTotals()
    {
        var subtotal=_cart.Sum(x=>x.Total);
        try
        {
            var discount=Amount(SaleDiscount.Text,"Discount"); var taxRate=Amount(SaleTax.Text,"Tax percentage");
            var paid=Amount(SalePaid.Text,"Payment");
            if (discount>subtotal || taxRate>100) throw new ArgumentException("Check discount and tax.");
            var tax=decimal.Round((subtotal-discount)*taxRate/100m,2,MidpointRounding.AwayFromZero);
            CartSubtotal.Text="Subtotal  "+F(subtotal); CartTax.Text="Tax  "+F(tax);
            CartTotal.Text="Total  "+F(subtotal-discount+tax); CartDue.Text="Due  "+F(subtotal-discount+tax-paid);
        }
        catch (ArgumentException)
        {
            CartSubtotal.Text="Subtotal  "+F(subtotal); CartTax.Text="Tax  —";
            CartTotal.Text="Check amounts"; CartDue.Text="Enter valid discount, tax, and payment amounts.";
        }
    }
    private async void PostSale_Click(object sender,RoutedEventArgs e)
    {
        if (!_ready) return;
        if (_cart.Count==0) { StatusText.Text="Add at least one item first."; return; }
        if (MessageBox.Show(this,"Post this invoice and deduct its stock? Posted invoices cannot be edited in this version.",
            "Post invoice",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) return;
        long invoiceId=0;
        await Work(async () =>
        {
            var request=new SaleRequest(SaleCustomer.Text,SalePhone.Text,_cart.Select(x=>new SaleLine(x.ProductId,x.Quantity,x.UnitPrice)).ToArray(),
                Amount(SaleDiscount.Text,"Discount"),Amount(SaleTax.Text,"Tax percentage"),Amount(SalePaid.Text,"Payment"),Choice(SaleMethod),SaleNotes.Text);
            invoiceId=await Task.Run(()=>_store.PostSale(request));
            ResetSale(); await RefreshAll();
            Pages.SelectedIndex=4; MarkNavigation(4);
            InvoicesGrid.SelectedItem=InvoicesGrid.Items.Cast<Invoice>().FirstOrDefault(x=>x.Id==invoiceId);
        },"Invoice posted. Open View / print invoice for a printable copy.");
    }

    private async void PayFull_Click(object sender,RoutedEventArgs e)
    {
        await Work(() =>
        {
            var subtotal = _cart.Sum(x => x.Total);
            var discount = Amount(SaleDiscount.Text,"Discount");
            var tax = Amount(SaleTax.Text,"Tax percentage");
            if (discount > subtotal || tax > 100) throw new ArgumentException("Check the invoice discount and tax percentage first.");
            SalePaid.Text = F(subtotal-discount+decimal.Round((subtotal-discount)*tax/100m,2,MidpointRounding.AwayFromZero));
            return Task.CompletedTask;
        },"Full invoice amount entered as payment.");
    }

    private void Invoice_Selected(object sender,SelectionChangedEventArgs e) => UpdatePaymentSelection();
    private void UpdatePaymentSelection()
    {
        if (PaymentInvoice is null) return;
        if (InvoicesGrid.SelectedItem is Invoice i)
        {
            PaymentInvoice.Text=$"{i.Number}\n{i.Customer}\nOutstanding: {F(i.Balance)} {_settings.Currency}";
            PaymentAmount.Text=i.Balance==0 ? "" : F(i.Balance);
        }
        else { PaymentInvoice.Text="No invoice selected"; PaymentAmount.Clear(); }
    }
    private async void RecordPayment_Click(object sender,RoutedEventArgs e)
    {
        await Work(async () =>
        {
            if (InvoicesGrid.SelectedItem is not Invoice invoice) throw new ArgumentException("Select an invoice first.");
            var amount=Amount(PaymentAmount.Text,"Payment amount"); var method=Choice(PaymentMethod); var reference=PaymentReference.Text;
            await Task.Run(()=>_store.RecordPayment(invoice.Id,amount,method,reference));
            PaymentReference.Clear(); await RefreshAll();
        },"Payment recorded. Invoice balance updated.");
    }
    private async void AddExpense_Click(object sender,RoutedEventArgs e)
    {
        await Work(async () =>
        {
            var category=ExpenseCategory.Text; var description=ExpenseDescription.Text; var amount=Amount(ExpenseAmount.Text,"Expense amount");
            await Task.Run(()=>_store.AddExpense(category,description,amount));
            ExpenseDescription.Clear(); ExpenseAmount.Clear(); await RefreshAll();
        },"Expense recorded.");
    }
    private async void RunReport_Click(object sender,RoutedEventArgs e) => await Work(RefreshReport,"Report updated.");

    private async void ExportSales_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new SaveFileDialog { Filter="CSV files|*.csv", FileName=$"naeem-sales-{DateTime.Today:yyyy-MM-dd}.csv" };
        if (dialog.ShowDialog(this)!=true) return;
        await Work(async () =>
        {
            await RefreshReport();
            var rows=new List<IEnumerable<string>> { new[] { "Invoice","Date","Customer","Phone","Subtotal","Discount","Tax","Total","Cost","Gross profit","Paid","Due","Status","Currency" } };
            var (from,through) = ReportDates();
            var invoices = await Task.Run(() => _store.FindInvoices(from:from, through:through, limit:-1));
            rows.AddRange(invoices.Select(i=>new[] { i.Number,i.CreatedAt.ToString("yyyy-MM-dd HH:mm"),i.Customer,i.Phone,
                Csv(i.Subtotal),Csv(i.Discount),Csv(i.Tax),Csv(i.Total),Csv(i.Cost),Csv(i.GrossProfit),Csv(i.Paid),Csv(i.Balance),i.Status,_settings.Currency }));
            await Task.Run(()=>CsvExport.Write(dialog.FileName,rows));
        },"Sales CSV saved (all invoices in selected dates).");
    }
    private async void ExportStock_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new SaveFileDialog { Filter="CSV files|*.csv", FileName=$"naeem-stock-{DateTime.Today:yyyy-MM-dd}.csv" };
        if (dialog.ShowDialog(this)!=true) return;
        await Work(async () =>
        {
            var products=await Task.Run(()=>_store.FindProducts(filter:"All",limit:-1));
            var rows=new List<IEnumerable<string>> { new[] { "Part number","Product","Category","Brand","Tractor models","Unit","Shelf","Stock","Minimum stock","Selling price","Average cost","Stock value","Status","Currency" } };
            rows.AddRange(products.Select(p=>new[] { p.Sku,p.Name,p.Category,p.Brand,p.TractorModel,p.Unit,p.Location,p.Stock.ToString(CultureInfo.InvariantCulture),
                p.MinimumStock.ToString(CultureInfo.InvariantCulture),Csv(p.SellingPrice),Csv(p.AverageCost),Csv(p.StockValue),p.StockStatus,_settings.Currency }));
            await Task.Run(()=>CsvExport.Write(dialog.FileName,rows));
        },"Stock CSV saved (all products, including archived).");
    }
    private async void SaveSettings_Click(object sender,RoutedEventArgs e)
    {
        await Work(async () =>
        {
            var settings=new ShopSettings(ShopName.Text,ShopPhone.Text,ShopAddress.Text,ShopCurrency.Text);
            await Task.Run(()=>_store.SaveSettings(settings)); await RefreshAll();
        },"Shop profile saved.");
    }
    private async void Backup_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new SaveFileDialog { Filter="SQLite backup|*.db", FileName=$"naeem-backup-{DateTime.Now:yyyy-MM-dd-HHmmss}.db" };
        if (dialog.ShowDialog(this)!=true) return;
        await Work(()=>Task.Run(()=>_store.Backup(dialog.FileName)),"Backup saved and integrity checked. Keep a copy outside this computer.");
    }
    private async void PrintInvoice_Click(object sender,RoutedEventArgs e)
    {
        await Work(async () =>
        {
            if (InvoicesGrid.SelectedItem is not Invoice selected) throw new ArgumentException("Select an invoice first.");
            var invoice=await Task.Run(()=>_store.GetInvoice(selected.Id));
            var lines=await Task.Run(()=>_store.InvoiceLines(selected.Id));
            ShowInvoice(invoice,lines);
        },"Invoice preview opened. Print to a printer or Microsoft Print to PDF.");
    }

    private void ShowInvoice(Invoice invoice,IReadOnlyList<InvoiceLine> lines)
    {
        var document=new FlowDocument { FontFamily=new FontFamily("Segoe UI"),FontSize=12,PagePadding=new Thickness(45),
            ColumnWidth=double.PositiveInfinity,Background=Brushes.White,Foreground=(Brush)FindResource("Ink") };
        document.Blocks.Add(new Paragraph(new Run(_settings.Name)) { FontSize=26,FontWeight=FontWeights.Bold });
        if (!string.IsNullOrWhiteSpace(_settings.Address+_settings.Phone))
            document.Blocks.Add(new Paragraph(new Run(_settings.Address+"\n"+_settings.Phone)));
        document.Blocks.Add(new Paragraph(new Run($"SALES INVOICE  {invoice.Number}\n{invoice.CreatedAt:dd MMM yyyy HH:mm}\nCustomer: {invoice.Customer}\nPhone: {invoice.Phone}")));
        var table=new Table { CellSpacing=0 };
        foreach (var width in new[] { 110,250,55,95,100 }) table.Columns.Add(new TableColumn { Width=new GridLength(width) });
        var group=new TableRowGroup(); table.RowGroups.Add(group);
        void Row(bool heading,params string[] values)
        {
            var row=new TableRow();
            foreach (var value in values)
                row.Cells.Add(new TableCell(new Paragraph(new Run(value))) { Padding=new Thickness(6),
                    BorderBrush=(Brush)FindResource("Line"),BorderThickness=new Thickness(0,0,0,1),
                    Background=heading ? (Brush)FindResource("GreenLight") : Brushes.White,
                    FontWeight=heading ? FontWeights.SemiBold : FontWeights.Normal });
            group.Rows.Add(row);
        }
        Row(true,"Part no.","Product","Qty","Price","Amount");
        foreach (var line in lines) Row(false,line.Sku,line.Name,line.Quantity.ToString(CultureInfo.CurrentCulture),F(line.UnitPrice),F(line.Total));
        document.Blocks.Add(table);
        document.Blocks.Add(new Paragraph(new Run($"Subtotal: {F(invoice.Subtotal)}\nDiscount: {F(invoice.Discount)}\nTax: {F(invoice.Tax)}\nTOTAL: {F(invoice.Total)} {_settings.Currency}\nPaid: {F(invoice.Paid)}\nBalance due: {F(invoice.Balance)}"))
            { TextAlignment=TextAlignment.Right,FontSize=14 });
        if (invoice.Notes.Length>0) document.Blocks.Add(new Paragraph(new Run("Notes: "+invoice.Notes)));
        document.Blocks.Add(new Paragraph(new Run("Thank you for your business.")) { Foreground=(Brush)FindResource("Green") });
        var preview=new Window { Title=invoice.Number+" · Invoice preview",Width=850,Height=850,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var panel=new DockPanel();
        var print=new Button { Content="Print / Save as PDF…",Margin=new Thickness(14),HorizontalAlignment=HorizontalAlignment.Right };
        print.Click+=(_,_)=>
        {
            try
            {
                var dialog=new PrintDialog();
                if (dialog.ShowDialog()!=true) return;
                document.PageWidth=dialog.PrintableAreaWidth; document.PageHeight=dialog.PrintableAreaHeight;
                // Proportional columns fit the selected paper's printable width.
                var available=Math.Max(200,document.PageWidth-document.PagePadding.Left-document.PagePadding.Right);
                var ratios=new[] { .17,.39,.08,.18,.18 };
                for (var index=0; index<table.Columns.Count; index++) table.Columns[index].Width=new GridLength(available*ratios[index]);
                dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator,invoice.Number);
            }
            catch (Exception ex) { MessageBox.Show(preview,ex.Message,"Printing failed",MessageBoxButton.OK,MessageBoxImage.Warning); }
        };
        DockPanel.SetDock(print,Dock.Top); panel.Children.Add(print);
        panel.Children.Add(new FlowDocumentScrollViewer { Document=document,IsToolBarVisible=false });
        preview.Content=panel; preview.Show();
    }

    private async void Window_KeyDown(object sender,KeyEventArgs e)
    {
        if (!_ready || _busy) return;
        var ctrl=(Keyboard.Modifiers & ModifierKeys.Control)!=0;
        if (e.Key==Key.F5) { e.Handled=true; await Work(RefreshCurrent); }
        else if (ctrl && e.Key==Key.F) { e.Handled=true; FocusSearch(); }
        else if (ctrl && e.Key==Key.N) { e.Handled=true; Pages.SelectedIndex=3; MarkNavigation(3); await Work(RefreshSalePicker); SaleSearch.Focus(); }
        else if (ctrl && e.Key==Key.S && Pages.SelectedIndex==1) { e.Handled=true; SaveProduct_Click(sender,e); }
        else if (ctrl && e.Key==Key.Enter && Pages.SelectedIndex==3) { e.Handled=true; PostSale_Click(sender,e); }
    }
    private void FocusSearch()
    {
        TextBox? box=Pages.SelectedIndex switch { 1=>ProductSearch,2=>StockProductSearch,3=>SaleSearch,4=>InvoiceSearch,5=>ExpenseSearch,_=>null };
        box?.Focus(); box?.SelectAll();
    }
    private void Window_Closing(object? sender,CancelEventArgs e)
    {
        if (_busy) { e.Cancel=true; StatusText.Text="Please wait for the current operation to finish."; return; }
        if (_cart.Count>0 && MessageBox.Show(this,"Close and discard the unsaved invoice?","Unsaved draft",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)
        { e.Cancel=true; return; }
        foreach (var source in _searches.Values) { source.Cancel(); source.Dispose(); }
    }
    private static string F(decimal amount) => amount.ToString("N2",CultureInfo.CurrentCulture);
    private static string Csv(decimal amount) => amount.ToString("0.00",CultureInfo.InvariantCulture);
    private static string Choice(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
    private static decimal Amount(string text,string label)
    {
        if (!decimal.TryParse(text,NumberStyles.Number,CultureInfo.CurrentCulture,out var value)) throw new ArgumentException($"Enter a valid {label.ToLowerInvariant()}.");
        Money.ToMinor(value);
        return value;
    }
    private static long Whole(string text,string label,bool allowZero=false)
    {
        if (!long.TryParse(text,NumberStyles.Integer,CultureInfo.CurrentCulture,out var value) || value<(allowZero ? 0 : 1) || value>1_000_000)
            throw new ArgumentException($"{label} must be a whole number between {(allowZero ? 0 : 1)} and 1,000,000.");
        return value;
    }
}

public sealed record CartItem(long ProductId,string Sku,string Name,long Quantity,decimal UnitPrice)
{
    public decimal Total=>Quantity*UnitPrice;
}
