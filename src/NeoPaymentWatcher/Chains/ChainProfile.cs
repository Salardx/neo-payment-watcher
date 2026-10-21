using System.Globalization;

namespace NeoPaymentWatcher.Chains;

/// <summary>
/// Everything chain-specific the watcher needs at runtime, resolved once at startup from
/// <c>Watcher:Chain</c>, <c>Watcher:Network</c> and the overrides in <see cref="WatcherOptions"/>.
/// </summary>
public sealed record ChainProfile(
    string Kind,
    string Network,
    string DisplayName,
    string NetworkLabel,
    long? ChainId,
    uint? NetworkMagic,
    string NativeSymbol,
    int NativeDecimals,
    string? ExplorerTx,
    int Confirmations,
    string[] RpcUrls)
{
    public const string NeoN3 = "neo-n3";
    public const string Evm = "evm";

    public bool IsEvm => Kind == Evm;

    /// <summary>Pins a database to one network. Neo N3 keeps its magic number, so databases from before multi-chain support still open.</summary>
    public string NetworkKey => IsEvm
        ? $"evm:{ChainId!.Value.ToString(CultureInfo.InvariantCulture)}"
        : NetworkMagic!.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>"Neo X · testnet", as the checkout page shows it.</summary>
    public string Label => $"{DisplayName} · {NetworkLabel}";

    public string? ExplorerUrl(string? txId) => txId is null || ExplorerTx is null ? null : ExplorerTx.Replace("{tx}", txId);
}

/// <summary>The networks the watcher knows out of the box. Any other EVM chain works with Network "custom" and an explicit ChainId.</summary>
public static class ChainPresets
{
    sealed record Preset(
        string Kind, string DisplayName, string NetworkLabel, long? ChainId, uint? Magic, string Native, int Decimals,
        string? Explorer, int Confirmations, string[] Rpc);

    static readonly Dictionary<string, Preset> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mainnet"] = new(ChainProfile.NeoN3, "Neo N3", "mainnet", null, 860833102u, "GAS", 8,
            "https://dora.coz.io/transaction/neo3/mainnet/{tx}", 0, ["https://mainnet1.neo.coz.io:443", "http://seed1.neo.org:10332"]),
        ["testnet"] = new(ChainProfile.NeoN3, "Neo N3", "testnet", null, 894710606u, "GAS", 8,
            "https://dora.coz.io/transaction/neo3/testnet/{tx}", 0, ["https://testnet1.neo.coz.io:443", "http://seed1t5.neo.org:20332"]),
        // Neo X runs dBFT like N3: a block is final when it exists, so no confirmation depth is needed.
        ["neox-mainnet"] = new(ChainProfile.Evm, "Neo X", "mainnet", 47763, null, "GAS", 18,
            "https://xexplorer.neo.org/tx/{tx}", 0, ["https://mainnet-1.rpc.banelabs.org", "https://mainnet-2.rpc.banelabs.org"]),
        ["neox-testnet"] = new(ChainProfile.Evm, "Neo X", "testnet", 12227332, null, "GAS", 18,
            "https://xt4scan.ngd.network/tx/{tx}", 0, ["https://neoxt4seed1.ngd.network", "https://neoxt4seed2.ngd.network"]),
        ["xlayer-mainnet"] = new(ChainProfile.Evm, "X Layer", "mainnet", 196, null, "OKB", 18,
            "https://www.oklink.com/xlayer/tx/{tx}", 2, ["https://rpc.xlayer.tech", "https://xlayerrpc.okx.com"]),
        ["xlayer-testnet"] = new(ChainProfile.Evm, "X Layer", "testnet", 1952, null, "OKB", 18,
            "https://www.oklink.com/xlayer-test/tx/{tx}", 2, ["https://testrpc.xlayer.tech", "https://xlayertestrpc.okx.com"]),
        // Base's own public node throttles eth_getLogs hard ("request limit reached"), so a second public node comes first.
        ["base-mainnet"] = new(ChainProfile.Evm, "Base", "mainnet", 8453, null, "ETH", 18,
            "https://basescan.org/tx/{tx}", 6, ["https://base-rpc.publicnode.com", "https://mainnet.base.org"]),
        ["base-sepolia"] = new(ChainProfile.Evm, "Base", "testnet", 84532, null, "ETH", 18,
            "https://sepolia.basescan.org/tx/{tx}", 6, ["https://base-sepolia-rpc.publicnode.com", "https://sepolia.base.org"]),
    };

    public static IEnumerable<string> EvmNetworks => Presets.Where(p => p.Value.Kind == ChainProfile.Evm).Select(p => p.Key);

    public static ChainProfile Resolve(WatcherOptions options)
    {
        var kind = (options.Chain ?? ChainProfile.NeoN3).Trim().ToLowerInvariant();
        if (kind is not (ChainProfile.NeoN3 or ChainProfile.Evm))
            throw new InvalidOperationException($"Watcher:Chain must be '{ChainProfile.NeoN3}' or '{ChainProfile.Evm}', not '{options.Chain}'.");

        var network = (options.Network ?? "").Trim();
        var preset = Presets.TryGetValue(network, out var found) && found.Kind == kind ? found : null;
        var custom = preset is null && kind == ChainProfile.Evm && network.Equals("custom", StringComparison.OrdinalIgnoreCase);
        if (preset is null && !custom)
        {
            throw new InvalidOperationException(kind == ChainProfile.NeoN3
                ? $"Watcher:Network must be 'mainnet' or 'testnet' for Neo N3, not '{network}'."
                : $"Watcher:Network '{network}' is not a known EVM network ({string.Join(", ", EvmNetworks)}); use 'custom' with Watcher:ChainId.");
        }
        if (custom && options.ChainId is null)
            throw new InvalidOperationException("Watcher:ChainId is required for a custom EVM network.");

        var rpc = options.RpcUrls.Length > 0 ? options.RpcUrls : preset?.Rpc ?? [];
        if (rpc.Length == 0)
            throw new InvalidOperationException("Watcher:RpcUrls must list at least one JSON-RPC endpoint for a custom network.");
        if (options.Confirmations is < 0)
            throw new InvalidOperationException("Watcher:Confirmations cannot be negative.");

        return new ChainProfile(
            kind,
            custom ? "custom" : network.ToLowerInvariant(),
            options.ChainName ?? preset?.DisplayName ?? "EVM",
            preset?.NetworkLabel ?? "custom",
            options.ChainId ?? preset?.ChainId,
            preset?.Magic,
            options.NativeSymbol ?? preset?.Native ?? "ETH",
            preset?.Decimals ?? 18,
            options.ExplorerTx ?? preset?.Explorer,
            options.Confirmations ?? preset?.Confirmations ?? 0,
            rpc);
    }
}
