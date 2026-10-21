using System.Globalization;
using System.Numerics;
using System.Text;

namespace NeoPaymentWatcher.Chains.Evm;

/// <summary>Hex quantities, addresses, log topics and the two ABI return types the watcher reads.</summary>
public static class EvmHex
{
    public static string Quantity(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);

    public static BigInteger ToBigInteger(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
            return BigInteger.Zero;
        var digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        return digits.Length == 0 ? BigInteger.Zero : BigInteger.Parse("0" + digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    public static long ToInt64(string? hex) => (long)ToBigInteger(hex);

    /// <summary>"0x" and 40 hex digits, in any case, to lowercase. Checksums are not enforced: a wrong-case address still names the same account.</summary>
    public static bool TryNormalizeAddress(string? value, out string address)
    {
        address = "";
        if (value is null)
            return false;
        var text = value.Trim();
        if (text.Length != 42 || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || !text[2..].All(Uri.IsHexDigit))
            return false;
        address = "0x" + text[2..].ToLowerInvariant();
        return true;
    }

    /// <summary>An address as it appears in an indexed log topic: left-padded to 32 bytes.</summary>
    public static string AddressTopic(string address) => "0x" + new string('0', 24) + address[2..];

    public static bool TryAddressFromTopic(string? topic, out string address)
    {
        address = "";
        if (topic is null || topic.Length != 66 || !topic.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return false;
        return TryNormalizeAddress("0x" + topic[26..], out address);
    }

    /// <summary>Decodes a <c>string</c> return value, or a <c>bytes32</c> one for tokens like MKR that return the symbol as fixed bytes.</summary>
    public static string DecodeString(string? hex)
    {
        var bytes = Convert.FromHexString(hex is { Length: > 2 } ? hex[2..] : "");
        if (bytes.Length == 32)
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        if (bytes.Length < 64)
            return "";
        var offset = (int)Word(bytes, 0);
        var length = (int)Word(bytes, offset);
        return offset + 32 + length <= bytes.Length ? Encoding.UTF8.GetString(bytes, offset + 32, length) : "";
    }

    public static int DecodeUInt8(string? hex) => (int)ToBigInteger(hex);

    static BigInteger Word(byte[] bytes, int offset) =>
        offset + 32 <= bytes.Length ? new BigInteger(bytes.AsSpan(offset, 32), isUnsigned: true, isBigEndian: true) : BigInteger.Zero;
}
