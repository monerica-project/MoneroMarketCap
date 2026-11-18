using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Web.Services;

namespace MoneroMarketCap.Web.Pages.Explorer;

public class BlockModel : PageModel
{
    private readonly MoneroExplorer _explorer;

    public BlockModel(MoneroExplorer explorer) => _explorer = explorer;

    public ExplorerBlock? Block { get; private set; }

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken ct)
    {
        if (!MoneroExplorer.IsHash(id) && !MoneroExplorer.IsHeight(id, out _))
        {
            return NotFound();
        }

        Block = await _explorer.GetBlockAsync(id, ct);
        return Block is null ? NotFound() : Page();
    }
}
