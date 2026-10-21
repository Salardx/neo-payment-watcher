using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Neo;

namespace NeoPaymentWatcher.Services;

/// <summary>The tokens the watcher accepts, with symbol and decimals read from chain.</summary>
public sealed class TokenRegistry
{
    public const string NeoHash = "0xef4073a0f2b305a38ec4050e4d3d28bc40ea63f5";
    public const string GasHash = "0xd2a4cff31913016155e38e474a2c06d08be276cf";

    /// <summary>The chain's own coin on an EVM network (GAS on Neo X, OKB on X Layer, ETH on Base), which has no contract.</summary>
    public const string Native = "native";

    readonly Dictionary<string, TokenInfo> tokens = new();
    readonly HashSet<string> hashes = new();

    public IReadOnlyCollection<TokenInfo> All => tokens.Values;

    public IReadOnlySet<string> Hashes => hashes;

    /// <summary>Loads the configured tokens; with none configured, NEO and GAS on Neo N3, and the native coin on an EVM chain.</summary>
    public async Task LoadAsync(IChainClient chain, WatcherOptions settings, CancellationToken ct)
    {
        var evm = chain.Chain.IsEvm;
        var wanted = settings.Tokens.Select(t => Normalize(t, evm)).Distinct().ToList();
        if (evm)
        {
            if (settings.WatchNative && !wanted.Contains(Native))
                wanted.Insert(0, Native);
        }
        else if (wanted.Count == 0)
        {
            wanted = [NeoHash, GasHash];
        }
        if (wanted.Count == 0)
            throw new InvalidOperationException("Nothing to accept: list Watcher:Tokens or set Watcher:WatchNative.");
        foreach (var hash in wanted)
            Register(await chain.GetTokenInfoAsync(hash, ct));
    }

    public void Register(TokenInfo token)
    {
        tokens[token.Hash] = token;
        hashes.Add(token.Hash);
    }

    /// <summary>Returns the token, or a placeholder for one that has since been removed from the configuration.</summary>
    public TokenInfo Get(string hash) => tokens.GetValueOrDefault(hash) ?? new TokenInfo(hash, "UNKNOWN", 0);

    /// <summary>Finds a token by contract hash, by "native", or by symbol when the symbol is unambiguous.</summary>
    public TokenInfo? Resolve(string? symbolOrHash)
    {
        var text = symbolOrHash?.Trim() ?? "";
        if (text.Equals(Native, StringComparison.OrdinalIgnoreCase))
            return tokens.GetValueOrDefault(Native);
        if (NeoAddress.TryNormalizeContractHash(text, out var hash))
            return tokens.GetValueOrDefault(hash);
        var matches = tokens.Values
            .Where(t => string.Equals(t.Symbol, text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    static string Normalize(string hash, bool evm)
    {
        if (evm && hash.Trim().Equals(Native, StringComparison.OrdinalIgnoreCase))
            return Native;
        return NeoAddress.TryNormalizeContractHash(hash, out var normalized)
            ? normalized
            : throw new InvalidOperationException($"'{hash}' is not a contract hash.");
    }
}
