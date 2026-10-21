using System.Numerics;
using System.Text.Json;
using NeoPaymentWatcher.Api;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Chains.Evm;

namespace NeoPaymentWatcher.Tests;

public class ChainsTests
{
    const string Merchant = "0x1234567890abcdef1234567890abcdef12345678";
    const string Payer = "0xabcdefabcdefabcdefabcdefabcdefabcdefabcd";
    const string Usdc = "0x833589fcd6edb6e08f4c7c32d4f71b54bda02913";   // USDC on Base, 6 decimals

    static readonly HashSet<string> Watched = [Merchant];
    static readonly HashSet<string> Tokens = [Usdc, "native"];

    [Fact]
    public void Neo_n3_presets_keep_their_magic_number_as_the_database_key()
    {
        var testnet = ChainPresets.Resolve(new WatcherOptions { Network = "testnet" });

        Assert.Equal("neo-n3", testnet.Kind);
        Assert.False(testnet.IsEvm);
        Assert.Equal(894710606u, testnet.NetworkMagic);
        Assert.Equal("894710606", testnet.NetworkKey);
        Assert.Equal("Neo N3 · testnet", testnet.Label);
        Assert.Equal(0, testnet.Confirmations);
        Assert.Equal(2, testnet.RpcUrls.Length);
        Assert.Equal("https://dora.coz.io/transaction/neo3/testnet/0xabc", testnet.ExplorerUrl("0xabc"));
    }

    [Fact]
    public void Evm_presets_know_their_chain_id_coin_and_explorer()
    {
        var neox = ChainPresets.Resolve(new WatcherOptions { Chain = "evm", Network = "neox-mainnet" });

        Assert.True(neox.IsEvm);
        Assert.Equal(47763, neox.ChainId);
        Assert.Equal("evm:47763", neox.NetworkKey);
        Assert.Equal("GAS", neox.NativeSymbol);
        Assert.Equal(18, neox.NativeDecimals);
        Assert.Equal("Neo X · mainnet", neox.Label);
        Assert.Equal("https://xexplorer.neo.org/tx/0xabc", neox.ExplorerUrl("0xabc"));
        Assert.Null(neox.ExplorerUrl(null));

        Assert.Equal("OKB", ChainPresets.Resolve(new WatcherOptions { Chain = "evm", Network = "xlayer-mainnet" }).NativeSymbol);
        Assert.Equal(8453, ChainPresets.Resolve(new WatcherOptions { Chain = "evm", Network = "base-mainnet" }).ChainId);
    }

    [Fact]
    public void Settings_override_a_preset_and_describe_a_custom_chain()
    {
        var overridden = ChainPresets.Resolve(new WatcherOptions
        {
            Chain = "evm",
            Network = "base-mainnet",
            Confirmations = 3,
            RpcUrls = ["https://rpc.example"],
            ExplorerTx = "https://x/{tx}",
        });
        Assert.Equal(3, overridden.Confirmations);
        Assert.Equal(["https://rpc.example"], overridden.RpcUrls);
        Assert.Equal("https://x/0x1", overridden.ExplorerUrl("0x1"));

        var custom = ChainPresets.Resolve(new WatcherOptions
        {
            Chain = "evm",
            Network = "custom",
            ChainId = 137,
            ChainName = "Polygon",
            NativeSymbol = "POL",
            RpcUrls = ["https://polygon.example"],
        });
        Assert.Equal("custom", custom.Network);
        Assert.Equal("evm:137", custom.NetworkKey);
        Assert.Equal("Polygon · custom", custom.Label);
        Assert.Equal("POL", custom.NativeSymbol);
        Assert.Null(custom.ExplorerUrl("0x1"));
    }

    [Theory]
    [InlineData("neo-n3", "neox-mainnet")]      // an EVM network on the Neo N3 kind
    [InlineData("evm", "mainnet")]              // and the other way round
    [InlineData("evm", "polygon")]              // unknown
    [InlineData("evm", "custom")]               // custom without a chain id
    [InlineData("solana", "mainnet")]           // unknown kind
    public void Unknown_or_incomplete_networks_are_refused(string kind, string network) =>
        Assert.Throws<InvalidOperationException>(() => ChainPresets.Resolve(new WatcherOptions { Chain = kind, Network = network }));

    [Fact]
    public void Evm_settings_validate_addresses_and_tokens()
    {
        var valid = new WatcherOptions { Chain = "evm", Network = "neox-testnet", Addresses = ["0x1234567890ABCDEF1234567890abcdef12345678"], Tokens = ["native", Usdc] };
        valid.Validate();

        Assert.Throws<InvalidOperationException>(new WatcherOptions { Chain = "evm", Network = "neox-testnet", Addresses = ["NQGp42c1jC2Uom7bpLRsMseSSNk2A6vx2x"] }.Validate);
        Assert.Throws<InvalidOperationException>(new WatcherOptions { Chain = "evm", Network = "neox-testnet", Addresses = [Merchant], WatchNative = false }.Validate);
        Assert.Throws<InvalidOperationException>(new WatcherOptions { Network = "testnet", Addresses = [Merchant] }.Validate);   // an EVM address on Neo N3
    }

    [Fact]
    public void Payment_links_follow_nep9_on_neo_and_eip681_on_evm()
    {
        var neo = ChainPresets.Resolve(new WatcherOptions { Network = "mainnet" });
        var baseChain = ChainPresets.Resolve(new WatcherOptions { Chain = "evm", Network = "base-mainnet" });
        var gas = new TokenInfo("0xd2a4cff31913016155e38e474a2c06d08be276cf", "GAS", 8);

        Assert.Equal("neo:NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTe?asset=gas&amount=1.5",
            PaymentUris.Build(neo, "NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTe", gas, 150_000_000, "1.5"));
        Assert.Equal($"ethereum:{Merchant}@8453?value=1500000000000000000",
            PaymentUris.Build(baseChain, Merchant, new TokenInfo("native", "ETH", 18), BigInteger.Parse("1500000000000000000"), "1.5"));
        Assert.Equal($"ethereum:{Usdc}@8453/transfer?address={Merchant}&uint256=1500000",
            PaymentUris.Build(baseChain, Merchant, new TokenInfo(Usdc, "USDC", 6), 1_500_000, "1.5"));
    }

    [Fact]
    public void Hex_helpers_read_quantities_addresses_topics_and_abi_strings()
    {
        Assert.Equal(BigInteger.Zero, EvmHex.ToBigInteger("0x0"));
        Assert.Equal(BigInteger.Parse("1000000000000000000"), EvmHex.ToBigInteger("0xde0b6b3a7640000"));
        Assert.Equal(47763, EvmHex.ToInt64("0xba93"));
        Assert.Equal("0x1e240", EvmHex.Quantity(123456));

        Assert.True(EvmHex.TryNormalizeAddress(" 0x1234567890ABCDEF1234567890abcdef12345678 ", out var address));
        Assert.Equal(Merchant, address);
        Assert.False(EvmHex.TryNormalizeAddress("0x1234", out _));
        Assert.False(EvmHex.TryNormalizeAddress("NQGp42c1jC2Uom7bpLRsMseSSNk2A6vx2x", out _));

        var topic = EvmHex.AddressTopic(Merchant);
        Assert.Equal("0x000000000000000000000000" + Merchant[2..], topic);
        Assert.True(EvmHex.TryAddressFromTopic(topic, out var back));
        Assert.Equal(Merchant, back);

        // string: offset 0x20, length 4, "USDC"
        Assert.Equal("USDC", EvmHex.DecodeString("0x" + "20".PadLeft(64, '0') + "4".PadLeft(64, '0') + "55534443".PadRight(64, '0')));
        // bytes32, as MKR returns it
        Assert.Equal("MKR", EvmHex.DecodeString("0x" + "4d4b52".PadRight(64, '0')));
        Assert.Equal(6, EvmHex.DecodeUInt8("0x" + "6".PadLeft(64, '0')));
    }

    [Fact]
    public void Erc20_transfer_logs_to_a_watched_address_become_transfers()
    {
        var logs = JsonDocument.Parse($$"""
            [
              { "address": "{{Usdc.ToUpperInvariant().Replace("0X", "0x")}}", "topics": ["{{EvmTransfers.TransferTopic}}", "{{EvmHex.AddressTopic(Payer)}}", "{{EvmHex.AddressTopic(Merchant)}}"],
                "data": "0x{{"16e360".PadLeft(64, '0')}}", "logIndex": "0x2a", "transactionHash": "0xaaa", "removed": false },
              { "address": "{{Usdc}}", "topics": ["{{EvmTransfers.TransferTopic}}", "{{EvmHex.AddressTopic(Payer)}}", "{{EvmHex.AddressTopic(Merchant)}}"],
                "data": "0x{{"1".PadLeft(64, '0')}}", "logIndex": "0x2b", "transactionHash": "0xbbb", "removed": true },
              { "address": "{{Usdc}}", "topics": ["{{EvmTransfers.TransferTopic}}", "{{EvmHex.AddressTopic(Payer)}}", "{{EvmHex.AddressTopic(Payer)}}"],
                "data": "0x{{"1".PadLeft(64, '0')}}", "logIndex": "0x2c", "transactionHash": "0xccc" },
              { "address": "{{Usdc}}", "topics": ["{{EvmTransfers.TransferTopic}}", "{{EvmHex.AddressTopic(Payer)}}", "{{EvmHex.AddressTopic(Merchant)}}", "0x01"],
                "data": "0x", "logIndex": "0x2d", "transactionHash": "0xddd" },
              { "address": "{{Usdc}}", "topics": ["{{EvmTransfers.TransferTopic}}", "{{EvmHex.AddressTopic(Payer)}}", "{{EvmHex.AddressTopic(Merchant)}}"],
                "data": "0x{{"0".PadLeft(64, '0')}}", "logIndex": "0x2e", "transactionHash": "0xeee" },
              { "address": "0x4200000000000000000000000000000000000006", "topics": ["{{EvmTransfers.TransferTopic}}", "{{EvmHex.AddressTopic(Payer)}}", "{{EvmHex.AddressTopic(Merchant)}}"],
                "data": "0x{{"1".PadLeft(64, '0')}}", "logIndex": "0x2f", "transactionHash": "0xfff" }
            ]
            """).RootElement;

        var transfer = Assert.Single(EvmTransfers.ParseLogs(logs, 100, 1_791_000_000_000, Watched, Tokens));

        Assert.Equal(new TokenTransfer("0xaaa", 42, 100, 1_791_000_000_000, Usdc, Payer, Merchant, 1_500_000), transfer);
    }

    [Fact]
    public void Native_candidates_are_direct_payments_to_a_watched_address()
    {
        var transactions = JsonDocument.Parse($$"""
            [
              { "hash": "0x1", "from": "{{Payer}}", "to": "{{Merchant.ToUpperInvariant().Replace("0X", "0x")}}", "value": "0xde0b6b3a7640000" },
              { "hash": "0x2", "from": "{{Payer}}", "to": null, "value": "0x1" },
              { "hash": "0x3", "from": "{{Payer}}", "to": "{{Merchant}}", "value": "0x0" },
              { "hash": "0x4", "from": "{{Merchant}}", "to": "{{Merchant}}", "value": "0x5" },
              { "hash": "0x5", "from": "{{Payer}}", "to": "{{Payer}}", "value": "0x5" }
            ]
            """).RootElement;

        var candidate = Assert.Single(EvmTransfers.NativeCandidates(transactions, Watched));

        Assert.Equal(new NativeCandidate("0x1", Payer, Merchant, BigInteger.Parse("1000000000000000000")), candidate);
        Assert.Equal(new TokenTransfer("0x1", -1, 7, 5, "native", Payer, Merchant, BigInteger.Parse("1000000000000000000")),
            EvmTransfers.Native(candidate, 7, 5));
    }

    [Theory]
    [InlineData(8, 9999, 9999, "1")]                       // GAS on Neo N3: 1 base unit steps, up to 0.00009999
    [InlineData(6, 9999, 9999, "1")]                       // USDT/USDC: up to 0.009999
    [InlineData(18, 9999, 9999, "10000000000")]            // 18 decimals: 8-decimal steps
    [InlineData(2, 9999, 1, "1")]                          // 2 decimals: at most 0.01
    [InlineData(1, 9999, 0, "1")]
    [InlineData(0, 9999, 0, "1")]                          // NEO: no unique amounts possible
    [InlineData(8, 50, 50, "1")]
    public void Offsets_stay_under_one_percent_and_at_8_decimals(int decimals, int configured, int maxOffset, string unit)
    {
        var rule = InvoiceService.OffsetRule(decimals, configured);

        Assert.Equal(maxOffset, rule.MaxOffset);
        Assert.Equal(BigInteger.Parse(unit), rule.Unit);
    }
}
