using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace NaeemTractors.Core;

/// <summary>One short-lived connection per operation. All financial postings are atomic.</summary>
public sealed class InventoryStore
{
    public string DatabasePath { get; }
    public InventoryStore(string databasePath) => DatabasePath = Path.GetFullPath(databasePath);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, ForeignKeys = true, DefaultTimeout = 15, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var c = Open();
        Execute(c, null, "PRAGMA journal_mode=WAL;");
        var version = Scalar(c, null, "PRAGMA user_version;");
        if (version > 1) throw new InvalidOperationException("This database needs a newer app version.");
        using var tx = c.BeginTransaction();
        Execute(c, tx, """
            CREATE TABLE IF NOT EXISTS Products(
                Id INTEGER PRIMARY KEY, Sku TEXT NOT NULL COLLATE NOCASE UNIQUE,
                Name TEXT NOT NULL, Category TEXT NOT NULL, Brand TEXT NOT NULL,
                TractorModel TEXT NOT NULL, Unit TEXT NOT NULL, Location TEXT NOT NULL,
                MinimumStock INTEGER NOT NULL CHECK(MinimumStock>=0),
                SellingPrice INTEGER NOT NULL CHECK(SellingPrice>=0),
                AverageCost INTEGER NOT NULL DEFAULT 0 CHECK(AverageCost>=0),
                Active INTEGER NOT NULL CHECK(Active IN(0,1)));
            CREATE TABLE IF NOT EXISTS Invoices(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, Number TEXT UNIQUE, CreatedAt TEXT NOT NULL,
                Customer TEXT NOT NULL, Phone TEXT NOT NULL, Subtotal INTEGER NOT NULL,
                Discount INTEGER NOT NULL, Tax INTEGER NOT NULL, Total INTEGER NOT NULL,
                Cost INTEGER NOT NULL, Notes TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS InvoiceLines(
                Id INTEGER PRIMARY KEY, InvoiceId INTEGER NOT NULL REFERENCES Invoices(Id),
                ProductId INTEGER NOT NULL REFERENCES Products(Id), Sku TEXT NOT NULL, Name TEXT NOT NULL,
                Quantity INTEGER NOT NULL CHECK(Quantity>0), UnitPrice INTEGER NOT NULL CHECK(UnitPrice>=0),
                UnitCost INTEGER NOT NULL CHECK(UnitCost>=0));
            CREATE TABLE IF NOT EXISTS StockMovements(
                Id INTEGER PRIMARY KEY, ProductId INTEGER NOT NULL REFERENCES Products(Id),
                CreatedAt TEXT NOT NULL, Quantity INTEGER NOT NULL CHECK(Quantity<>0), Kind TEXT NOT NULL,
                UnitCost INTEGER NOT NULL CHECK(UnitCost>=0), Reference TEXT NOT NULL,
                Supplier TEXT NOT NULL, Notes TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Payments(
                Id INTEGER PRIMARY KEY, InvoiceId INTEGER NOT NULL REFERENCES Invoices(Id),
                CreatedAt TEXT NOT NULL, Amount INTEGER NOT NULL CHECK(Amount>0),
                Method TEXT NOT NULL, Reference TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Expenses(
                Id INTEGER PRIMARY KEY, CreatedAt TEXT NOT NULL, Category TEXT NOT NULL,
                Description TEXT NOT NULL, Amount INTEGER NOT NULL CHECK(Amount>0));
            CREATE TABLE IF NOT EXISTS Settings(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Products_Name ON Products(Name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_Stock_Product ON StockMovements(ProductId);
            CREATE INDEX IF NOT EXISTS IX_Stock_Date ON StockMovements(CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_Invoice_Date ON Invoices(CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_Payments_Invoice ON Payments(InvoiceId);
            CREATE INDEX IF NOT EXISTS IX_Payments_Date ON Payments(CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_Expenses_Date ON Expenses(CreatedAt);
            INSERT OR IGNORE INTO Settings VALUES('Name','Naeem Tractors');
            INSERT OR IGNORE INTO Settings VALUES('Phone','');
            INSERT OR IGNORE INTO Settings VALUES('Address','');
            INSERT OR IGNORE INTO Settings VALUES('Currency','PKR');
            PRAGMA user_version=1;
            """);
        tx.Commit();
    }

    private const string ProductSelect = """
        SELECT p.Id,p.Sku,p.Name,p.Category,p.Brand,p.TractorModel,p.Unit,p.Location,
        p.MinimumStock,p.SellingPrice,p.AverageCost,COALESCE(s.Qty,0),p.Active
        FROM Products p LEFT JOIN
        (SELECT ProductId,SUM(Quantity) Qty FROM StockMovements GROUP BY ProductId) s ON s.ProductId=p.Id
        """;

    public IReadOnlyList<Product> FindProducts(string search = "", string category = "", string filter = "Active", int limit = 500)
    {
        using var c = Open();
        using var cmd = Command(c, null, ProductSelect + """
             WHERE ($q='' OR p.Sku LIKE $q ESCAPE '\' OR p.Name LIKE $q ESCAPE '\'
               OR p.Brand LIKE $q ESCAPE '\' OR p.TractorModel LIKE $q ESCAPE '\' OR p.Location LIKE $q ESCAPE '\')
             AND ($category='' OR p.Category=$category)
             AND ($filter='All' OR ($filter='Archived' AND p.Active=0)
               OR ($filter='Active' AND p.Active=1)
               OR ($filter='Low stock' AND p.Active=1 AND COALESCE(s.Qty,0)<=p.MinimumStock)
               OR ($filter='In stock' AND p.Active=1 AND COALESCE(s.Qty,0)>0))
             ORDER BY p.Name COLLATE NOCASE LIMIT $limit
            """, ("$q", Search(search)), ("$category", category), ("$filter", filter), ("$limit",limit));
        using var r = cmd.ExecuteReader();
        var result = new List<Product>();
        while (r.Read()) result.Add(ReadProduct(r));
        return result;
    }

    public IReadOnlyList<string> Categories()
    {
        using var c = Open();
        using var cmd = Command(c, null, "SELECT DISTINCT Category FROM Products WHERE Category<>'' ORDER BY Category");
        using var r = cmd.ExecuteReader();
        var values = new List<string>();
        while (r.Read()) values.Add(r.GetString(0));
        return values;
    }

    public long SaveProduct(ProductInput p)
    {
        var sku = Required(p.Sku, "Part number", 80);
        var name = Required(p.Name, "Product name", 160);
        var unit = Required(p.Unit, "Unit", 30);
        if (p.MinimumStock < 0 || p.MinimumStock > 1_000_000) throw new ArgumentException("Minimum stock must be between 0 and 1,000,000.");
        var price = Money.ToMinor(p.SellingPrice);
        using var c = Open();
        using var tx = c.BeginTransaction();
        try
        {
            if (p.Id != 0)
            {
                var existing = ProductById(c, tx, p.Id);
                if (!string.Equals(existing.Unit, unit, StringComparison.OrdinalIgnoreCase) &&
                    Scalar(c, tx, "SELECT COUNT(*) FROM StockMovements WHERE ProductId=$id", ("$id",p.Id)) > 0)
                    throw new ArgumentException("The unit cannot change after stock has been recorded. Create a separate product for a different unit.");
            }
            var args = new (string, object)[] { ("$sku",sku),("$name",name),("$category",Clean(p.Category)),
                ("$brand",Clean(p.Brand)),("$model",Clean(p.TractorModel)),("$unit",unit),("$location",Clean(p.Location)),
                ("$min",p.MinimumStock),("$price",price),("$active",p.Active ? 1 : 0),("$id",p.Id) };
            if (p.Id == 0)
            {
                Execute(c, tx, """
                    INSERT INTO Products(Sku,Name,Category,Brand,TractorModel,Unit,Location,MinimumStock,SellingPrice,Active)
                    VALUES($sku,$name,$category,$brand,$model,$unit,$location,$min,$price,$active)
                    """, args);
            }
            else if (Execute(c, tx, """
                UPDATE Products SET Sku=$sku,Name=$name,Category=$category,Brand=$brand,TractorModel=$model,
                Unit=$unit,Location=$location,MinimumStock=$min,SellingPrice=$price,Active=$active WHERE Id=$id
                """, args) != 1) throw new ArgumentException("Product no longer exists.");
            var id = p.Id == 0 ? Scalar(c, tx, "SELECT last_insert_rowid()") : p.Id;
            tx.Commit();
            return id;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ArgumentException("That part number already exists. Use a unique part number.", ex);
        }
    }

    public void ReceiveStock(long productId, long quantity, decimal unitCost, string kind,
        string supplier, string reference, string notes)
    {
        Quantity(quantity);
        if (kind is not ("Opening stock" or "Purchase")) throw new ArgumentException("Choose opening stock or purchase.");
        var cost = Money.ToMinor(unitCost);
        if (kind == "Purchase") Required(supplier, "Supplier", 160);
        using var c = Open();
        using var tx = c.BeginTransaction();
        var p = ProductById(c, tx, productId);
        if (!p.Active) throw new ArgumentException("Activate this product before receiving stock.");
        if (kind == "Opening stock" && Scalar(c, tx, "SELECT COUNT(*) FROM StockMovements WHERE ProductId=$id", ("$id",productId)) > 0)
            throw new ArgumentException("Opening stock is only available before this product's first movement. Use a purchase receipt.");
        var newQty = checked(p.Stock + quantity);
        if (newQty > 1_000_000) throw new ArgumentException("Stock cannot exceed 1,000,000 units per product.");
        var average = checked((long)decimal.Round((p.Stock * Money.ToMinor(p.AverageCost) + (decimal)quantity * cost) / newQty,
            0, MidpointRounding.AwayFromZero));
        Execute(c, tx, "UPDATE Products SET AverageCost=$cost WHERE Id=$id", ("$cost",average),("$id",productId));
        InsertMovement(c, tx, productId, quantity, kind, cost, Clean(reference), Clean(supplier), Clean(notes));
        tx.Commit();
    }

    public long PostSale(SaleRequest request)
    {
        if (request.Lines.Count == 0) throw new ArgumentException("Add at least one product to the invoice.");
        if (request.Lines.Count > 200) throw new ArgumentException("An invoice can contain at most 200 lines.");
        if (request.Lines.Select(x => x.ProductId).Distinct().Count() != request.Lines.Count)
            throw new ArgumentException("Combine quantities for repeated products.");
        if (request.TaxPercent < 0 || request.TaxPercent > 100 || decimal.Round(request.TaxPercent, 2) != request.TaxPercent)
            throw new ArgumentException("Tax percentage must be between 0 and 100, with at most two decimal places.");
        var discount = Money.ToMinor(request.Discount);
        var paid = Money.ToMinor(request.Paid);
        PaymentMethod(request.PaymentMethod);
        using var c = Open();
        // A deferred transaction is intentionally not used: writers reserve before validating stock.
        using var tx = c.BeginTransaction();
        var lines = new List<(Product Product, long Qty, long Price, long Cost)>();
        long subtotal = 0, totalCost = 0;
        foreach (var line in request.Lines)
        {
            Quantity(line.Quantity);
            var p = ProductById(c, tx, line.ProductId);
            if (!p.Active) throw new ArgumentException($"{p.Name} is archived.");
            if (p.Stock < line.Quantity) throw new ArgumentException($"Insufficient stock for {p.Name}. Available: {p.Stock}.");
            var price = Money.ToMinor(line.UnitPrice);
            var cost = Money.ToMinor(p.AverageCost);
            subtotal = checked(subtotal + line.Quantity * price);
            totalCost = checked(totalCost + line.Quantity * cost);
            lines.Add((p,line.Quantity,price,cost));
        }
        if (discount > subtotal) throw new ArgumentException("Discount cannot exceed the subtotal.");
        var tax = checked((long)decimal.Round((subtotal - discount) * request.TaxPercent / 100m, 0, MidpointRounding.AwayFromZero));
        var total = checked(subtotal - discount + tax);
        if (total > Money.ToMinor(1_000_000_000m)) throw new ArgumentException("An invoice total cannot exceed 1,000,000,000.");
        if (paid > total) throw new ArgumentException("Payment cannot exceed the invoice total.");
        var customer = Clean(request.Customer);
        if (paid < total) customer = Required(customer, "Customer name for a credit sale", 160);
        if (customer.Length == 0) customer = "Walk-in customer";
        Execute(c, tx, """
            INSERT INTO Invoices(CreatedAt,Customer,Phone,Subtotal,Discount,Tax,Total,Cost,Notes)
            VALUES($date,$customer,$phone,$subtotal,$discount,$tax,$total,$cost,$notes)
            """, ("$date",Now()),("$customer",customer),("$phone",Clean(request.Phone)),("$subtotal",subtotal),
            ("$discount",discount),("$tax",tax),("$total",total),("$cost",totalCost),("$notes",Clean(request.Notes)));
        var id = Scalar(c, tx, "SELECT last_insert_rowid()");
        var number = $"NT-{id:000000}";
        Execute(c, tx, "UPDATE Invoices SET Number=$number WHERE Id=$id", ("$number",number),("$id",id));
        foreach (var (p,qty,price,cost) in lines)
        {
            Execute(c, tx, """
                INSERT INTO InvoiceLines(InvoiceId,ProductId,Sku,Name,Quantity,UnitPrice,UnitCost)
                VALUES($invoice,$product,$sku,$name,$qty,$price,$cost)
                """, ("$invoice",id),("$product",p.Id),("$sku",p.Sku),("$name",p.Name),("$qty",qty),("$price",price),("$cost",cost));
            InsertMovement(c, tx, p.Id, -qty, "Sale", cost, number, "", "");
        }
        if (paid > 0) InsertPayment(c, tx, id, paid, request.PaymentMethod, "At invoice posting");
        tx.Commit();
        return id;
    }

    public void RecordPayment(long invoiceId, decimal amount, string method, string reference)
    {
        var minor = Money.ToMinor(amount);
        if (minor == 0) throw new ArgumentException("Payment must be greater than zero.");
        PaymentMethod(method);
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (Scalar(c, tx, "SELECT COUNT(*) FROM Invoices WHERE Id=$id", ("$id",invoiceId)) != 1)
            throw new ArgumentException("Select an existing invoice.");
        var due = Scalar(c, tx, """
            SELECT Total-COALESCE((SELECT SUM(Amount) FROM Payments WHERE InvoiceId=$id),0)
            FROM Invoices WHERE Id=$id
            """, ("$id",invoiceId));
        if (minor > due) throw new ArgumentException("Payment exceeds this invoice's outstanding balance.");
        InsertPayment(c, tx, invoiceId, minor, method, Clean(reference));
        tx.Commit();
    }

    private const string InvoiceSelect = """
        SELECT i.Id,i.Number,i.CreatedAt,i.Customer,i.Phone,i.Subtotal,i.Discount,i.Tax,i.Total,i.Cost,
        COALESCE((SELECT SUM(Amount) FROM Payments WHERE InvoiceId=i.Id),0),i.Notes FROM Invoices i
        """;

    public IReadOnlyList<Invoice> FindInvoices(string search = "", bool outstandingOnly = false,
        DateTime? from = null, DateTime? through = null, int limit = 500)
    {
        var (start,end) = DateBounds(from, through);
        using var c = Open();
        using var cmd = Command(c, null, InvoiceSelect + """
             WHERE ($q='' OR i.Number LIKE $q ESCAPE '\' OR i.Customer LIKE $q ESCAPE '\' OR i.Phone LIKE $q ESCAPE '\')
             AND ($due=0 OR i.Total>COALESCE((SELECT SUM(Amount) FROM Payments WHERE InvoiceId=i.Id),0))
             AND ($from='' OR i.CreatedAt >= $from) AND ($to='' OR i.CreatedAt < $to)
             ORDER BY i.Id DESC LIMIT $limit
            """, ("$q",Search(search)),("$due",outstandingOnly ? 1 : 0),("$from",start),("$to",end),("$limit",limit));
        using var r = cmd.ExecuteReader();
        var result = new List<Invoice>();
        while (r.Read()) result.Add(ReadInvoice(r));
        return result;
    }

    public Invoice GetInvoice(long id)
    {
        using var c = Open();
        using var cmd = Command(c, null, InvoiceSelect + " WHERE i.Id=$id", ("$id",id));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new ArgumentException("Invoice not found.");
        return ReadInvoice(r);
    }

    public IReadOnlyList<InvoiceLine> InvoiceLines(long id)
    {
        using var c = Open();
        using var cmd = Command(c, null, "SELECT Sku,Name,Quantity,UnitPrice,UnitCost FROM InvoiceLines WHERE InvoiceId=$id ORDER BY Id", ("$id",id));
        using var r = cmd.ExecuteReader();
        var result = new List<InvoiceLine>();
        while (r.Read()) result.Add(new(r.GetString(0),r.GetString(1),r.GetInt64(2),Amount(r,3),Amount(r,4)));
        return result;
    }

    public IReadOnlyList<Payment> Payments(string search = "")
    {
        using var c = Open();
        using var cmd = Command(c, null, """
            SELECT p.Id,i.Number,p.CreatedAt,i.Customer,p.Amount,p.Method,p.Reference FROM Payments p
            JOIN Invoices i ON i.Id=p.InvoiceId
            WHERE $q='' OR i.Number LIKE $q ESCAPE '\' OR i.Customer LIKE $q ESCAPE '\'
            ORDER BY p.Id DESC LIMIT 500
            """, ("$q",Search(search)));
        using var r = cmd.ExecuteReader();
        var result = new List<Payment>();
        while (r.Read()) result.Add(new(r.GetInt64(0),r.GetString(1),Date(r,2),r.GetString(3),Amount(r,4),r.GetString(5),r.GetString(6)));
        return result;
    }

    public IReadOnlyList<StockMovement> Movements(string search = "")
    {
        using var c = Open();
        using var cmd = Command(c, null, """
            SELECT m.Id,m.CreatedAt,p.Sku,p.Name,m.Quantity,m.Kind,m.UnitCost,m.Reference,m.Supplier,m.Notes
            FROM StockMovements m JOIN Products p ON p.Id=m.ProductId
            WHERE $q='' OR p.Sku LIKE $q ESCAPE '\' OR p.Name LIKE $q ESCAPE '\'
                OR m.Reference LIKE $q ESCAPE '\' OR m.Supplier LIKE $q ESCAPE '\'
            ORDER BY m.Id DESC LIMIT 500
            """, ("$q",Search(search)));
        using var r = cmd.ExecuteReader();
        var result = new List<StockMovement>();
        while (r.Read()) result.Add(new(r.GetInt64(0),Date(r,1),r.GetString(2),r.GetString(3),r.GetInt64(4),
            r.GetString(5),Amount(r,6),r.GetString(7),r.GetString(8),r.GetString(9)));
        return result;
    }

    public void AddExpense(string category, string description, decimal amount)
    {
        category = Required(category,"Expense category",80);
        description = Required(description,"Expense description",300);
        var minor = Money.ToMinor(amount);
        if (minor == 0) throw new ArgumentException("Expense must be greater than zero.");
        using var c = Open();
        Execute(c, null, "INSERT INTO Expenses(CreatedAt,Category,Description,Amount) VALUES($date,$category,$description,$amount)",
            ("$date",Now()),("$category",category),("$description",description),("$amount",minor));
    }

    public IReadOnlyList<Expense> Expenses(string search = "")
    {
        using var c = Open();
        using var cmd = Command(c, null, """
            SELECT Id,CreatedAt,Category,Description,Amount FROM Expenses
            WHERE $q='' OR Category LIKE $q ESCAPE '\' OR Description LIKE $q ESCAPE '\'
            ORDER BY Id DESC LIMIT 500
            """, ("$q",Search(search)));
        using var r = cmd.ExecuteReader();
        var result = new List<Expense>();
        while (r.Read()) result.Add(new(r.GetInt64(0),Date(r,1),r.GetString(2),r.GetString(3),Amount(r,4)));
        return result;
    }

    public Dashboard Dashboard()
    {
        var today = Report(DateTime.Today,DateTime.Today);
        using var c = Open();
        var outstanding = Scalar(c, null, "SELECT COALESCE(SUM(Total),0)-(SELECT COALESCE(SUM(Amount),0) FROM Payments) FROM Invoices");
        var value = Scalar(c, null, """
            SELECT COALESCE(SUM(p.AverageCost*s.Qty),0) FROM Products p JOIN
            (SELECT ProductId,SUM(Quantity) Qty FROM StockMovements GROUP BY ProductId) s ON s.ProductId=p.Id
            """);
        var count = Scalar(c, null, "SELECT COUNT(*) FROM Products WHERE Active=1");
        var low = Scalar(c, null, """
            SELECT COUNT(*) FROM Products p WHERE p.Active=1 AND
            COALESCE((SELECT SUM(Quantity) FROM StockMovements WHERE ProductId=p.Id),0)<=p.MinimumStock
            """);
        return new(today.Sales,today.Collections,Money.FromMinor(outstanding),Money.FromMinor(value),
            count,low,today.GrossProfit,today.Expenses);
    }

    public PeriodReport Report(DateTime from, DateTime through)
    {
        var (start,end) = DateBounds(from,through);
        using var c = Open();
        using var cmd = Command(c, null, """
            SELECT COALESCE(SUM(Total),0),COALESCE(SUM(Discount),0),COALESCE(SUM(Tax),0),COALESCE(SUM(Cost),0),COUNT(*)
            FROM Invoices WHERE CreatedAt >= $from AND CreatedAt < $to
            """, ("$from",start),("$to",end));
        long sales,discount,tax,cost,count;
        using (var r = cmd.ExecuteReader())
        {
            r.Read(); sales=r.GetInt64(0); discount=r.GetInt64(1); tax=r.GetInt64(2); cost=r.GetInt64(3); count=r.GetInt64(4);
        }
        var collections = Scalar(c, null, "SELECT COALESCE(SUM(Amount),0) FROM Payments WHERE CreatedAt >= $from AND CreatedAt < $to", ("$from",start),("$to",end));
        var expenses = Scalar(c, null, "SELECT COALESCE(SUM(Amount),0) FROM Expenses WHERE CreatedAt >= $from AND CreatedAt < $to", ("$from",start),("$to",end));
        return new(Money.FromMinor(sales),Money.FromMinor(discount),Money.FromMinor(tax),Money.FromMinor(cost),
            Money.FromMinor(collections),Money.FromMinor(expenses),count);
    }

    public ShopSettings Settings()
    {
        using var c = Open();
        using var cmd = Command(c, null, "SELECT Key,Value FROM Settings");
        using var r = cmd.ExecuteReader();
        var values = new Dictionary<string,string>();
        while (r.Read()) values[r.GetString(0)] = r.GetString(1);
        return new(values["Name"],values["Phone"],values["Address"],values["Currency"]);
    }

    public void SaveSettings(ShopSettings settings)
    {
        Required(settings.Name,"Shop name",160);
        Required(settings.Currency,"Currency code",8);
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var (key,value) in new[] { ("Name",settings.Name),("Phone",settings.Phone),("Address",settings.Address),("Currency",settings.Currency) })
            Execute(c, tx, "UPDATE Settings SET Value=$value WHERE Key=$key", ("$value",Clean(value)),("$key",key));
        tx.Commit();
    }

    public void Backup(string destination)
    {
        var path = Path.GetFullPath(destination);
        if (string.Equals(path,DatabasePath,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose a backup file outside the active database.");
        using var source = Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=path, Pooling=false }.ToString());
        target.Open();
        source.BackupDatabase(target);
        using var check = target.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        if (!string.Equals(check.ExecuteScalar()?.ToString(),"ok",StringComparison.Ordinal))
            throw new IOException("Backup integrity check failed.");
    }

    public void BackupDaily(string directory)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory,$"naeem-{DateTime.Today:yyyy-MM-dd}.db");
        if (!File.Exists(destination)) Backup(destination);
    }

    private static Product ProductById(SqliteConnection c, SqliteTransaction tx, long id)
    {
        using var cmd = Command(c,tx,ProductSelect + " WHERE p.Id=$id",("$id",id));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new ArgumentException("Select an existing product.");
        return ReadProduct(r);
    }
    private static Product ReadProduct(SqliteDataReader r) => new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),
        r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetInt64(8),Amount(r,9),Amount(r,10),r.GetInt64(11),r.GetInt64(12)==1);
    private static Invoice ReadInvoice(SqliteDataReader r) => new(r.GetInt64(0),r.GetString(1),Date(r,2),r.GetString(3),
        r.GetString(4),Amount(r,5),Amount(r,6),Amount(r,7),Amount(r,8),Amount(r,9),Amount(r,10),r.GetString(11));
    private static decimal Amount(SqliteDataReader r,int index) => Money.FromMinor(r.GetInt64(index));
    private static DateTime Date(SqliteDataReader r,int index) => DateTime.Parse(r.GetString(index),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToLocalTime();
    private static string Now() => DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture);
    private static (string,string) DateBounds(DateTime? from, DateTime? through)
    {
        if (from.HasValue && through.HasValue && from.Value.Date > through.Value.Date)
            throw new ArgumentException("The start date must be on or before the end date.");
        return (from?.Date.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture) ?? "",
            through?.Date.AddDays(1).ToUniversalTime().ToString("O",CultureInfo.InvariantCulture) ?? "");
    }
    private static void Quantity(long quantity)
    {
        if (quantity <= 0 || quantity > 1_000_000) throw new ArgumentException("Quantity must be a whole number from 1 to 1,000,000.");
    }
    private static void PaymentMethod(string method)
    {
        if (method is not ("Cash" or "Bank" or "Easypaisa" or "JazzCash")) throw new ArgumentException("Select a supported payment method.");
    }
    private static string Clean(string text) => text.Trim();
    private static string Required(string text,string label,int maximum)
    {
        var value = Clean(text);
        if (value.Length == 0 || value.Length > maximum) throw new ArgumentException($"{label} is required (maximum {maximum} characters).");
        return value;
    }
    private static string Search(string text) => string.IsNullOrWhiteSpace(text) ? "" : "%" + text.Trim().Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_") + "%";
    private static void InsertMovement(SqliteConnection c,SqliteTransaction tx,long product,long quantity,string kind,long cost,string reference,string supplier,string notes) =>
        Execute(c,tx,"""
            INSERT INTO StockMovements(ProductId,CreatedAt,Quantity,Kind,UnitCost,Reference,Supplier,Notes)
            VALUES($product,$date,$quantity,$kind,$cost,$reference,$supplier,$notes)
            """,("$product",product),("$date",Now()),("$quantity",quantity),("$kind",kind),("$cost",cost),("$reference",reference),("$supplier",supplier),("$notes",notes));
    private static void InsertPayment(SqliteConnection c,SqliteTransaction tx,long invoice,long amount,string method,string reference) =>
        Execute(c,tx,"INSERT INTO Payments(InvoiceId,CreatedAt,Amount,Method,Reference) VALUES($invoice,$date,$amount,$method,$reference)",
            ("$invoice",invoice),("$date",Now()),("$amount",amount),("$method",method),("$reference",reference));
    private static SqliteCommand Command(SqliteConnection c,SqliteTransaction? tx,string sql,params (string Name,object Value)[] args)
    {
        var cmd = c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText=sql;
        foreach (var (name,value) in args) cmd.Parameters.AddWithValue(name,value);
        return cmd;
    }
    private static int Execute(SqliteConnection c,SqliteTransaction? tx,string sql,params (string Name,object Value)[] args)
    { using var cmd=Command(c,tx,sql,args); return cmd.ExecuteNonQuery(); }
    private static long Scalar(SqliteConnection c,SqliteTransaction? tx,string sql,params (string Name,object Value)[] args)
    { using var cmd=Command(c,tx,sql,args); return Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture); }
}

public static class CsvExport
{
    public static void Write(string path, IEnumerable<IEnumerable<string>> rows)
    {
        using var writer = new StreamWriter(path,false,new UTF8Encoding(true));
        foreach (var row in rows) writer.WriteLine(string.Join(",",row.Select(Cell)));
    }
    private static string Cell(string value)
    {
        // Prevent spreadsheet formula execution, including after leading whitespace.
        if (value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return "\"" + value.Replace("\"","\"\"") + "\"";
    }
}
