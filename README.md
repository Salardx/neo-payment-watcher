# Neo Payment Watcher

Watch-only payment detection for **Neo N3**. It follows the chain, detects incoming NEP-17 transfers (GAS, NEO or any token you whitelist) to your addresses, matches them to orders by unique amount, and calls your webhook. It never holds keys or funds: payments go straight to your own wallet.

- .NET 10, SQLite, plain JSON-RPC to any public Neo N3 node; one NuGet dependency (`Microsoft.Data.Sqlite`).
- Runs anywhere .NET runs (tested on Windows Server 2025), no node or Docker required.
- Exactly-once bookkeeping per block, signed webhooks with retries, invoice API with NEP-9 payment URIs.
- A hosted checkout page per invoice (amount, QR code, live status), so a shop can take Neo payments with no frontend work.

## How it works

1. **Follow blocks.** For each new block, read every transaction's application log (`getblock`, `getapplicationlog`). Neo N3's dBFT consensus has one-block finality, so there is no confirmation depth and no reorg handling.
2. **Keep only real payments.** A notification counts when the execution ended in `HALT`, the event is `Transfer` with exactly three arguments (NEP-17, not NEP-11), the contract is on your token whitelist, and the recipient is a watched address. Mints (claimed GAS), burns, self-transfers and zero amounts are ignored. The whitelist matters: anyone can deploy a contract that emits `Transfer`.
3. **Match orders by amount.** `POST /invoices` for 1.5 GAS returns a unique amount such as `1.50007883 GAS`. The offset is random, at most 0.01 of the token, and never shared with another open invoice. When exactly that amount arrives before expiry, the invoice is paid. Expired amounts stay reserved for a cooldown so a late payment cannot settle a newer order.
4. **Commit atomically.** A block's transfers, invoice updates, webhook events and the scan checkpoint are written in one SQLite transaction. After a crash the watcher resumes from the last committed block, and re-processing a block changes nothing.
5. **Notify.** Webhooks are delivered from a database outbox with exponential backoff (5 s, 10 s, 20 s … capped at 1 h), signed with HMAC-SHA256.

## Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

Create `src/NeoPaymentWatcher/appsettings.Local.json` (git-ignored):

```json
{
  "Watcher": {
    "Network": "testnet",
    "Addresses": [ "<your Neo N3 receiving address>" ],
    "Webhook": { "Url": "https://shop.example/neo-webhook", "Secret": "<random secret>" },
    "ApiKey": "<random key>"
  }
}
```

Run it:

```
dotnet run --project src/NeoPaymentWatcher
```

Output from a mainnet replay starting at block 13746549 (`"StartHeight": 13746549`):

```
info: Watching Nec6R8h1VU1FSBYDcnKNDPm3AwUkaUjc6u on mainnet for NEO (0xef4073a0f2b305a38ec4050e4d3d28bc40ea63f5), GAS (0xd2a4cff31913016155e38e474a2c06d08be276cf)
info: Scanning mainnet from block 13746549 (tip 13746827)
info: Received 2039.57997308 GAS at Nec6R8h1VU1FSBYDcnKNDPm3AwUkaUjc6u from NY6HgPkxWPBEiKaC8HSo1PhjXATEiuAdEB in block 13746549 (tx 0x99a9a8bdfa53457dc2d0f71d3c296bb277fea5ac78fab8560783d03528b63597)
```

End to end on testnet (3 Oct 2026): an order for 1 GAS got the unique amount `1.00002098`. The payment, sent from NeoLine, landed in block 19898561, and the signed `invoice.paid` webhook was delivered 2.7 seconds after the block ([transaction](https://dora.coz.io/transaction/neo3/testnet/0x84bea723bc3100affa007b8b6b58a5ace094f3585cc0a7e6b5e1f1056089f42c)):

```
info: Received 1.00002098 GAS at NQGp42c1jC2Uom7bpLRsMseSSNk2A6vx2x from NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTe in block 19898561 (tx 0x84bea723bc3100affa007b8b6b58a5ace094f3585cc0a7e6b5e1f1056089f42c), paid invoice inv_08d735fe38e39f03
```

The checkout page, end to end on testnet (20 Oct 2026): invoice `inv_eadcd65c6758d2bd` for 1.5 GAS got the unique amount `1.50001444`; the customer opened `/pay/inv_eadcd65c6758d2bd`, scanned the address and paid from NeoLine; the transfer landed in block 20343628 ([transaction](https://dora.coz.io/transaction/neo3/testnet/0x259a809cd59508997c1cce962805a07936f3c7a0284085e86977136d20ab1dfa)) and the page flipped to **Paid** on its next poll:

```
info: Received 1.50001444 GAS at NQGp42c1jC2Uom7bpLRsMseSSNk2A6vx2x from NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTe in block 20343628 (tx 0x259a809cd59508997c1cce962805a07936f3c7a0284085e86977136d20ab1dfa), paid invoice inv_eadcd65c6758d2bd
```

For mainnet set `"Network": "mainnet"`, mainnet `RpcUrls` (e.g. `https://mainnet1.neo.coz.io:443`, `http://seed1.neo.org:10332`) and a separate `DatabasePath`. At startup the watcher checks the node's network magic and refuses to mix networks in one database.

Every setting can also come from environment variables, e.g. `Watcher__Addresses__0=N...`.

## HTTP API

Listens on `http://127.0.0.1:5080` by default. When `Watcher:ApiKey` is set, every endpoint except `/health` and the checkout page requires the `X-Api-Key` header.

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/invoices` | Create an invoice: `{"token": "GAS", "amount": "1.5", "reference": "order-1001", "ttlMinutes": 15}`. `address` is optional (defaults to the first watched address). |
| `GET` | `/invoices/{id}` | Invoice status: `pending`, `paid` or `expired`. |
| `GET` | `/transfers?limit=50` | Latest incoming transfers. |
| `GET` | `/health` | Network, chain tip, last processed block. |
| `GET` | `/pay/{id}` | The checkout page for an invoice (see below). No API key. |
| `GET` | `/pay/{id}/status` | What the page polls: `status`, `paidTxId`, `paidAt`, `expiresAt`, `explorerUrl`. No API key. |

Response from `POST /invoices`:

```json
{
  "id": "inv_55a0b177a274926e",
  "status": "pending",
  "address": "Nec6R8h1VU1FSBYDcnKNDPm3AwUkaUjc6u",
  "token": { "symbol": "GAS", "hash": "0xd2a4cff31913016155e38e474a2c06d08be276cf", "decimals": 8 },
  "amount": "1.50007883",
  "amountUnits": "150007883",
  "requestedAmount": "1.5",
  "reference": "order-1001",
  "createdAt": "2026-10-03T15:05:23.026+00:00",
  "expiresAt": "2026-10-03T15:20:23.026+00:00",
  "paidTxId": null,
  "paidAt": null,
  "paymentUri": "neo:Nec6R8h1VU1FSBYDcnKNDPm3AwUkaUjc6u?asset=gas&amount=1.50007883",
  "checkoutUrl": "https://pay.example.com/pay/inv_55a0b177a274926e"
}
```

Send the customer to `checkoutUrl`, or show them `amount` yourself (or a QR code of `paymentUri`); they must send exactly that amount. `checkoutUrl` is `null` until `Checkout:PublicBaseUrl` is set. NEO has no decimals, so it cannot be used for unique-amount invoices; its transfers are still reported.

## Checkout page

`GET /pay/{id}` is a payment page the shop links its customer to:

- The exact amount with a copy button, the receiving address with a copy button, and a QR code of the address (or of the NEP-9 link, with `Checkout:QrContent` set to `uri`, for wallets that prefill the amount from it).
- A status line that counts down to expiry, then flips to **Paid** with a link to the transaction on [Dora](https://dora.coz.io) the moment the watcher sees the transfer. The page polls `/pay/{id}/status` every 3 seconds, one block; without JavaScript it reloads every 10 seconds instead.
- One HTML document with no external resources: no fonts, scripts or images are fetched from anywhere, so nothing about the customer leaks to a third party, and it works behind any firewall. The QR code is generated in the service (byte mode, error correction level M) rather than by a graphics library.
- Public on purpose, so that customers need no key: the invoice id (64 random bits) is the only secret, the page shows nothing but that invoice, and it is sent with `Cache-Control: no-store`, `Referrer-Policy: no-referrer` and a Content-Security-Policy.

Set `Checkout:PublicBaseUrl` to the origin customers reach the service on (behind TLS), and `Checkout:Title` to the shop's name.

The testnet invoice of 20 Oct 2026, before and after the customer paid it from NeoLine (the wallet's confirmation is in [docs/checkout-neoline.png](docs/checkout-neoline.png)):

![Checkout page waiting for payment](docs/checkout-pending.png)
![Checkout page after the payment was seen](docs/checkout-paid.png)

## Webhooks

`POST` to `Watcher:Webhook:Url` with:

- `X-Event-Id`: `<txid>:<notification index>`, unique per transfer. Delivery is at-least-once, so de-duplicate on it.
- `X-Signature`: `sha256=<hex HMAC-SHA256 of the raw body>`, when `Webhook:Secret` is set.

`type` is `invoice.paid` when the transfer settled an invoice, else `transfer.received`. This payload was produced by replaying mainnet block 13746549:

```json
{
  "id": "0x99a9a8bdfa53457dc2d0f71d3c296bb277fea5ac78fab8560783d03528b63597:0",
  "type": "transfer.received",
  "network": "mainnet",
  "transfer": {
    "txId": "0x99a9a8bdfa53457dc2d0f71d3c296bb277fea5ac78fab8560783d03528b63597",
    "notifyIndex": 0,
    "blockIndex": 13746549,
    "blockTime": "2026-10-03T14:50:31.247+00:00",
    "token": { "symbol": "GAS", "hash": "0xd2a4cff31913016155e38e474a2c06d08be276cf", "decimals": 8 },
    "from": "NY6HgPkxWPBEiKaC8HSo1PhjXATEiuAdEB",
    "to": "Nec6R8h1VU1FSBYDcnKNDPm3AwUkaUjc6u",
    "amount": "2039.57997308",
    "amountUnits": "203957997308",
    "invoiceId": null
  },
  "invoice": null
}
```

Verifying the signature in C#:

```csharp
var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody));
var valid = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signatureHeader));
```

## Configuration

All settings live under `Watcher`.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Network` | `testnet` | `mainnet` or `testnet`; checked against the node at startup. |
| `RpcUrls` | COZ + NGD testnet nodes | JSON-RPC endpoints; the next one is used when a call fails. The node needs the ApplicationLogs plugin (public nodes have it). |
| `Addresses` | (required) | Receiving addresses to watch. |
| `Tokens` | NEO, GAS | NEP-17 contract hashes to accept. Symbol and decimals are read from chain. |
| `StartHeight` | chain tip | Block to start from on the first run; later runs resume from the database. |
| `PollIntervalMs` | `1000` | Delay between polls once caught up (blocks are 3 s apart). |
| `DatabasePath` | `watcher-testnet.db` | SQLite file. Use one per network. |
| `ApiKey` | empty | Required `X-Api-Key` value for the API when set. |
| `Webhook:Url` / `Secret` / `MaxAttempts` | empty / empty / `12` | Event delivery. |
| `Invoices:DefaultTtlMinutes` | `30` | Invoice lifetime. |
| `Invoices:MaxOffsetUnits` | `9999` | Largest unique-amount offset in base units, capped at 1% of one token. |
| `Invoices:ReuseCooldownMinutes` | `60` | How long an expired invoice's amount stays reserved. |
| `Checkout:Enabled` | `true` | Serve the checkout page at `/pay/{id}`. |
| `Checkout:PublicBaseUrl` | empty | Public origin of the service, e.g. `https://pay.example.com`; gives invoices their `checkoutUrl`. |
| `Checkout:Title` | `Payment` | Heading of the page, e.g. the shop's name. |
| `Checkout:QrContent` | `address` | `address` (any wallet can scan it) or `uri` (the NEP-9 link; prefills the amount in wallets that support it). |

## Security notes

- Watch-only: the service only needs public addresses. Keep your keys in your wallet.
- The API binds to localhost by default. Set `ApiKey` and put TLS in front before exposing it. The checkout page is meant to be public; everything else stays behind the key.
- Only whitelisted token contracts count as payment; verify contract hashes before adding tokens.
- Treat a webhook as a trigger and confirm with `GET /invoices/{id}` if your receiver cannot verify signatures.

## Development

```
dotnet test
```

The tests cover address encoding, amount maths, transfer parsing against real mainnet notifications (including NEP-11, mints, failed transactions and fake tokens), invoice matching, expiry, idempotent block commits, webhook retry scheduling, the checkout page and the QR encoder (whose output was also decoded back with jsQR for every symbol version).

## License

MIT
