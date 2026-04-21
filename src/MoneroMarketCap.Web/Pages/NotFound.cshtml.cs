using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MoneroMarketCap.Pages;

public class NotFoundModel : PageModel
{
    public void OnGet()
    {
        Response.StatusCode = 404;
    }
}
