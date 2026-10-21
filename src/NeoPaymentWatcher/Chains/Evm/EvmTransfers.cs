using System.Numerics;
using System.Text.Json;
using NeoPaymentWatcher.Services;

namespace NeoPaymentWatcher.Chains.Evm;

public sealed record NativeCandidate(string TxId, string From, string To, BigInteger Value);

/// <summary>
/// Pure parsing of what an EVM node returns. ERC-20 payments come from <c>Transfer</c> logs, which only
/// exist for transactions that succeeded. Native-coin payments come from a block's transactions, and
/// those need their receipt checked by the caller: a failed transaction moves nothing. Native coin sent
/// by a contract (an "internal transfer") never shows up in a block's transaction list; see the README.
/// </summary>
public static class EvmTransfers
{
    /// <summary>keccak256("Transfer(address,address,uint256)").</summary>
    public const string TransferTopic = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";

    public static List<NativeCandidate> NativeCandidates(JsonElement transactions, IReadOnlySet<string> watchedKeys)
    {
        var candidates = new List<NativeCandidate>();
        if (transactions.ValueKind != JsonValueKind.Array)
            return candidates;
        foreach (var tx in transactions.EnumerateArray())
        {
            if (tx.ValueKind != JsonValueKind.Object)
                continue;
            if (!EvmHex.TryNormalizeAddress(Text(tx, "to"), out var to) || !watchedKeys.Contains(to))
                continue;
            if (!EvmHex.TryNormalizeAddress(Text(tx, "from"), out var from) || from == to)
                continue;
            var value = EvmHex.ToBigInteger(Text(tx, "value"));
            if (value.Sign <= 0)
                continue;
            candidates.Add(new NativeCandidate(Text(tx, "hash")!, from, to, value));
        }
        return candidates;
    }

    public static List<TokenTransfer> ParseLogs(
        JsonElement logs, uint blockIndex, long blockTime, IReadOnlySet<string> watchedKeys, IReadOnlySet<string> tokenHashes)
    {
        var transfers = new List<TokenTransfer>();
        if (logs.ValueKind != JsonValueKind.Array)
            return transfers;
        foreach (var log in logs.EnumerateArray())
        {
            if (log.TryGetProperty("removed", out var removed) && removed.ValueKind == JsonValueKind.True)
                continue;
            if (!EvmHex.TryNormalizeAddress(Text(log, "address"), out var token) || !tokenHashes.Contains(token))
                continue;
            if (!log.TryGetProperty("topics", out var topics) || topics.ValueKind != JsonValueKind.Array || topics.GetArrayLength() != 3)
                continue;   // ERC-20 Transfer indexes both parties; ERC-721 adds a fourth topic for the token id
            if (!string.Equals(topics[0].GetString(), TransferTopic, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!EvmHex.TryAddressFromTopic(topics[1].GetString(), out var from) || !EvmHex.TryAddressFromTopic(topics[2].GetString(), out var to))
                continue;
            if (from == to || !watchedKeys.Contains(to))
                continue;
            var amount = EvmHex.ToBigInteger(Text(log, "data"));
            if (amount.Sign <= 0)
                continue;

            transfers.Add(new TokenTransfer(Text(log, "transactionHash")!, (int)EvmHex.ToInt64(Text(log, "logIndex")),
                blockIndex, blockTime, token, from, to, amount));
        }
        return transfers;
    }

    public static TokenTransfer Native(NativeCandidate c, uint blockIndex, long blockTime) =>
        new(c.TxId, -1, blockIndex, blockTime, TokenRegistry.Native, c.From, c.To, c.Value);

    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
