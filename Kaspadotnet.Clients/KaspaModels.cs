namespace Kaspadotnet.Clients;

/// <summary>
/// Balance in sompi. 1 KAS = <see cref="SompiPerKas"/> sompi, the same unit
/// composehk/kaswallet uses for REST amounts.
/// </summary>
public sealed record KaspaBalance(string Address, ulong Sompi)
{
    public const ulong SompiPerKas = 100_000_000;

    public decimal Kas => Sompi / (decimal)SompiPerKas;
}

/// <summary>One unspent output. Amounts are sompi.</summary>
public sealed record KaspaUtxo(
    string Address,
    string TransactionId,
    uint OutputIndex,
    ulong AmountSompi,
    string ScriptPublicKey,
    ulong BlockDaaScore,
    bool IsCoinbase);

/// <summary>Subset of kaspad <c>GetInfo</c> / <c>GET /info/kaspad</c>.</summary>
/// <param name="P2pId">
/// Raw p2p id from gRPC, or the SHA-256 hex published by api.kaspa.org when
/// <see cref="P2pIdIsHashed"/> is true.
/// </param>
public sealed record KaspaNodeInfo(
    string? ServerVersion,
    ulong? MempoolSize,
    string? P2pId,
    bool? IsUtxoIndexed,
    bool? IsSynced)
{
    public bool P2pIdIsHashed { get; init; }
}

/// <summary>
/// Outpoint referenced by a transaction input. <see cref="TransactionId"/> is hex.
/// </summary>
public sealed record KaspaPreviousOutpoint
{
    public string TransactionId { get; init; } = "";

    public uint Index { get; init; }
}

/// <summary>
/// Transaction input. <see cref="SignatureScript"/> is hex.
/// <see cref="Sequence"/> is a consensus uint64. <see cref="SigOpCount"/> is one byte.
/// </summary>
public sealed record KaspaTransactionInput
{
    public KaspaPreviousOutpoint PreviousOutpoint { get; init; } = new();

    public string SignatureScript { get; init; } = "";

    public ulong Sequence { get; init; }

    public byte SigOpCount { get; init; }
}

/// <summary>
/// Script and version on an output. <see cref="ScriptPublicKey"/> is hex.
/// Version 0 is Schnorr, 1 is ECDSA, 8 is P2SH.
/// </summary>
public sealed record KaspaScriptPublicKey
{
    public uint Version { get; init; }

    public string ScriptPublicKey { get; init; } = "";
}

/// <summary>Transaction output. <see cref="Amount"/> is sompi.</summary>
public sealed record KaspaTransactionOutput
{
    public ulong Amount { get; init; }

    public KaspaScriptPublicKey ScriptPublicKey { get; init; } = new();
}

/// <summary>
/// Kaspa transaction as JSON for <c>POST /transactions</c> and kaspad
/// <c>submitTransaction</c>. Hex fields have no <c>0x</c> prefix.
/// Property names serialize as camelCase
/// (<c>lockTime</c>, <c>subnetworkId</c>, <c>sigOpCount</c>, <c>scriptPublicKey</c>).
/// </summary>
public sealed record KaspaTransaction
{
    /// <summary>Consensus transaction version. The public API sends <c>0</c>.</summary>
    public uint Version { get; init; }

    public IReadOnlyList<KaspaTransactionInput> Inputs { get; init; } = Array.Empty<KaspaTransactionInput>();

    public IReadOnlyList<KaspaTransactionOutput> Outputs { get; init; } = Array.Empty<KaspaTransactionOutput>();

    public ulong LockTime { get; init; }

    /// <summary>Subnetwork id hex. Native transfers use 20 zero bytes.</summary>
    public string SubnetworkId { get; init; } = "";

    public ulong Gas { get; init; }

    /// <summary>Optional payload hex. Empty for a native transfer.</summary>
    public string Payload { get; init; } = "";
}

/// <summary>
/// Result of submitting a transaction. <see cref="TransactionId"/> is set when the
/// node accepts it. <see cref="Error"/> is the node message when it does not.
/// </summary>
public sealed record KaspaSubmitTransactionResult(string? TransactionId, string? Error);

/// <summary>Subset of kaspad <c>GetBlockDagInfo</c> / <c>GET /info/blockdag</c>.</summary>
public sealed record KaspaBlockDagInfo(
    string? NetworkName,
    ulong BlockCount,
    ulong HeaderCount,
    IReadOnlyList<string> TipHashes,
    double Difficulty,
    long PastMedianTime,
    IReadOnlyList<string> VirtualParentHashes,
    string? PruningPointHash,
    ulong VirtualDaaScore,
    string? Sink);

/// <summary>One fee-rate bucket: <see cref="FeeRate"/> in sompi per gram of mass, and the node's time estimate.</summary>
public sealed record KaspaFeeRateBucket(double FeeRate, double EstimatedSeconds);

/// <summary>
/// kaspad <c>GetFeeEstimate</c> / <c>GET /info/fee-estimate</c>. Buckets are as returned by the node
/// (normal and low buckets ordered from higher to lower fee rate). Estimates are advisory only.
/// </summary>
public sealed record KaspaFeeEstimate(
    KaspaFeeRateBucket PriorityBucket,
    IReadOnlyList<KaspaFeeRateBucket> NormalBuckets,
    IReadOnlyList<KaspaFeeRateBucket> LowBuckets)
{
    /// <summary>Fee rate of the first normal bucket, or the priority rate when the node returns none.</summary>
    public double NormalFeeRate => NormalBuckets.Count > 0 ? NormalBuckets[0].FeeRate : PriorityBucket.FeeRate;

    /// <summary>Fee rate of the first low bucket, falling back to <see cref="NormalFeeRate"/>.</summary>
    public double LowFeeRate => LowBuckets.Count > 0 ? LowBuckets[0].FeeRate : NormalFeeRate;
}

/// <summary>kaspad gRPC or the public REST API returned an error.</summary>
public sealed class KaspaRpcException : Exception
{
    public KaspaRpcException(string message) : base(message)
    {
    }

    public KaspaRpcException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
