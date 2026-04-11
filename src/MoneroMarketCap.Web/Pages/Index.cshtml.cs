using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;

namespace MoneroMarketCap.Pages;

public class IndexModel : PageModel
{
    private readonly ICoinRepository _coins;

    public IReadOnlyList<Coin> Coins { get; set; } = new List<Coin>();
    public Coin? Monero { get; set; }

    public IndexModel(ICoinRepository coins) => _coins = coins;

    public async Task OnGetAsync()
    {
        Coins = await _coins.GetAllAsync();
        Monero = Coins.FirstOrDefault(c => c.Symbol.ToUpper() == "XMR");
    }
}