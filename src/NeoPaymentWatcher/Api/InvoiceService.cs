using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NeoPaymentWatcher.Neo;
using NeoPaymentWatcher.Services;
using NeoPaymentWatcher.Storage;

namespace NeoPaymentWatcher.Api;

public sealed class InvoiceService(WatcherStore store, TokenRegistry tokens, WatchList watchList, IOptions<WatcherOptions> options)
{
    public IResult Create(CreateInvoiceRequest request)
    {
        var settings = options.Value.Invoices;
        var token = tokens.Resolve(request.Token);
        if (token is null)
            return BadRequest($"Unknown token '{request.Token}'. Accepted: {string.Join(", ", tokens.All.Select(t => t.Symbol))}.");
        var address = string.IsNullOrWhiteSpace(request.Address) ? watchList.Addresses[0] : request.Address.Trim();
        if (!watchList.Contains(address))
            return BadRequest($"Address {address} is not in Watcher:Addresses.");
        if (!TokenAmount.TryParse(request.Amount, token.Decimals, out var requested) || requested.Sign <= 0)
            return BadRequest($"Amount must be a positive decimal string with at most {token.Decimals} decimals, e.g. \"1.5\".");
        var maxOffset = MaxOffset(token.Decimals, settings.MaxOffsetUnits);
        if (maxOffset < 1)
            return BadRequest($"{token.Symbol} has {token.Decimals} decimals; unique-amount invoices need at least 2.");

        var ttlMinutes = request.TtlMinutes is > 0 ? request.TtlMinutes.Value : settings.DefaultTtlMinutes;
        try
        {
            var invoice = store.CreateInvoice(address, token.Hash, requested, maxOffset,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ttlMinutes * 60_000L, settings.ReuseCooldownMinutes * 60_000L,
                request.Reference);
            return Results.Created($"/invoices/{invoice.Id}", InvoiceView.Create(invoice, token));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    public IResult Get(string id) => store.GetInvoice(id) is { } invoice
        ? Results.Ok(InvoiceView.Create(invoice, tokens.Get(invoice.TokenHash)))
        : Results.NotFound();

    public IResult RecentTransfers(int limit) => Results.Ok(store.GetRecentTransfers(Math.Clamp(limit, 1, 500))
        .Select(s => TransferView.Create(s.Transfer, tokens.Get(s.Transfer.TokenHash), s.InvoiceId)));

    /// <summary>Keeps the offset under 1% of one whole token, so a customer never noticeably overpays.</summary>
    internal static int MaxOffset(int decimals, int configured) =>
        (int)BigInteger.Min(configured, BigInteger.Pow(10, decimals) / 100);

    static IResult BadRequest(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest);
}

/// <summary>Requires the X-Api-Key header when Watcher:ApiKey is set.</summary>
public sealed class ApiKeyFilter(IOptions<WatcherOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = options.Value.ApiKey;
        if (!string.IsNullOrEmpty(expected))
        {
            var given = context.HttpContext.Request.Headers["X-Api-Key"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected)))
                return Results.Unauthorized();
        }
        return await next(context);
    }
}
