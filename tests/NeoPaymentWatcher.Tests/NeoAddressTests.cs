using NeoPaymentWatcher.Neo;

namespace NeoPaymentWatcher.Tests;

public class NeoAddressTests
{
    // A mainnet address with its script hash bytes as they appear in notifications.
    const string Address = "NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTe";
    const string ScriptHash = "75d4f75127f909136174fb1bab6a934cca790967";

    [Fact]
    public void Decodes_address_to_script_hash()
    {
        Assert.True(NeoAddress.TryGetScriptHash(Address, out var hash));
        Assert.Equal(ScriptHash, Convert.ToHexStringLower(hash));
    }

    [Fact]
    public void Encodes_script_hash_to_address() =>
        Assert.Equal(Address, NeoAddress.FromScriptHash(Convert.FromHexString(ScriptHash)));

    [Theory]
    [InlineData("NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTf")] // last character changed: checksum fails
    [InlineData("NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDT")] // truncated
    [InlineData("NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDT0")] // '0' is not in the Base58 alphabet
    [InlineData("0x31Ca1498A596eB51B76c90837eCE403C3ca64ceB")] // an EVM address
    [InlineData("")]
    public void Rejects_invalid_addresses(string address) =>
        Assert.False(NeoAddress.TryGetScriptHash(address, out _));

    [Fact]
    public void Rejects_legacy_address_with_the_same_script_hash()
    {
        var legacy = NeoAddress.Encode(0x17, Convert.FromHexString(ScriptHash));
        Assert.StartsWith("A", legacy);
        Assert.False(NeoAddress.TryGetScriptHash(legacy, out _));
    }

    [Fact]
    public void Displays_contract_hashes_big_endian() =>
        Assert.Equal("0xd2a4cff31913016155e38e474a2c06d08be276cf",
            NeoAddress.ToDisplayHash(Convert.FromHexString("cf76e28bd0062c4a478ee35561011319f3cfa4d2")));

    [Theory]
    [InlineData("0xD2A4CFF31913016155E38E474A2C06D08BE276CF")]
    [InlineData("d2a4cff31913016155e38e474a2c06d08be276cf")]
    public void Normalizes_contract_hashes(string input)
    {
        Assert.True(NeoAddress.TryNormalizeContractHash(input, out var hash));
        Assert.Equal("0xd2a4cff31913016155e38e474a2c06d08be276cf", hash);
    }

    [Theory]
    [InlineData("0xd2a4cff31913016155e38e474a2c06d08be276")]
    [InlineData("0xz2a4cff31913016155e38e474a2c06d08be276cf")]
    [InlineData(null)]
    public void Rejects_malformed_contract_hashes(string? input) =>
        Assert.False(NeoAddress.TryNormalizeContractHash(input, out _));
}
