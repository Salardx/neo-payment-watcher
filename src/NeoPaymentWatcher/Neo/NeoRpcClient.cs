using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace NeoPaymentWatcher.Neo;

public sealed record BlockInfo(uint Index, string Hash, long Time, IReadOnlyList<string> TxIds);

public sealed record TokenInfo(string Hash, string Symbol, int Decimals);

public sealed class NeoRpcException(string method, int code, string? message)
    : Exception($"RPC {method} failed with code {code}: {message}")
{
    public int Code { get; } = code;
}

/// <summary>Plain JSON-RPC 2.0 client for Neo N3 nodes. A failed call is retried on the next configured endpoint.</summary>
public sealed class NeoRpcClient
{
    readonly HttpClient http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    readonly string[] urls;
    readonly ILogger<NeoRpcClient> logger;
    int preferred;

    public NeoRpcClient(IOptions<WatcherOptions> options, ILogger<NeoRpcClient> logger)
    {
        urls = options.Value.RpcUrls;
        this.logger = logger;
    }

    public async Task<uint> GetBlockCountAsync(CancellationToken ct) =>
        (await CallAsync("getblockcount", [], ct)).GetUInt32();

    public async Task<uint> GetNetworkMagicAsync(CancellationToken ct) =>
        (await CallAsync("getversion", [], ct)).GetProperty("protocol").GetProperty("network").GetUInt32();

    public async Task<BlockInfo> GetBlockAsync(uint index, CancellationToken ct)
    {
        var block = await CallAsync("getblock", [index, true], ct);
        var txIds = block.GetProperty("tx").EnumerateArray().Select(tx => tx.GetProperty("hash").GetString()!).ToArray();
        return new BlockInfo(index, block.GetProperty("hash").GetString()!, block.GetProperty("time").GetInt64(), txIds);
    }

    public Task<JsonElement> GetApplicationLogAsync(string txId, CancellationToken ct) =>
        CallAsync("getapplicationlog", [txId], ct);

    public async Task<TokenInfo> GetTokenInfoAsync(string contractHash, CancellationToken ct)
    {
        var decimals = await InvokeReadOnlyAsync(contractHash, "decimals", ct);
        var symbol = await InvokeReadOnlyAsync(contractHash, "symbol", ct);
        return new TokenInfo(
            contractHash,
            Encoding.UTF8.GetString(Convert.FromBase64String(symbol.GetProperty("value").GetString()!)),
            int.Parse(decimals.GetProperty("value").GetString()!, CultureInfo.InvariantCulture));
    }

    async Task<JsonElement> InvokeReadOnlyAsync(string contractHash, string operation, CancellationToken ct)
    {
        var result = await CallAsync("invokefunction", [contractHash, operation, Array.Empty<object>()], ct);
        if (result.GetProperty("state").GetString() != "HALT")
            throw new InvalidOperationException($"{contractHash}.{operation}() faulted; is it a NEP-17 token?");
        return result.GetProperty("stack")[0];
    }

    async Task<JsonElement> CallAsync(string method, object[] parameters, CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < urls.Length; attempt++)
        {
            var index = (preferred + attempt) % urls.Length;
            try
            {
                var result = await CallOnceAsync(urls[index], method, parameters, ct);
                preferred = index;
                return result;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex;
                logger.LogWarning("RPC {Method} on {Url} failed: {Error}", method, urls[index], ex.Message);
            }
        }
        throw new InvalidOperationException($"All RPC endpoints failed for {method}.", lastError);
    }

    async Task<JsonElement> CallOnceAsync(string url, string method, object[] parameters, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, new { jsonrpc = "2.0", id = 1, method, @params = parameters }, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            throw new NeoRpcException(method,
                error.TryGetProperty("code", out var code) ? code.GetInt32() : 0,
                error.TryGetProperty("message", out var message) ? message.GetString() : null);
        }
        return root.GetProperty("result").Clone();
    }
}
