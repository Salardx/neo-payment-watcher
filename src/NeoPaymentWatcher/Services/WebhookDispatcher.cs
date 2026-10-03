using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NeoPaymentWatcher.Storage;

namespace NeoPaymentWatcher.Services;

/// <summary>
/// Delivers queued payment events at least once, retrying with exponential backoff. Receivers should
/// de-duplicate on X-Event-Id and, when a secret is configured, verify X-Signature.
/// </summary>
public sealed class WebhookDispatcher(WatcherStore store, IOptions<WatcherOptions> options, ILogger<WebhookDispatcher> logger)
    : BackgroundService
{
    readonly HttpClient http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.Webhook;
        if (string.IsNullOrWhiteSpace(settings.Url))
        {
            logger.LogInformation("No Watcher:Webhook:Url configured; payments are logged and stored only");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var item in store.GetDueWebhooks(Now(), 20))
                    await DeliverAsync(item, settings, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook dispatch failed; retrying in 5 seconds");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    async Task DeliverAsync(OutboxItem item, WebhookOptions settings, CancellationToken ct)
    {
        string error;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.Url)
            {
                Content = new StringContent(item.Payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Event-Id", item.EventId);
            if (!string.IsNullOrEmpty(settings.Secret))
                request.Headers.Add("X-Signature", Sign(settings.Secret, item.Payload));
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                store.MarkWebhookDelivered(item.Id, Now());
                return;
            }
            error = $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            error = ex.Message;
        }

        var attempts = item.Attempts + 1;
        long? next = attempts >= settings.MaxAttempts ? null : Now() + Backoff(attempts);
        store.MarkWebhookFailed(item.Id, attempts, next, error);
        logger.LogWarning("Webhook {EventId} attempt {Attempt} failed: {Error}{GiveUp}",
            item.EventId, attempts, error, next is null ? "; giving up" : "");
    }

    /// <summary>"sha256=" + hex HMAC-SHA256 of the raw request body.</summary>
    internal static string Sign(string secret, string payload) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    /// <summary>5 s, 10 s, 20 s, ... capped at one hour.</summary>
    internal static long Backoff(int attempts) => (long)Math.Min(5_000 * Math.Pow(2, attempts - 1), 3_600_000);

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public override void Dispose()
    {
        http.Dispose();
        base.Dispose();
    }
}
