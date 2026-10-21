using System.Text.Json;

namespace NeoPaymentWatcher.Chains;

public sealed class JsonRpcException(string method, int code, string? message)
    : Exception($"RPC {method} failed with code {code}: {message}")
{
    public int Code { get; } = code;
}

/// <summary>Plain JSON-RPC 2.0 over HTTP. A failed call is retried on the next configured endpoint, and the one that worked is preferred afterwards.</summary>
public sealed class JsonRpcClient(string[] urls, ILogger logger)
{
    readonly HttpClient http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    int preferred;

    public async Task<JsonElement> CallAsync(string method, object?[] parameters, CancellationToken ct)
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

    async Task<JsonElement> CallOnceAsync(string url, string method, object?[] parameters, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, new { jsonrpc = "2.0", id = 1, method, @params = parameters }, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            throw new JsonRpcException(method,
                error.TryGetProperty("code", out var code) ? code.GetInt32() : 0,
                error.TryGetProperty("message", out var message) ? message.GetString() : null);
        }
        return root.GetProperty("result").Clone();
    }
}
