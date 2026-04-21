using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using System.Globalization;
using System.Security.Claims;
using System.Text;

namespace MoneroMarketCap.Pages.Portfolios;

[Authorize]
public class TransactionsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IPortfolioRepository _portfolios;

    public const int PageSize = 25;

    public PortfolioCoin? PortfolioCoin { get; set; }
    public List<CoinTransaction> Transactions { get; set; } = new();
    public int TotalTransactionCount { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int TotalPages => TotalTransactionCount == 0 ? 1 : (int)Math.Ceiling(TotalTransactionCount / (double)PageSize);

    public decimal CoinCurrentPrice { get; set; }

    public bool IsXmrCoin => PortfolioCoin?.Coin.Symbol.ToUpper() == "XMR";

    // Import status (flash message)
    public string? ImportMessage { get; set; }
    public bool ImportSuccess { get; set; }

    // Add form fields
    [BindProperty] public TransactionType TransactionType { get; set; }
    [BindProperty] public decimal Amount { get; set; }
    [BindProperty] public decimal PriceUsdAtTime { get; set; }
    [BindProperty] public string? Notes { get; set; }
    [BindProperty] public string? ExternalTransactionId { get; set; }
    [BindProperty] public DateTime TransactedAt { get; set; } = DateTime.UtcNow;

    // Edit form fields
    [BindProperty] public int EditTransactionId { get; set; }
    [BindProperty] public TransactionType EditType { get; set; }
    [BindProperty] public decimal EditAmount { get; set; }
    [BindProperty] public decimal EditPriceUsdAtTime { get; set; }
    [BindProperty] public string? EditNotes { get; set; }
    [BindProperty] public string? EditExternalTransactionId { get; set; }
    [BindProperty] public DateTime EditTransactedAt { get; set; }

    // Import form
    [BindProperty] public IFormFile? ImportFile { get; set; }

    public bool PrivacyMode { get; set; }

    public TransactionsModel(AppDbContext db, IPortfolioRepository portfolios)
    {
        _db = db;
        _portfolios = portfolios;
    }

    private int GetUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task LoadPrivacyModeAsync()
    {
        PrivacyMode = await _db.Users
            .Where(u => u.Id == GetUserId())
            .Select(u => u.PrivacyMode)
            .FirstOrDefaultAsync();
    }

    private async Task<bool> LoadPortfolioCoin(int portfolioCoinId)
    {
        PortfolioCoin = await _db.PortfolioCoins
            .Include(pc => pc.Coin)
            .Include(pc => pc.Portfolio)
            .FirstOrDefaultAsync(pc => pc.Id == portfolioCoinId);

        if (PortfolioCoin == null || PortfolioCoin.Portfolio.UserId != GetUserId())
            return false;

        CoinCurrentPrice = PortfolioCoin.Coin.PriceUsd;
        return true;
    }

    private async Task LoadTransactionsPageAsync(int portfolioCoinId, int page)
    {
        var query = _db.CoinTransactions
            .Where(t => t.PortfolioCoinId == portfolioCoinId);

        TotalTransactionCount = await query.CountAsync();

        CurrentPage = Math.Max(1, page);
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;

        Transactions = await query
            .OrderByDescending(t => t.TransactedAt)
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();
    }

    /// <summary>
    /// Recalculates TotalAmount and TotalCostBasis for a PortfolioCoin based on all
    /// its transactions (both already-persisted and pending Add/Modify in the change tracker).
    /// Call BEFORE SaveChangesAsync.
    /// </summary>
    private async Task RecalculateTotalsAsync(PortfolioCoin portfolioCoin)
    {
        // Persisted transactions (excluding any pending deletes)
        var persisted = await _db.CoinTransactions
            .Where(t => t.PortfolioCoinId == portfolioCoin.Id)
            .ToListAsync();

        // Remove any that are marked for deletion in the current context
        var deletedIds = _db.ChangeTracker.Entries<CoinTransaction>()
            .Where(e => e.State == EntityState.Deleted && e.Entity.PortfolioCoinId == portfolioCoin.Id)
            .Select(e => e.Entity.Id)
            .ToHashSet();
        persisted = persisted.Where(t => !deletedIds.Contains(t.Id)).ToList();

        // Include pending Added rows not yet persisted
        var pending = _db.ChangeTracker.Entries<CoinTransaction>()
            .Where(e => e.State == EntityState.Added && e.Entity.PortfolioCoinId == portfolioCoin.Id)
            .Select(e => e.Entity)
            .ToList();

        var combined = persisted.Concat(pending).OrderBy(t => t.TransactedAt).ToList();

        decimal totalAmount = 0;
        decimal totalCostBasis = 0;

        foreach (var t in combined)
        {
            if (t.Type == TransactionType.Buy)
            {
                totalAmount += t.Amount;
                totalCostBasis += t.TotalUsd;
            }
            else
            {
                if (totalAmount > 0)
                {
                    var proportion = Math.Min(t.Amount / totalAmount, 1m);
                    totalCostBasis -= totalCostBasis * proportion;
                }
                totalAmount -= t.Amount;
                if (totalAmount < 0) totalAmount = 0;
                if (totalCostBasis < 0) totalCostBasis = 0;
            }
        }

        portfolioCoin.TotalAmount = totalAmount;
        portfolioCoin.TotalCostBasis = totalCostBasis;
    }

    public async Task<IActionResult> OnGetAsync(int portfolioCoinId, int pageNumber = 1)
    {
        if (!await LoadPortfolioCoin(portfolioCoinId))
            return Forbid();

        await LoadPrivacyModeAsync();
        await LoadTransactionsPageAsync(portfolioCoinId, pageNumber);

        if (TempData["ImportMessage"] is string msg)
        {
            ImportMessage = msg;
            ImportSuccess = TempData["ImportSuccess"] as bool? ?? false;
        }

        PriceUsdAtTime = CoinCurrentPrice;
        TransactedAt = DateTime.UtcNow;
        return Page();
    }

    public async Task<IActionResult> OnGetExportAsync(int portfolioCoinId)
    {
        if (!await LoadPortfolioCoin(portfolioCoinId))
            return Forbid();

        var all = await _db.CoinTransactions
            .Where(t => t.PortfolioCoinId == portfolioCoinId)
            .OrderBy(t => t.TransactedAt)
            .ToListAsync();

        var inv = CultureInfo.InvariantCulture;
        var symbol = PortfolioCoin!.Coin.Symbol;
        var portfolioName = PortfolioCoin.Portfolio.Name;

        var sb = new StringBuilder();
        sb.AppendLine("Date (UTC),Portfolio,Coin,Type,Amount,Price USD,Total USD,Transaction ID,Notes");

        foreach (var t in all)
        {
            sb.Append(t.TransactedAt.ToString("yyyy-MM-dd HH:mm:ss", inv)).Append(',');
            sb.Append(Csv(portfolioName)).Append(',');
            sb.Append(Csv(symbol)).Append(',');
            sb.Append(t.Type).Append(',');
            sb.Append(t.Amount.ToString(inv)).Append(',');
            sb.Append(t.PriceUsdAtTime.ToString(inv)).Append(',');
            sb.Append(t.TotalUsd.ToString(inv)).Append(',');
            sb.Append(Csv(t.ExternalTransactionId ?? string.Empty)).Append(',');
            sb.Append(Csv(t.Notes ?? string.Empty));
            sb.AppendLine();
        }

        var filename = $"transactions-{portfolioName}-{symbol}-{DateTime.UtcNow:yyyyMMdd}.csv"
            .Replace(' ', '_');

        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes(sb.ToString()))
            .ToArray();

        return File(bytes, "text/csv", filename);
    }

    public async Task<IActionResult> OnPostImportMoneroAsync(int portfolioCoinId)
    {
        if (!await LoadPortfolioCoin(portfolioCoinId))
            return Forbid();

        if (PortfolioCoin!.Coin.Symbol.ToUpper() != "XMR")
        {
            TempData["ImportMessage"] = "Monero GUI import is only available for XMR.";
            TempData["ImportSuccess"] = false;
            return RedirectToPage(new { portfolioCoinId });
        }

        if (ImportFile == null || ImportFile.Length == 0)
        {
            TempData["ImportMessage"] = "No file provided.";
            TempData["ImportSuccess"] = false;
            return RedirectToPage(new { portfolioCoinId });
        }

        if (ImportFile.Length > 5_000_000)
        {
            TempData["ImportMessage"] = "File is too large (max 5 MB).";
            TempData["ImportSuccess"] = false;
            return RedirectToPage(new { portfolioCoinId });
        }

        int imported = 0;
        int skipped = 0;
        int errors = 0;
        var errorSamples = new List<string>();

        var coinGeckoId = PortfolioCoin.Coin.CoinGeckoId;
        var priceHistory = await _db.CoinPriceHistories
            .Where(h => h.Coin.CoinGeckoId == coinGeckoId && h.Interval == "1d")
            .Select(h => new { h.RecordedAt, h.PriceUsd })
            .ToListAsync();

        var priceByDate = priceHistory
            .GroupBy(h => h.RecordedAt.Date)
            .ToDictionary(g => g.Key, g => g.Last().PriceUsd);

        var existingTxIds = await _db.CoinTransactions
            .Where(t => t.PortfolioCoinId == portfolioCoinId && t.ExternalTransactionId != null)
            .Select(t => t.ExternalTransactionId!)
            .ToListAsync();
        var existingSet = new HashSet<string>(existingTxIds, StringComparer.OrdinalIgnoreCase);

        var inv = CultureInfo.InvariantCulture;
        using var reader = new StreamReader(ImportFile.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        string? headerLine = await reader.ReadLineAsync();
        if (headerLine == null)
        {
            TempData["ImportMessage"] = "File is empty.";
            TempData["ImportSuccess"] = false;
            return RedirectToPage(new { portfolioCoinId });
        }

        var headers = ParseCsvLine(headerLine);
        int Idx(string name) => headers.FindIndex(h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase));

        int idxDate = Idx("date");
        int idxEpoch = Idx("epoch");
        int idxDirection = Idx("direction");
        int idxAmount = Idx("amount");
        int idxTxid = Idx("txid");
        int idxLabel = Idx("label");
        int idxDesc = Idx("description");

        if (idxDate < 0 || idxDirection < 0 || idxAmount < 0 || idxTxid < 0)
        {
            TempData["ImportMessage"] = "Unrecognized CSV — expected Monero GUI export with columns: date, direction, amount, txid.";
            TempData["ImportSuccess"] = false;
            return RedirectToPage(new { portfolioCoinId });
        }

        int lineNum = 1;
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            lineNum++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var fields = ParseCsvLine(line);

                if (fields.Count <= Math.Max(Math.Max(idxDate, idxDirection), Math.Max(idxAmount, idxTxid)))
                {
                    errors++;
                    if (errorSamples.Count < 3) errorSamples.Add($"line {lineNum}: not enough columns");
                    continue;
                }

                var txid = fields[idxTxid].Trim();
                if (string.IsNullOrEmpty(txid))
                {
                    errors++;
                    if (errorSamples.Count < 3) errorSamples.Add($"line {lineNum}: missing txid");
                    continue;
                }

                if (existingSet.Contains(txid))
                {
                    skipped++;
                    continue;
                }

                DateTime transactedAt;
                if (idxEpoch >= 0 && long.TryParse(fields[idxEpoch], NumberStyles.Integer, inv, out var epoch))
                {
                    transactedAt = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                }
                else if (!DateTime.TryParse(fields[idxDate], inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out transactedAt))
                {
                    errors++;
                    if (errorSamples.Count < 3) errorSamples.Add($"line {lineNum}: bad date");
                    continue;
                }

                var direction = fields[idxDirection].Trim().ToLowerInvariant();
                TransactionType type = direction switch
                {
                    "in" => TransactionType.Buy,
                    "out" => TransactionType.Sell,
                    _ => TransactionType.Buy
                };

                if (!decimal.TryParse(fields[idxAmount], NumberStyles.Any, inv, out var amount) || amount <= 0)
                {
                    errors++;
                    if (errorSamples.Count < 3) errorSamples.Add($"line {lineNum}: bad amount");
                    continue;
                }

                decimal priceUsd = 0;
                var key = transactedAt.Date;
                if (priceByDate.TryGetValue(key, out var hp))
                {
                    priceUsd = hp;
                }
                else
                {
                    var earlier = priceByDate
                        .Where(kv => kv.Key <= key)
                        .OrderByDescending(kv => kv.Key)
                        .FirstOrDefault();
                    if (earlier.Key != default) priceUsd = earlier.Value;
                    else priceUsd = PortfolioCoin.Coin.PriceUsd;
                }

                var label = idxLabel >= 0 ? fields[idxLabel].Trim() : string.Empty;
                var desc = idxDesc >= 0 ? fields[idxDesc].Trim() : string.Empty;
                var notes = string.Join(" · ", new[] { label, desc }
                    .Where(s => !string.IsNullOrWhiteSpace(s) && s != "Primary account"));
                if (string.IsNullOrWhiteSpace(notes)) notes = "Imported from Monero GUI";

                var tx = new CoinTransaction
                {
                    PortfolioCoinId = PortfolioCoin.Id,
                    Type = type,
                    Amount = amount,
                    PriceUsdAtTime = priceUsd,
                    Notes = notes,
                    ExternalTransactionId = txid,
                    TransactedAt = DateTime.SpecifyKind(transactedAt, DateTimeKind.Utc)
                };

                _db.CoinTransactions.Add(tx);
                existingSet.Add(txid);
                imported++;
            }
            catch (Exception ex)
            {
                errors++;
                if (errorSamples.Count < 3) errorSamples.Add($"line {lineNum}: {ex.Message}");
            }
        }

        if (imported > 0)
        {
            await RecalculateTotalsAsync(PortfolioCoin);
            await _db.SaveChangesAsync();
        }

        var summary = $"Imported {imported}, skipped {skipped} duplicate(s), {errors} error(s).";
        if (errorSamples.Any())
            summary += " First errors: " + string.Join("; ", errorSamples);

        TempData["ImportMessage"] = summary;
        TempData["ImportSuccess"] = imported > 0;
        return RedirectToPage(new { portfolioCoinId });
    }

    public async Task<IActionResult> OnPostAddAsync(int portfolioCoinId)
    {
        if (!await LoadPortfolioCoin(portfolioCoinId))
            return Forbid();

        var tx = new CoinTransaction
        {
            PortfolioCoinId = PortfolioCoin!.Id,
            Type = TransactionType,
            Amount = Amount,
            PriceUsdAtTime = PriceUsdAtTime,
            Notes = Notes,
            ExternalTransactionId = string.IsNullOrWhiteSpace(ExternalTransactionId) ? null : ExternalTransactionId.Trim(),
            TransactedAt = TransactedAt == default ? DateTime.UtcNow : DateTime.SpecifyKind(TransactedAt, DateTimeKind.Utc)
        };

        _db.CoinTransactions.Add(tx);
        await RecalculateTotalsAsync(PortfolioCoin);
        await _db.SaveChangesAsync();

        return RedirectToPage(new { portfolioCoinId });
    }

    public async Task<IActionResult> OnPostEditAsync(int portfolioCoinId, int pageNumber = 1)
    {
        if (!await LoadPortfolioCoin(portfolioCoinId))
            return Forbid();

        var tx = await _db.CoinTransactions
            .FirstOrDefaultAsync(t => t.Id == EditTransactionId && t.PortfolioCoinId == portfolioCoinId);

        if (tx == null)
            return NotFound();

        tx.Type = EditType;
        tx.Amount = EditAmount;
        tx.PriceUsdAtTime = EditPriceUsdAtTime;
        tx.Notes = EditNotes;
        tx.ExternalTransactionId = string.IsNullOrWhiteSpace(EditExternalTransactionId)
            ? null
            : EditExternalTransactionId.Trim();
        tx.TransactedAt = EditTransactedAt == default
            ? tx.TransactedAt
            : DateTime.SpecifyKind(EditTransactedAt, DateTimeKind.Utc);

        await RecalculateTotalsAsync(PortfolioCoin!);
        await _db.SaveChangesAsync();

        return RedirectToPage(new { portfolioCoinId, pageNumber });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int portfolioCoinId, int transactionId, int pageNumber = 1)
    {
        if (!await LoadPortfolioCoin(portfolioCoinId))
            return Forbid();

        var tx = await _db.CoinTransactions
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.PortfolioCoinId == portfolioCoinId);

        if (tx == null)
            return NotFound();

        var remainingCount = await _db.CoinTransactions
            .CountAsync(t => t.PortfolioCoinId == portfolioCoinId);

        // Use repository for the delete itself (keeps whatever side effects it has)
        await _portfolios.DeleteTransactionAsync(transactionId);

        // Reload PortfolioCoin fresh since repo delete may have modified it, then recalc
        var refreshed = await _db.PortfolioCoins
            .FirstOrDefaultAsync(pc => pc.Id == portfolioCoinId);
        if (refreshed != null)
        {
            await RecalculateTotalsAsync(refreshed);
            await _db.SaveChangesAsync();
        }

        if (remainingCount <= 1)
            return RedirectToPage("/Portfolios/Detail", new { id = PortfolioCoin!.PortfolioId });

        return RedirectToPage(new { portfolioCoinId, pageNumber });
    }

    // ─── CSV helpers ────────────────────────────────────────────────────────

    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var needsQuoting = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!needsQuoting) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == ',')
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                else if (c == '"' && sb.Length == 0)
                {
                    inQuotes = true;
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        result.Add(sb.ToString());
        return result;
    }
}