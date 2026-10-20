using Microsoft.Extensions.Options;
using NeoPaymentWatcher.Api;
using NeoPaymentWatcher.Neo;
using NeoPaymentWatcher.Storage;

namespace NeoPaymentWatcher.Services;

/// <summary>
/// Follows the chain block by block. Neo N3's dBFT consensus has one-block finality: a block the node
/// returns is final, so there is no confirmation depth and no reorg handling.
/// </summary>
public sealed class BlockScanner(
    NeoRpcClient rpc,
    WatcherStore store,
    TokenRegistry tokens,
    WatchList watchList,
    ScannerState state,
    IOptions<WatcherOptions> options,
    ILogger<BlockScanner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var announced = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var tip = await rpc.GetBlockCountAsync(stoppingToken) - 1;
                state.Tip = tip;
                var next = store.GetNextHeight() ?? settings.StartHeight ?? tip;
                if (!announced)
                {
                    logger.LogInformation("Scanning {Network} from block {Next} (tip {Tip})", settings.Network, next, tip);
                    announced = true;
                }
                for (; next <= tip && !stoppingToken.IsCancellationRequested; next++)
                    await ProcessBlockAsync(next, settings, stoppingToken);
                await Task.Delay(settings.PollIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scan failed; retrying in 5 seconds");
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

    async Task ProcessBlockAsync(uint index, WatcherOptions settings, CancellationToken ct)
    {
        var block = await rpc.GetBlockAsync(index, ct);
        var transfers = new List<Nep17Transfer>();
        foreach (var txId in block.TxIds)
        {
            var log = await rpc.GetApplicationLogAsync(txId, ct);
            transfers.AddRange(TransferParser.ParseIncoming(log, index, block.Time, watchList.ScriptHashes, tokens.Hashes));
        }

        var webhooks = !string.IsNullOrWhiteSpace(settings.Webhook.Url);
        var committed = store.CommitBlock(index, block.Time, transfers,
            (transfer, invoice) => webhooks
                ? WebhookPayloads.Build(settings.Network, tokens.Get(transfer.TokenHash), transfer, invoice, settings.Checkout)
                : null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        foreach (var (transfer, invoice) in committed)
        {
            var token = tokens.Get(transfer.TokenHash);
            logger.LogInformation("Received {Amount} {Symbol} at {To} from {From} in block {Block} (tx {TxId}){Invoice}",
                TokenAmount.Format(transfer.Amount, token.Decimals), token.Symbol, transfer.To, transfer.From,
                transfer.BlockIndex, transfer.TxId, invoice is null ? "" : $", paid invoice {invoice.Id}");
        }
        state.LastBlock = index;
        state.LastBlockTime = block.Time;
    }
}
