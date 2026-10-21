using NeoPaymentWatcher;
using NeoPaymentWatcher.Api;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Chains.Evm;
using NeoPaymentWatcher.Neo;
using NeoPaymentWatcher.Services;
using NeoPaymentWatcher.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);
builder.Services.Configure<WatcherOptions>(builder.Configuration.GetSection(WatcherOptions.Section));

// Fail fast on bad configuration, before anything is wired up or scanned.
var settings = builder.Configuration.GetSection(WatcherOptions.Section).Get<WatcherOptions>() ?? new WatcherOptions();
settings.Validate();
var chain = settings.ResolveChain();

builder.Services.AddSingleton(chain);
builder.Services.AddSingleton<IChainClient>(sp => chain.IsEvm
    ? new EvmChainClient(chain, sp.GetRequiredService<ILogger<EvmChainClient>>())
    : new NeoChainClient(chain, sp.GetRequiredService<ILogger<NeoChainClient>>()));
builder.Services.AddSingleton(new WatcherStore(settings.DatabasePath));
builder.Services.AddSingleton<WatchList>();
builder.Services.AddSingleton<TokenRegistry>();
builder.Services.AddSingleton<ScannerState>();
builder.Services.AddSingleton<InvoiceService>();
builder.Services.AddSingleton<CheckoutService>();
builder.Services.AddHostedService<BlockScanner>();
builder.Services.AddHostedService<WebhookDispatcher>();

var app = builder.Build();

// A node from the wrong network, or a database from another one, stops the start.
var client = app.Services.GetRequiredService<IChainClient>();
await client.VerifyNetworkAsync(app.Lifetime.ApplicationStopping);
app.Services.GetRequiredService<WatcherStore>().Initialize(chain.NetworkKey);
var tokens = app.Services.GetRequiredService<TokenRegistry>();
await tokens.LoadAsync(client, settings, app.Lifetime.ApplicationStopping);
var watchList = app.Services.GetRequiredService<WatchList>();
app.Logger.LogInformation("Watching {Addresses} on {Chain} for {Tokens}",
    string.Join(", ", watchList.Addresses), chain.Label, string.Join(", ", tokens.All.Select(t => $"{t.Symbol} ({t.Hash})")));

app.MapGet("/health", (ScannerState state) => Results.Ok(new
{
    chain = chain.Kind,
    network = chain.Network,
    chainId = chain.ChainId,
    tip = state.Tip,
    lastBlock = state.LastBlock,
    lastBlockTime = state.LastBlockTime is { } time ? DateTimeOffset.FromUnixTimeMilliseconds(time) : (DateTimeOffset?)null,
}));
app.MapPost("/invoices", (CreateInvoiceRequest request, InvoiceService invoices) => invoices.Create(request))
    .AddEndpointFilter<ApiKeyFilter>();
app.MapGet("/invoices/{id}", (string id, InvoiceService invoices) => invoices.Get(id))
    .AddEndpointFilter<ApiKeyFilter>();
app.MapGet("/transfers", (int? limit, InvoiceService invoices) => invoices.RecentTransfers(limit ?? 50))
    .AddEndpointFilter<ApiKeyFilter>();

// The customer-facing page needs no API key; the invoice id is the secret.
if (settings.Checkout.Enabled)
{
    app.MapGet("/pay/{id}", (string id, HttpResponse response, CheckoutService checkout) => checkout.Page(id, response));
    app.MapGet("/pay/{id}/status", (string id, HttpResponse response, CheckoutService checkout) => checkout.Status(id, response));
}

app.Run();
