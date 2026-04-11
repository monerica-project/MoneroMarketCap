using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Data.Repositories;

namespace MoneroMarketCap.Pages.Coins;

public class IndexModel : PageModel
{
    private readonly ICoinRepository _coins;
    public IReadOnlyList<Coin> AllCoins { get; set; } = new List<Coin>();

    public IndexModel(ICoinRepository coins) => _coins = coins;

    public async Task OnGetAsync()
    {
        AllCoins = await _coins.GetAllAsync();
    }
}