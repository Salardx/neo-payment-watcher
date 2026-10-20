using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using NeoPaymentWatcher.Services;
using NeoPaymentWatcher.Storage;

namespace NeoPaymentWatcher.Api;

/// <summary>Serves the checkout page and its status. Public endpoints: an invoice id, 64 random bits, is the only key.</summary>
public sealed class CheckoutService(WatcherStore store, TokenRegistry tokens, IOptions<WatcherOptions> options)
{
    const string Html = "text/html; charset=utf-8";

    public IResult Page(string id, HttpResponse response)
    {
        var settings = options.Value;
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        response.Headers.ContentSecurityPolicy =
            $"default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-{nonce}'; connect-src 'self'; base-uri 'none'; form-action 'none'";
        response.Headers.CacheControl = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";   // the explorer link must not learn the invoice URL
        response.Headers.XContentTypeOptions = "nosniff";

        if (store.GetInvoice(id) is not { } invoice)
            return Results.Content(CheckoutPage.NotFound(settings.Checkout), Html, statusCode: StatusCodes.Status404NotFound);

        var html = CheckoutPage.Render(invoice, tokens.Get(invoice.TokenHash), settings.Network, settings.Checkout, nonce,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return Results.Content(html, Html);
    }

    public IResult Status(string id, HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        return store.GetInvoice(id) is { } invoice
            ? Results.Ok(CheckoutPage.Status(invoice, options.Value.Network))
            : Results.NotFound();
    }
}
