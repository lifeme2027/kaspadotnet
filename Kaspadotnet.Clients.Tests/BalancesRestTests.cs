using System.Net;
using System.Text;
using System.Text.Json;
using Kaspadotnet.Clients;
using Xunit;

namespace Kaspadotnet.Clients.Tests;

public class BalancesRestTests
{
    private const string A = "kaspa:qp0l70zd5x85ttwd6jv7g3s3a8llzj96d8dncn4zmhv4tlzx5k2jyqh70xmfj";
    private const string B = "kaspa:qr0lr4ml9fn3chekrqmjdkergxl93l4wrk3dankcgvjq776s9wn9jkdskewva";

    private static KaspaPublicRestClient Client(Stub handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://rest.test/") });

    [Fact]
    public async Task Balances_posts_addresses_and_keeps_request_order()
    {
        var handler = new Stub($$"""[{"address":"{{B}}","balance":"250"},{"address":"{{A}}","balance":100000000}]""");
        using var client = Client(handler);

        var result = await client.GetBalancesAsync(new[] { A, B });

        Assert.Equal("POST", handler.Method);
        Assert.Equal("/addresses/balances", handler.Path);
        using var doc = JsonDocument.Parse(handler.Body!);
        var sent = doc.RootElement.GetProperty("addresses").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { A, B }, sent);
        Assert.Equal(2, result.Count);
        Assert.Equal(A, result[0].Address);
        Assert.Equal(100_000_000UL, result[0].Sompi);
        Assert.Equal(1m, result[0].Kas);
        Assert.Equal(B, result[1].Address);
        Assert.Equal(250UL, result[1].Sompi);
    }

    [Fact]
    public async Task Balances_rejects_invalid_or_empty_input_without_http()
    {
        var handler = new Stub("[]");
        using var client = Client(handler);

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetBalancesAsync(new[] { "kaspa:notanaddress" }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetBalancesAsync(Array.Empty<string>()));
        Assert.Null(handler.Method);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""[null]""")]
    [InlineData("""[{"balance":5}]""")]
    [InlineData("not json")]
    public async Task Balances_rejects_incomplete_or_malformed_responses(string body)
    {
        using var client = Client(new Stub(body));
        await Assert.ThrowsAsync<KaspaRpcException>(() => client.GetBalancesAsync(new[] { A }));
    }

    [Fact]
    public async Task Balances_http_error_throws()
    {
        using var client = Client(new Stub("""{"detail":"rate limited"}""", HttpStatusCode.BadRequest));
        var ex = await Assert.ThrowsAsync<KaspaRpcException>(() => client.GetBalancesAsync(new[] { A }));
        Assert.Contains("400", ex.Message);
    }

    private sealed class Stub : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public Stub(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public string? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json"), RequestMessage = request };
        }
    }
}
