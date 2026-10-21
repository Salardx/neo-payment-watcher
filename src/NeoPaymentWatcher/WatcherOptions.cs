using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Chains.Evm;
using NeoPaymentWatcher.Neo;
using NeoPaymentWatcher.Services;

namespace NeoPaymentWatcher;

public sealed class WatcherOptions
{
    public const string Section = "Watcher";

    /// <summary>"neo-n3" (the default) or "evm".</summary>
    public string? Chain { get; set; }

    /// <summary>
    /// Neo N3: "mainnet" or "testnet". EVM: "neox-mainnet", "neox-testnet", "xlayer-mainnet", "xlayer-testnet",
    /// "base-mainnet", "base-sepolia", or "custom" with a ChainId. Checked against the node at startup.
    /// </summary>
    public string Network { get; set; } = "testnet";

    /// <summary>EVM chain id; required for a custom network, otherwise set by the preset.</summary>
    public long? ChainId { get; set; }

    /// <summary>Display name of a custom EVM chain, e.g. "Polygon".</summary>
    public string? ChainName { get; set; }

    /// <summary>Symbol of a custom EVM chain's own coin; presets know theirs (GAS, OKB, ETH).</summary>
    public string? NativeSymbol { get; set; }

    /// <summary>Transaction URL template with "{tx}", e.g. "https://basescan.org/tx/{tx}". Presets have one.</summary>
    public string? ExplorerTx { get; set; }

    /// <summary>Blocks to stay behind the tip on chains that can reorganise. Presets set it; dBFT chains (Neo N3, Neo X) need 0.</summary>
    public int? Confirmations { get; set; }

    /// <summary>JSON-RPC endpoints. When a call fails, the next one is tried. Empty uses the preset's public nodes.</summary>
    public string[] RpcUrls { get; set; } = [];

    /// <summary>Block to start from on the first run; empty means the current chain tip. Later runs resume from the database.</summary>
    public uint? StartHeight { get; set; }

    public int PollIntervalMs { get; set; } = 1000;

    public string DatabasePath { get; set; } = "watcher.db";

    /// <summary>Receiving addresses to watch. The watcher only needs the addresses, never the keys.</summary>
    public string[] Addresses { get; set; } = [];

    /// <summary>Token contracts to accept: NEP-17 hashes on Neo N3 (empty means NEO and GAS), ERC-20 addresses on an EVM chain.</summary>
    public string[] Tokens { get; set; } = [];

    /// <summary>EVM only: also accept the chain's own coin (GAS on Neo X, OKB on X Layer, ETH on Base).</summary>
    public bool WatchNative { get; set; } = true;

    /// <summary>When set, the invoice and transfer endpoints require this value in the X-Api-Key header.</summary>
    public string? ApiKey { get; set; }

    public WebhookOptions Webhook { get; set; } = new();

    public InvoiceOptions Invoices { get; set; } = new();

    public CheckoutOptions Checkout { get; set; } = new();

    public ChainProfile ResolveChain() => ChainPresets.Resolve(this);

    public void Validate()
    {
        var chain = ResolveChain();
        if (Addresses.Length == 0)
            throw new InvalidOperationException("Watcher:Addresses must list at least one address to watch.");
        foreach (var address in Addresses)
        {
            var valid = chain.IsEvm ? EvmHex.TryNormalizeAddress(address, out _) : NeoAddress.TryGetScriptHash(address, out _);
            if (!valid)
                throw new InvalidOperationException($"'{address}' is not a valid {chain.DisplayName} address.");
        }
        foreach (var token in Tokens)
        {
            if (chain.IsEvm && token.Trim().Equals(TokenRegistry.Native, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!NeoAddress.TryNormalizeContractHash(token, out _))
                throw new InvalidOperationException($"'{token}' is not a contract hash (0x followed by 40 hex digits).");
        }
        if (chain.IsEvm && !WatchNative && Tokens.Length == 0)
            throw new InvalidOperationException("Watcher:Tokens must list at least one ERC-20 token when Watcher:WatchNative is false.");
        if (PollIntervalMs <= 0 || Invoices.DefaultTtlMinutes <= 0 || Invoices.MaxOffsetUnits <= 0 || Invoices.ReuseCooldownMinutes < 0)
            throw new InvalidOperationException("Watcher:PollIntervalMs and Watcher:Invoices values must be positive.");
        if (!Checkout.EncodesUri && !Checkout.QrContent.Equals("address", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Watcher:Checkout:QrContent must be 'address' or 'uri'.");
        if (!string.IsNullOrWhiteSpace(Checkout.PublicBaseUrl)
            && !(Uri.TryCreate(Checkout.PublicBaseUrl, UriKind.Absolute, out var baseUrl) && baseUrl.Scheme is "http" or "https"))
            throw new InvalidOperationException("Watcher:Checkout:PublicBaseUrl must be an absolute http(s) URL, e.g. https://pay.example.com.");
    }
}

public sealed class WebhookOptions
{
    /// <summary>Where payment events are POSTed. Empty disables webhooks.</summary>
    public string? Url { get; set; }

    /// <summary>HMAC-SHA256 key for the X-Signature header. Empty sends unsigned requests.</summary>
    public string? Secret { get; set; }

    public int MaxAttempts { get; set; } = 12;
}

public sealed class InvoiceOptions
{
    public int DefaultTtlMinutes { get; set; } = 30;

    /// <summary>Largest offset, in steps of the token's smallest shown unit, added to make an invoice amount unique. Never more than 1% of one token.</summary>
    public int MaxOffsetUnits { get; set; } = 9999;

    /// <summary>How long an expired invoice keeps its amount reserved, so a late payment cannot settle a newer invoice.</summary>
    public int ReuseCooldownMinutes { get; set; } = 60;
}

public sealed class CheckoutOptions
{
    /// <summary>Serves GET /pay/{id}, a payment page to link customers to. No API key: the invoice id, 64 random bits, is the secret.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Public origin of this service, e.g. "https://pay.example.com". When set, invoices carry a checkoutUrl.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Heading of the page, e.g. the shop's name.</summary>
    public string Title { get; set; } = "Payment";

    /// <summary>
    /// What the QR code holds: "address", which every wallet can scan, or "uri", the payment link
    /// (NEP-9 on Neo N3, EIP-681 on EVM chains), which prefills the amount in wallets that support it.
    /// </summary>
    public string QrContent { get; set; } = "address";

    public bool EncodesUri => QrContent.Equals("uri", StringComparison.OrdinalIgnoreCase);

    public string? UrlFor(string invoiceId) =>
        Enabled && !string.IsNullOrWhiteSpace(PublicBaseUrl) ? $"{PublicBaseUrl.TrimEnd('/')}/pay/{invoiceId}" : null;
}
