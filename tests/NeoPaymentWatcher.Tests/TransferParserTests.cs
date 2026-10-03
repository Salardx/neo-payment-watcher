using System.Numerics;
using System.Text.Json;
using NeoPaymentWatcher.Neo;

namespace NeoPaymentWatcher.Tests;

public class TransferParserTests
{
    const string Gas = "0xd2a4cff31913016155e38e474a2c06d08be276cf";

    // Accounts from mainnet transaction 0x99a9a8bd…63597 (block 13746549).
    const string Sender = "hZUbWNs/22TdTn2vYV59dFhmZCg=";
    const string Merchant = "zQjwPDPMmdflObiIaF9Do/vmV7w=";

    static readonly HashSet<string> Watched = [Convert.ToHexStringLower(Convert.FromBase64String(Merchant))];
    static readonly HashSet<string> Tokens = [Gas];

    [Fact]
    public void Picks_only_real_incoming_payments()
    {
        var log = Log("HALT",
            Transfer(Gas, Any, Bytes(Merchant), "860708458"), // 0: GAS mint (claim), not a payment
            Transfer(Gas.ToUpperInvariant(), Bytes(Sender), Bytes(Merchant), "203957997308"), // 1: the payment
            Transfer("0x341cbe084b3b15fccf15064c7b7790f5f0f659d2", Bytes(Sender), Bytes(Merchant), "1", Bytes("6g8=")), // 2: NEP-11
            Transfer("0x1111111111111111111111111111111111111111", Bytes(Sender), Bytes(Merchant), "5"), // 3: unknown token
            Transfer(Gas, Bytes(Merchant), Bytes(Merchant), "10"), // 4: self-transfer
            Transfer(Gas, Bytes(Sender), Bytes(Merchant), "0"), // 5: zero amount
            Transfer(Gas, Bytes(Merchant), Bytes(Sender), "77")); // 6: outgoing

        var transfer = Assert.Single(TransferParser.ParseIncoming(log, 13746549, 1_791_039_000_000, Watched, Tokens));

        Assert.Equal(1, transfer.NotifyIndex);
        Assert.Equal(Gas, transfer.TokenHash);
        Assert.Equal(BigInteger.Parse("203957997308"), transfer.Amount);
        Assert.Equal(NeoAddress.FromScriptHash(Convert.FromBase64String(Merchant)), transfer.To);
        Assert.Equal(NeoAddress.FromScriptHash(Convert.FromBase64String(Sender)), transfer.From);
        Assert.Equal(13746549u, transfer.BlockIndex);
    }

    [Fact]
    public void Ignores_failed_transactions() =>
        Assert.Empty(TransferParser.ParseIncoming(
            Log("FAULT", Transfer(Gas, Bytes(Sender), Bytes(Merchant), "100")), 1, 0, Watched, Tokens));

    static JsonElement Log(string vmState, params string[] notifications) => JsonDocument.Parse($$"""
        {
          "txid": "0x99a9a8bdfa53457dc2d0f71d3c296bb277fea5ac78fab8560783d03528b63597",
          "executions": [{
            "trigger": "Application",
            "vmstate": "{{vmState}}",
            "notifications": [{{string.Join(",", notifications)}}]
          }]
        }
        """).RootElement;

    static string Transfer(string contract, string from, string to, string amount, string? tokenId = null)
    {
        List<string> args = [from, to, $$"""{ "type": "Integer", "value": "{{amount}}" }"""];
        if (tokenId is not null)
            args.Add(tokenId);
        return $$"""{ "contract": "{{contract}}", "eventname": "Transfer", "state": { "type": "Array", "value": [{{string.Join(",", args)}}] } }""";
    }

    static string Bytes(string base64) => $$"""{ "type": "ByteString", "value": "{{base64}}" }""";

    const string Any = """{ "type": "Any" }""";
}
