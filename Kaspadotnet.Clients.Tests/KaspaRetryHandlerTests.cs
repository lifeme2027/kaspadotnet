using System.Net;
using System.Text;
using Kaspadotnet.Clients.Http;
using Xunit;

namespace Kaspadotnet.Clients.Tests;

public class KaspaRetryHandlerTests
{
    private const string Address =
        "kaspa:qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqkx9awp4e";

    private const string BalanceJson =
        """{"address":"kaspa:qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqkx9awp4e","balance":7}""";

    private static KaspaRetryOptions Options(List<TimeSpan> delays, int maxRetries = 3, TimeSpan? maxDelay = null) => new()
    {
        MaxRetries = maxRetries,
        BaseDelay = TimeSpan.FromMilliseconds(100),
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(10),
        Jitter = false,
        DelayAsync = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        },
    };

    [Fact]
    public void Compute_delay_doubles_and_is_capped()
    {
        var options = Options(new List<TimeSpan>(), maxDelay: TimeSpan.FromMilliseconds(350));

        Assert.Equal(TimeSpan.FromMilliseconds(100), KaspaRetryHandler.ComputeDelay(options, 1, null));
        Assert.Equal(TimeSpan.FromMilliseconds(200), KaspaRetryHandler.ComputeDelay(options, 2, null));
        Assert.Equal(TimeSpan.FromMilliseconds(350), KaspaRetryHandler.ComputeDelay(options, 3, null));
        Assert.Equal(TimeSpan.FromMilliseconds(350), KaspaRetryHandler.ComputeDelay(options, 60, null));
    }

    [Fact]
    public void Compute_delay_prefers_retry_after_but_caps_it()
    {
        var options = Options(new List<TimeSpan>(), maxDelay: TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(2), KaspaRetryHandler.ComputeDelay(options, 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(5), KaspaRetryHandler.ComputeDelay(options, 1, TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.Zero, KaspaRetryHandler.ComputeDelay(options, 1, TimeSpan.FromSeconds(-3)));
    }

    [Fact]
    public void Compute_delay_applies_jitter_factor_only_when_enabled()
    {
        var jittered = new KaspaRetryOptions { BaseDelay = TimeSpan.FromMilliseconds(100), Jitter = true };

        Assert.Equal(TimeSpan.FromMilliseconds(50), KaspaRetryHandler.ComputeDelay(jittered, 1, null, 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(100), KaspaRetryHandler.ComputeDelay(Options(new List<TimeSpan>()), 1, null, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => KaspaRetryHandler.ComputeDelay(jittered, 0, null));
    }

    [Fact]
    public void Read_retry_after_supports_seconds_and_http_dates()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        using var seconds = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        seconds.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        using var date = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        date.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddSeconds(30));
        using var past = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        past.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddSeconds(-30));
        using var none = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        Assert.Equal(TimeSpan.FromSeconds(7), KaspaRetryHandler.ReadRetryAfter(seconds, now));
        Assert.Equal(TimeSpan.FromSeconds(30), KaspaRetryHandler.ReadRetryAfter(date, now));
        Assert.Equal(TimeSpan.Zero, KaspaRetryHandler.ReadRetryAfter(past, now));
        Assert.Null(KaspaRetryHandler.ReadRetryAfter(none, now));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.NotImplemented, false)]
    public void Transient_status_classification(HttpStatusCode status, bool expected)
    {
        Assert.Equal(expected, KaspaRetryHandler.IsTransientStatus(status));
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        var inner = new ScriptedHandler();

        Assert.Throws<ArgumentOutOfRangeException>(() => new KaspaRetryHandler(inner, new KaspaRetryOptions { MaxRetries = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KaspaRetryHandler(inner, new KaspaRetryOptions { BaseDelay = TimeSpan.FromSeconds(20) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KaspaRetryHandler(inner, new KaspaRetryOptions { PerAttemptTimeout = TimeSpan.Zero }));
    }

    [Fact]
    public async Task Get_is_retried_until_success_with_backoff()
    {
        var delays = new List<TimeSpan>();
        var inner = new ScriptedHandler(
            Respond(HttpStatusCode.ServiceUnavailable, "{}"),
            Respond(HttpStatusCode.BadGateway, "{}"),
            Respond(HttpStatusCode.OK, BalanceJson));
        using var http = new HttpClient(new KaspaRetryHandler(inner, Options(delays))) { BaseAddress = new Uri("http://rest.test/") };
        using var client = new KaspaPublicRestClient(http);

        var balance = await client.GetBalanceAsync(Address);

        Assert.Equal(7UL, balance.Sompi);
        Assert.Equal(3, inner.Calls);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200) }, delays);
    }

    [Fact]
    public async Task Retry_after_header_sets_the_wait()
    {
        var delays = new List<TimeSpan>();
        var limited = Respond(HttpStatusCode.TooManyRequests, "{}");
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
        var inner = new ScriptedHandler(limited, Respond(HttpStatusCode.OK, BalanceJson));
        using var http = new HttpClient(new KaspaRetryHandler(inner, Options(delays))) { BaseAddress = new Uri("http://rest.test/") };

        using var response = await http.GetAsync("addresses/x/balance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, delays);
    }

    [Fact]
    public async Task Exhausted_retries_surface_the_last_error_through_the_client()
    {
        var delays = new List<TimeSpan>();
        var inner = new ScriptedHandler(Respond(HttpStatusCode.ServiceUnavailable, """{"detail":"busy"}"""));
        using var http = new HttpClient(new KaspaRetryHandler(inner, Options(delays, maxRetries: 2))) { BaseAddress = new Uri("http://rest.test/") };
        using var client = new KaspaPublicRestClient(http);

        var error = await Assert.ThrowsAsync<KaspaRpcException>(() => client.GetInfoAsync());

        Assert.Contains("503", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, inner.Calls);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task Client_errors_are_not_retried()
    {
        var delays = new List<TimeSpan>();
        var inner = new ScriptedHandler(Respond(HttpStatusCode.NotFound, "{}"));
        using var http = new HttpClient(new KaspaRetryHandler(inner, Options(delays))) { BaseAddress = new Uri("http://rest.test/") };
        using var client = new KaspaPublicRestClient(http);

        await Assert.ThrowsAsync<KaspaRpcException>(() => client.GetInfoAsync());

        Assert.Equal(1, inner.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Network_exceptions_are_retried_then_rethrown()
    {
        var delays = new List<TimeSpan>();
        var inner = new ScriptedHandler { ThrowAlways = new HttpRequestException("connection reset") };
        using var http = new HttpClient(new KaspaRetryHandler(inner, Options(delays, maxRetries: 2))) { BaseAddress = new Uri("http://rest.test/") };

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("info/kaspad"));

        Assert.Equal("connection reset", error.Message);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Network_exception_then_success_recovers()
    {
        var delays = new List<TimeSpan>();
        var inner = new ScriptedHandler(Respond(HttpStatusCode.OK, BalanceJson)) { ThrowFirst = 1 };
        using var http = new HttpClient(new KaspaRetryHandler(inner, Options(delays))) { BaseAddress = new Uri("http://rest.test/") };
        using var client = new KaspaPublicRestClient(http);

        var balance = await client.GetBalanceAsync(Address);

        Assert.Equal(7UL, balance.Sompi);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Per_attempt_timeout_is_retried_and_reported_as_timeout()
    {
        var delays = new List<TimeSpan>();
        var options = new KaspaRetryOptions
        {
            MaxRetries = 1,
            BaseDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(1),
            PerAttemptTimeout = TimeSpan.FromMilliseconds(30),
            Jitter = false,
            DelayAsync = (d, _) => { delays.Add(d); return Task.CompletedTask; },
        };
        var inner = new ScriptedHandler { HangUntilCancelled = true };
        using var http = new HttpClient(new KaspaRetryHandler(inner, options)) { BaseAddress = new Uri("http://rest.test/") };

        await Assert.ThrowsAsync<TimeoutException>(() => http.GetAsync("info/kaspad"));

        Assert.Equal(2, inner.Calls);
        Assert.Single(delays);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried()
    {
        var delays = new List<TimeSpan>();
        var inner = new ScriptedHandler { HangUntilCancelled = true };
        using var http = new HttpClient(new KaspaRetryHandler(inner, Options(delays))) { BaseAddress = new Uri("http://rest.test/") };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetAsync("info/kaspad", cts.Token));

        Assert.Equal(1, inner.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Submit_transaction_is_never_retried()
    {
        var delays = new List<TimeSpan>();
        var inner = new ScriptedHandler(
            Respond(HttpStatusCode.ServiceUnavailable, """{"error":"busy"}"""),
            Respond(HttpStatusCode.OK, """{"transactionId":"aa"}"""));
        using var http = new HttpClient(new KaspaRetryHandler(inner, Options(delays))) { BaseAddress = new Uri("http://rest.test/") };
        using var client = new KaspaPublicRestClient(http);

        var result = await client.SubmitTransactionAsync(new KaspaTransaction());

        Assert.Equal("busy", result.Error);
        Assert.Equal(1, inner.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Multi_address_utxo_post_is_retried_with_the_same_body()
    {
        const string second = "kaspa:qp0l70zd5x85ttwd6jv7g3s3a8llzj96d8dncn4zmhv4tlzx5k2jyqh70xmfj";
        var delays = new List<TimeSpan>();
        var inner = new ScriptedHandler(
            Respond(HttpStatusCode.TooManyRequests, "{}"),
            Respond(HttpStatusCode.OK, "[]"));
        using var http = new HttpClient(new KaspaRetryHandler(inner, Options(delays))) { BaseAddress = new Uri("http://rest.test/") };
        using var client = new KaspaPublicRestClient(http);

        var utxos = await client.GetUtxosByAddressesAsync(new[] { Address, second });

        Assert.Empty(utxos);
        Assert.Equal(2, inner.Calls);
        Assert.Equal(2, inner.Bodies.Count);
        Assert.Equal(inner.Bodies[0], inner.Bodies[1]);
        Assert.Contains(second, inner.Bodies[1], StringComparison.Ordinal);
        Assert.All(inner.Methods, m => Assert.Equal("POST", m));
        Assert.Equal(new[] { "application/json" }, inner.ContentTypes.Distinct());
    }

    [Fact]
    public async Task Unmarked_post_is_sent_once()
    {
        var delays = new List<TimeSpan>();
        var inner = new ScriptedHandler(Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        using var http = new HttpClient(new KaspaRetryHandler(inner, Options(delays))) { BaseAddress = new Uri("http://rest.test/") };

        using var response = await http.PostAsync("anything", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task None_policy_sends_get_once()
    {
        var inner = new ScriptedHandler(Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        using var http = new HttpClient(new KaspaRetryHandler(inner, KaspaRetryOptions.None)) { BaseAddress = new Uri("http://rest.test/") };

        using var response = await http.GetAsync("info/kaspad");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void Rest_client_with_owned_http_client_accepts_retry_options()
    {
        using var client = new KaspaPublicRestClient("http://rest.test", null, KaspaRetryOptions.None);

        Assert.Equal(new Uri("http://rest.test/"), client.BaseAddress);
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>Plays queued responses in order; the last one repeats once the queue is down to one.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public ScriptedHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public int Calls { get; private set; }
        public int ThrowFirst { get; init; }
        public Exception? ThrowAlways { get; init; }
        public bool HangUntilCancelled { get; init; }
        public List<string> Bodies { get; } = new();
        public List<string> Methods { get; } = new();
        public List<string> ContentTypes { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Methods.Add(request.Method.Method);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
                ContentTypes.Add(request.Content.Headers.ContentType?.MediaType ?? string.Empty);
            }

            if (HangUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (ThrowAlways is not null)
            {
                throw ThrowAlways;
            }

            if (Calls <= ThrowFirst)
            {
                throw new HttpRequestException("transient network failure");
            }

            var next = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            // Hand out a copy so a repeated response can be read more than once.
            var copy = new HttpResponseMessage(next.StatusCode)
            {
                Content = new StringContent(await next.Content.ReadAsStringAsync(cancellationToken), Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
            foreach (var header in next.Headers)
            {
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return copy;
        }
    }
}
