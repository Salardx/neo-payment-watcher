using System.Numerics;
using NeoPaymentWatcher.Api;
using NeoPaymentWatcher.Neo;
using NeoPaymentWatcher.Services;

namespace NeoPaymentWatcher.Tests;

public class TokenAmountTests
{
    [Theory]
    [InlineData("1", 8, "100000000")]
    [InlineData("1.5", 8, "150000000")]
    [InlineData("0.00000001", 8, "1")]
    [InlineData(" 2039.57997308 ", 8, "203957997308")]
    [InlineData(".5", 2, "50")]
    [InlineData("12.", 2, "1200")]
    [InlineData("7", 0, "7")]
    public void Parses_decimal_strings_to_base_units(string text, int decimals, string expected)
    {
        Assert.True(TokenAmount.TryParse(text, decimals, out var units));
        Assert.Equal(BigInteger.Parse(expected), units);
    }

    [Theory]
    [InlineData("1.123456789", 8)] // more decimals than the token has
    [InlineData("0.5", 0)]
    [InlineData("-1", 8)]
    [InlineData("1e5", 8)]
    [InlineData("1,5", 8)]
    [InlineData("1.2.3", 8)]
    [InlineData(".", 8)]
    [InlineData("", 8)]
    [InlineData(null, 8)]
    public void Rejects_malformed_amounts(string? text, int decimals) =>
        Assert.False(TokenAmount.TryParse(text, decimals, out _));

    [Theory]
    [InlineData("150000000", 8, "1.5")]
    [InlineData("100000000", 8, "1")]
    [InlineData("1", 8, "0.00000001")]
    [InlineData("203957997308", 8, "2039.57997308")]
    [InlineData("7", 0, "7")]
    public void Formats_base_units(string units, int decimals, string expected) =>
        Assert.Equal(expected, TokenAmount.Format(BigInteger.Parse(units), decimals));

    [Theory]
    [InlineData(8, 9999)] // GAS: at most 0.00009999 extra
    [InlineData(6, 9999)] // a 6-decimal stablecoin: at most 0.009999 extra
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    [InlineData(0, 0)] // NEO is indivisible, so no unique amounts
    public void Caps_unique_offsets_at_one_percent_of_a_token(int decimals, int expected) =>
        Assert.Equal(expected, InvoiceService.MaxOffset(decimals, 9999));

    [Fact]
    public void Signs_webhooks_with_hmac_sha256() =>
        // RFC 4231 test case 2.
        Assert.Equal("sha256=5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843",
            WebhookDispatcher.Sign("Jefe", "what do ya want for nothing?"));

    [Fact]
    public void Backs_off_exponentially_up_to_an_hour()
    {
        Assert.Equal(5_000, WebhookDispatcher.Backoff(1));
        Assert.Equal(10_000, WebhookDispatcher.Backoff(2));
        Assert.Equal(3_600_000, WebhookDispatcher.Backoff(20));
    }
}
