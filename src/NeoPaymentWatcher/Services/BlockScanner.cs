using Microsoft.Extensions.Options;
using NeoPaymentWatcher.Api;
using NeoPaymentWatcher.Chains;
using NeoPaymentWatcher.Storage;

namespace NeoPaymentWatcher.Services;

/// <summary>
/// Follows the chain block by block, staying <see cref="ChainProfile.Confirmations"/> blocks behind the
/// tip. On dBFT chains (Neo N3, Neo X) that is zero: a block the node returns is final, so there is no
/// reorg handling. On chains that can reorganise, the depth makes a reorganised block vanishingly unlikely.
/// </summary>
public sealed class BlockScanner(
    IChainClient chain,
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
        var confirmations = (uint)chain.Chain.Confirmations;
        var announced = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var tip = await chain.GetTipAsync(stoppingToken);
                state.Tip = tip;
                var safe = tip >= confirmations ? tip - confirmations : 0;
                var next = store.GetNextHeight() ?? settings.StartHeight ?? safe;
                if (!announced)
                {
                    logger.LogInformation("Scanning {Chain} from block {Next} (tip {Tip}, {Confirmations} confirmations)",
                        chain.Chain.Label, next, tip, confirmations);
                    announced = true;
                }
                for (; next <= safe && !stoppingToken.IsCancellationRequested; next++)
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
        var block = await chain.ScanBlockAsync(index, watchList.Keys, tokens.Hashes, ct);

        var webhooks = !string.IsNullOrWhiteSpace(settings.Webhook.Url);
        var committed = store.CommitBlock(index, block.Time, block.Transfers,
            (transfer, invoice) => webhooks
                ? WebhookPayloads.Build(chain.Chain, tokens.Get(transfer.TokenHash), transfer, invoice, settings.Checkout)
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
