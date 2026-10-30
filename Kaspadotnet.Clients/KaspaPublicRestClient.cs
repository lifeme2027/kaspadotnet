using System.Text.Json;
using System.Text.Json.Serialization;
using Kaspadotnet.Clients.Http;
using Kaspadotnet.Clients.Shared;

namespace Kaspadotnet.Clients;

/// <summary>
/// REST client for the public Kaspa indexer at <c>https://api.kaspa.org</c>
/// (kaspa-ng/kaspa-rest-server). No local kaspad is required.
/// </summary>
/// <remarks>
/// This is the path that works against mainnet today, and it lines up with
/// composehk/kaswallet's REST usage. Amounts are sompi.
/// Endpoints used:
/// <list type="bullet">
/// <item><c>GET /addresses/{address}/balance</c> and <c>POST /addresses/balances</c></item>
/// <item><c>GET /addresses/{address}/utxos</c> and <c>POST /addresses/utxos</c></item>
/// <item><c>GET /info/kaspad</c></item>
/// <item><c>GET /info/blockdag</c></item>
/// <item><c>GET /info/fee-estimate</c></item>
/// <item><c>POST /transactions</c></item>
/// </list>
/// The public API rate-limits UTXO routes. One address per call is enough for
/// wallets; do not scrape from this client. When this class creates its own
/// <see cref="HttpClient"/>, reads are retried on transient failures (429/5xx,
/// network errors, per-attempt timeout) via <see cref="KaspaRetryHandler"/>;
/// <c>POST /transactions</c> is never retried. gRPC via <see cref="KaspaRpcClient"/>
/// stays available for a local node and is marked legacy/WIP.
/// </remarks>
public sealed class KaspaPublicRestClient : IDisposable
{
    public const string DefaultBaseAddressText = "https://api.kaspa.org/";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public KaspaPublicRestClient()
        : this(baseAddress: null, httpClient: null)
    {
    }

    public KaspaPublicRestClient(string baseAddress)
        : this(baseAddress, httpClient: null)
    {
    }

    public KaspaPublicRestClient(HttpClient httpClient)
        : this(baseAddress: null, httpClient)
    {
    }

    public KaspaPublicRestClient(string? baseAddress, HttpClient? httpClient)
        : this(baseAddress, httpClient, retryOptions: null)
    {
    }

    /// <summary>
    /// Creates a client with an explicit retry policy for the HTTP client this class
    /// owns. Pass <see cref="KaspaRetryOptions.None"/> to disable retries. When
    /// <paramref name="httpClient"/> is supplied, <paramref name="retryOptions"/> is ignored:
    /// the caller controls the handler chain (wrap it in <see cref="KaspaRetryHandler"/> to opt in).
    /// </summary>
    public KaspaPublicRestClient(string? baseAddress, HttpClient? httpClient, KaspaRetryOptions? retryOptions)
    {
        _ownsClient = httpClient is null;
        if (httpClient is null)
        {
            var retry = retryOptions ?? KaspaRetryOptions.Default;
            var handler = new KaspaRetryHandler(new HttpClientHandler(), retry);
            // The retry handler enforces its own per-attempt timeout, so the overall
            // HttpClient timeout must not cut a retry sequence short.
            var overall = retry.PerAttemptTimeout == Timeout.InfiniteTimeSpan
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromTicks((retry.PerAttemptTimeout.Ticks * (retry.MaxRetries + 1)) + (retry.MaxDelay.Ticks * retry.MaxRetries));
            var owned = new HttpClient(handler, disposeHandler: true) { BaseAddress = NormalizeBase(baseAddress), Timeout = overall };
            owned.DefaultRequestHeaders.UserAgent.ParseAdd("kaspadotnet (+https://github.com/lifeme2027/kaspadotnet)");
            _http = owned;
        }
        else
        {
            _http = httpClient;
            if (_http.BaseAddress is null)
            {
                _http.BaseAddress = NormalizeBase(baseAddress);
            }
        }

        BaseAddress = _http.BaseAddress ?? NormalizeBase(baseAddress);
    }

    public Uri BaseAddress { get; }

    public async Task<KaspaBalance> GetBalanceAsync(string address, CancellationToken cancellationToken = default)
    {
        var parsed = KaspaAddress.Parse(address).ToString();
        var relative = "addresses/" + Uri.EscapeDataString(parsed) + "/balance";
        var dto = await ReadJsonAsync<BalanceDto>(relative, cancellationToken).ConfigureAwait(false);
        return new KaspaBalance(string.IsNullOrEmpty(dto.Address) ? parsed : dto.Address, dto.Balance);
    }

    /// <summary>
    /// POST <c>{baseUrl}/addresses/balances</c> with <c>{"addresses":[...]}</c>.
    /// Read-only lookup, so it is marked idempotent and retried like other reads.
    /// Addresses are validated and canonicalised first; the result keeps request order
    /// and has one entry per requested address (missing rows throw).
    /// </summary>
    public async Task<IReadOnlyList<KaspaBalance>> GetBalancesAsync(
        IEnumerable<string> addresses,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(addresses);
        var payload = JsonSerializer.Serialize(new AddressListDto { Addresses = normalized }, JsonOptions);
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "addresses/balances") { Content = content };
        KaspaRetryHandler.MarkIdempotent(request);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new KaspaRpcException($"REST {(int)response.StatusCode} for addresses/balances: {Trim(body)}");
        }

        var rows = Deserialize<List<BalanceDto?>>(body, "balances");
        var byAddress = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is null || string.IsNullOrEmpty(row.Address))
            {
                throw new KaspaRpcException("REST balances entry is missing address.");
            }

            byAddress[KaspaAddress.Parse(row.Address).ToString()] = row.Balance;
        }

        var result = new List<KaspaBalance>(normalized.Count);
        foreach (var address in normalized)
        {
            if (!byAddress.TryGetValue(address, out var sompi))
            {
                throw new KaspaRpcException($"REST balances response has no entry for {address}.");
            }

            result.Add(new KaspaBalance(address, sompi));
        }

        return result;
    }

    public async Task<IReadOnlyList<KaspaUtxo>> GetUtxosByAddressesAsync(
        IEnumerable<string> addresses,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(addresses);
        if (normalized.Count == 1)
        {
            var relative = "addresses/" + Uri.EscapeDataString(normalized[0]) + "/utxos";
            var rows = await ReadJsonAsync<List<UtxoDto>>(relative, cancellationToken).ConfigureAwait(false);
            return MapUtxos(rows, normalized[0]);
        }

        var payload = JsonSerializer.Serialize(new AddressListDto { Addresses = normalized }, JsonOptions);
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        // Read-only lookup that happens to be a POST, so it is safe to retry.
        using var request = new HttpRequestMessage(HttpMethod.Post, "addresses/utxos") { Content = content };
        KaspaRetryHandler.MarkIdempotent(request);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new KaspaRpcException($"REST {(int)response.StatusCode} for addresses/utxos: {Trim(body)}");
        }

        return MapUtxos(Deserialize<List<UtxoDto>>(body, "UTXO"), null);
    }

    public async Task<KaspaNodeInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var dto = await ReadJsonAsync<NodeInfoDto>("info/kaspad", cancellationToken).ConfigureAwait(false);
        var hashed = string.IsNullOrEmpty(dto.P2pId) && !string.IsNullOrEmpty(dto.P2pIdHashed);
        return new KaspaNodeInfo(
            dto.ServerVersion,
            dto.MempoolSize,
            hashed ? dto.P2pIdHashed : dto.P2pId,
            dto.IsUtxoIndexed,
            dto.IsSynced)
        {
            P2pIdIsHashed = hashed,
        };
    }

    /// <summary>
    /// POST <c>{baseUrl}/transactions</c> with
    /// <c>{"transaction":{...},"allowOrphan":false}</c>.
    /// Parses <c>{"transactionId":...}</c> or <c>{"error":...}</c>.
    /// A node error is returned on <see cref="KaspaSubmitTransactionResult.Error"/>
    /// and is not thrown. This method does not retry and does not sign.
    /// </summary>
    public async Task<KaspaSubmitTransactionResult> SubmitTransactionAsync(
        KaspaTransaction transaction,
        bool allowOrphan = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var payload = JsonSerializer.Serialize(
            new SubmitTransactionBody { Transaction = transaction, AllowOrphan = allowOrphan },
            JsonOptions);
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("transactions", content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        SubmitTransactionResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SubmitTransactionResponseDto>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new KaspaRpcException("Could not parse REST response for transactions.", ex);
        }

        if (dto is null)
        {
            throw new KaspaRpcException("Empty REST response for transactions.");
        }

        if (!response.IsSuccessStatusCode
            && string.IsNullOrEmpty(dto.TransactionId)
            && string.IsNullOrEmpty(dto.Error))
        {
            throw new KaspaRpcException($"REST {(int)response.StatusCode} for transactions: {Trim(body)}");
        }

        return new KaspaSubmitTransactionResult(dto.TransactionId, dto.Error);
    }

    public async Task<KaspaBlockDagInfo> GetBlockDagInfoAsync(CancellationToken cancellationToken = default)
    {
        var dto = await ReadJsonAsync<BlockDagDto>("info/blockdag", cancellationToken).ConfigureAwait(false);
        return new KaspaBlockDagInfo(
            dto.NetworkName ?? dto.Network,
            dto.BlockCount,
            dto.HeaderCount,
            dto.TipHashes ?? new List<string>(),
            dto.Difficulty,
            dto.PastMedianTime,
            dto.VirtualParentHashes ?? new List<string>(),
            dto.PruningPointHash,
            dto.VirtualDaaScore,
            dto.Sink);
    }

    /// <summary>
    /// GET <c>{baseUrl}/info/fee-estimate</c>. Read-only; retried like other reads.
    /// Throws <see cref="KaspaRpcException"/> when the priority bucket is missing or a
    /// fee rate is negative, NaN or infinite.
    /// </summary>
    public async Task<KaspaFeeEstimate> GetFeeEstimateAsync(CancellationToken cancellationToken = default)
    {
        var dto = await ReadJsonAsync<FeeEstimateDto>("info/fee-estimate", cancellationToken).ConfigureAwait(false);
        var priority = MapBucket(dto.PriorityBucket ?? throw new KaspaRpcException("REST fee estimate is missing priorityBucket."));
        return new KaspaFeeEstimate(priority, MapBuckets(dto.NormalBuckets), MapBuckets(dto.LowBuckets));
    }

    private static IReadOnlyList<KaspaFeeRateBucket> MapBuckets(List<FeeBucketDto?>? rows)
    {
        if (rows is null || rows.Count == 0)
        {
            return Array.Empty<KaspaFeeRateBucket>();
        }

        var list = new List<KaspaFeeRateBucket>(rows.Count);
        foreach (var row in rows)
        {
            list.Add(MapBucket(row ?? throw new KaspaRpcException("REST fee estimate contains a null bucket.")));
        }

        return list;
    }

    private static KaspaFeeRateBucket MapBucket(FeeBucketDto dto)
    {
        var rate = dto.Feerate ?? dto.FeeRate ?? throw new KaspaRpcException("REST fee estimate bucket is missing feerate.");
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0)
        {
            throw new KaspaRpcException($"REST fee estimate bucket has an invalid feerate: {rate}.");
        }

        var seconds = dto.EstimatedSeconds ?? 0;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            throw new KaspaRpcException($"REST fee estimate bucket has invalid estimatedSeconds: {seconds}.");
        }

        return new KaspaFeeRateBucket(rate, seconds);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private async Task<T> ReadJsonAsync<T>(string relative, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(relative, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new KaspaRpcException($"REST {(int)response.StatusCode} for {relative}: {Trim(body)}");
        }

        return Deserialize<T>(body, relative);
    }

    private static T Deserialize<T>(string body, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions)
                ?? throw new KaspaRpcException($"Empty REST response for {what}.");
        }
        catch (JsonException ex)
        {
            throw new KaspaRpcException($"Could not parse REST response for {what}.", ex);
        }
    }

    private static IReadOnlyList<KaspaUtxo> MapUtxos(IReadOnlyList<UtxoDto>? rows, string? fallbackAddress)
    {
        if (rows is null || rows.Count == 0)
        {
            return Array.Empty<KaspaUtxo>();
        }

        var utxos = new List<KaspaUtxo>(rows.Count);
        foreach (var row in rows)
        {
            var entry = row.UtxoEntry ?? throw new KaspaRpcException("REST UTXO entry is missing utxoEntry.");
            var outpoint = row.Outpoint ?? throw new KaspaRpcException("REST UTXO entry is missing outpoint.");
            utxos.Add(new KaspaUtxo(
                string.IsNullOrEmpty(row.Address) ? fallbackAddress ?? string.Empty : row.Address,
                outpoint.TransactionId ?? string.Empty,
                outpoint.Index,
                entry.Amount,
                ReadScript(entry.ScriptPublicKey),
                entry.BlockDaaScore,
                entry.IsCoinbase));
        }

        return utxos;
    }

    private static string ReadScript(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;
            case JsonValueKind.Object:
                if (element.TryGetProperty("scriptPublicKey", out var script) ||
                    element.TryGetProperty("script", out script))
                {
                    return script.ValueKind == JsonValueKind.String
                        ? script.GetString() ?? string.Empty
                        : script.ToString();
                }

                return element.ToString();
            default:
                return string.Empty;
        }
    }

    private static List<string> Normalize(IEnumerable<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var normalized = new List<string>();
        foreach (var address in addresses)
        {
            normalized.Add(KaspaAddress.Parse(address).ToString());
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException("At least one Kaspa address is required.", nameof(addresses));
        }

        return normalized;
    }

    private static Uri NormalizeBase(string? baseAddress)
    {
        var text = string.IsNullOrWhiteSpace(baseAddress) ? DefaultBaseAddressText : baseAddress.Trim();
        if (!text.EndsWith('/'))
        {
            text += "/";
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"'{baseAddress}' is not an absolute REST base address.", nameof(baseAddress));
        }

        return uri;
    }

    private static string Trim(string body)
    {
        var compact = body.ReplaceLineEndings(" ");
        return compact.Length <= 300 ? compact : compact[..300];
    }

    private sealed class BalanceDto
    {
        public string? Address { get; set; }
        public ulong Balance { get; set; }
    }

    private sealed class AddressListDto
    {
        public List<string> Addresses { get; set; } = new();
    }

    private sealed class UtxoDto
    {
        public string? Address { get; set; }
        public OutpointDto? Outpoint { get; set; }
        public UtxoEntryDto? UtxoEntry { get; set; }
    }

    private sealed class OutpointDto
    {
        public string? TransactionId { get; set; }
        public uint Index { get; set; }
    }

    private sealed class UtxoEntryDto
    {
        public ulong Amount { get; set; }
        public JsonElement ScriptPublicKey { get; set; }
        public ulong BlockDaaScore { get; set; }
        public bool IsCoinbase { get; set; }
    }

    private sealed class NodeInfoDto
    {
        public ulong? MempoolSize { get; set; }
        public string? ServerVersion { get; set; }
        public bool? IsUtxoIndexed { get; set; }
        public bool? IsSynced { get; set; }
        public string? P2pId { get; set; }
        public string? P2pIdHashed { get; set; }
    }

    private sealed class SubmitTransactionBody
    {
        public KaspaTransaction? Transaction { get; set; }
        public bool AllowOrphan { get; set; }
    }

    private sealed class SubmitTransactionResponseDto
    {
        public string? TransactionId { get; set; }
        public string? Error { get; set; }
    }

    private sealed class BlockDagDto
    {
        public string? NetworkName { get; set; }
        public string? Network { get; set; }
        public ulong BlockCount { get; set; }
        public ulong HeaderCount { get; set; }
        public List<string>? TipHashes { get; set; }
        public double Difficulty { get; set; }
        public long PastMedianTime { get; set; }
        public List<string>? VirtualParentHashes { get; set; }
        public string? PruningPointHash { get; set; }
        public ulong VirtualDaaScore { get; set; }
        public string? Sink { get; set; }
    }

    private sealed class FeeEstimateDto
    {
        public FeeBucketDto? PriorityBucket { get; set; }
        public List<FeeBucketDto?>? NormalBuckets { get; set; }
        public List<FeeBucketDto?>? LowBuckets { get; set; }
    }

    private sealed class FeeBucketDto
    {
        public double? Feerate { get; set; }
        [JsonPropertyName("fee_rate")]
        public double? FeeRate { get; set; }
        public double? EstimatedSeconds { get; set; }
    }
}
