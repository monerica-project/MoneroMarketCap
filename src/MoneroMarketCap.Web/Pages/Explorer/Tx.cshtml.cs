using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Web.Services;

namespace MoneroMarketCap.Web.Pages.Explorer;

public class TxModel : PageModel
{
    private readonly MoneroExplorer _explorer;

    public TxModel(MoneroExplorer explorer) => _explorer = explorer;

    public ExplorerTx? Tx { get; private set; }

    public async Task<IActionResult> OnGetAsync(string hash, CancellationToken ct)
    {
        if (!MoneroExplorer.IsHash(hash))
        {
            return NotFound();
        }

        Tx = await _explorer.GetTransactionAsync(hash, ct);
        return Tx is null ? NotFound() : Page();
    }
}
