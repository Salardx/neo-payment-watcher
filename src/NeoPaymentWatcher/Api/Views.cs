using System.Globalization;
using System.Text.Json;
using NeoPaymentWatcher.Neo;
using NeoPaymentWatcher.Services;
using NeoPaymentWatcher.Storage;

namespace NeoPaymentWatcher.Api;

public sealed record CreateInvoiceRequest(string? Token, string? Amount, string? Address, string? Reference, int? TtlMinutes);

public sealed record TokenView(string Symbol, string Hash, int Decimals)
{
    public static TokenView Create(TokenInfo token) => new(token.Symbol, token.Hash, token.Decimals);
}

public sealed record TransferView(
    string TxId,
    int NotifyIndex,
    uint BlockIndex,
    DateTimeOffset BlockTime,
    TokenView Token,
    string From,
    string To,
    string Amount,
    string AmountUnits,
    string? InvoiceId)
{
    public static TransferView Create(Nep17Transfer t, TokenInfo token, string? invoiceId) => new(
        t.TxId, t.NotifyIndex, t.BlockIndex, DateTimeOffset.FromUnixTimeMilliseconds(t.BlockTime), TokenView.Create(token),
        t.From, t.To, TokenAmount.Format(t.Amount, token.Decimals), t.Amount.ToString(CultureInfo.InvariantCulture), invoiceId);
}

public sealed record InvoiceView(
    string Id,
    string Status,
    string Address,
    TokenView Token,
    string Amount,
    string AmountUnits,
    string RequestedAmount,
    string? Reference,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? PaidTxId,
    DateTimeOffset? PaidAt,
    string PaymentUri,
    string? CheckoutUrl)
{
    public static InvoiceView Create(InvoiceRecord invoice, TokenInfo token, string? checkoutUrl = null)
    {
        var amount = TokenAmount.Format(invoice.UniqueUnits, token.Decimals);
        return new InvoiceView(
            invoice.Id,
            invoice.Status,
            invoice.Address,
            TokenView.Create(token),
            amount,
            invoice.UniqueUnits.ToString(CultureInfo.InvariantCulture),
            TokenAmount.Format(invoice.RequestedUnits, token.Decimals),
            invoice.Reference,
            DateTimeOffset.FromUnixTimeMilliseconds(invoice.CreatedAt),
            DateTimeOffset.FromUnixTimeMilliseconds(invoice.ExpiresAt),
            invoice.PaidTxId,
            invoice.PaidAt is { } paidAt ? DateTimeOffset.FromUnixTimeMilliseconds(paidAt) : null,
            BuildPaymentUri(invoice.Address, token, amount),
            checkoutUrl);
    }

    /// <summary>NEP-9 payment URI, which wallets turn into a prefilled transfer (e.g. from a QR code).</summary>
    internal static string BuildPaymentUri(string address, TokenInfo token, string amount)
    {
        var asset = token.Hash switch
        {
            TokenRegistry.NeoHash => "neo",
            TokenRegistry.GasHash => "gas",
            _ => token.Hash,
        };
        return $"neo:{address}?asset={asset}&amount={amount}";
    }
}

/// <summary>Body of a webhook POST. Type is "invoice.paid" when the transfer settled an invoice, else "transfer.received".</summary>
public sealed record WebhookEvent(string Id, string Type, string Network, TransferView Transfer, InvoiceView? Invoice);

public static class WebhookPayloads
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Build(string network, TokenInfo token, Nep17Transfer transfer, InvoiceRecord? invoice, CheckoutOptions? checkout = null) =>
        JsonSerializer.Serialize(new WebhookEvent(
            $"{transfer.TxId}:{transfer.NotifyIndex}",
            invoice is null ? "transfer.received" : "invoice.paid",
            network,
            TransferView.Create(transfer, token, invoice?.Id),
            invoice is null ? null : InvoiceView.Create(invoice, token, checkout?.UrlFor(invoice.Id))), Json);
}
