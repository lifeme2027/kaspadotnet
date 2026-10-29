using System.Net;
using System.Text;
using Kaspadotnet.Clients;
using Kaspadotnet.Clients.Http;
using Xunit;

namespace Kaspadotnet.Clients.Tests;

public class FeeEstimateRestTests
{
    private static KaspaPublicRestClient Client(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://rest.test/") });

    [Fact]
    public async Task Fee_estimate_parses_all_buckets_and_hits_route()
    {
        var handler = new StubHandler("""
            {"priorityBucket":{"feerate":3.5,"estimatedSeconds":0.004},
             "normalBuckets":[{"feerate":1.2,"estimatedSeconds":"1.5"},{"feerate":1,"estimatedSeconds":2}],
             "lowBuckets":[{"feerate":1,"estimatedSeconds":30}]}
            """);
        using var client = Client(handler);

        var fee = await client.GetFeeEstimateAsync();

        Assert.Equal("GET", handler.Method);
        Assert.Equal("/info/fee-estimate", handler.Path);
        Assert.Equal(3.5, fee.PriorityBucket.FeeRate);
        Assert.Equal(0.004, fee.PriorityBucket.EstimatedSeconds);
        Assert.Equal(2, fee.NormalBuckets.Count);
        Assert.Equal(1.5, fee.NormalBuckets[0].EstimatedSeconds);
        Assert.Equal(1.2, fee.NormalFeeRate);
        Assert.Equal(1.0, fee.LowFeeRate);
    }

    [Fact]
    public async Task Fee_estimate_without_normal_or_low_buckets_falls_back_to_priority()
    {
        using var client = Client(new StubHandler("""{"priorityBucket":{"feerate":2}}"""));

        var fee = await client.GetFeeEstimateAsync();

        Assert.Empty(fee.NormalBuckets);
        Assert.Empty(fee.LowBuckets);
        Assert.Equal(2.0, fee.NormalFeeRate);
        Assert.Equal(2.0, fee.LowFeeRate);
        Assert.Equal(0.0, fee.PriorityBucket.EstimatedSeconds);
    }

    [Theory]
    [InlineData("""{"normalBuckets":[]}""")]
    [InlineData("""{"priorityBucket":{"estimatedSeconds":1}}""")]
    [InlineData("""{"priorityBucket":{"feerate":-1}}""")]
    [InlineData("""{"priorityBucket":{"feerate":1},"lowBuckets":[null]}""")]
    [InlineData("""{"priorityBucket":{"feerate":1,"estimatedSeconds":-3}}""")]
    [InlineData("not json")]
    public async Task Fee_estimate_rejects_malformed_responses(string body)
    {
        using var client = Client(new StubHandler(body));
        await Assert.ThrowsAsync<KaspaRpcException>(() => client.GetFeeEstimateAsync());
    }

    [Fact]
    public async Task Fee_estimate_http_error_is_reported_with_route()
    {
        using var client = Client(new StubHandler("down", HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<KaspaRpcException>(() => client.GetFeeEstimateAsync());
        Assert.Contains("404", ex.Message);
        Assert.Contains("info/fee-estimate", ex.Message);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public string? Method { get; private set; }
        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }
}
