# Naeem Tractors · Windows inventory app

A working first implementation in **C# / .NET 10 / WPF / SQLite**, for a single owner on a single Windows computer. The interface uses charcoal, ivory, forest green, and amber, with custom controls for selections and calendars. No blue is specified by the app. Windows-owned title bars, file dialogs, message boxes, and printer dialogs follow the computer's system theme.

## Download from GitHub

Open this repository’s **Actions → Windows build**, select a successful run, and download **NaeemTractors-Windows-x64** under **Artifacts**. GitHub may require you to sign in. Extract the downloaded artifact, then extract the app ZIP inside it and open `NaeemTractors.exe`. GitHub’s green **Code → Download ZIP** downloads source code only.

## Run on Windows

Requires Windows 10/11 x64. For development, install the **.NET 10 SDK** (Visual Studio with the .NET desktop workload is optional).

```powershell
dotnet restore NaeemTractors.sln
dotnet test tests/NaeemTractors.Tests -c Release
dotnet run --project src/NaeemTractors.Desktop
```

To create a portable Windows distribution with .NET bundled:

```powershell
.\scripts\Publish-Windows.ps1
```

Extract the resulting `artifacts/NaeemTractors-Windows-x64.zip` to a permanent folder and open `NaeemTractors.exe`. Keep the full folder together. This is a portable distribution, not an installer. No separate .NET installation is needed for the self-contained build. The app does not request administrator privileges. Windows may show its usual prompt for an unsigned app; code signing is not configured.

## First use

1. **Settings:** enter the real shop phone and address. These are intentionally blank because the supplied Maps listing could not be read. Currency initially displays PKR; changing the label does not convert amounts.
2. **Products:** create each part with a unique part number, category, brand, tractor compatibility, unit, shelf, selling price, and minimum stock. Products start with zero stock. Quantities are whole units; create separate products for different packs or units.
3. **Stock receipts:** select a product and post opening stock with its cost. Opening stock can only be posted before that product's first stock movement. Subsequent receipts are purchases requiring a supplier name. Costs use a moving weighted average, rounded to the smallest currency unit.
4. **New sale:** search, choose a product, enter quantity and price, and add the item. Re-adding a product at the same price combines its quantity. Enter an optional invoice discount and tax percentage, then the payment received. Credit sales require a customer name. Posting deducts stock and records the invoice and initial payment together.
5. **Invoices & payments:** filter outstanding invoices, record later collections, and view or print invoices. Choose **Microsoft Print to PDF** in the Windows printer dialog to save a PDF.
6. **Expenses:** record shop operating costs. Purchases and supplier payments should not be entered here as operating expenses.
7. **Reports:** choose an inclusive local date range. Summaries cover all records in the range, while the table shows the latest 500 invoices. Sales CSV exports all invoices in the range; stock CSV exports all products, including archived items.

The dashboard separates today's invoiced sales, today's payments received, all customer dues, stock value, low stock, gross profit, and expenses. Sales totals include entered tax; gross profit excludes tax. Operating result is gross profit minus recorded expenses. These are management figures, not a complete accounting statement.

## Search, filters, and shortcuts

- Product search covers part number, name, brand, compatible model, and shelf; category and stock-status filters work together. Search is case-insensitive for ASCII; SQLite's default comparison does not provide full Unicode case folding.
- Stock history searches products, suppliers, and references. Invoice search covers invoice number, customer, and phone. Expense search covers category and description.
- Searches wait 220 ms after typing; database work runs away from the UI thread. Tables use row virtualization and at most 500 matching rows. Refine search to reach additional records. Click column headings to sort the loaded rows.
- **Ctrl+N:** open a new sale (preserves any current draft).
- **Ctrl+F:** focus the current screen's search.
- **Ctrl+S:** save product details on Products.
- **Ctrl+Enter:** post the current invoice, after confirmation.
- **F5:** refresh the current screen.

## Data and backups

The database is created at:

```text
%LOCALAPPDATA%\NaeemTractors\inventory.db
```

The app creates one daily backup at startup in the adjacent `Backups` directory, and **Settings → Backup now** creates a user-selected backup using SQLite's online backup API and an integrity check. Daily backups are retained until manually removed. A backup on the same drive does not protect against drive failure; copy it to another drive regularly. Database files and exports contain shop data and are not encrypted by the app.

Restore procedure:

1. Close all copies of the app. Preserve the entire existing `NaeemTractors` data folder, including any `inventory.db-wal` and `inventory.db-shm` files, in a separate location.
2. Place a verified backup in a **fresh** `NaeemTractors` data folder, named `inventory.db`. Do not mix old WAL/SHM files with the restored database.
3. Reopen the app. Check product quantities, a known invoice, customer dues, and the shop profile before continuing trading.

Automated tests verify reopening a backup and checking balances, stock, and profile. There is no in-app restore wizard yet. Do not put the live database on a network share or in a cloud-synced folder. Backups may be copied there after they are created.

## Business rules

- Money is stored as integer minor units; input permits at most two decimal places. Quantity and money limits are validated.
- Negative stock, duplicate part numbers (case-insensitive), repeated invoice lines, excess discounts, and overpayments are rejected.
- A stock receipt changes weighted-average cost. Each posted sale snapshots product identity, selling price, and cost, so later catalog/cost changes cannot rewrite historical profit.
- A product's unit cannot change after its first stock movement. Archive products instead of deleting history. Remaining archived stock still contributes to stock valuation.
- SQLite foreign keys, constraints, WAL journaling, and immediate write transactions protect posting. Concurrent sales of the last unit cannot both succeed.
- Invoice numbers are assigned by the database. Invoice, stock movements, and initial payment commit atomically. Collections are stored separately and invoice balances are derived from them.
- Posting operations are serialized in the interface. Unsaved invoices are held in memory and prompt before discard/exit; they do not survive a crash.
- Reports use the Windows computer's local dates; stored timestamps are UTC. Set the computer's time zone correctly.
- CSV cells are quoted and potential spreadsheet formulas are neutralized.

## Current scope and validation

Implemented: owner dashboard, product creation/edit/archive, stock receipts/history, sales drafts/posting, invoice balances, payment history, expenses, dated reports, CSV exports, shop settings, printable invoices, and backups.

This is **v0.1**, not a finished accounting system. Still to implement: returns/refunds, invoice cancellation/reversal, stock counts/adjustments, supplier accounts and dues, dedicated customer accounts/statements/opening balances, staff authentication/permissions, Android synchronization, automatic application updates, and a signed installer. Supplier names on receipts are references only. Tax entry is manual and does not establish local tax compliance. The current app is for a trusted owner; it has no authentication or tamper-proof audit log. Editing SQLite directly bypasses application checks.

The solution can be cross-compiled on Linux with the Windows targeting pack. Business tests run there using real temporary SQLite databases. WPF needs Windows to run; visual layout, keyboard behavior, high-DPI display, and physical printer integration require Windows validation. A build is not evidence that those checks passed. The Windows CI workflow builds, tests, and publishes when pushed; its results should be checked separately.

Windows acceptance check (use test records before real trading): create a part, receive 10 units at cost 100, sell 2 at price 150 with payment 100, and verify stock 8, invoice due 200, sales 300, collections 100, and gross profit 100. Collect the remaining 200 and check the paid status. Print the invoice to PDF; check all lines and totals. Reopen the app to confirm persistence. Check search, dropdowns, date selection, tab navigation, and keyboard shortcuts at the laptop's display scale. Run the backup/restore procedure in a separate Windows user account or a preserved test data folder. If startup fails, the app attempts to write diagnostics to `crash.log` beside the database.

## Project structure

```text
src/NaeemTractors.Core/       SQLite persistence, validation, calculations, exports
src/NaeemTractors.Desktop/    WPF screens, shared theme, UI event handling, printing
tests/NaeemTractors.Tests/    Transaction, costing, reporting, concurrency, backup tests
scripts/                     Repeatable Windows packaging
```

Presentation is separate from the core business service. The current desktop uses code-behind event handlers rather than a full MVVM framework. No website, web server, credentials, or internet connection is required to run the installed app.
