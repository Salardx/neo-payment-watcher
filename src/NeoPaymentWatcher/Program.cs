using Microsoft.Extensions.Options;
using NeoPaymentWatcher;
using NeoPaymentWatcher.Api;
using NeoPaymentWatcher.Neo;
using NeoPaymentWatcher.Services;
using NeoPaymentWatcher.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);
builder.Services.Configure<WatcherOptions>(builder.Configuration.GetSection(WatcherOptions.Section));
builder.Services.AddSingleton<NeoRpcClient>();
builder.Services.AddSingleton(sp => new WatcherStore(sp.GetRequiredService<IOptions<WatcherOptions>>().Value.DatabasePath));
builder.Services.AddSingleton<WatchList>();
builder.Services.AddSingleton<TokenRegistry>();
builder.Services.AddSingleton<ScannerState>();
builder.Services.AddSingleton<InvoiceService>();
builder.Services.AddHostedService<BlockScanner>();
builder.Services.AddHostedService<WebhookDispatcher>();

var app = builder.Build();

// Fail fast on bad configuration, or on a node from the wrong network, before anything is scanned.
var settings = app.Services.GetRequiredService<IOptions<WatcherOptions>>().Value;
settings.Validate();
var rpc = app.Services.GetRequiredService<NeoRpcClient>();
var magic = await rpc.GetNetworkMagicAsync(app.Lifetime.ApplicationStopping);
if (magic != settings.NetworkMagic)
{
    throw new InvalidOperationException(
        $"The RPC node is on network {magic}, but Watcher:Network is '{settings.Network}' ({settings.NetworkMagic}).");
}
app.Services.GetRequiredService<WatcherStore>().Initialize(settings.NetworkMagic);
var tokens = app.Services.GetRequiredService<TokenRegistry>();
await tokens.LoadAsync(rpc, settings.Tokens, app.Lifetime.ApplicationStopping);
app.Logger.LogInformation("Watching {Addresses} on {Network} for {Tokens}",
    string.Join(", ", settings.Addresses), settings.Network, string.Join(", ", tokens.All.Select(t => $"{t.Symbol} ({t.Hash})")));

app.MapGet("/health", (ScannerState state) => Results.Ok(new
{
    network = settings.Network,
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

app.Run();
