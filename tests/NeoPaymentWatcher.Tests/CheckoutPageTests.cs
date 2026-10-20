using NeoPaymentWatcher.Api;
using NeoPaymentWatcher.Neo;
using NeoPaymentWatcher.Storage;

namespace NeoPaymentWatcher.Tests;

public class CheckoutPageTests
{
    const string Merchant = "NQGp42c1jC2Uom7bpLRsMseSSNk2A6vx2x";
    const string TxId = "0x84bea723bc3100affa007b8b6b58a5ace094f3585cc0a7e6b5e1f1056089f42c";
    const long Now = 1_791_000_000_000;
    const long Minute = 60_000;

    static readonly TokenInfo Gas = new("0xd2a4cff31913016155e38e474a2c06d08be276cf", "GAS", 8);
    static readonly CheckoutOptions Options = new() { Title = "Salar's Shop" };

    static InvoiceRecord Invoice(string status = "pending", string? reference = "order-1001", long expiresAt = Now + 15 * Minute) =>
        new("inv_08d735fe38e39f03", Merchant, Gas.Hash, 100_000_000, 100_002_098, status, reference, Now, expiresAt,
            status == "paid" ? TxId : null, status == "paid" ? Now + 3 * Minute : null);

    [Fact]
    public void Pending_page_shows_what_to_pay_where_and_until_when()
    {
        var html = CheckoutPage.Render(Invoice(), Gas, "testnet", Options, "n0nce", Now);

        Assert.Contains("<title>1.00002098 GAS · Salar&#39;s Shop</title>", html);
        Assert.Contains("Send exactly", html);
        Assert.Contains("1.00002098 <small>GAS</small>", html);
        Assert.Contains("Your order is 1 GAS; the extra 0.00002098 identifies it.", html);
        Assert.Contains($"<code>{Merchant}</code>", html);
        Assert.Contains($"data-copy=\"{Merchant}\"", html);
        Assert.Contains("<figure><svg xmlns=\"http://www.w3.org/2000/svg\"", html);
        Assert.Contains("Waiting for payment · expires in 15 min", html);
        Assert.Contains("Order: order-1001", html);
        Assert.Contains("Neo N3 · testnet", html);
        Assert.Contains("data-status=\"pending\"", html);
        Assert.Contains($"data-expires=\"{Now + 15 * Minute}\"", html);
        Assert.Contains("<script nonce=\"n0nce\">", html);
        Assert.Contains("<noscript><meta http-equiv=\"refresh\" content=\"10\"></noscript>", html);
        Assert.DoesNotContain("http://", html.Replace("http://www.w3.org/2000/svg", ""));   // nothing external is loaded
    }

    [Fact]
    public void Paid_page_links_the_transaction_on_the_explorer_and_stops_asking_for_payment()
    {
        var html = CheckoutPage.Render(Invoice("paid"), Gas, "mainnet", Options, "n0nce", Now + 10 * Minute);

        Assert.Contains("data-status=\"paid\"", html);
        Assert.Contains("Received", html);
        Assert.Contains($"Paid ✓ <a href=\"https://dora.coz.io/transaction/neo3/mainnet/{TxId}\" rel=\"noopener\">tx 0x84bea723…89f42c</a>", html);
        Assert.DoesNotContain("<noscript>", html);
    }

    [Fact]
    public void Expired_and_overdue_pages_say_so()
    {
        Assert.Contains("This invoice has expired.", CheckoutPage.Render(Invoice("expired"), Gas, "testnet", Options, "n", Now));
        Assert.Contains("Time is up · checking the latest blocks",
            CheckoutPage.Render(Invoice(expiresAt: Now - 1), Gas, "testnet", Options, "n", Now));
    }

    [Fact]
    public void Everything_from_the_merchant_or_the_chain_is_html_encoded()
    {
        var html = CheckoutPage.Render(Invoice(reference: "<img src=x onerror=alert(1)> & \"quotes\""), Gas, "testnet",
            new CheckoutOptions { Title = "<b>Shop</b>" }, "n", Now);

        Assert.Contains("Order: &lt;img src=x onerror=alert(1)&gt; &amp; &quot;quotes&quot;", html);
        Assert.Contains("<h1>&lt;b&gt;Shop&lt;/b&gt;</h1>", html);
        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void Qr_code_holds_the_address_by_default_and_the_payment_link_on_request()
    {
        var address = CheckoutPage.Render(Invoice(), Gas, "testnet", Options, "n", Now);
        var uri = CheckoutPage.Render(Invoice(), Gas, "testnet", new CheckoutOptions { QrContent = "uri" }, "n", Now);

        Assert.Contains(QrCode.Encode(Merchant).ToSvg(), address);
        Assert.Contains(QrCode.Encode($"neo:{Merchant}?asset=gas&amount=1.00002098").ToSvg(), uri);
        Assert.NotEqual(address, uri);
    }

    [Fact]
    public void Status_carries_only_the_state_of_the_invoice()
    {
        var pending = CheckoutPage.Status(Invoice(), "testnet");
        Assert.Equal("pending", pending.Status);
        Assert.Null(pending.PaidTxId);
        Assert.Null(pending.ExplorerUrl);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Now + 15 * Minute), pending.ExpiresAt);

        var paid = CheckoutPage.Status(Invoice("paid"), "testnet");
        Assert.Equal("paid", paid.Status);
        Assert.Equal(TxId, paid.PaidTxId);
        Assert.Equal($"https://dora.coz.io/transaction/neo3/testnet/{TxId}", paid.ExplorerUrl);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Now + 3 * Minute), paid.PaidAt);
    }

    [Fact]
    public void Not_found_page_names_the_shop_and_nothing_else() =>
        Assert.Contains("No such invoice", CheckoutPage.NotFound(Options));

    [Theory]
    [InlineData("https://pay.example.com", true, "https://pay.example.com/pay/inv_1")]
    [InlineData("https://pay.example.com/", true, "https://pay.example.com/pay/inv_1")]
    [InlineData("", true, null)]
    [InlineData("https://pay.example.com", false, null)]
    public void Checkout_url_needs_the_page_enabled_and_a_public_base_url(string baseUrl, bool enabled, string? expected) =>
        Assert.Equal(expected, new CheckoutOptions { PublicBaseUrl = baseUrl, Enabled = enabled }.UrlFor("inv_1"));

    [Fact]
    public void Invoice_responses_and_webhooks_carry_the_checkout_url()
    {
        var checkout = new CheckoutOptions { PublicBaseUrl = "https://pay.example.com" };
        var view = InvoiceView.Create(Invoice(), Gas, checkout.UrlFor("inv_08d735fe38e39f03"));
        Assert.Equal("https://pay.example.com/pay/inv_08d735fe38e39f03", view.CheckoutUrl);

        var transfer = new Nep17Transfer(TxId, 0, 100, Now, Gas.Hash, "NWf1NLqnUEe55eq4TW3V1DwUhhhzJfrDTe", Merchant, 100_002_098);
        Assert.Contains("\"checkoutUrl\":\"https://pay.example.com/pay/inv_08d735fe38e39f03\"",
            WebhookPayloads.Build("testnet", Gas, transfer, Invoice("paid"), checkout));
        Assert.Contains("\"checkoutUrl\":null", WebhookPayloads.Build("testnet", Gas, transfer, Invoice("paid")));
    }

    [Theory]
    [InlineData("address", "", true)]
    [InlineData("URI", "https://pay.example.com", true)]
    [InlineData("png", "", false)]
    [InlineData("address", "pay.example.com", false)]
    [InlineData("address", "ftp://pay.example.com", false)]
    public void Settings_are_validated(string qrContent, string baseUrl, bool valid)
    {
        var options = new WatcherOptions
        {
            RpcUrls = ["https://testnet1.neo.coz.io:443"],
            Addresses = [Merchant],
            Checkout = new CheckoutOptions { QrContent = qrContent, PublicBaseUrl = baseUrl },
        };

        if (valid)
            options.Validate();
        else
            Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
