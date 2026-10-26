# kaspadotnet

[![dotnet](https://github.com/composehk/kaspadotnet/actions/workflows/dotnet.yml/badge.svg?branch=main)](https://github.com/composehk/kaspadotnet/actions/workflows/dotnet.yml)

Fork of [svarogg/kaspadotnet](https://github.com/svarogg/kaspadotnet) by [composehk](https://github.com/composehk).

.NET 8 client layer for Kaspa: a small address check, a public REST client that works without a local node, and a legacy protowire gRPC wrapper for kaspad / kaspawalletd. It complements [composehk/kaswallet](https://github.com/composehk/kaswallet) (`dotnet/` C# wallet library and CLI). See [COMPLEMENT.md](COMPLEMENT.md).

## What works

| Piece | Status |
| --- | --- |
| `KaspaAddress` / `IKaspaAddressValidator` | Offline. Prefix `kaspa:`, `kaspatest:`, `kaspasim:`, `kaspadev:` plus bech32 charset and CashAddr checksum. |
| `KaspaPublicRestClient` | Use this today. Balance, UTXOs, node info, and blockDAG from `https://api.kaspa.org`. No local node. |
| `KaspaRpcClient` | Legacy / WIP. gRPC `GetBalance`, `GetUtxosByAddresses`, `GetInfo`, `GetBlockDagInfo` against the bundled protowire snapshot. Needs a kaspad gRPC port (`--utxoindex` for balance and UTXOs). |
| `KaspadClient` / `KaspawalletdClient` | Original low-level channels. Examples for the local wallet daemon still use them. |
| `Kaspadotnet.Clients.Tests` | Offline. No node and no call to api.kaspa.org. |

Modern rusty-kaspa's primary API is wRPC. The `.proto` files in this repo are an older protowire snapshot. They still build. Treat gRPC as legacy until those protos are refreshed. Public DNS seeders discover peers; they are not open RPC endpoints. Seeders and default ports are documented on `KaspaRpcClientOptions`.

Amounts are sompi (1 KAS = 100,000,000 sompi), the same unit kaswallet uses.

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
