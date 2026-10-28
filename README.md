# kaspadotnet

[![dotnet](https://github.com/lifeme2027/kaspadotnet/actions/workflows/dotnet.yml/badge.svg?branch=main)](https://github.com/lifeme2027/kaspadotnet/actions/workflows/dotnet.yml)

Fork of [svarogg/kaspadotnet](https://github.com/svarogg/kaspadotnet) by [composehk](https://github.com/composehk).

.NET 8 client layer for Kaspa: a small address check, a public REST client that works without a local node, and a legacy protowire gRPC wrapper for kaspad / kaspawalletd. It complements [lifeme2027/kaswallet](https://github.com/lifeme2027/kaswallet) (`dotnet/` C# wallet library and CLI). See [COMPLEMENT.md](COMPLEMENT.md).

## What works

| Piece | Status |
| --- | --- |
| `KaspaAddress` / `IKaspaAddressValidator` | Offline. Prefix `kaspa:`, `kaspatest:`, `kaspasim:`, `kaspadev:` plus bech32 charset and CashAddr checksum. |
| `MassCalculator` / `FeeEstimator` | Offline. 交易质量（compute、transient、KIP-9 storage mass，取归一化后最大值）与手续费估算，version 0 交易，与 kaswallet 对齐。 |
| `KaspaPublicRestClient` | Use this today. Balance, UTXOs, node info, and blockDAG from `https://api.kaspa.org`. No local node. Reads retry transient failures (see below). |
| `KaspaRetryHandler` / `KaspaRetryOptions` | Offline. `DelegatingHandler` with exponential backoff and `Retry-After` support for 429/5xx, network errors and per-attempt timeouts. |
| `KaspaRpcClient` | Legacy / WIP. gRPC `GetBalance`, `GetUtxosByAddresses`, `GetInfo`, `GetBlockDagInfo` against the bundled protowire snapshot. Needs a kaspad gRPC port (`--utxoindex` for balance and UTXOs). |
| `KaspadClient` / `KaspawalletdClient` | Original low-level channels. Examples for the local wallet daemon still use them. |
| `Kaspadotnet.Clients.Tests` | Offline. No node and no call to api.kaspa.org. |

Modern rusty-kaspa's primary API is wRPC. The `.proto` files in this repo are an older protowire snapshot. They still build. Treat gRPC as legacy until those protos are refreshed. Public DNS seeders discover peers; they are not open RPC endpoints. Seeders and default ports are documented on `KaspaRpcClientOptions`.

Amounts are sompi (1 KAS = 100,000,000 sompi), the same unit kaswallet uses.

## 交易质量与手续费估算

`Kaspadotnet.Clients.Mass` 里的纯函数，不访问网络，移植自 rusty-kaspa `consensus/core/src/mass`，与 kaswallet 的 `MassCalculator` 行为一致。直接作用于 `KaspaTransaction`（十六进制字段）和 `KaspaUtxo`：

- `MassCalculator.Calculate(tx, spentUtxos)`：compute = 字节 + 脚本字节×10 + sigop×1000，transient = 字节×4，再加 KIP-9 storage mass（C = 10^12）。三者归一化到 compute 尺度后取最大值，即 `FeeMass`。
- `FeeEstimator.Estimate(tx, spentUtxos, feeRate)`：手续费 = `FeeMass` × 费率（sompi/gram）。
- `FeeEstimator.Estimate(utxos, outputs, feeRate)`：不用先签名，按标准 Schnorr P2PK（66 字节签名脚本、1 个 sigop）补占位签名后估算；`outputs` 要包含找零输出。UTXO 可先用 `KaspaPublicRestClient.GetUtxosByAddressesAsync` 取得。

会抛异常的情况：没有输入、没有输出、任一输入或输出金额为 0（`ArgumentException`）；费率低于 100 sompi/gram（`ArgumentOutOfRangeException`）；质量超过标准上限 100000（`InvalidOperationException`）；`Version != 0` 的交易（`NotSupportedException`）。coinbase 交易质量为 0。

```csharp
var utxos = await rest.GetUtxosByAddressesAsync(new[] { from });
var outputs = new[]
{
    new KaspaTransactionOutput { Amount = 500_000_000, ScriptPublicKey = new KaspaScriptPublicKey { ScriptPublicKey = destScriptHex } },
    new KaspaTransactionOutput { Amount = change, ScriptPublicKey = new KaspaScriptPublicKey { ScriptPublicKey = changeScriptHex } },
};
var estimate = FeeEstimator.Estimate(utxos, outputs, feeRate: 100);
Console.WriteLine($"mass={estimate.Mass.FeeMass} fee={estimate.FeeSompi} sompi");
```

测试向量来自 rusty-kaspa 的 `test_storage_mass`、`test_storage_mass_pluralities` 等单元测试（经 kaswallet 复现），另有少量 api.kaspa.org 实测值，见 `MassCalculatorTests`。

## Retries and timeouts

`KaspaPublicRestClient` retries idempotent reads when it creates its own `HttpClient`: HTTP 408, 429, 500, 502, 503, 504, network errors, and a per-attempt timeout. Defaults: 3 retries, backoff 500 ms doubling up to 10 s (with jitter), `Retry-After` honoured and capped at the same maximum, 30 s per attempt. When retries run out you get the same `KaspaRpcException` as before.

- `POST /transactions` is never retried, so a submit cannot be sent twice by the client.
- The multi-address `POST /addresses/utxos` is a read and is retried.
- Caller cancellation is never retried.

```csharp
// Custom policy, or KaspaRetryOptions.None to turn retries off.
using var rest = new KaspaPublicRestClient(
    "https://api.kaspa.org",
    httpClient: null,
    new KaspaRetryOptions { MaxRetries = 5, MaxDelay = TimeSpan.FromSeconds(20) });

// Bring your own HttpClient: wrap the handler yourself.
var http = new HttpClient(new KaspaRetryHandler(new HttpClientHandler())) { BaseAddress = new Uri("https://api.kaspa.org/") };
using var custom = new KaspaPublicRestClient(http);
```

Retrying does not raise the public API's rate limit; keep request volume low.

## How this complements kaswallet

| Repo | Role |
| --- | --- |
| **kaswallet** | Wallet core: mnemonics, keys, canonical address use, REST balance/UTXO, unsigned transaction drafts, CLI. |
| **kaspadotnet** | Shared client experience: the same `kaspa:` address text, the same public REST routes, plus local kaspad / kaspawalletd RPC samples. |

`Kaspadotnet.Clients/Shared/KaspaAddress.cs` is intentionally small. Signing stays in kaswallet. When either repo changes address rules, compare them.

## Build

Requires the .NET 8 SDK.

```bash
dotnet build kaspadotnet.sln
dotnet test kaspadotnet.sln
```

## Examples

```bash
dotnet run --project Kaspadotnet.Examples --
```

Prints the demo list. Useful without a node:

```bash
dotnet run --project Kaspadotnet.Examples -- address kaspa:qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqkx9awp4e
dotnet run --project Kaspadotnet.Examples -- rest-balance kaspa:youraddress
dotnet run --project Kaspadotnet.Examples -- rest-info
```

`api.kaspa.org` rate-limits UTXO routes. Query addresses you care about; do not scrape.

Local daemon demos (`new-address`, `wallet-balance`, `utxos`, `send`, `subscribe`, `follow`) still expect:

1. `kaspad --utxoindex` (gRPC default port 16110; the UTXO sample is pointed at devnet port 16610).
2. `kaspawallet start-daemon` on `localhost:8082` for wallet RPCs.

Those gRPC demos are the legacy path.
