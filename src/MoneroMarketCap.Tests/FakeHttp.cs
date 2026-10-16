using System.Net;
using System.Text;

namespace MoneroMarketCap.Tests;

/// <summary>HttpMessageHandler driven by a delegate; counts calls.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;
    private int calls;

    public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => this.respond = respond;

    public int Calls => this.calls;

    public static HttpResponseMessage Text(string body, string mediaType, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.calls);
        return this.respond(request, cancellationToken);
    }
}

internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler handler;

    public FakeHttpClientFactory(HttpMessageHandler handler) => this.handler = handler;

    public HttpClient CreateClient(string name) => new(this.handler, disposeHandler: false);
}
