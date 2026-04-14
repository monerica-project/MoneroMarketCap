using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using MoneroMarketCap.Services.Interfaces;

namespace MoneroMarketCap.Web.Controllers;

[ApiController]
[Route("api/coins")]
public class CoinsController : ControllerBase
{
    private readonly ICoinGeckoService _gecko;
    private readonly IMemoryCache _cache;

    public CoinsController(ICoinGeckoService gecko, IMemoryCache cache)
    {
        _gecko = gecko;
        _cache = cache;
    }

    [HttpGet("{cgId}/history")]
    public async Task<IActionResult> History(string cgId)
    {
        var cacheKey = $"history_{cgId}";

        if (_cache.TryGetValue(cacheKey, out string? cached))
            return Content(cached!, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var prices = await _gecko.GetMarketChartAsync(cgId);

            if (prices == null)
                return StatusCode(503, "Chart data unavailable");

            _cache.Set(cacheKey, prices, TimeSpan.FromHours(6));

            return Content(prices, "application/json");
        }
        catch (OperationCanceledException)
        {
            return StatusCode(503, "Chart data timed out");
        }
    }
}
