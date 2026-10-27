# kaspadotnet 与 lifeme2027/kaswallet

- 主钱包：[lifeme2027/kaswallet](https://github.com/lifeme2027/kaswallet) → `dotnet/`（助记词、密钥、签名、未签名交易草稿、CLI）
- 本仓：保留原名 **kaspadotnet**，做 .NET 8 客户端层（地址文本、公开 REST、遗留 gRPC）
- 两边互通的是同一套地址前缀和同一套 REST 余额/UTXO，不是把签名搬进本仓

## Complement

| Repo | Owns |
| --- | --- |
| [lifeme2027/kaswallet](https://github.com/lifeme2027/kaswallet) `dotnet/` | Wallet core: BIP39, keys, signing, canonical address use inside the wallet, REST balance/UTXO, unsigned drafts, CLI. |
| kaspadotnet | Client layer a wallet can share: `IKaspaAddressValidator`, `KaspaPublicRestClient`, and a legacy kaspad gRPC wrapper. |

Address reuse lives in `Kaspadotnet.Clients/Shared/KaspaAddress.cs`.

- Form: `{prefix}:{payload}` with prefixes `kaspa` (mainnet), `kaspatest`, `kaspasim`, `kaspadev`. That is the `kaspa:` notion kaswallet already uses.
- Charset: `qpzry9x8gf2tvdw0s3jn54khce6mua7l` (Kaspa bech32, not the looser `[a-z0-9]` some indexers accept).
- Checksum: CashAddr polymod, same constants as rusty-kaspa `crypto/addresses`. A bad character or a flipped checksum fails offline.
- Out of scope here: version byte, script payload length policy, and key derivation. Those stay in kaswallet. If kaswallet tightens those rules, keep this helper in sync or call the wallet assembly later. This repo does not reference kaswallet.

REST reuse matches kaswallet's public indexer style (`https://api.kaspa.org`, kaspa-rest-server):

- `GET /addresses/{address}/balance`
- `GET /addresses/{address}/utxos` and `POST /addresses/utxos`
- `GET /info/kaspad`
- `GET /info/blockdag`

Amounts are sompi (1 KAS = 100_000_000 sompi) on both `KaspaBalance` and the gRPC proto fields.

## What works

- `dotnet build kaspadotnet.sln` and `dotnet test kaspadotnet.sln` on **net8.0**. Tests do not dial a node or the public API.
- `KaspaPublicRestClient` is the client to use today when no local kaspad is running.
- `KaspaRpcClient` builds against the bundled protowire and exposes `GetBalanceAsync`, `GetUtxosByAddressesAsync`, `GetInfoAsync`, and `GetBlockDagInfoAsync`. **gRPC is legacy/WIP**: the protos are a snapshot, rusty-kaspa's primary API is wRPC, and DNS seeders (`seeder1.kaspad.net`, `mainnet-dnsseed-1.kaspanet.org`, and the others listed on `KaspaRpcClientOptions`) only discover peers. Default local gRPC port is 16110. Balance and UTXO RPCs need `--utxoindex`.
- Historical types `KaspadClient` and `KaspawalletdClient` stay in the namespace `Kaspadontnet.Clients` (the original spelling). New types are in `Kaspadotnet.Clients`.

## Build

```bash
dotnet build kaspadotnet.sln
dotnet test kaspadotnet.sln
dotnet run --project Kaspadotnet.Examples --
```
