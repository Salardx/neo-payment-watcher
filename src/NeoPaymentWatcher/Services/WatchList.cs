using Microsoft.Extensions.Options;
using NeoPaymentWatcher.Chains;

namespace NeoPaymentWatcher.Services;

/// <summary>The watched receiving addresses in their stored form, with the keys notifications and logs are matched on.</summary>
public sealed class WatchList
{
    readonly IChainClient chain;

    public WatchList(IOptions<WatcherOptions> options, IChainClient chain)
    {
        this.chain = chain;
        var addresses = new List<string>();
        var keys = new HashSet<string>();
        foreach (var address in options.Value.Addresses)
        {
            if (!chain.TryCanonicalizeAddress(address, out var canonical, out var key))
                throw new InvalidOperationException($"'{address}' is not a valid {chain.Chain.DisplayName} address.");
            if (keys.Add(key))
                addresses.Add(canonical);
        }
        Addresses = addresses;
        Keys = keys;
    }

    public IReadOnlyList<string> Addresses { get; }

    public IReadOnlySet<string> Keys { get; }

    /// <summary>The stored form of a watched address given in any accepted spelling, or null when it is not watched.</summary>
    public string? Canonical(string? address) =>
        chain.TryCanonicalizeAddress(address, out var canonical, out var key) && Keys.Contains(key) ? canonical : null;
}

/// <summary>Scanner progress, reported by /health.</summary>
public sealed class ScannerState
{
    public uint? Tip { get; set; }

    public uint? LastBlock { get; set; }

    public long? LastBlockTime { get; set; }
}
