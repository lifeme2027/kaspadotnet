using System.Text.Json;
using System.Text.Json.Serialization;
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
/// <item><c>GET /addresses/{address}/balance</c></item>
/// <item><c>GET /addresses/{address}/utxos</c> and <c>POST /addresses/utxos</c></item>
/// <item><c>GET /info/kaspad</c></item>
/// <item><c>GET /info/blockdag</c></item>
/// <item><c>POST /transactions</c></item>
/// </list>
/// The public API rate-limits UTXO routes. One address per call is enough for
/// wallets; do not scrape from this client. gRPC via <see cref="KaspaRpcClient"/>
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
    {
        _ownsClient = httpClient is null;
        if (httpClient is null)
        {
            var owned = new HttpClient { BaseAddress = NormalizeBase(baseAddress), Timeout = TimeSpan.FromSeconds(30) };
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
        using var response = await _http.PostAsync("addresses/utxos", content, cancellationToken).ConfigureAwait(false);
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
}
