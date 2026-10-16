using System.Net;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using MoneroMarketCap.Data;
using MoneroMarketCap.Data.Models;
using MoneroMarketCap.Web.Helpers;
using MoneroMarketCap.Web.Services;
using Xunit;

namespace MoneroMarketCap.Tests;

public class WebTests
{
    [Fact]
    public void Captcha_IsOneTimeUse_AndWrongAnswerBurnsToken()
    {
        var store = new CaptchaStore(new MemoryCache(new MemoryCacheOptions()));

        var token = store.Create(12);
        Assert.True(store.TryConsume(token, 12));
        Assert.False(store.TryConsume(token, 12)); // replay rejected

        var token2 = store.Create(7);
        Assert.False(store.TryConsume(token2, 8));
        Assert.False(store.TryConsume(token2, 7)); // a wrong guess consumed it

        Assert.False(store.TryConsume(null, 1));
        Assert.False(store.TryConsume("not-a-token", 1));
    }

    private static ClaimsPrincipal Principal(string name, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name), new(ClaimTypes.NameIdentifier, "5") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "CookieAuth"));
    }

    [Fact]
    public void CookieValidator_RejectsDeletedUser_ReplacesOnRoleChange()
    {
        var p = Principal("alice", "Admin");

        Assert.Equal(CookiePrincipalValidator.Outcome.Reject, CookiePrincipalValidator.Decide(p, null));
        Assert.Equal(
            CookiePrincipalValidator.Outcome.Replace,
            CookiePrincipalValidator.Decide(p, new CookiePrincipalValidator.UserSnapshot(5, "alice", Array.Empty<string>())));
        Assert.Equal(
            CookiePrincipalValidator.Outcome.Valid,
            CookiePrincipalValidator.Decide(p, new CookiePrincipalValidator.UserSnapshot(5, "alice", new[] { "Admin" })));

        var rebuilt = CookiePrincipalValidator.BuildPrincipal(
            new CookiePrincipalValidator.UserSnapshot(5, "alice", new[] { "Moderator" }), "CookieAuth");
        Assert.True(rebuilt.IsInRole("Moderator"));
        Assert.False(rebuilt.IsInRole("Admin"));
        Assert.Equal("5", rebuilt.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public void Suggest_EscapesLikeWildcards()
    {
        Assert.Equal("100\\%", CoinSuggest.EscapeLike("100%"));
        Assert.Equal("a\\_b", CoinSuggest.EscapeLike("a_b"));
        Assert.Equal("x\\\\y", CoinSuggest.EscapeLike("x\\y"));
    }

    [Fact]
    public void Suggest_TranslatesToSqlWithEscapeAndLimit()
    {
        // ToQueryString only needs the provider's SQL generator; no connection is opened.
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=designtime;Username=designtime")
            .Options);

        var sql = CoinSuggest.Query(db.Coins, "b%", 10).ToQueryString();

        Assert.Contains("ILIKE", sql);
        Assert.Contains("ESCAPE", sql);
        Assert.Contains("LIMIT", sql);
        Assert.Contains("b\\%", sql);
    }

    [Fact]
    public async Task KnownCoinIds_OnlyAcceptsTrackedIds()
    {
        var name = nameof(KnownCoinIds_OnlyAcceptsTrackedIds);
        using var sp = TestDb.Services(name);
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Coins.Add(new Coin { CoinGeckoId = "monero", Symbol = "XMR" });
            await db.SaveChangesAsync();
        }

        var scopes = sp.GetRequiredService<IServiceScopeFactory>();
        var cache = sp.GetRequiredService<IMemoryCache>();
        Assert.True(await KnownCoinIds.IsKnownAsync("monero", scopes, cache));
        Assert.False(await KnownCoinIds.IsKnownAsync("not-a-coin", scopes, cache));
        Assert.False(await KnownCoinIds.IsKnownAsync(new string('a', 500), scopes, cache));
        Assert.False(await KnownCoinIds.IsKnownAsync(null, scopes, cache));
    }
}
