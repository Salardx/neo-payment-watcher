using Microsoft.Extensions.Options;
using NeoPaymentWatcher.Neo;

namespace NeoPaymentWatcher.Services;

/// <summary>The watched receiving addresses, with their script hashes for matching notifications.</summary>
public sealed class WatchList(IOptions<WatcherOptions> options)
{
    public IReadOnlyList<string> Addresses { get; } = options.Value.Addresses.Distinct().ToArray();

    public IReadOnlySet<string> ScriptHashes { get; } = options.Value.Addresses
        .Select(address => NeoAddress.TryGetScriptHash(address, out var hash)
            ? Convert.ToHexStringLower(hash)
            : throw new InvalidOperationException($"'{address}' is not a valid Neo N3 address."))
        .ToHashSet();

    public bool Contains(string address) => Addresses.Contains(address);
}

/// <summary>Scanner progress, reported by /health.</summary>
public sealed class ScannerState
{
    public uint? Tip { get; set; }

    public uint? LastBlock { get; set; }

    public long? LastBlockTime { get; set; }
}
