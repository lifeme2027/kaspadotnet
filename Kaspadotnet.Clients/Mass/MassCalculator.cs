namespace Kaspadotnet.Clients.Mass;

/// <summary>
/// One UTXO storage cell for the KIP-9 storage mass: how many 100-byte "storage units"
/// the entry occupies (<see cref="Plurality"/>, 1 for every standard script) and its amount in sompi.
/// </summary>
public readonly record struct UtxoCell(ulong Plurality, ulong Amount);

/// <summary>Per-dimension block mass limits (rusty-kaspa <c>BlockMassLimits</c>).</summary>
public readonly record struct BlockMassLimits(ulong Storage, ulong Compute, ulong Transient)
{
    /// <summary>Mainnet / testnet-10 limits: compute 500k, storage 500k, transient 1M.</summary>
    public static BlockMassLimits Default { get; } = new(500_000, 500_000, 1_000_000);
}

/// <summary>Compute / transient mass of a transaction (no UTXO context needed).</summary>
public readonly record struct NonContextualMasses(ulong ComputeMass, ulong TransientMass);

/// <summary>Every mass dimension of one transaction and the fee-relevant value.</summary>
public readonly record struct KaspaTransactionMass(
    ulong ComputeMass,
    ulong TransientMass,
    ulong StorageMass,
    ulong NormalizedTransientMass,
    ulong NormalizedStorageMass,
    ulong FeeMass);

/// <summary>
/// Kaspa transaction mass, ported from rusty-kaspa <c>consensus/core/src/mass/mod.rs</c>
/// (version-0 native transactions) and aligned with composehk/kaswallet
/// <c>KasWallet.Core.Consensus.MassCalculator</c>. Three dimensions exist:
/// compute = bytes*1 + scriptPublicKeyBytes*10 + sigOps*1000,
/// transient = bytes*4, and persistent storage mass (KIP-9, C = 10^12).
/// The fee is charged on the normalized maximum of the three.
/// </summary>
/// <remarks>
/// Works on <see cref="KaspaTransaction"/> (hex string fields) and the <see cref="KaspaUtxo"/>
/// entries it spends. Signature scripts must already be present (or be placeholders of the
/// final length); see <see cref="FeeEstimator"/> for building a draft.
/// </remarks>
public static class MassCalculator
{
    public const ulong MassPerTxByte = 1;
    public const ulong MassPerScriptPubKeyByte = 10;
    public const ulong MassPerSigOp = 1000;

    /// <summary>KIP-9 storage mass parameter C = SOMPI_PER_KASPA * 10_000.</summary>
    public const ulong StorageMassParameter = KaspaBalance.SompiPerKas * 10_000UL;

    /// <summary>KIP-13 transient mass per serialized byte.</summary>
    public const ulong TransientByteToMassFactor = 4;

    /// <summary>Standard (relay policy) maximum mass of one transaction.</summary>
    public const ulong MaxStandardTransactionMass = 100_000;

    /// <summary>Minimum relay fee rate in sompi per gram (mass unit); 100_000 sompi/kg in rusty-kaspa.</summary>
    public const ulong MinimumFeeRate = 100;

    // UTXO fixed storage: outpoint 32+4, amount 8, daa score 8, coinbase flag 1, spk version 2, spk len 8.
    private const ulong UtxoConstStorage = 32 + 4 + 8 + 8 + 1 + 2 + 8;
    private const ulong UtxoUnitSize = 100;
    private const int SubnetworkIdLength = 20;

    /// <summary>rusty-kaspa <c>transaction_estimated_serialized_size</c> for a version-0 transaction.</summary>
    public static ulong EstimatedSerializedSize(KaspaTransaction tx)
    {
        RequireSupported(tx);
        ulong size = 2 + 8; // version u16, input count u64
        foreach (var input in tx.Inputs)
            size += 32 + 4 + 8 + HexLength(input.SignatureScript, "input signature script") + 8; // outpoint, script len, script, sequence
        size += 8; // output count
        foreach (var output in tx.Outputs)
            size += 8 + 2 + 8 + HexLength(output.ScriptPublicKey.ScriptPublicKey, "output script public key"); // value, spk version, spk len, spk
        size += 8 + 20 + 8 + 32 + 8; // lock time, subnetwork id, gas, payload hash, payload length
        return size + HexLength(tx.Payload, "payload");
    }

    /// <summary>Compute and transient mass (rusty-kaspa <c>calc_non_contextual_masses</c>). Coinbase is free.</summary>
    public static NonContextualMasses CalcNonContextualMasses(KaspaTransaction tx)
    {
        RequireSupported(tx);
        if (IsCoinbase(tx))
            return new NonContextualMasses(0, 0);

        var size = EstimatedSerializedSize(tx);
        ulong spkSize = 0;
        foreach (var output in tx.Outputs)
            spkSize += 2 + HexLength(output.ScriptPublicKey.ScriptPublicKey, "output script public key"); // spk version u16 + script
        ulong sigOps = 0;
        foreach (var input in tx.Inputs)
            sigOps += input.SigOpCount;

        var compute = size * MassPerTxByte + spkSize * MassPerScriptPubKeyByte + sigOps * MassPerSigOp;
        return new NonContextualMasses(compute, size * TransientByteToMassFactor);
    }

    public static ulong ComputeMass(KaspaTransaction tx) => CalcNonContextualMasses(tx).ComputeMass;

    /// <summary>Raw transient mass (4 per byte), before normalization to the compute scale.</summary>
    public static ulong TransientMass(KaspaTransaction tx) => CalcNonContextualMasses(tx).TransientMass;

    /// <summary>Storage plurality: how many 100-byte units a UTXO with this script occupies (&gt;= 1).</summary>
    public static ulong UtxoPlurality(int scriptLength, bool hasCovenantId = false)
    {
        if (scriptLength < 0)
            throw new ArgumentOutOfRangeException(nameof(scriptLength));
        var bytes = UtxoConstStorage + (ulong)scriptLength + (hasCovenantId ? 32UL : 0UL);
        return (bytes + UtxoUnitSize - 1) / UtxoUnitSize;
    }

    /// <summary>Storage cell of a spent UTXO (script is hex).</summary>
    public static UtxoCell CellOf(KaspaUtxo utxo)
    {
        ArgumentNullException.ThrowIfNull(utxo);
        return new UtxoCell(UtxoPlurality(checked((int)HexLength(utxo.ScriptPublicKey, "UTXO script public key"))), utxo.AmountSompi);
    }

    /// <summary>Storage cell of a transaction output.</summary>
    public static UtxoCell CellOf(KaspaTransactionOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return new UtxoCell(
            UtxoPlurality(checked((int)HexLength(output.ScriptPublicKey.ScriptPublicKey, "output script public key"))),
            output.Amount);
    }

    /// <summary>
    /// KIP-9 storage mass: max(0, C * (|O|/H(O) - |I|/A(I))) with the relaxed (harmonic input)
    /// form when |O| = 1, |I| = 1 or |O| = |I| = 2. Port of rusty-kaspa <c>calc_storage_mass</c>.
    /// </summary>
    /// <exception cref="ArgumentException">No inputs, no outputs, or a zero-amount input/output.</exception>
    public static ulong StorageMass(
        IReadOnlyList<UtxoCell> inputs,
        IReadOnlyList<UtxoCell> outputs,
        ulong storageMassParameter = StorageMassParameter)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        if (inputs.Count == 0)
            throw new ArgumentException("A non-coinbase transaction needs at least one input.", nameof(inputs));
        if (outputs.Count == 0)
            throw new ArgumentException("A transaction needs at least one output.", nameof(outputs));
        foreach (var cell in inputs)
        {
            if (cell.Amount == 0 || cell.Plurality == 0)
                throw new ArgumentException("Input amount and plurality must be non-zero.", nameof(inputs));
        }

        foreach (var cell in outputs)
        {
            if (cell.Amount == 0 || cell.Plurality == 0)
                throw new ArgumentException("Output amount and plurality must be non-zero.", nameof(outputs));
        }

        checked
        {
            // Sum over outputs of C * p^2 / amount  (= |O| / H(O) scaled by C).
            ulong outsPlurality = 0;
            ulong harmonicOuts = 0;
            foreach (var o in outputs)
            {
                outsPlurality += o.Plurality;
                harmonicOuts += storageMassParameter * o.Plurality * o.Plurality / o.Amount;
            }

            ulong insPlurality = 0;
            foreach (var i in inputs)
                insPlurality += i.Plurality;

            var relaxed = outsPlurality == 1
                          || insPlurality == 1
                          || (outsPlurality == 2 && insPlurality == 2);

            if (relaxed)
            {
                ulong harmonicIns = 0;
                foreach (var i in inputs)
                    harmonicIns = SaturatingAdd(harmonicIns, storageMassParameter * i.Plurality * i.Plurality / i.Amount);
                return SaturatingSub(harmonicOuts, harmonicIns);
            }

            UInt128 sumIns = 0;
            foreach (var i in inputs)
                sumIns += i.Amount;
            var meanIns = (ulong)(sumIns / insPlurality);
            if (meanIns < 1)
                meanIns = 1;
            var arithmeticIns = SaturatingMul(insPlurality, storageMassParameter / meanIns);
            return SaturatingSub(harmonicOuts, arithmeticIns);
        }
    }

    /// <summary>Convenience overload for the usual standard (plurality 1) cells.</summary>
    public static ulong StorageMass(IReadOnlyList<ulong> inputAmounts, IReadOnlyList<ulong> outputAmounts)
    {
        ArgumentNullException.ThrowIfNull(inputAmounts);
        ArgumentNullException.ThrowIfNull(outputAmounts);
        return StorageMass(
            inputAmounts.Select(a => new UtxoCell(1, a)).ToList(),
            outputAmounts.Select(a => new UtxoCell(1, a)).ToList());
    }

    /// <summary>Storage mass of a populated transaction (<paramref name="spentUtxos"/> aligned with inputs). Coinbase is 0.</summary>
    public static ulong StorageMass(KaspaTransaction tx, IReadOnlyList<KaspaUtxo> spentUtxos)
    {
        RequireSupported(tx);
        ArgumentNullException.ThrowIfNull(spentUtxos);
        if (IsCoinbase(tx))
            return 0;
        if (tx.Inputs.Count == 0)
            throw new ArgumentException("A non-coinbase transaction needs at least one input.", nameof(tx));
        if (spentUtxos.Count != tx.Inputs.Count)
            throw new ArgumentException("One spent UTXO is required per input.", nameof(spentUtxos));
        return StorageMass(
            spentUtxos.Select(CellOf).ToList(),
            tx.Outputs.Select(CellOf).ToList());
    }

    /// <summary>
    /// Normalizes <paramref name="mass"/> from a dimension with limit <paramref name="dimensionLimit"/>
    /// to the compute scale: <c>ceil(mass * compute / limit)</c> (exact integer arithmetic).
    /// </summary>
    public static ulong Normalize(ulong mass, ulong dimensionLimit, ulong computeLimit)
    {
        if (dimensionLimit == 0)
            throw new ArgumentOutOfRangeException(nameof(dimensionLimit));
        var numerator = (UInt128)mass * computeLimit;
        return (ulong)((numerator + dimensionLimit - 1) / dimensionLimit);
    }

    /// <summary>
    /// Normalized maximum of compute, transient and storage mass: the "gram" count the fee rate multiplies.
    /// With default limits transient mass is halved (4/byte becomes 2/byte).
    /// </summary>
    public static KaspaTransactionMass Calculate(
        KaspaTransaction tx,
        IReadOnlyList<KaspaUtxo> spentUtxos,
        BlockMassLimits? limits = null)
    {
        var l = limits ?? BlockMassLimits.Default;
        var nc = CalcNonContextualMasses(tx);
        var storage = StorageMass(tx, spentUtxos);
        var normTransient = Normalize(nc.TransientMass, l.Transient, l.Compute);
        var normStorage = Normalize(storage, l.Storage, l.Compute);
        var feeMass = Math.Max(nc.ComputeMass, Math.Max(normTransient, normStorage));
        return new KaspaTransactionMass(nc.ComputeMass, nc.TransientMass, storage, normTransient, normStorage, feeMass);
    }

    /// <summary>Normalized maximum of explicit non-contextual and storage masses (rusty-kaspa <c>Mass::normalized_max</c>).</summary>
    public static ulong NormalizedMax(ulong computeMass, ulong transientMass, ulong storageMass, BlockMassLimits limits)
        => Math.Max(
            Math.Max(computeMass, Normalize(transientMass, limits.Transient, limits.Compute)),
            Normalize(storageMass, limits.Storage, limits.Compute));

    /// <summary>True for the coinbase subnetwork id (first byte 1, rest zero). Empty or all-zero ids are native.</summary>
    public static bool IsCoinbase(KaspaTransaction tx)
    {
        ArgumentNullException.ThrowIfNull(tx);
        var id = DecodeHex(tx.SubnetworkId, "subnetwork id");
        if (id.Length == 0)
            return false;
        if (id.Length != SubnetworkIdLength)
            throw new ArgumentException($"Subnetwork id must be {SubnetworkIdLength} bytes, got {id.Length}.", nameof(tx));
        if (id[0] != 1)
            return false;
        for (var i = 1; i < id.Length; i++)
        {
            if (id[i] != 0)
                return false;
        }

        return true;
    }

    private static void RequireSupported(KaspaTransaction tx)
    {
        ArgumentNullException.ThrowIfNull(tx);
        if (tx.Version != 0)
            throw new NotSupportedException("Only version-0 transactions are supported by the mass calculator.");
    }

    private static ulong HexLength(string? hex, string what) => (ulong)DecodeHex(hex, what).Length;

    private static byte[] DecodeHex(string? hex, string what)
    {
        if (string.IsNullOrEmpty(hex))
            return Array.Empty<byte>();
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"The {what} is not valid hex.", ex);
        }
    }

    private static ulong SaturatingAdd(ulong a, ulong b) => ulong.MaxValue - a < b ? ulong.MaxValue : a + b;
    private static ulong SaturatingSub(ulong a, ulong b) => a > b ? a - b : 0;
    private static ulong SaturatingMul(ulong a, ulong b) => b != 0 && a > ulong.MaxValue / b ? ulong.MaxValue : a * b;
}
