using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;

namespace MoneroMarketCap.Pages;

public class IndexModel : PageModel
{
    private readonly ICoinRepository _coins;
    private readonly IConfiguration _config;

    public int SponsorRotateIntervalSeconds { get; set; }
    public IReadOnlyList<Coin> Coins { get; set; } = new List<Coin>();
    public Coin? Monero { get; set; }

    public IndexModel(ICoinRepository coins, IConfiguration config)
    {
        _coins = coins;
        _config = config;
    }

    public async Task OnGetAsync()
    {
        Coins = await _coins.GetAllAsync();
        Monero = await _coins.GetByCoinGeckoIdAsync("monero");
        SponsorRotateIntervalSeconds = _config.GetValue<int>("Sponsors:RotateIntervalSeconds" +
            "", 30);
    }
}