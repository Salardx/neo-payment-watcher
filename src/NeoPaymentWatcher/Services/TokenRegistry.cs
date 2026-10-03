using NeoPaymentWatcher.Neo;

namespace NeoPaymentWatcher.Services;

/// <summary>The NEP-17 tokens the watcher accepts, with symbol and decimals read from chain.</summary>
public sealed class TokenRegistry
{
    public const string NeoHash = "0xef4073a0f2b305a38ec4050e4d3d28bc40ea63f5";
    public const string GasHash = "0xd2a4cff31913016155e38e474a2c06d08be276cf";

    readonly Dictionary<string, TokenInfo> tokens = new();
    readonly HashSet<string> hashes = new();

    public IReadOnlyCollection<TokenInfo> All => tokens.Values;

    public IReadOnlySet<string> Hashes => hashes;

    /// <summary>Loads the configured tokens, or NEO and GAS when none are configured.</summary>
    public async Task LoadAsync(NeoRpcClient rpc, IEnumerable<string> configured, CancellationToken ct)
    {
        var wanted = configured.Select(Normalize).Distinct().ToList();
        if (wanted.Count == 0)
            wanted = [NeoHash, GasHash];
        foreach (var hash in wanted)
            Register(await rpc.GetTokenInfoAsync(hash, ct));
    }

    public void Register(TokenInfo token)
    {
        tokens[token.Hash] = token;
        hashes.Add(token.Hash);
    }

    /// <summary>Returns the token, or a placeholder for one that has since been removed from the configuration.</summary>
    public TokenInfo Get(string hash) => tokens.GetValueOrDefault(hash) ?? new TokenInfo(hash, "UNKNOWN", 0);

    /// <summary>Finds a token by contract hash, or by symbol when the symbol is unambiguous.</summary>
    public TokenInfo? Resolve(string? symbolOrHash)
    {
        if (NeoAddress.TryNormalizeContractHash(symbolOrHash, out var hash))
            return tokens.GetValueOrDefault(hash);
        var matches = tokens.Values
            .Where(t => string.Equals(t.Symbol, symbolOrHash?.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    static string Normalize(string hash) => NeoAddress.TryNormalizeContractHash(hash, out var normalized)
        ? normalized
        : throw new InvalidOperationException($"'{hash}' is not a contract hash.");
}
