using System.Net;

namespace Kaspadotnet.Clients.Http;

/// <summary>
/// <see cref="DelegatingHandler"/> that retries transient failures with exponential
/// backoff. Retried: HTTP 408, 429, 500, 502, 503, 504, <see cref="HttpRequestException"/>
/// and a per-attempt timeout. Not retried: other status codes and caller cancellation.
/// </summary>
/// <remarks>
/// Only idempotent requests are retried: GET, HEAD, OPTIONS, and any request that
/// opts in with <see cref="MarkIdempotent"/>. Anything else, in particular
/// <c>POST /transactions</c>, is sent exactly once so a submit is never duplicated.
/// When retries run out, the last response is returned (or the last exception thrown),
/// so callers see the same error they would without this handler.
/// </remarks>
public sealed class KaspaRetryHandler : DelegatingHandler
{
    private static readonly HttpRequestOptionsKey<bool> IdempotentKey = new("kaspadotnet.idempotent");

    private readonly KaspaRetryOptions _options;

    public KaspaRetryHandler(HttpMessageHandler innerHandler, KaspaRetryOptions? options = null)
        : base(innerHandler)
    {
        _options = options ?? KaspaRetryOptions.Default;
        _options.Validate();
    }

    /// <summary>Allows retrying a non-GET request that is known to be read-only.</summary>
    public static void MarkIdempotent(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(IdempotentKey, true);
    }

    /// <summary>True for the status codes this handler treats as transient.</summary>
    public static bool IsTransientStatus(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// Backoff before retry number <paramref name="retryNumber"/> (1-based):
    /// <c>BaseDelay * 2^(n-1)</c> capped at <c>MaxDelay</c>, or the server's
    /// <c>Retry-After</c> (also capped) when one is given.
    /// </summary>
    public static TimeSpan ComputeDelay(KaspaRetryOptions options, int retryNumber, TimeSpan? retryAfter, double jitterFactor = 1.0)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (retryNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retryNumber));
        }

        if (retryAfter is { } hinted)
        {
            return Min(hinted < TimeSpan.Zero ? TimeSpan.Zero : hinted, options.MaxDelay);
        }

        var exponent = Math.Min(retryNumber - 1, 30);
        var ticks = options.BaseDelay.Ticks * Math.Pow(2, exponent);
        var capped = ticks >= options.MaxDelay.Ticks ? options.MaxDelay : TimeSpan.FromTicks((long)ticks);
        return options.Jitter ? TimeSpan.FromTicks((long)(capped.Ticks * jitterFactor)) : capped;
    }

    /// <summary>Reads <c>Retry-After</c> as either delta-seconds or an HTTP date.</summary>
    public static TimeSpan? ReadRetryAfter(HttpResponseMessage response, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta;
        }

        if (header.Date is { } date)
        {
            var wait = date - (now ?? DateTimeOffset.UtcNow);
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canRetry = _options.MaxRetries > 0 && IsIdempotent(request);

        // Buffer the body once so it can be replayed. Retries need fresh request objects.
        byte[]? body = null;
        if (canRetry && request.Content is not null)
        {
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var attempt = 0; ; attempt++)
        {
            var current = attempt == 0 ? request : Clone(request, body);
            TimeSpan? retryAfter = null;
            HttpResponseMessage? response = null;
            Exception? failure = null;

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_options.PerAttemptTimeout != Timeout.InfiniteTimeSpan)
            {
                attemptCts.CancelAfter(_options.PerAttemptTimeout);
            }

            try
            {
                response = await base.SendAsync(current, attemptCts.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                failure = ex;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                failure = new TimeoutException("The REST request timed out.", ex);
            }

            if (response is not null)
            {
                if (!canRetry || attempt >= _options.MaxRetries || !IsTransientStatus(response.StatusCode))
                {
                    return response;
                }

                retryAfter = ReadRetryAfter(response);
                response.Dispose();
            }
            else if (!canRetry || attempt >= _options.MaxRetries)
            {
                throw failure!;
            }

            var jitter = _options.Jitter ? 0.5 + (Random.Shared.NextDouble() * 0.5) : 1.0;
            var delay = ComputeDelay(_options, attempt + 1, retryAfter, jitter);
            if (delay > TimeSpan.Zero)
            {
                await _options.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsIdempotent(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get
        || request.Method == HttpMethod.Head
        || request.Method == HttpMethod.Options
        || (request.Options.TryGetValue(IdempotentKey, out var marked) && marked);

    private static HttpRequestMessage Clone(HttpRequestMessage source, byte[]? body)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri) { Version = source.Version, VersionPolicy = source.VersionPolicy };
        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in source.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        if (body is not null && source.Content is not null)
        {
            var content = new ByteArrayContent(body);
            foreach (var header in source.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        return clone;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;
}
