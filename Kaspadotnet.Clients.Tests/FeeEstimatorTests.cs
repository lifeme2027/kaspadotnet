using Kaspadotnet.Clients.Mass;
using Xunit;

namespace Kaspadotnet.Clients.Tests;

/// <summary>
/// Mass-based fee estimation, adapted from composehk/kaswallet <c>FeeRateBuilderTests</c>.
/// kaswallet builds and signs; here the same inputs/outputs are estimated with placeholder signatures.
/// </summary>
public class FeeEstimatorTests
{
    private const ulong Kas = 100_000_000;
    private const ulong Large = 5_675_155_239;

    private static KaspaUtxo Utxo(ulong amount) => MassCalculatorTests.Utxo(amount);

    private static KaspaTransactionOutput Out(ulong amount) => MassCalculatorTests.Output(amount);

    /// <summary>Pays <paramref name="amount"/> and returns the rest (minus a nominal 0.002036 KAS) as change.</summary>
    private static FeeEstimate Send(ulong amount, ulong rate, params ulong[] utxoAmounts)
    {
        var utxos = utxoAmounts.Select(Utxo).ToList();
        var change = utxoAmounts.Aggregate(0UL, (a, b) => a + b) - amount - 203_600;
        return FeeEstimator.Estimate(utxos, new[] { Out(amount), Out(change) }, rate);
    }

    [Fact]
    public void Five_kas_from_a_large_utxo_pays_compute_mass_2036_at_minimum_rate()
    {
        var est = Send(5 * Kas, 100, Large);
        Assert.Equal(2036UL, est.Mass.FeeMass);
        Assert.Equal(2036UL * 100, est.FeeSompi); // 0.002036 KAS
        Assert.Equal(100UL, est.FeeRate);
        Assert.Equal(0.00203600m, est.FeeKas);
    }

    [Fact]
    public void One_kas_from_a_large_utxo_is_dominated_by_storage_mass()
    {
        var est = Send(1 * Kas, 100, Large);
        Assert.Equal(est.Mass.StorageMass, est.Mass.FeeMass);
        Assert.InRange(est.Mass.FeeMass, 10_000UL, 10_010UL); // C / 1e8 = 10_000 plus change term
        Assert.Equal(est.Mass.FeeMass * 100, est.FeeSompi);
        Assert.InRange(est.FeeSompi, 1_000_000UL, 1_001_000UL); // ~0.01 KAS
    }

    [Fact]
    public void Small_output_of_0_12_kas_pays_about_0_083_kas()
    {
        var est = Send(12_000_000, 100, Large);
        Assert.InRange(est.Mass.FeeMass, 83_333UL, 83_340UL);
        Assert.Equal(est.Mass.FeeMass * 100, est.FeeSompi);
        Assert.InRange(est.FeeSompi, 8_333_300UL, 8_334_000UL);
    }

    [Fact]
    public void Fee_equals_mass_times_rate_for_higher_rates()
    {
        var at100 = Send(5 * Kas, 100, Large);
        var at300 = Send(5 * Kas, 300, Large);
        Assert.Equal(at100.Mass.FeeMass, at300.Mass.FeeMass);
        Assert.Equal(at100.FeeSompi * 3, at300.FeeSompi);
        Assert.Equal(300UL, at300.FeeRate);
    }

    [Fact]
    public void Each_extra_input_adds_mass()
    {
        var two = Send(5 * Kas, 100, 3 * Kas, 3 * Kas, 3 * Kas);
        var one = Send(Kas, 100, 3 * Kas);
        Assert.True(two.Mass.ComputeMass > one.Mass.ComputeMass + 1000);
        Assert.Equal(two.Mass.FeeMass * 100, two.FeeSompi);
    }

    [Fact]
    public void Draft_matches_a_signed_transaction_shape()
    {
        var utxos = new[] { Utxo(Large) };
        var draft = FeeEstimator.BuildDraft(utxos, new[] { Out(5 * Kas), Out(Large - 5 * Kas - 203_600) });
        Assert.Equal(0u, draft.Version);
        Assert.Single(draft.Inputs);
        Assert.Equal(FeeEstimator.SchnorrSignatureScriptLength * 2, draft.Inputs[0].SignatureScript.Length);
        Assert.Equal(1, draft.Inputs[0].SigOpCount);
        Assert.False(MassCalculator.IsCoinbase(draft));
        Assert.Equal(MassCalculator.Calculate(draft, utxos), FeeEstimator.Estimate(draft, utxos, 100).Mass);
    }

    [Fact]
    public void Fee_rate_below_the_minimum_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Send(Kas, 99, 5 * Kas));

    [Fact]
    public void No_inputs_no_outputs_and_zero_amounts_are_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            FeeEstimator.Estimate(Array.Empty<KaspaUtxo>(), new[] { Out(Kas) }, 100));
        Assert.Throws<ArgumentException>(() =>
            FeeEstimator.Estimate(new[] { Utxo(5 * Kas) }, Array.Empty<KaspaTransactionOutput>(), 100));
        Assert.Throws<ArgumentException>(() =>
            FeeEstimator.Estimate(new[] { Utxo(5 * Kas) }, new[] { Out(0) }, 100));
        Assert.Throws<ArgumentException>(() =>
            FeeEstimator.Estimate(new[] { Utxo(0) }, new[] { Out(Kas) }, 100));
    }

    [Fact]
    public void Mass_above_the_standard_limit_is_refused()
    {
        // 1 KAS output with ~0.01 KAS change: storage mass is far above 100_000.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FeeEstimator.Estimate(new[] { Utxo(Kas + 1_002_000) }, new[] { Out(Kas), Out(1_000) }, 100));
        Assert.Contains("standard limit", ex.Message);
        Assert.True(MassCalculator.Calculate(
            FeeEstimator.BuildDraft(new[] { Utxo(Kas + 1_002_000) }, new[] { Out(Kas), Out(1_000) }),
            new[] { Utxo(Kas + 1_002_000) }).FeeMass > MassCalculator.MaxStandardTransactionMass);
    }

    [Fact]
    public void Version_1_transactions_are_not_supported()
    {
        var tx = MassCalculatorTests.Tx(new[] { 66 }, new ulong[] { 100 }) with { Version = 1 };
        Assert.Throws<NotSupportedException>(() => FeeEstimator.Estimate(tx, new[] { Utxo(500) }, 100));
    }

    [Fact]
    public void Fee_report_lists_mass_rate_and_fee()
    {
        var lines = FeeReport.Lines(Send(5 * Kas, 100, Large));
        Assert.Contains("mass=2036", lines);
        Assert.Contains("fee_rate=100", lines);
        Assert.Contains("fee_sompi=203600", lines);
        Assert.Contains("fee_kas=0.00203600", lines);
    }
}
