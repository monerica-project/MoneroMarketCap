using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MoneroMarketCap.Web.Services;

namespace MoneroMarketCap.Web.Pages.Explorer;

public class IndexModel : PageModel
{
    private readonly MoneroExplorer _explorer;

    public IndexModel(MoneroExplorer explorer) => _explorer = explorer;

    public ExplorerSummary? Summary { get; private set; }

    public IReadOnlyList<ExplorerBlockHeader> Blocks { get; private set; } = [];

    public IReadOnlyList<string> Pool { get; private set; } = [];

    public string? SearchError { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var q = (Q ?? string.Empty).Trim();
        if (q.Length > 0)
        {
            // A number is a block height. A 64-character hash is a transaction, or else a block.
            if (MoneroExplorer.IsHeight(q, out var height))
            {
                return Redirect($"/explorer/block/{height}");
            }

            if (MoneroExplorer.IsHash(q))
            {
                var hash = q.ToLowerInvariant();
                if (await _explorer.GetTransactionAsync(hash, ct) is not null)
                {
                    return Redirect($"/explorer/tx/{hash}");
                }

                if (await _explorer.GetBlockAsync(hash, ct) is not null)
                {
                    return Redirect($"/explorer/block/{hash}");
                }

                SearchError = "Nothing found for that hash. It may be too new, or mistyped.";
            }
            else
            {
                SearchError = "Enter a block height (a number) or a 64-character transaction or block hash.";
            }

            Q = q.Length > 64 ? q[..64] : q;
        }

        Summary = await _explorer.GetSummaryAsync(ct);
        Blocks = await _explorer.GetLatestBlocksAsync(ct);
        Pool = await _explorer.GetPoolHashesAsync(ct) ?? [];
        return Page();
    }
}
