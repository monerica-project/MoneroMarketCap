using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;
using MoneroMarketCap.Services.Implementations;
using MoneroMarketCap.Services.Interfaces;
using MoneroMarketCap.Services.Models;

namespace MoneroMarketCap.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class AddCoinModel : PageModel
{
    private readonly ICoinRepository _coins;
    private readonly ICoinGeckoService _gecko;

    public List<CoinGeckoSearchResult> SearchResults { get; set; } = new();
    public List<string> Added { get; set; } = new();
    public List<string> Skipped { get; set; } = new();

    [BindProperty] public string SearchQuery { get; set; } = string.Empty;
    [BindProperty] public string CoinGeckoId { get; set; } = string.Empty;
    [BindProperty] public List<string> BulkIds { get; set; } = new();

    public static readonly List<(string Id, string Label)> PopularCoins = new()
    {
        ("bitcoin",          "BTC — Bitcoin"),
        ("ethereum",         "ETH — Ethereum"),
        ("monero",           "XMR — Monero"),
        ("litecoin",         "LTC — Litecoin"),
        ("bitcoin-cash",     "BCH — Bitcoin Cash"),
        ("zcash",            "ZEC — Zcash"),
        ("dash",             "DASH — Dash"),
        ("dogecoin",         "DOGE — Dogecoin"),
        ("tether",           "USDT — Tether"),
        ("usd-coin",         "USDC — USD Coin"),
        ("solana",           "SOL — Solana"),
        ("cardano",          "ADA — Cardano"),
        ("ripple",           "XRP — XRP"),
        ("polkadot",         "DOT — Polkadot"),
        ("chainlink",        "LINK — Chainlink"),
        ("binancecoin",      "BNB — BNB"),
        ("avalanche-2",      "AVAX — Avalanche"),
        ("matic-network",    "MATIC — Polygon"),
        ("cosmos",           "ATOM — Cosmos"),
        ("near",             "NEAR — NEAR Protocol"),
    };

    public AddCoinModel(ICoinRepository coins, ICoinGeckoService gecko)
    {
        _coins = coins;
        _gecko = gecko;
    }

    public void OnGet() { }

    public async Task OnPostSearchAsync()
    {
        if (!string.IsNullOrWhiteSpace(SearchQuery))
            SearchResults = await _gecko.SearchCoinsAsync(SearchQuery);
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (string.IsNullOrWhiteSpace(CoinGeckoId))
            return Page();

        await AddCoinByIdAsync(CoinGeckoId);
        TempData["Status"] = Added.Any()
            ? $"Added {string.Join(", ", Added)}."
            : $"Skipped: {string.Join(", ", Skipped)}";

        return RedirectToPage("/Admin/Index");
    }

    public async Task<IActionResult> OnPostBulkAddAsync()
    {
        foreach (var id in BulkIds)
            await AddCoinByIdAsync(id);

        TempData["Status"] = $"Added: {string.Join(", ", Added)}. " +
                             (Skipped.Any() ? $"Already existed: {string.Join(", ", Skipped)}." : "");

        return RedirectToPage("/Admin/Index");
    }

    private async Task AddCoinByIdAsync(string geckoId)
    {
        var m = await _gecko.GetMarketDataAsync(geckoId);
        if (m == null) { Skipped.Add(geckoId); return; }

        var existing = await _coins.GetBySymbolAsync(m.Symbol.ToUpper());
        if (existing != null) { Skipped.Add(m.Symbol.ToUpper()); return; }

        await _coins.AddAsync(new Coin
        {
            CoinGeckoId = m.Id,
            Symbol = m.Symbol.ToUpper(),
            Name = m.Name,
            ImageUrl = m.Image,
            PriceUsd = m.CurrentPrice ?? 0,
            MarketCapUsd = m.MarketCap ?? 0,
            MarketCapRank = m.MarketCapRank ?? 0,
            FullyDilutedValuation = m.FullyDilutedValuation ?? 0,
            TotalVolume = m.TotalVolume ?? 0,
            High24h = m.High24h ?? 0,
            Low24h = m.Low24h ?? 0,
            CirculatingSupply = m.CirculatingSupply ?? 0,
            TotalSupply = m.TotalSupply ?? 0,
            MaxSupply = m.MaxSupply,
            Ath = m.Ath ?? 0,
            AthChangePercentage = m.AthChangePercentage ?? 0,
            AthDate = m.AthDate,
            Atl = m.Atl ?? 0,
            AtlChangePercentage = m.AtlChangePercentage ?? 0,
            AtlDate = m.AtlDate,
            PriceChangePercent24h = m.PriceChangePercentage24h ?? 0,
            IsActive = true
        });
        await _coins.SaveChangesAsync();
        Added.Add(m.Symbol.ToUpper());
    }
}