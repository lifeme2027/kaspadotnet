using System.Globalization;

namespace Kaspadotnet.Clients.Mass;

/// <summary>Mass and fee of one transaction. <see cref="FeeSompi"/> = <c>Mass.FeeMass * FeeRate</c>.</summary>
public sealed record FeeEstimate(KaspaTransactionMass Mass, ulong FeeRate, ulong FeeSompi)
{
    public decimal FeeKas => FeeSompi / (decimal)KaspaBalance.SompiPerKas;
}

/// <summary>
/// Fee = mass x fee rate (sompi per gram), the rule composehk/kaswallet applies.
/// Pure functions; no node or network access.
/// </summary>
public static class FeeEstimator
{
    /// <summary>Signature script of a Schnorr P2PK spend: push(64-byte sig + sighash type) = 1 + 65 bytes.</summary>
    public const int SchnorrSignatureScriptLength = 66;

    /// <summary>Fee in sompi = mass x rate. Rates below <see cref="MassCalculator.MinimumFeeRate"/> throw.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The rate is below 100 sompi/gram.</exception>
    /// <exception cref="OverflowException">The product does not fit in a ulong.</exception>
    public static ulong EstimateFee(ulong mass, ulong feeRate)
    {
        if (feeRate < MassCalculator.MinimumFeeRate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(feeRate),
                $"Fee rate {feeRate} is below the network minimum of {MassCalculator.MinimumFeeRate} sompi/gram.");
        }

        return checked(mass * feeRate);
    }

    /// <summary>
    /// Mass and fee of <paramref name="tx"/>, which spends <paramref name="spentUtxos"/> (one per input, same order).
    /// </summary>
    /// <exception cref="InvalidOperationException">The fee mass exceeds the 100,000 standard limit.</exception>
    public static FeeEstimate Estimate(
        KaspaTransaction tx,
        IReadOnlyList<KaspaUtxo> spentUtxos,
        ulong feeRate,
        BlockMassLimits? limits = null)
    {
        var mass = MassCalculator.Calculate(tx, spentUtxos, limits);
        if (mass.FeeMass > MassCalculator.MaxStandardTransactionMass)
        {
            throw new InvalidOperationException(
                $"Transaction mass {mass.FeeMass} exceeds the standard limit of {MassCalculator.MaxStandardTransactionMass}. "
                + "Use fewer inputs or avoid tiny outputs (storage mass).");
        }

        return new FeeEstimate(mass, feeRate, EstimateFee(mass.FeeMass, feeRate));
    }

    /// <summary>
    /// Draft transaction for estimation: version 0, native subnetwork, one 66-byte placeholder signature
    /// script and one sig-op per input (standard Schnorr P2PK spend).
    /// </summary>
    public static KaspaTransaction BuildDraft(
        IReadOnlyList<KaspaUtxo> utxos,
        IReadOnlyList<KaspaTransactionOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(utxos);
        ArgumentNullException.ThrowIfNull(outputs);
        var placeholder = new string('0', SchnorrSignatureScriptLength * 2);
        return new KaspaTransaction
        {
            Version = 0,
            Inputs = utxos.Select(u => new KaspaTransactionInput
            {
                PreviousOutpoint = new KaspaPreviousOutpoint { TransactionId = u.TransactionId, Index = u.OutputIndex },
                SignatureScript = placeholder,
                Sequence = 0,
                SigOpCount = 1,
            }).ToList(),
            Outputs = outputs,
            LockTime = 0,
            SubnetworkId = new string('0', 40),
            Gas = 0,
            Payload = "",
        };
    }

    /// <summary>
    /// Estimates the fee for spending <paramref name="utxos"/> into <paramref name="outputs"/>
    /// (include the change output) with placeholder Schnorr signatures. Pure function; fetch the UTXOs
    /// with <see cref="KaspaPublicRestClient.GetUtxosByAddressesAsync"/> first.
    /// </summary>
    public static FeeEstimate Estimate(
        IReadOnlyList<KaspaUtxo> utxos,
        IReadOnlyList<KaspaTransactionOutput> outputs,
        ulong feeRate,
        BlockMassLimits? limits = null)
        => Estimate(BuildDraft(utxos, outputs), utxos, feeRate, limits);
}

/// <summary>Human-readable <c>key=value</c> lines describing a fee estimate (same keys as kaswallet's FeeReport).</summary>
public static class FeeReport
{
    public static IReadOnlyList<string> Lines(FeeEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        var m = estimate.Mass;
        return new List<string>
        {
            $"mass={m.FeeMass}",
            $"mass_compute={m.ComputeMass}",
            $"mass_transient={m.NormalizedTransientMass}",
            $"mass_storage={m.NormalizedStorageMass}",
            $"fee_rate={estimate.FeeRate}",
            $"fee_sompi={estimate.FeeSompi}",
            $"fee_kas={estimate.FeeKas.ToString("0.00000000", CultureInfo.InvariantCulture)}",
        };
    }
}
