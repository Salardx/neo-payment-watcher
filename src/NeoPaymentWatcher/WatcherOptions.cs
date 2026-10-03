using NeoPaymentWatcher.Neo;

namespace NeoPaymentWatcher;

public sealed class WatcherOptions
{
    public const string Section = "Watcher";

    /// <summary>"mainnet" or "testnet"; checked against the RPC node's network magic at startup.</summary>
    public string Network { get; set; } = "testnet";

    /// <summary>Neo N3 JSON-RPC endpoints. When a call fails, the next one is tried.</summary>
    public string[] RpcUrls { get; set; } = [];

    /// <summary>Block to start from on the first run; empty means the current chain tip. Later runs resume from the database.</summary>
    public uint? StartHeight { get; set; }

    public int PollIntervalMs { get; set; } = 1000;

    public string DatabasePath { get; set; } = "watcher.db";

    /// <summary>Receiving addresses to watch. The watcher only needs the addresses, never the keys.</summary>
    public string[] Addresses { get; set; } = [];

    /// <summary>NEP-17 contract hashes to accept. Empty means NEO and GAS.</summary>
    public string[] Tokens { get; set; } = [];

    /// <summary>When set, the invoice and transfer endpoints require this value in the X-Api-Key header.</summary>
    public string? ApiKey { get; set; }

    public WebhookOptions Webhook { get; set; } = new();

    public InvoiceOptions Invoices { get; set; } = new();

    public uint NetworkMagic => Network.ToLowerInvariant() switch
    {
        "mainnet" => 860833102u,
        "testnet" => 894710606u,
        _ => throw new InvalidOperationException($"Unknown network '{Network}'; use 'mainnet' or 'testnet'."),
    };

    public void Validate()
    {
        _ = NetworkMagic;
        if (RpcUrls.Length == 0)
            throw new InvalidOperationException("Watcher:RpcUrls must list at least one Neo N3 RPC endpoint.");
        if (Addresses.Length == 0)
            throw new InvalidOperationException("Watcher:Addresses must list at least one address to watch.");
        foreach (var address in Addresses)
        {
            if (!NeoAddress.TryGetScriptHash(address, out _))
                throw new InvalidOperationException($"'{address}' is not a valid Neo N3 address.");
        }
        foreach (var token in Tokens)
        {
            if (!NeoAddress.TryNormalizeContractHash(token, out _))
                throw new InvalidOperationException($"'{token}' is not a contract hash (0x followed by 40 hex digits).");
        }
        if (PollIntervalMs <= 0 || Invoices.DefaultTtlMinutes <= 0 || Invoices.MaxOffsetUnits <= 0 || Invoices.ReuseCooldownMinutes < 0)
            throw new InvalidOperationException("Watcher:PollIntervalMs and Watcher:Invoices values must be positive.");
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

    /// <summary>Largest offset, in token base units, added to make an invoice amount unique. Never more than 1% of one token.</summary>
    public int MaxOffsetUnits { get; set; } = 9999;

    /// <summary>How long an expired invoice keeps its amount reserved, so a late payment cannot settle a newer invoice.</summary>
    public int ReuseCooldownMinutes { get; set; } = 60;
}
