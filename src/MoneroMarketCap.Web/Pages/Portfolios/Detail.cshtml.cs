using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using System.Globalization;
using System.Security.Claims;
using System.Text;

namespace MoneroMarketCap.Pages.Portfolios;

[Authorize]
public class DetailModel : PageModel
{
    private readonly IPortfolioRepository _portfolios;
    private readonly ICoinRepository _coins;
    private readonly AppDbContext _db;

    public Portfolio? Portfolio { get; set; }
    public List<SelectListItem> CoinOptions { get; set; } = new();
    public Dictionary<int, decimal> CoinPrices { get; set; } = new();
    public decimal XmrPrice { get; set; }

    public IReadOnlyList<AllocationSlice> Allocations { get; set; } = new List<AllocationSlice>();
    public IReadOnlyList<PnlPoint> PnlSeries { get; set; } = new List<PnlPoint>();

    public string? ImportMessage { get; set; }
    public bool ImportSuccess { get; set; }

    [BindProperty] public int CoinId { get; set; }
    [BindProperty] public TransactionType TransactionType { get; set; }
    [BindProperty] public decimal Amount { get; set; }
    [BindProperty] public decimal PriceUsdAtTime { get; set; }
    [BindProperty] public string? Notes { get; set; }
    [BindProperty] public string? ExternalTransactionId { get; set; }
    [BindProperty] public DateTime TransactedAt { get; set; } = DateTime.UtcNow;
    [BindProperty] public IFormFile? ImportFile { get; set; }

    public bool PrivacyMode { get; set; }

    public DetailModel(IPortfolioRepository portfolios, ICoinRepository coins, AppDbContext db)
    {
        _portfolios = portfolios;
        _coins = coins;
        _db = db;
    }

    private int GetUserId() => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> LoadPortfolio(int id)
    {
        Portfolio = await _portfolios.GetWithDetailsAsync(id);
        return Portfolio != null && Portfolio.UserId == GetUserId();
    }

    private async Task LoadPrivacyModeAsync()
    {
        PrivacyMode = await _db.Users
            .Where(u => u.Id == GetUserId())
            .Select(u => u.PrivacyMode)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Recalculates TotalAmount and TotalCostBasis for a PortfolioCoin based on all
    /// persisted + pending-Added transactions (minus pending deletes) in the change tracker.
    /// Call BEFORE SaveChangesAsync.
    /// </summary>
    private async Task RecalculateTotalsAsync(PortfolioCoin portfolioCoin)
    {
        var persisted = await _db.CoinTransactions
            .Where(t => t.PortfolioCoinId == portfolioCoin.Id)
            .ToListAsync();

        var deletedIds = _db.ChangeTracker.Entries<CoinTransaction>()
            .Where(e => e.State == EntityState.Deleted && e.Entity.PortfolioCoinId == portfolioCoin.Id)
            .Select(e => e.Entity.Id)
            .ToHashSet();
        persisted = persisted.Where(t => !deletedIds.Contains(t.Id)).ToList();

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

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadPortfolio(id))
            return NotFound();

        await LoadPrivacyModeAsync();
        await LoadCoinOptions();

        var coins = await _coins.GetAllAsync();
        var xmr = coins.FirstOrDefault(c => c.Symbol.ToUpper() == "XMR");
        if (xmr != null)
        {
            CoinId = xmr.Id;
            PriceUsdAtTime = xmr.PriceUsd;
            XmrPrice = xmr.PriceUsd;
        }

        TransactedAt = DateTime.UtcNow;

        if (TempData["ImportMessage"] is string msg)
        {
            ImportMessage = msg;
            ImportSuccess = TempData["ImportSuccess"] as bool? ?? false;
        }

        Allocations = Portfolio!.PortfolioCoins
            .Select(pc => new AllocationSlice
            {
                Symbol = pc.Coin.Symbol.ToUpper(),
                ValueUsd = pc.TotalAmount * pc.Coin.PriceUsd
            })
            .Where(a => a.ValueUsd > 0)
            .OrderByDescending(a => a.ValueUsd)
            .ToList();

        PnlSeries = await BuildPnlSeriesAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostDeletePortfolioAsync(int id)
    {
        if (!await LoadPortfolio(id))
            return NotFound();

        await _portfolios.DeletePortfolioAsync(id);
        return RedirectToPage("/Portfolios/Index");
    }

    public async Task<IActionResult> OnPostAddTransactionAsync(int id)
    {
        if (!await LoadPortfolio(id))
            return NotFound();

        var portfolioCoin = Portfolio!.PortfolioCoins.FirstOrDefault(pc => pc.CoinId == CoinId);
        if (portfolioCoin == null)
        {
            portfolioCoin = new PortfolioCoin
            {
                PortfolioId = id,
                CoinId = CoinId
            };
            _db.PortfolioCoins.Add(portfolioCoin);
            await _db.SaveChangesAsync();
        }

        var tx = new CoinTransaction
        {
            PortfolioCoinId = portfolioCoin.Id,
            Type = TransactionType,
            Amount = Amount,
            PriceUsdAtTime = PriceUsdAtTime,
            Notes = Notes,
            ExternalTransactionId = string.IsNullOrWhiteSpace(ExternalTransactionId) ? null : ExternalTransactionId.Trim(),
            TransactedAt = TransactedAt == default
                ? DateTime.UtcNow
                : DateTime.SpecifyKind(TransactedAt, DateTimeKind.Utc)
        };

        _db.CoinTransactions.Add(tx);
        await RecalculateTotalsAsync(portfolioCoin);
        await _db.SaveChangesAsync();

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostImportMoneroAsync(int id)
    {
        if (!await LoadPortfolio(id))
            return NotFound();

        if (ImportFile == null || ImportFile.Length == 0)
        {
            TempData["ImportMessage"] = "No file provided.";
            TempData["ImportSuccess"] = false;
            return RedirectToPage(new { id });
        }

        if (ImportFile.Length > 5_000_000)
        {
            TempData["ImportMessage"] = "File is too large (max 5 MB).";
            TempData["ImportSuccess"] = false;
            return RedirectToPage(new { id });
        }

        var xmrCoin = await _db.Coins.FirstOrDefaultAsync(c => c.Symbol.ToUpper() == "XMR");
        if (xmrCoin == null)
        {
            TempData["ImportMessage"] = "XMR coin not found in system.";
            TempData["ImportSuccess"] = false;
            return RedirectToPage(new { id });
        }

        var portfolioCoin = Portfolio!.PortfolioCoins.FirstOrDefault(pc => pc.CoinId == xmrCoin.Id);
        if (portfolioCoin == null)
        {
            portfolioCoin = new PortfolioCoin
            {
                PortfolioId = id,
                CoinId = xmrCoin.Id
            };
            _db.PortfolioCoins.Add(portfolioCoin);
            await _db.SaveChangesAsync();
        }

        int imported = 0, skipped = 0, errors = 0;
        var errorSamples = new List<string>();

        var priceHistory = await _db.CoinPriceHistories
            .Where(h => h.Coin.CoinGeckoId == xmrCoin.CoinGeckoId && h.Interval == "1d")
            .Select(h => new { h.RecordedAt, h.PriceUsd })
            .ToListAsync();

        var priceByDate = priceHistory
            .GroupBy(h => h.RecordedAt.Date)
            .ToDictionary(g => g.Key, g => g.Last().PriceUsd);

        var existingTxIds = await _db.CoinTransactions
            .Where(t => t.PortfolioCoinId == portfolioCoin.Id && t.ExternalTransactionId != null)
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
            return RedirectToPage(new { id });
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
            return RedirectToPage(new { id });
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

                decimal priceUsd;
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
                    priceUsd = earlier.Key != default ? earlier.Value : xmrCoin.PriceUsd;
                }

                var label = idxLabel >= 0 ? fields[idxLabel].Trim() : string.Empty;
                var desc = idxDesc >= 0 ? fields[idxDesc].Trim() : string.Empty;
                var notes = string.Join(" · ", new[] { label, desc }
                    .Where(s => !string.IsNullOrWhiteSpace(s) && s != "Primary account"));
                if (string.IsNullOrWhiteSpace(notes)) notes = "Imported from Monero GUI";

                var tx = new CoinTransaction
                {
                    PortfolioCoinId = portfolioCoin.Id,
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
            await RecalculateTotalsAsync(portfolioCoin);
            await _db.SaveChangesAsync();
        }

        var summary = $"Imported {imported} XMR transaction(s), skipped {skipped} duplicate(s), {errors} error(s).";
        if (errorSamples.Any())
            summary += " First errors: " + string.Join("; ", errorSamples);

        TempData["ImportMessage"] = summary;
        TempData["ImportSuccess"] = imported > 0;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteTransactionAsync(int id, int transactionId)
    {
        if (!await LoadPortfolio(id))
            return NotFound();

        var tx = Portfolio!.PortfolioCoins
            .SelectMany(pc => pc.Transactions)
            .FirstOrDefault(t => t.Id == transactionId);

        if (tx == null)
            return NotFound();

        var portfolioCoinId = tx.PortfolioCoinId;

        await _portfolios.DeleteTransactionAsync(transactionId);

        var refreshed = await _db.PortfolioCoins
            .FirstOrDefaultAsync(pc => pc.Id == portfolioCoinId);
        if (refreshed != null)
        {
            await RecalculateTotalsAsync(refreshed);
            await _db.SaveChangesAsync();
        }

        return RedirectToPage(new { id });
    }

    private async Task LoadCoinOptions()
    {
        var coins = await _coins.GetAllAsync();
        CoinOptions = coins
            .OrderBy(c => c.Symbol)
            .Select(c => new SelectListItem($"{c.Symbol} - {c.Name}", c.Id.ToString()))
            .ToList();
        CoinPrices = coins.ToDictionary(c => c.Id, c => c.PriceUsd);
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

    private async Task<IReadOnlyList<PnlPoint>> BuildPnlSeriesAsync()
    {
        if (Portfolio == null || !Portfolio.PortfolioCoins.Any())
            return new List<PnlPoint>();

        var allTxs = Portfolio.PortfolioCoins
            .SelectMany(pc => pc.Transactions.Select(t => new
            {
                CoinId = pc.CoinId,
                Symbol = pc.Coin.Symbol.ToUpper(),
                CoinGeckoId = pc.Coin.CoinGeckoId,
                t.TransactedAt,
                t.Type,
                t.Amount,
                t.PriceUsdAtTime,
                t.TotalUsd
            }))
            .OrderBy(t => t.TransactedAt)
            .ToList();

        if (!allTxs.Any()) return new List<PnlPoint>();

        var startDate = allTxs.First().TransactedAt.Date;
        var endDate = DateTime.UtcNow.Date;

        var coinGeckoIds = Portfolio.PortfolioCoins
            .Select(pc => pc.Coin.CoinGeckoId)
            .Distinct()
            .ToList();

        var historyRaw = await _db.CoinPriceHistories
            .Where(h => coinGeckoIds.Contains(h.Coin.CoinGeckoId)
                     && h.Interval == "1d"
                     && h.RecordedAt >= startDate)
            .Select(h => new
            {
                CoinGeckoId = h.Coin.CoinGeckoId,
                Date = h.RecordedAt.Date,
                h.PriceUsd
            })
            .ToListAsync();

        var priceLookup = historyRaw
            .GroupBy(h => h.CoinGeckoId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(x => x.Date)
                      .ToDictionary(dg => dg.Key, dg => dg.Last().PriceUsd));

        var xmrGeckoId = Portfolio.PortfolioCoins
            .FirstOrDefault(pc => pc.Coin.Symbol.ToUpper() == "XMR")?.Coin.CoinGeckoId;
        if (xmrGeckoId == null)
        {
            var xmrCoin = await _db.Coins.FirstOrDefaultAsync(c => c.Symbol.ToUpper() == "XMR");
            if (xmrCoin != null)
            {
                xmrGeckoId = xmrCoin.CoinGeckoId;
                var xmrHistory = await _db.CoinPriceHistories
                    .Where(h => h.Coin.CoinGeckoId == xmrGeckoId
                             && h.Interval == "1d"
                             && h.RecordedAt >= startDate)
                    .Select(h => new { Date = h.RecordedAt.Date, h.PriceUsd })
                    .ToListAsync();
                priceLookup[xmrGeckoId] = xmrHistory
                    .GroupBy(x => x.Date)
                    .ToDictionary(dg => dg.Key, dg => dg.Last().PriceUsd);
            }
        }

        var result = new List<PnlPoint>();
        var holdings = new Dictionary<int, decimal>();
        var costBasis = new Dictionary<int, decimal>();
        var coinIdToGeckoId = Portfolio.PortfolioCoins
            .ToDictionary(pc => pc.CoinId, pc => pc.Coin.CoinGeckoId);

        int txIdx = 0;
        var lastPrice = new Dictionary<string, decimal>();

        for (var day = startDate; day <= endDate; day = day.AddDays(1))
        {
            while (txIdx < allTxs.Count && allTxs[txIdx].TransactedAt.Date <= day)
            {
                var tx = allTxs[txIdx];
                if (!holdings.ContainsKey(tx.CoinId))
                {
                    holdings[tx.CoinId] = 0;
                    costBasis[tx.CoinId] = 0;
                }
                if (tx.Type == TransactionType.Buy)
                {
                    holdings[tx.CoinId] += tx.Amount;
                    costBasis[tx.CoinId] += tx.TotalUsd;
                }
                else
                {
                    var prevAmount = holdings[tx.CoinId];
                    if (prevAmount > 0)
                    {
                        var proportion = Math.Min(tx.Amount / prevAmount, 1m);
                        costBasis[tx.CoinId] -= costBasis[tx.CoinId] * proportion;
                    }
                    holdings[tx.CoinId] -= tx.Amount;
                    if (holdings[tx.CoinId] < 0) holdings[tx.CoinId] = 0;
                }
                txIdx++;
            }

            decimal valueUsd = 0;
            decimal totalBasis = 0;
            foreach (var kv in holdings)
            {
                if (kv.Value <= 0) continue;
                var gecko = coinIdToGeckoId[kv.Key];
                decimal price = 0;
                if (priceLookup.TryGetValue(gecko, out var coinPrices)
                    && coinPrices.TryGetValue(day, out var p))
                {
                    price = p;
                    lastPrice[gecko] = p;
                }
                else if (lastPrice.TryGetValue(gecko, out var lp))
                {
                    price = lp;
                }
                valueUsd += kv.Value * price;
                totalBasis += costBasis[kv.Key];
            }

            var pnlUsd = valueUsd - totalBasis;

            decimal xmrPriceThisDay = 0;
            if (xmrGeckoId != null && priceLookup.TryGetValue(xmrGeckoId, out var xmrPrices))
            {
                if (xmrPrices.TryGetValue(day, out var xp))
                {
                    xmrPriceThisDay = xp;
                    lastPrice[xmrGeckoId] = xp;
                }
                else if (lastPrice.TryGetValue(xmrGeckoId, out var lastXmr))
                {
                    xmrPriceThisDay = lastXmr;
                }
            }

            var pnlXmr = xmrPriceThisDay > 0 ? pnlUsd / xmrPriceThisDay : 0;

            result.Add(new PnlPoint
            {
                TimestampMs = new DateTimeOffset(day, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                PnlUsd = (double)pnlUsd,
                PnlXmr = (double)pnlXmr
            });
        }

        return result;
    }

    public class AllocationSlice
    {
        public string Symbol { get; set; } = "";
        public decimal ValueUsd { get; set; }
    }

    public class PnlPoint
    {
        public long TimestampMs { get; set; }
        public double PnlUsd { get; set; }
        public double PnlXmr { get; set; }
    }
}